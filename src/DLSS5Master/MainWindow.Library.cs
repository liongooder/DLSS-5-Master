using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using DLSS5Master.Core;
using DLSS5Master.Views;

namespace DLSS5Master;

/// <summary>The Games page around the grid: search and filters, totals and store groups.</summary>
public sealed partial class MainWindow
{
    private readonly CollectionViewSource _gamesView = new() { IsSourceGrouped = true };
    private bool _holdFilter;

    private static readonly Dictionary<string, string> StoreNames = new()
    {
        ["Folder"] = "Your folders",
        ["Manual"] = "Added by hand",
    };

    private static string Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private bool FiltersActive => (SearchBox.Text?.Trim() ?? "") != "" || Tag(ApiFilter) != "" || Tag(DlssFilter) != "" || Tag(StateFilter) != "";

    private static bool ApiMatches(GameItem g, string api) => api switch
    {
        "dx12" => g.Api == RenderApi.DX12,
        "dx11" => g.Api == RenderApi.DX11,
        "vulkan" => g.Api == RenderApi.Vulkan,
        "older" => g.Api is RenderApi.DX10 or RenderApi.DX9 or RenderApi.DX8 or RenderApi.DirectDraw or RenderApi.OpenGL,
        "unknown" => g.Scan is not null && g.Api == RenderApi.Unknown,
        _ => true,
    };

    private static bool DlssMatches(GameItem g, string dlss) => dlss switch
    {
        "dlss" => g.HasDlss,
        "fg" => g.HasFrameGen,
        "none" => g.Scan is not null && !g.HasDlss,
        _ => true,
    };

    private static bool StateMatches(GameItem g, string state) => state switch
    {
        "ready" => g.IsReady,
        "installed" => g.IsInstalled,
        "other" => g.IsModdedElsewhere,
        "clean" => g.Scan is not null && !g.IsInstalled && !g.IsModdedElsewhere,
        _ => true,
    };

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void LibraryFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_holdFilter && GameGrid is not null) ApplyFilter();
    }

    /// <summary>
    /// Shows the games that pass the search and filters, grouped by store. The grid is rebuilt only when the list
    /// changed (or <paramref name="force"/> is set, e.g. after a scan), keeping the scroll position.
    /// </summary>
    private void ApplyFilter(bool force = false)
    {
        var q = SearchBox.Text?.Trim() ?? "";
        string api = Tag(ApiFilter), dlss = Tag(DlssFilter), state = Tag(StateFilter);
        var list = _all.Where(g => (q == "" || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                                   && ApiMatches(g, api) && DlssMatches(g, dlss) && StateMatches(g, state)).ToList();
        if (force || !list.SequenceEqual(_visible) || GameGrid.ItemsSource is null)
        {
            _visible = list;
            // Biggest store first; games keep their alphabetical order inside each store.
            var groups = list.GroupBy(g => StoreNames.GetValueOrDefault(g.Launcher, g.Launcher))
                             .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                             .Select(g => new GameGroup(g.Key, g)).ToList();
            var scroll = FindScrollViewer(GameGrid);
            double offset = scroll?.VerticalOffset ?? 0;
            _gamesView.Source = groups;
            GameGrid.ItemsSource = _gamesView.View;
            if (scroll is not null && offset > 0)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => scroll.ChangeView(null, offset, null, true));
            UpdateEmptyState(libraryLoaded: LoadingPanel.Visibility != Visibility.Visible);
        }
        RefreshLibrarySummary();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>The "N of M shown" count and the clickable totals.</summary>
    private void RefreshLibrarySummary()
    {
        bool active = FiltersActive;
        LibraryCount.Text = _all.Count == 0 ? "" : active ? $"{_visible.Count} of {_all.Count} shown" : $"{_all.Count} games";
        ClearFiltersButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        SummaryChips.Children.Clear();
        if (_all.Count > 0)
        {
            AddChip(_all.Count(g => g.Api == RenderApi.DX12), "on DirectX 12", () => ApiFilter.SelectedIndex = 1);
            AddChip(_all.Count(g => g.IsReady), "ready for DLSS 5", () => StateFilter.SelectedIndex = 1);
            AddChip(_all.Count(g => g.HasDlss), "with DLSS", () => DlssFilter.SelectedIndex = 1);
            AddChip(_all.Count(g => g.HasFrameGen), "with Frame Generation", () => DlssFilter.SelectedIndex = 2);
            AddChip(_all.Count(g => g.IsInstalled), "set up with DLSS 5 Master", () => StateFilter.SelectedIndex = 2);
        }
    }

    private void AddChip(int count, string label, Action apply)
    {
        var chip = new Button
        {
            Content = $"{count} {label}", CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 4, 12, 5), FontSize = 13,
            Background = Res("RowBackground"), BorderBrush = Res("RowBorder"),
        };
        ToolTipService.SetToolTip(chip, "Show only these games");
        chip.Click += (_, _) => apply();
        SummaryChips.Children.Add(chip);
    }
}
