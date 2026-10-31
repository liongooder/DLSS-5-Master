using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using DLSS5Master.Core;

namespace DLSS5Master;

/// <summary>Game panel → Community: what other players report for this game, and this install's own report.</summary>
public sealed partial class MainWindow
{
    private int _communityLoad;   // ignores answers for a game the user already left
    private CommunitySummary? _community;

    private static readonly Dictionary<string, (string Icon, string Label)> Ratings = new()
    {
        ["works"] = ("✅", "Works"),
        ["partly"] = ("🟡", "Partly"),
        ["broken"] = ("❌", "Broken"),
    };

    private void CommunityBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowCommunity = CommunityBox.IsChecked == true;
        _settings.Save();
    }

    private async void CommunityRefresh_Click(object sender, RoutedEventArgs e) => await LoadCommunityAsync();

    private async Task LoadCommunityAsync()
    {
        CommunitySection.Visibility = _settings.ShowCommunity ? Visibility.Visible : Visibility.Collapsed;
        if (!_settings.ShowCommunity || _game is null) return;
        var game = _game;
        int ticket = ++_communityLoad;
        CommunityPanel.Children.Clear();
        CommunityPanel.Children.Add(Muted("Loading what other players report…"));
        try
        {
            var summary = await Community.GetAsync(Community.GameKey(game.Entry));
            if (ticket != _communityLoad || _game != game) return;
            _community = summary;
            RenderCommunity(summary);
        }
        catch (Exception ex)
        {
            if (ticket != _communityLoad) return;
            CommunityPanel.Children.Clear();
            CommunityPanel.Children.Add(Muted("Community reports are not available right now (" + ex.Message + ")."));
        }
    }

    /// <summary>Fills <paramref name="target"/> (the game panel's section by default) with a game's community summary.</summary>
    private void RenderCommunity(CommunitySummary s, Panel? target = null, bool editor = true)
    {
        target ??= CommunityPanel;
        target.Children.Clear();
        var family = Community.GpuFamily(Community.LocalGpu);

        // Overall counts.
        if (s.Counts.Total == 0)
            target.Children.Add(Muted("No reports for this game yet. Install a route and be the first to say how it went."));
        else
        {
            target.Children.Add(new TextBlock
            {
                FontSize = 16, FontWeight = FontWeights.SemiBold,
                Text = $"✅ {s.Counts.Works} work   ·   🟡 {s.Counts.Partly} partly   ·   ❌ {s.Counts.Broken} broken",
            });
            // Best combination, preferring the user's GPU family.
            var best = s.Combos.Where(c => c.Works > 0)
                .OrderByDescending(c => c.GpuFamily == family)
                .ThenByDescending(c => c.Works - c.Broken)
                .FirstOrDefault();
            if (best is not null)
                target.Children.Add(new TextBlock
                {
                    Foreground = Res("AccentText"), TextWrapping = TextWrapping.Wrap,
                    Text = $"Most reported to work: {RouteName(best.Route)}{(best.Mfg ? " + MFG" : "")} on {best.GpuFamily} ({best.Works} of {best.Works + best.Partly + best.Broken})",
                });
            var onMine = s.Combos.Where(c => c.GpuFamily == family).ToList();
            if (onMine.Count > 0 && family != "Other")
                target.Children.Add(Muted($"On {family} like yours: ✅ {onMine.Sum(c => c.Works)}  🟡 {onMine.Sum(c => c.Partly)}  ❌ {onMine.Sum(c => c.Broken)}"));
        }

        if (editor) target.Children.Add(MyReportEditor(s));

        // Every report with the PC it ran on, newest first (a server before 0.9.8 sends only the ones with a note).
        if (s.Reports.Count > 0)
        {
            var title = !s.AllReports ? "WHAT PLAYERS SAY"
                : s.Counts.Total > s.Reports.Count ? $"REPORTS · NEWEST {s.Reports.Count} OF {s.Counts.Total}"
                : $"REPORTS ({s.Reports.Count})";
            target.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitle"], Margin = new Thickness(0, 6, 0, 0) });
            foreach (var r in s.Reports) target.Children.Add(ReportRow(r, s.Mine?.Id == r.Id));
        }
    }

    private FrameworkElement MyReportEditor(CommunitySummary s)
    {
        var box = new StackPanel { Spacing = 8, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(12), Background = Res("RowBackground") };
        var manifest = _scan?.Manifest;
        box.Children.Add(new TextBlock { Text = s.Mine is null ? "How did it go for you?" : "Your report", FontWeight = FontWeights.SemiBold });
        if (manifest is null)
        {
            box.Children.Add(Muted("Install a route with DLSS 5 Master first; your report is about that install."));
            return box;
        }

        string? rating = s.Mine?.Rating;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var note = new TextBox
        {
            PlaceholderText = "Optional: what worked, settings, problems… (shown to everyone)",
            MaxLength = Community.NoteMax, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, Text = s.Mine?.Note ?? "",
        };
        var send = new Button { Content = s.Mine is null ? "Send" : "Update my report", Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = rating is not null };
        var status = new TextBlock { FontSize = 12, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap };

        foreach (var (key, (icon, label)) in Ratings)
        {
            var b = new ToggleButton { Content = $"{icon}  {label}", IsChecked = rating == key, Tag = key };
            b.Click += (_, _) =>
            {
                rating = key;
                foreach (var other in buttons.Children.OfType<ToggleButton>()) other.IsChecked = (string)other.Tag == key;
                send.IsEnabled = true;
            };
            buttons.Children.Add(b);
        }

        var route = manifest.Route;
        bool mfg = manifest.MfgUnlock is not null;
        var pc = Community.LocalSpecs;
        box.Children.Add(Muted($"Sent with your report: {RouteName(route)}{(mfg ? " + MFG " + manifest.MfgUnlock : "")} · {(pc.Length > 0 ? pc : "this PC")}."));
        box.Children.Add(buttons);
        box.Children.Add(note);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(send);
        row.Children.Add(status);
        box.Children.Add(row);
        if (s.Mine is { Hidden: true })
            box.Children.Add(new TextBlock { Text = "Your note is hidden after other players flagged it. Change it to show it again.", Foreground = Res("WarnText"), FontSize = 12, TextWrapping = TextWrapping.Wrap });

        send.Click += async (_, _) =>
        {
            if (rating is null || _game is null) return;
            send.IsEnabled = false;
            status.Text = "Sending…";
            try
            {
                await Community.SendAsync(new Community.Report(Community.GameKey(_game.Entry), _game.Name, rating, route, mfg, note.Text, ReportVersions(manifest), SelectedApi));
                status.Text = "Thanks! Your report is shared.";
                await LoadCommunityAsync();
            }
            catch (Exception ex)
            {
                status.Text = "Not sent: " + ex.Message;
                send.IsEnabled = true;
            }
        };
        return box;
    }

    /// <summary>
    /// One player's report: the note (if any), then the route, API, who and when, then the PC it ran on and, smaller, the
    /// app and add-on versions. A report sent before 0.9.8 knows only the graphics card.
    /// </summary>
    private FrameworkElement ReportRow(CommunityReport r, bool mine)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 8, 0, 8), BorderBrush = Res("RowBorder"), BorderThickness = new Thickness(0, 1, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock { Text = Ratings.GetValueOrDefault(r.Rating).Icon ?? "•", FontSize = 18 };
        ToolTipService.SetToolTip(icon, Ratings.GetValueOrDefault(r.Rating).Label);
        grid.Children.Add(icon);

        var text = new StackPanel { Spacing = 2 };
        // Plain text only: notes are shown exactly as typed, never interpreted.
        if (r.Hidden)
            text.Children.Add(new TextBlock { Text = "Hidden by moderation", FontStyle = Windows.UI.Text.FontStyle.Italic, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap });
        else if (r.Note.Length > 0)
            text.Children.Add(new TextBlock { Text = r.Note, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

        var who = r.Nickname is { Length: > 0 } nick ? nick + (r.IsDev ? " (developer)" : "") : "";
        text.Children.Add(new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = string.Join(" · ", new[]
            {
                RouteName(r.Route) + (r.Mfg ? " + MFG" : ""), r.Api.Length > 0 ? Community.ApiLabel(r.Api) : "", who,
                r.When.ToString("d MMM yyyy", CultureInfo.InvariantCulture), mine ? "your report" : "",
            }.Where(p => p.Length > 0)),
        });
        var card = r.Gpu.Length > 0 ? r.Gpu : r.GpuFamily is { Length: > 0 } family && family != "Other" ? family : "";   // a hidden report keeps only the card's family
        var pc = Community.SpecsLine(card, r.Driver, r.Cpu, r.RamGb, r.Os);
        if (pc.Length > 0)
            text.Children.Add(new TextBlock { Text = pc, FontSize = 11, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (Community.VersionsLine(r.AppVersion, r.Versions) is { Length: > 0 } versions)
            text.Children.Add(new TextBlock { Text = versions, FontSize = 11, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (!mine && !r.Hidden)
        {
            var flag = new HyperlinkButton { Content = "Flag", FontSize = 11, Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top };
            ToolTipService.SetToolTip(flag, "Report this as spam, fake or abusive (needs a community nickname). The developer sees flagged reports; several flags hide a report's text.");
            flag.Click += async (_, _) =>
            {
                flag.IsEnabled = false;
                try { await Community.FlagAsync(r.Id); flag.Content = "Flagged"; }
                catch (Exception ex)
                {
                    // The server's reason, such as "Choose a nickname first.", says more than "Failed" when it is short.
                    flag.Content = ex.Message.Length <= 40 ? ex.Message : "Failed";
                    ToolTipService.SetToolTip(flag, ex.Message);
                }
            };
            Grid.SetColumn(flag, 2);
            grid.Children.Add(flag);
        }
        return grid;
    }

    /// <summary>The add-on versions this install used, so reports can be compared across versions.</summary>
    private static Dictionary<string, string> ReportVersions(Manifest m)
    {
        var v = new Dictionary<string, string>();
        void Add(string id) { var src = Updates.Sources.FirstOrDefault(s => s.Id == id); if (src is not null) v[id] = Updates.InUse(src).Version; }
        switch (m.Route)
        {
            case "native": Add("renodx-dlss5"); break;
            case "multipass": Add("renodx-dlss"); break;
            case "feeder": Add("feeder"); Add("renodx-dlss5"); break;
        }
        if (m.OptiScalerBuild is { } opti) v["optiscaler"] = opti;
        if (m.MfgUnlock is { } mfg) v["mfgunlock"] = mfg;
        return v;
    }

    private static string RouteName(string route) => route switch
    {
        "native" => "Native",
        "multipass" => "Multipass",
        "feeder" => "Feeder",
        "optiscaler" => "OptiScaler",
        _ => route,
    };

    private static TextBlock Muted(string text) => new() { Text = text, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap };
}
