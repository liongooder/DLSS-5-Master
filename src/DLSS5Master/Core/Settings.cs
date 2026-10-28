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
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, AppPaths.SettingsFile, true);
    }

    public GameOverride OverrideFor(string gameDir)
    {
        if (!Overrides.TryGetValue(gameDir, out var o)) Overrides[gameDir] = o = new GameOverride();
        return o;
    }
}
