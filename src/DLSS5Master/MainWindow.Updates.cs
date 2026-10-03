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
            await Updates.CheckAsync();
            RefreshUpdatesPanel();
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
