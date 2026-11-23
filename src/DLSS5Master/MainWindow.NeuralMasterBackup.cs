using DLSS5Master.Core;
using DLSS5Master.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DLSS5Master;

public sealed partial class MainWindow
{
    private enum NeuralMasterBackupAction { Uninstall, Restore, Recover }

    private readonly NeuralMasterSnapshots _neuralMasterSnapshots = new();
    private NeuralMasterSnapshotStatus? _neuralMasterStatus;
    private string? _neuralMasterMessage;
    private int _neuralMasterRead;
    private bool _neuralMasterReading;
    private bool _neuralMasterWorking;

    private async void NeuralMasterUninstall_Click(object sender, RoutedEventArgs e) => await RunNeuralMasterActionAsync(NeuralMasterBackupAction.Uninstall);
    private async void NeuralMasterRestore_Click(object sender, RoutedEventArgs e) => await RunNeuralMasterActionAsync(NeuralMasterBackupAction.Restore);
    private async void NeuralMasterRecover_Click(object sender, RoutedEventArgs e) => await RunNeuralMasterActionAsync(NeuralMasterBackupAction.Recover);

    private void ResetNeuralMasterGameBar(bool checking = false)
    {
        ++_neuralMasterRead;
        _neuralMasterReading = checking;
        SetNeuralMasterResult(null);
        DisplayNeuralMasterStatus(null);
    }

    private bool IsCurrentNeuralMasterGame(GameItem game, string exe)
        => ReferenceEquals(game, _game) && string.Equals(exe, SelectedExe?.Path, StringComparison.OrdinalIgnoreCase);

    private Task<NeuralMasterSnapshotStatus?> InspectNeuralMasterAsync(string folder, string exe, Manifest? manifest) =>
        Task.Run<NeuralMasterSnapshotStatus?>(() =>
        {
            if (!NeuralMasterSnapshots.HasGameState(folder, exe, manifest)) return null;
            var result = _neuralMasterSnapshots.Inspect(folder, exe);
            return result.State != NeuralMasterSnapshotState.NotFound ||
                   string.Equals(manifest?.Route, "neuralmaster", StringComparison.OrdinalIgnoreCase)
                ? result : null;
        });

    private async Task RefreshNeuralMasterStatusAsync()
    {
        int read = ++_neuralMasterRead;
        if (_game is not { } game || _scan is not { } scan || SelectedExe?.Path is not { } exe)
        {
            _neuralMasterReading = false;
            DisplayNeuralMasterStatus(null);
            RefreshGameRouteWarning();
            return;
        }

        _neuralMasterReading = true;
        UpdateNeuralMasterButtons();
        RefreshGameRouteWarning();
        NeuralMasterSnapshotStatus? status = null;
        try
        {
            status = await InspectNeuralMasterAsync(scan.Dir, exe, scan.Manifest);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[{game.Name}] Could not check NeuralMaster: {ex.Message}");
        }
        if (read != _neuralMasterRead || !IsCurrentNeuralMasterGame(game, exe)) return;
        _neuralMasterReading = false;
        DisplayNeuralMasterStatus(status);
        RefreshGameRouteWarning();
    }

    private void DisplayNeuralMasterStatus(NeuralMasterSnapshotStatus? status)
    {
        _neuralMasterStatus = status;
        NeuralMasterGameBar.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        NeuralMasterGameBuild.Text = status switch
        {
            { State: NeuralMasterSnapshotState.Installed, Build: { Length: > 0 } build } => $"NeuralMaster · {build}",
            { Build: { Length: > 0 } build } => $"NeuralMaster · saved build {build}",
            _ => "NeuralMaster",
        };
        RefreshNeuralMasterStateLine();
        UpdateNeuralMasterButtons();
    }

    private void UpdateNeuralMasterButtons()
    {
        bool idle = !_busy && !_neuralMasterReading && !_neuralMasterWorking;
        ExeBox.IsEnabled = !_neuralMasterWorking;
        var status = _neuralMasterStatus;
        SetAction(NeuralMasterUninstallButton, status?.CanUninstall == true);
        SetAction(NeuralMasterRestoreButton, status?.CanRestore == true || status?.CanSwitch == true);
        SetAction(NeuralMasterRecoverButton, status?.CanRecover == true);

        void SetAction(Button button, bool available)
        {
            button.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            button.IsEnabled = idle && available;
        }
    }

    private void RefreshNeuralMasterStateLine()
    {
        var status = _neuralMasterStatus;
        var note = _neuralMasterMessage ?? (status switch
        {
            { State: NeuralMasterSnapshotState.Installed } => "Installed",
            { State: NeuralMasterSnapshotState.Uninstalled } => "Removed · ready to restore",
            { State: NeuralMasterSnapshotState.NotFound } => "The recorded setup could not be verified.",
            { CanSwitch: true } => "Restore will back up the current setup.",
            _ => status?.Message,
        });
        NeuralMasterBackupDetails.Text = note ?? "";
        NeuralMasterBackupDetails.Visibility = string.IsNullOrWhiteSpace(note) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetNeuralMasterResult(string? message, bool error = false)
    {
        _neuralMasterMessage = message;
        if (error) NeuralMasterBackupDetails.Foreground = Res("WarnText");
        else NeuralMasterBackupDetails.ClearValue(TextBlock.ForegroundProperty);
        RefreshNeuralMasterStateLine();
    }

    private async Task RunNeuralMasterActionAsync(NeuralMasterBackupAction action)
    {
        if (_neuralMasterWorking || _neuralMasterReading || _game is not { } game ||
            _scan is not { } scan || SelectedExe?.Path is not { } exe) return;
        if (_busy || _bulkRunning || _appUpdating)
        {
            SetNeuralMasterResult("Wait for the other setup or library refresh to finish, then try again.", error: true);
            return;
        }

        _neuralMasterWorking = true;
        ++_neuralMasterRead;
        UpdateNeuralMasterButtons();
        RefreshGameRouteWarning();
        SetNeuralMasterResult(null);
        bool writing = false;
        var folder = scan.Dir;
        try
        {
            var status = await InspectNeuralMasterAsync(folder, exe, scan.Manifest);
            if (!IsCurrentNeuralMasterGame(game, exe) || GameOverlay.Visibility != Visibility.Visible) return;
            DisplayNeuralMasterStatus(status);
            RefreshGameRouteWarning();
            if (status is null) return;
            if (_busy || _bulkRunning || _appUpdating)
                throw new InstallException("Wait for the other setup to finish, then try again.");
            bool switchAlternate = action == NeuralMasterBackupAction.Restore && !status.CanRestore && status.CanSwitch;
            if (!await ConfirmNeuralMasterActionAsync(game.Name, exe, action, status, switchAlternate)) return;
            if (_busy || _bulkRunning || _appUpdating || !IsCurrentNeuralMasterGame(game, exe) ||
                GameOverlay.Visibility != Visibility.Visible)
                throw new InstallException("The library or setup changed. Refresh this game and review the action again.");

            SetBusy(true, "Updating NeuralMaster…");
            writing = true;
            SetNeuralMasterResult("Working…");
            var result = await Task.Run(() =>
            {
                string? message = null;
                void Report(string line) => message = line;
                switch (action)
                {
                    case NeuralMasterBackupAction.Uninstall:
                        _neuralMasterSnapshots.Uninstall(folder, exe, status.Fingerprint, Report);
                        break;
                    case NeuralMasterBackupAction.Restore:
                        _neuralMasterSnapshots.Restore(folder, exe, switchAlternate, status.Fingerprint, Report);
                        break;
                    case NeuralMasterBackupAction.Recover:
                        _neuralMasterSnapshots.Recover(folder, exe, Report);
                        break;
                }
                return message;
            });
            var message = action switch
            {
                NeuralMasterBackupAction.Uninstall => "Backup verified.",
                NeuralMasterBackupAction.Restore when switchAlternate => "NeuralMaster restored. The alternate setup was backed up.",
                NeuralMasterBackupAction.Restore => "NeuralMaster restored.",
                _ => "The interrupted change was recovered.",
            };
            SetNeuralMasterResult(null);
            Log(result ?? message);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[{game.Name}] NeuralMaster {action} failed: {ex.Message}");
            SetNeuralMasterResult(ex.Message, error: true);
            Log("✖ " + ex.Message);
        }
        finally
        {
            if (writing) SetBusy(false);
            _neuralMasterWorking = false;
            UpdateNeuralMasterButtons();
            RefreshGameRouteWarning();
            if (writing && IsCurrentNeuralMasterGame(game, exe)) await RescanAsync(exe);
        }
    }

    private async Task<bool> ConfirmNeuralMasterActionAsync(string game, string exe, NeuralMasterBackupAction action,
        NeuralMasterSnapshotStatus status, bool switchAlternate)
    {
        (string Title, string Explanation, IReadOnlyList<string> Paths)? description = action switch
        {
            NeuralMasterBackupAction.Uninstall when status.CanUninstall =>
                ("Remove NeuralMaster", "Save a new backup of the installed build and remove these NeuralMaster add-on copies.", status.UninstallPaths),
            NeuralMasterBackupAction.Restore when switchAlternate =>
                ("Back up and switch", "Back up the identified alternate installation and restore the saved NeuralMaster files.", status.RestorePaths),
            NeuralMasterBackupAction.Restore when status.CanRestore =>
                ("Restore NeuralMaster", "Restore these files from the verified backup.", status.RestorePaths),
            NeuralMasterBackupAction.Recover when status.CanRecover =>
                ("Recover interrupted change", "Use the rollback journal to recover the setup for this game.", Array.Empty<string>()),
            _ => null,
        };
        if (description is not { } actionInfo) return false;

        var preview = new List<string> { game, exe, "", actionInfo.Explanation };
        preview.AddRange(actionInfo.Paths);
        if (switchAlternate && status.ConflictPaths.Count > 0)
        {
            preview.AddRange(["", "Alternate installation files"]);
            preview.AddRange(status.ConflictPaths);
        }
        if (action == NeuralMasterBackupAction.Restore && status.BackupLocation is { Length: > 0 } backup)
        {
            string date = status.BackupDate is { } saved ? $" · saved {saved.ToLocalTime():g}" : "";
            preview.AddRange(["", $"Backup location{date}", backup]);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = actionInfo.Title,
            Content = new ScrollViewer
            {
                MaxHeight = 385,
                Content = new TextBlock { Text = string.Join("\n", preview), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            PrimaryButtonText = actionInfo.Title,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
