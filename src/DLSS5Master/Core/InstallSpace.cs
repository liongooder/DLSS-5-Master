namespace DLSS5Master.Core;

public static partial class Installers
{
    // Plan destination writes without changing the game. Count conservatively:
    // version checks may keep a runtime that is included here.
    private static long InstallSpaceNeeded(InstallRequest req, GameScan scan, Dictionary<string, string> runtime,
        string? reshade, string? reshade64, string? consumer, string? multipass,
        Components.FeederPayload? feeder, string? opti, string? lecram, NativeMfgProviders? mfg, Action<InstallPreview>? preview = null)
    {
        var exeDir = Path.GetDirectoryName(req.ExePath)!;
        var copies = new List<(string Source, string Target)>();
        void Add(string? source, string target) { if (source is not null && File.Exists(source)) copies.Add((source, target)); }
        void Beside(string? source, string name) => Add(source, Path.Combine(exeDir, name));
        void Tree(string? source, string target)
        {
            if (source is null || !Directory.Exists(source)) return;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                Add(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }

        if (req.Route != Route.OptiScaler)
        {
            Beside(reshade, ReShadeHookFor(req));
            foreach (var file in scan.Upscalers.Where(u => u.Kind is UpscalerKind.DlssSR or UpscalerKind.DlssNR))
                Add(runtime.GetValueOrDefault(file.Name), file.Path);
            if (req.Route == Route.Feeder && feeder is not null)
            {
                Beside(req.Bitness == 64 ? feeder.Addon64 : feeder.Addon32,
                    req.Bitness == 64 ? "dlss5-feed.addon64" : "dlss5-feed.addon32");
                Tree(feeder.ShaderRoot, Path.Combine(exeDir, "reshade-shaders"));
                if (feeder.Verifier is not null) Beside(feeder.Verifier, Path.GetFileName(feeder.Verifier));
                var host = req.Bitness == 64 ? exeDir : Path.Combine(exeDir, "host64");
                Add(consumer, Path.Combine(host, ConsumerAddon));
                Add(runtime.GetValueOrDefault(NeuralDll), Path.Combine(host, NeuralDll));
                Add(runtime.GetValueOrDefault(DlssDll), Path.Combine(host, DlssDll));
                if (req.Bitness != 64)
                {
                    Add(feeder.Host64, Path.Combine(host, "dlss5-feed-host64.exe"));
                    Add(reshade64, Path.Combine(host, "dxgi.dll"));
                }
            }
            else
            {
                Beside(runtime.GetValueOrDefault(NeuralDll), NeuralDll);
                Beside(runtime.GetValueOrDefault(DlssDll), DlssDll);
                if (req.Route == Route.LecramNr && lecram is not null) Tree(lecram, exeDir);
                else Beside(req.Route == Route.Multipass ? multipass : consumer,
                    req.Route == Route.Multipass ? MultipassAddon : ConsumerAddon);
                if (req.Route == Route.Multipass)
                    foreach (var (name, source) in runtime.Where(kv => kv.Key.StartsWith("sl.", StringComparison.OrdinalIgnoreCase)))
                        Beside(source, name);
            }
        }
        else if (opti is not null)
        {
            // Include the complete package even when the build installs only a subset.
            Tree(opti, exeDir);
            Beside(Path.Combine(opti, "OptiScaler.dll"), req.OptiHook);
            Tree(Path.Combine(opti, "Licenses"), Path.Combine(exeDir, "OptiScaler", "licenses"));
            Beside(runtime.GetValueOrDefault(NeuralDll), NeuralDll);
        }
        if (mfg is not null)
        {
            Beside(mfg.BackendPath, Components.MfgAddon);
            foreach (var rel in mfg.BackendTargets) Add(mfg.BackendPath, Journal.SafePath(scan.Dir, rel));
            foreach (var entry in mfg.Entries) Add(entry.StagedPath, Journal.SafePath(scan.Dir, entry.RelativeTarget));
        }

        // Deleted add-ons, compiler DLLs and edited settings also need backups.
        // Include small loose files conservatively, including originals restored
        // from an earlier installation before this one starts.
        var backups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void LooseFiles(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir))
                if (Path.GetExtension(file).ToLowerInvariant() is ".dll" or ".asi" or ".addon64" or ".addon32" or ".addon" or ".ini" or ".cfg" or ".json" or ".log")
                    backups.Add(file);
        }
        LooseFiles(exeDir);
        if (req.Route == Route.Feeder && req.Bitness != 64) LooseFiles(Path.Combine(exeDir, "host64"));
        foreach (var replacement in scan.Manifest?.Replaced ?? new List<Replacement>())
            backups.Add(Journal.SafePath(scan.Dir, replacement.Rel));
        var required = CopySpaceNeeded(scan.Dir, scan.Manifest, copies, backups);
        var unique = copies.GroupBy(c => Path.GetFullPath(c.Target), StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        var writes = unique.Sum(c => new FileInfo(c.Source).Length);
        var rollbackTargets = unique.Select(c => c.Target).Concat(backups).Append(Journal.ManifestPath(scan.Dir)).ToList();
        if (scan.Manifest is { } previous)
        {
            rollbackTargets.AddRange(previous.Added.Select(r => Journal.SafePath(scan.Dir, r)));
            rollbackTargets.AddRange(previous.Replaced.Select(r => Journal.SafePath(scan.Dir, r.Rel)));
            rollbackTargets.AddRange(previous.Replaced.Select(r => Journal.SafePath(Journal.OriginalsDir(scan.Dir, previous), r.Rel)));
            rollbackTargets.AddRange(Journal.RuntimeLeftovers(scan.Dir, previous));
        }
        long rollback = 1024 * 1024; // journal and snapshot-index allowance
        foreach (var target in rollbackTargets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            InstallTransaction.CheckPath(scan.Dir, target);
            if (File.Exists(target)) rollback += new FileInfo(target).Length;
        }
        bool sameDrive = string.Equals(Path.GetPathRoot(scan.Dir), Path.GetPathRoot(AppPaths.Root), StringComparison.OrdinalIgnoreCase);
        if (!sameDrive) AssertRoom(AppPaths.Root, Path.GetPathRoot(Path.GetFullPath(AppPaths.Root))!, null, rollback);
        preview?.Invoke(new InstallPreview(unique.Select(c => new PlannedCopy(Path.GetRelativePath(scan.Dir, c.Target),
            File.Exists(c.Target) ? Pe.GetFileVersion(c.Target) : null, Pe.GetFileVersion(c.Source), new FileInfo(c.Source).Length)).OrderBy(c => c.RelativePath).ToList(),
            required, Math.Max(0, required - writes), req.Route == Route.OptiScaler ? new[] { "OptiScaler.ini" } :
            req.Route == Route.Feeder ? new[] { "ReShade.ini", "ReShadePreset.ini", "dlss5-feed.cfg" } : new[] { "ReShade.ini", "ReShadePreset.ini" }, rollback));
        return required + (sameDrive ? rollback : 0);
    }

    internal static long CopySpaceNeeded(string gameDir, Manifest? old,
        IEnumerable<(string Source, string Target)> copies, IEnumerable<string>? backupOnly = null)
    {
        // Deduplicate destinations, never sources: one runtime can be mirrored
        // into several folders. Repeated writes to a target need one backup.
        var writes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, target) in copies)
        {
            var path = Path.GetFullPath(target);
            writes[path] = Math.Max(writes.GetValueOrDefault(path), new FileInfo(source).Length);
        }
        long total = writes.Values.Sum();
        var targets = writes.Keys.Concat(backupOnly ?? Enumerable.Empty<string>())
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var rel = Path.GetRelativePath(gameDir, target);
            _ = Journal.SafePath(gameDir, rel);
            var replacement = old?.Replaced.FirstOrDefault(r => r.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase));
            // Reinstall restores this file first. Its original, rather than the
            // installed version, is what the new session will back up.
            var original = replacement is not null ? Path.Combine(Journal.OriginalsDir(gameDir, old!), rel)
                : old?.Added.Contains(rel, StringComparer.OrdinalIgnoreCase) == true ? null : target;
            if (original is not null && File.Exists(original)) total += new FileInfo(original).Length;
        }
        return total;
    }
}
