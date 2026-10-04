using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSS5Master.Core;

/// <summary>
/// Updates DLSS 5 Master itself from the project's GitHub releases: finds the newest stable release that has a
/// DLSS5Master-Setup-&lt;version&gt;.exe with a SHA-256 digest, downloads it (in parallel pieces, GitHub can be slow),
/// verifies it, and runs it silently. The installer closes the app, updates it in place and starts it again.
/// </summary>
public static partial class Updates
{
    public const string AppRepo = "liongooder/DLSS-5-Master";

    public static Version CurrentAppVersion { get; } = typeof(Updates).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    [GeneratedRegex(@"^DLSS5Master-Setup-[\d.]+\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex SetupAsset();

    /// <summary>The newer app release, or null when this is the newest one (or no check has run yet).</summary>
    public static AppRelease? AppUpdateAvailable =>
        State.App is { } a && Version.TryParse(a.Version, out var v) && v > CurrentAppVersion ? a : null;

    /// <summary>Asks GitHub for the newest stable DLSS 5 Master release and remembers it.</summary>
    public static async Task CheckAppAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await GetWithRetryAsync($"https://api.github.com/repos/{AppRepo}/releases?per_page=10", ct));
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
            var tag = (r.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out _)) continue;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var digest = a.TryGetProperty("digest", out var dg) ? dg.GetString() ?? "" : "";
                if (!SetupAsset().IsMatch(name) || !digest.StartsWith("sha256:") || digest.Length != 71) continue;
                State.App = new AppRelease
                {
                    Version = tag,
                    Page = r.GetProperty("html_url").GetString() ?? "",
                    Url = a.GetProperty("browser_download_url").GetString() ?? "",
                    Sha256 = digest[7..],
                    Size = a.GetProperty("size").GetInt64(),
                };
                Save();
                return;
            }
        }
    }

    /// <summary>
    /// Downloads and verifies the new installer, then starts it silently. The caller must exit the app right after:
    /// the installer waits for it to close, replaces the files and starts the new version.
    /// </summary>
    public static async Task InstallAppUpdateAsync(AppRelease rel, IProgress<string>? p, CancellationToken ct = default)
    {
        var dir = Path.Combine(AppPaths.Root, "app-update");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "DLSS5Master-Setup-*.exe"))
            if (!Path.GetFileName(old).Contains(rel.Version)) try { File.Delete(old); } catch { }
        var file = Path.Combine(dir, $"DLSS5Master-Setup-{rel.Version}.exe");

        if (!File.Exists(file) || !(await Task.Run(() => Sha256Of(file), ct)).Equals(rel.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await DownloadInPiecesAsync(rel.Url, rel.Size, file + ".part", p, ct);
            p?.Report("Checking the download…");
            var got = await Task.Run(() => Sha256Of(file + ".part"), ct);
            if (!got.Equals(rel.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file + ".part");
                throw new InstallException("The downloaded installer does not match the checksum GitHub published for it. Nothing was installed; try again.");
            }
            File.Move(file + ".part", file, true);
        }

        p?.Report($"Installing DLSS 5 Master {rel.Version}…");
        AppPaths.Log($"App update: starting installer {rel.Version}");
        Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
    }

    private static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Range requests in parallel, each retried: a single GitHub download stream can crawl at a few KB/s.</summary>
    private static async Task DownloadInPiecesAsync(string url, long size, string target, IProgress<string>? p, CancellationToken ct)
    {
        const int pieces = 16;
        long step = size / pieces + 1, done = 0;
        await using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None)) fs.SetLength(size);
        var gate = new object();
        var lastReport = DateTime.MinValue;

        await Parallel.ForEachAsync(Enumerable.Range(0, pieces), new ParallelOptions { MaxDegreeOfParallelism = pieces, CancellationToken = ct }, async (i, token) =>
        {
            long from = i * step, to = Math.Min(size, from + step) - 1;
            if (from > to) return;
            long pos = from;   // a retry resumes where the piece stopped
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(pos, to);
                    req.Headers.Accept.ParseAdd("application/octet-stream");
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                    resp.EnsureSuccessStatusCode();
                    if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) throw new HttpRequestException("range not supported");
                    await using var input = await resp.Content.ReadAsStreamAsync(token);
                    await using var output = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.Write);
                    output.Position = pos;
                    var buffer = new byte[1 << 16];
                    int n;
                    while (pos <= to && (n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, to - pos + 1)), token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), token);
                        pos += n;
                        lock (gate)
                        {
                            done += n;
                            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(300))
                            {
                                lastReport = DateTime.UtcNow;
                                p?.Report($"Downloading DLSS 5 Master… {done * 100 / size}%  ({done / 1048576} of {size / 1048576} MB)");
                            }
                        }
                    }
                    if (pos <= to) throw new IOException("piece ended early");
                    return;
                }
                catch (Exception) when (attempt < 10 && !token.IsCancellationRequested)
                {
                    await Task.Delay(2000 * Math.Min(attempt, 5), token);
                }
            }
        });
    }
}
