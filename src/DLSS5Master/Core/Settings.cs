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
    /// <summary>Add-ons players bought themselves and keep in DLSS 5 Master's folder; never shipped, and uninstalling keeps them.</summary>
    public static string YourAddons => Path.Combine(Root, "your-addons");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LibraryCache => Path.Combine(Root, "library.json");
    public static string LogFile => Path.Combine(Root, "activity.log");

    private static readonly object LogGate = new();

    /// <summary>
    /// Appends a line to the activity log. One writer at a time: a setup logs from a worker thread while the window logs
    /// too, and a second append while the file is open fails (the file is shared for reading only). A reader holding the
    /// file for a moment (Settings shows the log) is waited out with a few short retries.
    /// </summary>
    public static void Log(string line)
    {
        // Gregorian whatever the region: a Hijri calendar setting would otherwise date the log in another year.
        var text = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {line}{Environment.NewLine}";
        lock (LogGate)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Root);
                    File.AppendAllText(LogFile, text);
                    return;
                }
                catch (IOException) when (attempt < 3) { Thread.Sleep(15); }
                catch { return; }
            }
        }
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
    public FeederBackend? FeederBackend { get; set; }
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
        FeederBackend ??= other.FeederBackend;
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
    public NeuralModelChoice NeuralModel { get; set; } = NeuralModelChoice.Automatic;
    /// <summary>Folder or ReShade_Setup_*_Addon.exe providing ReShade32/64.dll. Empty = auto-detect.</summary>
    public string ReShadeSource { get; set; } = "";
    /// <summary>The player's copy of DLSS5 NR (the unzipped add-on folder). Empty = only games that already have it offer its route.</summary>
    public string NrFolder { get; set; } = "";
    /// <summary>A locally imported Feeder release. Empty = use the normal update and included component selection.</summary>
    public string FeederFolder { get; set; } = "";
    public bool FeederLiveControls { get; set; } = true;
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
                using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
                var root = document.RootElement;
                var s = root.Deserialize<AppSettings>() ?? new();
                if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty(nameof(NrFolder), out _)
                    && root.TryGetProperty("LecramNrFolder", out var previousFolder)
                    && previousFolder.ValueKind == JsonValueKind.String)
                    s.NrFolder = previousFolder.GetString() ?? "";
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

    internal static FeederBackend FeederBackendFor(GameOverride? choices, Manifest? installed) =>
        choices?.FeederBackend switch
        {
            FeederBackend.Lecram => FeederBackend.Lecram,
            FeederBackend.RenoDx => FeederBackend.RenoDx,
            _ => installed is { Route: "feeder", FeederBackend: FeederBackend.Lecram }
                ? FeederBackend.Lecram : FeederBackend.RenoDx,
        };

    internal bool TryRememberInstalledFeederBackend(GameEntry game, Manifest? installed)
    {
        if (OverrideOf(game)?.FeederBackend is FeederBackend.RenoDx or FeederBackend.Lecram ||
            installed is not { Route: "feeder", FeederBackend: FeederBackend.Lecram }) return false;
        OverrideFor(game).FeederBackend = FeederBackend.Lecram;
        return true;
    }
}
