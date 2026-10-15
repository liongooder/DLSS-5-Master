using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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

    private void CgFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CommunityPage is { Visibility: Visibility.Visible }) _ = LoadCommunityGamesAsync(reset: true);
    }

    private void CgSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => _ = LoadCommunityGamesAsync(reset: true);
    private void CgMore_Click(object sender, RoutedEventArgs e) => _ = LoadCommunityGamesAsync(reset: false);

    private static string ComboTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private async Task LoadCommunityGamesAsync(bool reset)
    {
        if (reset) { _cgOffset = 0; CgGrid.Children.Clear(); }
        CgStatus.Text = "Loading…";
        CgMore.Visibility = Visibility.Collapsed;
        try
        {
            var (total, games) = await CommunityHub.GamesAsync(ComboTag(CgRoute), ComboTag(CgResult), CgSearch.Text, ComboTag(CgSort) is { Length: > 0 } s ? s : "recent", _cgOffset);
            foreach (var g in games) CgGrid.Children.Add(GameCard(g));
            _cgOffset += games.Count;
            CgStatus.Text = total == 0 ? "No reported games match. Rate the games you set up from their panel on the Games tab!" : $"{total} reported game{(total == 1 ? "" : "s")}";
            CgMore.Visibility = _cgOffset < total ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { CgStatus.Text = "Community not available right now (" + ex.Message + ")."; }
    }

    private FrameworkElement GameCard(CommunityGame g)
    {
        var (label, color) = g.Status switch
        {
            "working" => ("WORKING", "AccentText"),
            "broken" => ("NOT WORKING", "WarnText"),
            _ => ("MIXED", "WarnText"),
        };
        var card = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12), CornerRadius = new CornerRadius(14),
            Background = Res("RowBackground"), BorderBrush = Res("RowBorder"),
        };
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var art = new Grid { Width = 64, Height = 96, CornerRadius = new CornerRadius(8), Background = Res("RowBorder") };
        art.Children.Add(new TextBlock { Text = g.Name.Length > 0 ? g.Name[..1].ToUpperInvariant() : "?", FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("MutedText") });
        if (g.Key.StartsWith("steam:"))
            art.Children.Add(new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri($"https://cdn.cloudflare.steamstatic.com/steam/apps/{g.Key[6..]}/library_600x900.jpg")) { DecodePixelWidth = 128 } });
        grid.Children.Add(art);

        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BorderBrush = Res(color), Child = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res(color) },
        });
        info.Children.Add(new TextBlock { Text = g.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = $"✅ {g.Works}   🟡 {g.Partly}   ❌ {g.Broken}" });
        info.Children.Add(new TextBlock { Text = $"{g.Total} report{(g.Total == 1 ? "" : "s")} · {g.Notes} note{(g.Notes == 1 ? "" : "s")} · {Ago(g.Updated)}", FontSize = 11, Foreground = Res("MutedText") });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        card.Content = grid;
        card.Click += async (_, _) => await ShowCommunityGameAsync(g);
        return card;
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

    private async Task LoadChatAsync()
    {
        ChatList.Children.Clear();
        _chatFirst = _chatLast = 0;
        ChatStatus.Text = "Loading…";
        try
        {
            var messages = await CommunityHub.ChatAsync();
            if (messages.Count == 0) ChatList.Children.Add(Muted("No messages yet. Say hello!"));
            foreach (var m in messages) ChatList.Children.Add(ChatRow(m));
            if (messages.Count > 0) { _chatFirst = messages[0].Id; _chatLast = messages[^1].Id; }
            ChatStatus.Text = "Enter to send · be kind · links show as text";
            ScrollChatToEnd();
        }
        catch (Exception ex) { ChatStatus.Text = "Chat not available right now (" + ex.Message + ")."; }
        StartChatPolling();
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
            if (newer.Count == 0) return;
            bool atEnd = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 40;
            if (_chatLast == 0) ChatList.Children.Clear();
            foreach (var m in newer) ChatList.Children.Add(ChatRow(m));
            if (_chatFirst == 0) _chatFirst = newer[0].Id;
            _chatLast = newer[^1].Id;
            if (atEnd) ScrollChatToEnd();
        }
        catch { /* keep polling quietly; Refresh shows errors */ }
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
                for (int i = older.Count - 1; i >= 0; i--) ChatList.Children.Insert(0, ChatRow(older[i]));
                _chatFirst = older[0].Id;
                ChatScroll.UpdateLayout();
                ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight - before, null, true);
            }
            else _chatFirst = 0;   // reached the beginning
        }
        catch { }
        finally { _chatLoadingOlder = false; }
    }

    private FrameworkElement ChatRow(ChatMessage m)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Res(m.IsDev ? "AccentText" : "RowBorder"), VerticalAlignment = VerticalAlignment.Top };
        avatar.Child = new TextBlock { Text = m.Nickname.Length > 0 ? m.Nickname[..1].ToUpperInvariant() : "?", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Foreground = m.IsDev ? new SolidColorBrush(Microsoft.UI.Colors.Black) : Res("MutedText") };
        grid.Children.Add(avatar);

        var body = new StackPanel { Spacing = 4 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = m.Nickname, FontWeight = FontWeights.SemiBold, Foreground = m.IsDev ? Res("AccentText") : null });
        if (m.IsDev) head.Children.Add(new Border { Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = Res("AccentText"), Child = new TextBlock { Text = "DEVELOPER", FontSize = 10, Foreground = Res("AccentText") } });
        head.Children.Add(new TextBlock { Text = Ago(m.When), FontSize = 11, Foreground = Res("MutedText"), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(head);
        if (m.Reply is { } q)
            body.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 760,
                BorderBrush = Res("AccentText"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), Background = Res("RowBackground"),
                Child = new TextBlock { Text = (q.Nickname.Length > 0 ? q.Nickname + ": " : "") + q.Text, FontSize = 12, Foreground = Res("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap },
            });
        // Plain text only: shown exactly as typed.
        body.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), Background = Res("RowBackground"),
            Child = new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 760 },
        });
        var reactions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var r in m.Reactions)
        {
            var b = new ToggleButton { Content = $"{r.Emoji} {r.Count}", IsChecked = r.Mine, Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
            b.Click += async (_, _) => await ReactAsync(m, r.Emoji, b.IsChecked == true);
            reactions.Children.Add(b);
        }
        if (reactions.Children.Count > 0) body.Children.Add(reactions);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        var menu = new MenuFlyout();
        var react = new MenuFlyoutSubItem { Text = "React" };
        foreach (var e in CommunityHub.Reactions)
        {
            var item = new MenuFlyoutItem { Text = e };
            item.Click += async (_, _) => await ReactAsync(m, e, true);
            react.Items.Add(item);
        }
        menu.Items.Add(react);
        var reply = new MenuFlyoutItem { Text = "Reply" };
        reply.Click += (_, _) =>
        {
            _chatReplyTo = m.Id;
            ChatReplyText.Text = $"Replying to {m.Nickname}: {m.Text}";
            ChatReplyBar.Visibility = Visibility.Visible;
            ChatInput.Focus(FocusState.Programmatic);
        };
        menu.Items.Add(reply);
        var copy = new MenuFlyoutItem { Text = "Copy text" };
        copy.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(m.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        };
        menu.Items.Add(copy);
        if (m.Mine)
        {
            var delete = new MenuFlyoutItem { Text = "Delete my message" };
            delete.Click += async (_, _) =>
            {
                try { await CommunityHub.DeleteChatAsync(m.Id); ChatList.Children.Remove(grid); }
                catch (Exception ex) { ChatStatus.Text = ex.Message; }
            };
            menu.Items.Add(delete);
        }
        more.Flyout = menu;
        ToolTipService.SetToolTip(more, "React, reply or copy");
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);
        return grid;
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

    private async void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await SendChatAsync();
    }

    private async void ChatSend_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async Task SendChatAsync()
    {
        var text = ChatInput.Text.Trim();
        if (text.Length == 0 || !await EnsureNicknameAsync()) return;
        ChatSend.IsEnabled = false;
        try
        {
            await CommunityHub.SendChatAsync(text, _chatReplyTo);
            ChatInput.Text = "";
            ChatReplyCancel_Click(this, new RoutedEventArgs());
            await PollChatAsync();
            ScrollChatToEnd();
            ChatStatus.Text = "Enter to send · be kind · links show as text";
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
