using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace DLSS5Master.Core;

/// <summary>One installed game as found in a launcher's records or a user folder.</summary>
public sealed class GameEntry
{
    public string Launcher { get; set; } = "";
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Dir { get; set; } = "";
    public string? Poster { get; set; }
    public string? Exe { get; set; }
}

public static partial class Library
{
    public static List<GameEntry> Discover(AppSettings settings)
    {
        var found = new List<GameEntry>();
        void Source(string name, Func<IEnumerable<GameEntry>> read)
        {
            try { found.AddRange(read()); }
            catch (Exception e) { AppPaths.Log($"Library: reading {name} failed: {e.Message}"); }
        }

        Source("Steam", Steam);
        Source("Epic Games", Epic);
        Source("GOG", Gog);
        Source("Ubisoft", Ubisoft);
        Source("Xbox", Xbox);
        Source("extra folders", () => ExtraFolders(settings.ExtraFolders));
        Source("manual games", () => Manual(settings.ManualExes));

        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in settings.HiddenGames)
        {
            try { hidden.Add(Normalize(h)); } catch { }
        }

        var byDir = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var g in found)
        {
            string key;
            try
            {
                if (string.IsNullOrWhiteSpace(g.Dir) || !Directory.Exists(g.Dir)) continue;
                key = Normalize(g.Dir);
            }
            catch { continue; }
            if (hidden.Contains(key)) continue;
            if (byDir.TryGetValue(key, out var existing))
            {
                // A launcher entry beats a plain folder entry for the same game.
                if (existing.Launcher == "Folder" && g.Launcher != "Folder") byDir[key] = g;
                continue;
            }
            byDir[key] = g;
            order.Add(key);
        }
        return order.Select(k => byDir[k]).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string Normalize(string dir) => Path.GetFullPath(dir).TrimEnd('\\', '/').ToLowerInvariant();

    // ------------------------------------------------------------------ Steam

    public static string? SteamRoot()
    {
        try
        {
            using var cu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (cu?.GetValue("SteamPath") is string p && p.Length > 0)
            {
                var full = Path.GetFullPath(p.Replace('/', '\\'));
                if (Directory.Exists(full)) return full;
            }
        }
        catch { }
        try
        {
            using var lm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (lm?.GetValue("InstallPath") is string p && p.Length > 0 && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        catch { }
        return null;
    }

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    [GeneratedRegex("\"(appid|name|installdir)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AcfField();

    [GeneratedRegex(@"redistributable|steamworks common|directx|vcredist|proton|steam linux runtime|soundtrack|dedicated server", RegexOptions.IgnoreCase)]
    private static partial Regex SteamNoise();

    private static string VdfUnescape(string s) => s.Replace("\\\\", "\\");

    private static IEnumerable<GameEntry> Steam()
    {
        var root = SteamRoot();
        if (root is null) return Array.Empty<GameEntry>();

        var libraries = new List<string> { root };
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
            {
                var lib = VdfUnescape(m.Groups[1].Value);
                if (!libraries.Any(l => l.TrimEnd('\\').Equals(lib.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) libraries.Add(lib);
            }

        var cache = Path.Combine(root, "appcache", "librarycache");
        var result = new List<GameEntry>();
        foreach (var lib in libraries)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(apps, "appmanifest_*.acf").ToList(); }
            catch (Exception e) { AppPaths.Log($"Library: Steam library {lib}: {e.Message}"); continue; }
            foreach (var acf in manifests)
            {
                try
                {
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match m in AcfField().Matches(File.ReadAllText(acf)))
                        fields.TryAdd(m.Groups[1].Value, VdfUnescape(m.Groups[2].Value));
                    if (!fields.TryGetValue("appid", out var id) || !fields.TryGetValue("installdir", out var installDir)) continue;
                    var name = fields.GetValueOrDefault("name") ?? installDir;
                    if (id == "228980" || SteamNoise().IsMatch(name) || name.TrimEnd().EndsWith("SDK", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new GameEntry
                    {
                        Launcher = "Steam",
                        Id = id,
                        Name = name,
                        Dir = Path.Combine(apps, "common", installDir),
                        Poster = SteamPoster(cache, id),
                    });
                }
                catch (Exception e) { AppPaths.Log($"Library: {acf}: {e.Message}"); }
            }
        }
        return result;
    }

    private static string? SteamPoster(string cache, string appId)
    {
        try
        {
            var flat = Path.Combine(cache, $"{appId}_library_600x900.jpg");
            if (File.Exists(flat)) return flat;
            var folder = Path.Combine(cache, appId);
            if (!Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, "library_600x900*.jpg", SearchOption.AllDirectories).FirstOrDefault()
                   ?? Directory.EnumerateFiles(folder, "library_capsule*.jpg", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ Epic / GOG / Ubisoft / Xbox

    private static IEnumerable<GameEntry> Epic()
    {
        var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        var result = new List<GameEntry>();
        if (!Directory.Exists(dir)) return result;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var r = doc.RootElement;
                if (r.TryGetProperty("bIsApplication", out var isApp) && isApp.ValueKind == JsonValueKind.False) continue;
                string? Str(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var location = Str("InstallLocation");
                if (string.IsNullOrWhiteSpace(location)) continue;
                result.Add(new GameEntry
                {
                    Launcher = "Epic Games",
                    Id = Str("AppName"),
                    Name = Str("DisplayName") ?? Path.GetFileName(location.TrimEnd('\\', '/')),
                    Dir = location.Replace('/', '\\'),
                });
            }
            catch (Exception e) { AppPaths.Log($"Library: {item}: {e.Message}"); }
        }
        return result;
    }

    private static IEnumerable<GameEntry> Gog()
    {
        var result = new List<GameEntry>();
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games is null) return result;
        foreach (var id in games.GetSubKeyNames())
        {
            using var k = games.OpenSubKey(id);
            if (k?.GetValue("path") is not string path || path.Length == 0) continue;
            var name = k.GetValue("gameName") as string;
            result.Add(new GameEntry { Launcher = "GOG", Id = id, Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path.TrimEnd('\\')) : name, Dir = path });
        }
        return result;
    }

    private static IEnumerable<GameEntry> Ubisoft()
    {
        var result = new List<GameEntry>();
        using var installs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (installs is null) return result;
        foreach (var id in installs.GetSubKeyNames())
        {
            using var k = installs.OpenSubKey(id);
            if (k?.GetValue("InstallDir") is not string dir || dir.Length == 0) continue;
            dir = dir.Replace('/', '\\').TrimEnd('\\');
            result.Add(new GameEntry { Launcher = "Ubisoft", Id = id, Name = Path.GetFileName(dir), Dir = dir });
        }
        return result;
    }

    private static IEnumerable<GameEntry> Xbox()
    {
        var result = new List<GameEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                if (!Directory.Exists(root)) continue;
                foreach (var folder in Directory.EnumerateDirectories(root))
                {
                    var content = Path.Combine(folder, "Content");
                    var config = Path.Combine(content, "MicrosoftGame.config");
                    if (!File.Exists(config)) continue;
                    var name = Path.GetFileName(folder);
                    try
                    {
                        var visuals = XDocument.Load(config).Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                        var display = visuals?.Attribute("DefaultDisplayName")?.Value;
                        if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)) name = display;
                    }
                    catch (Exception e) { AppPaths.Log($"Library: {config}: {e.Message}"); }
                    result.Add(new GameEntry { Launcher = "Xbox", Name = name, Dir = content });
                }
            }
            catch (Exception e) { AppPaths.Log($"Library: Xbox games on {drive.Name}: {e.Message}"); }
        }
        return result;
    }

    // ------------------------------------------------------------------ user-provided

    private static IEnumerable<GameEntry> ExtraFolders(IEnumerable<string> folders)
    {
        var result = new List<GameEntry>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name.StartsWith('_') || name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) continue;
                if (HasExe(sub, 4)) result.Add(new GameEntry { Launcher = "Folder", Name = name, Dir = sub });
            }
        }
        return result;
    }

    /// <summary>True when an .exe exists in <paramref name="dir"/> or up to <paramref name="levels"/> folders below it.</summary>
    private static bool HasExe(string dir, int levels)
    {
        try
        {
            if (Directory.EnumerateFiles(dir, "*.exe").Any()) return true;
            if (levels <= 0) return false;
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (HasExe(sub, levels - 1)) return true;
        }
        catch { }
        return false;
    }

    private static IEnumerable<GameEntry> Manual(IEnumerable<string> exes)
    {
        var result = new List<GameEntry>();
        foreach (var exe in exes)
        {
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) continue;
            var full = Path.GetFullPath(exe);
            result.Add(new GameEntry
            {
                Launcher = "Manual",
                Name = Artwork.ExeProductName(full) ?? Path.GetFileNameWithoutExtension(full),
                Dir = Path.GetDirectoryName(full)!,
                Exe = full,
            });
        }
        return result;
    }
}
