using System.Security.Cryptography;
using System.Text.Json;

namespace DLSS5Master.Core;

internal sealed partial class NeuralMasterSnapshots
{
    private static Layout Discover(string game, string exe, bool strict)
    {
        var exeDirectory = Path.GetDirectoryName(exe)!;
        var hosts = new[] { exeDirectory, game, Path.Combine(exeDirectory, "_storage_"), Path.Combine(game, "_storage_") };
        var directories = new Dictionary<string, HashSet<string>>(PathComparer);
        foreach (var dir in hosts.Distinct(PathComparer).Where(Directory.Exists))
        {
            CheckLocation(dir, dir);
            TryConfigure(dir);
        }
        return new(directories);

        void CheckLocation(string directory, string hostDirectory)
        {
            try
            {
                if (PathComparer.Equals(game, directory)) CheckOrdinary(game);
                else InstallTransaction.CheckPath(game, directory);
                if (!directories.TryGetValue(directory, out var owners))
                    directories[directory] = owners = new(PathComparer);
                owners.Add(hostDirectory);
            }
            catch (Exception ex) when (!strict && Expected(ex)) { }
        }

        void TryConfigure(string dir)
        {
            var ini = Path.Combine(dir, SharedIni);
            if (!File.Exists(ini)) return;
            try
            {
                InstallTransaction.CheckPath(game, ini);
                var addonPath = Ini.Get(File.ReadAllText(ini), "ADDON", "AddonPath")?.Trim().Trim('"');
                if (string.IsNullOrEmpty(addonPath)) return;
                var configured = Path.GetFullPath(addonPath, dir);
                CheckLocation(configured, dir);
            }
            catch (Exception ex) when (Expected(ex))
            {
                if (strict) throw new InstallException($"The add-on location in {ini} cannot be verified. {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> FindFiles(Layout layout, Func<string, bool> predicate, bool strict)
    {
        foreach (var dir in layout.Directories)
        {
            string[] files;
            try { files = Directory.Exists(dir) ? Directory.GetFiles(dir) : []; }
            catch (Exception ex) when (!strict && Expected(ex)) { continue; }
            foreach (var path in files.Order(PathComparer))
                if (predicate(Path.GetFileName(path))) yield return path;
        }
    }

    private static void CheckOrdinary(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InstallException($"A link or junction cannot be verified: {path}");
    }

    private Plan BuildPlan(GameContext ctx)
    {
        var layout = Discover(ctx.Game, ctx.Exe, strict: true);
        var active = FindFiles(layout, IsNeuralMasterAddonName, strict: true).ToList();
        SavedSnapshot? backup;
        try { backup = ReadSnapshot(ctx); }
        catch (Exception ex) when (Expected(ex))
        {
            return Describe(NeuralMasterSnapshotState.NeedsAttention, TryBackupInfo(ctx), $"The NeuralMaster backup cannot be verified. {ex.Message}");
        }

        if (active.Count > 0)
        {
            var files = Identify(ctx, layout, active, Catalogue(ctx), out var known, out var conflicts, includeReleases: true);
            if (files is null)
                return Describe(NeuralMasterSnapshotState.NeedsAttention, backup,
                    "The installed NeuralMaster build or ReShade host is unrecognized.", conflicts: conflicts);
            return Describe(NeuralMasterSnapshotState.Installed, backup, "The installed add-on and host checksums match.",
                active: files, build: known!.Name);
        }

        if (backup is null)
            return Describe(NeuralMasterSnapshotState.NotFound, null, "No identified NeuralMaster installation or backup for this game.");

        var restore = new List<SnapshotEntry>();
        var restoreCurrentHashes = new Dictionary<string, string?>(PathComparer);
        var changedHosts = new List<string>();
        var matchingHosts = new List<string>();
        var blocked = new List<string>();
        foreach (var file in backup.Record.Files.Where(file => file.Role is FileRole.Addon or FileRole.Host))
        {
            var path = GameFile(ctx, file.Path);
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir) || !layout.HostsByAddonDirectory.ContainsKey(dir) || Directory.Exists(path))
            {
                blocked.Add(file.Path);
                continue;
            }
            if (File.Exists(path))
            {
                var currentHash = Components.Sha256(path);
                if (PathComparer.Equals(currentHash, file.Sha256))
                {
                    if (file.Role == FileRole.Host) matchingHosts.Add(file.Path);
                    continue;
                }
                restoreCurrentHashes[file.Path] = currentHash;
                if (file.Role == FileRole.Host) changedHosts.Add(file.Path);
                else blocked.Add(file.Path);
            }
            else restoreCurrentHashes[file.Path] = null;
            restore.Add(file);
        }
        if (blocked.Count > 0)
            return Describe(NeuralMasterSnapshotState.NeedsAttention, backup,
                "A saved file's destination is occupied or its load location changed.",
                restore: restore, conflicts: blocked, restoreCurrentHashes: restoreCurrentHashes, matchingHosts: matchingHosts);

        var other = FindFiles(layout, IsOtherNr, strict: true).ToList();
        if (other.Count == 0 && changedHosts.Count == 0)
            return Describe(NeuralMasterSnapshotState.Uninstalled, backup, "The verified backup is available.", restore: restore,
                canRestore: true, restoreCurrentHashes: restoreCurrentHashes, matchingHosts: matchingHosts);

        var listedConflicts = changedHosts.Concat(other.Select(path => Path.GetRelativePath(ctx.Game, path))).ToList();
        var alternate = Identify(ctx, layout, other, _alternates, out var alternative, out var unknown);
        // A recognized DLL on its own is not enough to identify the installation that owns it.
        bool canSwitch = alternate is not null && changedHosts.All(path => alternate.Any(f => f.Role == FileRole.Host && PathComparer.Equals(f.Path, path)))
            && !ManagedAlternate(ctx, alternate);
        string message;
        if (canSwitch)
            message = $"{alternative!.Name} occupies this setup. Back up and switch will verify its files, remove its loadable add-on copies, and restore NeuralMaster.";
        else if (ManagerOwnsConflicts(ctx.Game, listedConflicts))
            message = "DLSS 5 Master manages the files blocking this backup. Use Restore originals for this game, then check the NeuralMaster backup again.";
        else
            message = "Another add-on or ReShade host conflicts with this backup.";

        listedConflicts.AddRange(unknown);
        return Describe(NeuralMasterSnapshotState.NeedsAttention, backup, message,
            restore: restore, conflicts: listedConflicts, alternate: canSwitch ? alternate : null, alternateName: alternative?.Name,
            restoreCurrentHashes: restoreCurrentHashes, matchingHosts: matchingHosts);
    }


    private sealed record SetupLayerRepair(string Manifest, string ManifestHash, string OriginalHost, string HostHash,
        string ReplacedHostHash, IReadOnlyList<string> Hosts, string AddonHash, IReadOnlyList<string> Addons)
    {
        public string Fingerprint => string.Join("\n", new[] { Manifest, ManifestHash, OriginalHost, HostHash, ReplacedHostHash, AddonHash }
            .Concat(Hosts.Order(PathComparer)).Concat(Addons.Order(PathComparer)));
    }

    private SetupLayerRepair? PlanExternalSetupLayer(GameContext ctx)
    {
        var exeDir = Path.GetDirectoryName(ctx.Exe)!;
        if (!NrAddon.SetupInstalled(exeDir)) return null;
        if (Journal.ReadManifest(ctx.Game) is not null && !Installers.NrSetupAfterUs(ctx.Game)) return null;
        var record = GameFile(ctx, Path.GetRelativePath(ctx.Game, NrAddon.SetupRecord(exeDir)));
        var recordHash = Components.Sha256(record);
        var setup = NrAddon.ReadSetupInstall(exeDir);
        if (setup.Problem is not null || setup.Dirs.Count != 0) return null;

        var rootHost = Path.Combine(exeDir, HostName);
        var rootAddon = Path.Combine(exeDir, NrAddon.Addon64);
        var hostBackups = setup.PutBack.Where(item => PathComparer.Equals(item.Target, rootHost)).ToList();
        if (hostBackups.Count != 1 || !setup.Delete.Contains(rootAddon, PathComparer)) return null;
        // Only the x64 setup layout is recognized. Keep the shared runtime and the player's current INI files.
        var preserved = new[] { SharedIni, "ReShadePreset.ini", "nvngx_dlssnr.dll" }
            .Select(name => Path.Combine(exeDir, name)).ToHashSet(PathComparer);
        if (setup.PutBack.Any(item => !PathComparer.Equals(item.Target, rootHost) && !preserved.Contains(item.Target))
            || setup.Delete.Any(path => !PathComparer.Equals(path, rootAddon) && !preserved.Contains(path))) return null;

        var backup = hostBackups[0];
        var source = GameFile(ctx, Path.GetRelativePath(ctx.Game, backup.Source));
        var backupRoot = Path.Combine(Path.GetDirectoryName(record)!, "backup");
        if (!PathComparer.Equals(source, Path.Combine(backupRoot, HostName))) return null;
        rootHost = GameFile(ctx, Path.GetRelativePath(ctx.Game, rootHost));
        rootAddon = GameFile(ctx, Path.GetRelativePath(ctx.Game, rootAddon));
        if (!File.Exists(rootHost) || !File.Exists(rootAddon)) return null;

        var replacedHash = Components.Sha256(rootHost);
        if (PathComparer.Equals(replacedHash, backup.Sha)) return null;
        var layout = Discover(ctx.Game, ctx.Exe, strict: true);
        var active = FindFiles(layout, IsNeuralMasterAddonName, strict: true).ToList();
        var addonHash = Components.Sha256(rootAddon);
        var installed = Identify(ctx, layout, active, Catalogue(ctx), out _, out _, includeReleases: true,
            proposedHost: (path, actual, expected) =>
                PathComparer.Equals(actual, replacedHash) && PathComparer.Equals(expected, backup.Sha)
                && CanRepairHost(path));
        if (installed is null) return null;
        var hosts = installed.Where(file => file.Role == FileRole.Host)
            .Select(file => GameFile(ctx, file.Path))
            .Where(path => PathComparer.Equals(Components.Sha256(path), replacedHash)).ToList();
        if (!hosts.Contains(rootHost, PathComparer)) return null;

        var addons = FindFiles(layout, IsOtherNr, strict: true)
            .Select(path => GameFile(ctx, Path.GetRelativePath(ctx.Game, path))).ToList();
        var addonTargets = hosts.Select(path => Path.Combine(Path.GetDirectoryName(path)!, NrAddon.Addon64)).ToHashSet(PathComparer);
        if (!addons.Contains(rootAddon, PathComparer) || addons.Any(path => !addonTargets.Contains(path)
            || !PathComparer.Equals(Components.Sha256(path), addonHash))) return null;
        RequireHash(source, backup.Sha);
        RequireHash(record, recordHash);
        return new(record, recordHash, source, backup.Sha, replacedHash, hosts, addonHash, addons);

        bool CanRepairHost(string relativePath)
        {
            var fullPath = GameFile(ctx, relativePath);
            return PathComparer.Equals(fullPath, rootHost) ||
                new[] { ctx.Game, exeDir }.Any(dir => PathComparer.Equals(fullPath, Path.Combine(dir, "_storage_", HostName)));
        }
    }

    internal void RemoveExternalSetupLayer(string gameDir, string exePath, string expectedFingerprint, Action<string>? log = null)
    {
        var ctx = Context(gameDir, exePath);
        WithTransaction(ctx, () =>
        {
            var plan = PlanExternalSetupLayer(ctx);
            if (plan is null || !StringComparer.Ordinal.Equals(plan.Fingerprint, expectedFingerprint))
                throw new InstallException("The DLSS5 NR setup or backups changed after verification. Refresh the game and check again.");

            var archive = Path.Combine(Journal.BackupRoot(ctx.Game), "removed", $"nr-setup-{Guid.NewGuid():N}");
            foreach (var target in plan.Hosts.Concat(plan.Addons).Append(plan.Manifest))
            {
                AssertWritableTarget(target);
                InstallTransaction.Capture(target);
            }

            foreach (var target in plan.Hosts)
            {
                var stage = Path.Combine(Path.GetDirectoryName(target)!, $".d5m-{Guid.NewGuid():N}.nm-stage");
                InstallTransaction.Capture(stage);
                CopyDurable(plan.OriginalHost, stage);
                RequireHash(stage, plan.HostHash);
                Archive(target, plan.ReplacedHostHash);
                File.Move(stage, target);
                afterChange?.Invoke(Path.GetRelativePath(ctx.Game, target));
            }
            foreach (var addon in plan.Addons) Archive(addon, plan.AddonHash);
            Archive(plan.Manifest, plan.ManifestHash);
            if (!BuildPlan(ctx).Status.CanUninstall)
                throw new InstallException("The restored ReShade host failed NeuralMaster verification.");
            return $"Removed the DLSS5 NR setup layer and restored the verified NeuralMaster host. Archived files: {archive}";

            void Archive(string target, string hash)
            {
                RequireHash(target, hash);
                var relative = Path.GetRelativePath(ctx.Game, target);
                var destination = GameFile(ctx, Path.GetRelativePath(ctx.Game, Path.Combine(archive, relative)));
                InstallTransaction.Capture(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(target, destination);
            }
        }, log);
    }


    internal void ImportBuild(string gameDir, string exePath, string addonFile, string hostFile, string name, Action<string>? log = null)
    {
        var ctx = Context(gameDir, exePath);
        name = name.Trim();
        if (!ValidBuildName(name)) throw new InstallException("Enter a build label of 1 to 64 printable characters.");
        var addon = TrustedSource(ctx, addonFile, IsNeuralMasterAddonName);
        var host = TrustedSource(ctx, hostFile, filename => PathComparer.Equals(filename, HostName)
            || PathComparer.Equals(filename, "ReShade64.dll") || PathComparer.Equals(filename, "ReShade32.dll"));
        WithTransaction(ctx, () =>
        {
            var build = new NeuralMasterKnownBuild(name, Components.Sha256(addon), Components.Sha256(host));
            var catalogue = Catalogue(ctx);
            var previous = FindKnownBuild(ctx, catalogue, build.AddonSha256, includeReleases: true);
            if (previous is not null && !PathComparer.Equals(previous.HostSha256, build.HostSha256))
                throw new InstallException("This add-on already has a verified matching host with a different checksum.");
            if (previous is null && catalogue.Any(item => PathComparer.Equals(item.Name, name)))
                throw new InstallException("This build label is already used. Enter a different label.");

            var layout = Discover(ctx.Game, ctx.Exe, strict: true);
            var active = FindFiles(layout, IsNeuralMasterAddonName, strict: true).ToList();
            var files = Identify(ctx, layout, active, [build], out _, out var conflicts);
            if (files is null)
                throw new InstallException(active.Count == 0 ? "There is no loadable NeuralMaster add-on to verify."
                    : "The trusted files do not match all installed copies: " + string.Join(", ", conflicts.Distinct(PathComparer)));
            VerifyGameFiles(ctx, files.Where(file => file.Role is FileRole.Addon or FileRole.Host));
            Installers.AssertGameClosed(ctx.Exe);
            if (previous is not null) return $"Verified that the installed copies already match NeuralMaster {previous.Name}.";

            var imported = ReadImported(ctx);
            imported.Add(build);
            var destination = StorageFile(ctx, Path.Combine(ctx.Store, "known-builds.json"));
            Directory.CreateDirectory(ctx.Store);
            var temporary = StorageFile(ctx, Path.Combine(ctx.Store, $"known-builds-{Guid.NewGuid():N}.tmp"));
            WriteDurable(temporary, JsonSerializer.SerializeToUtf8Bytes(imported, Journal.JsonOptions));
            File.Move(temporary, destination, overwrite: true);
            return $"Verified NeuralMaster {name} for {Path.GetFileName(ctx.Game)}.";
        }, log);
    }

    private static string TrustedSource(GameContext ctx, string source, Func<string, bool> acceptsName)
    {
        var full = Path.GetFullPath(source);
        if (full.StartsWith(ctx.Game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(full) || !acceptsName(Path.GetFileName(full)))
            throw new InstallException("Choose the trusted NeuralMaster add-on and matching ReShade DLL from outside the game folder.");
        for (string? path = full; path is not null; path = Path.GetDirectoryName(path)) CheckOrdinary(path);
        return full;
    }

    private static List<SnapshotEntry>? Identify(GameContext ctx, Layout layout, IReadOnlyList<string> addons,
        NeuralMasterKnownBuild[] catalogue, out NeuralMasterKnownBuild? identity, out List<string> conflicts,
        bool includeReleases = false, Func<string, string, string, bool>? proposedHost = null)
    {
        var files = new Dictionary<string, SnapshotEntry>(PathComparer);
        conflicts = [];
        identity = null;
        if (addons.Count == 0) return null;
        foreach (var addon in addons)
        {
            var rel = Path.GetRelativePath(ctx.Game, addon);
            var actual = Components.Sha256(GameFile(ctx, rel));
            var known = identity is not null && PathComparer.Equals(identity.AddonSha256, actual)
                ? identity : FindKnownBuild(ctx, catalogue, actual, includeReleases);
            if (known is null || (identity is not null && identity != known))
            {
                conflicts.Add(rel);
                continue;
            }
            identity = known;
            files[rel] = new(rel, FileRole.Addon, actual);
            var hosts = layout.HostsByAddonDirectory[Path.GetDirectoryName(addon)!];
            // A configured subfolder is not a second host beside its add-on. Consider only real hosts;
            // if none exist, report all candidate host paths so the user can see what is missing.
            var choices = hosts.Select(dir => Path.GetRelativePath(ctx.Game, Path.Combine(dir, HostName))).ToList();
            var present = choices.Where(path => File.Exists(GameFile(ctx, path))).ToList();
            if (present.Count == 0) conflicts.AddRange(choices);
            foreach (var host in present)
            {
                var digest = Components.Sha256(GameFile(ctx, host));
                if (!PathComparer.Equals(digest, known.HostSha256) && proposedHost?.Invoke(host, digest, known.HostSha256) != true) conflicts.Add(host);
                else files[host] = new(host, FileRole.Host, digest);
            }
        }
        if (conflicts.Count > 0) return null;

        // Keep both independent ReShade settings files and optional NeuralMaster sidecars for recovery.
        // Neither the backup nor the restore applies those settings over the user's current configuration.
        var settingsDirs = files.Values.Select(file => Path.GetDirectoryName(GameFile(ctx, file.Path))!)
            .Distinct(PathComparer).ToList();
        foreach (var dir in settingsDirs)
        foreach (var name in new[] { SharedIni, OwnedIni })
        {
            var path = Path.Combine(dir, name);
            var relative = Path.GetRelativePath(ctx.Game, path);
            var full = GameFile(ctx, relative);
            files[relative] = new(relative, FileRole.Settings, File.Exists(full) ? Components.Sha256(full) : null);
        }
        return files.Values.OrderBy(file => file.Path, PathComparer).ToList();
    }

    private static bool ManagerOwnsConflicts(string gameDir, IReadOnlyList<string> conflicts)
    {
        try
        {
            if (conflicts.Count == 0 || Journal.ReadManifest(gameDir) is not { } manifest) return false;
            var owned = manifest.Added.Concat(manifest.Replaced.Select(file => file.Rel))
                .OfType<string>().Select(path => path.Replace('/', '\\')).ToHashSet(PathComparer);
            return conflicts.All(owned.Contains);
        }
        catch (InstallException) { return false; }
    }

    private static bool ManagedAlternate(GameContext ctx, IReadOnlyList<SnapshotEntry> alternate)
    {
        if (File.Exists(Path.Combine(ctx.Game, "_DLSS5_Backup", "manifest.json")) || Scanner.OtherAppRecordRouteFor(ctx.Game) is not null)
            return true;
        var addonPaths = alternate.Where(file => file.Role == FileRole.Addon).Select(file => file.Path).ToHashSet(PathComparer);
        if (Journal.ReadManifest(ctx.Game) is { } manifest &&
            (manifest.Route is "nraddon" or "native" or "multipass" or "feeder" or "optiscaler" or "neuralmaster" or "uplift"
             || manifest.Added.Concat(manifest.Replaced.Select(file => file.Rel)).Any(path => addonPaths.Contains(path.Replace('/', '\\')))))
            return true;
        return alternate.Select(file => Path.GetDirectoryName(GameFile(ctx, file.Path))!).Distinct(PathComparer)
            .Any(NrAddon.SetupInstalled);
    }

    private static Plan Describe(NeuralMasterSnapshotState state, SavedSnapshot? backup, string message,
        IReadOnlyList<SnapshotEntry>? active = null, string? build = null, IReadOnlyList<SnapshotEntry>? restore = null,
        IReadOnlyList<string>? conflicts = null, IReadOnlyList<SnapshotEntry>? alternate = null, string? alternateName = null,
        bool canRestore = false, IReadOnlyDictionary<string, string?>? restoreCurrentHashes = null,
        IReadOnlyList<string>? matchingHosts = null)
    {
        active ??= [];
        restore ??= [];
        alternate ??= [];
        restoreCurrentHashes ??= new Dictionary<string, string?>();
        var signature = JsonSerializer.SerializeToUtf8Bytes(new { state, Backup = backup?.Directory, Active = active, Restore = restore,
            RestoreCurrentHashes = restoreCurrentHashes, Alternate = alternate, conflicts });
        var fingerprint = Convert.ToHexString(SHA256.HashData(signature));
        var status = Status(state, backup, message, build,
            active.Where(file => file.Role == FileRole.Addon).Select(file => file.Path).ToList(),
            restore.Select(file => file.Path).ToList(), conflicts, canUninstall: active.Count > 0, canRestore: canRestore,
            canSwitch: alternate.Count > 0, fingerprint: fingerprint) with
        {
            VerifiedHostPaths = matchingHosts ?? active.Where(file => file.Role == FileRole.Host).Select(file => file.Path).ToList(),
        };
        return new(status, backup, active, alternate, alternateName, restore, restoreCurrentHashes, build);
    }

    private static NeuralMasterSnapshotStatus Status(NeuralMasterSnapshotState state, SavedSnapshot? backup, string message,
        string? build = null, IReadOnlyList<string>? uninstall = null, IReadOnlyList<string>? restore = null,
        IReadOnlyList<string>? conflicts = null, bool canUninstall = false, bool canRestore = false, bool canSwitch = false,
        bool canRecover = false, string fingerprint = "") =>
        new(state, build ?? backup?.Record.Build, backup?.Record.Date, backup?.Directory,
            uninstall ?? [], restore ?? [], conflicts?.Distinct(PathComparer).Order(PathComparer).ToList() ?? [], message,
            canUninstall, canRestore, canSwitch, canRecover, fingerprint);
}
