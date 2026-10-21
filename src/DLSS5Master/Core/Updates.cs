using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSS5Master.Core;

public enum UpdateKind { File, ZipFile, Feeder, OptiScaler }

/// <summary>An add-on whose newer releases DLSS 5 Master can pick up from its GitHub releases.</summary>
public sealed record UpdateSource(string Id, string Name, string Repo, string? TagPrefix, string AssetPattern, UpdateKind Kind,
                                  string? Inner, string BundledVersion, DateTime BundledDate);

public sealed class ReleaseAsset
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class ReleaseInfo
{
    public string Tag { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime Published { get; set; }
    public bool Prerelease { get; set; }
    public List<ReleaseAsset> Assets { get; set; } = new();
}

/// <summary>A newer add-on version the user switched to. Path is a file or folder; Builds is used for OptiScaler.</summary>
public sealed class ActiveUpdate
{
    public string Version { get; set; } = "";
    public DateTime Published { get; set; }
    public string? Path { get; set; }
    public List<ReleaseAsset> Builds { get; set; } = new();
}

public sealed class UpdateState
{
    public DateTime? LastCheck { get; set; }
    public bool IncludePrereleases { get; set; }
    public bool AutoCheck { get; set; } = true;
    public Dictionary<string, ReleaseInfo> Latest { get; set; } = new();
    public Dictionary<string, ActiveUpdate> Active { get; set; } = new();
    /// <summary>The newest DLSS 5 Master release seen on GitHub (any version, newer or not).</summary>
    public AppRelease? App { get; set; }
}

public sealed class AppRelease
{
    public string Version { get; set; } = "";
    public string Page { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// Finds newer releases of the bundled add-ons on GitHub and switches DLSS 5 Master to them on request. Every file is
/// verified against the SHA-256 digest GitHub publishes for it; releases without a digest are never offered. Games that
/// were already set up keep their version until they are reinstalled.
/// </summary>
public static partial class Updates
{
    public static readonly UpdateSource[] Sources =
    {
        new("renodx-dlss5", "RenoDX DLSS5 add-on", "RankFTW/rhi-repo", "renodx-dlss5-", @"^renodx-dlss5_.*\.zip$",
            UpdateKind.ZipFile, "renodx-dlss5.addon64", "6.5.3", new DateTime(2026, 9, 19, 17, 9, 53, DateTimeKind.Utc)),
        new("renodx-dlss", "RenoDX DLSS Tool (multipass)", "RankFTW/rhi-repo", "renodx-dlss-SF-", @"^renodx-dlss_SF_.*\.zip$",
            UpdateKind.ZipFile, "renodx-dlss.addon64", "SF 26.1003.2350", new DateTime(2026, 10, 4, 7, 59, 13, DateTimeKind.Utc)),
        new("mfgunlock", "MFG unlock (MFGAdaUnlock)", "mavismmg/MFGAdaUnlock-RenoDx", null, @"^renodx-mfgunlock\.addon64$",
            UpdateKind.File, null, "1.4.5", new DateTime(2026, 10, 18, 3, 20, 48, DateTimeKind.Utc)),
        new("feeder", "DLSS5 Feeder", "jlrouzies-fr/DLSS5-Feeder", null, @"^DLSS5-Feeder-.*\.zip$",
            UpdateKind.Feeder, null, "1.19.0", new DateTime(2026, 10, 11, 5, 56, 10, DateTimeKind.Utc)),
        new("optiscaler-nr", "OptiScaler NR (pre-SR multipass)", "wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass", null, @"^OptiScaler-NR-v[\d.]+(-rtx40-mfg)?\.zip$",
            UpdateKind.OptiScaler, null, "0.8.3", new DateTime(2026, 9, 13, 6, 16, 54, DateTimeKind.Utc)),
    };

    // Downloads (add-on files, the app installer in pieces): long timeout, but no reuse of dead connections.
    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(120));
    // GitHub API look-ups are small: fail fast, so a stalled connection never leaves "Checking…" on screen for minutes.
    private static readonly HttpClient Api = CreateClient(TimeSpan.FromSeconds(20));

    /// <summary>The longest a whole update check may take before it gives up and says GitHub could not be reached.</summary>
    public static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(60);

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
        };
        var c = new HttpClient(handler) { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"DLSS5Master/{typeof(Updates).Assembly.GetName().Version?.ToString(3)}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static string StateFile => Path.Combine(AppPaths.Root, "updates.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static UpdateState? _state;

    public static UpdateState State
    {
        get
        {
            if (_state is not null) return _state;
            try { _state = File.Exists(StateFile) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StateFile)) : null; }
            catch (Exception e) { AppPaths.Log("Could not read updates.json: " + e.Message); }
            return _state ??= new UpdateState();
        }
    }

    private static readonly object SaveLock = new();

    /// <summary>Writes updates.json. Locked: the add-on check and the app check can finish at the same moment.</summary>
    public static void Save()
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(StateFile + ".tmp", JsonSerializer.Serialize(State, Json));
            File.Move(StateFile + ".tmp", StateFile, true);
        }
    }

    [GeneratedRegex(@"(^|[-_.\s])(rc|beta|alpha|preview|pre)\d*([-_.\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PreTag();

    // ------------------------------------------------------------------ status

    public static (string Version, DateTime Published, bool Updated) InUse(UpdateSource s) =>
        State.Active.TryGetValue(s.Id, out var a) ? (a.Version, a.Published, true) : (s.BundledVersion, s.BundledDate, false);

    public static ReleaseInfo? Available(UpdateSource s) =>
        State.Latest.TryGetValue(s.Id, out var r) && r.Published > InUse(s).Published ? r : null;

    public static int AvailableCount => Sources.Count(s => Available(s) is not null);

    public static bool CheckDue => State.AutoCheck && (State.LastCheck is null || DateTime.UtcNow - State.LastCheck > TimeSpan.FromDays(1));

    // ------------------------------------------------------------------ check

    /// <summary>
    /// Asks GitHub for each add-on's newest release with a verifiable asset: one request per repository, all in
    /// parallel, and only the most recent releases (release lists with notes are large).
    /// </summary>
    public static async Task CheckAsync(CancellationToken ct = default)
    {
        var groups = Sources.GroupBy(s => s.Repo).ToList();
        var pages = await Task.WhenAll(groups.Select(g =>
            GetWithRetryAsync($"https://api.github.com/repos/{g.Key}/releases?per_page={(g.Count() > 1 ? 30 : 10)}", ct)));
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            using var doc = JsonDocument.Parse(pages[gi]);
            var releases = doc.RootElement.EnumerateArray()
                .Where(r => !(r.TryGetProperty("draft", out var d) && d.GetBoolean()))
                .Select(r => new
                {
                    Tag = r.GetProperty("tag_name").GetString() ?? "",
                    Published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime().ToUniversalTime() : DateTime.MinValue,
                    Pre = r.TryGetProperty("prerelease", out var pr) && pr.GetBoolean(),
                    Assets = r.GetProperty("assets").EnumerateArray().Select(a => new ReleaseAsset
                    {
                        Name = a.GetProperty("name").GetString() ?? "",
                        Url = a.GetProperty("browser_download_url").GetString() ?? "",
                        Sha256 = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } s && s.StartsWith("sha256:") ? s[7..] : "",
                    }).ToList(),
                })
                .OrderByDescending(r => r.Published)
                .ToList();

            foreach (var src in group)
            {
                var pattern = new Regex(src.AssetPattern, RegexOptions.IgnoreCase);
                var pick = releases.FirstOrDefault(r =>
                    (src.TagPrefix is null || r.Tag.StartsWith(src.TagPrefix, StringComparison.OrdinalIgnoreCase)) &&
                    (State.IncludePrereleases || !(r.Pre || PreTag().IsMatch(r.Tag))) &&
                    r.Assets.Any(a => pattern.IsMatch(a.Name) && a.Sha256.Length == 64));
                if (pick is null) { State.Latest.Remove(src.Id); continue; }
                var version = pick.Tag;
                if (src.TagPrefix is not null) version = version[src.TagPrefix.Length..];
                version = version.TrimStart('v', 'V');
                if (src.Id == "renodx-dlss") version = "SF " + version;
                State.Latest[src.Id] = new ReleaseInfo
                {
                    Tag = pick.Tag, Version = version, Published = pick.Published, Prerelease = pick.Pre || PreTag().IsMatch(pick.Tag),
                    Assets = pick.Assets.Where(a => pattern.IsMatch(a.Name) && a.Sha256.Length == 64).ToList(),
                };
            }
        }
        State.LastCheck = DateTime.UtcNow;
        Save();
    }

    /// <summary>GitHub requests occasionally drop or stall; try up to three times (20 s each) before giving up.</summary>
    private static async Task<string> GetWithRetryAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await Api.GetStringAsync(url, ct); }
            catch (HttpRequestException) when (attempt < 3) { await Task.Delay(1000 * attempt, ct); }
            catch (TaskCanceledException) when (attempt < 3 && !ct.IsCancellationRequested) { await Task.Delay(1000 * attempt, ct); }
        }
    }

    /// <summary>A friendly reason for a failed check, for the status line.</summary>
    public static string CheckFailure(Exception e) => e is OperationCanceledException or TimeoutException or HttpRequestException
        ? "GitHub could not be reached just now. It will try again next time, or press Check now."
        : "Could not check for updates: " + e.Message;

    // ------------------------------------------------------------------ apply / undo

    private static string UpdateDir(UpdateSource s, string version) =>
        Path.Combine(AppPaths.Components, "updates", s.Id, Regex.Replace(version, @"[^\w.\-]+", "_"));

    /// <summary>Downloads and verifies the available release, then makes it the version used for new installs.</summary>
    public static async Task ApplyAsync(UpdateSource s, IProgress<string>? p, CancellationToken ct = default)
    {
        var rel = State.Latest.GetValueOrDefault(s.Id) ?? throw new InstallException("No newer version is known. Check for updates first.");
        var dir = UpdateDir(s, rel.Version);
        string? path = null;
        switch (s.Kind)
        {
            case UpdateKind.File:
            {
                var a = rel.Assets[0];
                path = Path.Combine(dir, a.Name);
                await Components.DownloadVerifiedAsync(a.Url, a.Sha256, path, p, ct);
                break;
            }
            case UpdateKind.ZipFile:
            {
                var a = rel.Assets[0];
                var zip = Path.Combine(dir, a.Name);
                await Components.DownloadVerifiedAsync(a.Url, a.Sha256, zip, p, ct);
                var extract = Path.Combine(dir, "files");
                await Task.Run(() => { if (Directory.Exists(extract)) Directory.Delete(extract, true); ZipFile.ExtractToDirectory(zip, extract); }, ct);
                path = Directory.EnumerateFiles(extract, s.Inner!, SearchOption.AllDirectories).FirstOrDefault()
                       ?? throw new InstallException($"{a.Name} does not contain {s.Inner}. Nothing was changed.");
                break;
            }
            case UpdateKind.Feeder:
            {
                var a = rel.Assets[0];
                var zip = Path.Combine(dir, a.Name);
                await Components.DownloadVerifiedAsync(a.Url, a.Sha256, zip, p, ct);
                p?.Report("Preparing the Feeder update…");
                path = await Task.Run(() => BuildFeeder(zip, dir), ct);
                break;
            }
            case UpdateKind.OptiScaler:
                // Builds download when first used (OptiScaler packages are large); here they are only registered.
                break;
        }
        State.Active[s.Id] = new ActiveUpdate
        {
            Version = rel.Version, Published = rel.Published, Path = path,
            Builds = s.Kind == UpdateKind.OptiScaler ? rel.Assets : new(),
        };
        Save();
        AppPaths.Log($"Add-on update: {s.Name} → {rel.Version}");
    }

    /// <summary>The bundled Feeder set (shaders, licences, scripts) with every file from the new release laid over it.</summary>
    private static string BuildFeeder(string zip, string dir)
    {
        var fresh = Path.Combine(dir, "release");
        var merged = Path.Combine(dir, "feeder");
        if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
        if (Directory.Exists(merged)) Directory.Delete(merged, true);
        ZipFile.ExtractToDirectory(zip, fresh);
        var bundled = Path.Combine(Components.BundledPayload, "feeder");
        if (Directory.Exists(bundled))
            foreach (var f in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories))
            {
                var t = Path.Combine(merged, Path.GetRelativePath(bundled, f));
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                File.Copy(f, t, true);
            }
        var mergedFiles = Directory.Exists(merged) ? Directory.EnumerateFiles(merged, "*", SearchOption.AllDirectories).ToList() : new();
        foreach (var f in Directory.EnumerateFiles(fresh, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            var targets = mergedFiles.Where(m => Path.GetFileName(m).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (targets.Count == 0) targets.Add(Path.Combine(merged, Path.GetRelativePath(fresh, f)));
            foreach (var t in targets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                File.Copy(f, t, true);
            }
        }
        if (Components.FindFeederAny(merged) is null)
            throw new InstallException("The new DLSS5 Feeder release is missing files DLSS 5 Master needs. Nothing was changed.");
        return merged;
    }

    /// <summary>Go back to the version that ships with DLSS 5 Master.</summary>
    public static void Revert(UpdateSource s)
    {
        State.Active.Remove(s.Id);
        Save();
        AppPaths.Log($"Add-on update undone: {s.Name} back to {s.BundledVersion}");
    }

    /// <summary>
    /// Accepted add-on updates that this app version already includes (its bundled copy is the same release or a
    /// newer one). Such an update is redundant: the built-in copy should be used again.
    /// </summary>
    public static List<string> Superseded(UpdateState state) =>
        Sources.Where(s => state.Active.TryGetValue(s.Id, out var a) && a.Published <= s.BundledDate)
               .Select(s => s.Id).ToList();

    /// <summary>Run at start-up: after an app update, switch back to the built-in copy of any add-on it now includes.</summary>
    public static void DropSuperseded()
    {
        var ids = Superseded(State);
        if (ids.Count == 0) return;
        foreach (var id in ids)
        {
            var src = Sources.First(s => s.Id == id);
            AppPaths.Log($"Add-on update no longer needed: {src.Name} {State.Active[id].Version} is included with this version (built-in {src.BundledVersion})");
            State.Active.Remove(id);
        }
        Save();
    }

    // ------------------------------------------------------------------ used by Components

    public static string? ActivePath(string id) =>
        State.Active.TryGetValue(id, out var a) && a.Path is { } p && (File.Exists(p) || Directory.Exists(p)) ? p : null;

    /// <summary>OptiScaler builds from an accepted update, newest edition first.</summary>
    public static IEnumerable<OptiBuild> OptiBuilds()
    {
        if (!State.Active.TryGetValue("optiscaler-nr", out var a)) yield break;
        foreach (var asset in a.Builds.OrderByDescending(b => b.Name.Contains("rtx40", StringComparison.OrdinalIgnoreCase)))
        {
            bool mfg = asset.Name.Contains("rtx40", StringComparison.OrdinalIgnoreCase);
            yield return new OptiBuild($"{a.Version}{(mfg ? "-rtx40-mfg" : "")}",
                $"OptiScaler NR {a.Version} · {(mfg ? "RTX 40 MFG" : "standard")} (updated)",
                asset.Url, asset.Sha256, null, null, Forwarded: false, NeuralRendering: true);
        }
    }
}
