using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using DLSS5Master.Core;
using DLSS5Master.Views;
using Windows.Storage.Pickers;

namespace DLSS5Master;

/// <summary>The per-game panel: executable, API, backend/route choice, status, DLL swapping, install/restore.</summary>
public sealed partial class MainWindow
{
    private GameItem? _game;
    private GameScan? _scan;
    private bool _populating;
    private bool _busy;
    private CancellationTokenSource? _installCts;

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private Button? _removeOtherButton;
    private Button? _removeReShadeButton;

    /// <summary>Fills the OptiScaler build list (updated builds first) and labels the MFG option with the version in use.</summary>
    private void RefreshOptiBuildList()
    {
        var selected = (OptiBuildBox.SelectedItem as ComboBoxItem)?.Tag as OptiBuild;
        OptiBuildBox.Items.Clear();
        foreach (var b in Components.AllOptiBuilds) OptiBuildBox.Items.Add(new ComboBoxItem { Content = b.Label, Tag = b });
        OptiBuildBox.SelectedItem = OptiBuildBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((OptiBuild)i.Tag).Id == selected?.Id)
                                    ?? OptiBuildBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        var mfg = Updates.InUse(Updates.Sources.First(s => s.Id == "mfgunlock")).Version;
        MfgBox.Content = $"MFG unlock for RTX 40 — DLSS multi frame generation 3x/4x (MFGAdaUnlock {mfg})";
    }

    private void InitGamePanel()
    {
        _removeOtherButton = new Button { Content = "Remove it" };
        _removeOtherButton.Click += RemoveOther_Click;
        _removeReShadeButton = new Button { Content = "Remove ReShade" };
        _removeReShadeButton.Click += RemoveUnrecordedReShade_Click;
        RefreshOptiBuildList();
        foreach (var h in Installers.OptiHookNames) OptiHookBox.Items.Add(h);
        foreach (var (name, code) in Installers.MenuKeys) MenuKeyBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
    }

    // ------------------------------------------------------------------ open / close

    private async void GameGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GameItem item) return;
        _game = item;
        LogBox.Text = "";
        GameOverlay.Visibility = Visibility.Visible;
        PanelTitle.Text = item.Name;
        PanelPoster.Source = item.Poster;
        await RescanAsync();
    }

    private void ClosePanel_Click(object sender, RoutedEventArgs e) => ClosePanel();
    private void GameOverlay_Tapped(object sender, TappedRoutedEventArgs e) => ClosePanel();
    private void GamePanel_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void ClosePanel()
    {
        if (_busy) return;
        GameOverlay.Visibility = Visibility.Collapsed;
        _game = null;
        _scan = null;
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await RescanAsync();

    private async Task RescanAsync()
    {
        if (_game is null) return;
        var game = _game;
        SetBusy(true, "Scanning…");
        var preferred = _settings.Overrides.GetValueOrDefault(game.Entry.Dir)?.Exe ?? game.Entry.Exe;
        try
        {
            _scan = await Task.Run(() => Scanner.Scan(game.Entry.Dir, preferred));
            game.SetScan(_scan);
            Populate();
        }
        catch (Exception ex) { Log("Scan failed: " + ex.Message); }
        finally { SetBusy(false); }
    }

    // ------------------------------------------------------------------ populate

    private ExeInfo? SelectedExe => ExeBox.SelectedItem as ExeInfo;

    private RenderApi SelectedApi
    {
        get
        {
            if (ApiBox.SelectedItem is ComboBoxItem { Tag: RenderApi api }) return api;
            return SelectedExe?.Api ?? RenderApi.Unknown;
        }
    }

    private void Populate()
    {
        if (_scan is null || _game is null) return;
        _populating = true;
        try
        {
            ExeBox.Items.Clear();
            foreach (var exe in _scan.Executables) ExeBox.Items.Add(exe);
            ExeBox.SelectedItem = _scan.Primary;
            if (_scan.Primary is null) ExeBox.PlaceholderText = "No game executable found — use Change .exe";

            var ov = _settings.Overrides.GetValueOrDefault(_game.Entry.Dir);
            BuildApiBox(ov?.Api);

            var manifest = _scan.Manifest;
            var backend = manifest?.Route == "optiscaler" ? 1 : 0;
            BackendBox.SelectedIndex = backend;

            var buildId = manifest?.OptiScalerBuild;
            OptiBuildBox.SelectedItem = OptiBuildBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((OptiBuild)i.Tag).Id == buildId) ?? OptiBuildBox.Items[0];
            OptiHookBox.SelectedItem = manifest?.OptiScalerHook ?? (SelectedApi == RenderApi.Vulkan ? "winmm.dll" : "dxgi.dll");

            var key = CurrentMenuKey() ?? _settings.OptiScalerMenuKey;
            MenuKeyBox.SelectedItem = MenuKeyBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, key, StringComparison.OrdinalIgnoreCase)) ?? MenuKeyBox.Items[0];

            AntiCheatBox.Visibility = _scan.AntiCheat ? Visibility.Visible : Visibility.Collapsed;
            AntiCheatBox.IsChecked = false;
            AddDlssBox.IsChecked = false;
            // MFG unlock: keep what is installed; on a fresh game, default on for RTX 40 owners when the game has frame generation.
            MfgBox.IsChecked = manifest is not null
                ? manifest.MfgUnlock is not null
                : Gpu.HasRtx40 && Installers.MfgUnavailable(SelectedApi, SelectedExe?.Bitness ?? 0, _scan.HasFrameGen) is null;
            ProxyBox.SelectedIndex = manifest?.ReShadeHook?.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
        }
        finally { _populating = false; }
        RefreshRoutes(selectFromManifest: true);
        RefreshStatus();
        RefreshDlls();
        RestoreButton.IsEnabled = _scan.Manifest is not null;
    }

    private void BuildApiBox(string? overrideApi)
    {
        ApiBox.Items.Clear();
        var detected = SelectedExe?.Api ?? RenderApi.Unknown;
        ApiBox.Items.Add(new ComboBoxItem { Content = $"Automatic (detected: {Scanner.Label(detected)})", Tag = null });
        foreach (var api in new[] { RenderApi.DX12, RenderApi.DX11, RenderApi.DX10, RenderApi.DX9, RenderApi.DX8, RenderApi.DirectDraw, RenderApi.Vulkan, RenderApi.OpenGL })
            ApiBox.Items.Add(new ComboBoxItem { Content = Scanner.Label(api), Tag = api });
        ApiBox.SelectedItem = ApiBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is RenderApi a && a.ToString() == overrideApi) ?? ApiBox.Items[0];
    }

    private string? CurrentMenuKey()
    {
        if (SelectedExe is null) return null;
        var ini = Path.Combine(Path.GetDirectoryName(SelectedExe.Path)!, "OptiScaler.ini");
        var value = File.Exists(ini) ? Ini.Get(Ini.ReadText(ini), "Menu", "ShortcutKey") : null;
        return value is null or "auto" ? null : value;
    }

    private void RefreshRoutes(bool selectFromManifest = false)
    {
        if (_scan is null) return;
        _populating = true;
        try
        {
            bool opti = BackendBox.SelectedIndex == 1;
            RouteBox.Visibility = opti ? Visibility.Collapsed : Visibility.Visible;
            OptiBuildBox.Visibility = opti ? Visibility.Visible : Visibility.Collapsed;
            RouteLabel.Text = opti ? "OptiScaler build" : "Installation route";
            ReShadeOptions.Visibility = opti ? Visibility.Collapsed : Visibility.Visible;
            OptiOptions.Visibility = opti ? Visibility.Visible : Visibility.Collapsed;

            var bits = SelectedExe?.Bitness ?? 0;
            var routes = Installers.RoutesFor(SelectedApi, bits, _scan.HasNativeDlss, _scan.HasFrameGen);
            var previous = (RouteBox.SelectedItem as ComboBoxItem)?.Tag as Route?;
            var reshadeRoutes = routes.Where(r => r.Route != Route.OptiScaler).ToList();
            // Rebuild the list only when the available routes changed. Rebuilding a ComboBox's items while it is
            // handling the user's pick (RouteBox_SelectionChanged) makes WinUI fail fast with "Element not found".
            var signature = string.Join("|", reshadeRoutes.Select(r => $"{r.Route}:{r.Unavailable is null}"));
            bool rebuild = !Equals(RouteBox.Tag, signature);
            if (rebuild)
            {
                RouteBox.Items.Clear();
                foreach (var r in reshadeRoutes)
                {
                    var item = new ComboBoxItem { Content = r.Label + (r.Unavailable is null ? "" : "  — unavailable"), Tag = r.Route, IsEnabled = r.Unavailable is null };
                    if (r.Unavailable is not null) ToolTipService.SetToolTip(item, r.Unavailable);
                    RouteBox.Items.Add(item);
                }
                RouteBox.Tag = signature;
            }
            if (rebuild || selectFromManifest)
            {
                Route wanted = Installers.RecommendedRoute(SelectedApi, bits, _scan.HasNativeDlss);
                if (selectFromManifest && _scan.Manifest?.Route is "native" or "multipass" or "feeder")
                    wanted = _scan.Manifest.Route switch { "native" => Route.Native, "multipass" => Route.Multipass, _ => Route.Feeder };
                else if (previous is { } p && routes.Any(r => r.Route == p && r.Unavailable is null)) wanted = p;
                var target = RouteBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (Route)i.Tag == wanted && i.IsEnabled)
                             ?? RouteBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.IsEnabled);
                if (!ReferenceEquals(RouteBox.SelectedItem, target)) RouteBox.SelectedItem = target;
            }

            ProxyPanel.Visibility = SelectedApi == RenderApi.DX11 ? Visibility.Visible : Visibility.Collapsed;
            var mfgWhy = Installers.MfgUnavailable(SelectedApi, bits, _scan.HasFrameGen);
            MfgBox.IsEnabled = mfgWhy is null;
            if (mfgWhy is not null) MfgBox.IsChecked = false;
            MfgNote.Text = mfgWhy ?? (Gpu.HasRtx40 ? "" : $"Built for GeForce RTX 40 cards; this PC has {Gpu.Nvidia ?? "no NVIDIA GPU"}.");
            MfgNote.Visibility = MfgNote.Text == "" ? Visibility.Collapsed : Visibility.Visible;
            CustomOptiPanel.Visibility = (OptiBuildBox.SelectedItem as ComboBoxItem)?.Tag is OptiBuild { Id: "custom" } ? Visibility.Visible : Visibility.Collapsed;
            ApplyKeyButton.IsEnabled = _scan.OptiScalerIni;

            // Description + warnings for the current choice.
            var chosen = opti ? routes.First(r => r.Route == Route.OptiScaler) : routes.FirstOrDefault(r => (RouteBox.SelectedItem as ComboBoxItem)?.Tag is Route rt && r.Route == rt);
            RouteDescription.Text = chosen?.Description ?? "";
            string? warning = chosen?.Unavailable ?? (RouteBox.SelectedItem is null && !opti ? "No ReShade route fits this game's renderer." : null);
            if (warning is null && opti && (OptiBuildBox.SelectedItem as ComboBoxItem)?.Tag is OptiBuild { NeuralRendering: true } && !_scan.HasNativeDlss)
                warning = "DLSS-NR OptiScaler builds need a game that ships its own DLSS. Pick the custom (official) build, or a ReShade route.";
            bool otherApp = _scan.OtherAppBackup || _scan.OtherAppRecord;
            if (otherApp)
                warning = "Another app has an install in this game. Remove it before installing with DLSS 5 Master.";
            // ReShade that nobody recorded (installed by hand or by an older tool): offer to move it out of the game.
            bool unrecorded = !otherApp && _scan.Manifest is null && _scan.ReShade.Installed && _scan.ReShade.File is not null;
            if (unrecorded && warning is null)
                warning = $"ReShade {_scan.ReShade.Version} is already in this game, and no app has a record of it. Remove it to start clean, or install over it.";
            if (warning is null && _components.RuntimeDir is null && !(opti && (OptiBuildBox.SelectedItem as ComboBoxItem)?.Tag is OptiBuild { NeuralRendering: false }))
                warning = "No NVIDIA runtime folder found. Set it in Settings.";
            if (warning is null && SelectedApi == RenderApi.Unknown)
                warning = "The renderer could not be detected. Choose the Rendering API by hand.";
            RouteWarning.Message = warning ?? "";
            RouteWarning.IsOpen = warning is not null;
            RouteWarning.ActionButton = otherApp ? _removeOtherButton : unrecorded && warning is not null && warning.StartsWith("ReShade ") ? _removeReShadeButton : null;
            InstallButton.IsEnabled = !_busy && SelectedExe is not null && (opti ? chosen?.Unavailable is null : RouteBox.SelectedItem is not null);
            InstallButton.Content = _scan.Manifest is null ? "Complete Installation" : "Reinstall";
        }
        finally { _populating = false; }
    }

    private void AddChip(string text, bool accent = false)
    {
        PanelChips.Children.Add(new Border
        {
            Padding = new Thickness(10, 3, 10, 3),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("RowBorder"),
            Background = Res("RowBackground"),
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = accent ? Res("AccentText") : new SolidColorBrush(Microsoft.UI.Colors.White) }
        });
    }

    private void AddRow(StackPanel host, string label, string value, string? brushKey = null, UIElement? extra = null)
    {
        var grid = new Grid { Padding = new Thickness(20, 12, 20, 12), ColumnSpacing = 12, BorderBrush = Res("RowBorder"), BorderThickness = new Thickness(0, host.Children.Count == 0 ? 0 : 1, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, Foreground = Res("MutedText"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var v = new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        if (brushKey is not null) v.Foreground = Res(brushKey);
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        if (extra is not null) { Grid.SetColumn((FrameworkElement)extra, 2); grid.Children.Add(extra); }
        host.Children.Add(grid);
    }

    private void RefreshStatus()
    {
        if (_scan is null) return;
        var exe = SelectedExe;
        PanelChips.Children.Clear();
        if (exe is not null)
        {
            AddChip(Scanner.Label(SelectedApi));
            AddChip(exe.Bitness == 0 ? "unreadable exe" : $"{exe.Bitness}-bit");
        }
        if (_scan.Dlss is not null) AddChip("DLSS", accent: true);
        if (_scan.Upscalers.Any(u => u.Kind == UpscalerKind.Fsr)) AddChip("FSR");
        if (_scan.Upscalers.Any(u => u.Kind == UpscalerKind.XeSS)) AddChip("XeSS");
        if (_scan.AntiCheat) AddChip("Anti-cheat");

        StatusRows.Children.Clear();
        AddRow(StatusRows, "Architecture", exe is null ? "—" : exe.Bitness == 0 ? "unknown" : $"{exe.Bitness}-bit");
        AddRow(StatusRows, "Rendering API", Scanner.Label(SelectedApi), SelectedApi == RenderApi.Unknown ? "WarnText" : "AccentText");
        var m = _scan.Manifest;
        AddRow(StatusRows, "Installed backend (this app)", Installers.RouteLabel(m?.Route), m is null ? null : "AccentText");
        if (m is not null)
            AddRow(StatusRows, "Installed on", m.Date.ToLocalTime().ToString("g") + $"  ·  {m.Added.Count} added, {m.Replaced.Count} replaced");
        if (_scan.OtherAppBackup || _scan.OtherAppRecord)
            AddRow(StatusRows, "Other app install", _scan.OtherAppRecordRoute ?? _scan.OtherAppBackupRoute ?? "yes", "WarnText");

        var dlss = _scan.Dlss;
        AddRow(StatusRows, "DLSS", dlss?.Version ?? "none", dlss is null ? null : "AccentText");
        var nr = _scan.Upscalers.FirstOrDefault(u => u.Kind == UpscalerKind.DlssNR);
        AddRow(StatusRows, "Neural rendering runtime", nr?.Version ?? "not installed", nr is null ? null : "AccentText");
        var addons = _scan.RenoDxAddons;
        AddRow(StatusRows, "DLSS 5 add-on", addons.Count == 0 ? "not installed" : string.Join(", ", addons.Where(a => !a.Equals(Components.MfgAddon, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty("not installed")), addons.Count == 0 ? null : "AccentText");
        var fg = _scan.Upscalers.FirstOrDefault(u => u.Kind == UpscalerKind.DlssFG);
        AddRow(StatusRows, "DLSS Frame Generation", fg?.Version ?? "none", fg is null ? null : Scanner.CompareVersions(fg.Version, "310.0.0.0") < 0 ? "WarnText" : "AccentText");
        bool mfgHere = addons.Any(a => a.Equals(Components.MfgAddon, StringComparison.OrdinalIgnoreCase));
        AddRow(StatusRows, "MFG unlock (RTX 40)", mfgHere ? (m?.MfgUnlock is { } mv ? $"Installed · {mv}" : "Installed (other app)") : "not installed", mfgHere ? "AccentText" : null);
        var rs = _scan.ReShade;
        AddRow(StatusRows, "ReShade", rs.Installed ? $"{rs.Version}{(rs.AddonSupport ? " + add-on" : " (no add-on support)")}  ·  {rs.File}" : "not installed",
            rs.Installed ? (rs.AddonSupport ? "AccentText" : "WarnText") : null);
        var optiKey = CurrentMenuKey();
        AddRow(StatusRows, "OptiScaler", _scan.OptiScalerHook is not null ? $"{_scan.OptiScalerHook}{(optiKey is null ? "" : $"  ·  menu key {KeyName(optiKey)}")}" : _scan.OptiScalerIni ? "OptiScaler.ini only" : "not installed",
            _scan.OptiScalerHook is not null ? "AccentText" : null);
        AddRow(StatusRows, "Game folder", _scan.Dir);
    }

    private static string KeyName(string code) =>
        Installers.MenuKeys.FirstOrDefault(k => k.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Name ?? code;

    private void RefreshDlls()
    {
        DllRows.Children.Clear();
        if (_scan is null) return;
        var files = _scan.Upscalers.Where(u => u.Kind != UpscalerKind.NgxCore).ToList();
        if (files.Count == 0)
        {
            DllRows.Children.Add(new TextBlock { Text = "No DLSS, FSR, XeSS or Streamline DLLs in this game.", Margin = new Thickness(20, 14, 20, 14), Foreground = Res("MutedText") });
            return;
        }
        foreach (var file in files)
        {
            var flyout = new MenuFlyout();
            foreach (var (path, version, source) in Installers.SwapCandidates(file.Name, _components).Where(c => Pe.GetBitness(c.Path) == file.Bitness))
            {
                var item = new MenuFlyoutItem { Text = $"{version}  ·  {source}", IsEnabled = version != file.Version };
                item.Click += async (_, _) => await SwapAsync(file, path);
                flyout.Items.Add(item);
            }
            if (flyout.Items.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
            var import = new MenuFlyoutItem { Text = $"Choose a {file.Name} file…" };
            import.Click += async (_, _) => await SwapFromFileAsync(file);
            flyout.Items.Add(import);
            var swap = new DropDownButton { Content = "Swap", Flyout = flyout, Margin = new Thickness(8, 0, 0, 0) };
            AddRow(DllRows, $"{Scanner.KindLabel(file.Kind)}  ·  {file.Rel}", $"{file.Version ?? "?"}  ·  {(file.Bitness == 0 ? "?" : file.Bitness + "-bit")}", null, swap);
        }
    }

    // ------------------------------------------------------------------ selection changes

    private void ExeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || _scan is null || SelectedExe is null) return;
        Scanner.InspectExeDir(_scan, Path.GetDirectoryName(SelectedExe.Path)!);
        _settings.OverrideFor(_scan.Dir).Exe = SelectedExe == _scan.Executables.FirstOrDefault() ? null : SelectedExe.Path;
        _settings.Save();
        _populating = true;
        BuildApiBox(_settings.Overrides.GetValueOrDefault(_scan.Dir)?.Api);
        _populating = false;
        RefreshRoutes();
        RefreshStatus();
    }

    private void ApiBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || _scan is null) return;
        _settings.OverrideFor(_scan.Dir).Api = (ApiBox.SelectedItem as ComboBoxItem)?.Tag is RenderApi a ? a.ToString() : null;
        _settings.Save();
        RefreshRoutes();
        RefreshStatus();
    }

    private void BackendBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_populating) RefreshRoutes(); }
    private void RouteBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_populating) RefreshRoutes(); }
    private void OptiBuildBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_populating) RefreshRoutes(); }

    private async void ChangeExe_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) return;
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var root = Path.GetFullPath(_game.Entry.Dir).TrimEnd('\\') + "\\";
        if (!file.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            Log($"Pick an .exe inside {_game.Entry.Dir}. For a game elsewhere, add it with + on the library.");
            return;
        }
        _settings.OverrideFor(_game.Entry.Dir).Exe = file.Path;
        _settings.Save();
        await RescanAsync();
    }

    private async void PickCustomOpti_Click(object sender, RoutedEventArgs e)
    {
        var dir = await PickFolderAsync();
        if (dir is not null) CustomOptiBox.Text = dir;
    }

    private async void HideGame_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"Hide {_game.Name}?",
            Content = "It disappears from the library. Settings → \"Show hidden games again\" brings it back.",
            PrimaryButtonText = "Hide",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _settings.HiddenGames.Add(Library.Normalize(_game.Entry.Dir));
        _settings.Save();
        var item = _game;
        ClosePanel();
        _all.Remove(item);
        _visible.Remove(item);
    }

    // ------------------------------------------------------------------ actions

    private void Log(string line)
    {
        void Append() { LogBox.Text += (LogBox.Text.Length > 0 ? "\n" : "") + line; LogBox.SelectionStart = LogBox.Text.Length; }
        if (DispatcherQueue.HasThreadAccess) Append(); else DispatcherQueue.TryEnqueue(Append);
    }

    private void SetBusy(bool busy, string text = "")
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyText.Text = text;
        InstallButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy && _scan?.Manifest is not null;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null || _game is null || SelectedExe is null) return;
        bool opti = BackendBox.SelectedIndex == 1;
        var build = (OptiBuildBox.SelectedItem as ComboBoxItem)?.Tag as OptiBuild;
        var route = opti ? Route.OptiScaler : (RouteBox.SelectedItem as ComboBoxItem)?.Tag as Route? ?? Route.Native;
        if (SelectedApi == RenderApi.Unknown) { Log("Choose the Rendering API first."); return; }
        var req = new InstallRequest
        {
            GameDir = _scan.Dir,
            ExePath = SelectedExe.Path,
            Api = SelectedApi,
            Bitness = SelectedExe.Bitness,
            Route = route,
            OptiBuild = build,
            OptiCustomFolder = CustomOptiBox.Text,
            OptiHook = OptiHookBox.SelectedItem as string ?? "dxgi.dll",
            MenuKey = (MenuKeyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "0x2D",
            ReShadeProxy = (ProxyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "dxgi.dll",
            AddMissingDlss = AddDlssBox.IsChecked == true,
            MfgUnlock = MfgBox.IsEnabled && MfgBox.IsChecked == true,
            AntiCheatAcknowledged = AntiCheatBox.IsChecked == true,
        };
        if (opti) { _settings.OptiScalerMenuKey = req.MenuKey; _settings.Save(); }

        LogBox.Text = "";
        Log($"Installing {(opti ? build?.Label : Installers.RouteLabel(Installers.RouteKey(route)))} for {_game.Name} ({Path.GetFileName(req.ExePath)}, {Scanner.Label(req.Api)}, {req.Bitness}-bit)…");
        SetBusy(true, "Installing…");
        _installCts = new CancellationTokenSource();
        var progress = new Progress<string>(t => BusyText.Text = t);
        try
        {
            await Installers.InstallAsync(req, _components, Log, progress, _installCts.Token);
        }
        catch (Exception ex)
        {
            Log("✖ " + ex.Message);
            AppPaths.Log($"[{_game.Name}] install failed: {ex}");
            if (Journal.ReadManifest(_scan.Dir) is not null) Log("Files changed before the failure are recorded; Restore originals undoes them.");
        }
        finally
        {
            SetBusy(false);
        }
        var log = LogBox.Text;
        await RescanAsync();
        LogBox.Text = log;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null) return;
        LogBox.Text = "";
        SetBusy(true, "Restoring…");
        try { await Installers.RestoreAsync(_scan.Dir, SelectedExe?.Path, Log); }
        catch (Exception ex) { Log("✖ " + ex.Message); }
        finally { SetBusy(false); }
        var log = LogBox.Text;
        await RescanAsync();
        LogBox.Text = log;
    }

    private async void ApplyKey_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null || SelectedExe is null) return;
        var code = (MenuKeyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "0x2D";
        try
        {
            Installers.SetOptiMenuKey(_scan.Dir, Path.GetDirectoryName(SelectedExe.Path)!, code, Log);
            _settings.OptiScalerMenuKey = code;
            _settings.Save();
        }
        catch (Exception ex) { Log("✖ " + ex.Message); }
        var log = LogBox.Text;
        await RescanAsync();
        LogBox.Text = log;
    }

    /// <summary>Undo another tool's install in this game, from that tool's own records and backups.</summary>
    private async void RemoveOther_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null || _busy) return;
        var dir = _scan.Dir;
        var (restore, delete, problem) = await Task.Run(() => ForeignInstalls.Summary(dir));
        if (problem is not null)
        {
            LogBox.Text = "";
            Log("✖ " + problem + " Nothing was changed.");
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Remove the other app's install?",
            Content = $"This puts back {restore} original file(s) and deletes {delete} file(s) the other app added to {_game?.Name ?? "this game"}, " +
                      "using that app's own backups. The other app will then show this game as not installed.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        LogBox.Text = "";
        SetBusy(true, "Removing…");
        try { await ForeignInstalls.RemoveAllAsync(dir, SelectedExe?.Path, Log); }
        catch (Exception ex) { Log("✖ " + ex.Message); }
        finally { SetBusy(false); }
        var log = LogBox.Text;
        await RescanAsync();
        LogBox.Text = log;
    }

    /// <summary>Move a ReShade install that no app recorded out of the game, into a dated folder in _DLSS5Master_Backup.</summary>
    private async void RemoveUnrecordedReShade_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null || _busy || SelectedExe is null || _scan.ReShade.File is not { } hook) return;
        var dir = _scan.Dir;
        var exe = SelectedExe.Path;
        var exeDir = Path.GetDirectoryName(exe)!;
        var items = await Task.Run(() => ForeignInstalls.UnrecordedReShadeFiles(exeDir, hook));
        if (items.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Remove ReShade from this game?",
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"No app has a record of this ReShade install, so DLSS 5 Master moves its files out of {_game?.Name ?? "the game"} " +
                           "instead of deleting them. They go to _DLSS5Master_Backup\\removed in the game folder, so you can put them back.\n\n" +
                           string.Join("\n", items.Select(i => "• " + Path.GetFileName(i) + (Directory.Exists(i) ? "\\" : ""))) +
                           "\n\nThe game's own files are not touched.",
                }
            },
            PrimaryButtonText = "Remove ReShade",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        LogBox.Text = "";
        SetBusy(true, "Removing ReShade…");
        try { await ForeignInstalls.MoveAsideUnrecordedReShadeAsync(dir, exe, hook, Log); }
        catch (Exception ex) { Log("✖ " + ex.Message); }
        finally { SetBusy(false); }
        var log = LogBox.Text;
        await RescanAsync();
        LogBox.Text = log;
    }

    private async Task SwapAsync(UpscalerFile file, string source)
    {
        if (_scan is null || SelectedExe is null) return;
        SetBusy(true, "Swapping…");
        try { await Task.Run(() => Installers.SwapDll(_scan.Dir, SelectedExe.Path, file, source, Log)); }
        catch (Exception ex) { Log("✖ " + ex.Message); }
        finally { SetBusy(false); }
        var log = LogBox.Text;
        await RescanAsync();
        LogBox.Text = log;
    }

    private async Task SwapFromFileAsync(UpscalerFile file)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".dll");
        var picked = await picker.PickSingleFileAsync();
        if (picked is null) return;
        string source;
        try { source = Installers.ImportDll(picked.Path); }
        catch (Exception ex) { Log("✖ " + ex.Message); return; }
        await SwapAsync(file, source);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = SelectedExe is not null ? Path.GetDirectoryName(SelectedExe.Path) : _scan?.Dir;
        if (dir is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) return;
        try
        {
            if (_game.Entry.Launcher == "Steam" && _game.Entry.Id is not null)
                Process.Start(new ProcessStartInfo($"steam://rungameid/{_game.Entry.Id}") { UseShellExecute = true });
            else if (SelectedExe is not null)
                Process.Start(new ProcessStartInfo(SelectedExe.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(SelectedExe.Path)! });
            Log("Launched.");
        }
        catch (Exception ex) { Log("✖ Could not launch: " + ex.Message); }
    }
}
