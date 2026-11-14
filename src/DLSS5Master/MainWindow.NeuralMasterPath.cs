using DLSS5Master.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DLSS5Master;

public sealed partial class MainWindow
{
    private void InitNeuralMasterPath()
    {
        var host = NeuralMasterPathContent;
        var title = new StackPanel { Spacing = 5 };
        title.Children.Add(new TextBlock { Text = "NEURALMASTER", FontSize = 13, CharacterSpacing = 180, Foreground = Res("AccentText") });
        title.Children.Add(new TextBlock { Text = "Native-guide rendering path", FontSize = 32, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        title.Children.Add(new TextBlock { Text = "Game inputs, neural passes, Look and HDR — developed as one independent renderer.", FontSize = 15, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap });
        host.Children.Add(title);
        var status = new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Title = "Experimental trial", Message = NeuralMasterDevelopmentPath.BuildStatus
        };
        host.Children.Add(status);
        host.Children.Add(new Border
        {
            Padding = new Thickness(18), CornerRadius = new CornerRadius(12), Background = Res("RowBackground"),
            BorderBrush = Res("AccentText"), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = NeuralMasterDevelopmentPath.Summary, FontSize = 15, TextWrapping = TextWrapping.Wrap }
        });
        foreach (var stage in NeuralMasterDevelopmentPath.Stages)
        {
            var row = new Grid { Padding = new Thickness(18, 14, 18, 14), ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var number = new TextBlock { Text = stage.Number, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Res("AccentText") };
            row.Children.Add(number);
            var body = new StackPanel { Spacing = 5 };
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            heading.Children.Add(new TextBlock { Text = stage.Title, FontSize = 17, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(Pill(stage.Status, "MutedText"));
            body.Children.Add(heading);
            body.Children.Add(new TextBlock { Text = stage.Description, FontSize = 13, Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(body, 1); row.Children.Add(body);
            host.Children.Add(new Border { Child = row, Background = Res("RowBackground"), BorderBrush = Res("RowBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) });
        }
        var fallback = new StackPanel { Spacing = 8 };
        fallback.Children.Add(new TextBlock { Text = "Built-in fallback routes · in development", FontSize = 20, FontWeight = FontWeights.SemiBold });
        fallback.Children.Add(new TextBlock { Text = "NeuralMaster's design does not require an external Feeder, Lumenite or depth add-on. The native-DLSS trial already receives the game's guides. For games without DLSS, reliable depth discovery and a real-frame NVIDIA Optical Flow producer remain unfinished; optical flow estimates motion, not depth. The separate 64-bit helper has transport checks, but complete older-API and 32-bit game rendering is not ready.", TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedText") });
        host.Children.Add(fallback);
        host.Children.Add(new TextBlock { Text = "The finished-picture prototype has been removed. The native-input path is still in development; full parity, a flicker fix and a GPU-time advantage have not been demonstrated.", TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedText"), FontSize = 12 });
        var back = new Button { Content = "Back to games" };
        back.Click += (_, _) => { Tabs.SelectedItem = TabGames; if (_game is not null) GameOverlay.Visibility = Visibility.Visible; };
        host.Children.Add(back);
    }

    private void ShowNeuralMasterPath()
    {
        GameOverlay.Visibility = Visibility.Collapsed;
        Tabs.SelectedItem = TabNeuralMaster;
    }
}
