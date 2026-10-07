using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using DLSS5Master.Core;
using DLSS5Master.Views;
using Windows.Storage.Pickers;

namespace DLSS5Master;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Components _components;
    private readonly List<GameItem> _all = new();
    private readonly ObservableCollection<GameItem> _visible = new();
    private CancellationTokenSource? _scanCts;

    public MainWindow()
    {
        InitializeComponent();
        _components = new Components(_settings);
        SizeToScreen();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "TitleBar.ico");   // badge raised to line up with the title text
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        // No separate Windows title bar: the header row (logo, tabs, tools) is the top of the window,
        // with Windows' own minimize/maximize/close drawn over its right end.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var tb = AppWindow.TitleBar;
        tb.PreferredHeightOption = TitleBarHeightOption.Tall;
        tb.ButtonBackgroundColor = tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonForegroundColor = tb.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 150, 160);
        tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 24, 40, 56);
        tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 34, 58, 80);
        TitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        // The tab row re-centres when a label changes ("SETTINGS ●"); keep its clickable area in step.
        Tabs.SizeChanged += (_, _) => UpdateTitleBarRegions();
        GameTools.SizeChanged += (_, _) => UpdateTitleBarRegions();
        AppUpdateButton.SizeChanged += (_, _) => UpdateTitleBarRegions();
        AppUpdateButton.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateTitleBarRegions());
        Tabs.LayoutUpdated += (_, _) => { if (Tabs.ActualOffset != _tabsOffset) { _tabsOffset = Tabs.ActualOffset; UpdateTitleBarRegions(); } };
        GameOverlay.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateTitleBarRegions());
        GameTools.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateTitleBarRegions());
        GameGrid.ItemsSource = _visible;
        NoticesText.Text = Notices;
        AboutCoffeeHost.Content = CoffeeButton();
        InitGamePanel();
        Root.Loaded += async (_, _) =>
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
            BackgroundArt.Source = await LoadImageAsync(Path.Combine(assets, "Background.jpg"), 2560);
            var logo = Path.Combine(assets, "Logo.png");
            HeaderLogo.Source = await LoadImageAsync(logo, 400);
            AboutLogo.Source = await LoadImageAsync(logo, 600);
            CoffeeQr.Source = await LoadImageAsync(Path.Combine(assets, "BmcQr.png"), 400);
            UpdateTitleBarRegions();
            _ = AutoCheckUpdatesAsync();
            await LoadLibraryAsync();
        };
    }

    private nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);
    private System.Numerics.Vector3 _tabsOffset;

    /// <summary>
    /// The header is the window's drag area. Keep room for the caption buttons on the right, and let clicks
    /// reach the tabs, the game tools and (while it is open) the game panel instead of dragging the window.
    /// </summary>
    private void UpdateTitleBarRegions()
    {
        if (TitleBar.XamlRoot is null) return;
        double scale = TitleBar.XamlRoot.RasterizationScale;
        TitleBar.Padding = new Thickness(20, 0, AppWindow.TitleBar.RightInset / scale + 8, 0);

        var rects = new List<Windows.Graphics.RectInt32>();
        // Caption buttons occupy the top band; never cover them with a clickable region.
        double captionBottom = AppWindow.TitleBar.Height / scale;
        void Add(FrameworkElement e, bool belowCaption = false)
        {
            if (e.Visibility != Visibility.Visible || e.ActualWidth <= 0 || !e.IsHitTestVisible) return;
            var r = e.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
            if (belowCaption && r.Y < captionBottom) { r.Height -= captionBottom - r.Y; r.Y = captionBottom; }
            if (r.Height <= 0) return;
            rects.Add(new Windows.Graphics.RectInt32((int)(r.X * scale), (int)(r.Y * scale), (int)Math.Ceiling(r.Width * scale), (int)Math.Ceiling(r.Height * scale)));
        }
        Add(Tabs);
        Add(AppUpdateButton);
        Add(GameTools);
        if (GameOverlay.Visibility == Visibility.Visible) Add(GamePanel, belowCaption: true);
        Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, rects.ToArray());
    }

    public const string CoffeeUrl = "https://www.buymeacoffee.com/Liongooder";

    /// <summary>Buy Me a Coffee button in BMC's own colours (yellow #FFDD00, black text and outline). Opens the page in the browser.</summary>
    private static Button CoffeeButton()
    {
        var yellow = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xDD, 0x00));
        var hover = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xE5, 0x40));
        var pressed = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE6, 0xC7, 0x00));
        var black = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black);
        var button = new Button
        {
            Background = yellow,
            Foreground = black,
            BorderBrush = black,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 8, 22, 8),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "☕", FontSize = 20, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock
                    {
                        Text = "Buy me a coffee",
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cookie, Segoe Script, Segoe Print"),
                        FontSize = 19,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = black,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        // Keep BMC's colours on hover/press instead of the theme's grey button states.
        foreach (var (key, brush) in new[] { ("ButtonBackground", yellow), ("ButtonBackgroundPointerOver", hover), ("ButtonBackgroundPressed", pressed) })
            button.Resources[key] = brush;
        foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" })
            button.Resources[key] = black;
        ToolTipService.SetToolTip(button, "Support DLSS 5 Master — buymeacoffee.com/Liongooder");
        button.Click += (_, _) => Process.Start(new ProcessStartInfo(CoffeeUrl) { UseShellExecute = true });
        return button;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>AppWindow sizes are physical pixels: scale 1480×960 by the monitor's DPI and fit the work area.</summary>
    private void SizeToScreen()
    {
        double scale = Math.Max(1.0, GetDpiForWindow(Hwnd) / 96.0);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1480 * scale), (int)(work.Width * 0.94));
        int h = Math.Min((int)(960 * scale), (int)(work.Height * 0.92));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = (int)(1000 * scale);
            p.PreferredMinimumHeight = (int)(640 * scale);
        }
    }

    // ------------------------------------------------------------------ library

    private async Task LoadLibraryAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = "Finding your games…";
        _all.Clear();
        _visible.Clear();
        List<GameEntry> games;
        try { games = await Task.Run(() => Library.Discover(_settings)); }
        catch (Exception e) { LoadingText.Text = "Could not read the game library: " + e.Message; return; }
        foreach (var g in games) _all.Add(new GameItem(g));
        ApplyFilter();
        LoadingPanel.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_all.Count == 0) { LoadingText.Text = "No games found. Add a library folder in Settings or a game with +."; return; }

        _ = LoadPostersAsync(_all.ToList(), ct);
        await ScanAllAsync(_all.ToList(), ct);
    }

    private static async Task<BitmapImage?> LoadImageAsync(string? path, int width)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var bmp = new BitmapImage { DecodePixelWidth = width };
            using var ms = new MemoryStream(bytes);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>The poster file to show: the user's own pick, Steam's local library image, or a downloaded one.</summary>
    private string? PosterPath(GameItem item)
    {
        var custom = _settings.Overrides.GetValueOrDefault(item.Entry.Dir)?.Poster;
        if (custom is not null && File.Exists(custom)) return custom;
        if (item.Entry.Poster is not null && File.Exists(item.Entry.Poster)) return item.Entry.Poster;
        return Artwork.Cached(item.Entry);
    }

    private async Task LoadPostersAsync(List<GameItem> items, CancellationToken ct)
    {
        var missing = new List<GameItem>();
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            var path = PosterPath(item);
            if (path is null) { missing.Add(item); continue; }
            item.Poster = await LoadImageAsync(path, 380);
        }
        // Games with no artwork yet: fetch from Steam in the background, a few at a time.
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(missing.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var path = await Artwork.FetchAsync(item.Entry, ct);
                if (path is not null && !ct.IsCancellationRequested) item.Poster = await LoadImageAsync(path, 380);
            }
            catch (OperationCanceledException) { }
            finally { gate.Release(); }
        }));
    }

    // ------------------------------------------------------------------ artwork menu (right-click on a game)

    private static GameItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as GameItem ?? (sender as FrameworkElement)?.DataContext as GameItem;

    private async void ChangeArtwork_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        // Keep a private copy so the artwork survives the original file being moved or deleted.
        Directory.CreateDirectory(Artwork.CacheDir);
        var copy = Path.Combine(Artwork.CacheDir, "custom-" + Guid.NewGuid().ToString("N")[..12] + Path.GetExtension(file.Path));
        File.Copy(file.Path, copy, true);
        _settings.OverrideFor(item.Entry.Dir).Poster = copy;
        _settings.Save();
        item.Poster = await LoadImageAsync(copy, 380);
    }

    private async void RefreshArtwork_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        Artwork.ClearCached(item.Entry);
        var path = await Artwork.FetchAsync(item.Entry);
        var custom = _settings.Overrides.GetValueOrDefault(item.Entry.Dir)?.Poster;
        item.Poster = await LoadImageAsync(custom is not null && File.Exists(custom) ? custom : path ?? item.Entry.Poster, 380);
    }

    private async void DefaultArtwork_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        _settings.OverrideFor(item.Entry.Dir).Poster = null;
        _settings.Save();
        var path = PosterPath(item) ?? await Artwork.FetchAsync(item.Entry);
        item.Poster = await LoadImageAsync(path, 380);
    }

    private async Task ScanAllAsync(List<GameItem> items, CancellationToken ct)
    {
        int done = 0;
        using var gate = new SemaphoreSlim(3);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var preferred = _settings.Overrides.GetValueOrDefault(item.Entry.Dir)?.Exe ?? item.Entry.Exe;
                var scan = await Task.Run(() => { try { return Scanner.Scan(item.Entry.Dir, preferred, ct); } catch { return null; } }, ct);
                item.SetScan(scan);
            }
            finally
            {
                gate.Release();
                ScanStatus.Text = ++done < items.Count ? $"Scanning games… {done}/{items.Count}" : $"{items.Count} games";
            }
        });
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        IEnumerable<GameItem> items = _all;
        if (q != "") items = items.Where(i => i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        items = FilterBox.SelectedIndex switch
        {
            1 => items.Where(i => i.Scan?.Dlss is not null),
            2 => items.Where(i => i.IsInstalled),
            3 => items.Where(i => i.Scan is { Manifest: null } s && (s.OtherAppBackup || s.OtherAppRecord || s.OptiScalerHook is not null || s.ReShade.Installed)),
            _ => items
        };
        var list = items.ToList();
        if (list.SequenceEqual(_visible)) return;
        _visible.Clear();
        foreach (var i in list) _visible.Add(i);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();
    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_visible is not null) ApplyFilter(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadLibraryAsync();

    private async void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        if (!_settings.ManualExes.Contains(file.Path, StringComparer.OrdinalIgnoreCase)) _settings.ManualExes.Add(file.Path);
        _settings.Save();
        await LoadLibraryAsync();
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tab = sender.SelectedItem;
        // The games grid stays laid out while hidden (hiding it with Collapsed re-measures every poster on return).
        bool games = tab == TabGames;
        GamesPage.Opacity = games ? 1 : 0;
        GamesPage.IsHitTestVisible = games;
        // Hide the search tools without giving up their space: collapsing them re-centred the tabs, which then
        // jumped sideways under the mouse on every switch.
        GameTools.Opacity = games ? 1 : 0;
        GameTools.IsHitTestVisible = games;
        UpdateTitleBarRegions();
        SettingsPage.Visibility = tab == TabSettings ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tab == TabAbout ? Visibility.Visible : Visibility.Collapsed;
        if (tab == TabSettings) { RefreshUpdatesPanel(); _ = RefreshSettingsPage(); }
    }

    // ------------------------------------------------------------------ settings page

    /// <summary>Fills the Settings page. File checks (versions, ReShade add-on support) run off the UI thread.</summary>
    private async Task RefreshSettingsPage()
    {
        RuntimeFolderBox.Text = _settings.RuntimeFolder;
        ReShadeSourceBox.Text = _settings.ReShadeSource;
        FolderList.ItemsSource = _settings.ExtraFolders.ToList();
        static string Where(string path) =>
            path.StartsWith(Components.BundledPayload, StringComparison.OrdinalIgnoreCase) ? "the copies included with DLSS 5 Master" : path;

        var (rt, runtimeText, rs, reshadeText, libCount) = await Task.Run(() =>
        {
            var rt = _components.RuntimeDir;
            var files = _components.RuntimeFiles();
            var runtimeText = rt is null
                ? "Not found. Reinstall DLSS 5 Master, or pick a folder containing nvngx_dlssnr.dll."
                : $"Using {Where(rt)}\n" + string.Join("   ", files.Keys.Where(k => k.StartsWith("nvngx")).OrderBy(k => k).Select(k => $"{k} {Pe.GetFileVersion(files[k])}"));
            var rs = _components.ReShadeDll(64);
            var reshadeText = rs is null ? "Not found. Pick a ReShade_Setup_*_Addon.exe." : $"Using ReShade {Pe.GetFileVersion(rs)} ({Where(rs)})";
            int libCount = 0;
            try { libCount = Directory.Exists(AppPaths.DllLibrary) ? Directory.EnumerateFiles(AppPaths.DllLibrary, "*.dll", SearchOption.AllDirectories).Count() : 0; } catch { }
            return (rt, runtimeText, rs, reshadeText, libCount);
        });

        RuntimeStatus.Text = runtimeText;
        RuntimeStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[rt is null ? "WarnText" : "AccentText"];
        ReShadeStatus.Text = reshadeText;
        ReShadeStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[rs is null ? "WarnText" : "AccentText"];
        DllLibraryStatus.Text = $"{libCount} DLL build(s) in {AppPaths.DllLibrary}";
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void PickRuntime_Click(object sender, RoutedEventArgs e)
    {
        var dir = await PickFolderAsync();
        if (dir is null) return;
        _settings.RuntimeFolder = dir;
        _settings.Save();
        _ = RefreshSettingsPage();
    }

    private void ClearRuntime_Click(object sender, RoutedEventArgs e) { _settings.RuntimeFolder = ""; _settings.Save(); _ = RefreshSettingsPage(); }

    private async void PickReShade_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        if (Components.ExtractFromReShadeSetup(file.Path, "ReShade64.dll") is null)
        {
            ReShadeStatus.Text = "That file is not a ReShade setup with embedded DLLs.";
            return;
        }
        _settings.ReShadeSource = file.Path;
        _settings.Save();
        _ = RefreshSettingsPage();
    }

    private void ClearReShade_Click(object sender, RoutedEventArgs e) { _settings.ReShadeSource = ""; _settings.Save(); _ = RefreshSettingsPage(); }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = await PickFolderAsync();
        if (dir is null || _settings.ExtraFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) return;
        _settings.ExtraFolders.Add(dir);
        _settings.Save();
        _ = RefreshSettingsPage();
        await LoadLibraryAsync();
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not string dir) return;
        _settings.ExtraFolders.Remove(dir);
        _settings.Save();
        _ = RefreshSettingsPage();
        await LoadLibraryAsync();
    }

    private async void UnhideAll_Click(object sender, RoutedEventArgs e)
    {
        _settings.HiddenGames.Clear();
        _settings.Save();
        await LoadLibraryAsync();
    }

    private async void ImportDlls_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".dll");
        var files = await picker.PickMultipleFilesAsync();
        var errors = new List<string>();
        foreach (var f in files)
        {
            try { Installers.ImportDll(f.Path); }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        _ = RefreshSettingsPage();
        if (errors.Count > 0) DllLibraryStatus.Text += "\n" + string.Join("\n", errors);
    }

    private void OpenDllLibrary_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DllLibrary);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DllLibrary}\"") { UseShellExecute = true });
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AppPaths.LogFile)) AppPaths.Log("Log created.");
        Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
    }

    private const string Notices =
        "Included with DLSS 5 Master (SHA-256 pinned; licence texts in the install folder):\n" +
        "• NVIDIA DLSS, Frame Generation, Ray Reconstruction, neural rendering runtimes and Streamline — © NVIDIA Corporation, NVIDIA license\n" +
        "• ReShade 6.8 with add-on support — reshade.me, © Patrick Mours (BSD-3-Clause)\n" +
        "• RenoDX DLSS5 add-on and DLSS Tool — github.com/clshortfuse/renodx (MIT)\n" +
        "• DLSS5-Feeder — github.com/jlrouzies-fr/DLSS5-Feeder (MIT)\n" +
        "• vort_Shaders — github.com/vortigern11/vort_Shaders (MIT)\n" +
        "• MFGAdaUnlock-RenoDx 1.4.2 — github.com/mavismmg/MFGAdaUnlock-RenoDx (MIT)\n" +
        "• OptiScaler NR pre-SR multipass 0.8.3 RTX 40 MFG — github.com/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass (GPL-3.0; source code at tag v0.8.3)\n\n" +
        "Downloaded when first used, from their authors' release pages and verified by SHA-256:\n" +
        "• OptiScaler NR pre-SR multipass 0.8.3 (standard) — github.com/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass (GPL-3.0)\n" +
        "• OptiScaler DLSS-NR — github.com/Dagherbou/OptiScaler_DLSSNR (GPL-3.0)\n" +
        "• OptiScaler DLSS-NR pre-SR multipass — github.com/jlrouzies-fr/OptiScaler-DLSSNR-PreSR-Multipass (GPL-3.0)";
}
