using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using DLSS5Master.Core;

namespace DLSS5Master;

/// <summary>
/// Game panel → "How to run DLSS 5": one card per route. The cards only show and set the choice; BackendBox and
/// RouteBox (hidden) stay the source of truth that RefreshRoutes and the installer read.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly Route[] CardOrder = { Route.Native, Route.Multipass, Route.Feeder, Route.OptiScaler, Route.NrAddon, Route.Uplift };

    private static (string Glyph, string Title, string Line) CardText(Route r) => r switch
    {
        Route.Native => ("", "Native", "RenoDX DLSS5 add-on in ReShade"),
        Route.Multipass => ("", "Multipass", "RenoDX DLSS Tool, several neural passes"),
        Route.Feeder => ("", "Feeder", "For games without their own DLSS"),
        Route.Uplift => ("\uE945", "GITC Uplift", "Neural rendering from the game's backbuffer"),
        Route.NeuralMaster => ("\uE713", "NeuralMaster", "View the latest native-input path"),
        Route.NrAddon => ("", "DLSS5 NR", "Your locally supplied add-on, loaded through ReShade."),
        _ => ("", "OptiScaler", "DLSS-NR through OptiScaler, no ReShade"),
    };

    private readonly Dictionary<Route, Button> _routeCards = new();

    private Route? RecommendedGameRoute(List<RouteOption> routes, int bits)
    {
        if (_scan is null) return null;
        if (SelectedApi != RenderApi.Vulkan) return Installers.RecommendedRoute(SelectedApi, bits, _scan.HasNativeDlss);
        if (routes.Any(r => r.Route == Route.NrAddon && r.Unavailable is null)) return Route.NrAddon;
        if (routes.Any(r => r.Route == Route.Feeder && r.Unavailable is null)) return Route.Feeder;
        if (_scan.HasNativeDlss && routes.Any(r => r.Route == Route.OptiScaler && r.Unavailable is null)) return Route.OptiScaler;
        return routes.Any(r => r.Route == Route.Uplift && r.Unavailable is null) ? Route.Uplift : null;
    }

    /// <summary>Builds the cards when the available routes change; otherwise only updates which one looks selected.</summary>
    private void RenderRouteCards(List<RouteOption> routes, bool opti, int bits)
    {
        if (_scan is null) return;
        var recommended = RecommendedGameRoute(routes, bits);
        var installed = routes.FirstOrDefault(r => Installers.RouteKey(r.Route) == _scan.Manifest?.Route)?.Route;
        var signature = string.Join("|", routes.Select(r => $"{r.Route}:{r.Label}:{r.Description}:{r.Unavailable}")) + $"|{recommended}|{installed}";
        if (!Equals(RouteCards.Tag, signature))
        {
            RouteCards.Children.Clear();
            _routeCards.Clear();
            foreach (var route in CardOrder)
            {
                var option = routes.FirstOrDefault(o => o.Route == route);
                if (option is null) continue;
                RouteCards.Children.Add(BuildRouteCard(option, route == recommended && option.Unavailable is null, route == installed));
            }
            LayoutRouteCards(RouteCards.ActualWidth);
            RouteCards.Tag = signature;
        }

        Route? selected = opti ? Route.OptiScaler : (RouteBox.SelectedItem as ComboBoxItem)?.Tag as Route?;
        foreach (var (route, button) in _routeCards)
        {
            bool on = route == selected;
            button.BorderBrush = on ? Res("AccentText") : Res("RowBorder");
            button.BorderThickness = new Thickness(on ? 2 : 1);
            button.Background = on ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x6F, 0xD3, 0x4A)) : Res("RowBackground");
        }
    }

    private void RouteCards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
            LayoutRouteCards(e.NewSize.Width);
    }

    private void LayoutRouteCards(double width)
    {
        var count = RouteCards.Children.Count;
        if (count == 0) return;
        const double minimumCardWidth = 166;
        var spacing = RouteCards.ColumnSpacing;
        var maxColumns = width > 0 ? Math.Max(1, (int)((width + spacing) / (minimumCardWidth + spacing))) : 5;
        var rows = (count + maxColumns - 1) / maxColumns;
        var columns = (count + rows - 1) / rows;
        if (RouteCards.ColumnDefinitions.Count != columns)
        {
            RouteCards.ColumnDefinitions.Clear();
            for (int i = 0; i < columns; i++)
                RouteCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        if (RouteCards.RowDefinitions.Count != rows)
        {
            RouteCards.RowDefinitions.Clear();
            for (int i = 0; i < rows; i++)
                RouteCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (int i = 0; i < count; i++)
        {
            if (RouteCards.Children[i] is not FrameworkElement card) continue;
            Grid.SetRow(card, i / columns);
            Grid.SetColumn(card, i % columns);
        }
    }

    private FrameworkElement BuildRouteCard(RouteOption option, bool recommended, bool installed)
    {
        var (glyph, title, line) = CardText(option.Route);
        if (option.Route == Route.NrAddon) title = option.Label;
        bool available = option.Route == Route.NeuralMaster || option.Unavailable is null;

        var content = new StackPanel { Spacing = 6 };
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Foreground = available ? Res("AccentText") : Res("MutedText") });
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(heading, 1);
        head.Children.Add(heading);
        content.Children.Add(head);
        content.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap, MaxLines = 2 });

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        if (installed) badges.Children.Add(Pill("Installed", "AccentText"));
        if (option.Route == Route.NeuralMaster) badges.Children.Add(Pill("In development", "MutedText"));
        if (recommended) badges.Children.Add(Pill("Recommended", "AccentText"));
        if (!available) badges.Children.Add(Pill("Not for this game", "WarnText"));
        if (badges.Children.Count > 0) content.Children.Add(badges);

        var button = new Button
        {
            Content = content, IsEnabled = available,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(12),
            Background = Res("RowBackground"), BorderBrush = Res("RowBorder"),
        };
        AutomationName(button, $"{title} route{(available ? "" : ", not available")}");
        // Picking happens after the click finishes: the pick rebuilds route state, which must not run inside the click.
        button.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (option.Route == Route.NeuralMaster) ShowNeuralMasterPath();
            else PickRoute(option.Route);
        });
        _routeCards[option.Route] = button;

        // A disabled button shows no tooltip, so the reason sits on a wrapper.
        var wrapper = new Border { Child = button, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(wrapper, available ? option.Route == Route.NeuralMaster ? "View the latest NeuralMaster path and build status" : option.Description : option.Unavailable);
        return wrapper;
    }

    private void PickRoute(Route route)
    {
        if (_scan is null || _busy) return;
        if (route == Route.NeuralMaster) { ShowNeuralMasterPath(); return; }
        if (route == Route.OptiScaler) { BackendBox.SelectedIndex = 1; return; }
        if (BackendBox.SelectedIndex != 0) BackendBox.SelectedIndex = 0;
        var item = RouteBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is Route r && r == route && i.IsEnabled);
        if (item is not null && !ReferenceEquals(RouteBox.SelectedItem, item)) RouteBox.SelectedItem = item;
    }

    private Border Pill(string text, string colorKey) => new()
    {
        Padding = new Thickness(7, 1, 7, 1), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = Res(colorKey),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Res(colorKey) },
    };

    private static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
