using DLSS5Master.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DLSS5Master;

public sealed partial class MainWindow
{
    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _bulkRunning || _appUpdating) return;
        _busy = true;
        try
        {
            var games = _all.Select(i => i.Entry).ToList();
            var files = await Task.Run(() => DiagnosticsBundle.Preview(games, _components));
            var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 360 };
            foreach (var name in files.Keys) choices.Items.Add(name);
            choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is string name) text.Text = files[name]; };
            choices.SelectedIndex = 0;
            var body = new StackPanel { Spacing = 10, Width = 660 };
            body.Children.Add(Muted("Review the exact ZIP contents below. Local paths and identity/credential lines are removed from the log. Game names, hardware and relative filenames remain visible. Export saves locally; nothing is sent."));
            body.Children.Add(choices); body.Children.Add(text);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Preview support ZIP", Content = body,
                PrimaryButtonText = "Choose export folder", CloseButtonText = "Cancel" };
            dialog.Resources["ContentDialogMaxWidth"] = 740.0;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var folder = await PickFolderAsync();
            if (folder is null) return;
            var path = Path.Combine(folder, $"DLSS5Master-support-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            await Task.Run(() => DiagnosticsBundle.Export(path, files));
            ActivityStatus.Text = "Saved support ZIP: " + path;
        }
        catch (Exception ex) { ActivityStatus.Text = "Could not export diagnostics: " + ex.Message; }
        finally { _busy = false; RefreshLibrarySummary(); }
    }
}
