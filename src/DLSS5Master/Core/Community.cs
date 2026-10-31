using System.Globalization;
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

/// <summary>
/// One player's report and the PC it ran on. Reports sent before 0.9.8 have no driver, processor, memory or Windows ("" / 0);
/// a hidden one (flagged, or its author banned) comes without its note, card and processor names, versions and nickname.
/// </summary>
public sealed record CommunityReport(long Id, string Rating, string Route, bool Mfg, string Api, string Gpu, string GpuFamily, string Driver,
                                     string Cpu, int RamGb, string Os, string AppVersion, IReadOnlyDictionary<string, string> Versions,
                                     string Note, bool Hidden, DateTime When, string Nickname = "", bool IsDev = false);

public sealed record CommunityMine(long Id, string Rating, string Route, bool Mfg, string Note, bool Hidden);

/// <summary>A game's community page. <paramref name="AllReports"/> is false when the server sent only the reports with a note (its "notes", before 0.9.8).</summary>
public sealed record CommunitySummary(string Key, CommunityCounts Counts, List<CommunityCombo> Combos, List<CommunityReport> Reports, bool AllReports,
                                      CommunityMine? Mine);

/// <summary>
/// Per-game community reports ("works / partly / broken" + an optional short note) shared through the DLSS 5 Master
/// community server. No accounts: each install has a random ID, which the server stores only as a salted hash.
/// Reading sends only that random ID (so the server can mark the reader's own report; reads are not stored); a report
/// or a flag is sent only when the user presses Send or Flag.
/// </summary>
public static partial class Community
{
    /// <summary>The community server; tests point DLSS5MASTER_COMMUNITY at a local copy.</summary>
    public static readonly string Server = Environment.GetEnvironmentVariable("DLSS5MASTER_COMMUNITY") is { Length: > 0 } test
        ? test.TrimEnd('/') : "https://dlss5master-community.dlss5master-community.workers.dev";
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

    /// <summary>
    /// A report's PC in one line, leaving out what is not known:
    /// "NVIDIA GeForce RTX 4070 · driver 581.57 · AMD Ryzen 7 7800X3D · 32 GB RAM · Windows 11 24H2 (26100)".
    /// </summary>
    public static string SpecsLine(string gpu, string driver, string cpu, int ramGb, string os) => string.Join(" · ",
        new[] { gpu, driver.Length > 0 ? "driver " + driver : "", cpu, ramGb > 0 ? ramGb.ToString(CultureInfo.InvariantCulture) + " GB RAM" : "", os }
            .Where(p => p.Length > 0));

    /// <summary>This PC as a report sent from it describes it.</summary>
    public static string LocalSpecs => SpecsLine(LocalGpu ?? "", Gpu.DriverFor(LocalGpu), PcSpecs.Cpu, PcSpecs.RamGb, PcSpecs.Windows);

    // ------------------------------------------------------------------ calls

    public static async Task<CommunitySummary> GetAsync(string key, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/games/" + Uri.EscapeDataString(key));
        req.Headers.Add("X-Install", InstallId);
        using var resp = await Http.SendAsync(req, ct);
        await EnsureOk(resp, ct);
        return ParseSummary(key, await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The server's page for one game. A server before 0.9.8 sends no "reports", only the ones with a note ("notes").</summary>
    internal static CommunitySummary ParseSummary(string key, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var c = r.GetProperty("counts");
        var counts = new CommunityCounts(c.GetProperty("works").GetInt32(), c.GetProperty("partly").GetInt32(), c.GetProperty("broken").GetInt32());
        var combos = r.GetProperty("byRoute").EnumerateArray().Select(x => new CommunityCombo(
            Str(x, "route"), Num(x, "mfg") != 0, Str(x, "gpu_family"), (int)Num(x, "works"), (int)Num(x, "partly"), (int)Num(x, "broken"))).ToList();
        bool all = r.TryGetProperty("reports", out var list) && list.ValueKind == JsonValueKind.Array;
        var reports = (all ? list : r.GetProperty("notes")).EnumerateArray().Select(x => new CommunityReport(
            (long)Num(x, "id"), Str(x, "rating"), Str(x, "route"), Bool(x, "mfg"), Str(x, "api"), Str(x, "gpu"), Str(x, "gpu_family"),
            Str(x, "driver"), Str(x, "cpu"), (int)Num(x, "ram_gb"), Str(x, "os"), Str(x, "app_version"), Versions(x), Str(x, "note"), Bool(x, "hidden"),
            DateTimeOffset.FromUnixTimeMilliseconds((long)Num(x, "updated_at")).LocalDateTime, Str(x, "nickname"), Bool(x, "is_dev"))).ToList();
        CommunityMine? mine = null;
        if (r.TryGetProperty("mine", out var mi) && mi.ValueKind == JsonValueKind.Object)
            mine = new CommunityMine((long)Num(mi, "id"), Str(mi, "rating"), Str(mi, "route"), Num(mi, "mfg") != 0, Str(mi, "note"), Num(mi, "hidden") != 0);
        return new CommunitySummary(key, counts, combos, reports, all, mine);
    }

    private static Dictionary<string, string> Versions(JsonElement e)
    {
        var versions = new Dictionary<string, string>();
        if (e.TryGetProperty("versions", out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) versions[p.Name] = p.Value.GetString() ?? "";
        return versions;
    }

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.\-_+ ]{0,39}$")]
    private static partial Regex VersionText();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,5}){1,3}$")]
    private static partial Regex AppVersionText();

    /// <summary>
    /// What a report's install used, for a line under it: "DLSS 5 Master 0.9.8 · RenoDX DLSS5 add-on 10.8.0 · MFG unlock (MFGAdaUnlock) 1.7.0".
    /// Only the add-ons this app knows, with version-looking values: anything else a report carries is not shown.
    /// </summary>
    public static string VersionsLine(string appVersion, IReadOnlyDictionary<string, string> versions)
    {
        var parts = new List<string>();
        if (AppVersionText().IsMatch(appVersion)) parts.Add("DLSS 5 Master " + appVersion);
        foreach (var (id, version) in versions)
        {
            if (!VersionText().IsMatch(version)) continue;
            var name = id == "optiscaler" ? "OptiScaler" : Updates.Sources.FirstOrDefault(s => s.Id == id)?.Name;
            if (name is not null) parts.Add(name + " " + version);
        }
        return string.Join(" · ", parts);
    }

    public sealed record Report(string Key, string Name, string Rating, string Route, bool Mfg, string Note, Dictionary<string, string> Versions, RenderApi Api = RenderApi.Unknown);

    /// <summary>The community server's name for a rendering API ("" when unknown).</summary>
    public static string ApiKey(RenderApi api) => api switch
    {
        RenderApi.DX12 => "dx12",
        RenderApi.DX11 => "dx11",
        RenderApi.DX10 => "dx10",
        RenderApi.DX9 => "dx9",
        RenderApi.DX8 => "dx8",
        RenderApi.DirectDraw => "ddraw",
        RenderApi.Vulkan => "vulkan",
        RenderApi.OpenGL => "opengl",
        _ => "",
    };

    public static string ApiLabel(string key) => key switch
    {
        "dx12" => "DX12", "dx11" => "DX11", "dx10" => "DX10", "dx9" => "DX9", "dx8" => "DX8",
        "ddraw" => "DDraw", "vulkan" => "Vulkan", "opengl" => "OpenGL", _ => key.ToUpperInvariant(),
    };

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
            ["driver"] = Gpu.DriverFor(gpu),
            ["cpu"] = PcSpecs.Cpu,
            ["ramGb"] = PcSpecs.RamGb,
            ["os"] = PcSpecs.Windows,
            ["api"] = ApiKey(report.Api),
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
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
