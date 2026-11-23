using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using DLSS5Master.Core;
using DLSS5Master.Views;

namespace DLSS5Master;

/// <summary>
/// Games page → "Set up several games": one route in many games at once. Every game the route fits is ticked; the games it
/// does not fit, and every game with anti-cheat, are left as they are and listed with the reason. The ticked games are then
/// set up one after another, each the way its own page would set it up (<see cref="BulkSetup"/>).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Every game of the library is scanned (cleared while the library is read again).</summary>
    private bool _libraryScanned;
    /// <summary>"Set up several games" is reading the games or setting them up.</summary>
    private bool _bulkRunning;

    private enum BulkOutcome { Done, Failed, Left, Stopped }

    private static string GameCount(int n) => n == 1 ? "1 game" : $"{n} games";

    private static string BulkRouteLine(Route r) => r switch
    {
        Route.Native => "ReShade with the game's own DLSS: 64-bit DirectX 11 and 12 games that ship DLSS.",
        Route.Multipass => "ReShade with the RenoDX DLSS Tool: 64-bit DirectX 9, 11 and 12 games.",
        Route.Feeder => "ReShade with estimated motion for games without DLSS. Supports DirectX 11, DirectX 12, OpenGL and Vulkan with a registered ReShade layer.",
        Route.Uplift => "Neural rendering through ReShade. Supports DirectX 9–12, OpenGL and Vulkan with a registered ReShade layer.",
        Route.OptiScaler => "OptiScaler on its own, without ReShade: 64-bit DirectX 11, 12 and Vulkan games.",
        Route.MfgOnly => "MFG unlock with ReShade only: 64-bit DirectX 12 games with DLSS Frame Generation. No NR route is added.",
        _ => "Locally supplied add-on with ReShade. Supports Direct3D 9 to 12, OpenGL and 64-bit Vulkan with a registered ReShade layer.",
    };

    /// <summary>The drive a folder is on ("D:"), for "the drive is full".</summary>
    private static string DriveOf(string dir)
    {
        try { return (Path.GetPathRoot(Path.GetFullPath(dir)) ?? dir).TrimEnd('\\'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return dir; }
    }

    /// <summary>Whether the scan's primary .exe is the one chosen on the game's page (an Unreal launcher stub stands for the game it starts).</summary>
    private static bool SameExe(string chosen, string? primary)
    {
        if (primary is null) return false;
        try
        {
            var target = Scanner.UnrealGameFor(chosen) ?? chosen;
            return string.Equals(Path.GetFullPath(target), Path.GetFullPath(primary), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { return false; }
    }

    private async void BulkSetup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _bulkRunning || _appUpdating || !_libraryScanned || _visible.Count == 0) return;
        var library = _scanCts;   // a new read of the library replaces it
        _bulkRunning = true;
        RefreshLibrarySummary();
        ScanStatus.Text = "Reading the games…";
        var items = new List<GameItem>();
        var games = new List<BulkGame>();
        BulkContext? context = null;
        try
        {
            // The games the page shows; a filter changed while they were read: read the ones it shows now.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var shown = _visible.ToList();
                var choices = shown.Select(i => _settings.OverrideOf(i.Entry)).ToList();
                (games, context) = await Task.Run(() =>
                {
                    var c = BulkContext.Read(_settings, _components);
                    var read = new List<BulkGame>(shown.Count);
                    for (int n = 0; n < shown.Count; n++)
                    {
                        var item = shown[n];
                        var scan = item.Scan;
                        // Its setup uses the .exe chosen on its page; the library's scan may be of another one.
                        if (scan is not null && (choices[n]?.Exe ?? item.Entry.Exe) is { } preferred && !SameExe(preferred, scan.Primary?.Path))
                        {
                            try { scan = Scanner.Scan(item.Entry.Dir, preferred); }
                            catch (Exception ex) { AppPaths.Log($"[{item.Name}] Set up several games: could not scan it: {ex.Message}"); }
                        }
                        read.Add(BulkSetup.Read(item.Entry, scan, choices[n]));
                    }
                    return (read, c);
                });
                items = shown;
                if (_visible.SequenceEqual(shown)) break;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Set up several games: could not read the games: " + ex);
            return;
        }
        finally
        {
            _bulkRunning = false;
            ScanStatus.Text = "";
            RefreshLibrarySummary();
        }
        // A game's page opened, the library was read again, the app began updating itself, or the games shown kept changing
        // while the games were read: that wins.
        if (context is null || _busy || _game is not null || _appUpdating || !ReferenceEquals(library, _scanCts) || !_visible.SequenceEqual(items)) return;
        try { await ShowBulkSetupAsync(items, games, context, library); }
        catch (Exception ex) { AppPaths.Log("Set up several games: " + ex); }
    }

    private async Task ShowBulkSetupAsync(List<GameItem> items, List<BulkGame> games, BulkContext context, CancellationTokenSource? library)
    {
        var itemOf = new Dictionary<BulkGame, GameItem>();
        for (int n = 0; n < games.Count; n++) itemOf[games[n]] = items[n];
        var labels = Installers.RoutesFor(RenderApi.DX12, 64, hasNativeDlss: true, hasFrameGen: true, nrCopy: true, addonName: AddonIdentity.Read(context.NrCopy).Name)
            .ToDictionary(r => r.Route, r => r.Label);
        string LabelOf(Route r) => labels.TryGetValue(r, out var l) ? l : r.ToString();
        // DLSS5 NR only with a copy of it, as on a game's page.
        bool nrOffered = context.NrCopy is not null || games.Any(g => g.NrInGame);
        var routes = BulkSetup.Routes.Where(r => r != Route.NrAddon || nrOffered).ToList();
        var builds = Components.AllOptiBuilds;

        Route? route = null;
        var rows = new List<BulkRow>();
        var unticked = new HashSet<BulkGame>();
        var boxes = new List<CheckBox>();
        var statusOf = new Dictionary<BulkGame, (FrameworkElement Row, TextBlock Status)>();
        bool preparing = false;
        string? reviewed = null;
        bool running = false, finished = false, stopAsked = false, closeAsked = false;
        CancellationTokenSource? cts = null;

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Bulk install",
            PrimaryButtonText = "Set up",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
        };

        // ---- the parts of the dialog
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedText"),
            Text = "Pick one route, then choose the games to install it into. Every compatible game is selected initially; untick any to skip it. Unsupported games and games with anti-cheat " +
                   "are left as they are and listed below. Restore originals on a game's page undoes its setup." +
                   (items.Count < _all.Count
                       ? (items.Count == 1 ? " This is the 1 game the Games page shows now" : $" These are the {items.Count} games the Games page shows now") +
                         $"; clear its filters to include all {_all.Count}."
                       : ""),
        };
        var progressText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Visibility = Visibility.Collapsed };
        var progressBar = new ProgressBar { Minimum = 0, Visibility = Visibility.Collapsed };
        var stepText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res("MutedText"), Visibility = Visibility.Collapsed };
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Res("WarnText"), Visibility = Visibility.Collapsed };

        var routeTitle = DialogSection("1 · ROUTE");
        var routePanel = new StackPanel { Spacing = 6 };
        var fitLines = new Dictionary<Route, TextBlock>();
        var radios = new List<(RadioButton Radio, Route Route)>();
        foreach (var r in routes)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = LabelOf(r), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var line = new TextBlock { FontSize = 12, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap };
            text.Children.Add(line);
            fitLines[r] = line;
            var radio = new RadioButton { GroupName = "BulkSetupRoute", Content = text };
            AutomationProperties.SetName(radio, LabelOf(r));
            radios.Add((radio, r));
            routePanel.Children.Add(radio);
        }

        var mfgBox = new CheckBox
        {
            IsChecked = Gpu.HasRtx40,
            Content = new TextBlock { Text = "MFG unlock in DirectX 12 games with DLSS Frame Generation", TextWrapping = TextWrapping.Wrap },
        };
        AutomationProperties.SetName(mfgBox, "MFG unlock");
        var mfgPanel = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed };
        mfgPanel.Children.Add(mfgBox);
        mfgPanel.Children.Add(new TextBlock
        {
            FontSize = 12, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = "For games set up the first time; games set up again keep their own choice." +
                   (Gpu.HasRtx40 ? "" : " Off at first: its usual frame generation needs an RTX 40-series card."),
        });

        var buildBox = new ComboBox { MinWidth = 360 };
        foreach (var b in builds) buildBox.Items.Add(new ComboBoxItem { Content = b.Label, Tag = b });
        buildBox.SelectedIndex = 0;
        AutomationProperties.SetName(buildBox, "OptiScaler build");
        var buildPanel = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        buildPanel.Children.Add(new TextBlock { Text = "OptiScaler build", Style = (Style)Application.Current.Resources["FieldLabel"] });
        buildPanel.Children.Add(buildBox);

        var againText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var againBox = new CheckBox { Content = againText, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(againBox, "Also set up again the games already set up");

        var blocker = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };

        var countText = new TextBlock { FontSize = 12, Foreground = Res("MutedText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var tickAll = new HyperlinkButton { Content = "Tick all", Padding = new Thickness(4, 0, 4, 0), Visibility = Visibility.Collapsed };
        var untickAll = new HyperlinkButton { Content = "Untick all", Padding = new Thickness(4, 0, 4, 0), Visibility = Visibility.Collapsed };
        var gamesHeader = new Grid();
        gamesHeader.Children.Add(DialogSection("2 · GAMES"));
        var headerRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        headerRight.Children.Add(countText);
        headerRight.Children.Add(tickAll);
        headerRight.Children.Add(untickAll);
        gamesHeader.Children.Add(headerRight);
        var gamesEmpty = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedText"),
            Text = "Pick a route above: every game it fits is listed here, ticked.",
        };
        var gamesPanel = new StackPanel { Spacing = 4 };
        var gamesScroll = new ScrollViewer { Content = gamesPanel, MaxHeight = 300, Padding = new Thickness(0, 0, 12, 0), Visibility = Visibility.Collapsed };

        var leftTitle = DialogSection("LEFT AS THEY ARE");
        leftTitle.Visibility = Visibility.Collapsed;
        var leftPanel = new StackPanel { Spacing = 4 };
        var leftScroll = new ScrollViewer { Content = leftPanel, MaxHeight = 240, Padding = new Thickness(0, 0, 12, 0), Visibility = Visibility.Collapsed };

        var previewBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MaxHeight = 280, Visibility = Visibility.Collapsed };
        var body = new StackPanel { Spacing = 12, Width = 700 };
        foreach (var part in new UIElement[] { intro, progressText, progressBar, stepText, routeTitle, routePanel, mfgPanel, buildPanel, againBox, blocker,
                                               notice, gamesHeader, gamesEmpty, gamesScroll, leftTitle, leftScroll, previewBox })
            body.Children.Add(part);
        dialog.Content = new ScrollViewer { Content = body, Padding = new Thickness(0, 0, 12, 0) };
        // Wider than a dialog's usual 548 px, for game names and reasons; the app's artwork behind it, like the window picker.
        dialog.Resources["ContentDialogMaxWidth"] = 780.0;
        dialog.Background = new Microsoft.UI.Xaml.Media.ImageBrush
        {
            ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Background.jpg"))) { DecodePixelWidth = 1920 },
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
        };
        dialog.Resources["ContentDialogTopOverlay"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        dialog.BorderBrush = Res("RowBorder");

        // ---- building blocks
        OptiBuild Build() => (buildBox.SelectedItem as ComboBoxItem)?.Tag as OptiBuild ?? builds[0];
        BulkOptions OptionsFor(Route r) => new(r, Build(), mfgBox.IsChecked == true, againBox.IsChecked == true);

        string Details(BulkRow row)
        {
            var item = itemOf[row.Game];
            var req = row.Request!;
            return $"{StoreNames.GetValueOrDefault(item.Launcher, item.Launcher)} · {Scanner.Label(req.Api)} · {req.Bitness}-bit" +
                   (row.Note == "" ? "" : " · " + row.Note);
        }

        static Border Card(UIElement child) => new()
        {
            Child = child, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(9),
            Background = Res("RowBackground"), BorderBrush = Res("RowBorder"), BorderThickness = new Thickness(1),
        };

        FrameworkElement PickRow(BulkRow row)
        {
            var item = itemOf[row.Game];
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = Details(row), FontSize = 12, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap });
            var box = new CheckBox { Content = text, IsChecked = !unticked.Contains(row.Game), HorizontalAlignment = HorizontalAlignment.Stretch, Tag = row.Game };
            AutomationProperties.SetName(box, item.Name);
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) unticked.Remove(row.Game);
                else unticked.Add(row.Game);
                UpdateCount();
            };
            boxes.Add(box);
            return Card(box);
        }

        FrameworkElement RunRow(BulkRow row)
        {
            var item = itemOf[row.Game];
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = Details(row), FontSize = 12, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap });
            var status = new TextBlock { Text = "Waiting", FontSize = 12, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            text.Children.Add(status);
            var card = Card(text);
            statusOf[row.Game] = (card, status);
            return card;
        }

        FrameworkElement LeftGroup(string reason, IEnumerable<string> names, bool antiCheat)
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = reason, FontSize = 12, Foreground = Res(antiCheat ? "WarnText" : "MutedText"), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = string.Join(" · ", names), TextWrapping = TextWrapping.Wrap });
            return Card(text);
        }

        static void Show(TextBlock status, string text, string brush)
        {
            status.Text = text;
            status.Foreground = Res(brush);
            ToolTipService.SetToolTip(status, text);
        }

        void Notice(string? text)
        {
            notice.Text = text ?? "";
            notice.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }

        int Ticked() => rows.Count(p => p.Request is not null && !unticked.Contains(p.Game));

        void UpdateCount()
        {
            reviewed = null;
            if (!running) dialog.PrimaryButtonText = "Preview selected games";
            int ready = rows.Count(p => p.Request is not null), ticked = Ticked();
            countText.Text = ready == 0 ? "" : $"{ticked} of {ready} ticked";
            tickAll.Visibility = untickAll.Visibility = ready > 0 ? Visibility.Visible : Visibility.Collapsed;
            dialog.PrimaryButtonText = "Preview selected games";
            dialog.IsPrimaryButtonEnabled = ticked > 0 && !running;
        }

        // Works out the plan again for the route and options picked; the ticks the player took off stay off.
        void Replan()
        {
            foreach (var r in routes)
            {
                var plan = BulkSetup.Plan(games, OptionsFor(r), context);
                int fit = plan.Count(p => p.Request is not null), waiting = plan.Count(p => p.NeedsAgain);
                fitLines[r].Text = BulkRouteLine(r) + " " + (fit == 0 && waiting == 0 ? "Fits none of these games."
                    : fit == 0 ? $"Fits {GameCount(waiting)} set up already."
                    : waiting == 0 ? $"Fits {GameCount(fit)}."
                    : $"Fits {GameCount(fit)}, and {waiting} more set up already.");
            }
            mfgPanel.Visibility = route is { } shown && shown is not (Route.OptiScaler or Route.MfgOnly) ? Visibility.Visible : Visibility.Collapsed;
            buildPanel.Visibility = route == Route.OptiScaler ? Visibility.Visible : Visibility.Collapsed;
            gamesPanel.Children.Clear();
            leftPanel.Children.Clear();
            boxes.Clear();
            if (route is not { } rt)
            {
                rows = new List<BulkRow>();
                UpdateCount();
                return;
            }
            var o = OptionsFor(rt);
            rows = BulkSetup.Plan(games, o, context);
            var includingReinstalls = o.Again ? rows : BulkSetup.Plan(games, o with { Again = true }, context);
            int setUp = includingReinstalls.Count(p => p.Reinstall && p.Request is not null);
            againText.Text = setUp switch
            {
                0 => "Also set up again the games DLSS 5 Master has set up already (this route fits none of them)",
                1 => "Also set up again the game DLSS 5 Master has set up already",
                _ => $"Also set up again the {setUp} games DLSS 5 Master has set up already",
            };
            againBox.Visibility = setUp > 0 || againBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            bool usesVulkanLayer = includingReinstalls.Any(row => row.Request is { Route: Route.NrAddon or Route.Feeder or Route.Uplift, Api: RenderApi.Vulkan });
            var block = BulkSetup.Blocker(o, context, usesVulkanLayer);
            blocker.Message = block ?? "";
            blocker.IsOpen = block is not null;

            var ready = rows.Where(p => p.Request is not null).ToList();
            foreach (var row in ready) gamesPanel.Children.Add(PickRow(row));
            gamesEmpty.Text = rows.Any(p => p.NeedsAgain)
                ? "Every game this route fits is set up already. Tick \"Also set up again\" to set them up again."
                : "None of these games can take this route. The reasons are below.";
            gamesEmpty.Visibility = ready.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            gamesScroll.Visibility = ready.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            // Games set up already are counted on the "Also set up again" box instead.
            var left = rows.Where(p => p.Request is null && !p.NeedsAgain).ToList();
            foreach (var group in left.GroupBy(p => p.Skip ?? "").OrderByDescending(g => g.First().AntiCheat)
                                      .ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
                leftPanel.Children.Add(LeftGroup(group.Key, group.Select(p => itemOf[p.Game].Name), group.First().AntiCheat));
            leftTitle.Text = $"LEFT AS THEY ARE · {left.Count}";
            leftTitle.Visibility = leftScroll.Visibility = left.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateCount();
        }

        void AskStop()
        {
            stopAsked = true;
            try { cts?.Cancel(); }
            catch (ObjectDisposedException) { }
            dialog.CloseButtonText = closeAsked ? "Closing…" : "Stopping…";
            stepText.Text = (closeAsked ? "DLSS 5 Master closes" : "Stopping") + " at the next safe point: a game whose files are being changed " +
                            "is finished first, so no game is left half set up.";
            stepText.Visibility = Visibility.Visible;
        }

        // Closing the window while games are set up waits for the one being set up, then closes.
        TypedEventHandler<AppWindow, AppWindowClosingEventArgs> windowClosing = (_, args) =>
        {
            if (!running) return;
            args.Cancel = true;
            closeAsked = true;
            AskStop();
        };

        async Task RunAsync(Route rt)
        {
            var todo = rows.Where(p => p.Request is not null && !unticked.Contains(p.Game)).ToList();
            if (todo.Count == 0) return;
            // What changed while the dialog was open wins over the list it shows.
            var refusal = _busy ? "Another setup is running. Wait for it to finish, then try again."
                : _appUpdating ? "DLSS 5 Master is updating itself and starts again when that is done. Set up the games after that."
                : !ReferenceEquals(library, _scanCts) || !_libraryScanned ? "The library was read again while this was open. Close this, then open Bulk install again."
                : null;
            if (refusal is not null) { Notice(refusal); return; }
            Notice(null);
            running = true;   // before the first wait: a second press finds it running
            _bulkRunning = true;
            _busy = true;     // as an install on a game's page: the app update waits for it
            dialog.IsPrimaryButtonEnabled = false;
            var source = new CancellationTokenSource();
            cts = source;
            bool begun = false, cancelled = false;
            int done = 0, failed = 0, left = 0, notStarted = 0;
            try
            {
                // The runtime, ReShade or NR copy picked in Settings while this was open changes what fits.
                BulkContext fresh;
                try { fresh = await Task.Run(() => BulkContext.Read(_settings, _components)); }
                catch (Exception ex)
                {
                    Notice("Could not read the settings: " + ex.Message);
                    return;
                }
                if (stopAsked) { cancelled = true; return; }
                if (fresh != context)
                {
                    context = fresh;
                    Notice("Settings changed while this was open, so the list was worked out again. Check it, then press Set up again.");
                    return;
                }

                begun = true;
                AppWindow.Closing += windowClosing;
                // The choices are made: the list now shows the games being set up.
                foreach (var part in new UIElement[] { intro, routeTitle, routePanel, mfgPanel, buildPanel, againBox, blocker, tickAll, untickAll })
                    part.Visibility = Visibility.Collapsed;
                blocker.IsOpen = false;
                gamesPanel.Children.Clear();
                statusOf.Clear();
                foreach (var row in todo) gamesPanel.Children.Add(RunRow(row));
                countText.Text = GameCount(todo.Count);
                var options = OptionsFor(rt);
                var label = rt == Route.OptiScaler ? options.Build.Label : LabelOf(rt);
                dialog.Title = "Setting up " + GameCount(todo.Count);
                dialog.PrimaryButtonText = "";
                dialog.DefaultButton = ContentDialogButton.None;
                dialog.CloseButtonText = "Stop";
                progressBar.Maximum = todo.Count;
                progressBar.Value = 0;
                progressText.Visibility = progressBar.Visibility = stepText.Visibility = Visibility.Visible;
                int notFit = rows.Count(p => p.Request is null), untickedNow = rows.Count(p => p.Request is not null) - todo.Count;
                AppPaths.Log($"Set up several games: {label} in {GameCount(todo.Count)}; {notFit} left as they are" +
                             (untickedNow > 0 ? $", {untickedNow} unticked." : "."));

                // Once a stop is asked, the stop's explanation stays: the game's own steps no longer replace it.
                var progress = new Progress<string>(t => { if (!stopAsked) stepText.Text = t; });
                // A drive that filled up stops the games still to come on it: each would fail part-way too.
                var fullDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int n = 0; n < todo.Count; n++)
                {
                    var (card, status) = statusOf[todo[n].Game];
                    var item = itemOf[todo[n].Game];
                    if (stopAsked)
                    {
                        Show(status, "Not started", "MutedText");
                        notStarted++;
                        continue;
                    }
                    var drive = DriveOf(item.Entry.Dir);
                    if (fullDrives.Contains(drive))
                    {
                        Show(status, $"Left as it is: drive {drive} is full.", "WarnText");
                        AppPaths.Log($"[{item.Name}] Set up several games: left as it is: drive {drive} is full.");
                        left++;
                        progressBar.Value = n + 1;
                        continue;
                    }
                    progressText.Text = $"{n + 1} of {todo.Count}: {item.Name}";
                    stepText.Text = "";
                    Show(status, "Setting up…", "AccentText");
                    card.StartBringIntoView();
                    var (outcome, text, diskFull) = await SetUpOneAsync(item, options, context, label, progress, source.Token);
                    if (diskFull) fullDrives.Add(drive);
                    switch (outcome)
                    {
                        case BulkOutcome.Done: done++; Show(status, text, "AccentText"); break;
                        case BulkOutcome.Failed: failed++; Show(status, text, "ErrorText"); break;
                        case BulkOutcome.Left: left++; Show(status, text, "WarnText"); break;
                        default: notStarted++; Show(status, text, "MutedText"); break;
                    }
                    progressBar.Value = n + 1;
                }
            }
            finally
            {
                running = false;
                _bulkRunning = false;
                _busy = false;
                cts = null;
                source.Dispose();
                if (begun)
                {
                    finished = true;
                    AppWindow.Closing -= windowClosing;
                }
                else
                {
                    // Nothing was set up: back to picking, with the plan worked out again.
                    stopAsked = closeAsked = false;
                    dialog.CloseButtonText = "Cancel";
                    Replan();
                }
            }
            if (cancelled) { dialog.Hide(); return; }
            if (!begun) return;

            var parts = new List<string> { $"{done} set up" };
            if (failed > 0) parts.Add($"{failed} could not be set up");
            if (left > 0) parts.Add(left == 1 ? "1 left as it is" : $"{left} left as they are");
            if (notStarted > 0) parts.Add($"{notStarted} not started");
            AppPaths.Log("Set up several games: " + string.Join(", ", parts) + (stopAsked ? " (stopped)." : "."));
            dialog.Title = stopAsked ? $"Stopped: {done} of {GameCount(todo.Count)} set up"
                : done == todo.Count ? (todo.Count == 1 ? "1 game set up" : $"All {todo.Count} games set up")
                : $"{done} of {GameCount(todo.Count)} set up";
            progressText.Text = string.Join(" · ", parts) + ".";
            stepText.Text = "Every step is in Settings → Activity log. Restore originals on a game's page undoes its setup.";
            dialog.CloseButtonText = "Close";
            dialog.DefaultButton = ContentDialogButton.Close;
            // A filter on set-up games may show more games now.
            ApplyFilter();
            if (closeAsked)
            {
                dialog.Hide();
                Close();
            }
        }

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;   // the dialog stays open: it shows the progress, then what happened
            if (running || preparing || finished || route is not { } rt) return;
            var selected = rows.Where(p => p.Request is not null && !unticked.Contains(p.Game)).ToList();
            var stamp = System.Text.Json.JsonSerializer.Serialize(selected.Select(p => p.Request));
            if (reviewed != stamp)
            {
                if (_busy) return;
                preparing = true;
                _busy = true;
                dialog.IsPrimaryButtonEnabled = false;
                body.IsHitTestVisible = false;
                foreach (var control in radios.Select(p => (Control)p.Radio).Concat(boxes).Concat(new Control[] { mfgBox, buildBox, againBox, tickAll, untickAll })) control.IsEnabled = false;
                previewBox.Visibility = Visibility.Visible;
                previewBox.Text = "Preparing file preview. Components may be unpacked/downloaded; game files are not installed yet…";
                try
                {
                    var lines = new List<string>();
                    foreach (var row in selected)
                    {
                        InstallPreview? plan = null;
                        await Task.Run(() => Installers.InstallAsync(row.Request!, _components, _ => { }, null,
                            CancellationToken.None, p => plan = p, previewOnly: true));
                        lines.Add(itemOf[row.Game].Name + "\n" + (plan?.Describe() ?? "No file plan available."));
                    }
                    previewBox.Text = string.Join("\n\n", lines);
                    reviewed = stamp;
                    dialog.PrimaryButtonText = "Install reviewed games";
                }
                catch (Exception ex) { previewBox.Text = "Preview stopped: " + ex.Message + "\nUntick the affected game or resolve this before installing."; }
                finally { preparing = false; _busy = false; body.IsHitTestVisible = true; foreach (var control in radios.Select(p => (Control)p.Radio).Concat(boxes).Concat(new Control[] { mfgBox, buildBox, againBox, tickAll, untickAll })) control.IsEnabled = true; dialog.IsPrimaryButtonEnabled = Ticked() > 0; RefreshLibrarySummary(); }
                return;
            }
            try { await RunAsync(rt); }
            catch (Exception ex) { AppPaths.Log("Set up several games stopped: " + ex); }
        };
        dialog.Closing += (_, args) =>
        {
            if (preparing) { args.Cancel = true; return; }
            if (!running) return;
            args.Cancel = true;
            AskStop();
        };
        tickAll.Click += (_, _) =>
        {
            foreach (var box in boxes) box.IsChecked = true;
            unticked.ExceptWith(rows.Select(p => p.Game));
            UpdateCount();
        };
        untickAll.Click += (_, _) =>
        {
            foreach (var box in boxes) box.IsChecked = false;
            unticked.UnionWith(rows.Where(p => p.Request is not null).Select(p => p.Game));
            UpdateCount();
        };
        foreach (var (radio, r) in radios) radio.Checked += (_, _) => { route = r; Replan(); };
        mfgBox.Click += (_, _) => Replan();
        againBox.Click += (_, _) => Replan();
        buildBox.SelectionChanged += (_, _) => Replan();
        Replan();
        await dialog.ShowAsync();
    }

    /// <summary>
    /// What a failed setup left in the game, said after its error. Its record shows it: none (nothing changed, or the earlier
    /// setup was taken out first), the earlier one unchanged (it stopped before the new setup began; a restore that stopped
    /// part-way leaves that record too), or a new one (the new setup began, and what it changed is recorded).
    /// </summary>
    private static string AfterFailure(string gameDir, Manifest? before)
    {
        Manifest? after;
        try { after = Journal.ReadManifest(gameDir); }
        catch (Exception) { return " Its install record could not be read: open its page."; }
        if (after is null) return before is null ? "" : " Its earlier setup was taken out first, so the game has its own files again.";
        if (before is not null && after.Date == before.Date) return " Its earlier setup is still recorded: set it up again, or restore it, on its page.";
        return " What it changed before it stopped is recorded: Restore originals on its page undoes it.";
    }

    /// <summary>The drive is full (ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL).</summary>
    private static bool IsDiskFull(Exception ex) => ex is IOException && (ex.HResult & 0xFFFF) is 112 or 39;

    /// <summary>
    /// Sets one game up with the batch's choices. It is scanned and planned again first: the library's scan may be out of date
    /// (a game updated, run or changed by hand since), and what changed may leave it as it is now.
    /// </summary>
    private async Task<(BulkOutcome Outcome, string Text, bool DiskFull)> SetUpOneAsync(GameItem item, BulkOptions options, BulkContext context,
        string label, IProgress<string> progress, CancellationToken ct)
    {
        var choices = _settings.OverrideOf(item.Entry);
        var preferred = choices?.Exe ?? item.Entry.Exe;
        try
        {
            BulkRow plan;
            try
            {
                var game = await Task.Run(() => BulkSetup.Read(item.Entry, Scanner.Scan(item.Entry.Dir, preferred, ct), choices), ct);
                plan = BulkSetup.Plan(game, options, context);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                AppPaths.Log($"[{item.Name}] Set up several games: stopped before it started.");
                return (BulkOutcome.Stopped, "Stopped before anything changed", false);
            }
            catch (Exception ex)
            {
                AppPaths.Log($"[{item.Name}] Set up several games: could not scan it: {ex.Message}");
                return (BulkOutcome.Left, "Left as it is: it could not be scanned (" + ex.Message + ")", false);
            }
            if (plan.Request is not { } req)
            {
                AppPaths.Log($"[{item.Name}] Set up several games: left as it is: {plan.Skip}");
                return (BulkOutcome.Left, "Left as it is: " + plan.Skip, false);
            }
            var before = plan.Game.Scan?.Manifest;
            AppPaths.Log($"[{item.Name}] Set up several games: {label} ({Path.GetFileName(req.ExePath)}, {Scanner.Label(req.Api)}, {req.Bitness}-bit)");
            try
            {
                // Off the UI thread: the setup checks large files before it copies them. Its lines reach the activity log by themselves.
                await Task.Run(() => Installers.InstallAsync(req, _components, _ => { }, progress, ct));
                return (BulkOutcome.Done, "Set up", false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // InstallAsync gives up on a stop only before it changes the game.
                AppPaths.Log($"[{item.Name}] Set up several games: stopped before anything changed.");
                return (BulkOutcome.Stopped, "Stopped before anything changed", false);
            }
            catch (Exception ex)
            {
                // Also a time-out (an OperationCanceledException the player did not cause): a failure with its reason.
                AppPaths.Log($"[{item.Name}] install failed: {ex}");
                return (BulkOutcome.Failed, "Could not be set up: " + ex.Message + AfterFailure(req.GameDir, before), IsDiskFull(ex));
            }
        }
        finally
        {
            // The poster shows what the game holds now.
            try { item.SetScan(await Task.Run(() => Scanner.Scan(item.Entry.Dir, preferred))); }
            catch (Exception ex) { AppPaths.Log($"[{item.Name}] Set up several games: could not scan it again: {ex.Message}"); }
        }
    }
}
