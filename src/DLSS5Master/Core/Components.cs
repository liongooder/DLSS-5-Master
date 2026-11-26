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

    internal const string BundledOptiVersion = "0.9.96";
    internal const string BundledOptiBuildId = BundledOptiVersion + "-shyvortex";
    internal const string FeederLiveOptiLibrarySha256 = "936a1f03ce549d72b887d2a4487702299686c03e08ba08f28eaf802a6476e2c9";
    internal static readonly OptiBuild FeederLiveOptiScaler =
        new("0.8.93-feeder-live", "Feeder OptiScaler NR 0.8.93 (D3D12 backend)",
            "https://github.com/jlrouzies-fr/OptiScaler-DLSSNR-PreSR-Multipass/releases/download/v0.8.93/OptiScaler-NR-v0.8.93.zip",
            "8003065d88813b342049b869e8c894f8771c58f3d1a630ddf42fabd5c9d2f5fc", null, null, false, true, WholeBackend: true);
    internal static readonly IReadOnlyList<string> PreviousOptiBuildIds =
        ["0.9.96", "0.9.95-shyvortex", "0.9.95", "0.9.91-shyvortex", "0.9.91", "0.9.90-shyvortex"];

    public static readonly OptiBuild[] OptiBuilds =
    {
        new(BundledOptiBuildId, "OptiScaler NR " + BundledOptiVersion + " (recommended)",
            "https://github.com/ShyVortex/OptiScaler-NR-PreSR-MFG/releases/download/v" + BundledOptiVersion + "/OptiScaler-NR-v" + BundledOptiVersion + ".zip",
            "bc100c6e7092d569da7117e1861ed13024e4aa975a6134f0c39588975cb16381", null, null, false, true, WholeBackend: true),
        new("0.8.3-rtx40-mfg", "OptiScaler NR 0.8.3 · RTX 40 MFG (older)",
            "https://github.com/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass/releases/download/v0.8.3/OptiScaler-NR-v0.8.3-rtx40-mfg.zip",
            "aac7ea64d80604a5b79f686043ba28fbefefaf98818e861a377b78122cb24448", null, null, false, true),
    };

    // Keep profiles from the earlier bundles and accepted updates in this release line.
    internal static string? CurrentOptiBuildId(string? id) =>
        id is not null && PreviousOptiBuildIds.Contains(id) ? BundledOptiBuildId : id;

    public static readonly Dictionary<string, string> FeederHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dlss5-feed.addon32"] = "7ec337e0023652655fbf0f40f815bc1b4cd95eec43e1216a681a9ac0bcef2793",
        ["dlss5-feed.addon64"] = "d2b940c7617f8bc418ee0b02d3aed28cfd58e44e6095f0f862b6ff96a4388ffa",
        ["dlss5-feed-host64.exe"] = "7d360bf6d7b21d2d180346a40d74eafea45581802374225e6d30737fb97bc48f",
        ["DLSS5_Feed.fx"] = "72f537693374f5d2f63900cccb1cf48dd067dde3ef982c94086c8801448d5f6d",
    };

    public const string MfgAddon = "renodx-mfgunlock.addon64";
    public const string MfgVersion = "1.12.0";
    public const string MfgUrl = "https://github.com/mavismmg/MFGAdaUnlock-RenoDx/releases/download/" + MfgVersion + "/" + MfgAddon;
    public const string MfgSha256 = "f7cc86a84c05436409a1b7a59499ffb9d8d5cec51ed6fe0e8bb4e55885e62ebf";

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
        ("https://github.com/RankFTW/rhi-repo/releases/download/renodx-dlss-SF-26.1025.0009/renodx-dlss_SF_26.1025.0009.zip",
         "9851d9fb959076d94f9877d34dc7cee82ce6591ce0df9a54f9d2a92ccaaa6b9a",
         "renodx-dlss.addon64", "dd5ed7ba6773a6168390f3a53e757f09b563ecdf1a9f4fcdec6dd3129d93bace", "renodx-dlss-SF-26.1025.0009");

    private const string FeederUrl = "https://github.com/jlrouzies-fr/DLSS5-Feeder/releases/download/v2.0.0/DLSS5-Feeder-2.0.0.zip";
    private const string FeederSha = "fa41fe2c5d3da803688775b802dc373a922daab0e721c8154a17066fcdd62584";
    private const string FeederFolder = "DLSS5-Feeder-2.0.0";

    public const string UpliftVersion = "2.0.0";
    private const string UpliftZip = "GITC-Uplift-" + UpliftVersion + ".zip";
    private const string UpliftUrl = "https://github.com/ghostinthecamera/GITC-Uplift/releases/download/v" + UpliftVersion + "/" + UpliftZip;
    private const string UpliftSha256 = "1eac38a62339e993cc98fd4f4773dd8a327b098b47c4d21fc070a9d3d6aac255";
    private static readonly string[] UpliftFiles =
    {
        "Addons/gitc-uplift.addon64",
        "Addons/gitc-uplift.addon32",
        "Addons/gitc-uplift-helper64.exe",
        "Shaders/Uplift.fx",
        "Shaders/UpliftMask.fx",
    };

    public sealed class UpliftPayload : IDisposable
    {
        private readonly string _root;

        internal UpliftPayload(string root) => _root = root;

        public string AddonDirectory => Path.Combine(_root, "Addons");
        public string ShaderDirectory => Path.Combine(_root, "Shaders");

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public async Task<UpliftPayload> UpliftAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var zip = ComponentHealth.IncludedFile("payload/gitc-uplift/" + UpliftZip);
        if (!HashMatches(zip, UpliftSha256))
        {
            zip = Path.Combine(AppPaths.Components, UpliftZip);
            await DownloadVerifiedAsync(UpliftUrl, UpliftSha256, zip, progress, ct);
        }

        progress?.Report("Preparing GITC Uplift…");
        return await Task.Run(() => ExtractUplift(zip, ct), ct);
    }

    private static UpliftPayload ExtractUplift(string zip, CancellationToken ct)
    {
        using var input = File.OpenRead(zip);
        if (!Convert.ToHexStringLower(SHA256.HashData(input)).Equals(UpliftSha256, StringComparison.Ordinal))
            throw new InstallException("The GITC Uplift release did not match its published SHA-256.");

        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var stage = Directory.CreateTempSubdirectory("dlss5master-uplift-").FullName;
        var payload = new UpliftPayload(stage);
        try
        {
            foreach (var name in UpliftFiles)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(name) ?? throw new InstallException($"The GITC Uplift ZIP is missing {name}.");
                var target = Path.Combine(stage, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }
            return payload;
        }
        catch
        {
            payload.Dispose();
            throw;
        }
    }

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
    private const string NeuralRuntimeFile = "nvngx_dlssnr.dll";
    private const string NvidiaNeuralRuntimePath = "payload/nvidia-neural/" + NeuralRuntimeFile;
    private const string NvidiaNeuralRuntimeSha256 = "2bc19bc09a7b89dd44f73e2ac0108fcc586e2154bdd0830f242f4f040e28789f";

    internal static bool IsBundledNvidiaModel(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length == 266424944 &&
                ComponentHealth.Matches(path, NvidiaNeuralRuntimeSha256);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }


    public string? RuntimeDir
    {
        get
        {
            var candidates = new[] { _settings.RuntimeFolder, ComponentHealth.IncludedDirectory("runtime", "payload/runtime"), Path.Combine(AppPaths.Components, "runtime") };
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

    private bool IsCustomRuntime(string? dir) =>
        !string.IsNullOrWhiteSpace(_settings.RuntimeFolder) && Directory.Exists(_settings.RuntimeFolder) && dir is not null &&
        string.Equals(Path.GetFullPath(_settings.RuntimeFolder).TrimEnd('\\', '/'), dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public Dictionary<string, string> RuntimeFiles(bool includeNvidiaModel = true) =>
        RuntimeFiles(out _, includeNvidiaModel);

    internal Dictionary<string, string> RuntimeFiles(out bool preferNeuralModel, bool includeNvidiaModel = true)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = RuntimeDir;
        var preference = _settings.NeuralModel;
        preferNeuralModel = false;
        try
        {
            if (dir is not null)
                foreach (var f in Directory.EnumerateFiles(dir, "*.dll")) map[Path.GetFileName(f)] = f;
        }
        catch { }
        // An explicitly selected, usable runtime folder keeps control of its files.
        if (IsCustomRuntime(dir)) return map;
        RuntimeUpdates.ApplySelections(map);
        if (!includeNvidiaModel) return map;

        preferNeuralModel = preference is NeuralModelChoice.Nvidia or NeuralModelChoice.Community;
        if (NeuralRuntimeSelection.Resolve(preference) == NeuralModelChoice.Nvidia)
        {
            var signed = ComponentHealth.IncludedFile(NvidiaNeuralRuntimePath);
            if (IsBundledNvidiaModel(signed))
            {
                map[NeuralRuntimeFile] = signed;
                preferNeuralModel = true;
            }
            else if (preference == NeuralModelChoice.Nvidia)
            {
                map.Remove(NeuralRuntimeFile);
            }
        }
        return map;
    }

    public string? NeuralRuntime => RuntimeFiles().GetValueOrDefault(NeuralRuntimeFile);

    public string NeuralModelStatus
    {
        get
        {
            if (IsCustomRuntime(RuntimeDir)) return "Using the custom runtime folder.";
            var preference = _settings.NeuralModel;
            if (NeuralRuntimeSelection.Resolve(preference) != NeuralModelChoice.Nvidia)
                return "Selected model: Community.";
            if (IsBundledNvidiaModel(ComponentHealth.IncludedFile(NvidiaNeuralRuntimePath)))
                return "Selected model: NVIDIA signed (RTX 50 series).";
            return preference == NeuralModelChoice.Nvidia
                ? "NVIDIA signed model is missing or changed. Repair the included files."
                : "NVIDIA signed model is unavailable. Automatic selected Community.";
        }
    }

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
        if (Usable(ComponentHealth.IncludedFile("payload/reshade/" + name)) is { } bundled) return bundled;
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
        var bundled = ComponentHealth.IncludedFile("addons/" + MfgAddon);
        if (File.Exists(bundled) && HashMatches(bundled, MfgSha256)) return bundled;
        var target = Path.Combine(AppPaths.Components, $"MFGAdaUnlock-{MfgVersion}", MfgAddon);
        await DownloadVerifiedAsync(MfgUrl, MfgSha256, target, p, ct);
        return target;
    }

    public Task<string> RenoDxConsumerAsync(IProgress<string>? p, CancellationToken ct, bool requirePinned = false) =>
        !requirePinned && Updates.ActivePath("renodx-dlss5") is { } updated ? Task.FromResult(updated) : RenoDxAddonAsync(ConsumerPin, ComponentHealth.IncludedFile("payload/renodx/renodx-dlss5.addon64"), p, ct);

    public Task<string> MultipassAddonAsync(IProgress<string>? p, CancellationToken ct) =>
        Updates.ActivePath("renodx-dlss") is { } updated ? Task.FromResult(updated) : RenoDxAddonAsync(MultipassPin, ComponentHealth.IncludedFile("payload/renodx/renodx-dlss.addon64"), p, ct);

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

    public sealed record FeederPayload(string Addon64, string Addon32, string Host64, string ShaderRoot, string? Verifier)
    {
        public string? InGameControls { get; init; }
        public string? DesktopPanel { get; init; }
        public string? LiveOptiScalerLibrary { get; init; }
    }

    internal bool UseFeederLiveControls => _settings.FeederLiveControls;

    public async Task<FeederPayload> FeederAsync(IProgress<string>? p, CancellationToken ct, bool requireDlss5NrBridge = false)
    {
        // The older Feeder ZIPs can still feed DLAA but do not recognize the DLSS5 NR add-on.
        // Use the qualified bundle for this backend so a previous ZIP selection cannot mask it.
        if (requireDlss5NrBridge)
        {
            if (IncludedFeeder() is { } qualified) return qualified;
            if (_settings.FeederFolder is { Length: > 0 } folder &&
                FeederZipImport.ReadSelection(folder).Payload is { } imported &&
                HashMatches(imported.Addon64, FeederHashes["dlss5-feed.addon64"]))
                return imported;
            throw new InstallException("DLSS5 NR requires the compatible Feeder bundle. Repair the DLSS 5 Master installation or import the matching Feeder ZIP.");
        }
        if (_settings.FeederFolder is { Length: > 0 } selected)
            return FeederZipImport.ReadSelection(selected).Payload
                   ?? throw new InstallException("The selected DLSS5 Feeder ZIP is no longer available. Choose the ZIP again or select Use original in Settings.");
        if (Updates.ActivePath("feeder") is { } updated && FindFeederAny(updated) is { } fromUpdate) return fromUpdate;
        if (IncludedOriginalFeeder() is { } local) return local;
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

    internal const string FeederControlPanel = "dlss5-feed-controls.addon64";

    internal static FeederPayload FeederWithLiveControls()
    {
        var original = IncludedOriginalFeeder()
            ?? throw new InstallException("Repair DLSS 5 Master to restore the included Feeder.");
        const string panel = "payload/feeder/controls/" + FeederControlPanel;
        if (!ComponentHealth.Groups.Single(group => group.Id == "feeder").Files.Any(pin => pin.RelativePath == panel))
            throw new InstallException("The Feeder live controls are missing from this installer.");
        return original with { InGameControls = ComponentHealth.IncludedFile(panel) };
    }

    private static FeederPayload? IncludedOriginalFeeder()
    {
        if (IncludedFeeder() is not { } local) return null;
        const string original = "payload/feeder-original/";
        return local with
        {
            Addon64 = ComponentHealth.IncludedFile(original + "dlss5-feed.addon64"),
            Addon32 = ComponentHealth.IncludedFile(original + "dlss5-feed.addon32"),
            Host64 = ComponentHealth.IncludedFile(original + "dlss5-feed-host64.exe"),
        };
    }

    private static FeederPayload? IncludedFeeder()
    {
        var group = ComponentHealth.Groups.FirstOrDefault(candidate => candidate.Id == "feeder");
        return group is not null && ComponentHealth.Inspect(group).Ready
            ? FindFeederAny(ComponentHealth.IncludedDirectory("feeder", "payload/feeder")) : null;
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
        var zip = ComponentHealth.IncludedFile("payload/optiscaler/" + BundledOptiZipName(build));
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

    internal static bool HashMatches(string file, string sha)
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
