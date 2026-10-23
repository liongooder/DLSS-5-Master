using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DLSS5Master;

/// <summary>Small motion and empty-state touches: page fade-in, poster hover, the "nothing to show" panel.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Fades and slides an element in. The element declares OpacityTransition / TranslationTransition in XAML, so
    /// setting the start values and then the end values one frame later animates between them.
    /// </summary>
    private void Reveal(UIElement element)
    {
        element.Opacity = 0;
        element.Translation = new Vector3(0, 14, 0);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            element.Opacity = 1;
            element.Translation = Vector3.Zero;
        });
    }

    private void PosterCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid poster) return;
        poster.Scale = new Vector3(1.035f, 1.035f, 1);
        poster.BorderBrush = Res("AccentText");
    }

    private void PosterCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid poster) return;
        poster.Scale = Vector3.One;
        poster.BorderBrush = Res("RowBorder");
    }

    /// <summary>Shows the empty panel when the library has no games, or none match the search / filter.</summary>
    private void UpdateEmptyState(bool libraryLoaded = true)
    {
        bool none = libraryLoaded && _all.Count == 0;
        bool noMatch = !none && _all.Count > 0 && _visible.Count == 0;
        EmptyPanel.Visibility = none || noMatch ? Visibility.Visible : Visibility.Collapsed;
        if (none)
        {
            EmptyTitle.Text = "No games found";
            EmptyText.Text = "DLSS 5 Master looks in Steam, Epic, GOG, Ubisoft and Xbox. Add a game by its .exe, or add the folder where your games are.";
        }
        else if (noMatch)
        {
            EmptyTitle.Text = "No games match";
            EmptyText.Text = "Nothing in your library fits this search or filter.";
        }
        EmptyAdd.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        EmptyFolders.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        EmptyClear.Visibility = noMatch ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EmptyFolders_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = TabSettings;

    private void EmptyClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        FilterBox.SelectedIndex = 0;
        ApplyFilter();
    }
}
