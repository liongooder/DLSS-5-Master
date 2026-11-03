namespace DLSS5Master.Core;

/// <summary>
/// lecram's DLSS5 NR add-on: a paid add-on that players buy themselves. DLSS 5 Master never ships or downloads it; its route
/// installs the player's own copy, from the folder picked in Settings (the unzipped "DLSS5 NR Add-on" folder) or from a game
/// that already has it. Its layout, from its INSTALL.txt: dlss5-nr.addon64 and dlss5-nr.addon32 beside the game's ReShade,
/// and the dlss5-nr-host folder as it is (its 64-bit host for 32-bit, Direct3D 9/10 and OpenGL games).
/// </summary>
public static class LecramNr
{
    public const string Addon64 = "dlss5-nr.addon64";
    public const string Addon32 = "dlss5-nr.addon32";
    public const string HostFolder = "dlss5-nr-host";

    /// <summary>
    /// lecram's own setup keeps its record of a game beside the game's exe, in DLSS5-NR-setup\manifest.json, with backups of
    /// the files it replaced (DLSS 5 Master's own, when it came after it) and NVIDIA's runtime moved into its shared store.
    /// Its Remove NR undoes that from the record, so DLSS 5 Master must not change those files underneath it.
    /// </summary>
    public static string SetupRecord(string exeDir) => Path.Combine(exeDir, "DLSS5-NR-setup", "manifest.json");

    public static bool SetupInstalled(string exeDir) => File.Exists(SetupRecord(exeDir));

    public const string SetupInstalledMessage =
        "lecram's DLSS5 NR Setup has NR installed in this game. Remove it with Remove NR in lecram's setup first, then set up here " +
        "(the lecram's NR route installs your own copy); or keep NR as lecram's setup installed it.";

    public const string SetupAfterUsMessage =
        "lecram's DLSS5 NR Setup installed NR in this game after DLSS 5 Master did, and keeps DLSS 5 Master's files as the originals " +
        "it puts back. Use Remove NR in lecram's setup first, then Restore originals here.";

    /// <summary>Whether <paramref name="dir"/> holds a copy: its dlss5-nr.addon64 at least.</summary>
    public static bool IsCopy(string? dir) => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, Addon64));

    /// <summary>
    /// The copy in a folder the player picked: the folder itself, the one around it when they picked its host folder (which
    /// holds a dlss5-nr.addon64 of its own), or a folder in it (the folder the zip and the unzipped folder sit in). Null when none.
    /// </summary>
    public static string? ResolveFolder(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
            if (Path.GetFileName(full).Equals(HostFolder, StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(full) is { } parent && IsCopy(parent) ? parent : null;
            if (IsCopy(full)) return full;
            return Directory.EnumerateDirectories(full)
                .Where(d => !Path.GetFileName(d).Equals(HostFolder, StringComparison.OrdinalIgnoreCase) && IsCopy(d))
                .OrderByDescending(d => Version(d) ?? "", Comparer<string>.Create((a, b) => Scanner.CompareVersions(a, b)))
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The copy's version (dlss5-nr.addon64's file version), or null.</summary>
    public static string? Version(string dir) => Pe.GetFileVersion(Path.Combine(dir, Addon64));

    /// <summary>
    /// The copy's add-on file in the game: <paramref name="exeDir"/>'s, or the one an earlier setup by this app set aside into
    /// its backup (Restore originals, and the restore that starts a reinstall, put it back). Null when the game has none.
    /// </summary>
    public static string? InGame(string gameDir, string exeDir, Manifest? manifest, string addon = Addon64)
    {
        var here = Path.Combine(exeDir, addon);
        if (File.Exists(here)) return here;
        if (manifest is null) return null;
        try
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(gameDir), Path.GetFullPath(here));
            if (!manifest.Replaced.Any(r => r.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase))) return null;
            var backup = Path.Combine(Journal.OriginalsDir(gameDir, manifest), rel);
            return File.Exists(backup) ? backup : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstallException or ArgumentException) { return null; }
    }

    /// <summary>The copy to install: the folder in Settings, else the game's own. Null when neither has one.</summary>
    public static string? Source(string? settingsFolder, string gameDir, string exeDir, Manifest? manifest) =>
        IsCopy(settingsFolder) ? settingsFolder
        : InGame(gameDir, exeDir, manifest) is not null ? exeDir
        : null;

    /// <summary>Whether <paramref name="source"/> still holds the copy: the folder itself, or the game when it is the game's.</summary>
    public static bool Has(string? source, string gameDir, string exeDir, Manifest? manifest) =>
        IsCopy(source) || source is not null && SameDir(source, exeDir) && InGame(gameDir, exeDir, manifest) is not null;

    private static bool SameDir(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>A copy staged in a folder of its own, deleted when disposed.</summary>
    public sealed class Staged(string dir) : IDisposable
    {
        public string Dir { get; } = dir;

        public void Dispose()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Copies the copy's files from <paramref name="source"/> into a folder of their own before the game folder is touched:
    /// a reinstall's restore takes out what an earlier setup put in, and puts back what it set aside. From the game itself
    /// (<paramref name="source"/> = <paramref name="exeDir"/>) an add-on that setup set aside comes from the backup.
    /// </summary>
    public static Staged Stage(string source, string gameDir, string exeDir, Manifest? manifest)
    {
        var staged = new Staged(Path.Combine(Path.GetTempPath(), "DLSS5Master-lecram-nr", Guid.NewGuid().ToString("N")));
        try
        {
            Copy(source, staged.Dir, gameDir, exeDir, manifest);
            return staged;
        }
        catch
        {
            staged.Dispose();
            throw;
        }
    }

    private static void Copy(string source, string staging, string gameDir, string exeDir, Manifest? manifest)
    {
        bool fromGame = SameDir(source, exeDir);
        Directory.CreateDirectory(staging);
        foreach (var addon in new[] { Addon64, Addon32 })
        {
            var file = fromGame ? InGame(gameDir, exeDir, manifest, addon) : Path.Combine(source, addon);
            if (file is not null && File.Exists(file)) File.Copy(file, Path.Combine(staging, addon), true);
        }
        if (!File.Exists(Path.Combine(staging, Addon64)))
            throw new InstallException($"lecram's DLSS5 NR ({Addon64}) is no longer in {source}.");
        var host = Path.Combine(source, HostFolder);
        if (!Directory.Exists(host)) return;
        foreach (var file in Directory.EnumerateFiles(host, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(staging, HostFolder, Path.GetRelativePath(host, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}
