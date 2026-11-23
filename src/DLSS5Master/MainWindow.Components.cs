using DLSS5Master.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DLSS5Master;

public sealed partial class MainWindow
{
    private bool _componentWorking;
    private int _componentRead;
    private IReadOnlyList<ComponentInspection>? _componentReports;
    private DateTime? _componentVerifiedAt;
    private string? _componentViewStamp;
    private Task? _componentVerification;

    private async void VerifyComponents_Click(object sender, RoutedEventArgs e) => await RefreshComponentsAsync(verify: true);
    private async void RepairComponents_Click(object sender, RoutedEventArgs e) => await RepairComponentsAsync(null);

    private Task RefreshComponentsAsync(bool verify = false)
    {
        // A navigation refresh must not supersede a verification the user explicitly started.
        if (_componentVerification is { IsCompleted: false }) return _componentVerification;
        var refresh = RefreshComponentsCoreAsync(verify);
        if (verify) _componentVerification = refresh;
        return refresh;
    }

    private async Task RefreshComponentsCoreAsync(bool verify)
    {
        var read = ++_componentRead;
        VerifyComponentsButton.IsEnabled = false;
        if (verify) ComponentHealthStatus.Text = "Checking included files and SHA-256 checksums…";
        try
        {
            if (verify)
            {
                var fresh = await Task.Run(() => ComponentHealth.Groups.Select(g => ComponentHealth.Inspect(g)).ToList());
                if (read != _componentRead) return;
                _componentReports = fresh;
                _componentVerifiedAt = DateTime.Now;
            }
            bool verified = _componentReports is not null;
            var reports = _componentReports ?? ComponentHealth.Groups.Select(g => new ComponentInspection(g, 0, 0, Array.Empty<string>())).ToList();
            // A newer verification owns the displayed results.
            if (read != _componentRead) return;
            if (reports.Count == 0) throw new InvalidDataException("The component manifest is missing. Reinstall the app.");
            var selected = await Task.Run(() =>
            {
                var versions = _components.RuntimeFiles().Where(kv => kv.Key.StartsWith("nvngx", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {Pe.GetFileVersion(kv.Value) ?? "unknown"}");
                var reshade = _components.ReShadeDll(64);
                var feederFolder = _settings.FeederFolder;
                var feeder = string.IsNullOrWhiteSpace(feederFolder) ? null
                    : FeederZipImport.DisplayName(feederFolder) is { } zip ? $"{zip} · Local ZIP" : "Selected ZIP unavailable";
                return (Runtime: string.Join(" · ", versions), ReShade: reshade is null ? "Not found" : Pe.GetFileVersion(reshade) ?? "unknown", Feeder: feeder);
            });
            if (read != _componentRead) return;
            var stamp = System.Text.Json.JsonSerializer.Serialize(new { SelectedRuntime = selected.Runtime, SelectedReShade = selected.ReShade, SelectedFeeder = selected.Feeder, _settings.ReShadeSource, _settings.RuntimeFolder,
                _componentVerifiedAt, _componentWorking, Reports = reports.Select(r => new { r.Group.Id, r.Missing, r.Different }),
                Addons = Updates.Sources.Select(s => new { s.Id, Version = Updates.InUse(s).Version, Updated = Updates.InUse(s).Updated }) });
            if (_componentViewStamp == stamp) { await RefreshRuntimeUpdatesAsync(); return; }
            ComponentHealthRows.Children.Clear();
            foreach (var report in reports)
            {
                var group = report.Group;
                if (group.Id == "runtime") continue; // Shown as five separately updatable runtime rows.
                var body = new StackPanel { Spacing = 3 };
                body.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeights.SemiBold });
                var selection = group.Id switch
                {
                    "runtime" => (string.IsNullOrWhiteSpace(_settings.RuntimeFolder) ? "Automatic" : "Custom folder requested") +
                        (selected.Runtime.Length == 0 ? " · No runtimes found" : " · " + selected.Runtime),
                    "reshade" => selected.ReShade + (string.IsNullOrWhiteSpace(_settings.ReShadeSource) ? " · Automatic" : " · Custom source requested"),
                    "feeder" when selected.Feeder is not null => selected.Feeder,
                    _ when Updates.Sources.FirstOrDefault(s => s.Id == group.Id) is { } source =>
                        Updates.InUse(source).Version + (Updates.InUse(source).Updated ? " · Accepted update" : " · Included build"),
                    _ => group.Version,
                };
                body.Children.Add(new TextBlock
                {
                    Text = $"Included: {group.Version}\nSelected: {selection}", FontSize = 12,
                    Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap
                });
                body.Children.Add(new TextBlock
                {
                    Text = !verified ? "Not verified this session · Choose Verify files to check SHA-256" : report.Ready ? $"Last verification passed · {group.Files.Count} file(s) match SHA-256"
                        : $"Included build needs repair · {report.Missing} missing · {report.Different} changed or unreadable",
                    FontSize = 12, Foreground = Res(!verified ? "MutedText" : report.Ready ? "AccentText" : "WarnText"), TextWrapping = TextWrapping.Wrap
                });
                if (verified && !report.Ready) body.Children.Add(new TextBlock
                {
                    Text = string.Join("\n", report.Problems.Take(3)) + (report.Problems.Count > 3 ? $"\n…and {report.Problems.Count - 3} more" : ""),
                    FontSize = 11, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap
                });
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Background = Res("RowBackground") };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(body);
                if (!report.Ready)
                {
                    var repair = new Button { Content = "Repair", IsEnabled = !_componentWorking, VerticalAlignment = VerticalAlignment.Center };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(repair, "Repair " + group.Name);
                    repair.Click += async (_, _) => await RepairComponentsAsync(group);
                    Grid.SetColumn(repair, 1);
                    row.Children.Add(repair);
                }
                ComponentHealthRows.Children.Add(row);
                await Task.Delay(16);
                if (read != _componentRead) return;
            }
            ComponentHealthStatus.Text = !verified ? "Included files have not been verified this session. Verify files runs a fresh check; opening Settings does not re-hash the payload." : $"{reports.Sum(r => r.Group.Files.Count) - reports.Sum(r => r.Missing + r.Different)} of {reports.Sum(r => r.Group.Files.Count)} included files verified. " +
                $"Last verified {_componentVerifiedAt:t}. Custom sources and accepted updates are shown as selections.";
            RepairComponentsButton.IsEnabled = (!verified || reports.Any(r => !r.Ready)) && !_componentWorking;
            _componentViewStamp = stamp;
            await RefreshRuntimeUpdatesAsync();
        }
        catch (Exception ex)
        {
            if (read == _componentRead) ComponentHealthStatus.Text = "Could not check components: " + ex.Message;
        }
        finally { if (read == _componentRead) VerifyComponentsButton.IsEnabled = !_componentWorking; }
    }

    private async Task RepairComponentsAsync(ComponentGroup? requested)
    {
        if (_componentWorking || _busy || _bulkRunning || _appUpdating || _updating)
        {
            ComponentHealthStatus.Text = "Wait for the current install or update to finish, then repair components.";
            return;
        }
        _componentWorking = true;
        _updating = true;
        SetBusy(true, "Repairing components…");
        RefreshLibrarySummary();
        VerifyComponentsButton.IsEnabled = RepairComponentsButton.IsEnabled = CheckUpdatesButton.IsEnabled = false;
        string result;
        try
        {
            var groups = requested is null ? ComponentHealth.Groups : new[] { requested };
            int repaired = 0;
            var progress = new Progress<string>(text => ComponentHealthStatus.Text = text);
            foreach (var group in groups)
            {
                if ((await Task.Run(() => ComponentHealth.Inspect(group))).Ready) continue;
                await Task.Run(() => ComponentHealth.RepairAsync(group, progress));
                if (!(await Task.Run(() => ComponentHealth.Inspect(group))).Ready)
                    throw new InstallException(group.Name + " is still incomplete.");
                repaired++;
                AppPaths.Log("Component repair: " + group.Name + " " + group.Version + " verified in the repair cache.");
            }
            result = repaired == 0 ? "All included components are already verified."
                : $"Repaired {repaired} component set(s). The checked copies are ready for new setups.";
        }
        catch (Exception ex)
        {
            result = "Repair stopped: " + ex.Message;
            AppPaths.Log("Component repair stopped: " + ex);
        }
        finally
        {
            _componentWorking = false;
            _updating = false;
            SetBusy(false);
            RefreshLibrarySummary();
            CheckUpdatesButton.IsEnabled = true;
        }
        await RefreshSettingsPage();
        await RefreshComponentsAsync(verify: true);
        ComponentHealthStatus.Text = result;
    }
}
