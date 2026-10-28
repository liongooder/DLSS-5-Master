using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DLSS5Master.Core;

namespace DLSS5Master;

/// <summary>The DLSS 5 overlay: Games page → "DLSS 5 on any window", and the overlay section of each game panel.</summary>
public sealed partial class MainWindow
{
    private bool _overlayBusy;

    private void InitOverlay()
    {
        Overlay.Changed += () => DispatcherQueue.TryEnqueue(RefreshOverlayUi);
        // While it runs, the overlay adapts frame generation to the game; keep the bar's note current.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(5);
        timer.Tick += (_, _) => { if (Overlay.Running) RefreshOverlayUi(); };
        timer.Start();
        // The overlay belongs to this session: closing the app stops it.
        Closed += (_, _) => { try { _ = Overlay.StopAsync(); } catch { } };
        RefreshOverlayUi();
    }

    /// <summary>Style, passes, frame generation and strength; every change is saved and used at the next start. Motion tracking
    /// always runs: it only helps, and a switch for it confused more than it offered.</summary>
    private StackPanel BuildOverlaySettings()
    {
        var style = new ComboBox { Header = "Style", MinWidth = 190 };
        foreach (var s in new[] { "Default", "Natural", "Cinematic" }) style.Items.Add(s);
        style.SelectedIndex = Math.Clamp(_settings.OverlayStyle, 0, 2);
        ToolTipService.SetToolTip(style, "Which of the neural model's three looks to ask for. Natural and Cinematic can darken very bright highlights.");
        style.SelectionChanged += (_, _) => { _settings.OverlayStyle = style.SelectedIndex; _settings.Save(); };

        var passes = new ComboBox { Header = "Passes", MinWidth = 120 };
        passes.Items.Add("1");
        passes.Items.Add("2");
        passes.SelectedIndex = Math.Clamp(_settings.OverlayPasses, 1, 2) - 1;
        ToolTipService.SetToolTip(passes, "Run the neural pass twice on every frame for a stronger result. Each pass costs about as much GPU time as the first.");
        passes.SelectionChanged += (_, _) => { _settings.OverlayPasses = passes.SelectedIndex + 1; _settings.Save(); };

        var strength = new Slider { Header = $"Strength {_settings.OverlayStrength} %", Minimum = 0, Maximum = 100, StepFrequency = 5, HorizontalAlignment = HorizontalAlignment.Left, Width = 380, Value = _settings.OverlayStrength };
        strength.ValueChanged += (_, e) =>
        {
            _settings.OverlayStrength = (int)e.NewValue;
            strength.Header = $"Strength {_settings.OverlayStrength} %";
            _settings.Save();
        };

        var framegen = new ComboBox { Header = "Frame generation", MinWidth = 150 };
        foreach (var s in new[] { "Off", "2x", "3x", "4x" }) framegen.Items.Add(s);
        framegen.SelectedIndex = Math.Clamp(_settings.OverlayFrameGen, 0, 3);
        ToolTipService.SetToolTip(framegen, "DLSS Frame Generation adds in-between frames for smoother motion. RTX 50: up to 4x. RTX 40: 2x, and 3x/4x " +
            "through the bundled MFG unlock. RTX 20/30: up to 4x through dlssg_for_sm86 (needs NVIDIA driver R580 or newer). " +
            "Best on a high-refresh screen: 2x of a 60 fps game needs 120 Hz.");
        framegen.SelectionChanged += (_, _) => { _settings.OverlayFrameGen = framegen.SelectedIndex; _settings.Save(); };

        // Two rows, so it fits the narrow window-picker dialog as well as the game panel.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        row.Children.Add(style);
        row.Children.Add(passes);
        row.Children.Add(framegen);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(row);
        panel.Children.Add(strength);
        return panel;
    }

    private void RefreshOverlayUi()
    {
        var target = Overlay.Running ? Overlay.Target : null;
        OverlayBar.IsOpen = target is not null;
        if (target is not null)
            OverlayBar.Message = $"Running on “{target.Title}”. F9 switches DLSS 5 on and off while you play." +
                                 (Overlay.FrameGenNotice() is { } notice ? " " + notice : "");

        if (_game is null) return;
        bool mine = target is not null && IsThisGame(target);
        LiveOverlayButton.Content = mine ? "Stop overlay" : "Start overlay";
        LiveOverlayButton.IsEnabled = !_overlayBusy;
        LiveOverlayStatus.Text = mine ? "Running. F9 switches DLSS 5 on and off."
            : target is not null ? $"The overlay runs on “{target.Title}”; starting it here moves it to this game." : "";
    }

    private bool IsThisGame(OverlayWindow w) =>
        _game is not null && w.ExePath is not null &&
        w.ExePath.StartsWith(_game.Entry.Dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private async Task StartOverlayAsync(OverlayWindow window)
    {
        if (_overlayBusy) return;
        _overlayBusy = true;
        RefreshOverlayUi();
        var progress = new Progress<string>(t => { if (_game is not null) LiveOverlayStatus.Text = t; });
        try
        {
            await Overlay.StartAsync(_components, _settings, window, progress, CancellationToken.None);
            // The helper decides about frame generation once the first frame is in; show what it said.
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => DispatcherQueue.TryEnqueue(RefreshOverlayUi));
        }
        catch (Exception ex)
        {
            AppPaths.Log("Overlay did not start: " + ex.Message);
            await new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = "The overlay did not start",
                Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
            }.ShowAsync();
        }
        finally
        {
            _overlayBusy = false;
            RefreshOverlayUi();
        }
    }

    private async void OverlayStop_Click(object sender, RoutedEventArgs e)
    {
        await Overlay.StopAsync();
        RefreshOverlayUi();
    }

    /// <summary>Game panel: start the overlay on this game's window, or stop it.</summary>
    private async void LiveOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || _overlayBusy) return;
        if (Overlay.Running && Overlay.Target is { } t && IsThisGame(t)) { await Overlay.StopAsync(); RefreshOverlayUi(); return; }
        var exe = _settings.Overrides.GetValueOrDefault(_game.Entry.Dir)?.Exe ?? _game.Entry.Exe;
        var window = exe is not null ? Overlay.WindowOfExe(Path.Combine(_game.Entry.Dir, exe)) : null;
        window ??= Overlay.Windows().FirstOrDefault(IsThisGame);
        if (window is null)
        {
            // Not running, or its window belongs to a program outside the game folder: let the user pick it.
            await PickWindowAndStartAsync($"No window of {_game.Name} was found. Start the game in windowed or borderless mode, or pick its window here.");
            return;
        }
        await StartOverlayAsync(window);
    }

    private async void AnyWindow_Click(object sender, RoutedEventArgs e) =>
        await PickWindowAndStartAsync("Pick a window: a game, a video or anything else. DLSS 5 is drawn over it, and nothing in that program is changed.");

    // An icon for the kind of program behind a window, so the list reads at a glance.
    private string WindowGlyph(OverlayWindow w) => w.Process.ToLowerInvariant() switch
    {
        "chrome" or "msedge" or "firefox" or "opera" or "brave" or "vivaldi" => "",   // globe
        "explorer" => "",                                                              // folder
        "vlc" or "mpc-hc64" or "mpc-be64" or "potplayermini64" or "wmplayer" or "video.ui" => "",   // video
        _ when w.ExePath is not null && _all.Any(g => w.ExePath.StartsWith(g.Entry.Dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) => "",   // a game in the library
        _ => "",                                                                       // app window
    };

    private TextBlock DialogSection(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["SectionTitle"] };

    private async Task PickWindowAndStartAsync(string intro)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 236, Padding = new Thickness(0) };
        var empty = new TextBlock { Text = "No windows to show. Open the game or video first, then refresh.", Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap };
        void Fill()
        {
            list.Items.Clear();
            foreach (var w in Overlay.Windows())
            {
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 6, 10, 6), Tag = w };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var badge = new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Background = Res("RowBackground"),
                    Child = new FontIcon { Glyph = WindowGlyph(w), FontSize = 15, Foreground = Res("AccentText") },
                };
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = w.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = w.Process, FontSize = 11, Foreground = Res("MutedText") });
                Grid.SetColumn(text, 1);
                row.Children.Add(badge);
                row.Children.Add(text);
                list.Items.Add(new ListViewItem
                {
                    Content = row, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 4), MinHeight = 0, CornerRadius = new CornerRadius(9),
                    Background = Res("RowBackground"), BorderBrush = Res("RowBorder"), BorderThickness = new Thickness(1),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                });
            }
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Fill();

        var pickHeader = new Grid();
        pickHeader.Children.Add(DialogSection("1 · PICK A WINDOW"));
        var refresh = new HyperlinkButton { HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(4, 0, 4, 0) };
        var refreshContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        refreshContent.Children.Add(new FontIcon { Glyph = "", FontSize = 12 });
        refreshContent.Children.Add(new TextBlock { Text = "Refresh" });
        refresh.Content = refreshContent;
        refresh.Click += (_, _) => Fill();
        pickHeader.Children.Add(refresh);

        var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        hint.Children.Add(new FontIcon { Glyph = "", FontSize = 15, Foreground = Res("MutedText"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        hint.Children.Add(new TextBlock
        {
            Text = "F9 switches DLSS 5 on and off while you play, Ctrl+F9 stops it. Exclusive fullscreen cannot be captured: use windowed or borderless.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res("MutedText"), MaxWidth = 640,
        });

        var body = new StackPanel { Spacing = 14, Width = 680 };
        body.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedText") });
        body.Children.Add(pickHeader);
        body.Children.Add(list);
        body.Children.Add(empty);
        body.Children.Add(DialogSection("2 · LOOK"));
        body.Children.Add(BuildOverlaySettings());
        body.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "DLSS 5 on any window",
            Content = new ScrollViewer { Content = body, Padding = new Thickness(0, 0, 12, 0) },
            PrimaryButtonText = "Start DLSS 5",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsPrimaryButtonEnabled = false,
        };
        // Wider than the 548 px a dialog allows by default, so the window titles fit.
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;
        // The app's own artwork behind it, like every page, instead of the plain grey dialog.
        dialog.Background = new Microsoft.UI.Xaml.Media.ImageBrush
        {
            ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Background.jpg"))) { DecodePixelWidth = 1920 },
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
        };
        dialog.Resources["ContentDialogTopOverlay"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        dialog.BorderBrush = Res("RowBorder");
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (((list.SelectedItem as ListViewItem)?.Content as FrameworkElement)?.Tag is OverlayWindow chosen) await StartOverlayAsync(chosen);
    }
}
