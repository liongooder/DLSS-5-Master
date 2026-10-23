using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DLSS5Master.Core;

namespace DLSS5Master;

/// <summary>
/// Game panel → "How to run DLSS 5": one card per route. The cards only show and set the choice; BackendBox and
/// RouteBox (hidden) stay the source of truth that RefreshRoutes and the installer read.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly Route[] CardOrder = { Route.Native, Route.Multipass, Route.Feeder, Route.OptiScaler };

    private static (string Glyph, string Title, string Line) CardText(Route r) => r switch
    {
        Route.Native => ("", "Native", "RenoDX DLSS5 add-on in ReShade"),
        Route.Multipass => ("", "Multipass", "RenoDX DLSS Tool, several neural passes"),
        Route.Feeder => ("", "Feeder", "For games without their own DLSS"),
        _ => ("", "OptiScaler", "DLSS-NR through OptiScaler, no ReShade"),
    };

    private readonly Dictionary<Route, Button> _routeCards = new();

    /// <summary>Builds the cards when the available routes change; otherwise only updates which one looks selected.</summary>
    private void RenderRouteCards(List<RouteOption> routes, bool opti, int bits)
    {
        if (_scan is null) return;
        var recommended = Installers.RecommendedRoute(SelectedApi, bits, _scan.HasNativeDlss);
        Route? installed = _scan.Manifest?.Route switch
        {
            "native" => Route.Native, "multipass" => Route.Multipass, "feeder" => Route.Feeder, "optiscaler" => Route.OptiScaler, _ => null,
        };
        var signature = string.Join("|", routes.Select(r => $"{r.Route}:{r.Unavailable is null}")) + $"|{recommended}|{installed}";
        if (!Equals(RouteCards.Tag, signature))
        {
            RouteCards.Children.Clear();
            RouteCards.ColumnDefinitions.Clear();
            _routeCards.Clear();
            int col = 0;
            foreach (var route in CardOrder)
            {
                var option = routes.FirstOrDefault(o => o.Route == route);
                if (option is null) continue;
                RouteCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var card = BuildRouteCard(option, route == recommended && option.Unavailable is null, route == installed);
                Grid.SetColumn(card, col++);
                RouteCards.Children.Add(card);
            }
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

    private FrameworkElement BuildRouteCard(RouteOption option, bool recommended, bool installed)
    {
        var (glyph, title, line) = CardText(option.Route);
        bool available = option.Unavailable is null;

        var content = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Foreground = available ? Res("AccentText") : Res("MutedText") });
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15 });
        content.Children.Add(head);
        content.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap, MaxLines = 2 });

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        if (installed) badges.Children.Add(Pill("Installed", "AccentText"));
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
        button.Click += (_, _) => DispatcherQueue.TryEnqueue(() => PickRoute(option.Route));
        _routeCards[option.Route] = button;

        // A disabled button shows no tooltip, so the reason sits on a wrapper.
        var wrapper = new Border { Child = button, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(wrapper, available ? option.Description : option.Unavailable);
        return wrapper;
    }

    private void PickRoute(Route route)
    {
        if (_scan is null || _busy) return;
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
