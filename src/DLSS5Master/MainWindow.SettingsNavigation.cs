using Microsoft.UI.Xaml;

namespace DLSS5Master;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _settingsEntry;
    private bool _settingsReady;
    private int _settingsRead;

    /// <summary>Let the tab transition finish; reusing the page avoids repeated layout and disk work on each visit.</summary>
    private async Task EnterSettingsAsync()
    {
        _settingsEntry?.Cancel();
        using var entry = new CancellationTokenSource();
        _settingsEntry = entry;
        try
        {
            await Task.Delay(260, entry.Token);
            if (Tabs.SelectedItem != TabSettings) return;
            RefreshUpdatesPanel();
            if (!_settingsReady) await RefreshSettingsPage();
            if (!entry.IsCancellationRequested && Tabs.SelectedItem == TabSettings) await LoadActivityLogAsync(onlyIfChanged: true);
        }
        catch (OperationCanceledException) when (entry.IsCancellationRequested) { }
        catch (Exception ex) { Core.AppPaths.Log("Could not refresh Settings: " + ex.Message); }
        finally { if (ReferenceEquals(_settingsEntry, entry)) _settingsEntry = null; }
    }
}
