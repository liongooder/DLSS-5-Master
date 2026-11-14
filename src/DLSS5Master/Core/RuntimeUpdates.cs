using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSS5Master.Core;

public sealed record RuntimeSource(string Id, string Name, string FileName, string Repo, string TagPattern, string AssetPattern, string BundledVersion)
{
    public bool Streamline => Id == "streamline";
}
public sealed record RuntimeRelease(string Tag, string Version, DateTime Published, string Url, string Sha256, string Page, long Size = 0);
public sealed record RuntimeSelection(string Version, string Folder, Dictionary<string, string> Files, string? Tag = null);
public sealed class RuntimeUpdateState
{
    public DateTime? LastCheck { get; set; }
    public Dictionary<string, RuntimeRelease> Latest { get; set; } = new();
    public Dictionary<string, RuntimeSelection> Active { get; set; } = new();
}

/// <summary>Opt-in runtime selections in app data. Never writes to bundled files, custom folders or games.</summary>
public static class RuntimeUpdates
{
    public static readonly RuntimeSource[] Sources =
    {
        new("dlss-sr", "Super Resolution", "nvngx_dlss.dll", "RankFTW/rhi-repo", @"^dlss-(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$", @"^nvngx_dlss_[\d.]+\.zip$", "310.9.16.0"),
        new("dlss-rr", "Ray Reconstruction", "nvngx_dlssd.dll", "RankFTW/rhi-repo", @"^dlssd-(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$", @"^nvngx_dlssd_[\d.]+\.zip$", "310.9.16.0"),
        new("dlss-fg", "Frame Generation", "nvngx_dlssg.dll", "RankFTW/rhi-repo", @"^dlssg-(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$", @"^nvngx_dlssg_[\d.]+\.zip$", "310.9.16.0"),
        new("dlss-nr", "Neural Rendering model", "nvngx_dlssnr.dll", "RankFTW/rhi-repo", @"^dlssnr-(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$", @"^nvngx_dlssnr_[\d.]+\.zip$", "310.9.16.0"),
        new("streamline", "Streamline", "sl.interposer.dll", "RankFTW/rhi-repo", @"^streamline-(?<version>2\.13\.0\.0)$", @"^streamline_2\.13\.0\.0\.zip$", "2.13.0.0"),
    };
    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string StateFile => Path.Combine(AppPaths.Root, "runtime-updates.json");
    private static string Root => Path.Combine(AppPaths.Components, "runtime-updates");
    private static readonly object StateGate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static RuntimeUpdateState? _state;
    public static RuntimeUpdateState State
    {
        get
        {
            lock (StateGate)
            {
                if (_state is not null) return _state;
                try { _state = JsonSerializer.Deserialize<RuntimeUpdateState>(File.ReadAllText(StateFile)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
                _state ??= new();
                _state.Latest ??= new();
                _state.Active ??= new();
                return _state;
            }
        }
    }
    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Master-RuntimeUpdates");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    private static void Save(RuntimeUpdateState state)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var temp = StateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, StateFile, true);
        lock (StateGate) _state = state;
    }
    private static RuntimeUpdateState CopyState() => new()
    {
        LastCheck = State.LastCheck,
        Latest = new(State.Latest), Active = new(State.Active)
    };

    internal static int Compare(string? left, string? right)
    {
        static int[] Parts(string? value) => (value ?? "").Split('.').Select(p => int.TryParse(p, out var n) ? n : -1).Concat(new[] { 0, 0, 0, 0 }).Take(4).ToArray();
        var a = Parts(left); var b = Parts(right);
        for (int i = 0; i < 4; i++) { int c = a[i].CompareTo(b[i]); if (c != 0) return c; }
        return 0;
    }
    internal static bool ValidDigest(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-fA-F]{64}$");
    internal static bool ValidAssetUrl(RuntimeSource source, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com") return false;
        return uri.AbsolutePath.StartsWith("/" + source.Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase);
    }
    internal static RuntimeRelease? SelectRelease(RuntimeSource source, JsonElement releases)
    {
        var candidates = new List<RuntimeRelease>();
        foreach (var r in releases.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean() || r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var match = Regex.Match(tag, source.TagPattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            foreach (var asset in r.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!Regex.IsMatch(name, source.AssetPattern, RegexOptions.IgnoreCase)) continue;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (digest is null || !digest.StartsWith("sha256:") || !ValidDigest(digest[7..]) || !ValidAssetUrl(source, url)) continue;
                var date = r.TryGetProperty("published_at", out var at) && at.TryGetDateTime(out var parsed) ? parsed : DateTime.MinValue;
                candidates.Add(new(tag, match.Groups["version"].Value, date, url, digest[7..].ToLowerInvariant(),
                    $"https://github.com/{source.Repo}/releases/tag/{Uri.EscapeDataString(tag)}",
                    asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0));
            }
        }
        return candidates.OrderByDescending(r => r.Version, Comparer<string>.Create(Compare)).ThenByDescending(r => Revision(r.Tag)).ThenByDescending(r => r.Published).FirstOrDefault();
    }
    public static async Task CheckAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var repos = Sources.Select(s => s.Repo).Distinct().ToList();
            var pages = await Task.WhenAll(repos.Select(repo => Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases?per_page=100", ct)));
            var state = CopyState();
            foreach (var source in Sources)
            {
                using var doc = JsonDocument.Parse(pages[repos.IndexOf(source.Repo)]);
                var release = SelectRelease(source, doc.RootElement);
                if (release is null) state.Latest.Remove(source.Id); else state.Latest[source.Id] = release;
            }
            state.LastCheck = DateTime.UtcNow;
            Save(state);
        }
        finally { Gate.Release(); }
    }

    internal static int Revision(string tag) => Regex.Match(tag, @"-v(?<n>\d+)$") is { Success: true } m && int.TryParse(m.Groups["n"].Value, out var n) ? n : 0;
    public static RuntimeRelease? Available(RuntimeSource source, string? selectedVersion = null)
    {
        if (!State.Latest.TryGetValue(source.Id, out var release) || !ValidAssetUrl(source, release.Url) || source.Streamline && release.Version != source.BundledVersion) return null;
        var active = State.Active.GetValueOrDefault(source.Id);
        var current = selectedVersion ?? (active is not null && SelectionReady(source) ? active.Version : source.BundledVersion);
        var comparison = Compare(release.Version, current);
        return comparison > 0 || source.Streamline && comparison == 0 && Revision(release.Tag) > Revision(active?.Tag ?? "") ? release : null;
    }

    public static bool CheckDue => Updates.State.AutoCheck && (Sources.Any(s => s.Streamline && State.Latest.TryGetValue(s.Id, out var latest) && latest.Version != s.BundledVersion) || State.LastCheck is null || DateTime.UtcNow - State.LastCheck > TimeSpan.FromDays(1) || Sources.Any(s => State.Latest.TryGetValue(s.Id, out var release) && !ValidAssetUrl(s, release.Url)));

    private static readonly ConcurrentDictionary<string, (long Length, long Modified, string Sha, bool Valid)> Verified = new(StringComparer.OrdinalIgnoreCase);
    private static bool CheckFile(string file, string sha)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || !ValidDigest(sha)) return false;
            if (Verified.TryGetValue(file, out var known) && known.Length == info.Length && known.Modified == info.LastWriteTimeUtc.Ticks && known.Sha == sha) return known.Valid;
            var ok = ComponentHealth.Matches(file, sha);
            Verified[file] = (info.Length, info.LastWriteTimeUtc.Ticks, sha, ok);
            return ok;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    private static string? SelectionRoot(RuntimeSelection selection)
    {
        if (!Guid.TryParseExact(selection.Folder, "N", out _)) return null;
        return Path.Combine(Root, selection.Folder);
    }
    public static bool SelectionReady(RuntimeSource source)
    {
        if (!State.Active.TryGetValue(source.Id, out var selected) || selected.Files is null || selected.Files.Count == 0 || SelectionRoot(selected) is not { } root) return false;
        if (!selected.Files.ContainsKey(source.FileName)) return false;
        if (source.Streamline && Compare(selected.Version, source.BundledVersion) != 0) return false;
        if (!source.Streamline && (selected.Files.Count != 1 || !selected.Files.ContainsKey(source.FileName))) return false;
        if (source.Streamline && !new[] { "sl.common.dll", "sl.interposer.dll", "sl.dlss.dll", "sl.dlss_g.dll", "sl.reflex.dll" }.All(selected.Files.ContainsKey)) return false;
        foreach (var (name, sha) in selected.Files)
        {
            if (Path.GetFileName(name) != name || name.Contains(':') || source.Streamline && !(Regex.IsMatch(name, @"^sl\.[\w]+\.dll$", RegexOptions.IgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))) return false;
            if (!CheckFile(Path.Combine(root, name), sha)) return false;
        }
        return true;
    }
    public static void ApplySelections(Dictionary<string, string> files)
    {
        foreach (var source in Sources)
        {
            if (!SelectionReady(source)) continue;
            var selected = State.Active[source.Id];
            if (source.Streamline)
                foreach (var old in files.Keys.Where(k => k.StartsWith("sl.", StringComparison.OrdinalIgnoreCase)).ToList()) files.Remove(old);
            foreach (var name in selected.Files.Keys.Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                files[name] = Path.Combine(SelectionRoot(selected)!, name);
        }
    }
    public static string? SelectedVersion(RuntimeSource source, IReadOnlyDictionary<string, string> files) =>
        files.TryGetValue(source.FileName, out var file) ? Pe.GetFileVersion(file) : null;

    internal static IReadOnlyList<ZipArchiveEntry> PackageFiles(RuntimeSource source, ZipArchive archive)
    {
        // The selected mirror release ships flat x64 binaries. Never mix files from another package.
        if (source.Streamline)
        {
            const string prefix = "";
            var entries = archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !e.FullName[prefix.Length..].Contains('/') && !e.FullName.Contains('\\') &&
                (Regex.IsMatch(e.Name, @"^sl\.[\w]+\.dll$", RegexOptions.IgnoreCase) || e.Name.EndsWith(".license.txt", StringComparison.OrdinalIgnoreCase))).ToList();
            if (!new[] { "sl.common.dll", "sl.interposer.dll", "sl.dlss.dll", "sl.dlss_g.dll", "sl.reflex.dll" }.All(n => entries.Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
                throw new InstallException("The Streamline release lacks a complete x64 set.");
            if (entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new InstallException("Duplicate Streamline production files.");
            return entries;
        }
        var matches = archive.Entries.Where(e => e.Name.Equals(source.FileName, StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('\\') &&
            !e.FullName.Split('/').Contains("..")).ToList();
        if (matches.Count != 1) throw new InstallException("The release must contain exactly one " + source.FileName + ".");
        return matches;
    }

    public static async Task ApplyAsync(RuntimeSource source, IProgress<string>? progress, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var release = Available(source) ?? throw new InstallException("No newer verified release is available. Check for runtime updates first.");
            if (!ValidDigest(release.Sha256) || !ValidAssetUrl(source, release.Url)) throw new InstallException("Invalid runtime release source or checksum.");
            var archive = Path.Combine(Root, "downloads", release.Sha256 + ".zip");
            await Components.DownloadVerifiedAsync(release.Url, release.Sha256, archive, progress, ct);
            await ActivateArchiveAsync(source, release, archive, Pe.GetBitness, Pe.GetFileVersion, ct);
            AppPaths.Log($"Runtime update selected: {source.Name} {release.Version} from {source.Repo}.");
        }
        finally { Gate.Release(); }
    }
    internal static async Task ActivateArchiveAsync(RuntimeSource source, RuntimeRelease release, string archivePath,
        Func<string, int> bitness, Func<string, string?> version, CancellationToken ct = default)
    {
        if (source.Streamline && Compare(release.Version, source.BundledVersion) != 0) throw new InstallException("Streamline is pinned to 2.13.0.0 until neural rendering compatibility is tested.");
        if (!ComponentHealth.Matches(archivePath, release.Sha256)) throw new InstallException("Runtime archive failed SHA-256 verification.");
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(Root, id);
        Directory.CreateDirectory(stage);
        bool committed = false;
        try
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in PackageFiles(source, archive))
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.Name.ToLowerInvariant();
                var target = Journal.SafePath(stage, name);
                entry.ExtractToFile(target);
                if (entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && bitness(target) != 64)
                    throw new InstallException(entry.Name + " is not a 64-bit DLL.");
                hashes[name] = Components.Sha256(target);
            }
            if (Compare(version(Path.Combine(stage, source.FileName)), release.Version) != 0)
                throw new InstallException("The downloaded file version does not match its release tag.");
            ct.ThrowIfCancellationRequested();
            var state = CopyState();
            state.Active[source.Id] = new(release.Version, id, hashes, release.Tag);
            Save(state);
            committed = true;
        }
        finally
        {
            if (!committed && Path.GetFullPath(stage).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(stage, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    public static async Task RevertAsync(RuntimeSource source)
    {
        await Gate.WaitAsync();
        try { var state = CopyState(); state.Active.Remove(source.Id); Save(state); }
        finally { Gate.Release(); }
    }
}
