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

    /// <summary>Runs the daily check in the background when it is due; marks the Settings tab when updates exist.</summary>
    private async Task AutoCheckUpdatesAsync()
    {
        MarkSettingsTab();
        ShowAppUpdateBar();
        if (Updates.State.AutoCheck)
        {
            // The app itself is checked on every start (one small request); add-ons once a day.
            try { await Updates.CheckAppAsync(); }
            catch (Exception e) { AppPaths.Log("App update check failed: " + e.Message); }
            ShowAppUpdateBar();
        }
        if (!Updates.CheckDue) return;
        _updating = true;
        if (SettingsPage.Visibility == Visibility.Visible) UpdateStatus.Text = "Checking for updates…";
        try { await Updates.CheckAsync(); }
        catch (Exception e) { AppPaths.Log("Update check failed: " + e.Message); }
        finally { _updating = false; }
        MarkSettingsTab();
        if (SettingsPage.Visibility == Visibility.Visible) RefreshUpdatesPanel();
    }

    private void MarkSettingsTab() => TabSettings.Text = Updates.AvailableCount > 0 ? "SETTINGS ●" : "SETTINGS";

    private void RefreshUpdatesPanel()
    {
        AutoCheckBox.IsChecked = Updates.State.AutoCheck;
        PreReleaseBox.IsChecked = Updates.State.IncludePrereleases;
        UpdateRows.Children.Clear();
        foreach (var src in Updates.Sources)
        {
            var (version, _, updated) = Updates.InUse(src);
            var available = Updates.Available(src);
            var latest = Updates.State.Latest.GetValueOrDefault(src.Id);

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
                Text = $"In use: {version}{(updated ? " (updated)" : " (included)")}" +
                       (available is not null ? $"   ·   New: {available.Version}{(available.Prerelease ? " (beta)" : "")}, {available.Published.ToLocalTime():d MMM yyyy}"
                        : Updates.State.LastCheck is null ? "" : "   ·   Up to date"),
            });
            grid.Children.Add(info);

            if (available is not null)
            {
                var update = new Button { Content = "Update", Style = (Style)Application.Current.Resources["AccentButtonStyle"], VerticalAlignment = VerticalAlignment.Center };
                update.Click += async (_, _) => await ApplyUpdateAsync(src);
                Grid.SetColumn(update, 1);
                grid.Children.Add(update);
            }
            if (updated)
            {
                var undo = new Button { Content = "Use included", VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(undo, $"Go back to {src.BundledVersion}, the version included with DLSS 5 Master");
                undo.Click += (_, _) =>
                {
                    Updates.Revert(src);
                    RefreshOptiBuildList();
                    MarkSettingsTab();
                    RefreshUpdatesPanel();
                };
                Grid.SetColumn(undo, 2);
                grid.Children.Add(undo);
            }
            UpdateRows.Children.Add(grid);
        }
        UpdateStatus.Text = _updating ? "Checking for updates…" : Updates.State.LastCheck is { } t
            ? $"Last checked {t.ToLocalTime():g}. {(Updates.AvailableCount == 0 ? "Everything is up to date." : $"{Updates.AvailableCount} update(s) available.")}"
            : "Not checked yet.";
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _updating = true;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Checking GitHub…";
        try
        {
            await Task.WhenAll(Updates.CheckAsync(), Updates.CheckAppAsync());
            RefreshUpdatesPanel();
            ShowAppUpdateBar();
        }
        catch (Exception ex) { UpdateStatus.Text = "Could not check for updates: " + ex.Message; }
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
        if (_busy)
            body.Children.Add(new TextBlock { Text = "An install is running in a game. Wait for it to finish first.", Foreground = Res("WarnText"), TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"Update to DLSS 5 Master {rel.Version}?",
            Content = body,
            PrimaryButtonText = "Update now",
            IsPrimaryButtonEnabled = !_busy,
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await InstallAppUpdateAsync(rel);
    }

    private async Task InstallAppUpdateAsync(AppRelease rel)
    {
        if (_appUpdating || _busy) return;
        _appUpdating = true;
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
            await Updates.InstallAppUpdateAsync(rel, progress);
            AppUpdateText.Text = "Restarting…";
            await Task.Delay(1500);
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            _appUpdating = false;
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
        if (_updating) return;
        _updating = true;
        CheckUpdatesButton.IsEnabled = false;
        var progress = new Progress<string>(t => UpdateStatus.Text = t);
        try
        {
            UpdateStatus.Text = $"Updating {src.Name}…";
            await Updates.ApplyAsync(src, progress);
            RefreshOptiBuildList();
            RefreshUpdatesPanel();
            UpdateStatus.Text = $"{src.Name} updated to {Updates.InUse(src).Version}. Reinstall a game to give it the new version.";
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
