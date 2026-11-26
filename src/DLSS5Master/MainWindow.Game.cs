using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using DLSS5Master.Core;
using DLSS5Master.Views;

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
    private Button? _removeNrButton;
    private Button? _loadEarlyButton;
    private Button? _rescanLibraryButton;
    /// <summary>The installed add-ons ReShade could not start in the game's last run (error 1114), not yet loaded early.</summary>
    private List<string> _addonsToLoadEarly = new();
    /// <summary>The game's last run when frame generation had too few real frames to work with, else null.</summary>
    private Installers.RunSpeed? _slowFrameGen;
    /// <summary>The open game's folder no longer exists: it was moved (Steam's Move install folder) or uninstalled.</summary>
    private bool _folderGone;
    private string? _gameRouteWarning;
    private Button? _gameRouteWarningAction;
    private string? _unrecordedReShadeWarning;
    private const string CheckingVulkanLayer = "Checking the ReShade Vulkan layer for this executable…";
    private string? _vulkanNrUnavailable = CheckingVulkanLayer;
    private string? _vulkanFeederUnavailable = CheckingVulkanLayer;
    private int _vulkanLayerRead;

    /// <summary>Fills the OptiScaler build list (updated builds first) and labels the MFG option with the version in use.</summary>
    private void RefreshOptiBuildList()
    {
        var selected = (OptiBuildBox.SelectedItem as ComboBoxItem)?.Tag as OptiBuild;
        OptiBuildBox.Items.Clear();
        foreach (var b in Components.AllOptiBuilds) OptiBuildBox.Items.Add(new ComboBoxItem { Content = b.Label, Tag = b });
        OptiBuildBox.SelectedItem = OptiBuildBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((OptiBuild)i.Tag).Id == selected?.Id)
                                    ?? OptiBuildBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        var mfg = Updates.InUse(Updates.Sources.First(s => s.Id == "mfgunlock")).Version;
        MfgBox.Content = new TextBlock
        {
            Text = $"MFG unlock · DLSS 3x/4x on RTX 40 or Intel XeSS 2x–6x on RTX 20–50 (MFGAdaUnlock {mfg})",
            TextWrapping = TextWrapping.Wrap,
        };
        ToolTipService.SetToolTip(MfgBox, "Include MFG unlock when installing the selected route.");
    }

    private void InitGamePanel()
    {
        _removeOtherButton = new Button { Content = "Remove it" };
        _removeOtherButton.Click += RemoveOther_Click;
        _removeReShadeButton = new Button { Content = "Remove ReShade" };
        _removeReShadeButton.Click += RemoveUnrecordedReShade_Click;
        _removeNrButton = new Button { Content = "Remove NR" };
        _removeNrButton.Click += RemoveNrSetup_Click;
        _loadEarlyButton = new Button { Content = "Load add-ons early" };
        _loadEarlyButton.Click += LoadEarly_Click;
        _rescanLibraryButton = new Button { Content = "Rescan library" };
        _rescanLibraryButton.Click += RescanLibrary_Click;
        RefreshOptiBuildList();
        foreach (var h in Installers.OptiHookNames) OptiHookBox.Items.Add(h);
        foreach (var (name, code) in Installers.MenuKeys) MenuKeyBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
    }

    // ------------------------------------------------------------------ open / close

    private async void GameGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        // Set up several games is reading the games: one game's page would set games up beside it.
        if (_bulkRunning || _neuralMasterWorking) return;
        if (e.ClickedItem is GameItem item) await OpenGameAsync(item);
    }

    private async Task OpenGameAsync(GameItem item)
    {
        _game = item;
        ResetNeuralMasterGameBar();
        LogBox.Text = "";
        GameOverlay.Visibility = Visibility.Visible;
        Reveal(GamePanel);
        PanelTitle.Text = item.Name;
        PanelPoster.Source = item.Poster;
        await RescanAsync();
    }

    private void ClosePanel_Click(object sender, RoutedEventArgs e) => ClosePanel();
    private void GameOverlay_Tapped(object sender, TappedRoutedEventArgs e) => ClosePanel();
    private void GamePanel_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void ClosePanel()
    {
        if (_busy || _neuralMasterWorking) return;
        GameOverlay.Visibility = Visibility.Collapsed;
        _game = null;
        _scan = null;
        ++_vulkanLayerRead;
        ResetNeuralMasterGameBar();
        // An install or restore in the panel changes the totals.
        RefreshLibrarySummary();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await RescanAsync();

    private async Task RescanAsync(string? preferredExe = null)
    {
        if (_game is not { } game || _busy || _neuralMasterWorking) return;
        ++_neuralMasterRead;
        ++_vulkanLayerRead;
        _neuralMasterReading = true;
        SetBusy(true, "Scanning…");
        var preferred = preferredExe ?? _settings.OverrideOf(game.Entry)?.Exe ?? game.Entry.Exe;
        try
        {
            (_scan, _addonsToLoadEarly, _slowFrameGen, _folderGone) = await Task.Run(() =>
            {
                var scan = Scanner.Scan(game.Entry.Dir, preferred);
                return (scan, Installers.AddonsToLoadEarly(scan.Dir, scan.Manifest), Installers.SlowFrameGeneration(scan.Dir, scan.Manifest),
                        !Directory.Exists(game.Entry.Dir));
            });
            game.SetScan(_scan);
            await PopulateAsync();
            _ = LoadCommunityAsync();
        }
        catch (Exception ex)
        {
            _neuralMasterReading = false;
            DisplayNeuralMasterStatus(null);
            RefreshGameRouteWarning();
            Log("Scan failed: " + ex.Message);
            return;
        }
        finally { SetBusy(false); }
        await RefreshNeuralMasterStatusAsync();
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

    private async Task PopulateAsync()
    {
        if (_scan is null || _game is null) return;
        _populating = true;
        try
        {
            ExeBox.Items.Clear();
            foreach (var exe in _scan.Executables) ExeBox.Items.Add(exe);
            ExeBox.SelectedItem = _scan.Primary;
            if (_scan.Primary is null) ExeBox.PlaceholderText = "No game executable found — use Change .exe";

            var ov = _settings.OverrideOf(_game.Entry);
            BuildApiBox(ov?.Api);

            var manifest = _scan.Manifest;
            var backend = manifest?.Route == "optiscaler" ? 1 : 0;
            BackendBox.SelectedIndex = backend;

            var buildId = Components.CurrentOptiBuildId(manifest?.OptiScalerBuild);
            OptiBuildBox.SelectedItem = OptiBuildBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((OptiBuild)i.Tag).Id == buildId) ?? OptiBuildBox.Items[0];
            OptiHookBox.SelectedItem = manifest?.OptiScalerHook ?? (SelectedApi == RenderApi.Vulkan ? "winmm.dll" : "dxgi.dll");

            var key = CurrentMenuKey() ?? _settings.OptiScalerMenuKey;
            MenuKeyBox.SelectedItem = MenuKeyBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, key, StringComparison.OrdinalIgnoreCase)) ?? MenuKeyBox.Items[0];

            AntiCheatBox.Visibility = _scan.AntiCheat ? Visibility.Visible : Visibility.Collapsed;
            AntiCheatBox.IsChecked = false;
            if (_scan.AntiCheatName is { } antiCheat)
                AntiCheatBox.Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = Installers.AntiCheatWarning(antiCheat) + " I understand." + (ArkNote() is { Length: > 0 } ark ? " " + ark : ""),
                };
            // MFG unlock: keep what is installed; on a fresh game, default on for RTX 40 owners when the game has frame generation,
            // but off with anti-cheat: its hooks go deep into the game (an older one crashed Call of Duty at start).
            MfgBox.IsChecked = manifest is not null
                ? manifest.MfgUnlock is not null
                : Gpu.HasRtx40 && !_scan.AntiCheat && Installers.MfgUnavailable(SelectedApi, SelectedExe?.Bitness ?? 0, _scan.HasFrameGen) is null;
            ProxyBox.SelectedIndex = manifest?.ReShadeHook?.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
            EarlyAddonsBox.IsChecked = ov?.LoadAddonsEarly == true;
            // Open when it holds a choice that is on, so nothing in it is ever on unseen.
            TroubleshootingExpander.IsExpanded = EarlyAddonsBox.IsChecked == true;
        }
        finally { _populating = false; }
        var routeCheck = RefreshExecutableRoutesAsync(selectFromManifest: true);
        RefreshStatus();
        RefreshDlls();
        await routeCheck;
    }

    private async Task RefreshExecutableRoutesAsync(bool selectFromManifest = false)
    {
        int read = ++_vulkanLayerRead;
        _vulkanNrUnavailable = _vulkanFeederUnavailable = CheckingVulkanLayer;
        RefreshRoutes(selectFromManifest);
        if (SelectedApi != RenderApi.Vulkan || SelectedExe is not { Bitness: 32 or 64 } exe || _game is not { } game) return;

        (string? Nr, string? Feeder) availability;
        try
        {
            availability = await Task.Run(() => (
                exe.Bitness == 64 ? ReShadeVulkanLayer.UnavailableReason(exe.Path) : ReShadeVulkanLayer.Need64Bit,
                ReShadeVulkanLayer.FeederUnavailableReason(exe.Path)));
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[{game.Name}] Could not check the ReShade Vulkan layer: {ex.Message}");
            const string failed = "Could not check the ReShade Vulkan layer. Run the official ReShade installer with full add-on support for this executable, then rescan.";
            availability = (failed, failed);
        }
        if (read != _vulkanLayerRead || !ReferenceEquals(_game, game) || !ReferenceEquals(SelectedExe, exe) ||
            SelectedApi != RenderApi.Vulkan) return;

        (_vulkanNrUnavailable, _vulkanFeederUnavailable) = availability;
        RefreshRoutes(selectFromManifest);
        RefreshStatus();
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
            OptiBuildPanel.Visibility = opti ? Visibility.Visible : Visibility.Collapsed;
            ReShadeOptions.Visibility = opti ? Visibility.Collapsed : Visibility.Visible;
            OptiOptions.Visibility = opti ? Visibility.Visible : Visibility.Collapsed;

            var bits = SelectedExe?.Bitness ?? 0;
            var nrSource = NrSource;
            var routes = Installers.RoutesFor(SelectedApi, bits, _scan.HasNativeDlss, _scan.HasFrameGen,
                nrSource is not null, AddonIdentity.Read(nrSource, bits).Name, vulkanNrUnavailable: _vulkanNrUnavailable,
                vulkanFeederUnavailable: _vulkanFeederUnavailable);
            var previous = (RouteBox.SelectedItem as ComboBoxItem)?.Tag as Route?;
            var reshadeRoutes = routes.Where(r => r.Route is not (Route.OptiScaler or Route.NeuralMaster or Route.MfgOnly)).ToList();
            // Rebuild the list only when the available routes changed. Rebuilding a ComboBox's items while it is
            // handling the user's pick (RouteBox_SelectionChanged) makes WinUI fail fast with "Element not found".
            var signature = string.Join("|", reshadeRoutes.Select(r => $"{r.Route}:{r.Label}:{r.Unavailable}"));
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
                var wanted = RecommendedGameRoute(routes, bits);
                if (selectFromManifest && reshadeRoutes.FirstOrDefault(r => Installers.RouteKey(r.Route) == _scan.Manifest?.Route) is { } installed)
                    wanted = installed.Route;
                else if (previous is { } p && routes.Any(r => r.Route == p && r.Unavailable is null)) wanted = p;
                var target = RouteBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (Route)i.Tag == wanted && i.IsEnabled)
                             ?? RouteBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.IsEnabled);
                if (!ReferenceEquals(RouteBox.SelectedItem, target)) RouteBox.SelectedItem = target;
            }

            ProxyPanel.Visibility = SelectedApi == RenderApi.DX11 ? Visibility.Visible : Visibility.Collapsed;
            _neuralMasterConflict = NeuralMasterProtection.Conflict(_scan);
            var mfgWhy = Installers.MfgUnavailable(SelectedApi, bits, _scan.HasFrameGen);
            MfgBox.IsEnabled = _neuralMasterConflict is null && mfgWhy is null;
            var mfgInstallWhy = _neuralMasterConflict ?? mfgWhy ?? (SelectedExe is { } mfgExe
                ? Installers.MfgOnlyConflict(_scan.Dir, mfgExe.Path, _scan.Manifest) : "Choose a game executable first.");
            _canInstallMfg = mfgInstallWhy is null && !_folderGone;
            InstallMfgButton.IsEnabled = !_busy && _canInstallMfg;
            var mfgVersion = Updates.InUse(Updates.Sources.First(s => s.Id == "mfgunlock")).Version;
            InstallMfgButton.Content = _scan.Manifest?.MfgUnlock is { } installedMfg
                ? Scanner.CompareVersions(installedMfg, mfgVersion) < 0 ? "Update MFG unlock" : "Reinstall MFG unlock"
                : "Install MFG unlock";
            ToolTipService.SetToolTip(InstallMfgButton, mfgInstallWhy ?? "Apply MFG unlock to this game without changing its rendering route.");
            if (mfgWhy is not null) MfgBox.IsChecked = false;
            MfgNote.Text = mfgWhy ?? ((_scan.AntiCheat ? "Off by default in games with anti-cheat: it hooks deep into the game, which anti-cheat may block or answer with a crash. " : "") +
                (Gpu.HasRtx40 ? ""
                : Gpu.HasRtx ? $"On {Gpu.Nvidia}, choose XeMFG (XeSS frame generation 2x–6x) under General → Frame generation method in the add-on's in-game panel; it installs Intel's files itself. The default DLSS-G mode needs an RTX 40 card."
                : $"Needs a GeForce RTX 20-series or newer card; this PC has {Gpu.Nvidia ?? "no NVIDIA GPU"}.")).TrimEnd();
            MfgNote.Visibility = MfgNote.Text == "" ? Visibility.Collapsed : Visibility.Visible;
            ApplyKeyButton.IsEnabled = _neuralMasterConflict is null && _scan.OptiScalerIni;

            // Description + warnings for the current choice.
            var chosen = opti ? routes.First(r => r.Route == Route.OptiScaler) : routes.FirstOrDefault(r => (RouteBox.SelectedItem as ComboBoxItem)?.Tag is Route rt && r.Route == rt);
            RouteDescription.Text = chosen?.Description ?? "";
            bool feeder = chosen?.Route == Route.Feeder;
            FeederBackendPanel.Visibility = feeder ? Visibility.Visible : Visibility.Collapsed;
            if (selectFromManifest && _game is { } game)
            {
                var preferred = AppSettings.FeederBackendFor(_settings.OverrideOf(game.Entry), _scan.Manifest);
                FeederBackendBox.SelectedIndex = preferred == FeederBackend.Lecram ? 1 : 0;
                if (_settings.TryRememberInstalledFeederBackend(game.Entry, _scan.Manifest))
                {
                    try { _settings.Save(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { AppPaths.Log($"Could not save the installed Feeder backend for {game.Name}: {e.Message}"); }
                }
            }
            var feederProblem = feeder && SelectedFeederBackend == FeederBackend.Lecram
                ? Installers.FeederLecramUnavailable(SelectedApi, bits, nrSource) : null;
            FeederBackendNote.Text = feederProblem ?? (SelectedFeederBackend == FeederBackend.Lecram
                ? "Uses the included Feeder with your local DLSS5 NR add-on. Open the add-on panel for extra neural settings."
                : "");
            FeederBackendNote.Visibility = FeederBackendNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            // The library was read before the game moved (Steam's Move install folder) or was uninstalled: nothing else here
            // means anything until the library is read again.
            string? warning = _folderGone && _game is not null
                ? $"This game's folder is gone: {_game.Entry.Dir}. It was moved or uninstalled. Rescan the library to find it in its new place" +
                  (_game.Entry.Launcher == "Manual" ? "; a game you added by hand needs adding again with + Add a game." : ".")
                : chosen?.Unavailable;
            if (warning is null && !opti && chosen is null)
                warning = (SelectedApi switch
                {
                    RenderApi.Vulkan => routes.FirstOrDefault(r => r.Route == Route.NrAddon)?.Unavailable ??
                        routes.First(r => r.Route == Route.Uplift).Unavailable,
                    RenderApi.OpenGL => routes.First(r => r.Route == Route.Feeder).Unavailable,
                    _ => null,
                }) ?? "No ReShade route fits this game's renderer.";
            if (warning is null && opti && (OptiBuildBox.SelectedItem as ComboBoxItem)?.Tag is OptiBuild { NeuralRendering: true } && !_scan.HasNativeDlss)
                warning = "DLSS-NR OptiScaler builds need a game that ships its own DLSS. Pick the custom (official) build, or a ReShade route.";
            if (warning is null && chosen?.Route == Route.Feeder)
                warning = Installers.FeederStandsAside(SelectedApi, _scan.HasNativeDlss);
            // ReShade could not start this install's add-ons in the last run (Phantom Blade Zero): offer to load them early.
            bool loadEarly = warning is null && _addonsToLoadEarly.Count > 0;
            if (loadEarly)
                warning = $"ReShade could not start {string.Join(" and ", _addonsToLoadEarly)} in this game (error 1114 in ReShade.log). " +
                          "Having ReShade load them as soon as it starts fixes this in some games.";
            // Frame generation fed too few real frames flickers (CoD at 15 fps): say so, with the numbers from the last run.
            if (warning is null && _slowFrameGen is { } slow)
                warning = $"Last run: about {slow.FpsWithNr} real frames a second with DLSS 5 ({slow.NrMs.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} ms of each for DLSS 5) " +
                          $"and {slow.FrameGen}x frame generation on. Frame generation needs about 60 real frames a second to look clean; below that it can flicker. " +
                          "Use DLSS Quality in the game, or fewer passes or a lower resolution scale in the DLSS 5 add-on.";
            bool otherApp = _scan.OtherAppBackup || _scan.OtherAppRecord;
            if (otherApp)
                warning = "Another app has an install in this game. Remove it before installing with DLSS 5 Master.";
            // DLSS5 NR's own setup installed NR here (Onimusha: Way of the Sword, Like a Dragon: Ishin!, over a Native install): it
            // goes first, removed here or by its own Remove NR.
            bool nrSetup = !otherApp && SelectedExe is not null && NrAddon.SetupInstalled(Path.GetDirectoryName(SelectedExe.Path)!);
            if (nrSetup)
            {
                var setupName = NrAddon.SetupName(Path.GetDirectoryName(SelectedExe!.Path)!);
                warning = Installers.NrSetupAfterUs(_scan.Dir) ? NrAddon.SetupAfterUsMessage(setupName) : NrAddon.SetupInstalledMessage(setupName);
                if (_removeNrButton is not null) _removeNrButton.Content = $"Remove {setupName}";
            }
            // ReShade that nobody recorded (installed by hand or by an older tool): offer to move it out of the game.
            bool unrecorded = !otherApp && _scan.Manifest is null && _scan.ReShade.Installed && _scan.ReShade.File is not null;
            _unrecordedReShadeWarning = null;
            if (unrecorded && warning is null && _neuralMasterConflict is null)
                _unrecordedReShadeWarning = SelectedApi == RenderApi.Vulkan && chosen?.Route is Route.NrAddon or Route.Feeder or Route.Uplift
                    ? $"A separate ReShade DLL ({_scan.ReShade.File}) is already beside the game. The selected route uses the registered Vulkan layer."
                    : $"ReShade {_scan.ReShade.Version} is already in this game, and no app has a record of it. Remove it to start clean, or install over it.";
            if (warning is null && _components.RuntimeDir is null && !(opti && (OptiBuildBox.SelectedItem as ComboBoxItem)?.Tag is OptiBuild { NeuralRendering: false }))
                warning = "No NVIDIA runtime folder found. Set it in Settings.";
            if (warning is null && SelectedApi == RenderApi.Unknown)
                warning = "The renderer could not be detected. Choose the Rendering API by hand.";
            _gameRouteWarning = warning;
            _gameRouteWarningAction = _folderGone ? _rescanLibraryButton
                : _neuralMasterConflict is not null ? null
                : otherApp ? _removeOtherButton
                : nrSetup ? _removeNrButton
                : loadEarly ? _loadEarlyButton : null;
            RefreshGameRouteWarning();
            _canInstall = _neuralMasterConflict is null && feederProblem is null && SelectedExe is not null &&
                (opti ? chosen?.Unavailable is null : RouteBox.SelectedItem is not null);
            InstallButton.IsEnabled = !_busy && _canInstall;
            RestoreButton.IsEnabled = !_busy && _scan.Manifest is not null && _neuralMasterConflict is null;
            InstallButton.Content = _scan.Manifest?.Route is null or "mfgonly" ? "Set up DLSS 5" : "Reinstall";
            RenderRouteCards(routes, opti, bits);
        }
        finally { _populating = false; }
    }

    private void RefreshGameRouteWarning()
    {
        // The game-specific bar explains a detected NeuralMaster setup without a duplicate warning.
        var conflict = !_neuralMasterReading && _neuralMasterStatus is null ? _neuralMasterConflict : null;
        bool showUnrecordedReShade = CanReviewUnrecordedReShade();
        var warning = conflict ?? (showUnrecordedReShade ? _unrecordedReShadeWarning : _gameRouteWarning);
        bool externalSetupRepair = _neuralMasterStatus?.ExternalSetupRepairFingerprint is not null &&
            !_neuralMasterReading && !_neuralMasterWorking && _scan is { OtherAppBackup: false, OtherAppRecord: false };
        if (externalSetupRepair)
            warning = "DLSS5 NR's setup replaced NeuralMaster's ReShade host. Remove DLSS5 NR to restore the verified host.";
        else if (_neuralMasterConflict is not null && SelectedExe is { } exe && NrAddon.SetupInstalled(Path.GetDirectoryName(exe.Path)!))
            warning = "DLSS5 NR's own setup record is present. Use Remove NR in that setup if DLSS 5 Master cannot verify the files needed for recovery.";
        RouteWarning.Message = warning ?? "";
        RouteWarning.IsOpen = warning is not null;
        RouteWarning.ActionButton = externalSetupRepair ? (_busy ? null : _removeNrButton)
            : showUnrecordedReShade ? (_busy ? null : _removeReShadeButton) : _gameRouteWarningAction;
    }

    private bool CanReviewUnrecordedReShade() =>
        _unrecordedReShadeWarning is not null && !_neuralMasterReading && !_neuralMasterWorking &&
        _scan is { ReShade.File: { } hook } scan && SelectedExe?.Path is { } exe &&
        !IsVerifiedNeuralMasterHost(_neuralMasterStatus, scan.Dir, exe, hook);

    private static bool IsVerifiedNeuralMasterHost(NeuralMasterSnapshotStatus? status, string gameDir, string exe, string hook)
    {
        if (status is not { VerifiedHostPaths.Count: > 0 }) return false;
        var relative = Path.GetRelativePath(gameDir, Path.Combine(Path.GetDirectoryName(exe)!, hook));
        return status.VerifiedHostPaths.Contains(relative, StringComparer.OrdinalIgnoreCase);
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
        if (_scan.AntiCheatName is { } ac) AddChip(ac.Length <= 24 ? "Anti-cheat: " + ac : "Anti-cheat");

        StatusRows.Children.Clear();
        AddRow(StatusRows, "Architecture", exe is null ? "—" : exe.Bitness == 0 ? "unknown" : $"{exe.Bitness}-bit");
        AddRow(StatusRows, "Rendering API", Scanner.Label(SelectedApi), SelectedApi == RenderApi.Unknown ? "WarnText" : "AccentText");
        var m = _scan.Manifest;
        AddRow(StatusRows, "Installed backend (this app)", Installers.InstalledRouteLabel(m), m is null ? null : "AccentText");
        if (m is not null)
            AddRow(StatusRows, "Installed on", m.Date.ToLocalTime().ToString("d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture) + $"  ·  {m.Added.Count} added, {m.Replaced.Count} replaced");
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
        AddRow(StatusRows, "MFG unlock", mfgHere ? (m?.MfgUnlock is { } mv ? $"Installed · {mv}" : "Installed (other app)") : "not installed", mfgHere ? "AccentText" : null);
        var rs = _scan.ReShade;
        bool vulkan = SelectedApi == RenderApi.Vulkan;
        if (vulkan)
        {
            bool ready = _vulkanFeederUnavailable is null || _vulkanNrUnavailable is null;
            bool checking = exe?.Bitness is 32 or 64 && _vulkanFeederUnavailable == CheckingVulkanLayer;
            AddRow(StatusRows, "ReShade Vulkan layer", ready ? "Ready · full add-on support" : checking ? "Checking…" : "Unavailable",
                ready ? "AccentText" : checking ? null : "WarnText");
        }
        if (!vulkan || rs.Installed)
            AddRow(StatusRows, vulkan ? "Local ReShade" : "ReShade",
                rs.Installed ? $"{rs.Version}{(rs.AddonSupport ? " + add-on" : " (no add-on support)")}  ·  {rs.File}" : "not installed",
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
            var swap = new DropDownButton { Content = "Swap", Flyout = flyout, Margin = new Thickness(8, 0, 0, 0), IsEnabled = _neuralMasterConflict is null };
            AddRow(DllRows, $"{Scanner.KindLabel(file.Kind)}  ·  {file.Rel}", $"{file.Version ?? "?"}  ·  {(file.Bitness == 0 ? "?" : file.Bitness + "-bit")}", null, swap);
        }
    }

    // ------------------------------------------------------------------ selection changes

    private async void ExeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || _busy || _neuralMasterWorking || _scan is null || _game is null || SelectedExe is not { } exe) return;
        ResetNeuralMasterGameBar(checking: true);
        // The scan is the tile's too: its .exe, API and state follow the choice, and so does Set up several games.
        _scan.Primary = exe;
        Scanner.InspectExeDir(_scan, Path.GetDirectoryName(exe.Path)!);
        _game.SetScan(_scan);
        _settings.OverrideFor(_game.Entry).Exe = exe == _scan.Executables.FirstOrDefault() ? null : exe.Path;
        _settings.Save();
        _populating = true;
        BuildApiBox(_settings.OverrideOf(_game.Entry)?.Api);
        _populating = false;
        var routeCheck = RefreshExecutableRoutesAsync();
        RefreshStatus();
        await Task.WhenAll(routeCheck, RefreshNeuralMasterStatusAsync());
    }

    private async void ApiBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || _scan is null || _game is null) return;
        _settings.OverrideFor(_game.Entry).Api = (ApiBox.SelectedItem as ComboBoxItem)?.Tag is RenderApi a ? a.ToString() : null;
        _settings.Save();
        var routeCheck = RefreshExecutableRoutesAsync();
        RefreshStatus();
        await routeCheck;
    }

    private void BackendBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_populating) RefreshRoutes(); }
    private void RouteBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_populating) RefreshRoutes(); }
    private void OptiBuildBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_populating) RefreshRoutes(); }

    private async void ChangeExe_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _neuralMasterWorking || _game is not { } game) return;
        var file = await PickFileAsync(".exe");
        if (file is null || _busy || _neuralMasterWorking || !ReferenceEquals(game, _game)) return;
        var root = Path.GetFullPath(_game.Entry.Dir).TrimEnd('\\') + "\\";
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            Log($"Pick an .exe inside {_game.Entry.Dir}. For a game elsewhere, add it with + on the library.");
            return;
        }
        _settings.OverrideFor(_game.Entry).Exe = file;
        _settings.Save();
        await RescanAsync();
    }

    private async void HideGame_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _neuralMasterWorking || _game is null) return;
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
        ApplyFilter(force: true);
    }

    // ------------------------------------------------------------------ actions

    private void Log(string line)
    {
        void Append() { LogBox.Text += (LogBox.Text.Length > 0 ? "\n" : "") + line; LogBox.SelectionStart = LogBox.Text.Length; }
        if (DispatcherQueue.HasThreadAccess) Append(); else DispatcherQueue.TryEnqueue(Append);
    }

    /// <summary>Whether the page's choices can be set up at all, as RefreshRoutes last worked it out.</summary>
    private bool _canInstall;
    private bool _canInstallMfg;
    private string? _neuralMasterConflict;

    private void SetBusy(bool busy, string text = "")
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyText.Text = text;
        InstallButton.IsEnabled = !busy && _canInstall;
        InstallMfgButton.IsEnabled = !busy && _canInstallMfg;
        RestoreButton.IsEnabled = !busy && _scan?.Manifest is not null && _neuralMasterConflict is null;
        UpdateNeuralMasterButtons();
        RefreshGameRouteWarning();
    }

    /// <summary>The player's copy of DLSS5 NR for this game (<see cref="NrAddon.Source"/>); its route shows only with one.</summary>
    private string? NrSource => _scan is null || SelectedExe is null ? null
        : NrAddon.Source(_settings.NrFolder, _scan.Dir, Path.GetDirectoryName(SelectedExe.Path)!, _scan.Manifest);

    private FeederBackend SelectedFeederBackend => FeederBackendBox.SelectedIndex == 1 ? FeederBackend.Lecram : FeederBackend.RenoDx;

    private void FeederBackendBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || _game is null || _scan is null || FeederBackendBox.SelectedIndex is not (0 or 1)) return;
        var choices = _settings.OverrideFor(_game.Entry);
        var previous = choices.FeederBackend;
        if (previous != SelectedFeederBackend)
        {
            choices.FeederBackend = SelectedFeederBackend;
            try { _settings.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                choices.FeederBackend = previous;
                _populating = true;
                try { FeederBackendBox.SelectedIndex = AppSettings.FeederBackendFor(choices, _scan.Manifest) == FeederBackend.Lecram ? 1 : 0; }
                finally { _populating = false; }
                RefreshRoutes();
                FeederBackendNote.Text = "Could not save the Feeder backend. " + ex.Message;
                FeederBackendNote.Visibility = Visibility.Visible;
                return;
            }
        }
        RefreshRoutes();
    }

    /// <summary>
    /// DLSS5 NR is in the game and another ReShade route is chosen: next to it, NR switches itself off or runs a second
    /// neural pass. Asks whether to use NR's own route instead or set NR aside. The route to set up, or null for Cancel.
    /// </summary>
    private async Task<Route?> AskAboutNrAddonAsync(Route route, string exeDir)
    {
        var here = new[] { NrAddon.Addon64, NrAddon.Addon32 }.Select(a => Path.Combine(exeDir, a)).FirstOrDefault(File.Exists);
        if (route is Route.NrAddon or Route.MfgOnly || here is null || _game is null) return route;
        var version = Pe.GetFileVersion(here);
        var name = NrAddon.SetupName(exeDir);
        var routes = Installers.RoutesFor(SelectedApi, SelectedExe?.Bitness ?? 0, _scan?.HasNativeDlss == true,
            _scan?.HasFrameGen == true, nrCopy: true, vulkanNrUnavailable: _vulkanNrUnavailable,
            vulkanFeederUnavailable: _vulkanFeederUnavailable);
        var why = routes.First(r => r.Route == Route.NrAddon).Unavailable;
        var label = routes.FirstOrDefault(r => r.Route == route)?.Label ?? Installers.RouteLabel(Installers.RouteKey(route));
        var text = $"{_game.Name} has {name}{(version is null ? "" : " " + version)}. Next to {label} it switches itself off or runs a second neural pass, so it cannot stay as it is.\n\n" +
                   (why is null
                       ? $"Use {name}: set up its own route instead ({(NrAddon.IsCopy(_settings.NrFolder) ? "your copy from Settings" : NrAddon.DropCopy() is not null ? "your copy in DLSS 5 Master's folder" : "the copy in this game")}).\n\n"
                       : $"{name} cannot be set up by DLSS 5 Master here: {why}\n\n") +
                   $"Set it aside: set up {label}. {name} goes into this install's backup, and Restore originals puts it back.";
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"{name} is in this game",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = text },
            PrimaryButtonText = $"Use {name}",
            IsPrimaryButtonEnabled = why is null,
            SecondaryButtonText = "Set it aside",
            CloseButtonText = "Cancel",
            DefaultButton = why is null ? ContentDialogButton.Primary : ContentDialogButton.Secondary,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => Route.NrAddon,
            ContentDialogResult.Secondary => route,
            _ => null,
        };
    }

    /// <summary>ARK: Survival Ascended's own Steam launch choice without BattlEye, for the anti-cheat notes; "" for other games.</summary>
    private string ArkNote() => _game?.Entry is { Launcher: "Steam", Id: "2399830" }
        ? "Steam also offers \"Play ARK: No BattlEye AntiCheat, Unofficial Servers Only\", which starts ARK without BattlEye. " +
          "Start it that way every time while DLSS 5 is in the game: the normal Play ARK runs BattlEye with it."
        : "";

    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallGameAsync();

    private async void InstallMfg_Click(object sender, RoutedEventArgs e) => await InstallGameAsync(mfgOnly: true);

    private async Task InstallGameAsync(bool mfgOnly = false)
    {
        if (_busy || _scan is null || _game is null || SelectedExe is null || (mfgOnly && !_canInstallMfg)) return;
        bool opti = !mfgOnly && BackendBox.SelectedIndex == 1;
        var build = (OptiBuildBox.SelectedItem as ComboBoxItem)?.Tag as OptiBuild;
        var route = mfgOnly ? Route.MfgOnly
            : opti ? Route.OptiScaler : (RouteBox.SelectedItem as ComboBoxItem)?.Tag as Route? ?? Route.Native;
        if (SelectedApi == RenderApi.Unknown) { Log("Choose the Rendering API first."); return; }
        if (!mfgOnly && NrAddon.SetupInstalled(Path.GetDirectoryName(SelectedExe.Path)!))
        {
            var setupName = NrAddon.SetupName(Path.GetDirectoryName(SelectedExe.Path)!);
            var blocked = await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = $"Could not {(_scan.Manifest is null ? "set up" : "reinstall")} {_game.Name}",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Installers.NrSetupAfterUs(_scan.Dir) ? NrAddon.SetupAfterUsMessage(setupName) : NrAddon.SetupInstalledMessage(setupName) },
                PrimaryButtonText = $"Remove {setupName}",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
            // Same removal as the banner button: it asks once more, then undoes DLSS5 NR's setup from its own record.
            if (blocked == ContentDialogResult.Primary) RemoveNrSetup_Click(this, new RoutedEventArgs());
            return;
        }
        if (!opti && !mfgOnly && !(route == Route.Feeder && SelectedFeederBackend == FeederBackend.Lecram))
        {
            if (await AskAboutNrAddonAsync(route, Path.GetDirectoryName(SelectedExe.Path)!) is not { } chosen) return;
            if (chosen != route) { route = chosen; PickRoute(route); }
        }
        // The anti-cheat box is easy to miss, and the install only logged its refusal: ask here instead.
        if (_scan.AntiCheatName is { } antiCheat && AntiCheatBox.IsChecked != true)
        {
            var action = _scan.Manifest is null ? "Set up" : "Reinstall";
            var text = Installers.AntiCheatWarning(antiCheat) +
                       (_scan.Manifest is null ? "" : "\n\nDLSS 5 Master is already in this game: Cancel leaves it there, and Restore originals takes it out.") +
                       (ArkNote() is { Length: > 0 } ark ? "\n\n" + ark : "");
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = $"{action} {_game.Name} anyway?",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = text },
                PrimaryButtonText = $"{action} anyway",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            AntiCheatBox.IsChecked = true;
        }
        var req = new InstallRequest
        {
            GameDir = _scan.Dir,
            ExePath = SelectedExe.Path,
            Api = SelectedApi,
            Bitness = SelectedExe.Bitness,
            Route = route,
            OptiBuild = build,
            OptiHook = OptiHookBox.SelectedItem as string ?? "dxgi.dll",
            MenuKey = (MenuKeyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "0x2D",
            ReShadeProxy = (ProxyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "dxgi.dll",
            MfgUnlock = route == Route.MfgOnly || (MfgBox.IsEnabled && MfgBox.IsChecked == true),
            LoadAddonsEarly = EarlyAddonsBox.IsChecked == true,
            AntiCheatAcknowledged = AntiCheatBox.IsChecked == true,
            FeederBackend = route == Route.Feeder ? SelectedFeederBackend : FeederBackend.Automatic,
            NrAddonSource = route == Route.NrAddon || route == Route.Feeder && SelectedFeederBackend == FeederBackend.Lecram ? NrSource : null,
        };
        if (opti) { _settings.OptiScalerMenuKey = req.MenuKey; _settings.Save(); }

        LogBox.Text = "";
        Log($"Installing {(opti ? build?.Label : Installers.RouteLabel(Installers.RouteKey(route)))} for {_game.Name} ({Path.GetFileName(req.ExePath)}, {Scanner.Label(req.Api)}, {req.Bitness}-bit)…");
        SetBusy(true, "Installing…");
        _installCts = new CancellationTokenSource();
        var progress = new Progress<string>(t => BusyText.Text = t);
        string? failure = null;
        try
        {
            await Installers.InstallAsync(req, _components, Log, progress, _installCts.Token);
        }
        catch (Exception ex)
        {
            Log("✖ " + ex.Message);
            AppPaths.Log($"[{_game.Name}] install failed: {ex}");
            if (Journal.ReadManifest(_scan.Dir) is not null) Log("Files changed before the failure are recorded; Restore originals undoes them.");
            if (ex is not OperationCanceledException) failure = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
        // The log sits at the bottom of the page: a refusal shown only there looks like nothing happened.
        if (failure is not null)
            await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = mfgOnly ? "Could not apply MFG unlock" : $"Could not {(_scan.Manifest is null ? "set up" : "reinstall")} {_game.Name}",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = failure },
                CloseButtonText = "OK",
            }.ShowAsync();
        var log = LogBox.Text;
        await RescanAsync();
        LogBox.Text = log;
    }

    /// <summary>
    /// Reads the library again and opens the same game where it is now (a moved Steam game keeps its app ID); reading the
    /// library has moved the choices made on its page there too.
    /// </summary>
    private async void RescanLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || _busy) return;
        var gone = _game.Entry;
        ClosePanel();
        await LoadLibraryAsync();
        // A game added by hand is not found again (its page says to add it again): another one by that name is not it.
        if (gone.Launcher == "Manual") return;
        var found = _all.FirstOrDefault(i => i.Entry.Launcher == gone.Launcher && Directory.Exists(i.Entry.Dir)
            && (gone.Id is not null ? i.Entry.Id == gone.Id : i.Entry.Name.Equals(gone.Name, StringComparison.OrdinalIgnoreCase)));
        if (found is not null) await OpenGameAsync(found);
    }

    private void EarlyAddonsBox_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null || _game is null) return;
        _settings.OverrideFor(_game.Entry).LoadAddonsEarly = EarlyAddonsBox.IsChecked == true ? true : null;
        _settings.Save();
    }

    private async void LoadEarly_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null || _game is null || _busy) return;
        var dir = _scan.Dir;
        var entry = _game.Entry;
        LogBox.Text = "";
        SetBusy(true, "Changing ReShade.ini…");
        try
        {
            await Task.Run(() => Installers.LoadAddonsEarly(dir, Log));
            // Later setups of this game do the same.
            _settings.OverrideFor(entry).LoadAddonsEarly = true;
            _settings.Save();
            Log("Start the game again. Restore originals undoes this.");
        }
        catch (Exception ex) { Log("✖ " + ex.Message); }
        finally { SetBusy(false); }
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

    /// <summary>Undo DLSS5 NR's setup install in this game from its own record and backups, keeping the player's ReShade settings.</summary>
    private async void RemoveNrSetup_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null || _busy || SelectedExe is null) return;
        var dir = _scan.Dir;
        var exe = SelectedExe.Path;
        if (_neuralMasterStatus?.ExternalSetupRepairFingerprint is { } fingerprint)
        {
            if (_neuralMasterReading || _neuralMasterWorking || _bulkRunning || _appUpdating) return;
            var confirm = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Remove DLSS5 NR and recover NeuralMaster?",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "The matching ReShade host will be restored from DLSS5 NR's backup. " +
                           "The removed add-on and replaced host are archived in _DLSS5Master_Backup. " +
                           "Your current settings, NVIDIA runtime, and NeuralMaster backups stay in place."
                },
                PrimaryButtonText = "Remove DLSS5 NR",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary || _busy || _bulkRunning || _appUpdating ||
                _scan?.Dir != dir || SelectedExe?.Path != exe) return;
            LogBox.Text = "";
            SetBusy(true, "Restoring NeuralMaster's ReShade host…");
            var messages = new List<string>();
            try { await Task.Run(() => _neuralMasterSnapshots.RemoveExternalSetupLayer(dir, exe, fingerprint, messages.Add)); }
            catch (Exception ex) { messages.Add("✖ " + ex.Message); }
            finally { SetBusy(false); }
            await RescanAsync();
            foreach (var message in messages) Log(message);
            return;
        }
        var exeDir = Path.GetDirectoryName(exe)!;
        LogBox.Text = "";
        SetBusy(true, "Checking the backups…");
        NrAddon.SetupInstall si;
        try { si = await Task.Run(() => NrAddon.ReadSetupInstall(exeDir)); }
        finally { SetBusy(false); }
        if (si.Problem is not null)
        {
            Log("✖ " + si.Problem + " Nothing was changed.");
            await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = $"Could not remove {si.Name}",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = si.Problem + " Nothing was changed." },
                CloseButtonText = "OK",
            }.ShowAsync();
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"Remove {si.Name} from this game?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"This undoes the setup of {si.Name}{(si.Version is null ? "" : " " + si.Version)} in {_game?.Name ?? "this game"} from its own record " +
                       $"and backups: {si.PutBack.Count} original file(s) go back (each checked against the record) and the {si.Delete.Count} file(s) " +
                       "it added are deleted. Your ReShade settings stay.\n\n" +
                       "Its record and backups move to _DLSS5Master_Backup\\removed in the game folder, so nothing of it is lost.",
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true, $"Removing {si.Name}…");
        try { await NrAddon.RemoveSetupInstallAsync(dir, exe, Log); }
        catch (Exception ex) { Log("✖ " + ex.Message); }
        finally { SetBusy(false); }
        var log = LogBox.Text;
        await RescanAsync();
        LogBox.Text = log;
    }

    /// <summary>Move a ReShade install that no app recorded out of the game, into a dated folder in _DLSS5Master_Backup.</summary>
    private async void RemoveUnrecordedReShade_Click(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game || _busy || !CanReviewUnrecordedReShade() || SelectedExe?.Path is not { } exe ||
            _scan is not { ReShade.File: { } hook } scan || _removeReShadeButton is not { IsEnabled: true } button) return;
        int read = _neuralMasterRead;
        var dir = scan.Dir;
        var exeDir = Path.GetDirectoryName(exe)!;
        bool IsCurrentCleanupTarget() => read == _neuralMasterRead && IsCurrentNeuralMasterGame(game, exe) &&
            GameOverlay.Visibility == Visibility.Visible && !_busy && CanReviewUnrecordedReShade() &&
            string.Equals(_scan?.ReShade.File, hook, StringComparison.OrdinalIgnoreCase);

        List<string> items;
        button.IsEnabled = false;
        try
        {
            var status = await InspectNeuralMasterAsync(dir, exe, scan.Manifest);
            if (!IsCurrentCleanupTarget()) return;
            if (IsVerifiedNeuralMasterHost(status, dir, exe, hook))
            {
                DisplayNeuralMasterStatus(status);
                RefreshGameRouteWarning();
                return;
            }
            items = await Task.Run(() => ForeignInstalls.UnrecordedReShadeFiles(exeDir, hook));
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[{game.Name}] Could not check ReShade before removal: {ex.Message}");
            if (IsCurrentCleanupTarget()) Log("✖ Could not verify the ReShade files. Rescan this game and try again.");
            return;
        }
        finally { button.IsEnabled = true; }
        if (items.Count == 0 || !IsCurrentCleanupTarget()) return;
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
                    Text = $"No app has a record of this ReShade install, so DLSS 5 Master moves its files out of {game.Name} " +
                           "instead of deleting them. They go to _DLSS5Master_Backup\\removed in the game folder, so you can put them back.\n\n" +
                           string.Join("\n", items.Select(i => "• " + Path.GetFileName(i) + (Directory.Exists(i) ? "\\" : ""))) +
                           "\n\nThe game's own files are not touched.",
                }
            },
            PrimaryButtonText = "Remove ReShade",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !IsCurrentCleanupTarget()) return;

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
        var picked = await PickFileAsync(".dll");
        if (picked is null) return;
        string source;
        try { source = Installers.ImportDll(picked); }
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
        if (_game is null || _neuralMasterWorking) return;
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
