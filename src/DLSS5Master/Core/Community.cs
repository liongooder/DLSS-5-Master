using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DLSS5Master.Core;

public sealed record CommunityCounts(int Works, int Partly, int Broken)
{
    public int Total => Works + Partly + Broken;
}

public sealed record CommunityCombo(string Route, bool Mfg, string GpuFamily, int Works, int Partly, int Broken);

public sealed record CommunityNote(long Id, string Rating, string Route, bool Mfg, string Gpu, string GpuFamily, string AppVersion, string Note, DateTime When);

public sealed record CommunityMine(long Id, string Rating, string Route, bool Mfg, string Note, bool Hidden);

public sealed record CommunitySummary(string Key, CommunityCounts Counts, List<CommunityCombo> Combos, List<CommunityNote> Notes, CommunityMine? Mine);

/// <summary>
/// Per-game community reports ("works / partly / broken" + an optional short note) shared through the DLSS 5 Master
/// community server. No accounts: each install has a random ID, which the server stores only as a salted hash.
/// Reading is anonymous; nothing is sent unless the user presses Send or Flag.
/// </summary>
public static partial class Community
{
    public const string Server = "https://dlss5master-community.dlss5master-community.workers.dev";
    public const int NoteMax = 280;

    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { BaseAddress = new Uri(Server), Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"DLSS5Master/{Updates.CurrentAppVersion}");
        return c;
    }

    // ------------------------------------------------------------------ identity

    private static string? _installId;

    /// <summary>A random 128-bit ID made on first use and kept in %LOCALAPPDATA%\DLSS5Master. It identifies nothing but this install.</summary>
    public static string InstallId
    {
        get
        {
            if (_installId is not null) return _installId;
            var file = Path.Combine(AppPaths.Root, "install-id.txt");
            try
            {
                var existing = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
                if (InstallIdPattern().IsMatch(existing)) return _installId = existing.ToLowerInvariant();
            }
            catch { }
            _installId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            try { Directory.CreateDirectory(AppPaths.Root); File.WriteAllText(file, _installId); } catch { }
            return _installId;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{32}$")]
    private static partial Regex InstallIdPattern();

    /// <summary>"steam:&lt;appid&gt;" for Steam games, otherwise "name:" + the name in lowercase letters and digits.</summary>
    public static string GameKey(GameEntry game)
    {
        if (game.Launcher.Equals("Steam", StringComparison.OrdinalIgnoreCase) && game.Id is { } id && id.Length <= 10 && id.All(char.IsAsciiDigit))
            return "steam:" + id;
        var name = new string(game.Name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return "name:" + (name.Length == 0 ? "unknown" : name.Length > 60 ? name[..60] : name);
    }

    /// <summary>The GPU family reports are grouped by, e.g. "RTX 40".</summary>
    public static string GpuFamily(string? gpu)
    {
        if (string.IsNullOrWhiteSpace(gpu)) return "Other";
        var rtx = Regex.Match(gpu, @"RTX\s*(?:PRO\s*)?(\d)0\d\d", RegexOptions.IgnoreCase);
        if (rtx.Success) return $"RTX {rtx.Groups[1].Value}0";
        if (gpu.Contains("GTX", StringComparison.OrdinalIgnoreCase)) return "GTX";
        if (gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (gpu.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || gpu.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (gpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) || gpu.Contains("Arc", StringComparison.Ordinal)) return "Intel";
        return "Other";
    }

    public static string? LocalGpu => Gpu.Nvidia ?? Gpu.Names.FirstOrDefault();

    // ------------------------------------------------------------------ calls

    public static async Task<CommunitySummary> GetAsync(string key, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/games/" + Uri.EscapeDataString(key));
        req.Headers.Add("X-Install", InstallId);
        using var resp = await Http.SendAsync(req, ct);
        await EnsureOk(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        var c = r.GetProperty("counts");
        var counts = new CommunityCounts(c.GetProperty("works").GetInt32(), c.GetProperty("partly").GetInt32(), c.GetProperty("broken").GetInt32());
        var combos = r.GetProperty("byRoute").EnumerateArray().Select(x => new CommunityCombo(
            Str(x, "route"), Num(x, "mfg") != 0, Str(x, "gpu_family"), (int)Num(x, "works"), (int)Num(x, "partly"), (int)Num(x, "broken"))).ToList();
        var notes = r.GetProperty("notes").EnumerateArray().Select(x => new CommunityNote(
            (long)Num(x, "id"), Str(x, "rating"), Str(x, "route"), x.TryGetProperty("mfg", out var m) && m.ValueKind == JsonValueKind.True,
            Str(x, "gpu"), Str(x, "gpu_family"), Str(x, "app_version"), Str(x, "note"),
            DateTimeOffset.FromUnixTimeMilliseconds((long)Num(x, "updated_at")).LocalDateTime)).ToList();
        CommunityMine? mine = null;
        if (r.TryGetProperty("mine", out var mi) && mi.ValueKind == JsonValueKind.Object)
            mine = new CommunityMine((long)Num(mi, "id"), Str(mi, "rating"), Str(mi, "route"), Num(mi, "mfg") != 0, Str(mi, "note"), Num(mi, "hidden") != 0);
        return new CommunitySummary(key, counts, combos, notes, mine);
    }

    public sealed record Report(string Key, string Name, string Rating, string Route, bool Mfg, string Note, Dictionary<string, string> Versions);

    public static async Task SendAsync(Report report, CancellationToken ct = default)
    {
        var gpu = LocalGpu ?? "Unknown";
        var body = new Dictionary<string, object>
        {
            ["install"] = InstallId,
            ["key"] = report.Key,
            ["name"] = report.Name.Length > 100 ? report.Name[..100] : report.Name,
            ["rating"] = report.Rating,
            ["route"] = report.Route,
            ["mfg"] = report.Mfg,
            ["gpu"] = gpu.Length > 80 ? gpu[..80] : gpu,
            ["gpuFamily"] = GpuFamily(gpu),
            ["versions"] = report.Versions,
            ["appVersion"] = Updates.CurrentAppVersion.ToString(),
            ["note"] = report.Note.Trim() is var n && n.Length > NoteMax ? n[..NoteMax] : report.Note.Trim(),
        };
        using var resp = await Http.PostAsJsonAsync("/v1/reports", body, ct);
        await EnsureOk(resp, ct);
    }

    public static async Task FlagAsync(long reportId, CancellationToken ct = default)
    {
        using var resp = await Http.PostAsJsonAsync($"/v1/reports/{reportId}/flag", new Dictionary<string, string> { ["install"] = InstallId }, ct);
        await EnsureOk(resp, ct);
    }

    private static async Task EnsureOk(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string? message = null;
        try { using var d = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)); message = d.RootElement.GetProperty("error").GetString(); } catch { }
        throw new InstallException(message ?? $"The community server answered {(int)resp.StatusCode}.");
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
