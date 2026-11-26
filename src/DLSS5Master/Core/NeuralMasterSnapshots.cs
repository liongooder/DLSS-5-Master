using System.Security.Cryptography;
using System.Text.Json;

namespace DLSS5Master.Core;

internal enum NeuralMasterSnapshotState { Installed, Uninstalled, NeedsAttention, NotFound }

internal sealed record NeuralMasterKnownBuild(string Name, string AddonSha256, string HostSha256);

internal sealed record NeuralMasterSnapshotStatus(
    NeuralMasterSnapshotState State, string? Build, DateTimeOffset? BackupDate, string? BackupLocation,
    IReadOnlyList<string> UninstallPaths, IReadOnlyList<string> RestorePaths, IReadOnlyList<string> ConflictPaths,
    string Message, bool CanUninstall, bool CanRestore, bool CanSwitch, bool CanRecover, string Fingerprint)
{
    public string? ExternalSetupRepairFingerprint { get; init; }
    public IReadOnlyList<string> VerifiedHostPaths { get; init; } = [];
}

/// <summary>Dedicated recovery snapshots. Neither action edits the game's regular DLSS 5 Master record.</summary>
internal sealed partial class NeuralMasterSnapshots(
    IEnumerable<NeuralMasterKnownBuild>? builds = null,
    IEnumerable<NeuralMasterKnownBuild>? alternates = null,
    Action<string>? afterChange = null)
{
    private const string HostName = "dxgi.dll";
    private const string SharedIni = "ReShade.ini";
    private const string OwnedIni = "NeuralMaster-native-call.ini";
    private const string TransactionOwner = "neuralmaster";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly NeuralMasterKnownBuild[] _builds = builds?.ToArray() ??
    [
        new("T129", "f7d3b6f1dcc9ee8e1ce626a1b2c60e8d211ef10e4e34944b2636758ed42f4b99",
            "61c1a5258b9c2ce4250f4ac3462e052b87f8d16896b785a5298d7b580288d316"),
        new("T84", "4bc92834c047e9077112e67740644279e188d5846548a2613e8341cfd04a0bac",
            "7a05cbc16412f61c9cb718a9f69105d4f38fec5471d81727590e2fcaab4b86d2"),
        new("T83", "f579b8be4465d0f09df7d232efdda16bbebc9163184f2250b379c5f5a7efd611",
            "a1b946edeb2db706c6f1d4a113f070c205a2fd9b40ed121b0aaa9a0a92f19495"),
        new("T82", "889608cf4b2c19118e60615322bae434056dba09c555f667c458c400c5572649",
            "a88b72d6bc235b8ee351f726c97ffdc28e8e08abc1b3abf1c18dd3efcf26bb23"),
    ];
    private readonly NeuralMasterKnownBuild[] _alternates = alternates?.ToArray() ??
    [
        new("DLSS5 NR 15.1.0", "26f140748abac30237107e76c57521632d1f79ad7c7c7762e7c0ac25a782264b",
            "61c1a5258b9c2ce4250f4ac3462e052b87f8d16896b785a5298d7b580288d316"),
        new("DLSS5 NR 14.0.0 Dev3", "56c5303448fbb03f36b7af926529cc5416e83082f830aa02cfaa2b7c89eed1e4",
            "0cee63f9c9f13f3ac909c5b4903f4dbb4b719a7ab3b4f13b0deaf83c814b94f7"),
    ];

    private enum FileRole { Addon, Host, Settings }
    private sealed record SnapshotEntry(string Path, FileRole Role, string? Sha256);
    private sealed record Snapshot(int Version, string Game, string Exe, string Build, DateTimeOffset Date,
        IReadOnlyList<SnapshotEntry> Files);
    private sealed record SavedSnapshot(string Directory, Snapshot Record);
    private sealed record GameContext(string Game, string Exe, string Data, string Store);
    private sealed record Layout(Dictionary<string, HashSet<string>> HostsByAddonDirectory)
    {
        internal IEnumerable<string> Directories => HostsByAddonDirectory.Keys;
    }
    private sealed record Plan(NeuralMasterSnapshotStatus Status, SavedSnapshot? Backup,
        IReadOnlyList<SnapshotEntry> Active, IReadOnlyList<SnapshotEntry> Alternate, string? AlternateName,
        IReadOnlyList<SnapshotEntry> Restore, IReadOnlyDictionary<string, string?> RestoreCurrentHashes, string? Build);

    internal static bool IsNeuralMasterAddonName(string filename) => IsAddon(filename)
        && (filename.StartsWith("NeuralMaster-", StringComparison.OrdinalIgnoreCase)
            || filename.StartsWith("NeuralMaster.", StringComparison.OrdinalIgnoreCase));

    private static bool IsAddon(string filename) => Path.GetExtension(filename) is { } ext
        && (PathComparer.Equals(ext, ".addon64") || PathComparer.Equals(ext, ".addon32") || PathComparer.Equals(ext, ".addon"));

    private static bool IsOtherNr(string filename) => IsAddon(filename) &&
        (Installers.IsOtherNeuralAddon(filename)
         || filename.StartsWith("renodx-dlss", StringComparison.OrdinalIgnoreCase)
         || filename.StartsWith("dlss5-feed", StringComparison.OrdinalIgnoreCase));

    /// <summary>Best-effort guard for general manager operations. It reads filenames, not file contents.</summary>
    internal static string? FindLoadableAddon(string gameDir, string exePath)
    {
        try
        {
            var game = Path.GetFullPath(gameDir);
            var exe = Path.GetFullPath(exePath);
            return FindFiles(Discover(game, exe, strict: false), IsNeuralMasterAddonName, strict: false)
                .Select(path => Path.GetRelativePath(game, path)).FirstOrDefault();
        }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    internal static bool HasPendingOperation(string gameDir)
    {
        try { return InstallTransaction.PendingOwner(gameDir) == TransactionOwner; }
        catch (Exception ex) when (Expected(ex)) { return true; } // An unreadable journal must not allow a different workflow to write.
    }

    /// <summary>Find evidence for showing the game panel before verifying any add-on or backup bytes.</summary>
    internal static bool HasGameState(string gameDir, string exePath, Manifest? manifest = null)
    {
        if (PathComparer.Equals(manifest?.Route, "neuralmaster")) return true;
        try
        {
            if (File.Exists(Path.Combine(StoreFor(AppPaths.Root, gameDir, exePath), "latest.json"))) return true;
            // Show the recovery direction for the game even when a different executable is selected.
            // Inspect only offers recovery from the executable recorded by the journal.
            if (InstallTransaction.PendingOwner(gameDir) == TransactionOwner) return true;
        }
        catch (Exception ex) when (Expected(ex)) { }
        return FindLoadableAddon(gameDir, exePath) is not null;
    }

    internal NeuralMasterSnapshotStatus Inspect(string gameDir, string exePath)
    {
        try
        {
            var ctx = Context(gameDir, exePath);
            if (InstallTransaction.Pending(ctx.Game))
            {
                // Recovery precedes inspection of partially changed game files.
                bool ours = InstallTransaction.PendingOwner(ctx.Game) == TransactionOwner;
                var recordedExe = ours ? InstallTransaction.PendingOwnerTarget(ctx.Game) : null;
                if (ours && !PathComparer.Equals(recordedExe, ctx.Exe))
                    return Status(NeuralMasterSnapshotState.NeedsAttention, TryBackupInfo(ctx),
                        $"Select the executable for the interrupted NeuralMaster operation: {recordedExe}");
                return Status(NeuralMasterSnapshotState.NeedsAttention, TryBackupInfo(ctx), ours
                    ? "An interrupted NeuralMaster file operation needs recovery."
                    : "A game setup operation is pending. Recover it from the game's setup before changing NeuralMaster.",
                    canRecover: ours);
            }
            var status = BuildPlan(ctx).Status;
            if (status.State != NeuralMasterSnapshotState.NeedsAttention) return status;
            try
            {
                var repair = PlanExternalSetupLayer(ctx);
                return repair is null ? status : status with
                {
                    ExternalSetupRepairFingerprint = repair.Fingerprint,
                    Message = "DLSS5 NR's setup replaced the ReShade host. Remove that setup to put back NeuralMaster's verified host.",
                };
            }
            catch (Exception ex) when (Expected(ex)) { return status; }
        }
        catch (Exception ex) when (Expected(ex))
        {
            return Status(NeuralMasterSnapshotState.NeedsAttention, null, ex.Message);
        }
    }

    internal void Uninstall(string gameDir, string exePath, string? expectedFingerprint = null, Action<string>? log = null)
    {
        var ctx = Context(gameDir, exePath);
        WithTransaction(ctx, () =>
        {
            var plan = BuildPlan(ctx);
            ConfirmPlan(plan, expectedFingerprint);
            if (!plan.Status.CanUninstall) throw new InstallException(plan.Status.Message);
            var addons = plan.Active.Where(file => file.Role == FileRole.Addon).ToList();
            foreach (var file in addons) AssertWritableTarget(GameFile(ctx, file.Path));

            var snapshot = WriteSnapshot(ctx, plan.Build!, plan.Active, alternate: false);
            PublishSnapshot(ctx, snapshot);
            foreach (var file in addons) InstallTransaction.Capture(GameFile(ctx, file.Path));
            Installers.AssertGameClosed(ctx.Exe);
            foreach (var file in addons) RemoveCaptured(ctx, file);
            VerifyGameFiles(ctx, plan.Active.Where(file => file.Role == FileRole.Host));
            VerifyAbsent(ctx, addons);
            return $"Uninstalled NeuralMaster {plan.Build}. Verified backup: {snapshot.Directory}";
        }, log);
    }

    internal void Restore(string gameDir, string exePath, bool switchAlternate = false,
        string? expectedFingerprint = null, Action<string>? log = null)
    {
        var ctx = Context(gameDir, exePath);
        WithTransaction(ctx, () =>
        {
            var plan = BuildPlan(ctx);
            ConfirmPlan(plan, expectedFingerprint);
            if (!plan.Status.CanRestore && !(switchAlternate && plan.Status.CanSwitch))
                throw new InstallException(plan.Status.Message);

            var saved = plan.Backup!;
            var obsolete = plan.Alternate.Where(file => file.Role == FileRole.Addon).ToList();
            foreach (var file in plan.Restore.Concat(obsolete).DistinctBy(file => file.Path, PathComparer))
                AssertWritableTarget(GameFile(ctx, file.Path));

            var parked = plan.Alternate.Count == 0 ? null : WriteSnapshot(ctx, plan.AlternateName!, plan.Alternate, alternate: true);
            var staged = new List<(SnapshotEntry File, string Temporary)>();
            foreach (var file in plan.Restore)
            {
                var target = GameFile(ctx, file.Path);
                var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".d5m-{Guid.NewGuid():N}.nm-stage");
                InstallTransaction.Capture(target);
                InstallTransaction.Capture(temporary);
                CopyDurable(StorageFile(ctx, SnapshotFile(saved, file)), temporary);
                RequireHash(temporary, file.Sha256!);
                staged.Add((file, temporary));
            }
            foreach (var file in obsolete) InstallTransaction.Capture(GameFile(ctx, file.Path));
            Installers.AssertGameClosed(ctx.Exe);
            foreach (var file in obsolete) RemoveCaptured(ctx, file);
            foreach (var (file, temporary) in staged.OrderBy(pair => pair.File.Role != FileRole.Host))
            {
                var target = GameFile(ctx, file.Path);
                var expected = plan.RestoreCurrentHashes[file.Path];
                if (expected is null)
                {
                    if (File.Exists(target) || Directory.Exists(target)) throw new InstallException($"A new file occupies {file.Path}. Check the setup again.");
                }
                else RequireHash(target, expected);
                Journal.ClearReadOnly(target);
                File.Move(temporary, target, overwrite: true);
                afterChange?.Invoke(file.Path);
            }
            VerifyGameFiles(ctx, saved.Record.Files.Where(file => file.Role is FileRole.Host or FileRole.Addon));
            VerifyAbsent(ctx, obsolete);
            return $"Restored NeuralMaster {saved.Record.Build}." +
                (parked is null ? "" : $" Verified backup of {parked.Record.Build}: {parked.Directory}");
        }, log);
    }

    internal void Recover(string gameDir, string exePath, Action<string>? log = null)
    {
        var ctx = Context(gameDir, exePath);
        Installers.AssertGameClosed(ctx.Exe);
        InstallTransaction.Recover(ctx.Game, TransactionOwner, ctx.Exe);
        Report("Recovered the interrupted file operation for " + Path.GetFileName(ctx.Game) + ".", log);
    }

    private static bool Expected(Exception ex) => ex is InstallException or IOException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException;

    private static void Report(string message, Action<string>? log)
    {
        AppPaths.Log("[NeuralMaster] " + message);
        log?.Invoke(message);
    }

    private static void WithTransaction(GameContext ctx, Func<string> action, Action<string>? log)
    {
        Installers.AssertGameClosed(ctx.Exe);
        using var transaction = InstallTransaction.Begin(ctx.Game, TransactionOwner, ctx.Exe);
        string result;
        try
        {
            result = action();
            transaction.Commit();
        }
        catch (Exception failure)
        {
            try { transaction.Rollback(); }
            catch (Exception recovery)
            {
                throw new InstallException($"{failure.Message} Recovery is still needed. The rollback files were kept. {recovery.Message}");
            }
            throw;
        }
        Report(result, log);
    }

    private static void ConfirmPlan(Plan plan, string? expected)
    {
        if (expected is not null && !StringComparer.Ordinal.Equals(expected, plan.Status.Fingerprint))
            throw new InstallException("The game or backup changed after the preview. Check the paths again in the game's NeuralMaster bar.");
    }

    private void RemoveCaptured(GameContext ctx, SnapshotEntry file)
    {
        var path = GameFile(ctx, file.Path);
        RequireHash(path, file.Sha256!);
        Journal.ClearReadOnly(path);
        File.Delete(path);
        afterChange?.Invoke(file.Path);
    }

    private static void AssertWritableTarget(string path)
    {
        if (Directory.Exists(path)) throw new InstallException($"A directory occupies the file target {path}.");
        if (!File.Exists(path)) return;
        try { using var unused = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException) { throw new InstallException($"Close the game or application using this locked file: {path}"); }
    }

    private static void VerifyGameFiles(GameContext ctx, IEnumerable<SnapshotEntry> files)
    {
        foreach (var file in files) RequireHash(GameFile(ctx, file.Path), file.Sha256!);
    }

    private static void VerifyAbsent(GameContext ctx, IEnumerable<SnapshotEntry> files)
    {
        foreach (var file in files)
        {
            var path = GameFile(ctx, file.Path);
            if (File.Exists(path) || Directory.Exists(path)) throw new InstallException($"The add-on still occupies {path}.");
        }
    }

    private static void RequireHash(string path, string hash)
    {
        if (!File.Exists(path) || !PathComparer.Equals(Components.Sha256(path), hash))
            throw new InstallException($"SHA-256 verification failed for {path}.");
    }
}
