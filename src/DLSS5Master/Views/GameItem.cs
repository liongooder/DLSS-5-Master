using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
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
            if (Scan is null) return "";
            if (Scan.Manifest is not null)
                return Installers.RouteLabel(Scan.Manifest.Route) + (Scan.Manifest.MfgUnlock is not null ? " + MFG" : "");
            if (Scan.OptiScalerHook is not null || (Scan.OtherAppRecordRoute?.StartsWith("OptiScaler") ?? false)) return "OptiScaler (other app)";
            if (Scan.ReShade.Installed || Scan.OtherAppBackup || Scan.OtherAppRecord) return "ReShade (other app)";
            return "";
        }
    }
    public Visibility BadgeVisibility => Badge == "" ? Visibility.Collapsed : Visibility.Visible;
    public bool IsInstalled => Scan?.Manifest is not null;

    public string Subtitle
    {
        get
        {
            if (Scan?.Primary is null) return Entry.Launcher;
            var parts = new List<string> { Scanner.Label(Scan.Primary.Api) };
            if (Scan.Dlss?.Version is { } v) parts.Add("DLSS " + v);
            return string.Join(" · ", parts);
        }
    }

    public void SetScan(GameScan? scan)
    {
        Scan = scan;
        Raise(nameof(Badge));
        Raise(nameof(BadgeVisibility));
        Raise(nameof(Subtitle));
        Raise(nameof(IsInstalled));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
