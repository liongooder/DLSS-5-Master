using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DLSS5Master.Core;

namespace DLSS5Master;

/// <summary>Settings → Add-on updates: check GitHub, switch to a newer add-on version, or go back to the bundled one.</summary>
public sealed partial class MainWindow
{
    private bool _updating;
    private bool _feederZipWorking;
    private string? _updatesViewStamp;

    /// <summary>Runs the daily check in the background when it is due; marks the Settings tab when updates exist.</summary>
    private async Task AutoCheckUpdatesAsync()
    {
        try { Updates.DropSuperseded(); Updates.DropInstalledAppUpdates(); RefreshOptiBuildList(); }
        catch (Exception e) { AppPaths.Log("Could not tidy add-on updates: " + e.Message); }
        MarkSettingsTab();
        ShowAppUpdateBar();
        if (Updates.State.AutoCheck)
        {
            // The app itself is checked on every start (one small request); add-ons once a day.
            try { using var limit = new CancellationTokenSource(Updates.CheckLimit); await Updates.CheckAppAsync(limit.Token); }
            catch (Exception e) { AppPaths.Log("App update check failed: " + e.Message); }
            ShowAppUpdateBar();
        }
        if (!Updates.CheckDue && !RuntimeUpdates.CheckDue) return;
        _updating = true;
        if (Tabs.SelectedItem == TabSettings) UpdateStatus.Text = "Checking for updates…";
        string? failure = null;
        try
        {
            using var limit = new CancellationTokenSource(Updates.CheckLimit);
            await Task.WhenAll(Updates.CheckDue ? Updates.CheckAsync(limit.Token) : Task.CompletedTask,
                RuntimeUpdates.CheckDue ? RuntimeUpdates.CheckAsync(limit.Token) : Task.CompletedTask);
        }
        catch (Exception e) { AppPaths.Log("Update check failed: " + e.Message); failure = Updates.CheckFailure(e); }
        finally { _updating = false; }
        MarkSettingsTab();
        if (Tabs.SelectedItem == TabSettings)
        {
            RefreshUpdatesPanel();
            await RefreshRuntimeUpdatesAsync();
            if (failure is not null) UpdateStatus.Text = failure;
        }
    }

    private void MarkSettingsTab() => TabSettings.Text = Updates.AvailableCount > 0 || RuntimeUpdates.Sources.Any(s => RuntimeUpdates.Available(s) is not null) ? "SETTINGS ●" : "SETTINGS";

    private void RefreshUpdatesPanel()
    {
        bool customFeeder = !string.IsNullOrWhiteSpace(_settings.FeederFolder);
        var feederZip = customFeeder ? FeederZipImport.DisplayName(_settings.FeederFolder) : null;
        if (!_feederZipWorking) RefreshFeederZipStatus(customFeeder, feederZip);
        var stamp = System.Text.Json.JsonSerializer.Serialize(new { Updates.State, _updating, _componentWorking, customFeeder, feederZip, _settings.FeederLiveControls });
        if (_updatesViewStamp == stamp) return;
        _updatesViewStamp = stamp;
        AutoCheckBox.IsChecked = Updates.State.AutoCheck;
        PreReleaseBox.IsChecked = Updates.State.IncludePrereleases;
        UpdateRows.Children.Clear();
        foreach (var src in Updates.Sources)
        {
            var (version, _, updated) = Updates.InUse(src);
            var available = Updates.Available(src);
            var custom = src.Id == "feeder" && customFeeder;
            var current = custom
                ? feederZip is null ? "Selected ZIP unavailable" : $"Selected ZIP: {feederZip}"
                : $"In use: {version}{(updated ? " (updated)" : " (included)")}";

            var grid = new Grid { Padding = new Thickness(18, 10, 14, 10), ColumnSpacing = 10, BorderBrush = Res("RowBorder"),
                                  BorderThickness = new Thickness(0, UpdateRows.Children.Count == 0 ? 0 : 1, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = src.Name, FontWeight = FontWeights.SemiBold });
            info.Children.Add(new TextBlock
            {
                FontSize = 12, Foreground = Res(available is not null ? "AccentText" : "MutedText"), TextWrapping = TextWrapping.Wrap,
                Text = current +
                       (available is not null ? $"   ·   {(custom ? "Automatic update" : "New")}: {available.Version}{(available.Prerelease ? " (beta)" : "")}, {available.Published.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
                        : Updates.State.LastCheck is null || custom ? "" : "   ·   Up to date"),
            });
            grid.Children.Add(info);

            if (available is not null)
            {
                var update = new Button { Content = "Update", Style = (Style)Application.Current.Resources["AccentButtonStyle"], VerticalAlignment = VerticalAlignment.Center };
                update.Click += async (_, _) => await ApplyUpdateAsync(src);
                Grid.SetColumn(update, 1);
                grid.Children.Add(update);
            }
            if (updated && !custom)
            {
                var undo = new Button { Content = "Use included", VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(undo, $"Go back to {src.BundledVersion}, the version included with DLSS 5 Master");
                undo.Click += (_, _) =>
                {
                    if (_busy || _bulkRunning || _appUpdating || _componentWorking) return;
                    Updates.Revert(src);
                    RefreshOptiBuildList();
                    MarkSettingsTab();
                    RefreshUpdatesPanel();
                    _ = RefreshComponentsAsync();
                };
                Grid.SetColumn(undo, 2);
                grid.Children.Add(undo);
            }
            UpdateRows.Children.Add(grid);
        }
        UpdateStatus.Text = _updating ? "Checking for updates…" : Updates.State.LastCheck is { } t
            ? $"Last checked {t.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)} at {t.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture)}. {Updates.AvailableCount switch { 0 => "Everything is up to date.", 1 => "1 update available.", var n => $"{n} updates available." }}"
            : "Not checked yet.";
    }

    private void RefreshFeederZipStatus(bool custom, string? zip)
    {
        ChooseFeederZipButton.IsEnabled = true;
        var (version, _, updated) = Updates.InUse(Updates.Sources.First(source => source.Id == "feeder"));
        AutomaticFeederButton.IsEnabled = custom || updated;
        FeederLiveControlsBox.IsEnabled = true;
        FeederLiveControlsBox.IsChecked = _settings.FeederLiveControls;
        FeederZipStatus.Text = custom
            ? zip is null ? "Selected ZIP unavailable. Choose it again or use the original." : $"Selected ZIP · {zip}"
            : updated ? $"Feeder {version} (accepted update)." : $"Original Feeder {version} (included).";
        if (_settings.FeederLiveControls)
            FeederZipStatus.Text += custom
                ? "\n64-bit Feeder installs with RenoDX use the included build while live controls are enabled."
                : "\nInstall or reinstall a 64-bit Feeder game with RenoDX to use live controls.";
        FeederZipStatus.Foreground = Res(custom && zip is null ? "WarnText" : "MutedText");
    }

    private async void ChooseFeederZip_Click(object sender, RoutedEventArgs e)
    {
        if (_feederZipWorking || _busy || _bulkRunning || _appUpdating || _componentWorking) return;
        _feederZipWorking = true;
        ChooseFeederZipButton.IsEnabled = AutomaticFeederButton.IsEnabled = FeederLiveControlsBox.IsEnabled = false;
        string? error = null;
        try
        {
            if (await PickFileAsync(".zip") is not { } file) return;
            FeederZipStatus.Foreground = Res("MutedText");
            FeederZipStatus.Text = "Reading Feeder ZIP…";
            var copy = await Task.Run(() => FeederZipImport.Prepare(file));
            if (_busy || _bulkRunning || _appUpdating || _componentWorking)
                error = "Wait for the current setup or update to finish, then choose the ZIP again.";
            else
                SaveFeederSelection(copy);
        }
        catch (Exception ex) { error = $"Could not use this Feeder ZIP. {ex.Message}"; }
        finally
        {
            _feederZipWorking = false;
            RefreshUpdatesPanel();
            if (error is not null) ShowFeederZipError(error);
        }
    }

    private void AutomaticFeeder_Click(object sender, RoutedEventArgs e)
    {
        if (_feederZipWorking || _busy || _bulkRunning || _appUpdating || _componentWorking) return;
        try
        {
            SaveFeederSelection("");
            var source = Updates.Sources.First(source => source.Id == "feeder");
            if (Updates.ActivePath(source.Id) is not null) Updates.Revert(source);
            RefreshUpdatesPanel();
        }
        catch (Exception ex) { ShowFeederZipError($"Could not save the Feeder selection. {ex.Message}"); }
    }

    private void FeederLiveControls_Click(object sender, RoutedEventArgs e)
    {
        if (_feederZipWorking || _busy || _bulkRunning || _appUpdating || _componentWorking)
        {
            RefreshUpdatesPanel();
            return;
        }
        try
        {
            SaveFeederSelection(_settings.FeederFolder, FeederLiveControlsBox.IsChecked == true);
            RefreshUpdatesPanel();
        }
        catch (Exception ex)
        {
            RefreshUpdatesPanel();
            ShowFeederZipError($"Could not save the Feeder selection. {ex.Message}");
        }
    }

    private void SaveFeederSelection(string folder, bool? liveControls = null)
    {
        var previous = _settings.FeederFolder;
        var priorLiveControls = _settings.FeederLiveControls;
        _settings.FeederFolder = folder;
        if (liveControls.HasValue) _settings.FeederLiveControls = liveControls.Value;
        try { _settings.Save(); }
        catch
        {
            _settings.FeederFolder = previous;
            _settings.FeederLiveControls = priorLiveControls;
            throw;
        }
        _ = RefreshComponentsAsync();
    }

    private void ShowFeederZipError(string error)
    {
        FeederZipStatus.Text = error;
        FeederZipStatus.Foreground = Res("WarnText");
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _updating = true;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Checking GitHub…";
        try
        {
            using var limit = new CancellationTokenSource(Updates.CheckLimit);
            await Task.WhenAll(Updates.CheckAsync(limit.Token), Updates.CheckAppAsync(limit.Token), RuntimeUpdates.CheckAsync(limit.Token));
            RefreshUpdatesPanel();
            await RefreshRuntimeUpdatesAsync();
            ShowAppUpdateBar();
        }
        catch (Exception ex) { AppPaths.Log("Update check failed: " + ex.Message); UpdateStatus.Text = Updates.CheckFailure(ex); }
        finally
        {
            _updating = false;
            CheckUpdatesButton.IsEnabled = true;
            MarkSettingsTab();
        }
    }

    private async void UpdateOptions_Click(object sender, RoutedEventArgs e)
    {
        bool preChanged = Updates.State.IncludePrereleases != (PreReleaseBox.IsChecked == true);
        Updates.State.AutoCheck = AutoCheckBox.IsChecked == true;
        Updates.State.IncludePrereleases = PreReleaseBox.IsChecked == true;
        Updates.Save();
        if (preChanged) CheckUpdates_Click(sender, e);   // the list of candidate versions changes
        await Task.CompletedTask;
    }

    private bool _appUpdating;

    /// <summary>Shows the "Update x.y.z" button in the top bar when GitHub has a newer release than this one.</summary>
    private void ShowAppUpdateBar()
    {
        if (_appUpdating) return;
        if (Updates.AppUpdateAvailable is not { } rel) { AppUpdateButton.Visibility = Visibility.Collapsed; return; }
        AppUpdateText.Text = $"Update {rel.Version}";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AppUpdateButton, $"Update DLSS 5 Master to {rel.Version}");
        ToolTipService.SetToolTip(AppUpdateButton, $"DLSS 5 Master {rel.Version} is available (you have {Updates.CurrentAppVersion})");
        AppUpdateButton.IsEnabled = true;
        AppUpdateButton.Visibility = Visibility.Visible;
    }

    /// <summary>The top-bar button: details first, then download, install and restart.</summary>
    private async void AppUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appUpdating || Updates.AppUpdateAvailable is not { } rel) return;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"You have {Updates.CurrentAppVersion}. The update downloads {rel.Size / 1048576} MB, checks it, installs it and restarts DLSS 5 Master by itself. Your games and settings are kept.",
        });
        if (Uri.TryCreate(rel.Page, UriKind.Absolute, out var page))
            body.Children.Add(new HyperlinkButton { Content = "What's new in " + rel.Version, NavigateUri = page, Padding = new Thickness(0) });
        // An install on a game's page, or Set up several games (reading the games or setting them up).
        bool setupRunning = _busy || _bulkRunning;
        if (setupRunning)
            body.Children.Add(new TextBlock { Text = "An install is running in a game. Wait for it to finish first.", Foreground = Res("WarnText"), TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"Update to DLSS 5 Master {rel.Version}?",
            Content = body,
            PrimaryButtonText = "Update now",
            IsPrimaryButtonEnabled = !setupRunning,
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await InstallAppUpdateAsync(rel);
    }

    private async Task InstallAppUpdateAsync(AppRelease rel)
    {
        if (_appUpdating || _busy || _bulkRunning) return;
        _appUpdating = true;
        RefreshLibrarySummary();   // Set up several games waits until the new version runs
        AppUpdateButton.IsEnabled = false;
        AppUpdateText.Text = "Updating…";
        var progress = new Progress<string>(t =>
        {
            var pct = System.Text.RegularExpressions.Regex.Match(t, @"\d+%");
            AppUpdateText.Text = pct.Success ? $"Downloading {pct.Value}" : t.StartsWith("Checking") ? "Checking…" : "Installing…";
            ToolTipService.SetToolTip(AppUpdateButton, t);
        });
        try
        {
            var installer = await Updates.DownloadAppUpdateAsync(rel, progress);
            // The installer closes the app: a setup that started while the update downloaded finishes first.
            while (_busy || _bulkRunning)
            {
                AppUpdateText.Text = "Waiting for the setup…";
                await Task.Delay(500);
            }
            Updates.StartAppInstaller(rel, installer, progress);
            AppUpdateText.Text = "Restarting…";
            await Task.Delay(1500);
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            _appUpdating = false;
            RefreshLibrarySummary();
            AppUpdateButton.IsEnabled = true;
            AppUpdateText.Text = "Retry update";
            ToolTipService.SetToolTip(AppUpdateButton, "The update did not complete: " + ex.Message);
            await new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = "The update did not complete",
                Content = new TextBlock { Text = ex.Message + "\n\nNothing was changed. Press Retry update to try again.", TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
            }.ShowAsync();
        }
    }

    private async Task ApplyUpdateAsync(UpdateSource src)
    {
        if (_updating || _busy || _bulkRunning || _appUpdating || _componentWorking) return;
        _updating = true;
        CheckUpdatesButton.IsEnabled = false;
        var progress = new Progress<string>(t => UpdateStatus.Text = t);
        try
        {
            UpdateStatus.Text = $"Updating {src.Name}…";
            await Updates.ApplyAsync(src, progress);
            RefreshOptiBuildList();
            RefreshUpdatesPanel();
            await RefreshComponentsAsync();
            UpdateStatus.Text = src.Id == "feeder" && !string.IsNullOrWhiteSpace(_settings.FeederFolder)
                ? $"Feeder {Updates.InUse(src).Version} is available for automatic setup. Your custom ZIP is still selected."
                : $"{src.Name} updated to {Updates.InUse(src).Version}. Reinstall a game to give it the new version.";
        }
        catch (Exception ex) { UpdateStatus.Text = $"✖ {src.Name}: {ex.Message}"; }
        finally
        {
            _updating = false;
            CheckUpdatesButton.IsEnabled = true;
            MarkSettingsTab();
        }
    }
}
