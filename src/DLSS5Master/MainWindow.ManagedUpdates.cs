using Microsoft.UI.Windowing;
using Windows.Foundation;
using DLSS5Master.Views;
using DLSS5Master.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DLSS5Master;

public sealed partial class MainWindow
{
    private async void ManagedUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _bulkRunning || _appUpdating || !_libraryScanned) return;
        var games = _visible.ToList();
        var body = new StackPanel { Spacing = 10, Width = 700 };
        body.Children.Add(Muted("Reinstall selected managed games with the components currently selected in Settings. Each game keeps its route, OptiScaler build, hooks and in-game settings. Working MFG providers are retained. Development routes, anti-cheat games and other installers are excluded. Check for component updates in Settings first."));
        var list = new StackPanel { Spacing = 4 };
        var detail = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 260, TextWrapping = TextWrapping.NoWrap };
        body.Children.Add(list);
        body.Children.Add(detail);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Update installed games", Content = new ScrollViewer { Content = body },
            PrimaryButtonText = "Preview selected", IsPrimaryButtonEnabled = false, CloseButtonText = "Close" };
        dialog.Resources["ContentDialogMaxWidth"] = 780.0;
        var candidates = new List<(GameItem Item, CheckBox Box)>();
        var prepared = new List<(GameItem Item, InstallRequest Request, string Record)>();
        bool working = true, stop = false, closeAsked = false;
        TypedEventHandler<AppWindow, AppWindowClosingEventArgs> closing = (_, args) => { if (working) { args.Cancel = true; stop = closeAsked = true; } };
        AppWindow.Closing += closing;
        _busy = true;
        try
        {
            var context = await Task.Run(() => BulkContext.Read(_settings, _components));
            foreach (var item in games)
            {
                try
                {
                    var row = await Task.Run(() => ManagedGameUpdates.Plan(item.Entry, _settings.OverrideOf(item.Entry), context));
                    if (row.Request is null) { list.Children.Add(Muted(item.Name + ": " + row.Skip)); continue; }
                    var box = new CheckBox { Content = item.Name + " · " + Installers.RouteLabel(Installers.RouteKey(row.Request.Route)), IsChecked = false };
                    box.Click += (_, _) => { prepared.Clear(); dialog.PrimaryButtonText = "Preview selected"; detail.Text = "Selection changed. Preview again."; dialog.IsPrimaryButtonEnabled = candidates.Any(c => c.Box.IsChecked == true); };
                    candidates.Add((item, box)); list.Children.Add(box);
                }
                catch (Exception ex) { list.Children.Add(Muted(item.Name + ": " + ex.Message)); }
            }
        }
        catch (Exception ex) { detail.Text = ex.Message; }
        finally { _busy = false; working = false; }
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (working || _busy || _appUpdating) return;
            working = _busy = true;
            stop = false;
            dialog.IsPrimaryButtonEnabled = false;
            list.IsHitTestVisible = false;
            foreach (var candidate in candidates) candidate.Box.IsEnabled = false;
            try
            {
                if (prepared.Count == 0)
                {
                    var lines = new List<string>();
                    var ready = new List<(GameItem Item, InstallRequest Request, string Record)>();
                    var context = await Task.Run(() => BulkContext.Read(_settings, _components));
                    foreach (var (item, box) in candidates.Where(c => c.Box.IsChecked == true))
                    {
                        if (stop) break;
                        var row = await Task.Run(() => ManagedGameUpdates.Plan(item.Entry, _settings.OverrideOf(item.Entry), context));
                        if (row.Request is not { } req) { lines.Add(item.Name + ": skipped — " + row.Skip); continue; }
                        InstallPreview? plan = null;
                        await Task.Run(() => Installers.InstallAsync(req, _components, _ => { }, null, CancellationToken.None, p => plan = p, previewOnly: true));
                        var record = await Task.Run(() => File.ReadAllText(Journal.ManifestPath(item.Entry.Dir)));
                        ready.Add((item, req, record));
                        lines.Add(item.Name + "\n" + plan?.Describe());
                    }
                    if (!stop) prepared = ready;
                    detail.Text = string.Join("\n\n", lines);
                    dialog.PrimaryButtonText = prepared.Count > 0 ? "Update reviewed games" : "Preview selected";
                }
                else
                {
                    var lines = new List<string>();
                    foreach (var (item, req, record) in prepared)
                    {
                        if (stop) break;
                        try
                        {
                            if (File.ReadAllText(Journal.ManifestPath(req.GameDir)) != record) throw new InstallException("Install record changed; preview again.");
                            await Task.Run(() => Installers.InstallAsync(req, _components, _ => { }, null, CancellationToken.None));
                            lines.Add(item.Name + ": updated; route and settings retained.");
                        }
                        catch (Exception ex) { lines.Add(item.Name + ": " + ex.Message); }
                        detail.Text = string.Join("\n", lines);
                    }
                    if (stop) detail.Text += "\nStopped before the next game.";
                    prepared.Clear();
                    dialog.PrimaryButtonText = "Preview selected";
                }
            }
            catch (Exception ex) { prepared.Clear(); detail.Text = "Stopped: " + ex.Message; dialog.PrimaryButtonText = "Preview selected"; }
            finally { working = _busy = false; list.IsHitTestVisible = true; foreach (var candidate in candidates) candidate.Box.IsEnabled = true; if (closeAsked) { dialog.Hide(); Close(); } dialog.IsPrimaryButtonEnabled = candidates.Any(c => c.Box.IsChecked == true); RefreshLibrarySummary(); }
        };
        dialog.Closing += (_, args) => { if (working) { args.Cancel = true; stop = true; detail.Text += "\nStopping after the current safe operation…"; } };
        try { await dialog.ShowAsync(); } finally { AppWindow.Closing -= closing; }
        if (!_busy && !closeAsked) await LoadLibraryAsync();
    }
}
