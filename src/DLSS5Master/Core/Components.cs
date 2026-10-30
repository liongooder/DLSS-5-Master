using System.IO.Compression;
using System.Security.Cryptography;

namespace DLSS5Master.Core;

/// <summary>
/// A pinned OptiScaler package. <see cref="WholeBackend"/>
/// builds need their entire OptiScaler\ folder (own Streamline, frame-generation modules, model data), not just the
/// usual libraries.
/// </summary>
public sealed record OptiBuild(string Id, string Label, string? Url, string? Sha256, string? LicenseUrl,
                               string? LicenseSha256, bool Forwarded, bool NeuralRendering, bool WholeBackend = false);

/// <summary>Locates or downloads (always hash-verified) every third-party file an install needs.</summary>
public sealed class Components
{
    /// <summary>OptiScaler builds offered in the app: updated ones first, then the built-in list.</summary>
    public static List<OptiBuild> AllOptiBuilds => Updates.OptiBuilds().Concat(OptiBuilds).ToList();

    public static readonly OptiBuild[] OptiBuilds =
    {
        new("0.9.81-shyvortex", "OptiScaler NR 0.9.81 (recommended)",
            "https://github.com/ShyVortex/OptiScaler-NR-PreSR-MFG/releases/download/v0.9.81/OptiScaler-NR-v0.9.81.zip",
            "106fd70cc99768e4a5551b754cc4fd72840c87048d3034964752095e0b40bc25", null, null, false, true, WholeBackend: true),
        new("0.8.3-rtx40-mfg", "OptiScaler NR 0.8.3 · RTX 40 MFG (older)",
            "https://github.com/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass/releases/download/v0.8.3/OptiScaler-NR-v0.8.3-rtx40-mfg.zip",
            "aac7ea64d80604a5b79f686043ba28fbefefaf98818e861a377b78122cb24448", null, null, false, true),
    };

    public static readonly Dictionary<string, string> FeederHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dlss5-feed.addon32"] = "34c767bd53a99f51a6cb5d87b267734e04314d16bf495a6c69ab98acd62d0874",
        ["dlss5-feed.addon64"] = "2b8c905012fd464ddf0554c6b178cba55030ec10eb917acdcee0e7a2376fe4dd",
        ["dlss5-feed-host64.exe"] = "79e427430c5c0102f6c7a557155b706ad51b0c40933f6e245fce922bf5b1401e",
        ["DLSS5_Feed.fx"] = "c4ba1610df8e1593faa7c203d6d0517058d3a8ecf2dfda18ca67849844391456",
    };

    public const string MfgAddon = "renodx-mfgunlock.addon64";
    public const string MfgVersion = "1.7.0";
    public const string MfgUrl = "https://github.com/mavismmg/MFGAdaUnlock-RenoDx/releases/download/1.7.0/renodx-mfgunlock.addon64";
    public const string MfgSha256 = "3406f0af8a7b68bbb0a7a8ccedb61c4f46ddc513cf4ae31bffdaa1f7630ba9cb";

    public static readonly string[] OptiLibraries =
    {
        "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "libxell.dll", "amd_fidelityfx_vk.dll",
        "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll",
        @"D3D12_OptiScaler\D3D12Core.dll",
    };

    // RenoDX add-on pins: (zip url, zip hash, file inside, file hash, cache folder name).
    private static readonly (string Url, string ZipSha, string Inner, string InnerSha, string Cache) ConsumerPin =
        ("https://github.com/RankFTW/rhi-repo/releases/download/renodx-dlss5-10.8.0/renodx-dlss5_10.8.0.zip",
         "faba7814fa90b6b21786c98a45732dbe80f01fa36745c46324472e14ac0f440d",
         "renodx-dlss5.addon64", "79871879324053d49931a9f8c1865fcb83e8146df4a56b146d7a8a14cc7e70fe", "renodx-dlss5-10.8.0");

    private static readonly (string Url, string ZipSha, string Inner, string InnerSha, string Cache) MultipassPin =
        ("https://github.com/RankFTW/rhi-repo/releases/download/renodx-dlss-SF-26.1016.1304/renodx-dlss_SF_26.1016.1304.zip",
         "bc4ec58d31565f6919c4e0c1aad8d8ea6443199e12b54f9e019b2ffd8afaaabf",
         "renodx-dlss.addon64", "a8fd34b531fd22c3dbd008ed8fd3cbfcca2fb451729e19cd1fc2e4c85b5068d4", "renodx-dlss-SF-26.1016.1304");

    private const string FeederUrl = "https://github.com/jlrouzies-fr/DLSS5-Feeder/releases/download/v1.19.0/DLSS5-Feeder-1.19.0.zip";
    private const string FeederSha = "9cc19e28f8fb4e216ea61621fdf6443e207e338ec639979ab335503c75f48f36";
    private const string FeederFolder = "DLSS5-Feeder-1.19.0";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Master/0.1");
        return c;
    }

    private readonly AppSettings _settings;

    public Components(AppSettings settings) => _settings = settings;

    /// <summary>Runtimes that ship inside the installer (NVIDIA, ReShade add-on build, RenoDX add-ons, DLSS5-Feeder).</summary>
    public static string BundledPayload { get; } = Path.Combine(AppContext.BaseDirectory, "payload");

    // ------------------------------------------------------------------ NVIDIA runtime

    public string? RuntimeDir
    {
        get
        {
            var candidates = new[] { _settings.RuntimeFolder, Path.Combine(BundledPayload, "runtime"), Path.Combine(AppPaths.Components, "runtime") };
            foreach (var dir in candidates)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "nvngx_dlss*.dll").Any()) return Path.GetFullPath(dir);
                }
                catch { }
            }
            return null;
        }
    }

    public Dictionary<string, string> RuntimeFiles()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = RuntimeDir;
        if (dir is null) return map;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.dll")) map[Path.GetFileName(f)] = f;
        }
        catch { }
        return map;
    }

    public string? NeuralRuntime => RuntimeFiles().GetValueOrDefault("nvngx_dlssnr.dll");

    // ------------------------------------------------------------------ ReShade

    public string? ReShadeDll(int bitness)
    {
        var name = bitness == 32 ? "ReShade32.dll" : "ReShade64.dll";
        static string? Usable(string? f) => f is not null && File.Exists(f) && Scanner.HasAddonSupport(f) ? f : null;

        var src = _settings.ReShadeSource;
        if (!string.IsNullOrWhiteSpace(src))
        {
            if (Directory.Exists(src) && Usable(Path.Combine(src, name)) is { } fromFolder) return fromFolder;
            if (File.Exists(src) && src.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Usable(ExtractFromReShadeSetup(src, name)) is { } fromSetup)
                return fromSetup;
        }
        if (Usable(Path.Combine(BundledPayload, "reshade", name)) is { } bundled) return bundled;
        try
        {
            var cache = Path.Combine(AppPaths.Components, "reshade");
            if (Directory.Exists(cache))
                foreach (var f in Directory.EnumerateFiles(cache, name, SearchOption.AllDirectories))
                    if (Usable(f) is { } c) return c;
        }
        catch { }
        return null;
    }

    public static string? ExtractFromReShadeSetup(string setupExe, string entryName)
    {
        try
        {
            var outDir = Path.Combine(AppPaths.Components, "reshade", Path.GetFileNameWithoutExtension(setupExe));
            var wanted = Path.Combine(outDir, entryName);
            if (File.Exists(wanted)) return wanted;

            byte[] archive;
            using (var fs = new FileStream(setupExe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // The zip's end-of-central-directory record sits within the last 64 KB (+22 bytes) of the file.
                int tailLen = (int)Math.Min(fs.Length, 65536 + 22);
                var tail = new byte[tailLen];
                fs.Position = fs.Length - tailLen;
                fs.ReadExactly(tail);
                int eocd = -1;
                for (int i = tailLen - 22; i >= 0; i--)
                    if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06) { eocd = i; break; }
                if (eocd < 0) throw new InvalidDataException("no zip archive found");
                long cdSize = BitConverter.ToUInt32(tail, eocd + 12);
                long cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
                long eocdAbs = fs.Length - tailLen + eocd;
                long start = eocdAbs - cdSize - cdOffset;
                if (start < 0 || start >= fs.Length) throw new InvalidDataException("zip offsets out of range");
                // Copy the embedded archive so its internal offsets are relative to position 0.
                archive = new byte[fs.Length - start];
                fs.Position = start;
                fs.ReadExactly(archive);
            }

            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            Directory.CreateDirectory(outDir);
            foreach (var entry in zip.Entries)
            {
                if (entry.Name.Length == 0 || !entry.Name.StartsWith("ReShade", StringComparison.OrdinalIgnoreCase)) continue;
                entry.ExtractToFile(Path.Combine(outDir, entry.Name), true);
            }
            if (File.Exists(wanted)) return wanted;
            AppPaths.Log($"ReShade setup {setupExe} does not contain {entryName}.");
            return null;
        }
        catch (Exception e)
        {
            AppPaths.Log($"Could not extract {entryName} from {setupExe}: {e.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ add-ons

    public async Task<string> MfgUnlockAsync(IProgress<string>? p, CancellationToken ct)
    {
        if (Updates.ActivePath("mfgunlock") is { } updated) return updated;
        var bundled = Path.Combine(AppContext.BaseDirectory, "addons", MfgAddon);
        if (File.Exists(bundled) && HashMatches(bundled, MfgSha256)) return bundled;
        var target = Path.Combine(AppPaths.Components, $"MFGAdaUnlock-{MfgVersion}", MfgAddon);
        await DownloadVerifiedAsync(MfgUrl, MfgSha256, target, p, ct);
        return target;
    }

    public Task<string> RenoDxConsumerAsync(IProgress<string>? p, CancellationToken ct) =>
        Updates.ActivePath("renodx-dlss5") is { } updated ? Task.FromResult(updated) : RenoDxAddonAsync(ConsumerPin, Path.Combine(BundledPayload, "renodx", "renodx-dlss5.addon64"), p, ct);

    public Task<string> MultipassAddonAsync(IProgress<string>? p, CancellationToken ct) =>
        Updates.ActivePath("renodx-dlss") is { } updated ? Task.FromResult(updated) : RenoDxAddonAsync(MultipassPin, Path.Combine(BundledPayload, "renodx", "renodx-dlss.addon64"), p, ct);

    private static async Task<string> RenoDxAddonAsync((string Url, string ZipSha, string Inner, string InnerSha, string Cache) pin,
                                                       string localCopy, IProgress<string>? p, CancellationToken ct)
    {
        var cached = Path.Combine(AppPaths.Components, pin.Cache, pin.Inner);
        if (File.Exists(cached) && HashMatches(cached, pin.InnerSha)) return cached;
        if (File.Exists(localCopy) && HashMatches(localCopy, pin.InnerSha)) return localCopy;

        var zip = Path.Combine(AppPaths.Components, Path.GetFileName(new Uri(pin.Url).LocalPath));
        await DownloadVerifiedAsync(pin.Url, pin.ZipSha, zip, p, ct);
        var extractDir = Path.Combine(AppPaths.Components, pin.Cache + "-extract");
        await Task.Run(() =>
        {
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zip, extractDir);
        }, ct);
        var found = Directory.EnumerateFiles(extractDir, pin.Inner, SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new InstallException($"{Path.GetFileName(zip)} does not contain {pin.Inner}. Nothing was installed.");
        if (!HashMatches(found, pin.InnerSha))
            throw new InstallException($"{pin.Inner} inside {Path.GetFileName(zip)} failed its SHA-256 check. Nothing was installed.");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.Copy(found, cached, true);
        try { Directory.Delete(extractDir, true); } catch { }
        return cached;
    }

    // ------------------------------------------------------------------ Feeder

    public sealed record FeederPayload(string Addon64, string Addon32, string Host64, string ShaderRoot, string? Verifier);

    public async Task<FeederPayload> FeederAsync(IProgress<string>? p, CancellationToken ct)
    {
        if (Updates.ActivePath("feeder") is { } updated && FindFeederAny(updated) is { } fromUpdate) return fromUpdate;
        if (FindFeeder(Path.Combine(BundledPayload, "feeder"), out _) is { } local) return local;
        var dir = Path.Combine(AppPaths.Components, FeederFolder);
        if (FindFeeder(dir, out _) is { } cached) return cached;

        var zip = Path.Combine(AppPaths.Components, FeederFolder + ".zip");
        await DownloadVerifiedAsync(FeederUrl, FeederSha, zip, p, ct);
        p?.Report("Extracting DLSS5-Feeder…");
        await Task.Run(() =>
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            ZipFile.ExtractToDirectory(zip, dir);
        }, ct);
        return FindFeeder(dir, out var why)
               ?? throw new InstallException($"The DLSS5-Feeder package is incomplete: {why}. Nothing was installed.");
    }

    /// <summary>A complete, hash-verified Feeder payload under <paramref name="root"/>, or null with the reason.</summary>
    /// <summary>A complete Feeder payload without the pinned-hash requirement (for a verified update package).</summary>
    public static FeederPayload? FindFeederAny(string root) => FindFeeder(root, out _, pinned: false);

    private static FeederPayload? FindFeeder(string root, out string why, bool pinned = true)
    {
        why = "folder not found";
        if (!Directory.Exists(root)) return null;
        List<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList(); }
        catch (Exception e) { why = e.Message; return null; }

        var picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, sha) in FeederHashes)
        {
            var hit = files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase) && (!pinned || HashMatches(f, sha)));
            if (hit is null) { why = $"{name} is missing or does not match its pinned SHA-256"; return null; }
            picked[name] = hit;
        }
        var shaders = Path.GetDirectoryName(picked["DLSS5_Feed.fx"])!;
        var missing = new[] { "vort_Motion.fx", "ReShade.fxh" }.Where(n => !File.Exists(Path.Combine(shaders, n))).ToList();
        if (missing.Count > 0) { why = $"the shader folder {shaders} lacks {string.Join(" and ", missing)}"; return null; }
        var shaderRoot = Path.GetDirectoryName(shaders)!;
        var verifier = files.FirstOrDefault(f => Path.GetFileName(f).Equals("Verify-DLSS5Feeder.ps1", StringComparison.OrdinalIgnoreCase));
        why = "";
        return new FeederPayload(picked["dlss5-feed.addon64"], picked["dlss5-feed.addon32"], picked["dlss5-feed-host64.exe"], shaderRoot, verifier);
    }

    // ------------------------------------------------------------------ OptiScaler

    public async Task<string> OptiScalerAsync(OptiBuild build, IProgress<string>? p, CancellationToken ct)
    {
        if (build.Url is null || build.Sha256 is null)
            throw new InstallException($"The {build.Label} package has no pinned download. Nothing was installed.");

        // The recommended build ships in payload\optiscaler; the others are downloaded. The bundled name has no dots
        // besides ".zip", because the build's resource indexer reads dotted name parts as qualifiers ("…v0.8.3…").
        var zip = Path.Combine(BundledPayload, "optiscaler", BundledOptiZipName(build));
        if (!File.Exists(zip) || !HashMatches(zip, build.Sha256))
        {
            zip = Path.Combine(AppPaths.Components, $"OptiScaler-{build.Id}.zip");
            await DownloadVerifiedAsync(build.Url, build.Sha256, zip, p, ct);
        }
        var dir = Path.Combine(AppPaths.Components, $"OptiScaler-{build.Id}");
        p?.Report("Extracting OptiScaler…");
        await Task.Run(() =>
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            ZipFile.ExtractToDirectory(zip, dir);
        }, ct);

        if (build.LicenseUrl is not null && build.LicenseSha256 is not null)
        {
            // Keep a verified copy outside the (re-extracted) folder so it is not downloaded every time.
            var cachedLicense = Path.Combine(AppPaths.Components, $"OptiScaler-{build.Id}-LICENSE.txt");
            await DownloadVerifiedAsync(build.LicenseUrl, build.LicenseSha256, cachedLicense, p, ct);
            File.Copy(cachedLicense, Path.Combine(dir, "OptiScaler-GPL-3.0.txt"), true);
        }

        var dll = Directory.EnumerateFiles(dir, "OptiScaler.dll", SearchOption.AllDirectories)
                      .OrderBy(f => f.Length).FirstOrDefault()
                  ?? throw new InstallException($"The {build.Label} package does not contain OptiScaler.dll. Nothing was installed.");
        if (Pe.GetBitness(dll) != 64)
            throw new InstallException($"OptiScaler.dll in the {build.Label} package is not a 64-bit DLL. Nothing was installed.");
        return Path.GetDirectoryName(dll)!;
    }

    public static string BundledOptiZipName(OptiBuild build) => $"OptiScaler-{build.Id.Replace('.', '_')}.zip";

    // ------------------------------------------------------------------ hashing / downloading

    public static string Sha256(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static bool HashMatches(string file, string sha)
    {
        try { return Sha256(file).Equals(sha, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static async Task DownloadVerifiedAsync(string url, string sha256, string file, IProgress<string>? p, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        if (File.Exists(file) && HashMatches(file, sha256)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var part = file + ".part";
        try
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                var buffer = new byte[1 << 16];
                long got = 0;
                int lastPercent = -1;
                p?.Report($"Downloading {name}…");
                for (int n; (n = await input.ReadAsync(buffer, ct)) > 0;)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    got += n;
                    if (total is > 0)
                    {
                        int percent = (int)(got * 100 / total.Value);
                        if (percent != lastPercent) { lastPercent = percent; p?.Report($"Downloading {name}… {percent}%"); }
                    }
                }
            }
        }
        catch (HttpRequestException e)
        {
            TryDelete(part);
            throw new InstallException($"Could not download {name}: {e.Message}. Nothing was installed.");
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        var actual = Sha256(part);
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            throw new InstallException($"{name} failed its SHA-256 check (expected {sha256}, got {actual}). Nothing was installed.");
        }
        File.Move(part, file, true);
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }
}
