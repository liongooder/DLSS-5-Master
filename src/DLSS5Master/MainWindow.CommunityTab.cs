using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using DLSS5Master.Core;
using DLSS5Master.Views;
using Windows.Storage.Pickers;

namespace DLSS5Master;

/// <summary>The COMMUNITY tab: reported games, chat, the moderated gallery and the private line to the developer.</summary>
public sealed partial class MainWindow
{
    private CommunityMe? _me;
    private DispatcherTimer? _chatTimer;
    private long _chatFirst, _chatLast;
    private long? _chatReplyTo;
    private bool _chatLoadingOlder;
    private int _cgOffset, _galleryOffset;
    private byte[]? _privateImage;

    // ------------------------------------------------------------------ tab / sub-tabs

    private async Task EnterCommunityAsync()
    {
        CTabModerate.Visibility = CommunityHub.IsModerator ? Visibility.Visible : Visibility.Collapsed;
        await RefreshMeAsync();
        ShowCommunitySubPage();
    }

    private async Task RefreshMeAsync()
    {
        try
        {
            _me = await CommunityHub.MeAsync();
            NicknameText.Text = _me.Nickname is { } n ? n + (_me.IsDev ? "  · developer" : "") : "Choose a nickname";
            CTabPrivate.Text = _me.UnreadPrivate > 0 ? $"PRIVATE ● {_me.UnreadPrivate}" : "PRIVATE";
        }
        catch (Exception ex) { NicknameText.Text = "Offline"; ToolTipService.SetToolTip(NicknameButton, ex.Message); }
        if (CommunityHub.IsModerator)
        {
            try
            {
                var m = await CommunityHub.ModSummaryAsync();
                int n = m.PendingGallery + m.UnreadPrivate + m.FlaggedNotes;
                CTabModerate.Text = n > 0 ? $"MODERATE ● {n}" : "MODERATE";
            }
            catch { CTabModerate.Text = "MODERATE"; }
        }
    }

    private void CommunityTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowCommunitySubPage();

    private void ShowCommunitySubPage()
    {
        var tab = CommunityTabs.SelectedItem;
        CGamesPage.Visibility = tab == CTabGames ? Visibility.Visible : Visibility.Collapsed;
        CChatPage.Visibility = tab == CTabChat ? Visibility.Visible : Visibility.Collapsed;
        CGalleryPage.Visibility = tab == CTabGallery ? Visibility.Visible : Visibility.Collapsed;
        CPrivatePage.Visibility = tab == CTabPrivate ? Visibility.Visible : Visibility.Collapsed;
        CModeratePage.Visibility = tab == CTabModerate ? Visibility.Visible : Visibility.Collapsed;
        StopChatPolling();
        if (tab == CTabGames) _ = LoadCommunityGamesAsync(reset: true);
        else if (tab == CTabChat) _ = LoadChatAsync();
        else if (tab == CTabGallery) _ = LoadGalleryAsync(reset: true);
        else if (tab == CTabPrivate) _ = LoadPrivateAsync();
        else if (tab == CTabModerate) _ = LoadModerateAsync();
    }

    private void LeaveCommunity() => StopChatPolling();

    private async void CommunityRefreshTab_Click(object sender, RoutedEventArgs e)
    {
        await RefreshMeAsync();
        ShowCommunitySubPage();
    }

    // ------------------------------------------------------------------ nickname

    private async void Nickname_Click(object sender, RoutedEventArgs e) => await PickNicknameAsync();

    /// <summary>Asks for a nickname; true when one is set.</summary>
    private async Task<bool> PickNicknameAsync()
    {
        var box = new TextBox { Text = _me?.Nickname ?? "", PlaceholderText = "3 to 20 letters, digits, spaces, . - _", MaxLength = 20 };
        var error = new TextBlock { Foreground = Res("WarnText"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "This name shows next to your chat messages, pictures and notes. No account or e-mail is needed." });
        panel.Children.Add(box);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Your community nickname", Content = panel,
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        bool saved = false;
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await CommunityHub.SetNicknameAsync(box.Text);
                saved = true;
            }
            catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
        if (saved) await RefreshMeAsync();
        return _me?.Nickname is not null;
    }

    private async Task<bool> EnsureNicknameAsync() => _me?.Nickname is not null || await PickNicknameAsync();

    // ------------------------------------------------------------------ games

    private bool _cgHold;

    private void CgFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_cgHold && CommunityPage is { Visibility: Visibility.Visible }) _ = LoadCommunityGamesAsync(reset: true);
    }

    private void CgSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => _ = LoadCommunityGamesAsync(reset: true);

    /// <summary>Clearing the search box shows everything again without pressing Enter.</summary>
    private void CgSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && sender.Text.Length == 0) _ = LoadCommunityGamesAsync(reset: true);
    }

    private void CgView_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (CommunityPage is { Visibility: Visibility.Visible }) _ = LoadCommunityGamesAsync(reset: true);
    }

    private void CgClear_Click(object sender, RoutedEventArgs e)
    {
        _cgHold = true;
        CgSearch.Text = "";
        CgRoute.SelectedIndex = CgApi.SelectedIndex = CgResult.SelectedIndex = CgGpu.SelectedIndex = CgSort.SelectedIndex = 0;
        _cgHold = false;
        _ = LoadCommunityGamesAsync(reset: true);
    }

    private void CgMore_Click(object sender, RoutedEventArgs e) => _ = LoadCommunityGamesAsync(reset: false);

    private static string ComboTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private CommunityGameQuery CgQuery() => new(
        ComboTag(CgRoute), ComboTag(CgApi), ComboTag(CgGpu), ComboTag(CgResult), CgSearch.Text ?? "", ComboTag(CgSort),
        Mine: CgView.SelectedItem == CgViewMine,
        Keys: CgView.SelectedItem == CgViewMyGames ? _all.Select(g => Community.GameKey(g.Entry)).Distinct().Take(3000).ToList() : null);

    private async Task LoadCommunityGamesAsync(bool reset)
    {
        if (reset) { _cgOffset = 0; CgGrid.Children.Clear(); }
        var query = CgQuery();
        CgClear.IsEnabled = query is not { Route: "", Api: "", Gpu: "", Result: "", Search: "" } || CgSort.SelectedIndex != 0;
        CgStatus.Text = "Loading…";
        CgMore.Visibility = Visibility.Collapsed;
        try
        {
            var (total, games) = await CommunityHub.GamesAsync(query, _cgOffset);
            foreach (var g in games) CgGrid.Children.Add(GameCard(g));
            _cgOffset += games.Count;
            CgStatus.Text = total > 0 ? $"{total} reported game{(total == 1 ? "" : "s")}"
                : query.Mine ? "You have not rated a game yet. Open a game you set up on the Games tab and tell others how it runs."
                : query.Keys is not null ? "Nobody has reported the games in your library yet. Be the first: rate them from their panel on the Games tab."
                : "No reported games match. Rate the games you set up from their panel on the Games tab!";
            CgMore.Visibility = _cgOffset < total ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { CgStatus.Text = "Community not available right now (" + ex.Message + ")."; }
    }

    /// <summary>
    /// One reported game: its artwork across the card (the library poster if the game is installed here, else Steam's
    /// header picture), the overall result, APIs, coloured works / partly / broken counts and how much was said.
    /// </summary>
    private FrameworkElement GameCard(CommunityGame g)
    {
        var (label, color) = g.Status switch
        {
            "working" => ("WORKING", "ResultWorks"),
            "broken" => ("NOT WORKING", "ResultBroken"),
            _ => ("MIXED", "ResultPartly"),
        };
        var card = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0), CornerRadius = new CornerRadius(16), Background = Res("RowBackground"), BorderBrush = Res("RowBorder"),
        };
        var root = new Grid { CornerRadius = new CornerRadius(16) };

        // artwork
        root.Children.Add(new TextBlock { Text = g.Name.Length > 0 ? g.Name[..1].ToUpperInvariant() : "?", FontSize = 120, FontWeight = FontWeights.Black, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 28, 0) });
        var local = _all.FirstOrDefault(i => i.Poster is not null && Community.GameKey(i.Entry) == g.Key)?.Poster;
        if (local is not null) root.Children.Add(new Image { Source = local, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center });
        else if (g.Key.StartsWith("steam:"))
            root.Children.Add(new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri($"https://cdn.cloudflare.steamstatic.com/steam/apps/{g.Key[6..]}/header.jpg")) { DecodePixelWidth = 460 } });
        // darken towards the bottom so the text stays readable on any picture
        root.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x66, 0x06, 0x0B, 0x12), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0xF2, 0x06, 0x0B, 0x12), Offset = 0.75 },
                },
            },
        });

        var content = new Grid { Padding = new Thickness(16, 14, 16, 14) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // top: result and "yours"
        var top = new Grid();
        top.Children.Add(Pill(label, color, dot: true));
        if (g.Mine)
        {
            var mine = Pill("YOU REPORTED", "AccentText", dot: false);
            mine.HorizontalAlignment = HorizontalAlignment.Right;
            top.Children.Add(mine);
        }
        content.Children.Add(top);

        // bottom: name, APIs, counts, totals
        var bottom = new Grid { ColumnSpacing = 12 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel { Spacing = 7 };
        info.Children.Add(new TextBlock { Text = g.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (g.Apis.Count > 0)
        {
            var apis = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var a in g.Apis.OrderBy(a => a))
                apis.Children.Add(new Border
                {
                    Padding = new Thickness(7, 1, 7, 2), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = Res("RowBorder"),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x06, 0x0B, 0x12)),
                    Child = new TextBlock { Text = Community.ApiLabel(a), FontSize = 11, FontWeight = FontWeights.SemiBold },
                });
            info.Children.Add(apis);
        }
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        foreach (var (n, key, what) in new[] { (g.Works, "ResultWorks", "works"), (g.Partly, "ResultPartly", "partly works"), (g.Broken, "ResultBroken", "does not work") })
        {
            var c = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            c.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Res(key), VerticalAlignment = VerticalAlignment.Center });
            c.Children.Add(new TextBlock { Text = n.ToString(), FontSize = 16, FontWeight = FontWeights.SemiBold });
            ToolTipService.SetToolTip(c, $"{n} player{(n == 1 ? "" : "s")}: {what}");
            counts.Children.Add(c);
        }
        info.Children.Add(counts);
        info.Children.Add(new TextBlock
        {
            Text = $"{g.Total} report{(g.Total == 1 ? "" : "s")}  ·  {g.Notes} note{(g.Notes == 1 ? "" : "s")}  ·  {Ago(g.Updated)}",
            FontSize = 12, Foreground = Res("MutedText"),
        });
        bottom.Children.Add(info);
        var open = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), BorderBrush = Res("RowBorder"),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x06, 0x0B, 0x12)), VerticalAlignment = VerticalAlignment.Bottom,
            Child = new FontIcon { Glyph = "", FontSize = 14 },
        };
        Grid.SetColumn(open, 1);
        bottom.Children.Add(open);
        Grid.SetRow(bottom, 2);
        content.Children.Add(bottom);
        root.Children.Add(content);

        card.Content = root;
        card.Click += async (_, _) => await ShowCommunityGameAsync(g);
        return card;
    }

    private static Border Pill(string text, string colorKey, bool dot)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (dot) row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Res(colorKey), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold, CharacterSpacing = 60 };
        if (!dot) label.Foreground = Res(colorKey);   // a dotted pill keeps the normal text colour
        row.Children.Add(label);
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 12, 4), CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1), BorderBrush = Res(dot ? "RowBorder" : colorKey),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0x06, 0x0B, 0x12)), Child = row,
        };
    }

    private async Task ShowCommunityGameAsync(CommunityGame g)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 560 };
        panel.Children.Add(Muted("Loading…"));
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = g.Name, CloseButtonText = "Close",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560, Padding = new Thickness(0, 0, 12, 0) },
        };
        _ = Task.Run(async () =>
        {
            try
            {
                var s = await Community.GetAsync(g.Key);
                DispatcherQueue.TryEnqueue(() => RenderCommunity(s, panel, editor: false));
            }
            catch (Exception ex) { DispatcherQueue.TryEnqueue(() => { panel.Children.Clear(); panel.Children.Add(Muted(ex.Message)); }); }
        });
        await dialog.ShowAsync();
    }

    // ------------------------------------------------------------------ chat

    private const string ChatHint = "Enter to send · Shift+Enter for a new line · be kind · links show as text";
    /// <summary>Messages shown, oldest first. Rows are drawn from this list so grouping and day dividers stay right.</summary>
    private readonly List<ChatMessage> _chatMessages = new();

    private async Task LoadChatAsync()
    {
        ChatStatus.Text = "Loading…";
        try
        {
            var messages = await CommunityHub.ChatAsync();
            _chatMessages.Clear();
            _chatMessages.AddRange(messages);
            _chatFirst = messages.Count > 0 ? messages[0].Id : 0;
            _chatLast = messages.Count > 0 ? messages[^1].Id : 0;
            RenderChat();
            ChatStatus.Text = ChatHint;
            SetChatLive(true);
            ScrollChatToEnd();
        }
        catch (Exception ex)
        {
            ChatStatus.Text = "Chat not available right now (" + ex.Message + ").";
            SetChatLive(false);
        }
        StartChatPolling();
    }

    private void SetChatLive(bool online)
    {
        ChatLive.Visibility = Visibility.Visible;
        var brush = Res(online ? "AccentText" : "MutedText");
        ChatLive.BorderBrush = ChatLiveDot.Fill = ChatLiveText.Foreground = brush;
        ChatLiveText.Text = online ? "LIVE" : "OFFLINE";
        ToolTipService.SetToolTip(ChatLive, online ? "New messages appear by themselves every few seconds." : "The community server cannot be reached; trying again.");
    }

    private void StartChatPolling()
    {
        _chatTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _chatTimer.Tick -= ChatTimer_Tick;
        _chatTimer.Tick += ChatTimer_Tick;
        _chatTimer.Start();
    }

    private void StopChatPolling() => _chatTimer?.Stop();

    private async void ChatTimer_Tick(object? sender, object e) => await PollChatAsync();

    private async Task PollChatAsync()
    {
        if (CChatPage.Visibility != Visibility.Visible) { StopChatPolling(); return; }
        try
        {
            var newer = await CommunityHub.ChatAsync(after: _chatLast);
            SetChatLive(true);
            if (newer.Count == 0) return;
            bool atEnd = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 40;
            if (_chatMessages.Count == 0) ChatList.Children.Clear();   // the "say hello" line
            foreach (var m in newer)
            {
                AddChatRow(m, _chatMessages.Count > 0 ? _chatMessages[^1] : null);
                _chatMessages.Add(m);
            }
            if (_chatFirst == 0) _chatFirst = newer[0].Id;
            _chatLast = newer[^1].Id;
            if (atEnd) ScrollChatToEnd();
        }
        catch { SetChatLive(false); /* keep polling quietly; Refresh shows the error */ }
    }

    private void ScrollChatToEnd()
    {
        ChatScroll.UpdateLayout();
        ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, true);
    }

    private async void ChatScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate || _chatLoadingOlder || _chatFirst == 0 || ChatScroll.VerticalOffset > 10) return;
        _chatLoadingOlder = true;
        try
        {
            var older = await CommunityHub.ChatAsync(before: _chatFirst);
            if (older.Count > 0)
            {
                double before = ChatScroll.ScrollableHeight;
                _chatMessages.InsertRange(0, older);
                _chatFirst = older[0].Id;
                RenderChat();
                ChatScroll.UpdateLayout();
                ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight - before, null, true);
            }
            else _chatFirst = 0;   // reached the beginning
        }
        catch { }
        finally { _chatLoadingOlder = false; }
    }

    /// <summary>Draws every message again (after loading older ones, deleting or reacting).</summary>
    private void RenderChat()
    {
        ChatList.Children.Clear();
        if (_chatMessages.Count == 0) { ChatList.Children.Add(Muted("No messages yet. Say hello!")); return; }
        ChatMessage? prev = null;
        foreach (var m in _chatMessages) { AddChatRow(m, prev); prev = m; }
    }

    /// <summary>Adds a day divider when the day changes, then the message (without name and avatar right after the same person).</summary>
    private void AddChatRow(ChatMessage m, ChatMessage? prev)
    {
        if (prev is null || prev.When.Date != m.When.Date) ChatList.Children.Add(DayDivider(m.When));
        bool continued = prev is not null && prev.When.Date == m.When.Date && prev.Nickname == m.Nickname && prev.IsDev == m.IsDev
                         && m.Reply is null && (m.When - prev.When).TotalMinutes < 5;
        ChatList.Children.Add(ChatRow(m, continued));
    }

    private FrameworkElement DayDivider(DateTime when)
    {
        var day = when.Date == DateTime.Today ? "TODAY" : when.Date == DateTime.Today.AddDays(-1) ? "YESTERDAY"
            : when.ToString(when.Year == DateTime.Today.Year ? "dddd, d MMMM" : "dddd, d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        var grid = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 14, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Rectangle { Height = 1, Fill = Res("RowBorder"), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = day, FontSize = 11, FontWeight = FontWeights.SemiBold, CharacterSpacing = 100, Foreground = Res("MutedText") };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var right = new Rectangle { Height = 1, Fill = Res("RowBorder"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>
    /// One message. Hovering it lights the row and shows a small bar: six reactions, reply, and more (copy, delete).
    /// A <paramref name="continued"/> message (same person within five minutes) leaves out the name and avatar.
    /// </summary>
    private FrameworkElement ChatRow(ChatMessage m, bool continued)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, continued ? 2 : 8, 8, 4), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // left column: avatar, or the time (shown on hover) for a continued message
        TextBlock? sideTime = null;
        if (!continued)
        {
            var avatar = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = Res(m.IsDev ? "AccentText" : "RowBorder"), VerticalAlignment = VerticalAlignment.Top };
            avatar.Child = new TextBlock { Text = m.Nickname.Length > 0 ? m.Nickname[..1].ToUpperInvariant() : "?", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Foreground = m.IsDev ? new SolidColorBrush(Microsoft.UI.Colors.Black) : Res("MutedText") };
            row.Children.Add(avatar);
        }
        else
        {
            sideTime = new TextBlock { Text = m.When.ToString("HH:mm"), FontSize = 10, Foreground = Res("MutedText"), Opacity = 0, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(sideTime);
        }

        var body = new StackPanel { Spacing = 4 };
        if (!continued)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock { Text = m.Nickname, FontWeight = FontWeights.SemiBold, Foreground = m.IsDev ? Res("AccentText") : null });
            if (m.IsDev) head.Children.Add(new Border { Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = Res("AccentText"), Child = new TextBlock { Text = "DEVELOPER", FontSize = 10, Foreground = Res("AccentText") } });
            var time = new TextBlock { Text = Ago(m.When), FontSize = 11, Foreground = Res("MutedText"), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(time, m.When.ToString("dddd d MMMM yyyy, HH:mm"));
            head.Children.Add(time);
            body.Children.Add(head);
        }
        if (m.Reply is { } q)
            body.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 760, CornerRadius = new CornerRadius(4),
                BorderBrush = Res("AccentText"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), Background = Res("RowBackground"),
                Child = new TextBlock { Text = (q.Nickname.Length > 0 ? q.Nickname + ": " : "") + q.Text, FontSize = 12, Foreground = Res("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap },
            });
        // Plain text only: shown exactly as typed, line breaks included.
        body.Children.Add(new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left });
        var reactions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var r in m.Reactions)
        {
            var b = new ToggleButton { Content = $"{r.Emoji} {r.Count}", IsChecked = r.Mine, Padding = new Thickness(8, 1, 8, 2), FontSize = 12, CornerRadius = new CornerRadius(12) };
            ToolTipService.SetToolTip(b, r.Mine ? "Remove your reaction" : "React the same");
            b.Click += async (_, _) => await ReactAsync(m, r.Emoji, b.IsChecked == true);
            reactions.Children.Add(b);
        }
        if (reactions.Children.Count > 0) body.Children.Add(reactions);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        // hover bar: floats over the top right corner of the row
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        foreach (var e in CommunityHub.Reactions)
        {
            bool already = m.Reactions.Any(r => r.Emoji == e && r.Mine);
            var react = BarButton(new TextBlock { Text = e, FontSize = 15 }, already ? "Remove your reaction" : "React");
            react.Click += async (_, _) => await ReactAsync(m, e, !already);
            bar.Children.Add(react);
        }
        var reply = BarButton(new FontIcon { Glyph = "", FontSize = 14 }, "Reply");
        reply.Click += (_, _) =>
        {
            _chatReplyTo = m.Id;
            ChatReplyText.Text = $"Replying to {m.Nickname}: {m.Text}";
            ChatReplyBar.Visibility = Visibility.Visible;
            ChatInput.Focus(FocusState.Programmatic);
        };
        bar.Children.Add(reply);
        var more = BarButton(new FontIcon { Glyph = "", FontSize = 14 }, "More");
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "Copy text", Icon = new FontIcon { Glyph = "" } };
        copy.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(m.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        };
        menu.Items.Add(copy);
        if (m.Mine)
        {
            var delete = new MenuFlyoutItem { Text = "Delete my message", Icon = new FontIcon { Glyph = "" } };
            delete.Click += async (_, _) =>
            {
                try { await CommunityHub.DeleteChatAsync(m.Id); _chatMessages.Remove(m); RenderChat(); }
                catch (Exception ex) { ChatStatus.Text = ex.Message; }
            };
            menu.Items.Add(delete);
        }
        more.Flyout = menu;
        bar.Children.Add(more);
        var barHost = new Border
        {
            Child = bar, Padding = new Thickness(4, 2, 4, 2), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = Res("RowBorder"),
            Background = Res("PanelBackground"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -16, 4, 0), Opacity = 0, IsHitTestVisible = false,
        };
        Grid.SetColumnSpan(barHost, 2);
        row.Children.Add(barHost);

        void Hover(bool on)
        {
            row.Background = on ? Res("RowBackground") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            barHost.Opacity = on ? 1 : 0;
            barHost.IsHitTestVisible = on;
            if (sideTime is not null) sideTime.Opacity = on ? 1 : 0;
        }
        row.PointerEntered += (_, _) => Hover(true);
        // Keep the bar while its "more" menu is open, so the menu does not lose its anchor.
        row.PointerExited += (_, _) => { if (!menu.IsOpen) Hover(false); };
        menu.Closed += (_, _) => Hover(false);
        return row;
    }

    private static Button BarButton(UIElement content, string tip)
    {
        var b = new Button { Content = content, Padding = new Thickness(7, 4, 7, 4), MinWidth = 34, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    private async Task ReactAsync(ChatMessage m, string emoji, bool on)
    {
        if (!await EnsureNicknameAsync()) return;
        try { await CommunityHub.ReactAsync(m.Id, emoji, on); await ReloadChatKeepingPlaceAsync(); }
        catch (Exception ex) { ChatStatus.Text = ex.Message; }
    }

    private async Task ReloadChatKeepingPlaceAsync()
    {
        double offset = ChatScroll.VerticalOffset;
        await LoadChatAsync();
        ChatScroll.UpdateLayout();
        ChatScroll.ChangeView(null, offset, null, true);
    }

    private void ChatReplyCancel_Click(object sender, RoutedEventArgs e)
    {
        _chatReplyTo = null;
        ChatReplyBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Enter sends; Shift+Enter starts a new line.</summary>
    private async void ChatInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        await SendChatAsync();
    }

    private async void ChatSend_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async Task SendChatAsync()
    {
        var text = ChatInput.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0 || !await EnsureNicknameAsync()) return;
        ChatSend.IsEnabled = false;
        try
        {
            await CommunityHub.SendChatAsync(text, _chatReplyTo);
            ChatInput.Text = "";
            ChatReplyCancel_Click(this, new RoutedEventArgs());
            await PollChatAsync();
            ScrollChatToEnd();
            ChatStatus.Text = ChatHint;
        }
        catch (Exception ex) { ChatStatus.Text = "Not sent: " + ex.Message; }
        finally { ChatSend.IsEnabled = true; }
    }

    // ------------------------------------------------------------------ gallery

    private void GalleryMore_Click(object sender, RoutedEventArgs e) => _ = LoadGalleryAsync(reset: false);

    private async Task LoadGalleryAsync(bool reset)
    {
        if (reset) { _galleryOffset = 0; GalleryGrid.Children.Clear(); }
        GalleryMore.Visibility = Visibility.Collapsed;
        try
        {
            var items = await CommunityHub.GalleryAsync(_galleryOffset);
            foreach (var g in items) GalleryGrid.Children.Add(GalleryCard(g));
            _galleryOffset += items.Count;
            GalleryMore.Visibility = items.Count >= 30 ? Visibility.Visible : Visibility.Collapsed;
            if (reset && items.Count == 0) GalleryGrid.Children.Add(new TextBlock { Text = "No pictures yet. Share how your games look with DLSS 5!", Foreground = Res("MutedText"), Margin = new Thickness(12) });
            var mine = await CommunityHub.MyGalleryAsync();
            int waiting = mine.Count(x => x.Status == "pending");
            GalleryStatus.Text = "Uploads are checked by the developer before anyone sees them." + (waiting > 0 ? $"  You have {waiting} picture{(waiting == 1 ? "" : "s")} waiting." : "");
        }
        catch (Exception ex) { GalleryStatus.Text = "Gallery not available right now (" + ex.Message + ")."; }
    }

    private FrameworkElement GalleryCard(GalleryItem g)
    {
        var card = new Button
        {
            Margin = new Thickness(6), Padding = new Thickness(0), CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Background = Res("RowBackground"), BorderBrush = Res("RowBorder"),
        };
        var stack = new StackPanel();
        stack.Children.Add(new Image { Height = 200, Stretch = Stretch.UniformToFill, Source = new BitmapImage(CommunityHub.PublicImage(g.ImageId)) { DecodePixelWidth = 720 } });
        var text = new StackPanel { Padding = new Thickness(12, 8, 12, 8), Spacing = 2 };
        if (g.Caption.Length > 0 || g.Game.Length > 0)
            text.Children.Add(new TextBlock { Text = g.Game.Length > 0 && g.Caption.Length > 0 ? $"{g.Game} — {g.Caption}" : g.Game + g.Caption, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = $"{g.Nickname}{(g.IsDev ? " · developer" : "")}  ·  {Ago(g.When)}", FontSize = 11, Foreground = Res("MutedText") });
        stack.Children.Add(text);
        card.Content = stack;
        card.Click += async (_, _) =>
        {
            var big = new Image { Stretch = Stretch.Uniform, MaxHeight = 620, Source = new BitmapImage(CommunityHub.PublicImage(g.ImageId)) };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(big);
            if (g.Caption.Length > 0) panel.Children.Add(new TextBlock { Text = g.Caption, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            await new ContentDialog { XamlRoot = Root.XamlRoot, Title = g.Game.Length > 0 ? g.Game : "Picture", Content = panel, CloseButtonText = "Close" }.ShowAsync();
        };
        return card;
    }

    private async Task<byte[]?> PickPictureAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        foreach (var t in new[] { ".jpg", ".jpeg", ".png", ".bmp" }) picker.FileTypeFilter.Add(t);
        var file = await picker.PickSingleFileAsync();
        return file is null ? null : await ImageShrink.ToJpegAsync(file);
    }

    private async void GalleryUpload_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureNicknameAsync()) return;
        GalleryUpload.IsEnabled = false;
        try
        {
            var jpeg = await PickPictureAsync();
            if (jpeg is null) return;
            GalleryStatus.Text = $"Sending ({jpeg.Length / 1024} KB)…";
            await CommunityHub.UploadAsync(jpeg, GalleryCaption.Text.Trim(), GalleryGame.Text.Trim());
            GalleryCaption.Text = GalleryGame.Text = "";
            GalleryStatus.Text = "Thanks! Your picture appears here once the developer approves it.";
        }
        catch (Exception ex) { GalleryStatus.Text = "Not sent: " + ex.Message; }
        finally { GalleryUpload.IsEnabled = true; }
    }

    // ------------------------------------------------------------------ private line

    private async Task LoadPrivateAsync()
    {
        PrivateList.Children.Clear();
        try
        {
            var msgs = await CommunityHub.PrivateAsync();
            if (msgs.Count == 0) PrivateList.Children.Add(new TextBlock { Text = "Nothing here yet. Questions, problems or ideas: the developer reads everything you send here.", Foreground = Res("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
            foreach (var m in msgs) PrivateList.Children.Add(PrivateBubble(m, mineIsUser: true, asModerator: false));
            PrivateScroll.UpdateLayout();
            PrivateScroll.ChangeView(null, PrivateScroll.ScrollableHeight, null, true);
            CTabPrivate.Text = "PRIVATE";
        }
        catch (Exception ex) { PrivateList.Children.Add(Muted("Not available right now (" + ex.Message + ").")); }
    }

    /// <summary>One private message. On the user's side "mine" are the user's messages; for the moderator, the developer's.</summary>
    private FrameworkElement PrivateBubble(PrivateMessage m, bool mineIsUser, bool asModerator)
    {
        bool mine = mineIsUser ? !m.FromDev : m.FromDev;
        var stack = new StackPanel { Spacing = 4, HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, MaxWidth = 720 };
        stack.Children.Add(new TextBlock { Text = (m.FromDev ? "Developer" : mineIsUser ? "You" : "Player") + "  ·  " + Ago(m.When), FontSize = 11, Foreground = Res("MutedText"), HorizontalAlignment = stack.HorizontalAlignment });
        var bubble = new StackPanel { Spacing = 6 };
        if (m.Text.Length > 0) bubble.Children.Add(new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (m.ImageId is { } imageId)
        {
            var img = new Image { MaxHeight = 320, Stretch = Stretch.Uniform };
            bubble.Children.Add(img);
            _ = LoadPrivateImageAsync(img, imageId, asModerator);
        }
        stack.Children.Add(new Border { Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), Background = Res(mine ? "RowBorder" : "RowBackground"), Child = bubble });
        return stack;
    }

    private async Task LoadPrivateImageAsync(Image target, long imageId, bool asModerator)
    {
        try
        {
            var bytes = await CommunityHub.ImageBytesAsync(imageId, asModerator);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            target.Source = bmp;
        }
        catch { }
    }

    private async void PrivateAttach_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _privateImage = await PickPictureAsync();
            PrivateAttach.Content = _privateImage is null ? new FontIcon { Glyph = "", FontSize = 14 } : new TextBlock { Text = "📎 1" };
        }
        catch (Exception ex) { PrivateInput.PlaceholderText = ex.Message; }
    }

    private async void PrivateSend_Click(object sender, RoutedEventArgs e)
    {
        var text = PrivateInput.Text.Trim();
        if (text.Length == 0 && _privateImage is null) return;
        PrivateSend.IsEnabled = false;
        try
        {
            await CommunityHub.SendPrivateAsync(text, _privateImage);
            PrivateInput.Text = "";
            _privateImage = null;
            PrivateAttach.Content = new FontIcon { Glyph = "", FontSize = 14 };
            await LoadPrivateAsync();
        }
        catch (Exception ex) { PrivateList.Children.Add(Muted("Not sent: " + ex.Message)); }
        finally { PrivateSend.IsEnabled = true; }
    }

    // ------------------------------------------------------------------ helpers

    private static string Ago(DateTime when)
    {
        var d = DateTime.Now - when;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} hour{((int)d.TotalHours == 1 ? "" : "s")} ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} day{((int)d.TotalDays == 1 ? "" : "s")} ago";
        return when.ToString("d MMM yyyy");
    }
}
