using System.Globalization;
using System.Text.Json;

namespace DLSS5Master.Core;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\DLSS5Master; tests point DLSS5MASTER_DATA at a temporary folder so they never touch the real one.</summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable("DLSS5MASTER_DATA") is { Length: > 0 } test
        ? test : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Master");
    public static string Components => Path.Combine(Root, "components");
    public static string DllLibrary => Path.Combine(Root, "dll-library");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LibraryCache => Path.Combine(Root, "library.json");
    public static string LogFile => Path.Combine(Root, "activity.log");

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Root);
            // Gregorian whatever the region: a Hijri calendar setting would otherwise date the log in another year.
            File.AppendAllText(LogFile, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {line}{Environment.NewLine}");
        }
        catch { }
    }
}

public sealed class GameOverride
{
    public string? Exe { get; set; }
    public string? Api { get; set; }
    /// <summary>Artwork the user picked for this game (an image file), or null for automatic artwork.</summary>
    public string? Poster { get; set; }
    /// <summary>Setups of this game have ReShade load the add-ons as soon as it starts (ReShade.ini LoadFromDllMain).</summary>
    public bool? LoadAddonsEarly { get; set; }
    /// <summary>
    /// The launcher of the game these choices belong to ("Steam", "Epic Games", "GOG", "Ubisoft" or "Xbox"), so they follow
    /// it to another folder (<see cref="MovedGames"/>); null for a game added by hand or found in the user's own folders.
    /// </summary>
    public string? Launcher { get; set; }
    /// <summary>That launcher's ID of the game (Steam app ID, Epic AppName, GOG or Ubisoft key); null for an Xbox game, found by its name.</summary>
    public string? Id { get; set; }
    /// <summary>The game's name as its launcher gives it.</summary>
    public string? Name { get; set; }

    /// <summary>Takes <paramref name="other"/>'s choices where this one has none: this one's own choices win.</summary>
    internal void MergeFrom(GameOverride other)
    {
        Exe ??= other.Exe;
        Api ??= other.Api;
        Poster ??= other.Poster;
        LoadAddonsEarly ??= other.LoadAddonsEarly;
        // The game they belong to goes as a whole: never one game's launcher with another one's ID.
        if (Launcher is null) (Launcher, Id, Name) = (other.Launcher, other.Id, other.Name);
    }
}

public sealed class AppSettings
{
    public List<string> ExtraFolders { get; set; } = new();
    public List<string> ManualExes { get; set; } = new();
    public List<string> HiddenGames { get; set; } = new();
    /// <summary>Folder with nvngx_dlss*.dll / sl.*.dll. Empty = the copies bundled with the app.</summary>
    public string RuntimeFolder { get; set; } = "";
    /// <summary>Folder or ReShade_Setup_*_Addon.exe providing ReShade32/64.dll. Empty = auto-detect.</summary>
    public string ReShadeSource { get; set; } = "";
    public string OptiScalerMenuKey { get; set; } = "0x2D";
    public bool ShowCommunity { get; set; } = true;
    /// <summary>Overlay: neural style 0 Default, 1 Natural, 2 Cinematic.</summary>
    public int OverlayStyle { get; set; }
    /// <summary>Overlay: neural strength in percent (0 leaves the picture untouched).</summary>
    public int OverlayStrength { get; set; } = 100;
    /// <summary>Overlay: neural passes per frame, 1 or 2.</summary>
    public int OverlayPasses { get; set; } = 1;
    /// <summary>Overlay: in-between frames from DLSS Frame Generation per real frame (0 off, 1 = 2x … 3 = 4x).</summary>
    public int OverlayFrameGen { get; set; }
    /// <summary>Overlay: processing size, 0 Quality (100 % of the window), 1 Balanced (67 %), 2 Performance (50 %).</summary>
    public int OverlayQuality { get; set; }
    public Dictionary<string, GameOverride> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
                s.Overrides = IgnoringCase(s.Overrides);
                return s;
            }
        }
        catch { }
        return new();
    }

    /// <summary>
    /// The choices read from the file in a dictionary that ignores case, like a new one. Reading makes a dictionary of its own
    /// that does not, and after a restart "D:\Games\X" would no longer find what was saved under "d:\games\x". Keys that differ
    /// only in case are merged.
    /// </summary>
    private static Dictionary<string, GameOverride> IgnoringCase(Dictionary<string, GameOverride>? read)
    {
        var result = new Dictionary<string, GameOverride>(StringComparer.OrdinalIgnoreCase);
        if (read is null) return result;
        foreach (var (dir, o) in read)
        {
            if (o is null) continue;
            if (result.TryGetValue(dir, out var into)) into.MergeFrom(o);
            else result[dir] = o;
        }
        return result;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, AppPaths.SettingsFile, true);
    }

    /// <summary>
    /// The choices saved on a game's page, or null. Also found under another spelling of its folder (a trailing \ or /,
    /// forward slashes), as launchers report folders differently.
    /// </summary>
    public GameOverride? OverrideOf(GameEntry game)
    {
        if (Overrides.TryGetValue(game.Dir, out var o)) return o;
        if (MovedGames.FolderKey(game.Dir) is not { } key) return null;
        foreach (var (dir, other) in Overrides)
            if (string.Equals(MovedGames.FolderKey(dir), key, StringComparison.OrdinalIgnoreCase)) return other;
        return null;
    }

    /// <summary>The choices saved on a game's page, made when there are none yet, marked with the game they belong to.</summary>
    public GameOverride OverrideFor(GameEntry game)
    {
        var o = OverrideOf(game);
        if (o is null) Overrides[game.Dir] = o = new GameOverride();
        MovedGames.Stamp(o, game);
        return o;
    }
}
