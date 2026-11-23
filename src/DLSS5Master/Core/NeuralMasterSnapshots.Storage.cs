using System.Text.Json;

namespace DLSS5Master.Core;

internal sealed partial class NeuralMasterSnapshots
{
    internal const string ReleaseCatalogueFolderName = "neuralmaster-releases";
    private const int ReleaseReceiptLimit = 4096;

    private static GameContext Context(string gameDir, string exePath)
    {
        var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
        if (!Directory.Exists(game)) throw new InstallException("The selected game folder does not exist.");
        for (string? path = game; path is not null; path = Path.GetDirectoryName(path)) CheckOrdinary(path);
        var exe = Path.GetFullPath(exePath);
        if (!File.Exists(exe) || !PathComparer.Equals(Path.GetExtension(exe), ".exe"))
            throw new InstallException("The selected game executable does not exist.");
        InstallTransaction.CheckPath(game, exe);

        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.Root));
        if (PathComparer.Equals(data, game) || data.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InstallException("The NeuralMaster backup and rollback folders must be outside the game folder.");
        // App-data or its ancestors must not redirect the dedicated backup or journal into a load directory.
        for (string? path = data; path is not null; path = Path.GetDirectoryName(path))
            if (Directory.Exists(path)) CheckOrdinary(path);
        var store = StoreFor(data, game, exe);
        var journalRoot = Path.Combine(data, "install-transactions", InstallTransaction.Key(game));
        if (Directory.Exists(data))
        {
            InstallTransaction.CheckPath(data, store);
            InstallTransaction.CheckPath(data, Path.Combine(journalRoot, "operation.lock"));
            InstallTransaction.CheckPath(data, Path.Combine(journalRoot, "pending.json"));
            InstallTransaction.CheckPath(data, Path.Combine(journalRoot, "pending.json.tmp"));
            if (Directory.Exists(journalRoot))
                foreach (var file in Directory.EnumerateFiles(journalRoot)) CheckOrdinary(file);
        }
        return new(game, exe, data, store);
    }

    private static string StoreFor(string data, string game, string exe) =>
        Path.Combine(data, "neuralmaster-snapshots", InstallTransaction.Key(game), InstallTransaction.Key(exe));

    internal bool IsVerifiedSavedHost(string gameDir, string exePath, string hostPath)
    {
        if (!PathComparer.Equals(Path.GetFileName(hostPath), HostName)) return false;
        try
        {
            var preferred = StoreFor(AppPaths.Root, gameDir, exePath);
            var host = Path.GetFullPath(hostPath);
            if (!Directory.Exists(Path.GetDirectoryName(preferred)) || !File.Exists(host)) return false;
            // Executables beside the same hook can share it, even though their snapshots have separate records.
            var executables = Directory.EnumerateFiles(Path.GetDirectoryName(host)!, "*.exe")
                .Prepend(Path.GetFullPath(exePath)).Distinct(PathComparer);
            foreach (var exe in executables)
            {
                if (!File.Exists(Path.Combine(StoreFor(AppPaths.Root, gameDir, exe), "latest.json"))) continue;
                try
                {
                    var ctx = Context(gameDir, exe);
                    if (ReadSnapshot(ctx) is { } saved && saved.Record.Files.Any(file => file.Role == FileRole.Host
                        && PathComparer.Equals(GameFile(ctx, file.Path), host)
                        && PathComparer.Equals(Components.Sha256(host), file.Sha256))) return true;
                }
                catch (Exception ex) when (Expected(ex)) { }
            }
        }
        catch (Exception ex) when (Expected(ex)) { }
        return false;
    }

    private static string GameFile(GameContext ctx, string relative)
    {
        var target = CheckedRelative(ctx.Game, relative);
        InstallTransaction.CheckPath(ctx.Game, target);
        return target;
    }

    private static string CheckedRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')
            || relative.Split('\\', '/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InstallException($"Invalid snapshot path: {relative}");
        return Journal.SafePath(root, relative.Replace('/', '\\'));
    }

    private static string StorageFile(GameContext ctx, string full)
    {
        var path = CheckedRelative(ctx.Data, Path.GetRelativePath(ctx.Data, full));
        InstallTransaction.CheckPath(ctx.Data, path);
        return path;
    }

    private static string Pointer(GameContext ctx) => Path.Combine(ctx.Store, "latest.json");

    private static bool ValidBuildName(string name) => name.Length is >= 1 and <= 64 && !name.Any(char.IsControl);

    private static bool ValidSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private NeuralMasterKnownBuild[] Catalogue(GameContext ctx) => _builds.Concat(ReadImported(ctx)).ToArray();

    private static NeuralMasterKnownBuild? FindKnownBuild(GameContext ctx, IEnumerable<NeuralMasterKnownBuild> catalogue,
        string addonSha256, bool includeReleases) =>
        catalogue.FirstOrDefault(build => PathComparer.Equals(build.AddonSha256, addonSha256))
        ?? (includeReleases ? ReadReleaseReceipt(ctx, addonSha256) : null);

    private static NeuralMasterKnownBuild? ReadReleaseReceipt(GameContext ctx, string addonSha256)
    {
        if (!ValidSha256(addonSha256)) return null;
        try
        {
            var directory = Path.Combine(ctx.Data, ReleaseCatalogueFolderName);
            var gameRoot = Path.EndsInDirectorySeparator(ctx.Game) ? ctx.Game : ctx.Game + Path.DirectorySeparatorChar;
            if (PathComparer.Equals(directory, ctx.Game) ||
                directory.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(directory)) return null;
            directory = StorageFile(ctx, directory);
            var filename = addonSha256.ToLowerInvariant() + ".json";
            // Probe this digest only. Other files, including partial publisher output, are irrelevant.
            var path = Directory.EnumerateFiles(directory, filename, new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseSensitive,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).FirstOrDefault();
            if (path is null || !string.Equals(Path.GetFileName(path), filename, StringComparison.Ordinal)) return null;
            using var receipt = new FileStream(StorageFile(ctx, path), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (receipt.Length is <= 0 or > ReleaseReceiptLimit) return null;
            var bytes = new byte[(int)receipt.Length];
            receipt.ReadExactly(bytes);
            if (receipt.ReadByte() != -1) return null;
            var build = JsonSerializer.Deserialize<NeuralMasterKnownBuild>(bytes);
            return build?.Name is { } name && !string.IsNullOrWhiteSpace(name) && ValidBuildName(name)
                && PathComparer.Equals(build.AddonSha256, addonSha256) && ValidSha256(build.HostSha256) ? build : null;
        }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    private static List<NeuralMasterKnownBuild> ReadImported(GameContext ctx)
    {
        if (!Directory.Exists(ctx.Store)) return [];
        var path = StorageFile(ctx, Path.Combine(ctx.Store, "known-builds.json"));
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 65536) throw new InstallException("The verified build list is too large.");
        var entries = JsonSerializer.Deserialize<List<NeuralMasterKnownBuild>>(File.ReadAllBytes(path));
        if (entries is null || entries.Any(build => build?.Name is not { } name || !ValidBuildName(name)
            || !ValidSha256(build.AddonSha256) || !ValidSha256(build.HostSha256)))
            throw new InstallException("The verified build list is invalid.");
        return entries;
    }

    private static string? CurrentDirectory(GameContext ctx)
    {
        if (!Directory.Exists(ctx.Store)) return null;
        var pointer = StorageFile(ctx, Pointer(ctx));
        if (!File.Exists(pointer)) return null;
        var id = JsonSerializer.Deserialize<string>(File.ReadAllBytes(pointer));
        if (id is null || !id.StartsWith("snapshot-", StringComparison.Ordinal)
            || !Guid.TryParseExact(id[9..], "N", out _)) throw new InstallException("The NeuralMaster backup pointer is invalid.");
        return StorageFile(ctx, Path.Combine(ctx.Store, id));
    }

    private static SavedSnapshot? TryBackupInfo(GameContext ctx)
    {
        try
        {
            if (CurrentDirectory(ctx) is not { } path) return null;
            var record = ReadRecord(ctx, path);
            return new(path, record);
        }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    private SavedSnapshot? ReadSnapshot(GameContext ctx)
    {
        if (CurrentDirectory(ctx) is not { } path) return null;
        var saved = new SavedSnapshot(path, ReadRecord(ctx, path));
        VerifySnapshot(ctx, saved, alternate: false);
        return saved;
    }

    private static Snapshot ReadRecord(GameContext ctx, string directory)
    {
        var recordPath = StorageFile(ctx, Path.Combine(directory, "snapshot.json"));
        var record = JsonSerializer.Deserialize<Snapshot>(File.ReadAllBytes(recordPath));
        if (record is not { Version: 1, Files.Count: > 0 and <= 200 } || string.IsNullOrWhiteSpace(record.Build)
            || !PathComparer.Equals(record.Game, ctx.Game) || !PathComparer.Equals(record.Exe, ctx.Exe))
            throw new InstallException($"The backup record at {recordPath} does not match this game and executable.");
        return record;
    }

    private SavedSnapshot WriteSnapshot(GameContext ctx, string build, IReadOnlyList<SnapshotEntry> files, bool alternate)
    {
        // Write a new generation. The previous snapshot remains untouched until all new copies are verified.
        var path = StorageFile(ctx, Path.Combine(ctx.Store, $"{(alternate ? "alternate" : "snapshot")}-{Guid.NewGuid():N}"));
        var saved = new SavedSnapshot(path, new(1, ctx.Game, ctx.Exe, build, DateTimeOffset.Now, files));
        var store = StorageFile(ctx, Path.Combine(path, "files"));
        Directory.CreateDirectory(store);
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var current = GameFile(ctx, file.Path);
            if (file.Sha256 is null)
            {
                if (File.Exists(current) || Directory.Exists(current)) throw new InstallException($"{file.Path} changed while preparing the backup.");
                continue;
            }
            CopyDurable(current, StorageFile(ctx, Path.Combine(store, i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            RequireHash(current, file.Sha256);
        }
        var recordPath = StorageFile(ctx, Path.Combine(path, "snapshot.json"));
        WriteDurable(recordPath, JsonSerializer.SerializeToUtf8Bytes(saved.Record, Journal.JsonOptions));
        var verified = new SavedSnapshot(path, ReadRecord(ctx, path));
        VerifySnapshot(ctx, verified, alternate);
        return verified;
    }

    private static void PublishSnapshot(GameContext ctx, SavedSnapshot saved)
    {
        var temp = StorageFile(ctx, Path.Combine(ctx.Store, $"latest-{Guid.NewGuid():N}.tmp"));
        WriteDurable(temp, JsonSerializer.SerializeToUtf8Bytes(Path.GetFileName(saved.Directory)));
        File.Move(temp, StorageFile(ctx, Pointer(ctx)), overwrite: true);
    }

    private void VerifySnapshot(GameContext ctx, SavedSnapshot saved, bool alternate)
    {
        var files = saved.Record.Files;
        var addonHash = files.FirstOrDefault(entry => entry is { Role: FileRole.Addon })?.Sha256;
        var hostHash = files.FirstOrDefault(entry => entry is { Role: FileRole.Host })?.Sha256;
        // A later built-in name must not supersede a previously verified per-game backup.
        var known = (alternate ? _alternates : Catalogue(ctx)).FirstOrDefault(MatchesSavedIdentity);
        if (known is null && !alternate && addonHash is not null) known = ReadReleaseReceipt(ctx, addonHash);
        if (known is null || !MatchesSavedIdentity(known))
            throw new InstallException("The backup does not name a verified NeuralMaster or alternate build.");
        var paths = new HashSet<string>(PathComparer);
        for (int i = 0; i < files.Count; i++)
        {
            var entry = files[i];
            if (entry is null || !paths.Add(GameFile(ctx, entry.Path))) throw new InstallException("Duplicate or invalid backup entry.");
            var name = Path.GetFileName(entry.Path);
            bool valid = entry.Role switch
            {
                FileRole.Addon => (alternate ? IsOtherNr(name) : IsNeuralMasterAddonName(name)) && PathComparer.Equals(known.AddonSha256, entry.Sha256),
                FileRole.Host => PathComparer.Equals(HostName, name) && PathComparer.Equals(known.HostSha256, entry.Sha256),
                FileRole.Settings => PathComparer.Equals(SharedIni, name) || PathComparer.Equals(OwnedIni, name),
                _ => false,
            };
            if (!valid) throw new InstallException($"Unverified backup entry: {entry.Path}");
            if (entry.Sha256 is not null) RequireHash(StorageFile(ctx, SnapshotFile(saved, i)), entry.Sha256);
        }
        if (!files.Any(entry => entry.Role == FileRole.Addon) || !files.Any(entry => entry.Role == FileRole.Host))
            throw new InstallException("The backup does not contain both a verified add-on and its matching host.");

        bool MatchesSavedIdentity(NeuralMasterKnownBuild build) => build.Name == saved.Record.Build
            && PathComparer.Equals(build.AddonSha256, addonHash) && PathComparer.Equals(build.HostSha256, hostHash);
    }

    private static string SnapshotFile(SavedSnapshot saved, SnapshotEntry file)
    {
        for (int i = 0; i < saved.Record.Files.Count; i++)
            if (Equals(saved.Record.Files[i], file)) return SnapshotFile(saved, i);
        throw new InstallException("The requested file is not in the verified backup.");
    }

    private static string SnapshotFile(SavedSnapshot saved, int i) => Path.Combine(saved.Directory, "files",
        i.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void CopyDurable(string source, string dest)
    {
        using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void WriteDurable(string file, byte[] data)
    {
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }
}
