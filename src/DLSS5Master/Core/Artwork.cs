using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSS5Master.Core;

/// <summary>
/// Game posters for games without a local Steam library image (non-Steam launchers, manual games, uncached Steam games).
/// Steam games use their app id; others are matched by name through the public Steam store search. Images come from
/// Steam's public image servers and are cached in %LOCALAPPDATA%\DLSS5Master\artwork, so each downloads once.
/// </summary>
public static partial class Artwork
{
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Master/0.2");
        return c;
    }

    public static string CacheDir => Path.Combine(AppPaths.Root, "artwork");

    private static string Key(GameEntry g) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Library.Normalize(g.Dir)))).ToLowerInvariant()[..16];

    private static string CachedPath(GameEntry g) => Path.Combine(CacheDir, Key(g) + ".jpg");

    /// <summary>A previously downloaded poster for this game, or null.</summary>
    public static string? Cached(GameEntry g) => File.Exists(CachedPath(g)) ? CachedPath(g) : null;

    /// <summary>The executable's product name ("Onimusha: Way of the Sword"), unless it is an engine's generic name.</summary>
    public static string? ExeProductName(string exe)
    {
        try
        {
            var name = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3) return null;
            if (Regex.IsMatch(name, @"^(unreal|unity|ue4|ue5|game|launcher|bootstrap|cryengine)\b", RegexOptions.IgnoreCase)) return null;
            return name;
        }
        catch { return null; }
    }

    /// <summary>Downloads a poster for the game into the cache; returns its path, or null when none was found.</summary>
    public static async Task<string?> FetchAsync(GameEntry g, CancellationToken ct = default)
    {
        try
        {
            var appId = g.Launcher == "Steam" && !string.IsNullOrEmpty(g.Id) ? g.Id : await FindSteamAppIdAsync(g.Name, ct);
            if (!uint.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0) return null;
            var published = await FindPublishedPostersAsync(id, ct);
            var legacy = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}";
            foreach (var url in published.Concat(new[] { legacy + "/library_600x900_2x.jpg", legacy + "/library_600x900.jpg" }))
            {
                using var resp = await Http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 2048 || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[2] != 0xff) continue;
                Directory.CreateDirectory(CacheDir);
                var path = CachedPath(g);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes, ct);
                    File.Move(temporary, path, true);
                }
                finally
                {
                    try { File.Delete(temporary); } catch (IOException) { }
                }
                return path;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { AppPaths.Log($"Artwork for {g.Name}: {e.Message}"); }
        return null;
    }

    private static async Task<string[]> FindPublishedPostersAsync(uint appId, CancellationToken ct)
    {
        var request = JsonSerializer.Serialize(new
        {
            ids = new[] { new { appid = appId } },
            context = new { language = "english", country_code = "US" },
            data_request = new { include_assets = true },
        });
        var url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(request);
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            return PublishedPosterUrls(appId, doc.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException ||
                                  e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Array.Empty<string>();
        }
    }

    [GeneratedRegex(@"\A(?:[a-z0-9_-]+/)*[a-z0-9_-]+\.jpe?g\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativePosterFile();

    /// <summary>Resolve Steam's portrait assets under the CDN path for this exact app.</summary>
    internal static string[] PublishedPosterUrls(uint appId, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("store_items", out var items) || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("appid", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetUInt32(out var number) || number != appId ||
                !item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object) continue;

            var rootUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/";
            var version = assets.TryGetProperty("last_modified", out var modified) && modified.ValueKind == JsonValueKind.Number &&
                modified.TryGetUInt64(out var time) ? "?t=" + time.ToString(CultureInfo.InvariantCulture) : "";
            var result = new List<string>();
            foreach (var key in new[] { "library_capsule_2x", "library_capsule" })
                if (assets.TryGetProperty(key, out var file) && file.ValueKind == JsonValueKind.String &&
                    file.GetString() is { Length: > 0 and <= 240 } relative && RelativePosterFile().IsMatch(relative))
                    result.Add(rootUrl + relative + version);
            return result.ToArray();
        }
        return Array.Empty<string>();
    }

    [GeneratedRegex(@"\b(demo|soundtrack|ost|dlc|pack|outfit|upgrade|bundle|season pass|artbook|art book|expansion|costume|skin|playtest|bonus|wallpaper|deluxe edition upgrade)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotTheGame();

    private static string Normalize(string s) =>
        Regex.Replace(Regex.Replace(s.ToLowerInvariant().Replace("™", "").Replace("®", "").Replace("©", ""), @"[^a-z0-9]+", " "), @"\s+", " ").Trim();

    /// <summary>The Steam app id whose store name best matches the game name, ignoring DLC, demos and soundtracks.</summary>
    public static async Task<string?> FindSteamAppIdAsync(string name, CancellationToken ct = default)
    {
        var query = Normalize(name);
        if (query.Length < 2) return null;
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=US";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var candidates = items.EnumerateArray()
            .Select(i => (Id: i.TryGetProperty("id", out var id) ? id.ToString() : null,
                          Name: i.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
            .Where(c => c.Id is not null)
            .ToList();
        // 1) same name; 2) a store name that starts with ours and is not an add-on; 3) any non-add-on containing every word.
        var exact = candidates.FirstOrDefault(c => Normalize(c.Name) == query);
        if (exact.Id is not null) return exact.Id;
        var words = query.Split(' ');
        foreach (var c in candidates)
        {
            if (NotTheGame().IsMatch(c.Name)) continue;
            var n = Normalize(c.Name);
            if (n.StartsWith(query) || query.StartsWith(n) || words.All(w => n.Contains(w))) return c.Id;
        }
        return null;
    }
}
