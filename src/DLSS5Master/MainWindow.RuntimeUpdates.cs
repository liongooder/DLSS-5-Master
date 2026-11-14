using DLSS5Master.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DLSS5Master;

public sealed partial class MainWindow
{
    private int _runtimeRead;
    private string? _runtimeViewStamp;

    private async Task RefreshRuntimeUpdatesAsync()
    {
        int read = ++_runtimeRead;
        try
        {
            var inspection = _componentReports?.FirstOrDefault(r => r.Group.Id == "runtime");
            var rows = await Task.Run(() =>
            {
                var files = _components.RuntimeFiles();
                var included = ComponentHealth.Groups.FirstOrDefault(g => g.Id == "runtime");
                return RuntimeUpdates.Sources.Select(source =>
                {
                    var pins = included?.Files.Where(p => source.Streamline
                        ? Path.GetFileName(p.RelativePath).StartsWith("sl.", StringComparison.OrdinalIgnoreCase)
                        : Path.GetFileName(p.RelativePath).Equals(source.FileName, StringComparison.OrdinalIgnoreCase)).ToList() ?? new();
                    bool? good = inspection is null ? null : included is not null && pins.Count > 0 && pins.All(p => !inspection.Problems.Any(problem => problem.EndsWith(p.RelativePath, StringComparison.OrdinalIgnoreCase)));
                    var version = RuntimeUpdates.SelectedVersion(source, files);
                    return (Source: source, Version: version, IncludedGood: good, Count: pins.Count,
                        Active: RuntimeUpdates.State.Active.ContainsKey(source.Id), ActiveGood: RuntimeUpdates.SelectionReady(source),
                        Available: RuntimeUpdates.Available(source, version ?? source.BundledVersion),
                        Latest: source.Streamline && RuntimeUpdates.State.Latest.GetValueOrDefault(source.Id)?.Version != source.BundledVersion ? null : RuntimeUpdates.State.Latest.GetValueOrDefault(source.Id));
                }).ToList();
            });
            if (read != _runtimeRead) return;
            bool custom = !string.IsNullOrWhiteSpace(_settings.RuntimeFolder);
            var stamp = System.Text.Json.JsonSerializer.Serialize(new { Rows = rows.Select(r => new { r.Source.Id, r.Version, r.IncludedGood, r.Count, r.Active, r.ActiveGood, r.Available, r.Latest }), custom, _componentVerifiedAt, _componentWorking, RuntimeUpdates.State.LastCheck });
            if (_runtimeViewStamp == stamp) return;
            RuntimeUpdateRows.Children.Clear();
            foreach (var row in rows)
            {
                var source = row.Source;
                var body = new StackPanel { Spacing = 3 };
                body.Children.Add(new TextBlock { Text = source.Name, FontWeight = FontWeights.SemiBold });
                body.Children.Add(new TextBlock
                {
                    Text = $"Included: {source.BundledVersion} · Selected: {row.Version ?? "Not found"}" +
                        (custom ? " · Custom folder" : row.ActiveGood ? " · Updated" : " · Included build"),
                    FontSize = 12, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap
                });
                body.Children.Add(new TextBlock
                {
                    Text = source.Streamline ? "Source: RankFTW/rhi-repo · Streamline release package"
                        : $"Source: {source.Repo} · Release mirror · {source.FileName}",
                    FontSize = 12, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap
                });
                body.Children.Add(new TextBlock
                {
                    Text = row.IncludedGood switch { true => $"Last verification passed · {row.Count} file(s) match SHA-256", false => "Included files need repair; use Repair missing or changed above.", _ => "Not verified this session · Choose Verify files to check SHA-256" },
                    FontSize = 12, Foreground = Res(row.IncludedGood is true ? "AccentText" : row.IncludedGood is false ? "WarnText" : "MutedText"), TextWrapping = TextWrapping.Wrap
                });
                if (row.Active)
                    body.Children.Add(new TextBlock
                    {
                        Text = row.ActiveGood ? (custom ? "Saved update verified; the custom folder keeps priority." : "Selected update verified against its saved SHA-256 checksums.")
                            : "Saved update is missing or changed. Using the included files; update again or choose Use included.",
                        FontSize = 12, Foreground = Res(row.ActiveGood ? "AccentText" : "WarnText"), TextWrapping = TextWrapping.Wrap
                    });
                body.Children.Add(new TextBlock
                {
                    Text = row.Available is { } newer ? $"Update available: {newer.Version}" +
                        (newer.Size > 0 ? $" · Download: {Math.Ceiling(newer.Size / 1048576d):0} MB" : "")
                        : row.Latest is not null ? $"Latest verified release: {row.Latest.Version} · No newer version" : "No runtime update check yet.",
                    FontSize = 12, Foreground = Res(row.Available is null ? "MutedText" : "AccentText"), TextWrapping = TextWrapping.Wrap
                });
                if (source.Streamline)
                    body.Children.Add(new TextBlock
                    {
                        Text = "Pinned to Streamline 2.13.0.0. Later versions are excluded until neural rendering compatibility is tested.",
                        FontSize = 11, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap
                    });
                var actions = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                if (row.Available is not null)
                {
                    var update = new Button
                    {
                        Content = "Update", IsEnabled = !_componentWorking && !custom,
                        Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(update, "Update " + source.Name);
                    update.Click += async (_, _) => await UpdateRuntimeAsync(source);
                    actions.Children.Add(update);
                }
                if (row.Active)
                {
                    var revert = new Button { Content = "Use included", IsEnabled = !_componentWorking && !custom };
                    revert.Click += async (_, _) => await RunRuntimeActionAsync(async () =>
                    {
                        await RuntimeUpdates.RevertAsync(source);
                        return source.Name + " now uses the included build for new setups.";
                    });
                    actions.Children.Add(revert);
                }
                if (row.Latest is { } latest && Uri.TryCreate(latest.Page, UriKind.Absolute, out var page))
                    actions.Children.Add(new HyperlinkButton { Content = "Release notes", NavigateUri = page, Padding = new Thickness(2) });
                var panel = new Grid { Padding = new Thickness(12), ColumnSpacing = 12, CornerRadius = new CornerRadius(8), Background = Res("RowBackground") };
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                panel.Children.Add(body);
                Grid.SetColumn(actions, 1);
                panel.Children.Add(actions);
                RuntimeUpdateRows.Children.Add(panel);
                await Task.Delay(16);
                if (read != _runtimeRead) return;
            }
            int available = rows.Count(r => r.Available is not null);
            RuntimeUpdateStatus.Text = (RuntimeUpdates.State.LastCheck is { } at
                ? $"Last checked {at.ToLocalTime().ToString("d MMM yyyy, h:mm tt", System.Globalization.CultureInfo.InvariantCulture)}. {available} runtime update(s) available."
                : "Check for newer verified runtime releases.") +
                (custom ? " Choose Automatic under NVIDIA runtime to use these updates; your custom folder is unchanged." : "");
            UpdateAllRuntimesButton.IsEnabled = available > 0 && !custom && !_componentWorking;
            CheckRuntimeUpdatesButton.IsEnabled = !_componentWorking;
            _runtimeViewStamp = stamp;
        }
        catch (Exception ex) { if (read == _runtimeRead) RuntimeUpdateStatus.Text = "Could not read runtime updates: " + ex.Message; }
    }

    private async void CheckRuntimeUpdates_Click(object sender, RoutedEventArgs e) => await RunRuntimeActionAsync(async () =>
    {
        using var limit = new CancellationTokenSource(Updates.CheckLimit);
        await RuntimeUpdates.CheckAsync(limit.Token);
        return "Runtime update check finished. Streamline was checked against RankFTW/rhi-repo releases.";
    });

    private async Task UpdateRuntimeAsync(RuntimeSource source) => await RunRuntimeActionAsync(async () =>
    {
        if (!string.IsNullOrWhiteSpace(_settings.RuntimeFolder)) return "Choose Automatic under NVIDIA runtime first; the custom folder was kept.";
        var progress = new Progress<string>(text => RuntimeUpdateStatus.Text = text);
        await Task.Run(() => RuntimeUpdates.ApplyAsync(source, progress));
        return source.Name + " updated for new setups. Reinstall a game to give it this version.";
    });

    private async void UpdateAllRuntimes_Click(object sender, RoutedEventArgs e) => await RunRuntimeActionAsync(async () =>
    {
        if (!string.IsNullOrWhiteSpace(_settings.RuntimeFolder)) return "Choose Automatic under NVIDIA runtime first; the custom folder was kept.";
        int done = 0;
        var progress = new Progress<string>(text => RuntimeUpdateStatus.Text = text);
        foreach (var source in RuntimeUpdates.Sources)
        {
            var files = await Task.Run(() => _components.RuntimeFiles());
            if (RuntimeUpdates.Available(source, RuntimeUpdates.SelectedVersion(source, files) ?? source.BundledVersion) is null) continue;
            await Task.Run(() => RuntimeUpdates.ApplyAsync(source, progress));
            done++;
        }
        return $"{done} runtime update(s) selected for new setups. Existing games were kept unchanged.";
    });

    private async Task RunRuntimeActionAsync(Func<Task<string>> work)
    {
        if (_componentWorking || _updating || _busy || _bulkRunning || _appUpdating)
        {
            RuntimeUpdateStatus.Text = "Wait for the current install or update to finish, then try again.";
            return;
        }
        _componentWorking = _updating = true;
        SetBusy(true, "Updating runtime components…");
        RefreshLibrarySummary();
        CheckRuntimeUpdatesButton.IsEnabled = UpdateAllRuntimesButton.IsEnabled = VerifyComponentsButton.IsEnabled = RepairComponentsButton.IsEnabled = CheckUpdatesButton.IsEnabled = false;
        string message;
        try { message = await work(); }
        catch (Exception ex)
        {
            message = "Runtime update stopped: " + ex.Message + " Completed component selections remain available.";
            AppPaths.Log("Runtime update stopped: " + ex);
        }
        finally
        {
            _componentWorking = _updating = false;
            SetBusy(false);
            RefreshLibrarySummary();
            CheckUpdatesButton.IsEnabled = true;
        }
        await RefreshSettingsPage();
        MarkSettingsTab();
        RuntimeUpdateStatus.Text = message;
    }
}
