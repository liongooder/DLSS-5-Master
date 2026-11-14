using System.Security.Cryptography;

namespace DLSS5Master.Core;

/// <summary>Disk candidates only. Neither version nor a prepared file proves active-provider acceptance.</summary>
public sealed record MfgProviderImage(string Path, string? Version, int Bitness, string Sha256);

/// <summary>
/// Retains the selected FG files and existing working backend across reinstall's Restore step.
/// Does not load DLLs, patch hooks, edit settings, change Streamline or advertise a multiplier.
/// </summary>
public sealed class NativeMfgProviders : IDisposable
{
    public const string WorkingBackendSha256 = "88570f347931faba6a7807fb3b4035345db3ce5b08a98c8e21399a5f6d5b4854";
    public sealed record Entry(string RelativeTarget, string StagedPath, MfgProviderImage Selected);
    private readonly string _folder;
    private readonly List<string> _ownedFiles = new();
    public List<Entry> Entries { get; } = new();
    public List<string> BackendTargets { get; } = new();
    public string BackendPath { get; private set; } = "";
    public string BackendSha256 { get; private set; } = "";

    private NativeMfgProviders()
    {
        _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "d5m-mfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    private static MfgProviderImage Inspect(string path) =>
        new(System.IO.Path.GetFullPath(path), Pe.GetFileVersion(path), Pe.GetBitness(path), Hash(path));

    // A source version is not an identity. Equal-version different-byte files
    // remain explicit, and the current installed candidate is kept by default.
    public static MfgProviderImage Select(MfgProviderImage current, MfgProviderImage? supplied, Action<string> log)
    {
        if (current.Bitness != 64 || supplied is { Bitness: not 64 })
            throw new InstallException("Native MFG provider preparation requires 64-bit DLLs.");
        if (supplied is null || supplied.Sha256 == current.Sha256) return current;
        if (!System.Version.TryParse(current.Version, out var before) || !System.Version.TryParse(supplied.Version, out var after))
            throw new InstallException("MFG provider versions are unknown; select and review the exact files before reinstalling.");
        if (after > before) return supplied;
        if (after == before)
            log($"MFG disk candidates share version {current.Version} but differ in SHA256; keeping current {current.Path} ({current.Sha256}), not assuming equivalence with {supplied.Sha256}.");
        return current;
    }

    /// <summary>Prefer only the exact previously working backend, not any file merely called 1.8.5.</summary>
    public static string? KnownWorkingBackend(string exeDir, IEnumerable<UpscalerFile> files)
    {
        var directories = files.Where(f => f.Kind == UpscalerKind.DlssFG).Select(f => System.IO.Path.GetDirectoryName(f.Path)!)
            .Prepend(exeDir).Distinct(StringComparer.OrdinalIgnoreCase);
        var copies = directories.Select(d => System.IO.Path.Combine(d, Components.MfgAddon)).Where(File.Exists).ToList();
        if (copies.Count == 0) return null;
        var hashes = copies.Select(Hash).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hashes.Count > 1)
            throw new InstallException("Different MFG Unlock binaries are present in the selected runtime folders. Review the backend copies before reinstalling.");
        return hashes[0] == WorkingBackendSha256 ? copies[0] : null;
    }

    public static NativeMfgProviders Stage(string gameDir, IEnumerable<UpscalerFile> files, string? supplied,
                                           string backend, Action<string> log, string? exeDir = null)
    {
        var stage = new NativeMfgProviders();
        try
        {
            stage.BackendSha256 = Hash(backend);
            stage.BackendPath = stage.CopyVerified(backend, stage.BackendSha256, Components.MfgAddon);
            var captured = files.ToList();
            // Preserve existing mirrors too: RestoreForReinstall can remove the storage copy.
            foreach (var dir in captured.Where(f => f.Kind == UpscalerKind.DlssFG)
                         .Select(f => System.IO.Path.GetDirectoryName(f.Path)!).Append(exeDir ?? gameDir)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var target = System.IO.Path.Combine(dir, Components.MfgAddon);
                if (!File.Exists(target)) continue;
                var rel = System.IO.Path.GetRelativePath(gameDir, target);
                _ = Journal.SafePath(gameDir, rel);
                stage.BackendTargets.Add(rel);
            }
            var candidate = supplied is null ? null : Inspect(supplied);
            foreach (var file in captured.Where(f => f.Kind == UpscalerKind.DlssFG && f.Bitness == 64)
                         .DistinctBy(f => System.IO.Path.GetFullPath(f.Path), StringComparer.OrdinalIgnoreCase))
            {
                var target = Journal.SafePath(gameDir, file.Rel);
                if (!target.Equals(System.IO.Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase))
                    throw new InstallException("MFG scan path and relative target disagree.");
                var selected = Select(Inspect(file.Path), candidate, log);
                var copy = stage.CopyVerified(selected.Path, selected.Sha256, $"provider-{stage.Entries.Count}.dll");
                stage.Entries.Add(new(file.Rel, copy, selected));
                log($"MFG disk-selected candidate: target={file.Rel}; version={selected.Version}; SHA256={selected.Sha256}; source={selected.Path}. Active loaded provider and native acceptance are not yet observed.");
            }
            if (stage.Entries.Count == 0) throw new InstallException("No native 64-bit DLSS-G provider targets were captured before reinstall.");
            return stage;
        }
        catch { stage.Dispose(); throw; }
    }

    private string CopyVerified(string source, string hash, string name)
    {
        var target = System.IO.Path.Combine(_folder, name);
        _ownedFiles.Add(target);
        File.Copy(source, target, false);
        if (Hash(target) != hash || Hash(source) != hash)
            throw new InstallException("An MFG source changed during staging. Nothing in the game was changed.");
        return target;
    }

    public void Place(Session session)
    {
        if (Hash(BackendPath) != BackendSha256)
            throw new InstallException("The staged MFG backend changed; installation stopped.");
        foreach (var rel in BackendTargets)
        {
            var target = Journal.SafePath(session.GameDir, rel);
            if (!File.Exists(target) || Hash(target) != BackendSha256)
                session.CopyTracked(BackendPath, target, "addon");
            if (Hash(target) != BackendSha256)
                throw new InstallException("An MFG backend mirror failed its SHA256 check.");
            session.Log($"MFG backend preserved: {rel}; SHA256={BackendSha256}. No settings changed.");
        }
        foreach (var entry in Entries)
        {
            if (Hash(entry.StagedPath) != entry.Selected.Sha256)
                throw new InstallException("The staged MFG provider changed; installation stopped.");
            var target = Journal.SafePath(session.GameDir, entry.RelativeTarget);
            if (!File.Exists(target) || Hash(target) != entry.Selected.Sha256)
                session.CopyTracked(entry.StagedPath, target, "mfg-runtime");
            if (Hash(target) != entry.Selected.Sha256)
                throw new InstallException("An installed MFG provider failed its SHA256 check.");
            session.Log($"MFG disk-installed candidate: target={entry.RelativeTarget}; version={entry.Selected.Version}; SHA256={entry.Selected.Sha256}. This is not a loaded-module or output-count measurement.");
        }
    }

    public void Dispose()
    {
        // Only our explicit staging files, never a recursive deletion or game path.
        foreach (var file in _ownedFiles) try { File.Delete(file); } catch (IOException) { }
        try { Directory.Delete(_folder, false); } catch (IOException) { }
    }
}
