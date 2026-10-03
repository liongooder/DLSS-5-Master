using System.Diagnostics;
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

    public static void ClearCached(GameEntry g)
    {
        try { File.Delete(CachedPath(g)); } catch { }
    }

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
            if (appId is null) return null;
            foreach (var url in new[]
                     {
                         $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg",
                         $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
                     })
            {
                using var resp = await Http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 2048) continue;
                Directory.CreateDirectory(CacheDir);
                var path = CachedPath(g);
                await File.WriteAllBytesAsync(path + ".tmp", bytes, ct);
                File.Move(path + ".tmp", path, true);
                return path;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { AppPaths.Log($"Artwork for {g.Name}: {e.Message}"); }
        return null;
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
