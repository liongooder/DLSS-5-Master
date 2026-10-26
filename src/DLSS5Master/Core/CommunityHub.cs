using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DLSS5Master.Core;

public sealed record CommunityMe(string? Nickname, bool IsDev, bool Banned, int UnreadPrivate);
public sealed record CommunityGame(string Key, string Name, int Works, int Partly, int Broken, int Notes, DateTime Updated, string Status, List<string> Apis, bool Mine)
{
    public int Total => Works + Partly + Broken;
}

/// <summary>What to list on Community → Games. Empty strings mean "any".</summary>
public sealed record CommunityGameQuery(string Route, string Api, string Gpu, string Result, string Search, string Sort, bool Mine, IReadOnlyCollection<string>? Keys);
public sealed record ChatReaction(string Emoji, int Count, bool Mine);
public sealed record ChatQuote(long Id, string Nickname, string Text);
public sealed record ChatMessage(long Id, string Text, DateTime When, string Nickname, bool IsDev, bool Mine, ChatQuote? Reply, List<ChatReaction> Reactions);
public sealed record GalleryItem(long Id, long ImageId, string Caption, string Game, string Nickname, bool IsDev, string Status, DateTime When);
public sealed record PrivateMessage(long Id, bool FromDev, string Text, long? ImageId, DateTime When);

// Moderator views
public sealed record ModSummary(int PendingGallery, int UnreadPrivate, int FlaggedNotes);
public sealed record ModThread(string Author, string? Nickname, DateTime LastAt, int Unread, int Messages);
public sealed record ModGallery(long Id, string Author, long ImageId, string Caption, string Game, string Status, string? Nickname, DateTime When);
public sealed record ModNote(long Id, string Author, string Game, string Rating, string Note, bool Hidden, int Flags, DateTime When);
public sealed record ModChat(long Id, string Author, string Text, bool Hidden, string? Nickname, DateTime When);

/// <summary>
/// Community features beyond per-game reports: nickname, the list of reported games, chat, the moderated gallery and
/// the private line to the developer, plus the developer's moderator calls (enabled only where a moderator key is saved).
/// </summary>
public static class CommunityHub
{
    public const int ChatMax = 500, PrivateMax = 2000, CaptionMax = 140;
    public static readonly string[] Reactions = { "👍", "❤️", "😂", "😮", "😢", "🔥" };

    private static readonly HttpClient Http = new() { BaseAddress = new Uri(Community.Server), Timeout = TimeSpan.FromSeconds(60) };

    private static HttpRequestMessage Req(HttpMethod m, string path, object? body = null, bool withInstall = true, bool admin = false)
    {
        var r = new HttpRequestMessage(m, path);
        r.Headers.UserAgent.ParseAdd($"DLSS5Master/{Updates.CurrentAppVersion}");
        if (withInstall) r.Headers.Add("X-Install", Community.InstallId);
        if (admin) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ModeratorKey ?? "");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private static async Task<JsonDocument> SendAsync(HttpRequestMessage req, CancellationToken ct = default)
    {
        using (req)
        using (var resp = await Http.SendAsync(req, ct))
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string? message = null;
                try { using var d = JsonDocument.Parse(text); message = d.RootElement.GetProperty("error").GetString(); } catch { }
                throw new InstallException(message ?? $"The community server answered {(int)resp.StatusCode}.");
            }
            return JsonDocument.Parse(text);
        }
    }

    private static Dictionary<string, object?> Body(params (string Key, object? Value)[] fields)
    {
        var d = new Dictionary<string, object?> { ["install"] = Community.InstallId };
        foreach (var (k, v) in fields) d[k] = v;
        return d;
    }

    // ------------------------------------------------------------------ me

    public static async Task<CommunityMe> MeAsync()
    {
        using var d = await SendAsync(Req(HttpMethod.Get, "/v1/me"));
        var r = d.RootElement;
        return new CommunityMe(r.GetProperty("nickname").GetString(), r.GetProperty("isDev").GetBoolean(), r.GetProperty("banned").GetBoolean(), r.GetProperty("unreadPrivate").GetInt32());
    }

    public static async Task SetNicknameAsync(string nickname) =>
        (await SendAsync(Req(HttpMethod.Put, "/v1/me", Body(("nickname", nickname.Trim())), withInstall: false))).Dispose();

    // ------------------------------------------------------------------ games

    public static async Task<(int Total, List<CommunityGame> Games)> GamesAsync(CommunityGameQuery f, int offset)
    {
        static string P(string name, string value) => string.IsNullOrWhiteSpace(value) ? "" : $"&{name}={Uri.EscapeDataString(value.Trim())}";
        var q = $"/v1/games?sort={(f.Sort.Length > 0 ? f.Sort : "recent")}&offset={offset}"
                + P("route", f.Route) + P("api", f.Api) + P("gpu", f.Gpu) + P("result", f.Result) + P("q", f.Search) + (f.Mine ? "&mine=1" : "");
        // "My games": the library's keys go in the body, too many for the address.
        using var d = await SendAsync(f.Keys is null ? Req(HttpMethod.Get, q) : Req(HttpMethod.Post, q, new Dictionary<string, object> { ["keys"] = f.Keys }));
        var games = d.RootElement.GetProperty("games").EnumerateArray().Select(g => new CommunityGame(
            Str(g, "key"), Str(g, "name"), Int(g, "works"), Int(g, "partly"), Int(g, "broken"), Int(g, "notes"), Time(g, "updated"), Str(g, "status"),
            g.TryGetProperty("apis", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new(),
            Bool(g, "mine"))).ToList();
        return (Int(d.RootElement, "total"), games);
    }

    // ------------------------------------------------------------------ chat

    public static async Task<List<ChatMessage>> ChatAsync(long after = 0, long before = 0)
    {
        var q = after > 0 ? $"/v1/chat?after={after}" : before > 0 ? $"/v1/chat?before={before}" : "/v1/chat";
        using var d = await SendAsync(Req(HttpMethod.Get, q));
        return d.RootElement.EnumerateArray().Select(m => new ChatMessage(
            Long(m, "id"), Str(m, "text"), Time(m, "created_at"), Str(m, "nickname"), Bool(m, "is_dev"), Bool(m, "mine"),
            m.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.Object ? new ChatQuote(Long(r, "id"), Str(r, "nickname"), Str(r, "text")) : null,
            m.GetProperty("reactions").EnumerateArray().Select(x => new ChatReaction(Str(x, "emoji"), Int(x, "n"), Bool(x, "mine"))).ToList())).ToList();
    }

    public static async Task SendChatAsync(string text, long? replyTo) =>
        (await SendAsync(Req(HttpMethod.Post, "/v1/chat", Body(("text", text), ("replyTo", replyTo)), withInstall: false))).Dispose();

    public static async Task ReactAsync(long id, string emoji, bool on) =>
        (await SendAsync(Req(HttpMethod.Post, $"/v1/chat/{id}/react", Body(("emoji", emoji), ("on", on)), withInstall: false))).Dispose();

    public static async Task DeleteChatAsync(long id) =>
        (await SendAsync(Req(HttpMethod.Delete, $"/v1/chat/{id}", Body(), withInstall: false))).Dispose();

    // ------------------------------------------------------------------ gallery

    public static async Task<List<GalleryItem>> GalleryAsync(int offset = 0)
    {
        using var d = await SendAsync(Req(HttpMethod.Get, $"/v1/gallery?offset={offset}", withInstall: false));
        return d.RootElement.EnumerateArray().Select(g => new GalleryItem(Long(g, "id"), Long(g, "image_id"), Str(g, "caption"), Str(g, "game_name"),
            Str(g, "nickname") is { Length: > 0 } n ? n : "Player", Bool(g, "is_dev"), "approved", Time(g, "created_at"))).ToList();
    }

    public static async Task<List<GalleryItem>> MyGalleryAsync()
    {
        using var d = await SendAsync(Req(HttpMethod.Get, "/v1/gallery/mine"));
        return d.RootElement.EnumerateArray().Select(g => new GalleryItem(Long(g, "id"), Long(g, "image_id"), Str(g, "caption"), Str(g, "game_name"),
            "", false, Str(g, "status"), Time(g, "created_at"))).ToList();
    }

    public static async Task UploadAsync(byte[] jpeg, string caption, string game) =>
        (await SendAsync(Req(HttpMethod.Post, "/v1/gallery", Body(("caption", caption), ("game", game), ("image", Convert.ToBase64String(jpeg))), withInstall: false))).Dispose();

    /// <summary>A picture's address for public (approved gallery) pictures.</summary>
    public static Uri PublicImage(long imageId) => new($"{Community.Server}/v1/images/{imageId}");

    /// <summary>Downloads a picture this install may see (its own, or anything for the moderator).</summary>
    public static async Task<byte[]> ImageBytesAsync(long imageId, bool asModerator = false)
    {
        using var req = Req(HttpMethod.Get, asModerator ? $"/v1/admin/images/{imageId}" : $"/v1/images/{imageId}", admin: asModerator);
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync();
    }

    // ------------------------------------------------------------------ private line

    public static async Task<List<PrivateMessage>> PrivateAsync()
    {
        using var d = await SendAsync(Req(HttpMethod.Get, "/v1/private"));
        return d.RootElement.EnumerateArray().Select(ParsePrivate).ToList();
    }

    public static async Task SendPrivateAsync(string text, byte[]? jpeg) =>
        (await SendAsync(Req(HttpMethod.Post, "/v1/private",
            Body(("text", text), ("image", jpeg is null ? null : Convert.ToBase64String(jpeg))), withInstall: false))).Dispose();

    private static PrivateMessage ParsePrivate(JsonElement m) => new(Long(m, "id"), Bool(m, "from_dev"), Str(m, "text"),
        m.TryGetProperty("image_id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt64() : null, Time(m, "created_at"));

    // ------------------------------------------------------------------ moderator

    private static string KeyFile => Path.Combine(AppPaths.Root, "moderator.key");
    private static string? _moderatorKey;
    private static bool _keyLoaded;

    /// <summary>The moderator key saved on this PC (encrypted for this Windows user), or null.</summary>
    public static string? ModeratorKey
    {
        get
        {
            if (_keyLoaded) return _moderatorKey;
            _keyLoaded = true;
            try
            {
                if (File.Exists(KeyFile))
                    _moderatorKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception e) { AppPaths.Log("Moderator key could not be read: " + e.Message); }
            return _moderatorKey;
        }
    }

    public static bool IsModerator => !string.IsNullOrEmpty(ModeratorKey);

    /// <summary>Checks the key against the server, then keeps it (encrypted) on this PC. Empty removes it.</summary>
    public static async Task SaveModeratorKeyAsync(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            if (File.Exists(KeyFile)) File.Delete(KeyFile);
            _moderatorKey = null; _keyLoaded = true;
            return;
        }
        var old = _moderatorKey; _moderatorKey = key; _keyLoaded = true;
        try { await ModSummaryAsync(); }
        catch { _moderatorKey = old; throw new InstallException("That moderator key was not accepted by the community server."); }
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllBytes(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    }

    private static Task<JsonDocument> Admin(HttpMethod m, string path, object? body = null) => SendAsync(Req(m, "/v1/admin/" + path, body, withInstall: false, admin: true));

    public static async Task<ModSummary> ModSummaryAsync()
    {
        using var d = await Admin(HttpMethod.Get, "summary");
        var r = d.RootElement;
        return new ModSummary(Int(r, "pendingGallery"), Int(r, "unreadPrivate"), Int(r, "flaggedNotes"));
    }

    public static async Task<List<ModThread>> ModThreadsAsync()
    {
        using var d = await Admin(HttpMethod.Get, "private");
        return d.RootElement.EnumerateArray().Select(t => new ModThread(Str(t, "author"), NullStr(t, "nickname"), Time(t, "last_at"), Int(t, "unread"), Int(t, "messages"))).ToList();
    }

    public static async Task<List<PrivateMessage>> ModThreadAsync(string author)
    {
        using var d = await Admin(HttpMethod.Get, "private/" + author);
        return d.RootElement.EnumerateArray().Select(ParsePrivate).ToList();
    }

    public static async Task ModReplyAsync(string author, string text) =>
        (await Admin(HttpMethod.Post, "private/" + author, new Dictionary<string, string> { ["text"] = text })).Dispose();

    public static async Task<List<ModGallery>> ModGalleryAsync(string status = "pending")
    {
        using var d = await Admin(HttpMethod.Get, "gallery?status=" + status);
        return d.RootElement.EnumerateArray().Select(g => new ModGallery(Long(g, "id"), Str(g, "author"), Long(g, "image_id"), Str(g, "caption"),
            Str(g, "game_name"), Str(g, "status"), NullStr(g, "nickname"), Time(g, "created_at"))).ToList();
    }

    public static async Task ModGalleryDecideAsync(long id, bool approve) => (await Admin(HttpMethod.Post, $"gallery/{id}/{(approve ? "approve" : "reject")}")).Dispose();
    public static async Task ModGalleryDeleteAsync(long id) => (await Admin(HttpMethod.Delete, $"gallery/{id}")).Dispose();

    public static async Task<List<ModNote>> ModFlaggedAsync()
    {
        using var d = await Admin(HttpMethod.Get, "flagged");
        return d.RootElement.EnumerateArray().Select(n => new ModNote(Long(n, "id"), Str(n, "author"), Str(n, "game_name"), Str(n, "rating"), Str(n, "note"),
            Int(n, "hidden") != 0, Int(n, "flags"), Time(n, "updated_at"))).ToList();
    }

    public static async Task ModNoteAsync(long id, string action) =>   // hide | unhide | delete
        (await (action == "delete" ? Admin(HttpMethod.Delete, $"reports/{id}") : Admin(HttpMethod.Post, $"reports/{id}/{action}"))).Dispose();

    public static async Task<List<ModChat>> ModChatAsync()
    {
        using var d = await Admin(HttpMethod.Get, "chat");
        return d.RootElement.EnumerateArray().Select(c => new ModChat(Long(c, "id"), Str(c, "author"), Str(c, "text"), Int(c, "hidden") != 0, NullStr(c, "nickname"), Time(c, "created_at"))).ToList();
    }

    public static async Task ModChatHideAsync(long id, bool hide) => (await Admin(HttpMethod.Post, $"chat/{id}/{(hide ? "hide" : "unhide")}")).Dispose();
    public static async Task ModBanAsync(string author, bool ban) => (await Admin(HttpMethod.Post, $"{(ban ? "ban" : "unban")}/{author}")).Dispose();

    // ------------------------------------------------------------------ json helpers

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string? NullStr(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
    private static long Long(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetDouble() != 0));
    private static DateTime Time(JsonElement e, string n) => DateTimeOffset.FromUnixTimeMilliseconds(Long(e, n)).LocalDateTime;
}
