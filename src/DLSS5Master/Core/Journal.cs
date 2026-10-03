using System.Text.Json;

namespace DLSS5Master.Core;

/// <summary>A game file that existed before an install and was overwritten (its original is in the backup).</summary>
public sealed class Replacement
{
    public string Rel { get; set; } = "";
    public string? OldVersion { get; set; }
    public string? NewVersion { get; set; }
    public string? Kind { get; set; }
}

/// <summary>Everything one install changed inside a game folder; stored as manifest.json in the backup folder.</summary>
public sealed class Manifest
{
    public int Version { get; set; } = 1;
    public string BackupPrefix { get; set; } = "";
    public DateTime Date { get; set; }
    public string Route { get; set; } = "";
    public string Exe { get; set; } = "";
    public string Api { get; set; } = "";
    public int Bitness { get; set; }
    public string? ReShadeHook { get; set; }
    public string? OptiScalerBuild { get; set; }
    public string? OptiScalerHook { get; set; }
    public string? MfgUnlock { get; set; }
    public List<Replacement> Replaced { get; set; } = new();
    public List<string> Added { get; set; } = new();
    public List<string> AddedDirs { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed class InstallException(string message) : Exception(message);

public static class Journal
{
    public const string BackupDir = "_DLSS5Master_Backup";
    internal const string ManifestName = "manifest.json";

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string BackupRoot(string gameDir) => Path.Combine(Path.GetFullPath(gameDir), BackupDir);

    internal static string ManifestPath(string gameDir) => Path.Combine(BackupRoot(gameDir), ManifestName);

    public static Manifest? ReadManifest(string gameDir)
    {
        var path = ManifestPath(gameDir);
        if (!File.Exists(path)) return null;
        Manifest? m;
        try
        {
            m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InstallException($"The install record {path} could not be read ({e.Message}). Restore cannot continue automatically; check or remove that file.");
        }
        if (m is null) throw new InstallException($"The install record {path} is empty or damaged.");
        // Older or hand-edited records may carry nulls; keep the "lists are never null" promise.
        m.Replaced ??= new();
        m.Added ??= new();
        m.AddedDirs ??= new();
        m.Notes ??= new();
        m.BackupPrefix ??= "";
        m.Route ??= "";
        m.Exe ??= "";
        m.Api ??= "";
        m.Replaced.RemoveAll(r => r is null);
        return m;
    }

    public static string SafePath(string gameDir, string rel)
    {
        var root = Path.GetFullPath(gameDir).TrimEnd('\\', '/');
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, rel)); }
        catch (Exception e) { throw new InstallException($"Invalid path '{rel}': {e.Message}"); }
        if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InstallException($"Refusing to touch '{rel}': it is outside the game folder {root}.");
        return full;
    }

    public static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".dlss5master-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    internal static void ClearReadOnly(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    internal static string OriginalsDir(string gameDir, Manifest m) =>
        Path.Combine(BackupRoot(gameDir), m.BackupPrefix.Replace('/', '\\'));
}

/// <summary>
/// One install (or edit) of a game folder. Every write goes through here so the manifest always
/// describes how to undo it.
/// </summary>
public sealed class Session
{
    private readonly Action<string> _log;
    private readonly string _gameName;

    public string GameDir { get; }
    public Manifest Manifest { get; }

    public Session(string gameDir, Manifest manifest, Action<string> log)
    {
        GameDir = Path.GetFullPath(gameDir).TrimEnd('\\', '/');
        Manifest = manifest;
        _log = log;
        _gameName = Path.GetFileName(GameDir);
        if (string.IsNullOrEmpty(Manifest.BackupPrefix)) Manifest.BackupPrefix = NewPrefix();
    }

    private static string NewPrefix() => "originals/" + Guid.NewGuid().ToString("N");

    public static Session Begin(string gameDir, string exePath, string route, string api, int bitness, Action<string> log)
    {
        var manifest = Journal.ReadManifest(gameDir) ?? new Manifest { BackupPrefix = NewPrefix() };
        var s = new Session(gameDir, manifest, log);
        manifest.Date = DateTime.UtcNow;
        manifest.Route = route;
        manifest.Exe = s.Rel(exePath);
        manifest.Api = api;
        manifest.Bitness = bitness;
        return s;
    }

    public void Log(string line)
    {
        _log(line);
        AppPaths.Log($"[{_gameName}] {line}");
    }

    public string Rel(string path)
    {
        var full = Path.GetFullPath(path);
        var rel = Path.GetRelativePath(GameDir, full);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            throw new InstallException($"'{path}' is outside the game folder {GameDir}.");
        return rel;
    }

    public bool WasAdded(string rel) => Manifest.Added.Any(a => a.Equals(rel, StringComparison.OrdinalIgnoreCase));

    private Replacement? FindReplaced(string rel) =>
        Manifest.Replaced.FirstOrDefault(r => r.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase));

    private string BackupFile(string rel) => Path.Combine(Journal.OriginalsDir(GameDir, Manifest), rel);

    public string TrackBeforeWrite(string target, string? kind = null, string? newVersion = null)
    {
        var rel = Rel(target);
        var full = Journal.SafePath(GameDir, rel);
        if (WasAdded(rel)) return rel;

        var known = FindReplaced(rel);
        if (known is not null)
        {
            if (newVersion is not null)
            {
                known.NewVersion = newVersion;
                Save();
            }
            return rel;
        }

        if (File.Exists(full))
        {
            var backup = BackupFile(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            // File.Copy keeps the attributes, so Restore brings back a read-only flag too.
            File.Copy(full, backup, true);
            Manifest.Replaced.Add(new Replacement { Rel = rel, OldVersion = Pe.GetFileVersion(full), NewVersion = newVersion, Kind = kind });
        }
        else
        {
            Manifest.Added.Add(rel);
            // Remember each folder that this file will bring into existence, so Restore can remove it again.
            var missing = new List<string>();
            for (var dir = Path.GetDirectoryName(full);
                 dir is not null && dir.Length > GameDir.Length && !Directory.Exists(dir);
                 dir = Path.GetDirectoryName(dir))
                missing.Add(Rel(dir));
            foreach (var d in missing)
                if (!Manifest.AddedDirs.Contains(d, StringComparer.OrdinalIgnoreCase)) Manifest.AddedDirs.Add(d);
        }
        Save();
        return rel;
    }

    private static void PrepareTarget(string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Journal.ClearReadOnly(dest);
    }

    public string CopyTracked(string source, string dest, string? kind = null)
    {
        var rel = TrackBeforeWrite(dest, kind, Pe.GetFileVersion(source));
        PrepareTarget(dest);
        File.Copy(source, dest, true);
        return rel;
    }

    public string WriteTracked(string dest, string text, string? kind = "config")
    {
        var rel = TrackBeforeWrite(dest, kind);
        PrepareTarget(dest);
        File.WriteAllText(dest, text);
        return rel;
    }

    public void DeleteTracked(string target, string? kind = null)
    {
        var rel = Rel(target);
        bool ours = WasAdded(rel);
        if (File.Exists(target))
        {
            TrackBeforeWrite(target, kind);
            Journal.ClearReadOnly(target);
            File.Delete(target);
        }
        if (ours)
        {
            // Our own file is gone, so there is nothing left to undo for it.
            Manifest.Added.RemoveAll(a => a.Equals(rel, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    public void CopyTreeTracked(string sourceRoot, string destRoot, string? kind = null)
    {
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            CopyTracked(file, Path.Combine(destRoot, Path.GetRelativePath(sourceRoot, file)), kind);
    }

    public void Save()
    {
        var root = Journal.BackupRoot(GameDir);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Journal.ManifestName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Manifest, Journal.JsonOptions));
        File.Move(tmp, path, true);
    }

    public static bool Restore(string gameDir, Action<string> log)
    {
        var manifest = Journal.ReadManifest(gameDir);
        if (manifest is null) return false;
        var s = new Session(gameDir, manifest, log);
        var originals = Journal.OriginalsDir(s.GameDir, manifest);

        // Validate everything first so a broken backup never leaves the game half-restored.
        var plan = new List<(string backup, string target, string rel)>();
        foreach (var r in manifest.Replaced)
        {
            var target = Journal.SafePath(s.GameDir, r.Rel);
            var backup = Path.Combine(originals, r.Rel);
            if (!File.Exists(backup))
                throw new InstallException($"The backup of {r.Rel} is missing ({backup}). Nothing was restored.");
            plan.Add((backup, target, r.Rel));
        }
        var added = manifest.Added.Select(a => (rel: a, full: Journal.SafePath(s.GameDir, a))).ToList();
        var dirs = manifest.AddedDirs.Select(d => (rel: d, full: Journal.SafePath(s.GameDir, d))).ToList();

        foreach (var (backup, target, rel) in plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Journal.ClearReadOnly(target);
            File.Copy(backup, target, true);
            s.Log($"Restored {rel}");
        }
        foreach (var (rel, full) in added)
        {
            if (!File.Exists(full)) continue;
            Journal.ClearReadOnly(full);
            File.Delete(full);
            s.Log($"Removed {rel}");
        }
        foreach (var (rel, full) in dirs.OrderByDescending(d => d.full.Count(c => c == '\\')))
        {
            try
            {
                if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                {
                    Directory.Delete(full);
                    s.Log($"Removed folder {rel}");
                }
            }
            catch (Exception e) { s.Log($"Could not remove folder {rel}: {e.Message}"); }
        }

        var manifestPath = Journal.ManifestPath(s.GameDir);
        var done = $"{manifestPath}.done-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(manifestPath, done, true);
        s.Log($"Install record closed ({Path.GetFileName(done)}).");
        try
        {
            if (Directory.Exists(originals))
            {
                foreach (var f in Directory.EnumerateFiles(originals, "*", SearchOption.AllDirectories)) Journal.ClearReadOnly(f);
                Directory.Delete(originals, true);
            }
            // Drop the now-empty "originals" parent as well.
            var parent = Path.GetDirectoryName(originals);
            if (parent is not null && Directory.Exists(parent) && !parent.Equals(Journal.BackupRoot(s.GameDir), StringComparison.OrdinalIgnoreCase)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
            s.Log("Deleted the backup copies of this install.");
        }
        catch (Exception e) { s.Log($"Could not delete the backup copies: {e.Message}"); }
        s.Log("Restore complete.");
        return true;
    }
}
