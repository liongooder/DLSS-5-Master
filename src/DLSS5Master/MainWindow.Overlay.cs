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
        // The overlay belongs to this session: closing the app stops it.
        Closed += (_, _) => { try { _ = Overlay.StopAsync(); } catch { } };
        RefreshOverlayUi();
    }

    /// <summary>Style, strength, passes and motion; every change is saved and used at the next start.</summary>
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

        var motion = new CheckBox { Content = "Motion", IsChecked = _settings.OverlayMotion, VerticalAlignment = VerticalAlignment.Bottom };
        ToolTipService.SetToolTip(motion, "Measure how the picture moves with the graphics card's optical-flow engine, so fast camera turns do not smear.");
        motion.Click += (_, _) => { _settings.OverlayMotion = motion.IsChecked == true; _settings.Save(); };

        // Two rows, so it fits the narrow window-picker dialog as well as the game panel.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        row.Children.Add(style);
        row.Children.Add(passes);
        row.Children.Add(motion);
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
            OverlayBar.Message = $"Running on “{target.Title}”. F9 switches DLSS 5 on and off while you play, F8 motion.";

        if (_game is null) return;
        bool mine = target is not null && IsThisGame(target);
        LiveOverlayButton.Content = mine ? "Stop overlay" : "Start overlay";
        LiveOverlayButton.IsEnabled = !_overlayBusy;
        LiveOverlayStatus.Text = mine ? "Running. F9 switches DLSS 5 on and off, F8 motion."
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

    private async Task PickWindowAndStartAsync(string intro)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320 };
        void Fill()
        {
            list.Items.Clear();
            foreach (var w in Overlay.Windows())
            {
                var row = new StackPanel { Padding = new Thickness(0, 4, 0, 4), Tag = w };
                row.Children.Add(new TextBlock { Text = w.Title, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(new TextBlock { Text = w.Process, FontSize = 12, Foreground = Res("MutedText") });
                list.Items.Add(row);
            }
        }
        Fill();
        var refresh = new HyperlinkButton { Content = "Refresh the list", Padding = new Thickness(0) };
        refresh.Click += (_, _) => Fill();

        var body = new StackPanel { Spacing = 12 };   // no MinWidth: a dialog is at most 548 wide and clips anything wider
        body.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(list);
        body.Children.Add(refresh);
        body.Children.Add(BuildOverlaySettings());
        body.Children.Add(new TextBlock
        {
            Text = "Exclusive fullscreen cannot be captured: use windowed or borderless. F9 switches DLSS 5 on and off while you play, F8 motion, Ctrl+F9 stops the overlay.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res("MutedText"),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "DLSS 5 on any window",
            Content = new ScrollViewer { Content = body },
            PrimaryButtonText = "Start DLSS 5",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if ((list.SelectedItem as FrameworkElement)?.Tag is OverlayWindow chosen) await StartOverlayAsync(chosen);
    }
}
