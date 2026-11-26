using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DLSS5Master.Core;

public enum Backend { ReShade, OptiScaler }

public enum Route { Native, Multipass, Feeder, OptiScaler, NeuralMaster, NrAddon, MfgOnly, Uplift }
public enum FeederBackend { Automatic, RenoDx, Lecram }

public sealed class InstallRequest
{
    public required string GameDir { get; init; }
    public required string ExePath { get; init; }
    public required RenderApi Api { get; init; }
    public required int Bitness { get; init; }
    public required Route Route { get; init; }
    public FeederBackend FeederBackend { get; init; }
    public OptiBuild? OptiBuild { get; init; }
    public string OptiHook { get; init; } = "dxgi.dll";
    public string MenuKey { get; init; } = "0x2D";
    public string ReShadeProxy { get; init; } = "dxgi.dll";
    public bool MfgUnlock { get; init; }

    /// <summary>Have ReShade load the DLSS 5 add-ons as soon as it starts (ReShade.ini [ADDON] LoadFromDllMain).</summary>
    public bool LoadAddonsEarly { get; init; }
    public bool AntiCheatAcknowledged { get; init; }

    /// <summary>The player's local DLSS5 NR copy, used by the standalone route and the Lecram Feeder backend.</summary>
    public string? NrAddonSource { get; init; }

    // Offline fixtures can supply registered layer candidates without changing the Windows registry.
    internal IReadOnlyList<string>? VulkanLayerManifests { get; init; }
}

public sealed record RouteOption(Route Route, string Label, string Description, string? Unavailable);

/// <summary>A settings file the player changed in the game, kept across a reinstall: its path inside the game folder and its bytes.</summary>
internal sealed record KeptSetting(string Rel, byte[] Bytes);

public static partial class Installers
{
    public static readonly string[] OptiHookNames = { "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll", "OptiScaler.asi" };

    public static readonly (string Name, string Code)[] MenuKeys =
    {
        ("Insert", "0x2D"), ("Home", "0x24"), ("End", "0x23"), ("Delete", "0x2E"), ("Page Up", "0x21"), ("Page Down", "0x22"),
        ("F1", "0x70"), ("F2", "0x71"), ("F3", "0x72"), ("F4", "0x73"), ("F5", "0x74"), ("F6", "0x75"),
        ("F7", "0x76"), ("F8", "0x77"), ("F9", "0x78"), ("F10", "0x79"), ("F11", "0x7A"), ("F12", "0x7B"),
        ("Backspace", "0x08"), ("Pause", "0x13"), ("Scroll Lock", "0x91"),
    };

    private const string NeuralDll = "nvngx_dlssnr.dll";
    private const string DlssDll = "nvngx_dlss.dll";
    private const string FrameGenDll = "nvngx_dlssg.dll";
    private const string ConsumerAddon = "renodx-dlss5.addon64";
    private const string FeederHost64 = "dlss5-feed-host64.exe";
    private const string MultipassAddon = "renodx-dlss.addon64";
    private const string ForwarderDll = "nvngx.dll_dlssnr.dll";
    private const string UpliftRuntimeFolder = "GITC-Uplift";

    // ------------------------------------------------------------------ keys and labels

    public static string ApiKey(RenderApi api) => api switch
    {
        RenderApi.DX11 or RenderApi.DX12 => "dxgi",
        RenderApi.DX9 => "d3d9",
        RenderApi.DX8 => "d3d8",
        RenderApi.DX10 => "d3d10",
        RenderApi.DirectDraw => "ddraw",
        RenderApi.Vulkan => "vulkan",
        RenderApi.OpenGL => "opengl",
        _ => "unknown",
    };

    public static string RouteKey(Route r) => r switch
    {
        Route.Native => "native",
        Route.Multipass => "multipass",
        Route.Feeder => "feeder",
        Route.NeuralMaster => "neuralmaster",
        Route.NrAddon => "nraddon",
        Route.MfgOnly => "mfgonly",
        Route.Uplift => "uplift",
        _ => "optiscaler",
    };

    /// <summary>
    /// What a game's anti-cheat means for a setup. Every route puts an unsigned DLL into the game (ReShade's add-on build,
    /// or OptiScaler), which anti-cheat makers have not cleared (ReShade's own release notes), so anti-cheat often refuses
    /// to load it at all; EA Javelin refuses every unsigned DLL.
    /// </summary>
    public static string AntiCheatWarning(string antiCheat) =>
        $"This game has anti-cheat ({antiCheat}). It often stops injected DLLs such as ReShade and OptiScaler from loading, " +
        "so DLSS 5 Master may not work here, and injecting them can get your account banned, especially online.";

    public static string RouteLabel(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "none";
        return key.ToLowerInvariant() switch
        {
            "native" => "ReShade · Native DLSS-NR",
            "multipass" or "renodx" => "ReShade · RenoDX DLSS Tool (multipass)",
            "feeder" => "ReShade · DLSS5 Feeder",
            "optiscaler" => "OptiScaler",
            "neuralmaster" => "NeuralMaster · retired prototype",
            "nraddon" => "ReShade · DLSS5 NR (supplied separately)",
            "mfgonly" => "MFG unlock only",
            "uplift" => "ReShade · GITC Uplift",
            "swap" => "DLL swap only",
            _ => key,
        };
    }

    internal static string InstalledRouteLabel(Manifest? manifest) => manifest switch
    {
        { Route: "feeder", FeederBackend: FeederBackend.Lecram } => "ReShade · DLSS5 Feeder · DLSS5 NR",
        { Route: "feeder", OptiScalerBuild: not null } => "ReShade · DLSS5 Feeder · OptiScaler",
        { Route: "feeder" } => "ReShade · DLSS5 Feeder · RenoDX",
        _ => RouteLabel(manifest?.Route),
    };

    internal static string? FeederLecramUnavailable(RenderApi api, int bits, string? source)
    {
        if (bits != 64 || api is not (RenderApi.DX12 or RenderApi.Vulkan))
            return "DLSS5 NR with Feeder needs a 64-bit DirectX 12 or Vulkan game.";
        return source is null ? "Choose your local DLSS5 NR folder in Settings." : null;
    }

    // ------------------------------------------------------------------ route availability

    public static string? MfgUnavailable(RenderApi api, int bitness, bool hasFrameGen)
    {
        if (bitness != 64) return "The MFG unlock add-on only works with 64-bit games.";
        if (api != RenderApi.DX12) return "The MFG unlock add-on needs a DirectX 12 game (its Vulkan support is experimental and not offered).";
        if (!hasFrameGen) return "This game has no DLSS Frame Generation (nvngx_dlssg.dll), which the MFG unlock builds on.";
        return null;
    }

    internal static string? MfgOnlyConflict(string gameDir, string exePath, Manifest? manifest)
    {
        if (manifest is null) return null;
        if (manifest.Route is not ("native" or "multipass" or "feeder" or "nraddon" or "mfgonly" or "swap" or "uplift"))
            return "MFG unlock needs ReShade add-on support and cannot be added to this setup.";
        try
        {
            var target = Path.GetFullPath(exePath);
            if (!target.Equals(Journal.SafePath(gameDir, manifest.Exe), StringComparison.OrdinalIgnoreCase))
                return "Select the executable used by the existing setup to install MFG unlock.";
            var exeDir = Path.GetDirectoryName(target)!;
            if (manifest.Route == "uplift" &&
                !UpliftAddonDirectory(gameDir, exeDir).Equals(exeDir, StringComparison.OrdinalIgnoreCase))
                return "MFG unlock needs ReShade's add-on folder to be beside this game's executable.";
            return null;
        }
        catch (InstallException ex) { return ex.Message; }
    }

    /// <summary>
    /// Why the Feeder would do nothing in this game, or null. On DirectX 12 it runs DLSS on the game's own device, so it
    /// switches itself off when the game loads DLSS of its own (Like a Dragon: Ishin!); on DirectX 11 it brings its own device.
    /// </summary>
    public static string? FeederStandsAside(RenderApi api, bool hasNativeDlss) =>
        api == RenderApi.DX12 && hasNativeDlss
            ? "The Feeder stands aside in DirectX 12 games that have DLSS of their own: it switches itself off. Use Native, or set Rendering API to DirectX 11 if the game can run on it."
            : null;

    /// <param name="nrCopy">The player has a copy of DLSS5 NR (<see cref="NrAddon.Source"/>): only then is its route offered.</param>
    public static List<RouteOption> RoutesFor(RenderApi api, int bitness, bool hasNativeDlss, bool hasFrameGen = false, bool nrCopy = false,
        string? addonName = null, string? vulkanNrUnavailable = ReShadeVulkanLayer.NotAvailable,
        string? vulkanFeederUnavailable = ReShadeVulkanLayer.FeederNotAvailable)
    {
        bool x64 = bitness == 64;
        const string need64 = "Needs a 64-bit game.";

        // Native works through the game's own DLSS calls (DirectX 11 runs as well as 12: Alone in the Dark, 99.9 % of frames).
        // A game without DLSS never calls it, so Native would sit idle there (Shadow of Mordor: 0 calls in 95,000 frames).
        string? native = !x64 ? need64
            : api is not (RenderApi.DX11 or RenderApi.DX12) ? "Needs a DirectX 11 or 12 game."
            : !hasNativeDlss ? "Works through the game's own DLSS, and this game has none: use the Feeder."
            : null;

        string? multipass = !x64 ? need64
            : api is RenderApi.DX9 or RenderApi.DX11 or RenderApi.DX12 ? null
            : "Needs a DirectX 9, 11 or 12 game.";

        string? feeder = api switch
        {
            RenderApi.DX11 or RenderApi.DX12 => null,
            RenderApi.Vulkan or RenderApi.OpenGL when hasNativeDlss => "The Feeder is offered for Vulkan and OpenGL games without built-in DLSS.",
            RenderApi.Vulkan or RenderApi.OpenGL when bitness is not (32 or 64) => "Needs a 32-bit or 64-bit game.",
            RenderApi.Vulkan => vulkanFeederUnavailable,
            RenderApi.OpenGL => null,
            RenderApi.DX9 or RenderApi.DX8 or RenderApi.DirectDraw => "This API needs the dgVoodoo wrapper for the Feeder; not supported yet.",
            _ => "Needs DirectX 11, DirectX 12, Vulkan or OpenGL.",
        };
        var feederText = "For games without DLSS: shaders compute motion vectors and feed them through ReShade to the neural pass.";
        if (api == RenderApi.Vulkan)
            feederText += " Uses the game's registered ReShade Vulkan layer. Turn off NVIDIA Smooth Motion for this game if it is enabled.";

        string? opti = !x64 ? need64
            : api is RenderApi.DX11 or RenderApi.DX12 or RenderApi.Vulkan ? null
            : "Needs a DirectX 11, DirectX 12 or Vulkan game.";

        var optiText = "Loads OptiScaler straight into the game, without ReShade, and runs DLSS neural rendering through it.";
        if (!hasNativeDlss)
            optiText += " The DLSS-NR builds rely on the game's own DLSS, which this game does not ship; use the custom (official) build for upscaling only.";
        var multipassText = "ReShade with the RenoDX DLSS Tool, which runs several neural passes. Streamline is added when the game has none of its own.";
        if (hasFrameGen) multipassText += " The game's DLSS Frame Generation keeps working.";
        var upliftUnavailable = bitness is not (32 or 64) ? "Needs a 32-bit or 64-bit game." : api switch
        {
            RenderApi.DX9 or RenderApi.DX10 or RenderApi.DX11 or RenderApi.DX12 or RenderApi.OpenGL => null,
            RenderApi.Vulkan => vulkanFeederUnavailable,
            _ => "Needs DirectX 9–12, Vulkan or OpenGL.",
        };

        var options = new List<RouteOption>
        {
            new(Route.Native, "Native DLSS-NR (RenoDX DLSS5)",
                "ReShade plus the RenoDX DLSS5 add-on and NVIDIA's neural runtime, working through the game's own DLSS with its real depth and motion. The best fit for DirectX 11 and 12 games that use DLSS.", native),
            new(Route.Multipass, "RenoDX DLSS Tool (multipass)", multipassText, multipass),
            new(Route.Feeder, "DLSS5 Feeder", feederText, feeder),
            new(Route.OptiScaler, "OptiScaler", optiText, opti),
            new(Route.Uplift, "GITC Uplift",
                "Neural rendering through ReShade on RTX graphics, with or without the game's own DLSS. In game, open the GITC Uplift tab in ReShade to enable it.", upliftUnavailable),
            new(Route.MfgOnly, "MFG unlock only",
                "Installs the MFG unlock with ReShade add-on support, without adding an NR route, neural model or Streamline. Requires a 64-bit DirectX 12 game with DLSS Frame Generation.",
                MfgUnavailable(api, bitness, hasFrameGen)),
            new(Route.NeuralMaster, "NeuralMaster",
                "Native game depth and motion → neural passes → Look/HDR. View development status in the NeuralMaster tab.",
                NeuralMasterDevelopmentPath.BuildStatus),
        };
        // Its own host runs it in 32-bit, Direct3D 9/10 and OpenGL games. On Vulkan use a compatible registered layer.
        if (nrCopy)
            options.Add(new(Route.NrAddon, AddonIdentity.SafeName(addonName),
                "Use your own copy of the supported DLSS5 NR add-on with ReShade and NVIDIA’s neural runtime. Select its folder in Settings. " +
                "The add-on is supplied separately and is not included or downloaded by DLSS 5 Master.",
                api switch
                {
                    RenderApi.DX9 or RenderApi.DX10 or RenderApi.DX11 or RenderApi.DX12 or RenderApi.OpenGL => null,
                    RenderApi.Vulkan => x64 ? vulkanNrUnavailable : ReShadeVulkanLayer.Need64Bit,
                    _ => "Needs Direct3D 9, 10, 11, 12, OpenGL or a compatible 64-bit Vulkan setup.",
                }));
        return options;
    }

    public static Route RecommendedRoute(RenderApi api, int bitness, bool hasNativeDlss)
    {
        var routes = RoutesFor(api, bitness, hasNativeDlss);
        bool Ok(Route r) => routes.Any(o => o.Route == r && o.Unavailable is null);
        if (hasNativeDlss && Ok(Route.Native)) return Route.Native;
        if (Ok(Route.Feeder)) return Route.Feeder;
        return routes.FirstOrDefault(o => o.Unavailable is null)?.Route ?? Route.Native;
    }

    // ------------------------------------------------------------------ install

    /// <summary>Everything a route needs, gathered before the game folder is touched.</summary>
    private sealed class Job
    {
        public required InstallRequest Req { get; init; }
        public required Session S { get; init; }
        public required string ExeDir { get; init; }
        public required Dictionary<string, string> Runtime { get; init; }
        public bool PreferNeuralModel { get; init; }
        public string? ReShade { get; init; }
        public string? ReShade64 { get; init; }
        public string? Consumer { get; init; }
        public string? Multipass { get; init; }
        public Components.FeederPayload? Feeder { get; init; }
        public Components.UpliftPayload? Uplift { get; init; }
        public string? OptiFolder { get; init; }
        public OptiBuild? Build { get; init; }
        public string? MfgFile { get; init; }
        public NativeMfgProviders? MfgProviders { get; init; }
        /// <summary>The player's copy of DLSS5 NR, staged (<see cref="NrAddon.Stage"/>).</summary>
        public string? NrAddon { get; init; }
        public required GameScan Scan { get; set; }
        public int Bits => Req.Bitness;
        public void Log(string line) => S.Log(line);
    }

    public static async Task InstallAsync(InstallRequest req, Components components, Action<string> log, IProgress<string>? progress, CancellationToken ct, Action<InstallPreview>? preview = null, bool previewOnly = false)
    {
        // Reject even stale callers before any folder checks, scans, downloads or writes.
        if (req.Route == Route.NeuralMaster)
            throw new InstallException(NeuralMasterDevelopmentPath.BuildStatus);
        var gameDir = Path.GetFullPath(req.GameDir).TrimEnd('\\', '/');
        var exePath = Path.GetFullPath(req.ExePath);
        var exeDir = Path.GetDirectoryName(exePath)!;
        // The install record's own lines reach the activity log by themselves (Session.Log); these from outside it go there too,
        // so a setup run without a page to show them (Set up several games) still leaves every line in the log.
        var gameTag = $"[{Path.GetFileName(gameDir)}] ";
        void Note(string line) { log(line); AppPaths.Log(gameTag + line); }

        // ---- 1. preconditions (nothing downloaded or changed yet)
        NeuralMasterProtection.Check(gameDir, exePath);
        bool feederLecram = req.Route == Route.Feeder && req.FeederBackend == FeederBackend.Lecram;
        bool liveFeederControls = CanUseFeederLiveControls(req) && components.UseFeederLiveControls;
        if (feederLecram && FeederLecramUnavailable(req.Api, req.Bitness, req.NrAddonSource) is { } feederBackendProblem)
            throw new InstallException(feederBackendProblem);
        bool vulkanNr = req.Route == Route.NrAddon && req.Api == RenderApi.Vulkan;
        bool vulkanLayer = req.Route is (Route.Feeder or Route.Uplift) && req.Api == RenderApi.Vulkan;
        if (vulkanNr && VulkanNrUnavailable(req, exePath) is { } layerProblem)
            throw new InstallException(layerProblem);
        if (vulkanLayer && ReShadeVulkanLayer.FeederUnavailableReason(exePath, req.VulkanLayerManifests) is { } vulkanProblem)
            throw new InstallException(vulkanProblem);
        if (!Journal.CanWrite(exeDir))
        {
            var hint = exeDir.Contains("Program Files", StringComparison.OrdinalIgnoreCase)
                ? " Games under Program Files need DLSS 5 Master to run as administrator."
                : "";
            throw new InstallException($"Cannot write to {exeDir}.{hint}");
        }
        // Another tool's install in this game: never overwrite it (both apps would then fight over the same files).
        if (File.Exists(Path.Combine(gameDir, "_DLSS5_Backup", "manifest.json")) || Scanner.OtherAppRecordRouteFor(gameDir) is not null)
            throw new InstallException("Another app has an install in this game. Remove it with that app first, then install with DLSS 5 Master.");
        if (NrAddon.SetupInstalled(exeDir)) throw new InstallException(NrAddon.SetupInstalledMessage(NrAddon.SetupName(exeDir)));
        AssertGameClosed(exePath);
        if (!previewOnly) ReleaseDetroitFeederHelper(gameDir, exePath, Note);
        if (!previewOnly && InstallTransaction.Pending(gameDir))
        {
            InstallTransaction.Recover(gameDir);
            Note("Recovered the previous setup after an interrupted operation.");
        }

        progress?.Report("Scanning the game…");
        var scan = await Task.Run(() => Scanner.Scan(gameDir, exePath, ct), ct);
        if (NeuralMasterProtection.Conflict(scan) is { } neuralMasterConflict)
            throw new InstallException(neuralMasterConflict);
        if (req.Route == Route.MfgOnly && MfgOnlyConflict(gameDir, exePath, scan.Manifest) is { } conflict)
            throw new InstallException(conflict);
        if (scan.AntiCheatName is { } antiCheat && !req.AntiCheatAcknowledged)
            throw new InstallException(AntiCheatWarning(antiCheat) + " Tick the acknowledgement to continue anyway.");

        if ((req.Route == Route.NrAddon || feederLecram) && !NrAddon.Has(req.NrAddonSource, gameDir, exeDir, Journal.ReadManifest(gameDir)))
            throw new InstallException($"Your copy of DLSS5 NR was not found{(req.NrAddonSource is null ? "" : " in " + req.NrAddonSource)}. " +
                "Unzip it into DLSS 5 Master's folder for it (Settings → Open folder), or pick its folder in Settings.");
        var option = RoutesFor(req.Api, req.Bitness, scan.HasNativeDlss, scan.HasFrameGen, nrCopy: req.Route == Route.NrAddon,
            vulkanNrUnavailable: vulkanNr ? null : ReShadeVulkanLayer.NotAvailable,
            vulkanFeederUnavailable: vulkanLayer ? null : ReShadeVulkanLayer.FeederNotAvailable).First(o => o.Route == req.Route);
        if (option.Unavailable is not null)
            throw new InstallException($"{option.Label} cannot be used here: {option.Unavailable}");

        bool wantMfg = req.Route == Route.MfgOnly || (req.MfgUnlock && req.Route != Route.OptiScaler);
        if (wantMfg && MfgUnavailable(req.Api, req.Bitness, scan.HasFrameGen) is { } mfgWhy)
            throw new InstallException(mfgWhy);
        if (req.Route == Route.Uplift && wantMfg &&
            !UpliftAddonDirectory(gameDir, exeDir).Equals(exeDir, StringComparison.OrdinalIgnoreCase))
            throw new InstallException("MFG unlock needs ReShade's add-on folder to be beside this game's executable.");

        bool reshadeRoute = req.Route != Route.OptiScaler;
        var build = req.OptiBuild ?? Components.OptiBuilds[0];
        var (runtime, preferNeuralModel) = await Task.Run(() =>
        {
            var files = components.RuntimeFiles(out var prefer, includeNvidiaModel: req.Route != Route.MfgOnly);
            return (files, prefer);
        }, ct);
        if (req.Route == Route.MfgOnly)
            runtime = runtime.Where(p => p.Key.Equals(FrameGenDll, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        // Local prerequisites first, so we do not download anything that could not be used.
        if (reshadeRoute && req.Route != Route.MfgOnly && !runtime.ContainsKey(NeuralDll))
            throw new InstallException($"The selected neural model ({NeuralDll}) is unavailable. Check the neural model in Settings.");
        if (req.Route == Route.OptiScaler && build.NeuralRendering && !runtime.ContainsKey(NeuralDll) &&
            (preferNeuralModel || !File.Exists(Path.Combine(exeDir, NeuralDll))))
            throw new InstallException($"This OptiScaler build needs a neural model ({NeuralDll}). Check the neural model in Settings.");
        if (req.Route == Route.Feeder && !runtime.ContainsKey(DlssDll))
            throw new InstallException($"The Feeder route needs {DlssDll} in the NVIDIA runtime folder. Pick it in Settings.");
        string? reshade = null, reshade64 = null;
        if (reshadeRoute)
        {
            if (!vulkanNr && !vulkanLayer)
                reshade = components.ReShadeDll(req.Bitness)
                          ?? throw new InstallException($"No {req.Bitness}-bit ReShade with add-on support was found. Pick a ReShade_Setup_*_Addon.exe in Settings.");
            if (req.Route == Route.Feeder && req.Bitness != 64)
                reshade64 = components.ReShadeDll(64)
                            ?? throw new InstallException("The Feeder's 64-bit helper needs a 64-bit ReShade with add-on support. Pick a ReShade_Setup_*_Addon.exe in Settings.");
        }
        if (req.Route == Route.OptiScaler && build.NeuralRendering && !scan.HasNativeDlss)
            Note("Warning: this game has no DLSS of its own; DLSS-NR OptiScaler builds may not activate.");
        if (req.Route == Route.Uplift && Pe.GetBitness(runtime[NeuralDll]) != 64)
            throw new InstallException("GITC Uplift needs the 64-bit NVIDIA neural runtime, including in 32-bit games.");

        // ---- 2. fetch everything before touching the game
        string? consumer = null, multipass = null, opti = null, mfg = null;
        Components.FeederPayload? feeder = null;
        using var uplift = req.Route == Route.Uplift ? await components.UpliftAsync(progress, ct) : null;
        switch (req.Route)
        {
            case Route.Native:
                consumer = await components.RenoDxConsumerAsync(progress, ct);
                break;
            case Route.Multipass:
                multipass = await components.MultipassAddonAsync(progress, ct);
                break;
            case Route.Feeder:
                feeder = liveFeederControls
                    ? Components.FeederWithLiveControls()
                    : await components.FeederAsync(progress, ct, requireDlss5NrBridge: feederLecram);
                if (!feederLecram)
                    consumer = await components.RenoDxConsumerAsync(progress, ct, requirePinned: liveFeederControls);
                break;
            case Route.OptiScaler:
                opti = await components.OptiScalerAsync(build, progress, ct);
                break;
        }
        if (wantMfg)
            // The selected updated or included component wins. Existing backend
            // mirrors are replacement targets, never a reason to select old bytes.
            mfg = await components.MfgUnlockAsync(progress, ct);
        // Capture before RestoreForReinstall: that restore may put older FG
        // files back, and RuntimeFiles() may contain only NR/SR, not an FG DLL.
        using var mfgProviders = wantMfg
            ? NativeMfgProviders.Stage(gameDir, scan.Upscalers, runtime.GetValueOrDefault(FrameGenDll), mfg!, Note, exeDir)
            : null;
        ct.ThrowIfCancellationRequested();
        // The player's copy of DLSS5 NR, put aside before the restore below changes the game folder.
        using var nrDir = req.Route == Route.NrAddon || feederLecram
            ? await Task.Run(() => NrAddon.Stage(req.NrAddonSource!, gameDir, exeDir, Journal.ReadManifest(gameDir)), ct)
            : null;
        if (feederLecram && (Pe.GetBitness(Path.Combine(nrDir!.Dir, NrAddon.Addon64)) != 64 ||
            AddonIdentity.Read(nrDir.Dir).Name.Contains("NeuralMaster", StringComparison.OrdinalIgnoreCase)))
            throw new InstallException("Feeder needs your local x64 DLSS5 NR add-on.");
        var optiProfile = req.Route == Route.OptiScaler ? build.Id : null;
        // Count destination copies and the originals the journal must back up.
        await Task.Run(() => AssertRoom(gameDir, exeDir, scan.Manifest,
            InstallSpaceNeeded(req, scan, runtime, reshade, reshade64, consumer, multipass,
                feeder, uplift, opti, null, nrDir?.Dir, mfgProviders, preview)), ct);
        if (previewOnly) return;

        AssertGameClosed(exePath);
        ReleaseDetroitFeederHelper(gameDir, exePath, Note);
        using var transaction = InstallTransaction.Begin(gameDir);
        try
        {
        NeuralMasterProtection.Check(gameDir, exePath);
        // ---- 3. undo an earlier install by this app, keeping the settings the player changed in the game
        var kept = new List<KeptSetting>();
        var old = Journal.ReadManifest(gameDir);
        if (req.Route == Route.MfgOnly && old is not null)
        {
            AssertGameClosed(exePath);
            if (MfgOnlyConflict(gameDir, exePath, old) is { } changedSetup)
                throw new InstallException(changedSetup);
            progress?.Report("Installing MFG unlock…");
            await Task.Run(() =>
            {
                var existing = new Session(gameDir, old, log);
                var mfgJob = new Job
                {
                    Req = req, S = existing, ExeDir = exeDir, Runtime = runtime, ReShade = reshade,
                    MfgFile = mfgProviders!.BackendPath, MfgProviders = mfgProviders, Scan = scan,
                };
                PlaceReShade(mfgJob, feederMode: false);
                RunMfg(mfgJob);
                UpdateEarlyAddons(existing, exeDir, req.LoadAddonsEarly, mfgOnly: true);
                existing.Save();
            });
            transaction.Commit();
            return;
        }
        if (old is not null)
        {
            // The downloads can take minutes, and ReShade saves its settings while the game runs.
            AssertGameClosed(exePath);
            RouteProfiles.Save(gameDir, old, Note);
            Note("Restoring the previous install by DLSS 5 Master first…");
            progress?.Report("Restoring the previous install…");
            kept = await Task.Run(() => KeepSettings(gameDir, old, exeDir, req.Route, req.Bitness, wantMfg, optiProfile, Note));
            await Task.Run(() => RestoreForReinstall(gameDir, kept, log, Note));
        }

        if (old is null || old.Route != RouteKey(req.Route))
        {
            var profile = RouteProfiles.Load(gameDir, exePath, req.Route, req.Bitness, wantMfg, optiProfile);
            var currentMfg = kept.Where(k => Path.GetFileName(k.Rel).StartsWith("MFGAdaUnlock", StringComparison.OrdinalIgnoreCase)).ToList();
            kept = profile.Where(k => !currentMfg.Any(m => m.Rel.Equals(k.Rel, StringComparison.OrdinalIgnoreCase))).Concat(currentMfg).ToList();
            if (profile.Count > 0) Note("Restoring this route's saved in-game settings.");
        }

        // ---- 4. install
        progress?.Report("Installing…");
        var session = Session.Begin(gameDir, exePath, RouteKey(req.Route), ApiKey(req.Api), req.Bitness, log);
        session.Manifest.RenderingApi = req.Api.ToString();
        // Recorded with the route, before any file is written: a setup that stops part-way still tells the next Reinstall
        // which OptiScaler build and which MFG unlock the settings it put back belong to.
        if (optiProfile is not null)
        {
            session.Manifest.OptiScalerBuild = optiProfile;
            session.Manifest.OptiScalerHook = req.OptiHook;
        }
        if (req.Route == Route.NrAddon || feederLecram)
        {
            var identity = AddonIdentity.Read(nrDir?.Dir, req.Bitness);
            session.Manifest.AddonName = identity.Name;
            session.Manifest.AddonVersion = identity.Version;
        }
        if (req.Route == Route.Feeder)
            session.Manifest.FeederBackend = feederLecram ? FeederBackend.Lecram : FeederBackend.RenoDx;
        if (req.Route == Route.Uplift)
        {
            session.Manifest.AddonName = "GITC Uplift";
            session.Manifest.AddonVersion = Components.UpliftVersion;
        }
        session.Save();
        await Task.Run(() =>
        {
            // First, so the route code puts the app's own keys back on top of the player's settings.
            if (kept.Count > 0) PutBackSettings(session, exeDir, kept);
            var job = new Job
            {
                Req = req, S = session, ExeDir = exeDir, Runtime = runtime, PreferNeuralModel = preferNeuralModel,
                ReShade = reshade, ReShade64 = reshade64, Consumer = consumer, Multipass = multipass,
                Feeder = feeder, Uplift = uplift, OptiFolder = opti, Build = build, MfgFile = mfgProviders?.BackendPath ?? mfg, MfgProviders = mfgProviders, NrAddon = nrDir?.Dir,
                // Fresh look at the folder now that any earlier install is gone.
                Scan = Scanner.Scan(gameDir, exePath),
            };
            switch (req.Route)
            {
                case Route.Native: RunRenoDx(job, multipassMode: false); break;
                case Route.Multipass: RunRenoDx(job, multipassMode: true); break;
                case Route.Feeder: RunFeeder(job); break;
                case Route.Uplift: RunUplift(job); break;
                case Route.OptiScaler: RunOptiScaler(job); break;
                case Route.NrAddon: RunNrAddon(job); break;
                case Route.MfgOnly: PlaceReShade(job, feederMode: false); break;
            }
            if (wantMfg) RunMfg(job);
            // Last, with every add-on in place: ReShade must not be told to load early an add-on that is gone.
            if (req.Route != Route.OptiScaler)
                UpdateEarlyAddons(session, exeDir, req.LoadAddonsEarly, mfgOnly: req.Route == Route.MfgOnly,
                    addonDir: req.Route == Route.Uplift ? UpliftAddonDirectory(gameDir, exeDir) : null);
            session.Save();
        });
        session.Log($"Done: {RouteLabel(RouteKey(req.Route))} installed. Restore originals undoes every change.");
        transaction.Commit();
        }
        catch (Exception failure)
        {
            try { transaction.Rollback(); Note("Install failed. The setup from before this operation was restored automatically."); }
            catch (Exception recovery) { throw new InstallException($"{failure.Message} Rollback could not finish: {recovery.Message}. Snapshots are retained; close the game and retry to recover them."); }
            throw;
        }
    }

    // ------------------------------------------------------------------ in-game settings kept across a reinstall

    /// <summary>
    /// Reads, before the restore that starts a reinstall, the settings files that restore would undo: ReShade.ini (ReShade's
    /// own settings and the RenoDX add-ons') with its presets, the Feeder's, OptiScaler's and the MFG unlock's. A route's files
    /// only while the route stays the same (for OptiScaler also the build, whose settings differ from build to build), the MFG
    /// unlock's while it stays on, and none when the game's executable is in another folder now. Files the restore leaves
    /// alone need nothing. <see cref="PutBackSettings"/> writes them into the new install.
    /// </summary>
    internal static List<KeptSetting> KeepSettings(string gameDir, Manifest old, string exeDir, Route route, int bitness, bool mfg,
                                                   string? optiBuild, Action<string> log)
    {
        var kept = new List<KeptSetting>();
        var root = Path.GetFullPath(gameDir).TrimEnd('\\', '/');
        var dir = Path.GetFullPath(exeDir).TrimEnd('\\', '/');
        bool routeBefore = old.Route is "native" or "multipass" or "feeder" or "optiscaler" or "neuralmaster" or "nraddon" or "mfgonly" or "uplift";
        string? dirBefore = null;
        try { if (!string.IsNullOrEmpty(old.Exe)) dirBefore = Path.GetDirectoryName(Journal.SafePath(root, old.Exe)); }
        catch (InstallException) { }
        if (dirBefore is null || !dirBefore.Equals(dir, StringComparison.OrdinalIgnoreCase))
        {
            if (routeBefore) log("The game's executable is in another folder now, so this setup starts with fresh in-game settings.");
            return kept;
        }

        var candidates = new List<string>();
        var routeKey = RouteKey(route);
        // Native's add-on and DLSS5 NR keep their settings in the same ReShade.ini section ([RenoDX.DLSS5]).
        static string Family(string? key) => key == "nraddon" ? "native" : key ?? "";
        if (Family(old.Route) != Family(routeKey))
        {
            if (routeBefore) log($"The route changed from {RouteLabel(old.Route)} to {RouteLabel(routeKey)}, so this setup starts with fresh in-game settings.");
        }
        else if (route == Route.OptiScaler)
        {
            if (Components.CurrentOptiBuildId(old.OptiScalerBuild) == Components.CurrentOptiBuildId(optiBuild))
                candidates.Add(Path.Combine(dir, "OptiScaler.ini"));
        }
        else
        {
            var ini = Path.Combine(dir, "ReShade.ini");
            candidates.Add(ini);
            var presetPath = Ini.Get(Ini.ReadText(ini), "GENERAL", "PresetPath");
            // ReShade's own preset also when the player has since switched to another one: the restore would remove it.
            candidates.Add(Path.Combine(dir, "ReShadePreset.ini"));
            // A preset is an .ini or .txt file: a PresetPath naming anything else (a DLL of the install, say) brings nothing back.
            if (!string.IsNullOrWhiteSpace(presetPath) && PresetInGame(root, dir, presetPath) is { } preset
                && (preset.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || preset.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                candidates.Add(preset);
            if (route == Route.Feeder) candidates.Add(Path.Combine(dir, "dlss5-feed.cfg"));
            if (route == Route.Feeder && (bitness == 32 || IsDetroitExecutable(old.Exe) &&
                    TracksFile(old, Path.GetRelativePath(root, Path.Combine(dir, "host64", FeederHost64)))))
                candidates.Add(Path.Combine(dir, "host64", "ReShade.ini"));
            if (route == Route.Feeder && optiBuild is not null && old.OptiScalerBuild == optiBuild)
                candidates.Add(Path.Combine(dir, "OptiScaler.ini"));
        }
        // The player's choices (XeMFG or not, the per-game profiles), not the add-on's log or compatibility report.
        if (old.MfgUnlock is not null && mfg)
        {
            candidates.Add(Path.Combine(dir, "MFGAdaUnlock.ini"));
            candidates.Add(Path.Combine(dir, "MFGAdaUnlock.profiles.json"));
        }

        // Only what the restore changes: the files in the record, and those the add-ons wrote while the game ran.
        var recorded = old.Added.Concat(old.Replaced.Select(r => r.Rel)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leftovers = Journal.RuntimeLeftovers(root, old);
        foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(root, file);
            if (!File.Exists(file) || !(recorded.Contains(rel) || leftovers.Contains(file, StringComparer.OrdinalIgnoreCase))) continue;
            try { kept.Add(new KeptSetting(rel, File.ReadAllBytes(file))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"Could not keep {rel}: {e.Message}"); }
        }
        return kept;
    }

    /// <summary>
    /// The restore that starts a reinstall. When it stops part-way (another program holds a file of the install open), its
    /// record stays and still covers every file <see cref="KeepSettings"/> read: the kept settings go back where they were
    /// before the error is passed on, so the next try keeps them again and Restore originals still undoes them.
    /// </summary>
    /// <param name="log">For the restore, whose record logs to the activity log itself.</param>
    /// <param name="note">For this method's own lines, which go to the activity log as well.</param>
    private static void RestoreForReinstall(string gameDir, IReadOnlyList<KeptSetting> kept, Action<string> log, Action<string> note)
    {
        // Keep this outside the rollback catch: a changed/deleted user file is
        // not our restore's mutation and must never receive the stale snapshot.
        foreach (var setting in kept)
        {
            var file = Journal.SafePath(gameDir, setting.Rel);
            try
            {
                if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(setting.Bytes))
                    throw new InstallException($"Your settings changed during preparation ({setting.Rel}). This reinstall's restore was not started. Your current files were kept; review them before retrying.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InstallException($"Could not recheck current settings ({setting.Rel}): {e.Message}. This reinstall's restore was not started.");
            }
        }
        try { Session.Restore(gameDir, log); }
        catch
        {
            // Never once the record is closed: nothing would undo them then.
            if (File.Exists(Journal.ManifestPath(gameDir)))
            {
                var putBack = new List<string>();
                foreach (var k in kept)
                {
                    var file = Journal.SafePath(gameDir, k.Rel);
                    try
                    {
                        if (File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(k.Bytes)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        Journal.ClearReadOnly(file);
                        File.WriteAllBytes(file, k.Bytes);
                        putBack.Add(k.Rel);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { note($"Could not put back {k.Rel}: {e.Message}"); }
                }
                if (putBack.Count > 0) note($"Put back your in-game settings: {string.Join(", ", putBack)}.");
            }
            throw;
        }
    }

    /// <summary>
    /// Writes the settings <see cref="KeepSettings"/> kept into the new install, recorded like every other change so Restore
    /// originals still puts back what was there before the first setup. The route code runs after it and puts the app's own
    /// keys back on top. The DLSS 5 add-ons come off ReShade.ini's LoadFromDllMain line, other entries stay: the "Load add-ons
    /// early" box decides those, and <see cref="UpdateEarlyAddons"/> puts them back when it is ticked. Those that the player's
    /// own ReShade.ini from before the first setup listed stay as well, as they did through that setup.
    /// </summary>
    internal static void PutBackSettings(Session s, string exeDir, IReadOnlyList<KeptSetting> kept)
    {
        var iniPath = Path.GetFullPath(Path.Combine(exeDir, "ReShade.ini"));
        // What the restore left there: the player's own ReShade.ini from before the first setup, if there was one.
        var listedBefore = SplitList(Ini.Get(Ini.ReadText(iniPath), "ADDON", "LoadFromDllMain"));
        foreach (var k in kept) s.WriteTracked(Journal.SafePath(s.GameDir, k.Rel), k.Bytes, "settings");
        if (kept.Any(k => Journal.SafePath(s.GameDir, k.Rel).Equals(iniPath, StringComparison.OrdinalIgnoreCase)))
        {
            var text = Ini.ReadText(iniPath);
            var listed = SplitList(Ini.Get(text, "ADDON", "LoadFromDllMain"));
            var others = listed.Where(n => !IsDlssAddon(n) || listedBefore.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (others.Count < listed.Count) s.WriteTracked(iniPath, Ini.Set(text, "ADDON", "LoadFromDllMain", string.Join(",", others)));
        }
        s.Log($"Kept your in-game settings: {string.Join(", ", kept.Select(k => k.Rel))}.");
    }

    // ------------------------------------------------------------------ shared steps

    internal static string ReShadeHookFor(InstallRequest req) => req.Api switch
    {
        RenderApi.OpenGL => "opengl32.dll",
        RenderApi.DX9 => "d3d9.dll",
        RenderApi.DX11 when req.ReShadeProxy.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase) => "d3d11.dll",
        _ => "dxgi.dll",
    };

    private static void PlaceReShade(Job job, bool feederMode)
    {
        var hook = ReShadeHookFor(job.Req);
        var current = Scanner.InspectReShade(job.ExeDir);

        if (current.Installed && current.IsAsi)
        {
            if (feederMode)
                throw new InstallException($"ReShade is loaded here as an ASI plugin ({current.File}). The Feeder route needs its own ReShade as {hook}; remove the ASI ReShade first.");
            if (!current.AddonSupport || job.Req.Route == Route.Uplift &&
                !UpliftReShadeSupported(Path.Combine(job.ExeDir, current.File!), job.Req.Api))
                throw new InstallException($"ReShade is loaded here as an ASI plugin ({current.File}) without add-on support. Replace it with the add-on build of ReShade, or remove it.");
            job.Log($"Keeping the existing ReShade ASI ({current.File}); it has add-on support.");
            job.S.Manifest.ReShadeHook = current.File;
            return;
        }

        if ((feederMode || job.Req.Route == Route.Uplift) && job.Req.Api == RenderApi.OpenGL && current.Installed &&
            !string.Equals(current.File, hook, StringComparison.OrdinalIgnoreCase))
            throw new InstallException($"ReShade uses {current.File} here. Remove that setup before installing the OpenGL {(feederMode ? "Feeder" : "GITC Uplift")} with {hook}.");

        foreach (var name in new[] { "dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "opengl32.dll" })
        {
            var f = Path.Combine(job.ExeDir, name);
            if (!File.Exists(f) || job.S.WasAdded(job.S.Rel(f)) || Pe.VersionMentions(f, "ReShade")) continue;
            if (name.Equals(hook, StringComparison.OrdinalIgnoreCase) || Pe.VersionMentions(f, "OptiScaler"))
                throw new InstallException($"{name} next to the game belongs to another mod (for example OptiScaler or a wrapper). Remove it, or restore it in the tool that installed it, then try again.");
        }

        if (current.Installed && current.File is not null)
        {
            var existing = Path.Combine(job.ExeDir, current.File);
            if (current.AddonSupport && Pe.GetBitness(existing) == job.Bits &&
                (job.Req.Route != Route.Uplift || UpliftReShadeSupported(existing, job.Req.Api)))
            {
                job.Log($"Keeping the existing add-on ReShade {current.Version} ({current.File}).");
                job.S.Manifest.ReShadeHook = current.File;
                return;
            }
            if (!current.File.Equals(hook, StringComparison.OrdinalIgnoreCase))
            {
                job.S.DeleteTracked(existing, "reshade");
                job.Log($"Removed {current.File}: a ReShade without add-on support (or of the wrong bitness) cannot load the add-ons.");
            }
        }

        if (job.Req.Route == Route.Uplift && !UpliftReShadeSupported(job.ReShade!, job.Req.Api))
            throw new InstallException("GITC Uplift needs ReShade with full add-on support. OpenGL needs ReShade 6.8 or newer.");

        // ReShade writes these on first launch; tracking them now means Restore removes them later.
        foreach (var f in new[] { "ReShade.ini", "ReShadePreset.ini", "ReShade.log" })
            job.S.TrackBeforeWrite(Path.Combine(job.ExeDir, f), "reshade");
        job.S.CopyTracked(job.ReShade!, Path.Combine(job.ExeDir, hook), "reshade");
        job.S.Manifest.ReShadeHook = hook;
        job.Log($"Installed ReShade {Pe.GetFileVersion(job.ReShade!)} as {hook}.");
    }

    private static string AddonStem(string addonFile)
    {
        foreach (var ext in new[] { ".addon64", ".addon32", ".addon" })
            if (addonFile.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return addonFile[..^ext.Length];
        return addonFile;
    }

    /// <summary>Take the add-on out of ReShade's DisabledAddons list if the user had switched it off.</summary>
    private static void EnableAddon(Job job, string dir, string addonFile, string? displayName = null)
    {
        var ini = Path.Combine(dir, "ReShade.ini");
        if (!File.Exists(ini)) return;
        var text = Ini.ReadText(ini);
        var disabled = Ini.Get(text, "ADDON", "DisabledAddons");
        if (string.IsNullOrEmpty(disabled)) return;
        var stem = AddonStem(addonFile);
        var items = disabled.Split(',').Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
        var kept = items.Where(i => !i.Equals(stem, StringComparison.OrdinalIgnoreCase) &&
            !i.Equals(addonFile, StringComparison.OrdinalIgnoreCase) && !i.Equals(displayName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count == items.Count) return;
        job.S.WriteTracked(ini, Ini.Set(text, "ADDON", "DisabledAddons", string.Join(",", kept)));
        job.Log($"Re-enabled {stem} in {job.S.Rel(ini)}.");
    }

    /// <summary>
    /// The DLSS 5 add-ons, this app's and other DLSS 5 tools'. ReShade loads every add-on in its folder, so one that another
    /// install left behind (an older MFG unlock, a second neural add-on, a Feeder) would run alongside this one.
    /// </summary>
    private static readonly string[] DlssAddonPatterns = { "renodx-dlss*.addon64", "renodx-mfgunlock.addon*", "dlss5-feed*.addon*", "gitc-uplift.addon*", "Neuralmaster-nvngx.dll.addon64", "NeuralMaster.addon64" };

    /// <summary>
    /// Other neural rendering add-ons, including legacy names. A setup sets these aside to avoid competing passes.
    /// </summary>
    private static readonly (string Pattern, string Name)[] OtherNeuralAddons =
    {
        ("dlss5-nr*.addon*", "DLSS5 NR add-on"),
        ("gitc-uplift*.addon*", "GITC Uplift"),
        ("uplift.addon*", "GITC Uplift"),
        ("deep-fried-chicken*.addon*", "Deep Fried Chicken"),
    };

    internal static bool IsOtherNeuralAddon(string name) =>
        OtherNeuralAddons.Any(addon => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(addon.Pattern, name, ignoreCase: true));

    /// <summary>
    /// Sets aside the DLSS 5 add-ons in <paramref name="dir"/> other than <paramref name="keep"/>: each goes into this
    /// install's backup, so Restore originals puts it back. Other add-ons (a game's own RenoDX HDR mod, say) stay.
    /// </summary>
    internal static List<string> SetAsideOtherAddons(Session s, string dir, params string?[] keep)
    {
        var setAside = new List<string>();
        if (!Directory.Exists(dir)) return setAside;
        var kept = keep.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in DlssAddonPatterns.Concat(OtherNeuralAddons.Select(o => o.Pattern)).SelectMany(p => Directory.EnumerateFiles(dir, p))
                     .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var name = Path.GetFileName(f);
            if (kept.Contains(name)) continue;
            s.DeleteTracked(f, "addon");
            setAside.Add(name);
            var other = OtherNeuralAddons.FirstOrDefault(o => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(o.Pattern, name, ignoreCase: true)).Name;
            s.Log(other is null
                ? $"Set aside {name}: ReShade loads every add-on in this folder, so it would run alongside this install. Restore originals puts it back."
                : $"Set aside {name} ({other}, another DLSS 5 add-on): next to this install it would stop working or run a second neural pass. Restore originals puts it back.");
        }
        return setAside;
    }

    // ------------------------------------------------------------------ add-ons that ReShade loads as soon as it starts

    // ReShade.log: "Failed to load add-on from '<path>' with error code <n>!"
    [GeneratedRegex(@"Failed to load add-on from '([^']+)' with error code (\d+)")]
    private static partial Regex FailedAddonLine();

    private static bool IsDlssAddon(string name) =>
        DlssAddonPatterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));

    private static bool IsAddonFile(string name) =>
        name.EndsWith(".addon", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase);

    /// <summary>What ReShade.ini's [ADDON] LoadFromDllMain names: the add-ons ReShade loads as soon as it starts.</summary>
    internal static List<string> EarlyAddons(string dir) =>
        SplitList(Ini.Get(Ini.ReadText(Path.Combine(dir, "ReShade.ini")), "ADDON", "LoadFromDllMain"));

    /// <summary>
    /// The DLSS 5 add-ons that ReShade could not start in the game's last run after <paramref name="since"/>: ReShade.log
    /// shows them failing "with error code 1114", their own start-up giving up. In Phantom Blade Zero they could not find
    /// ReShade when it loaded them at the game's first graphics device; loaded as soon as ReShade starts, they could.
    /// </summary>
    internal static List<string> AddonsFailingToStart(string dir, DateTime since)
    {
        var failing = new List<string>();
        var log = Path.Combine(dir, "ReShade.log");
        try
        {
            if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) < (since.Kind == DateTimeKind.Local ? since.ToUniversalTime() : since))
                return failing;
            // ReShade keeps its log open while the game runs.
            using var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            for (string? line; (line = reader.ReadLine()) is not null;)
            {
                var m = FailedAddonLine().Match(line);
                if (!m.Success || m.Groups[2].Value != "1114") continue;
                var name = Path.GetFileName(m.Groups[1].Value);
                if (IsDlssAddon(name) && !failing.Contains(name, StringComparer.OrdinalIgnoreCase)) failing.Add(name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return failing;
    }

    /// <summary>
    /// For a game set up with a ReShade route: the DLSS 5 add-ons ReShade could not start since the install (see
    /// <see cref="AddonsFailingToStart"/>) that ReShade.ini does not load early yet.
    /// </summary>
    public static List<string> AddonsToLoadEarly(string gameDir, Manifest? manifest)
    {
        if (manifest is null || manifest.Route is not ("native" or "multipass" or "feeder" or "neuralmaster" or "nraddon" or "mfgonly" or "uplift") || string.IsNullOrEmpty(manifest.Exe)) return new();
        string? dir;
        try { dir = Path.GetDirectoryName(Journal.SafePath(Path.GetFullPath(gameDir).TrimEnd('\\', '/'), manifest.Exe)); }
        catch (InstallException) { return new(); }
        if (dir is null) return new();
        var early = EarlyAddons(dir);
        return AddonsFailingToStart(dir, manifest.Date).Where(a => !early.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>What the DLSS 5 add-on measured in a game's run: real frames a second with and without its pass, its cost, frame generation (1 = off).</summary>
    public sealed record RunSpeed(int FpsWithNr, int FpsWithoutNr, double NrMs, int FrameGen);

    /// <summary>Below this many real frames a second, frame generation's in-between frames get poor and can flicker.</summary>
    internal const int FrameGenMinFps = 40;

    [GeneratedRegex(@"NR: ([0-9.]+) ms of GPU time per frame \(estimate: (\d+) fps without NR, (\d+) fps with it\)")]
    private static partial Regex NrCostLine();

    [GeneratedRegex(@"frame generation detected \((\d+)x\)")]
    private static partial Regex FrameGenLine();

    /// <summary>
    /// The DLSS 5 add-on's own figures from a ReShade.log: its last cost line and the highest frame generation it saw;
    /// null when it wrote no cost line.
    /// </summary>
    internal static RunSpeed? ReadRunSpeed(IEnumerable<string> lines)
    {
        Match? cost = null;
        int frameGen = 1;
        foreach (var line in lines)
        {
            if (NrCostLine().Match(line) is { Success: true } m) cost = m;
            if (FrameGenLine().Match(line) is { Success: true } g && int.TryParse(g.Groups[1].Value, out var x)) frameGen = Math.Max(frameGen, x);
        }
        if (cost is null || !double.TryParse(cost.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
            return null;
        return new RunSpeed(int.Parse(cost.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                            int.Parse(cost.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), ms, frameGen);
    }

    /// <summary>
    /// For a game set up with Native or the Feeder (whose add-on writes these figures): the last run since the install, when
    /// frame generation ran on fewer real frames a second than it needs (CoD at 15 fps flickered); null otherwise.
    /// </summary>
    public static RunSpeed? SlowFrameGeneration(string gameDir, Manifest? manifest)
    {
        if (manifest is null || manifest.Route is not ("native" or "feeder") || string.IsNullOrEmpty(manifest.Exe)) return null;
        try
        {
            var dir = Path.GetDirectoryName(Journal.SafePath(Path.GetFullPath(gameDir).TrimEnd('\\', '/'), manifest.Exe));
            if (dir is null) return null;
            var log = Path.Combine(dir, "ReShade.log");
            var since = manifest.Date.Kind == DateTimeKind.Local ? manifest.Date.ToUniversalTime() : manifest.Date;
            if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) < since) return null;
            // ReShade keeps its log open while the game runs.
            using var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var lines = new List<string>();
            for (string? line; (line = reader.ReadLine()) is not null;) lines.Add(line);
            return ReadRunSpeed(lines) is { FrameGen: >= 2 } speed && speed.FpsWithNr < FrameGenMinFps ? speed : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstallException) { return null; }
    }

    /// <summary>
    /// Keeps the [ADDON] LoadFromDllMain line of ReShade.ini in <paramref name="dir"/> in step with the add-ons there: names
    /// of add-on files that are gone come off it (ReShade reports "errors loading some add-ons" for each), and with
    /// <paramref name="loadEarly"/> the DLSS 5 add-ons in the folder go on it. Other entries stay. True when ReShade.ini changed.
    /// </summary>
    internal static bool UpdateEarlyAddons(Session s, string dir, bool loadEarly, bool mfgOnly = false, string? addonDir = null)
    {
        var iniPath = Path.Combine(dir, "ReShade.ini");
        var text = Ini.ReadText(iniPath);
        var listed = SplitList(Ini.Get(text, "ADDON", "LoadFromDllMain"));
        addonDir ??= dir;
        var gone = mfgOnly ? new List<string>() : listed.Where(n => IsAddonFile(n) && !File.Exists(Path.Combine(addonDir, n))).ToList();
        var kept = listed.Where(n => !gone.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var added = new List<string>();
        if (loadEarly && Directory.Exists(addonDir))
            foreach (var name in (mfgOnly ? new[] { Components.MfgAddon } : DlssAddonPatterns)
                         .SelectMany(p => Directory.EnumerateFiles(addonDir, p)).Select(Path.GetFileName).OfType<string>()
                         .Where(n => !n.StartsWith("gitc-uplift.addon", StringComparison.OrdinalIgnoreCase) ||
                                     n.Equals($"gitc-uplift.addon{s.Manifest.Bitness}", StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                if (!kept.Contains(name, StringComparer.OrdinalIgnoreCase)) { kept.Add(name); added.Add(name); }
        if (gone.Count == 0 && added.Count == 0) return false;
        s.WriteTracked(iniPath, Ini.Set(text, "ADDON", "LoadFromDllMain", string.Join(",", kept)));
        if (gone.Count > 0)
            s.Log($"Took {string.Join(", ", gone)} off the LoadFromDllMain line in {s.Rel(iniPath)}: not in this folder, so ReShade would report errors loading add-ons.");
        if (added.Count > 0)
            s.Log($"{s.Rel(iniPath)} now has ReShade load {string.Join(" and ", added)} as soon as it starts (LoadFromDllMain).");
        return true;
    }

    /// <summary>
    /// "Load add-ons early" for a game already set up: its ReShade.ini loads the DLSS 5 add-ons as soon as ReShade starts.
    /// Recorded like every other change, so Restore originals undoes it.
    /// </summary>
    public static void LoadAddonsEarly(string gameDir, Action<string> log)
    {
        NeuralMasterProtection.Check(gameDir, null);
        var manifest = Journal.ReadManifest(gameDir) ?? throw new InstallException("Set up DLSS 5 in this game first.");
        if (string.IsNullOrEmpty(manifest.Exe)) throw new InstallException("The install record names no game executable.");
        var s = new Session(gameDir, manifest, log);
        var exe = Journal.SafePath(s.GameDir, manifest.Exe);
        // ReShade saves ReShade.ini while the game runs, which would undo the change.
        AssertGameClosed(exe);
        var dir = Path.GetDirectoryName(exe)!;
        if (!UpdateEarlyAddons(s, dir, loadEarly: true,
            addonDir: manifest.Route == "uplift" ? UpliftAddonDirectory(s.GameDir, dir) : null))
            s.Log("ReShade.ini already loads the DLSS 5 add-ons as soon as ReShade starts.");
        s.Save();
    }

    private static void FixOldCompiler(Job job)
    {
        var local = Path.Combine(job.ExeDir, "D3DCompiler_47.dll");
        if (!File.Exists(local)) return;
        var version = Pe.GetFileVersion(local);
        var major = version?.Split('.')[0];
        if (major is null || !int.TryParse(major, out var m) || m >= 10) return;
        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "D3DCompiler_47.dll"))) return;
        job.S.DeleteTracked(local, "compiler");
        job.Log($"Removed the game's outdated D3DCompiler_47.dll ({version}); Windows' own copy compiles the neural pass.");
    }

    /// <summary>Preserve newer runtimes unless a different neural model is requested.</summary>
    private static void PlaceRuntime(Job job, string src, string dest, int bitness)
    {
        int srcBits = Pe.GetBitness(src);
        if (srcBits != bitness)
            throw new InstallException($"{Path.GetFileName(src)} in the runtime folder is {(srcBits == 0 ? "not a valid DLL" : srcBits + "-bit")}; {job.S.Rel(dest)} needs {bitness}-bit.");
        var srcVersion = Pe.GetFileVersion(src);
        if (File.Exists(dest))
        {
            int destBits = Pe.GetBitness(dest);
            if (destBits != 0 && destBits != bitness)
            {
                job.Log($"Skipped {job.S.Rel(dest)}: it is {destBits}-bit.");
                return;
            }
            var destVersion = Pe.GetFileVersion(dest);
            if (Path.GetFileName(dest).Equals(NeuralDll, StringComparison.OrdinalIgnoreCase) &&
                (job.PreferNeuralModel || Components.IsBundledNvidiaModel(dest)))
            {
                if (new FileInfo(src).Length != new FileInfo(dest).Length || !ComponentHealth.Matches(dest, Components.Sha256(src)))
                {
                    job.S.CopyTracked(src, dest, "runtime");
                    job.Log($"Changed the neural model at {job.S.Rel(dest)} ({srcVersion ?? "unknown version"}).");
                }
                return;
            }
            if (destVersion is not null && srcVersion is not null)
            {
                int cmp = Scanner.CompareVersions(destVersion, srcVersion);
                bool comparable = Scanner.IsVersion(destVersion) && Scanner.IsVersion(srcVersion);
                if (destVersion == srcVersion || comparable && cmp == 0)
                {
                    job.Log($"{job.S.Rel(dest)} is already {destVersion}.");
                    return;
                }
                if (cmp > 0)
                {
                    job.Log($"Kept {job.S.Rel(dest)} {destVersion}: it is newer than the runtime folder's {srcVersion}.");
                    return;
                }
            }
            job.S.CopyTracked(src, dest, "runtime");
            job.Log($"Updated {job.S.Rel(dest)}: {destVersion ?? "?"} → {srcVersion ?? "?"}.");
            return;
        }
        job.S.CopyTracked(src, dest, "runtime");
        job.Log($"Added {job.S.Rel(dest)} {srcVersion}.");
    }

    // ------------------------------------------------------------------ DLSS5 NR (the player's copy)

    private static string? VulkanNrUnavailable(InstallRequest request, string exePath) => request.Bitness == 64
        ? ReShadeVulkanLayer.UnavailableReason(exePath, request.VulkanLayerManifests)
        : ReShadeVulkanLayer.Need64Bit;

    private static void RunNrAddon(Job job)
    {
        var bits = job.Bits;
        var src = job.NrAddon!;
        bool vulkan = job.Req.Api == RenderApi.Vulkan;
        if (vulkan)
        {
            AssertGameClosed(job.Req.ExePath);
            if (VulkanNrUnavailable(job.Req, job.Req.ExePath) is { } layerProblem)
                throw new InstallException(layerProblem);
        }
        else FixOldCompiler(job);

        foreach (var u in job.Scan.Upscalers.Where(u => u.Kind is UpscalerKind.DlssSR or UpscalerKind.DlssNR && u.Bitness == bits).ToList())
            if (job.Runtime.TryGetValue(u.Name, out var dll)) PlaceRuntime(job, dll, u.Path, bits);
        // NR runs 64-bit in every game (32-bit ones through its host) and looks for its runtime beside the game's exe.
        PlaceRuntime(job, job.Runtime[NeuralDll], Path.Combine(job.ExeDir, NeuralDll), 64);

        SetAsideOtherAddons(job.S, job.ExeDir, NrAddon.Addon64, NrAddon.Addon32, job.MfgFile is null ? null : Components.MfgAddon);
        SetAsideConsumerCopies(job);
        foreach (var addon in new[] { NrAddon.Addon64, NrAddon.Addon32 })
            if (File.Exists(Path.Combine(src, addon))) job.S.CopyTracked(Path.Combine(src, addon), Path.Combine(job.ExeDir, addon), "addon");
        if (Directory.Exists(Path.Combine(src, NrAddon.HostFolder)))
            job.S.CopyTreeTracked(Path.Combine(src, NrAddon.HostFolder), Path.Combine(job.ExeDir, NrAddon.HostFolder), "addon");
        else if (bits != 64 || job.Req.Api is RenderApi.DX9 or RenderApi.DX10 or RenderApi.OpenGL)
            job.Log($"Your copy has no {NrAddon.HostFolder} folder: NR needs its host in this game, so it will not start. Pick the whole unzipped add-on folder in Settings.");
        job.Log($"Added your copy of DLSS5 NR {NrAddon.Version(src) ?? ""}".TrimEnd() + ".");

        if (vulkan)
        {
            UseRegisteredVulkanLayer(job);
            job.Log("Choose Present in the NR panel if needed.");
        }
        else
        {
            PlaceReShade(job, feederMode: false);
            var reshade = Scanner.InspectReShade(job.ExeDir);
            if (reshade.Installed && BulkSetup.OlderThanNrNeeds(reshade.Version))
                job.Log($"DLSS5 NR needs ReShade 6.8 or newer, and this game keeps ReShade {reshade.Version}. Remove that ReShade, then reinstall.");
        }
        EnableAddon(job, job.ExeDir, bits == 64 ? NrAddon.Addon64 : NrAddon.Addon32);
        RemoveFeedTechnique(job);
        if (!job.Scan.Upscalers.Any(u => u.Kind == UpscalerKind.DlssSR && u.Bitness == bits))
            job.Log("The game has no DLSS of its own: NR takes DLSS Super Resolution from the NVIDIA driver's store, or from the storage DLSS5 NR's setup fills.");
        job.Log("In game, Home opens NR's panel and Shift+Home opens ReShade.");
    }

    private static void UseRegisteredVulkanLayer(Job job)
    {
        var ini = Path.Combine(job.ExeDir, "ReShade.ini");
        if (!File.Exists(ini))
            job.S.WriteTracked(ini, "[ADDON]\r\nAddonPath=.\\\r\n");
        // Track runtime-created files only if they were absent when this setup started.
        foreach (var name in new[] { "ReShadePreset.ini", "ReShade.log" })
        {
            var path = Path.Combine(job.ExeDir, name);
            if (!File.Exists(path)) job.S.TrackBeforeWrite(path, "reshade");
        }
        job.Log("Using this game's registered ReShade Vulkan layer.");
    }

    private static bool UpliftReShadeSupported(string file, RenderApi api)
    {
        if (!Scanner.HasAddonSupport(file) ||
            Pe.Contains(file, "because this build of ReShade has only limited add-on functionality")) return false;
        if (api != RenderApi.OpenGL) return true;
        var version = Pe.GetFileVersion(file);
        return Scanner.IsVersion(version) && Scanner.CompareVersions(version, "6.8.0") >= 0;
    }

    private static string UpliftAddonDirectory(string gameDir, string exeDir)
    {
        string dir;
        try
        {
            var configured = Ini.Get(Ini.ReadText(Path.Combine(exeDir, "ReShade.ini")), "ADDON", "AddonPath")?.Trim('"');
            dir = string.IsNullOrWhiteSpace(configured) ? exeDir :
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured, exeDir));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            throw new InstallException("ReShade's AddonPath is invalid.");
        }
        var backup = Journal.BackupRoot(gameDir);
        if (dir.Equals(backup, StringComparison.OrdinalIgnoreCase) ||
            dir.StartsWith(backup + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InstallException("ReShade's AddonPath points into this game's backup folder.");
        InstallTransaction.CheckPath(gameDir, Path.Combine(dir, "gitc-uplift.addon64"));
        return dir;
    }

    private static void RunUplift(Job job)
    {
        var req = job.Req;
        AssertGameClosed(req.ExePath);
        if (req.Api == RenderApi.Vulkan)
        {
            if (ReShadeVulkanLayer.FeederUnavailableReason(req.ExePath, req.VulkanLayerManifests) is { } problem)
                throw new InstallException(problem);
            UseRegisteredVulkanLayer(job);
        }
        else
            PlaceReShade(job, feederMode: false);

        var addonDir = UpliftAddonDirectory(job.S.GameDir, job.ExeDir);
        if (job.MfgFile is not null && !addonDir.Equals(job.ExeDir, StringComparison.OrdinalIgnoreCase))
            throw new InstallException("MFG unlock needs ReShade's add-on folder to be beside this game's executable.");
        var payload = job.Uplift!;
        SetAsideOtherAddons(job.S, addonDir, "gitc-uplift.addon32", "gitc-uplift.addon64",
            job.MfgFile is null ? null : Components.MfgAddon);
        var legacyHelper = Path.Combine(addonDir, "uplift-helper64.exe");
        if (File.Exists(legacyHelper)) job.S.DeleteTracked(legacyHelper, "addon");
        job.S.CopyTreeTracked(payload.AddonDirectory, addonDir, "addon");
        job.S.CopyTracked(job.Runtime[NeuralDll], Path.Combine(addonDir, UpliftRuntimeFolder, NeuralDll), "runtime");
        job.S.CopyTreeTracked(payload.ShaderDirectory, Path.Combine(job.ExeDir, "reshade-shaders", "Shaders"), "shaders");

        var iniPath = Path.Combine(job.ExeDir, "ReShade.ini");
        var ini = Ini.ReadText(iniPath);
        ini = Ini.Set(ini, "GENERAL", "EffectSearchPaths",
            MergeSearchPaths(Ini.Get(ini, "GENERAL", "EffectSearchPaths"), @".\reshade-shaders\Shaders\**"));
        job.S.WriteTracked(iniPath, ini);
        EnableAddon(job, job.ExeDir, $"gitc-uplift.addon{job.Bits}", "GITC Uplift");
        RemoveFeedTechnique(job);
        job.Log($"Added GITC Uplift {Components.UpliftVersion}. In game, open ReShade (Home), select GITC Uplift and turn on Enable.");
    }

    /// <summary>
    /// Takes the Feeder's DLSS5_Feed technique off the game's ReShade preset (Techniques and TechniqueSorting): DLSS5 NR finds
    /// depth and motion itself, and its INSTALL.txt asks for the technique to go. Other techniques stay; a preset outside the
    /// game is shared with other games and is left as it is.
    /// </summary>
    private static void RemoveFeedTechnique(Job job)
    {
        var presetPath = Ini.Get(Ini.ReadText(Path.Combine(job.ExeDir, "ReShade.ini")), "GENERAL", "PresetPath");
        if (string.IsNullOrWhiteSpace(presetPath)) presetPath = @".\ReShadePreset.ini";
        if (PresetInGame(job.S.GameDir, job.ExeDir, presetPath) is not { } preset || !File.Exists(preset)) return;
        var text = Ini.ReadText(preset);
        var changed = text;
        foreach (var key in new[] { "Techniques", "TechniqueSorting" })
        {
            var items = SplitList(Ini.Get(changed, "", key));
            var kept = items.Where(i => !i.Split('@')[0].Trim().Equals("DLSS5_Feed", StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count < items.Count) changed = Ini.Set(changed, "", key, string.Join(",", kept));
        }
        if (changed == text) return;
        job.S.WriteTracked(preset, ToCrlf(changed));
        job.Log($"Took the Feeder's DLSS5_Feed technique off {job.S.Rel(preset)}: DLSS5 NR finds depth and motion itself.");
    }

    /// <summary>
    /// Sets aside the RenoDX DLSS5 add-on wherever else it is in the game: DLSS5 NR switches itself off when it finds one
    /// anywhere there ("two NR add-ons are loaded"), mirror folders included (Onimusha: Way of the Sword's _storage_).
    /// </summary>
    private static IEnumerable<string> RenoDxConsumerCopies(string gameDir)
    {
        var backup = Journal.BackupRoot(gameDir) + Path.DirectorySeparatorChar;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        return Directory.EnumerateFiles(gameDir, "renodx-dlss5*.addon*", options)
            .Where(f => !f.StartsWith(backup, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetAsideConsumerCopies(Job job)
    {
        foreach (var f in RenoDxConsumerCopies(job.S.GameDir).ToList())
        {
            job.S.DeleteTracked(f, "addon");
            job.Log($"Set aside {job.S.Rel(f)}: DLSS5 NR switches itself off while the RenoDX DLSS5 add-on is anywhere in the game. Restore originals puts it back.");
        }
    }

    // ------------------------------------------------------------------ Native / Multipass

    private static void RunRenoDx(Job job, bool multipassMode)
    {
        var bits = job.Bits;
        FixOldCompiler(job);

        foreach (var u in job.Scan.Upscalers.Where(u => u.Kind is UpscalerKind.DlssSR or UpscalerKind.DlssNR && u.Bitness == bits).ToList())
            if (job.Runtime.TryGetValue(u.Name, out var src)) PlaceRuntime(job, src, u.Path, bits);

        PlaceRuntime(job, job.Runtime[NeuralDll], Path.Combine(job.ExeDir, NeuralDll), bits);
        // A game without DLSS of its own gets it beside the game, as the Feeder route always does (it used to be a tick
        // box; leaving it out only made the route do nothing there).
        if (!job.Scan.Upscalers.Any(u => u.Kind == UpscalerKind.DlssSR && u.Bitness == bits))
        {
            if (job.Runtime.TryGetValue(DlssDll, out var dlss))
            {
                PlaceRuntime(job, dlss, Path.Combine(job.ExeDir, DlssDll), bits);
                job.Log($"The game has no {DlssDll} of its own, so it was added beside the game.");
            }
            else job.Log($"The runtime folder has no {DlssDll}; could not add DLSS.");
        }

        if (multipassMode)
        {
            bool hasStreamline = job.Scan.Upscalers.Any(u => u.Name.Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase) && u.Bitness == bits);
            if (hasStreamline) job.Log("The game ships Streamline; leaving it alone.");
            else
            {
                foreach (var (name, src) in job.Runtime.Where(kv => kv.Key.StartsWith("sl.", StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key))
                {
                    var dest = Path.Combine(job.ExeDir, name);
                    if (File.Exists(dest) || Pe.GetBitness(src) != bits) continue;
                    job.S.CopyTracked(src, dest, "streamline");
                    job.Log($"Added Streamline {name} {Pe.GetFileVersion(src)}.");
                }
            }
        }

        var addonName = multipassMode ? MultipassAddon : ConsumerAddon;
        var addonSrc = multipassMode ? job.Multipass! : job.Consumer!;
        SetAsideOtherAddons(job.S, job.ExeDir, addonName, job.MfgFile is null ? null : Components.MfgAddon);
        job.S.CopyTracked(addonSrc, Path.Combine(job.ExeDir, addonName), "addon");
        job.Log($"Added {addonName}.");

        PlaceReShade(job, feederMode: false);
        EnableAddon(job, job.ExeDir, addonName);
        job.Log("In game, open the ReShade overlay (Home key) → Add-ons tab to find and configure the add-on.");
        // Like a Dragon: Ishin!: After and Before Upscaling ran on every frame yet changed nothing on screen; only the
        // Present hook point did (the add-on's F5 before/after shots are always taken there, so they look right regardless).
        if (!multipassMode)
            job.Log("No visible change in game? In the add-on's panel (Home → Add-ons → DLSS 5 Neural Rendering), set Hook point to Present (alpha), with the game's DLSS on.");
    }

    // ------------------------------------------------------------------ Feeder

    private const string FeedTechnique = "DLSS5_Feed@DLSS5_Feed.fx";
    private const string VortTechnique = "vort_MotionEffects@vort_Motion.fx";
    private static readonly HashSet<string> ReplacedTechniqueNames = new(StringComparer.OrdinalIgnoreCase)
    { "Lumenite_Kernel", "Lumenite_QuantMotion", "vort_MotionEffects", "DLSS5_Feed", "Launchpad" };

    private static readonly (string Key, string Value)[] FeedCfgDefaults =
    {
        ("enabled", "1"), ("mode", "2"), ("hdr", "-1"), ("depth_inverted", "-1"), ("flags", "-1"), ("reset_every", "0"),
        ("warmup_rebuild", "180"), ("rebuild", "0"), ("log_frames", "3"), ("create_delay", "60"), ("preset", "0"),
        ("work_resolution", "100"), ("mv_scale_x", "1.000"), ("mv_scale_y", "1.000"), ("host_window", "0"), ("async_home", "1"),
    };

    internal static bool CanUseFeederLiveControls(InstallRequest request) =>
        request is { Route: Route.Feeder, Bitness: 64, Api: RenderApi.DX11 or RenderApi.DX12 or RenderApi.Vulkan or RenderApi.OpenGL } &&
        request.FeederBackend != FeederBackend.Lecram;

    private static bool IsDetroitExecutable(string exe) =>
        Path.GetFileName(exe).Equals("DetroitBecomeHuman.exe", StringComparison.OrdinalIgnoreCase);

    private static void RunFeeder(Job job)
    {
        var req = job.Req;
        var payload = job.Feeder!;
        bool x64 = job.Bits == 64;
        bool useHelper = !x64;
        bool useLecram = req.FeederBackend == FeederBackend.Lecram;
        bool builtInMotion = req.Api == RenderApi.OpenGL;
        if (req.Api == RenderApi.Vulkan)
        {
            AssertGameClosed(req.ExePath);
            if (ReShadeVulkanLayer.FeederUnavailableReason(req.ExePath, req.VulkanLayerManifests) is { } layerProblem)
                throw new InstallException(layerProblem);
            UseRegisteredVulkanLayer(job);
        }
        else
        {
            if (req.Api is RenderApi.DX11 or RenderApi.DX12) FixOldCompiler(job);
            PlaceReShade(job, feederMode: true);
        }
        job.S.TrackBeforeWrite(Path.Combine(job.ExeDir, "dlss5-feed.log"), "tool");

        var feedName = x64 ? "dlss5-feed.addon64" : "dlss5-feed.addon32";
        job.S.CopyTracked(x64 ? payload.Addon64 : payload.Addon32, Path.Combine(job.ExeDir, feedName), "addon");
        if (payload.DesktopPanel is { } desktopPanel)
        {
            job.S.CopyTracked(desktopPanel, Path.Combine(job.ExeDir, FeederZipImport.DesktopPanelName), "tool");
            job.Log("Added the custom Feeder desktop panel beside the Feeder add-on.");
        }
        job.S.CopyTreeTracked(payload.ShaderRoot, Path.Combine(job.ExeDir, "reshade-shaders"), "shaders");
        if (payload.Verifier is not null)
            job.S.CopyTracked(payload.Verifier, Path.Combine(job.ExeDir, Path.GetFileName(payload.Verifier)), "tool");
        job.Log($"Added {feedName} and the Feeder shaders.");

        // ReShade.ini of the game
        var iniPath = Path.Combine(job.ExeDir, "ReShade.ini");
        var ini = Ini.ReadText(iniPath);
        ini = Ini.Set(ini, "GENERAL", "EffectSearchPaths", MergeSearchPaths(Ini.Get(ini, "GENERAL", "EffectSearchPaths"), @".\reshade-shaders\Shaders\**"));
        ini = Ini.Set(ini, "GENERAL", "TextureSearchPaths", MergeSearchPaths(Ini.Get(ini, "GENERAL", "TextureSearchPaths"), @".\reshade-shaders\Textures\**"));
        var presetPath = Ini.Get(ini, "GENERAL", "PresetPath");
        if (string.IsNullOrWhiteSpace(presetPath))
        {
            presetPath = @".\ReShadePreset.ini";
            ini = Ini.Set(ini, "GENERAL", "PresetPath", presetPath);
        }
        ini = Ini.Set(ini, "GENERAL", "StartupPresetPath", "");
        ini = Ini.Set(ini, "GENERAL", "NoReloadOnInit", "0");
        ini = Ini.Set(ini, "GENERAL", "PreprocessorDefinitions", EnsureMvProvider(Ini.Get(ini, "GENERAL", "PreprocessorDefinitions"), builtInMotion));
        ini = Ini.Set(ini, "ADDON", "AddonPath", @".\");
        if (!useHelper && !useLecram)
        {
            // Reinstalling Detroit with the original Feeder moves RenoDX back into the game.
            var helperIni = Path.Combine(job.ExeDir, "host64", "ReShade.ini");
            if (IsDetroitExecutable(req.ExePath) && TracksFile(job.S.Manifest, job.S.Rel(helperIni)))
            {
                const string section = "RenoDX.DLSS5";
                foreach (var (group, key, value, _) in Ini.Entries(Ini.ReadText(helperIni)))
                    if (group.Equals(section, StringComparison.OrdinalIgnoreCase))
                        ini = Ini.Set(ini, section, key, value);
            }
            ini = ApplyConsumerSettings(ini, helper: false);
        }

        // Never edit a preset shared with other games: fall back to a local one.
        var presetFile = PresetInGame(job.S.GameDir, job.ExeDir, presetPath);
        if (presetFile is null)
        {
            job.Log($"The preset path {presetPath} points outside the game; using a local ReShadePreset.ini instead.");
            ini = Ini.Set(ini, "GENERAL", "PresetPath", @".\ReShadePreset.ini");
            presetFile = Path.Combine(job.ExeDir, "ReShadePreset.ini");
        }
        job.S.WriteTracked(iniPath, ini);

        // Preset: Feeder techniques first, then the user's own.
        var preset = ToCrlf(Ini.ReadText(presetFile));
        preset = Ini.Set(preset, "", "Techniques", MergeTechniques(Ini.Get(preset, "", "Techniques"), builtInMotion));
        preset = Ini.Set(preset, "", "TechniqueSorting", MergeTechniques(Ini.Get(preset, "", "TechniqueSorting"), builtInMotion));
        preset = Ini.Set(preset, "", "PreprocessorDefinitions", EnsureMvProvider(Ini.Get(preset, "", "PreprocessorDefinitions"), builtInMotion));
        preset = Ini.Set(preset, "DLSS5_Feed.fx", "PreprocessorDefinitions", EnsureMvProvider(Ini.Get(preset, "DLSS5_Feed.fx", "PreprocessorDefinitions"), builtInMotion));
        job.S.WriteTracked(presetFile, ToCrlf(preset));
        job.Log($"Configured ReShade.ini and {job.S.Rel(presetFile)} for the Feeder.");

        var cfgPath = Path.Combine(job.ExeDir, "dlss5-feed.cfg");
        job.S.WriteTracked(cfgPath, BuildFeedCfg(Ini.ReadText(cfgPath), payload.InGameControls is not null));

        // The neural consumer runs in a 64-bit process: the game itself, or the helper for 32-bit games.
        var hostDir = useHelper ? Path.Combine(job.ExeDir, "host64") : job.ExeDir;
        var consumerName = useLecram ? NrAddon.Addon64 : ConsumerAddon;
        var panelName = payload.InGameControls is null ? null : Components.FeederControlPanel;
        SetAsideOtherAddons(job.S, job.ExeDir, feedName, panelName, useHelper ? null : consumerName,
            job.MfgFile is null ? null : Components.MfgAddon);
        if (useHelper) SetAsideOtherAddons(job.S, hostDir, ConsumerAddon);
        if (useLecram)
        {
            SetAsideConsumerCopies(job);
            job.S.CopyTracked(Path.Combine(job.NrAddon!, NrAddon.Addon64), Path.Combine(hostDir, NrAddon.Addon64), "addon");
            var migrationBackup = Path.Combine(hostDir, "ReShade.ini.v12-backup");
            if (!File.Exists(migrationBackup)) job.S.TrackBeforeWrite(migrationBackup, "reshade");
            job.Log("Added your local DLSS5 NR as the Feeder backend. Open its panel for neural settings.");
        }
        else job.S.CopyTracked(job.Consumer!, Path.Combine(hostDir, ConsumerAddon), "addon");
        PlaceRuntime(job, job.Runtime[NeuralDll], Path.Combine(hostDir, NeuralDll), 64);
        PlaceRuntime(job, job.Runtime[DlssDll], Path.Combine(hostDir, DlssDll), 64);

        if (useHelper)
        {
            job.S.CopyTracked(payload.Host64, Path.Combine(hostDir, FeederHost64), "tool");
            job.S.TrackBeforeWrite(Path.Combine(hostDir, "ReShade.ini"), "reshade");
            job.S.TrackBeforeWrite(Path.Combine(hostDir, "ReShade.log"), "reshade");
            job.S.CopyTracked(job.ReShade64!, Path.Combine(hostDir, "dxgi.dll"), "reshade");
            var hostIniPath = Path.Combine(hostDir, "ReShade.ini");
            var hostIni = Ini.Set(Ini.ReadText(hostIniPath), "ADDON", "AddonPath", @".\");
            hostIni = ApplyConsumerSettings(hostIni, helper: true);
            job.S.WriteTracked(hostIniPath, hostIni);
            job.Log("Set up the 64-bit helper in host64.");
        }

        EnableAddon(job, job.ExeDir, feedName);
        if (panelName is not null)
        {
            job.S.CopyTracked(payload.InGameControls!, Path.Combine(job.ExeDir, panelName), "addon");
            EnableAddon(job, job.ExeDir, panelName, "DLSS 5 Feeder Controls");
            job.Log("The original Feeder and RenoDX run inside the game. Open the live Feeder controls with End or your saved shortcut.");
        }
        if (consumerName is not null)
            EnableAddon(job, hostDir, consumerName, useLecram ? job.S.Manifest.AddonName : null);
        if (payload.DesktopPanel is not null)
            job.Log("In game, use the Feeder shortcut to open its desktop panel.");
        job.Log(builtInMotion ? payload.DesktopPanel is null
                ? "OpenGL uses the Feeder's built-in motion vectors. Press Alt+C to open Feeder settings."
                : "OpenGL uses the Feeder's built-in motion vectors."
            : "In the ReShade overlay, enable DLSS5_Feed and vort_MotionEffects.");
    }

    /// <summary>
    /// The preset file a ReShade.ini's PresetPath names, resolved against the game's executable folder; null when it leads
    /// out of the game folder <paramref name="gameDir"/> (a preset shared with other games).
    /// </summary>
    private static string? PresetInGame(string gameDir, string exeDir, string presetPath)
    {
        string presetFile;
        try { presetFile = Path.GetFullPath(Path.Combine(exeDir, presetPath.Trim().Trim('"'))); }
        catch { return null; }
        return presetFile.StartsWith(gameDir + "\\", StringComparison.OrdinalIgnoreCase) ? presetFile : null;
    }

    private static string ApplyConsumerSettings(string ini, bool helper)
    {
        const string s = "RenoDX.DLSS5";
        ini = Ini.Set(ini, s, "EnableHooks", "2");
        ini = Ini.Set(ini, s, "NeuralUplift", "1");
        ini = Ini.Set(ini, s, "NREnableUpscaling", "0");
        if (Ini.Get(ini, s, "NRStyle") == "2") ini = Ini.Set(ini, s, "NRStyle", "0");
        if (helper) ini = Ini.Set(ini, s, "NRToggleKey", "0");
        return ini;
    }

    private static List<string> SplitList(string? value) =>
        (value ?? "").Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();

    /// <summary>Folder identity used to spot duplicate search paths.</summary>
    private static string PathKey(string p)
    {
        var k = p.Trim().Trim('"').Replace('/', '\\');
        while (true)
        {
            if (k.EndsWith("\\**")) k = k[..^3];
            else if (k.EndsWith("\\")) k = k[..^1];
            else break;
        }
        if (k.StartsWith(".\\")) k = k[2..];
        return k.ToLowerInvariant();
    }

    private static string CollapseRecursive(string p)
    {
        while (p.EndsWith(@"\**\**") || p.EndsWith("/**/**")) p = p[..^3];
        return p;
    }

    internal static string MergeSearchPaths(string? current, string first)
    {
        var firstKey = PathKey(first);
        var result = new List<string> { first };
        foreach (var item in SplitList(current))
        {
            if (PathKey(item) == firstKey) continue;
            var v = CollapseRecursive(item);
            if (!result.Any(r => r.Equals(v, StringComparison.OrdinalIgnoreCase))) result.Add(v);
        }
        return string.Join(",", result);
    }

    internal static string EnsureMvProvider(string? current, bool builtInMotion = false)
    {
        var result = new List<string> { builtInMotion ? "DLSS5_MV_PROVIDER=5" : "DLSS5_MV_PROVIDER=2" };
        foreach (var item in SplitList(current))
        {
            var name = item.Split('=')[0].Trim();
            if (name.Equals("DLSS5_MV_PROVIDER", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(item);
        }
        return string.Join(",", result);
    }

    internal static string MergeTechniques(string? current, bool builtInMotion = false)
    {
        var result = builtInMotion ? new List<string> { FeedTechnique } : new List<string> { VortTechnique, FeedTechnique };
        foreach (var item in SplitList(current))
        {
            var name = item.Split('@')[0].Trim();
            if (ReplacedTechniqueNames.Contains(name)) continue;
            if (!result.Contains(item, StringComparer.OrdinalIgnoreCase)) result.Add(item);
        }
        return string.Join(",", result);
    }

    private static string ToCrlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    internal static string BuildFeedCfg(string existing, bool liveControls = false)
    {
        // Keep the original renderer and give the shortcut to exactly one panel.
        var entries = Ini.Entries(existing);
        var originalKey = entries.LastOrDefault(entry => entry.key.Equals("panel_key", StringComparison.OrdinalIgnoreCase)).value;
        var liveKey = entries.LastOrDefault(entry => entry.key.Equals("live_panel_key", StringComparison.OrdinalIgnoreCase)).value;
        var updatePanelKey = liveControls || liveKey is not null;
        var shortcut = (originalKey == "0" ? liveKey ?? originalKey : originalKey) ?? "35";

        var known = FeedCfgDefaults.ToDictionary(d => d.Key, d => d.Value, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var raw in existing.Replace("\r\n", "\n").Split('\n'))
        {
            int eq = raw.IndexOf('=');
            var t = raw.TrimStart();
            if (eq > 0 && !t.StartsWith(';') && !t.StartsWith('#'))
            {
                var key = raw[..eq].Trim();
                if (updatePanelKey && (key.Equals("panel_key", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("live_panel_key", StringComparison.OrdinalIgnoreCase))) continue;
                if (known.ContainsKey(key))
                {
                    lines.Add($"{key.ToLowerInvariant()}={raw[(eq + 1)..].Trim()}");
                    seen.Add(key);
                    continue;
                }
            }
            lines.Add(raw);
        }
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        foreach (var (key, value) in FeedCfgDefaults)
            if (!seen.Contains(key)) lines.Add($"{key}={value}");
        if (updatePanelKey)
        {
            lines.Add($"panel_key={(liveControls ? "0" : shortcut)}");
            if (liveControls) lines.Add($"live_panel_key={shortcut}");
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    // ------------------------------------------------------------------ OptiScaler

    internal static readonly HashSet<string> OptiConflictStems = new(StringComparer.OrdinalIgnoreCase)
    { "dxgi", "winmm", "version", "dbghelp", "dbgcore", "d3d12", "d3d11", "wininet", "winhttp", "OptiScaler" };

    private static void RunOptiScaler(Job job)
    {
        var build = job.Build!;
        var hook = job.Req.OptiHook;
        var pkg = job.OptiFolder!;
        var exeDir = job.ExeDir;

        // Another proxy DLL or ASI loader in the way?
        foreach (var f in Directory.EnumerateFiles(exeDir).ToList())
        {
            var name = Path.GetFileName(f);
            var ext = Path.GetExtension(name);
            bool candidate = ext.Equals(".asi", StringComparison.OrdinalIgnoreCase)
                             || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && OptiConflictStems.Contains(Path.GetFileNameWithoutExtension(name));
            if (!candidate || job.S.WasAdded(job.S.Rel(f))) continue;
            if ((name.Equals("dbghelp.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("dbgcore.dll", StringComparison.OrdinalIgnoreCase))
                && Pe.VersionMentions(f, "Microsoft"))
                continue;
            bool isReShade = Pe.VersionMentions(f, "ReShade");
            if (isReShade && !name.Equals(hook, StringComparison.OrdinalIgnoreCase)) continue;
            if (isReShade)
            {
                var alt = OptiHookNames.FirstOrDefault(h => !h.Equals(hook, StringComparison.OrdinalIgnoreCase) && h.EndsWith(".dll") && !File.Exists(Path.Combine(exeDir, h))) ?? "winmm.dll";
                throw new InstallException($"{name} is ReShade and uses the same file name as the OptiScaler hook. Choose another OptiScaler DLL name (e.g. {alt}).");
            }
            throw new InstallException($"{name} next to the game belongs to another mod or loader (maybe an older OptiScaler). Remove it, or restore it in the tool that installed it, then try again.");
        }

        if (build.NeuralRendering)
        {
            var forwarder = Path.Combine(exeDir, ForwarderDll);
            if (!build.Forwarded && File.Exists(forwarder))
            {
                job.S.DeleteTracked(forwarder, "optiscaler");
                job.Log($"Removed {ForwarderDll}: it would silently disable this build's neural pass.");
            }
            var library = Path.Combine(pkg, "OptiScaler.dll");
            job.S.CopyTracked(library, Path.Combine(exeDir, hook), "optiscaler");
            if (build.Forwarded)
            {
                var src = Path.Combine(pkg, ForwarderDll);
                if (!File.Exists(src)) throw new InstallException($"The {build.Label} package is missing {ForwarderDll}.");
                job.S.CopyTracked(src, forwarder, "optiscaler");
            }
            var backend = Path.Combine(pkg, "OptiScaler");
            if (build.WholeBackend && Directory.Exists(backend))
                job.S.CopyTreeTracked(backend, Path.Combine(exeDir, "OptiScaler"), "optiscaler");
            else
                foreach (var lib in Components.OptiLibraries)
                {
                    var src = Path.Combine(backend, lib);
                    if (File.Exists(src)) job.S.CopyTracked(src, Path.Combine(exeDir, "OptiScaler", lib), "optiscaler");
                }
            var licensesDir = Path.Combine(exeDir, "OptiScaler", "licenses");
            var pkgLicenses = Path.Combine(pkg, "Licenses");
            if (Directory.Exists(pkgLicenses)) job.S.CopyTreeTracked(pkgLicenses, licensesDir, "license");
            var parent = Path.GetDirectoryName(pkg);
            var gpl = new[]
            {
                Path.Combine(pkg, "LICENSE"),
                Path.Combine(pkg, "OptiScaler-GPL-3.0.txt"),
                parent is null ? null : Path.Combine(parent, "OptiScaler-GPL-3.0.txt"),
            }.FirstOrDefault(p => p is not null && File.Exists(p));
            if (gpl is not null) job.S.CopyTracked(gpl, Path.Combine(licensesDir, "LICENSE.GPL-3.0.txt"), "license");

            var model = Path.Combine(exeDir, NeuralDll);
            if (job.Runtime.TryGetValue(NeuralDll, out var nr) &&
                (job.PreferNeuralModel || Components.IsBundledNvidiaModel(model)))
                PlaceRuntime(job, nr, model, job.Bits);
            else if (File.Exists(model))
                job.Log($"Keeping the {NeuralDll} already next to the game ({Pe.GetFileVersion(model)}).");
            else if (nr is not null)
            {
                job.S.CopyTracked(nr, model, "runtime");
                job.Log($"Added {NeuralDll} {Pe.GetFileVersion(nr)}.");
            }
        }
        else
        {
            // Official build: mirror the folder, with OptiScaler.dll renamed to the hook.
            foreach (var f in Directory.EnumerateFiles(pkg, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith("setup_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("remove", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rel = Path.GetRelativePath(pkg, f);
                var dest = rel.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? Path.Combine(exeDir, hook) : Path.Combine(exeDir, rel);
                job.S.CopyTracked(f, dest, "optiscaler");
            }
        }
        job.Log($"Installed OptiScaler ({build.Label}) as {hook}.");

        // OptiScaler.ini
        var iniPath = Path.Combine(exeDir, "OptiScaler.ini");
        var ini = File.Exists(iniPath) ? Ini.ReadText(iniPath) : Ini.ReadText(Path.Combine(pkg, "OptiScaler.ini"));
        ini = Ini.Set(ini, "Menu", "ShortcutKey", job.Req.MenuKey);
        if (build.NeuralRendering)
        {
            ini = Ini.Set(ini, "DlssNr", "Enabled", "true");
            ini = Ini.Set(ini, "Log", "LogToFile", "true");
            ini = Ini.Set(ini, "Log", "LogLevel", "2");
            ini = Ini.Set(ini, "Spoofing", "Dxgi", "false");
            ini = Ini.Set(ini, "Plugins", "LoadAsiPlugins", "false");
            ini = Ini.Set(ini, "ProcessFilter", "TargetProcessName", Path.GetFileName(job.Req.ExePath));
            var dx12 = Ini.Get(ini, "Upscalers", "Dx12Upscaler");
            if (string.IsNullOrEmpty(dx12) || dx12.Equals("auto", StringComparison.OrdinalIgnoreCase))
                ini = Ini.Set(ini, "Upscalers", "Dx12Upscaler", "dlss");
            foreach (var key in new[] { "Dx11Upscaler", "VulkanUpscaler" })
            {
                var v = Ini.Get(ini, "Upscalers", key);
                if (string.IsNullOrEmpty(v) || v.Equals("auto", StringComparison.OrdinalIgnoreCase) || !v.EndsWith("_12", StringComparison.OrdinalIgnoreCase))
                    ini = Ini.Set(ini, "Upscalers", key, "ffx_12");
            }
        }
        job.S.WriteTracked(iniPath, ini);
        job.S.TrackBeforeWrite(Path.Combine(exeDir, "OptiScaler.log"), "optiscaler");
        job.Log($"OptiScaler menu key: {KeyName(job.Req.MenuKey)}. Press it in game to open the OptiScaler overlay.");
    }

    private static string KeyName(string code) =>
        MenuKeys.FirstOrDefault(k => k.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Name ?? code;

    // ------------------------------------------------------------------ MFG unlock

    private static void RunMfg(Job job)
    {
        job.S.Manifest.MfgUnlock = Pe.GetFileVersion(job.MfgFile!) ?? "unknown";
        job.S.Manifest.MfgUnlockSha256 = job.MfgProviders!.BackendSha256;
        // Record settings before the add-on creates or changes them in game.
        // Restore must not depend on NTFS creation times (which can be reused).
        foreach (var name in new[] { "MFGAdaUnlock.ini", "MFGAdaUnlock.profiles.json" })
            job.S.TrackBeforeWrite(Path.Combine(job.ExeDir, name), "settings");
        job.S.CopyTracked(job.MfgFile!, Path.Combine(job.ExeDir, Components.MfgAddon), "addon");
        job.Log($"Added the MFG unlock add-on {job.S.Manifest.MfgUnlock}.");

        var fresh = Scanner.Scan(job.S.GameDir, job.Req.ExePath);
        var frameGen = fresh.Upscalers.Where(u => u.Kind == UpscalerKind.DlssFG && u.Bitness == 64).ToList();
        if (job.MfgProviders is not null) job.MfgProviders.Place(job.S);
        else if (job.Runtime.TryGetValue(FrameGenDll, out var fgSrc))
            foreach (var f in frameGen) PlaceRuntime(job, fgSrc, f.Path, 64);
        foreach (var f in frameGen)
        {
            var v = Pe.GetFileVersion(f.Path);
            if (Scanner.CompareVersions(v, "310.0.0.0") < 0)
                job.Log($"Warning: {f.Rel} is {v}; the MFG unlock wants DLSS Frame Generation 310.x. Use the Swap button to update it.");
        }
        EnableAddon(job, job.ExeDir, Components.MfgAddon);
        job.Log($"MFG backend disk identity: version={Pe.GetFileVersion(job.MfgFile!)}; SHA256={NativeMfgProviders.Hash(job.MfgFile!)}. Existing game-controlled settings are preserved.");
        job.Log("Native menu capacity, requested generated-frame count, API acceptance and distinct presented output require separate live evidence. Installation is not proof of 4x/6x, Dynamic MFG support, or pre-query hook readiness.");
    }

    // ------------------------------------------------------------------ other operations

    public static Task RestoreAsync(string gameDir, string? exePath, Action<string> log) => Task.Run(() =>
    {
        NeuralMasterProtection.Check(gameDir, exePath);
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            AssertGameClosed(exePath);
            ReleaseDetroitFeederHelper(gameDir, exePath, log);
        }
        if (NrSetupAfterUs(gameDir))
            throw new InstallException(NrAddon.SetupAfterUsMessage(Journal.ReadManifest(gameDir) is { } m ? AddonIdentity.InstalledName(gameDir, m) : "DLSS5 NR"));
        if (InstallTransaction.Pending(gameDir)) InstallTransaction.Recover(gameDir);
        using var transaction = InstallTransaction.Begin(gameDir);
        try
        {
            NeuralMasterProtection.Check(gameDir, exePath);
            if (Journal.ReadManifest(gameDir) is { } old) RouteProfiles.Save(gameDir, old, log);
            if (!Session.Restore(gameDir, log)) log("Nothing to restore: DLSS 5 Master has no install record in this game.");
            transaction.Commit();
        }
        catch { transaction.Rollback(); throw; }
    });

    /// <summary>
    /// DLSS5 NR's own setup installed NR in the game after this app's install: its record then names this app's files as the
    /// originals it puts back, so a restore here first would leave its Remove NR putting them back again.
    /// </summary>
    public static bool NrSetupAfterUs(string gameDir)
    {
        try
        {
            if (Journal.ReadManifest(gameDir) is not { Exe.Length: > 0 } m) return false;
            var exeDir = Path.GetDirectoryName(Journal.SafePath(Path.GetFullPath(gameDir).TrimEnd('\\', '/'), m.Exe));
            if (exeDir is null || !NrAddon.SetupInstalled(exeDir)) return false;
            var ours = m.Date.Kind == DateTimeKind.Local ? m.Date.ToUniversalTime() : m.Date;
            return File.GetLastWriteTimeUtc(NrAddon.SetupRecord(exeDir)) > ours;
        }
        catch (InstallException) { return false; }
    }

    /// <summary>The size of <paramref name="files"/> and of every file in <paramref name="trees"/>, each counted once; missing ones count 0.</summary>
    internal static long SpaceNeeded(IEnumerable<string?> files, IEnumerable<string?> trees)
    {
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string file)
        {
            try { if (seen.Add(Path.GetFullPath(file))) total += new FileInfo(file).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        foreach (var f in files) if (f is not null) Add(f);
        foreach (var d in trees)
        {
            if (d is null || !Directory.Exists(d)) continue;
            try { foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) Add(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    /// <summary>
    /// Refuses a setup the game's drive has no room for, before anything in the game changes. What an earlier setup added
    /// counts as room: the restore that starts a reinstall takes it out first. Skipped when the free space cannot be read.
    /// </summary>
    internal static void AssertRoom(string gameDir, string exeDir, Manifest? old, long needed)
    {
        if (!GetDiskFreeSpaceEx(Path.GetFullPath(exeDir), out var free, out _, out _)) return;
        long freed = 0;
        foreach (var rel in old?.Added ?? new List<string>())
        {
            try
            {
                var file = Journal.SafePath(gameDir, rel);
                if (File.Exists(file)) freed += new FileInfo(file).Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstallException) { }
        }
        const long margin = 64L << 20;
        long room = (long)Math.Min(free, (ulong)(long.MaxValue / 2)) + freed;
        if (room >= needed + margin) return;
        static string Mb(long bytes) => (Math.Max(bytes, 0) / 1048576).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB";
        var drive = Path.GetPathRoot(Path.GetFullPath(exeDir))?.TrimEnd('\\') ?? exeDir;
        throw new InstallException($"Not enough free space on {drive}: {Mb((long)Math.Min(free, (ulong)long.MaxValue))} free, and this setup needs about " +
                                   $"{Mb(needed + margin - freed)}. Free some space, then try again. Nothing in the game was changed.");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);

    private static bool TracksFile(Manifest record, string relativePath) =>
        record.Added.Contains(relativePath, StringComparer.OrdinalIgnoreCase) ||
        record.Replaced.Any(file => file.Rel.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

    private static void ReleaseDetroitFeederHelper(string gameDir, string exePath, Action<string> log)
    {
        if (!IsDetroitExecutable(exePath) || Journal.ReadManifest(gameDir) is not { Route: "feeder" } old) return;
        var executable = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exePath))!, "host64", FeederHost64);
        var relative = Path.GetRelativePath(gameDir, executable);
        if (!TracksFile(old, relative)) return;

        foreach (var helper in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(FeederHost64)))
        {
            using (helper)
            {
                string? path;
                try { path = helper.MainModule?.FileName; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { continue; }
                if (!string.Equals(path, executable, StringComparison.OrdinalIgnoreCase)) continue;
                var pid = helper.Id;
                try
                {
                    if (helper.WaitForExit(1200)) continue;

                    // A helper from an older build can survive if Detroit closes during its startup.
                    AssertGameClosed(exePath);
                    helper.Kill();
                    if (!helper.WaitForExit(4000))
                        throw new InstallException($"Detroit's Feeder helper (PID {pid}) is still running. End it in Task Manager and retry.");
                    log($"Closed the Feeder helper left running after Detroit exited (PID {pid}).");
                }
                catch (InvalidOperationException) { } // exited between checks
                catch (Win32Exception ex)
                {
                    throw new InstallException($"Could not close Detroit's Feeder helper (PID {pid}). End it in Task Manager and retry. {ex.Message}");
                }
            }
        }
    }

    public static void AssertGameClosed(string exePath)
    {
        var full = Path.GetFullPath(exePath);
        var name = Path.GetFileNameWithoutExtension(full);
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                bool running;
                try
                {
                    var module = p.MainModule?.FileName;
                    running = module is null || Path.GetFullPath(module).Equals(full, StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidOperationException) { running = false; } // it exited meanwhile
                catch (Win32Exception) { running = true; }            // cannot read it: assume it is the game
                catch (NotSupportedException) { running = true; }
                if (running) throw new InstallException($"{Path.GetFileName(full)} is running. Close the game first.");
            }
        }
    }

    public static void SetOptiMenuKey(string gameDir, string exeDir, string code, Action<string> log)
    {
        NeuralMasterProtection.Check(gameDir, Path.Combine(exeDir, "game.exe"));
        var iniPath = Path.Combine(exeDir, "OptiScaler.ini");
        if (!File.Exists(iniPath)) throw new InstallException("OptiScaler.ini was not found next to the game. Install OptiScaler first.");
        var text = Ini.Set(Ini.ReadText(iniPath), "Menu", "ShortcutKey", code);
        var manifest = Journal.ReadManifest(gameDir);
        if (manifest is not null)
        {
            var s = new Session(gameDir, manifest, log);
            s.WriteTracked(iniPath, text);
            s.Save();
        }
        else
        {
            Journal.ClearReadOnly(iniPath);
            File.WriteAllText(iniPath, text);
        }
        var msg = $"OptiScaler menu key set to {KeyName(code)}.";
        log(msg);
        AppPaths.Log($"[{Path.GetFileName(Path.GetFullPath(gameDir).TrimEnd('\\'))}] {msg}");
    }

    public static void SwapDll(string gameDir, string exePath, UpscalerFile target, string source, Action<string> log)
    {
        NeuralMasterProtection.Check(gameDir, exePath);
        AssertGameClosed(exePath);
        if (!Path.GetFileName(source).Equals(target.Name, StringComparison.OrdinalIgnoreCase))
            throw new InstallException($"{Path.GetFileName(source)} cannot replace {target.Name}: the file names differ.");
        int srcBits = Pe.GetBitness(source);
        if (srcBits != target.Bitness)
            throw new InstallException($"{target.Name} in the game is {target.Bitness}-bit but the chosen file is {(srcBits == 0 ? "not a valid DLL" : srcBits + "-bit")}.");

        var manifest = Journal.ReadManifest(gameDir);
        var session = manifest is not null
            ? new Session(gameDir, manifest, log)
            : Session.Begin(gameDir, exePath, "swap", ApiKey(Scanner.ApiFromImports(Pe.GetImports(exePath))), Pe.GetBitness(exePath), log);
        var oldVersion = Pe.GetFileVersion(target.Path) ?? target.Version ?? "?";
        session.CopyTracked(source, target.Path, "swap");
        session.Save();
        session.Log($"Swapped {target.Rel}: {oldVersion} → {Pe.GetFileVersion(source) ?? "?"}.");
    }

    public static List<(string Path, string Version, string Source)> SwapCandidates(string name, Components components)
    {
        var all = new List<(string Path, string Version, string Source)>();
        if (components.RuntimeFiles(includeNvidiaModel: name.Equals(NeuralDll, StringComparison.OrdinalIgnoreCase)).TryGetValue(name, out var rt))
            all.Add((rt, Pe.GetFileVersion(rt) ?? "unknown", "Runtime folder"));
        var libDir = Path.Combine(AppPaths.DllLibrary, name.ToLowerInvariant());
        try
        {
            if (Directory.Exists(libDir))
                foreach (var f in Directory.EnumerateFiles(libDir, name, SearchOption.AllDirectories))
                    all.Add((f, Pe.GetFileVersion(f) ?? "unknown", "DLL library"));
        }
        catch { }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<(string Path, string Version, string Source)>();
        foreach (var c in all)
            if (seen.Add($"{c.Version}|{Pe.GetBitness(c.Path)}")) unique.Add(c);
        return unique.OrderByDescending(c => c, Comparer<(string Path, string Version, string Source)>.Create((a, b) => Scanner.CompareVersions(a.Version, b.Version))).ToList();
    }

    /// <summary>
    /// The DLL library's folder for one version: "&lt;version&gt;-x&lt;bits&gt;". The version is the DLL's own version text, which
    /// a DLL can set to anything, so path characters are replaced and it always names one folder inside the library.
    /// </summary>
    internal static string LibraryFolder(string version, int bits) =>
        $"{System.Text.RegularExpressions.Regex.Replace(version, @"[^\w.\-]+", "_")}-x{bits}";

    public static string ImportDll(string file)
    {
        var name = Path.GetFileName(file);
        if (Scanner.Classify(name) is null)
            throw new InstallException($"{name} is not a DLSS, Streamline, FSR or XeSS DLL.");
        var version = Pe.GetFileVersion(file) ?? "unknown";
        var bits = Pe.GetBitness(file);
        var dest = Path.Combine(AppPaths.DllLibrary, name.ToLowerInvariant(), LibraryFolder(version, bits), name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (!Path.GetFullPath(file).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            Journal.ClearReadOnly(dest);
            File.Copy(file, dest, true);
        }
        return dest;
    }
}
