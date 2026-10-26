using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using DLSS5Master.Core;

namespace DLSS5Master.Views;

/// <summary>One poster tile in the library grid.</summary>
public sealed class GameItem : INotifyPropertyChanged
{
    public GameEntry Entry { get; }
    public GameScan? Scan { get; private set; }

    public GameItem(GameEntry entry) => Entry = entry;

    public string Name => Entry.Name;
    public string Launcher => Entry.Launcher;
    public string Initials => string.Concat(Entry.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3).Select(w => char.ToUpperInvariant(w[0])));

    private BitmapImage? _poster;
    public BitmapImage? Poster { get => _poster; set { _poster = value; Raise(); Raise(nameof(PlaceholderVisibility)); } }
    public Visibility PlaceholderVisibility => _poster is null ? Visibility.Visible : Visibility.Collapsed;

    public string Badge
    {
        get
        {
            // Kept short: it shares the top of the poster with the API badge. The game panel has the full wording.
            if (Scan is null) return "";
            if (Scan.Manifest is not null)
            {
                var route = Scan.Manifest.Route.ToLowerInvariant() switch
                {
                    "native" => "Native", "multipass" or "renodx" => "Multipass", "feeder" => "Feeder", "optiscaler" => "OptiScaler",
                    var other => other,
                };
                return route + (Scan.Manifest.MfgUnlock is not null ? " + MFG" : "");
            }
            if (IsModdedElsewhere) return "Other app";
            return "";
        }
    }
    public Visibility BadgeVisibility => Badge == "" ? Visibility.Collapsed : Visibility.Visible;
    public bool IsInstalled => Scan?.Manifest is not null;

    public RenderApi Api => Scan?.Primary?.Api ?? RenderApi.Unknown;

    /// <summary>Rendering API for the poster's corner badge; empty until the game is scanned.</summary>
    public string ApiText => Scan?.Primary is null ? "" : Scanner.Label(Api);
    public Visibility ApiVisibility => ApiText == "" ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>DirectX 12 is the API every DLSS 5 route supports, so it gets the accent colour.</summary>
    public Brush ApiBackground => Res(Api == RenderApi.DX12 ? "ApiStrongBackground" : "ApiBackground");
    public Brush ApiForeground => Res(Api == RenderApi.DX12 ? "ApiStrongText" : "ApiText");

    public bool HasDlss => Scan?.Dlss is not null;
    public bool HasFrameGen => Scan?.HasFrameGen ?? false;
    /// <summary>"DLSS 310.9.1" (three parts fit beside the FG dot on the poster), or "no DLSS".</summary>
    public string DlssText => Scan is null ? "" : Scan.Dlss is { } d ? "DLSS " + (d.Version is { } v ? string.Join('.', v.Split('.').Take(3)) : "?") : "no DLSS";
    public Brush DlssDot => Res(HasDlss ? "AccentText" : "MutedText");
    public Visibility FrameGenVisibility => HasFrameGen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FactsVisibility => Scan is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The DLSS 5 routes (Native, Multipass, Feeder) this game can use.</summary>
    public List<string> ReadyRoutes => Scan?.Primary is not { } exe ? new()
        : Installers.RoutesFor(exe.Api, exe.Bitness, Scan.HasNativeDlss, Scan.HasFrameGen)
            .Where(r => r.Route != Route.OptiScaler && r.Unavailable is null).Select(r => r.Route.ToString()).ToList();
    public bool IsReady => ReadyRoutes.Count > 0;

    public bool IsModdedElsewhere => Scan is { Manifest: null } s && (s.OtherAppBackup || s.OtherAppRecord || s.OptiScalerHook is not null || s.ReShade.Installed);

    public string Subtitle
    {
        get
        {
            if (Scan is null) return "Scanning…";
            if (Scan.Primary is null) return "No game .exe found";
            if (Scan.Manifest is not null) return "Set up " + Ago(Scan.Manifest.Date);
            var routes = ReadyRoutes;
            return routes.Count > 0 ? string.Join(" · ", routes) : "No DLSS 5 route";
        }
    }

    public static string Ago(DateTime when)
    {
        var d = DateTime.Now - when.ToLocalTime();
        if (d.TotalHours < 1) return "just now";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} d ago";
        return when.ToLocalTime().ToString("d MMM yyyy");
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public void SetScan(GameScan? scan)
    {
        Scan = scan;
        foreach (var p in new[] { nameof(Badge), nameof(BadgeVisibility), nameof(Subtitle), nameof(IsInstalled), nameof(ApiText), nameof(ApiVisibility),
                                  nameof(ApiBackground), nameof(ApiForeground), nameof(DlssText), nameof(DlssDot), nameof(FrameGenVisibility), nameof(FactsVisibility) })
            Raise(p);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The games of one store (Steam, Epic Games…) as a group in the library grid.</summary>
public sealed class GameGroup : List<GameItem>
{
    public GameGroup(string key, IEnumerable<GameItem> items) : base(items) => Key = key;
    public string Key { get; }
    public string CountText => Count.ToString();
    public string ReadyText => $"{this.Count(g => g.IsReady)} ready for DLSS 5";
}
