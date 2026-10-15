using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DLSS5Master.Core;

namespace DLSS5Master;

/// <summary>Community → MODERATE: only on the developer's PC, where the moderator key is saved (Settings → Community).</summary>
public sealed partial class MainWindow
{
    private void ModTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => _ = LoadModerateAsync();

    private async Task LoadModerateAsync()
    {
        ModList.Children.Clear();
        ModList.Children.Add(Muted("Loading…"));
        try
        {
            var tab = ModTabs.SelectedItem;
            if (tab == MTabGallery) await ModGalleryAsync();
            else if (tab == MTabPrivate) await ModThreadsAsync();
            else if (tab == MTabNotes) await ModNotesAsync();
            else await ModChatAsync();
            await RefreshMeAsync();
        }
        catch (Exception ex) { ModList.Children.Clear(); ModList.Children.Add(Muted("Could not load: " + ex.Message)); }
    }

    // ------------------------------------------------------------------ pictures

    private async Task ModGalleryAsync()
    {
        var pending = await CommunityHub.ModGalleryAsync("pending");
        var approved = await CommunityHub.ModGalleryAsync("approved");
        ModList.Children.Clear();
        ModList.Children.Add(Header($"WAITING FOR YOU ({pending.Count})"));
        if (pending.Count == 0) ModList.Children.Add(Muted("No pictures waiting."));
        foreach (var g in pending) ModList.Children.Add(ModGalleryCard(g, pending: true));
        ModList.Children.Add(Header($"APPROVED ({approved.Count}, newest 100)"));
        foreach (var g in approved) ModList.Children.Add(ModGalleryCard(g, pending: false));
    }

    private FrameworkElement ModGalleryCard(ModGallery g, bool pending)
    {
        var card = Card();
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var img = new Image { MaxHeight = 200, Stretch = Stretch.Uniform };
        _ = LoadPrivateImageAsync(img, g.ImageId, asModerator: true);
        grid.Children.Add(img);
        var info = new StackPanel { Spacing = 6 };
        info.Children.Add(new TextBlock { Text = g.Game.Length > 0 ? g.Game : "(no game given)", FontWeight = FontWeights.SemiBold });
        if (g.Caption.Length > 0) info.Children.Add(new TextBlock { Text = g.Caption, TextWrapping = TextWrapping.Wrap });
        info.Children.Add(Muted($"{g.Nickname ?? "Player"} · {Ago(g.When)}"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (pending)
        {
            buttons.Children.Add(ModButton("Approve", true, async () => { await CommunityHub.ModGalleryDecideAsync(g.Id, true); }));
            buttons.Children.Add(ModButton("Reject", false, async () => { await CommunityHub.ModGalleryDecideAsync(g.Id, false); }));
        }
        buttons.Children.Add(ModButton("Delete", false, async () => { await CommunityHub.ModGalleryDeleteAsync(g.Id); }));
        buttons.Children.Add(BanButton(g.Author, g.Nickname));
        info.Children.Add(buttons);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        card.Child = grid;
        return card;
    }

    // ------------------------------------------------------------------ private messages

    private async Task ModThreadsAsync()
    {
        var threads = await CommunityHub.ModThreadsAsync();
        ModList.Children.Clear();
        if (threads.Count == 0) { ModList.Children.Add(Muted("No private messages yet.")); return; }
        foreach (var t in threads)
        {
            var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 10, 14, 10) };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{t.Nickname ?? "Player (no nickname)"}  ·  {t.Messages} message{(t.Messages == 1 ? "" : "s")}  ·  {Ago(t.LastAt)}", FontWeight = t.Unread > 0 ? FontWeights.SemiBold : FontWeights.Normal });
            if (t.Unread > 0)
            {
                var badge = new TextBlock { Text = $"{t.Unread} new", Foreground = Res("AccentText") };
                Grid.SetColumn(badge, 1);
                row.Children.Add(badge);
            }
            b.Content = row;
            b.Click += async (_, _) => await ModOpenThreadAsync(t);
            ModList.Children.Add(b);
        }
    }

    private async Task ModOpenThreadAsync(ModThread t)
    {
        var msgs = await CommunityHub.ModThreadAsync(t.Author);
        ModList.Children.Clear();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var back = new Button { Content = "← All messages" };
        back.Click += async (_, _) => await LoadModerateAsync();
        top.Children.Add(back);
        top.Children.Add(new TextBlock { Text = t.Nickname ?? "Player", FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(BanButton(t.Author, t.Nickname));
        ModList.Children.Add(top);
        foreach (var m in msgs) ModList.Children.Add(PrivateBubble(m, mineIsUser: false, asModerator: true));
        var reply = new TextBox { PlaceholderText = "Reply privately", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxLength = CommunityHub.PrivateMax };
        var send = new Button { Content = "Send reply", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var status = Muted("");
        send.Click += async (_, _) =>
        {
            if (reply.Text.Trim().Length == 0) return;
            send.IsEnabled = false;
            try { await CommunityHub.ModReplyAsync(t.Author, reply.Text.Trim()); await ModOpenThreadAsync(t); }
            catch (Exception ex) { status.Text = ex.Message; send.IsEnabled = true; }
        };
        ModList.Children.Add(reply);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(send);
        row.Children.Add(status);
        ModList.Children.Add(row);
    }

    // ------------------------------------------------------------------ notes

    private async Task ModNotesAsync()
    {
        var notes = await CommunityHub.ModFlaggedAsync();
        ModList.Children.Clear();
        if (notes.Count == 0) { ModList.Children.Add(Muted("No flagged notes.")); return; }
        foreach (var n in notes)
        {
            var card = Card();
            var info = new StackPanel { Spacing = 6 };
            info.Children.Add(new TextBlock { Text = $"{n.Game} · {n.Rating} · flagged {n.Flags}×{(n.Hidden ? " · HIDDEN" : "")}", FontWeight = FontWeights.SemiBold });
            info.Children.Add(new TextBlock { Text = n.Note, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            info.Children.Add(Muted(Ago(n.When)));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(n.Hidden
                ? ModButton("Show again (clear flags)", false, async () => await CommunityHub.ModNoteAsync(n.Id, "unhide"))
                : ModButton("Hide", false, async () => await CommunityHub.ModNoteAsync(n.Id, "hide")));
            buttons.Children.Add(ModButton("Delete report", false, async () => await CommunityHub.ModNoteAsync(n.Id, "delete")));
            buttons.Children.Add(BanButton(n.Author, null));
            info.Children.Add(buttons);
            card.Child = info;
            ModList.Children.Add(card);
        }
    }

    // ------------------------------------------------------------------ chat

    private async Task ModChatAsync()
    {
        var chat = await CommunityHub.ModChatAsync();
        ModList.Children.Clear();
        if (chat.Count == 0) { ModList.Children.Add(Muted("No chat messages yet.")); return; }
        foreach (var c in chat)
        {
            var card = Card();
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { Spacing = 4, Opacity = c.Hidden ? 0.5 : 1 };
            info.Children.Add(Muted($"{c.Nickname ?? "Player"} · {Ago(c.When)}{(c.Hidden ? " · hidden" : "")}"));
            info.Children.Add(new TextBlock { Text = c.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            grid.Children.Add(info);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
            buttons.Children.Add(ModButton(c.Hidden ? "Show" : "Hide", false, async () => await CommunityHub.ModChatHideAsync(c.Id, !c.Hidden)));
            buttons.Children.Add(BanButton(c.Author, c.Nickname));
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            card.Child = grid;
            ModList.Children.Add(card);
        }
    }

    // ------------------------------------------------------------------ helpers

    private Border Card() => new() { Padding = new Thickness(14), CornerRadius = new CornerRadius(12), Background = Res("RowBackground"), BorderBrush = Res("RowBorder"), BorderThickness = new Thickness(1) };

    private static TextBlock Header(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["SectionTitle"], Margin = new Thickness(0, 8, 0, 0) };

    /// <summary>A moderator action button: runs the action, then reloads the current list.</summary>
    private Button ModButton(string label, bool accent, Func<Task> action)
    {
        var b = new Button { Content = label };
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); await LoadModerateAsync(); }
            catch (Exception ex) { b.Content = "Failed"; ToolTipService.SetToolTip(b, ex.Message); }
        };
        return b;
    }

    private Button BanButton(string author, string? nickname)
    {
        var b = new Button { Content = "Ban", Foreground = Res("WarnText") };
        ToolTipService.SetToolTip(b, "Stop this player posting anywhere in the community and hide everything they posted.");
        b.Click += async (_, _) =>
        {
            var confirm = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = $"Ban {nickname ?? "this player"}?",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "They can no longer chat, upload, report or write privately, and their chat messages, notes and waiting pictures are hidden. This can be undone on the server." },
                PrimaryButtonText = "Ban", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            try { await CommunityHub.ModBanAsync(author, true); await LoadModerateAsync(); }
            catch (Exception ex) { b.Content = "Failed"; ToolTipService.SetToolTip(b, ex.Message); }
        };
        return b;
    }

    // ------------------------------------------------------------------ Settings → Community → Moderator

    private async void ModKeySave_Click(object sender, RoutedEventArgs e)
    {
        ModKeyStatus.Text = "Checking…";
        try
        {
            await CommunityHub.SaveModeratorKeyAsync(ModKeyBox.Password);
            ModKeyBox.Password = "";
            ModKeyStatus.Text = "Saved. Community now has a MODERATE page on this PC.";
            ModKeyStatus.Foreground = Res("AccentText");
        }
        catch (Exception ex) { ModKeyStatus.Text = ex.Message; ModKeyStatus.Foreground = Res("WarnText"); }
        CTabModerate.Visibility = CommunityHub.IsModerator ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ModKeyRemove_Click(object sender, RoutedEventArgs e)
    {
        await CommunityHub.SaveModeratorKeyAsync(null);
        ModKeyStatus.Text = "Removed from this PC.";
        ModKeyStatus.Foreground = Res("MutedText");
        CTabModerate.Visibility = Visibility.Collapsed;
    }
}
