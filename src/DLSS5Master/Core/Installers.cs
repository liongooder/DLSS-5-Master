using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DLSS5Master.Core;

public enum Backend { ReShade, OptiScaler }

public enum Route { Native, Multipass, Feeder, OptiScaler }

public sealed class InstallRequest
{
    public required string GameDir { get; init; }
    public required string ExePath { get; init; }
    public required RenderApi Api { get; init; }
    public required int Bitness { get; init; }
    public required Route Route { get; init; }
    public OptiBuild? OptiBuild { get; init; }
    public string? OptiCustomFolder { get; init; }
    public string OptiHook { get; init; } = "dxgi.dll";
    public string MenuKey { get; init; } = "0x2D";
    public string ReShadeProxy { get; init; } = "dxgi.dll";
    public bool AddMissingDlss { get; init; }
    public bool MfgUnlock { get; init; }
    public bool AntiCheatAcknowledged { get; init; }
}

public sealed record RouteOption(Route Route, string Label, string Description, string? Unavailable);

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
    private const string MultipassAddon = "renodx-dlss.addon64";
    private const string ForwarderDll = "nvngx.dll_dlssnr.dll";

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
        _ => "optiscaler",
    };

    public static string RouteLabel(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "none";
        return key.ToLowerInvariant() switch
        {
            "native" => "ReShade · Native DLSS-NR",
            "multipass" or "renodx" => "ReShade · RenoDX DLSS Tool (multipass)",
            "feeder" => "ReShade · DLSS5 Feeder",
            "optiscaler" => "OptiScaler",
            "swap" => "DLL swap only",
            _ => key,
        };
    }

    // ------------------------------------------------------------------ route availability

    public static string? MfgUnavailable(RenderApi api, int bitness, bool hasFrameGen)
    {
        if (bitness != 64) return "The MFG unlock add-on only works with 64-bit games.";
        if (api != RenderApi.DX12) return "The MFG unlock add-on needs a DirectX 12 game (its Vulkan support is experimental and not offered).";
        if (!hasFrameGen) return "This game has no DLSS Frame Generation (nvngx_dlssg.dll), which the MFG unlock builds on.";
        return null;
    }

    public static List<RouteOption> RoutesFor(RenderApi api, int bitness, bool hasNativeDlss, bool hasFrameGen = false)
    {
        bool x64 = bitness == 64;
        const string need64 = "Needs a 64-bit game.";

        string? native = !x64 ? need64 : api != RenderApi.DX12 ? "Needs a DirectX 12 game." : null;

        string? multipass = !x64 ? need64
            : api is RenderApi.DX9 or RenderApi.DX11 or RenderApi.DX12 ? null
            : "Needs a DirectX 9, 11 or 12 game.";

        string? feeder = api switch
        {
            RenderApi.DX11 or RenderApi.DX12 => null,
            RenderApi.Vulkan => "Vulkan games need a global Vulkan layer for the Feeder; not supported yet.",
            RenderApi.DX9 or RenderApi.DX8 or RenderApi.DirectDraw => "This API needs the dgVoodoo wrapper for the Feeder; not supported yet.",
            _ => "Needs a DirectX 11 or 12 game.",
        };

        string? opti = !x64 ? need64
            : api is RenderApi.DX11 or RenderApi.DX12 or RenderApi.Vulkan ? null
            : "Needs a DirectX 11, DirectX 12 or Vulkan game.";

        var optiText = "Loads OptiScaler straight into the game, without ReShade, and runs DLSS neural rendering through it.";
        if (!hasNativeDlss)
            optiText += " The DLSS-NR builds rely on the game's own DLSS, which this game does not ship; use the custom (official) build for upscaling only.";
        var multipassText = "ReShade with the RenoDX DLSS Tool, which runs several neural passes. Streamline is added when the game has none of its own.";
        if (hasFrameGen) multipassText += " The game's DLSS Frame Generation keeps working.";

        return new List<RouteOption>
        {
            new(Route.Native, "Native DLSS-NR (RenoDX DLSS5)",
                "ReShade plus the RenoDX DLSS5 add-on and NVIDIA's neural runtime. The best fit for DirectX 12 games that already use DLSS.", native),
            new(Route.Multipass, "RenoDX DLSS Tool (multipass)", multipassText, multipass),
            new(Route.Feeder, "DLSS5 Feeder",
                "For games without DLSS: shaders compute motion vectors and feed them through ReShade to the neural pass.", feeder),
            new(Route.OptiScaler, "OptiScaler", optiText, opti),
        };
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
        public string? ReShade { get; init; }
        public string? ReShade64 { get; init; }
        public string? Consumer { get; init; }
        public string? Multipass { get; init; }
        public Components.FeederPayload? Feeder { get; init; }
        public string? OptiFolder { get; init; }
        public OptiBuild? Build { get; init; }
        public string? MfgFile { get; init; }
        public required GameScan Scan { get; set; }
        public int Bits => Req.Bitness;
        public void Log(string line) => S.Log(line);
    }

    public static async Task InstallAsync(InstallRequest req, Components components, Action<string> log, IProgress<string>? progress, CancellationToken ct)
    {
        var gameDir = Path.GetFullPath(req.GameDir).TrimEnd('\\', '/');
        var exePath = Path.GetFullPath(req.ExePath);
        var exeDir = Path.GetDirectoryName(exePath)!;

        // ---- 1. preconditions (nothing downloaded or changed yet)
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
        AssertGameClosed(exePath);

        progress?.Report("Scanning the game…");
        var scan = await Task.Run(() => Scanner.Scan(gameDir, exePath, ct), ct);
        if (scan.AntiCheat && !req.AntiCheatAcknowledged)
            throw new InstallException("This game uses anti-cheat (EasyAntiCheat/BattlEye). Injecting ReShade or OptiScaler into it can get your account banned, especially online. Tick the acknowledgement to continue anyway.");

        var option = RoutesFor(req.Api, req.Bitness, scan.HasNativeDlss, scan.HasFrameGen).First(o => o.Route == req.Route);
        if (option.Unavailable is not null)
            throw new InstallException($"{option.Label} cannot be used here: {option.Unavailable}");

        bool wantMfg = req.MfgUnlock && req.Route != Route.OptiScaler;
        if (wantMfg && MfgUnavailable(req.Api, req.Bitness, scan.HasFrameGen) is { } mfgWhy)
            throw new InstallException(mfgWhy);

        bool reshadeRoute = req.Route != Route.OptiScaler;
        var build = req.OptiBuild ?? Components.OptiBuilds[0];
        var runtime = components.RuntimeFiles();

        // Local prerequisites first, so we do not download anything that could not be used.
        if (reshadeRoute && !runtime.ContainsKey(NeuralDll))
            throw new InstallException($"The NVIDIA neural runtime ({NeuralDll}) was not found. Pick the NVIDIA runtime folder in Settings.");
        if (req.Route == Route.OptiScaler && build.NeuralRendering && !runtime.ContainsKey(NeuralDll) && !File.Exists(Path.Combine(exeDir, NeuralDll)))
            throw new InstallException($"This OptiScaler build needs the NVIDIA neural runtime ({NeuralDll}). Pick the NVIDIA runtime folder in Settings.");
        if (req.Route == Route.Feeder)
        {
            if (req.Api is not (RenderApi.DX11 or RenderApi.DX12))
                throw new InstallException("The Feeder route supports DirectX 11 and 12 games only in this version.");
            if (!runtime.ContainsKey(DlssDll))
                throw new InstallException($"The Feeder route needs {DlssDll} in the NVIDIA runtime folder. Pick it in Settings.");
        }
        string? reshade = null, reshade64 = null;
        if (reshadeRoute)
        {
            reshade = components.ReShadeDll(req.Bitness)
                      ?? throw new InstallException($"No {req.Bitness}-bit ReShade with add-on support was found. Pick a ReShade_Setup_*_Addon.exe in Settings.");
            if (req.Route == Route.Feeder && req.Bitness != 64)
                reshade64 = components.ReShadeDll(64)
                            ?? throw new InstallException("The Feeder's 64-bit helper needs a 64-bit ReShade with add-on support. Pick a ReShade_Setup_*_Addon.exe in Settings.");
        }
        if (req.Route == Route.OptiScaler && build.NeuralRendering && !scan.HasNativeDlss)
            log("Warning: this game has no DLSS of its own; DLSS-NR OptiScaler builds may not activate.");

        // ---- 2. fetch everything before touching the game
        string? consumer = null, multipass = null, opti = null, mfg = null;
        Components.FeederPayload? feeder = null;
        switch (req.Route)
        {
            case Route.Native:
                consumer = await components.RenoDxConsumerAsync(progress, ct);
                break;
            case Route.Multipass:
                multipass = await components.MultipassAddonAsync(progress, ct);
                break;
            case Route.Feeder:
                feeder = await components.FeederAsync(progress, ct);
                consumer = await components.RenoDxConsumerAsync(progress, ct);
                break;
            case Route.OptiScaler:
                opti = await components.OptiScalerAsync(build, req.OptiCustomFolder, progress, ct);
                break;
        }
        if (wantMfg) mfg = await components.MfgUnlockAsync(progress, ct);
        ct.ThrowIfCancellationRequested();

        // ---- 3. undo an earlier install by this app
        if (Journal.ReadManifest(gameDir) is not null)
        {
            log("Restoring the previous install by DLSS 5 Master first…");
            progress?.Report("Restoring the previous install…");
            await Task.Run(() => Session.Restore(gameDir, log));
        }

        // ---- 4. install
        progress?.Report("Installing…");
        var session = Session.Begin(gameDir, exePath, RouteKey(req.Route), ApiKey(req.Api), req.Bitness, log);
        session.Save();
        await Task.Run(() =>
        {
            var job = new Job
            {
                Req = req, S = session, ExeDir = exeDir, Runtime = runtime,
                ReShade = reshade, ReShade64 = reshade64, Consumer = consumer, Multipass = multipass,
                Feeder = feeder, OptiFolder = opti, Build = build, MfgFile = mfg,
                // Fresh look at the folder now that any earlier install is gone.
                Scan = Scanner.Scan(gameDir, exePath),
            };
            switch (req.Route)
            {
                case Route.Native: RunRenoDx(job, multipassMode: false); break;
                case Route.Multipass: RunRenoDx(job, multipassMode: true); break;
                case Route.Feeder: RunFeeder(job); break;
                case Route.OptiScaler: RunOptiScaler(job); break;
            }
            if (wantMfg) RunMfg(job);
            session.Save();
        });
        session.Log($"Done: {RouteLabel(RouteKey(req.Route))} installed. Restore originals undoes every change.");
    }

    // ------------------------------------------------------------------ shared steps

    private static string ReShadeHookFor(InstallRequest req) => req.Api switch
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
            if (!current.AddonSupport)
                throw new InstallException($"ReShade is loaded here as an ASI plugin ({current.File}) without add-on support. Replace it with the add-on build of ReShade, or remove it.");
            job.Log($"Keeping the existing ReShade ASI ({current.File}); it has add-on support.");
            job.S.Manifest.ReShadeHook = current.File;
            return;
        }

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
            if (current.AddonSupport && Pe.GetBitness(existing) == job.Bits)
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
    private static void EnableAddon(Job job, string dir, string addonFile)
    {
        var ini = Path.Combine(dir, "ReShade.ini");
        if (!File.Exists(ini)) return;
        var text = Ini.ReadText(ini);
        var disabled = Ini.Get(text, "ADDON", "DisabledAddons");
        if (string.IsNullOrEmpty(disabled)) return;
        var stem = AddonStem(addonFile);
        var items = disabled.Split(',').Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
        var kept = items.Where(i => !i.Equals(stem, StringComparison.OrdinalIgnoreCase) && !i.Equals(addonFile, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count == items.Count) return;
        job.S.WriteTracked(ini, Ini.Set(text, "ADDON", "DisabledAddons", string.Join(",", kept)));
        job.Log($"Re-enabled {stem} in {job.S.Rel(ini)}.");
    }

    /// <summary>Only one RenoDX neural consumer may be loaded at a time.</summary>
    private static void OneConsumer(Job job, string dir, string keep)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "renodx-dlss*.addon64").ToList())
        {
            if (Path.GetFileName(f).Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
            job.S.DeleteTracked(f, "addon");
            job.Log($"Removed {Path.GetFileName(f)}: only one RenoDX neural add-on can be active.");
        }
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

    /// <summary>Put an NVIDIA runtime DLL in place without ever downgrading or mixing bitness.</summary>
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

    // ------------------------------------------------------------------ Native / Multipass

    private static void RunRenoDx(Job job, bool multipassMode)
    {
        var bits = job.Bits;
        FixOldCompiler(job);

        foreach (var u in job.Scan.Upscalers.Where(u => u.Kind is UpscalerKind.DlssSR or UpscalerKind.DlssNR && u.Bitness == bits).ToList())
            if (job.Runtime.TryGetValue(u.Name, out var src)) PlaceRuntime(job, src, u.Path, bits);

        PlaceRuntime(job, job.Runtime[NeuralDll], Path.Combine(job.ExeDir, NeuralDll), bits);
        if (job.Req.AddMissingDlss && !job.Scan.Upscalers.Any(u => u.Kind == UpscalerKind.DlssSR && u.Bitness == bits))
        {
            if (job.Runtime.TryGetValue(DlssDll, out var dlss)) PlaceRuntime(job, dlss, Path.Combine(job.ExeDir, DlssDll), bits);
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
        OneConsumer(job, job.ExeDir, addonName);
        job.S.CopyTracked(addonSrc, Path.Combine(job.ExeDir, addonName), "addon");
        job.Log($"Added {addonName}.");

        PlaceReShade(job, feederMode: false);
        EnableAddon(job, job.ExeDir, addonName);
        job.Log("In game, open the ReShade overlay (Home key) → Add-ons tab to find and configure the add-on.");
    }

    // ------------------------------------------------------------------ Feeder

    private static readonly string[] FeedTechniques = { "vort_MotionEffects@vort_Motion.fx", "DLSS5_Feed@DLSS5_Feed.fx" };
    private static readonly HashSet<string> ReplacedTechniqueNames = new(StringComparer.OrdinalIgnoreCase)
    { "Lumenite_Kernel", "Lumenite_QuantMotion", "vort_MotionEffects", "DLSS5_Feed", "Launchpad" };

    private static readonly (string Key, string Value)[] FeedCfgDefaults =
    {
        ("enabled", "1"), ("mode", "2"), ("hdr", "-1"), ("depth_inverted", "-1"), ("flags", "-1"), ("reset_every", "0"),
        ("warmup_rebuild", "180"), ("rebuild", "0"), ("log_frames", "3"), ("create_delay", "60"), ("preset", "0"),
        ("work_resolution", "100"), ("mv_scale_x", "1.000"), ("mv_scale_y", "1.000"), ("host_window", "0"), ("async_home", "1"),
    };

    private static void RunFeeder(Job job)
    {
        var req = job.Req;
        var payload = job.Feeder!;
        bool x64 = job.Bits == 64;
        if (req.Api is not (RenderApi.DX11 or RenderApi.DX12))
            throw new InstallException("The Feeder route supports DirectX 11 and 12 games only in this version.");

        FixOldCompiler(job);
        PlaceReShade(job, feederMode: true);

        var feedName = x64 ? "dlss5-feed.addon64" : "dlss5-feed.addon32";
        job.S.CopyTracked(x64 ? payload.Addon64 : payload.Addon32, Path.Combine(job.ExeDir, feedName), "addon");
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
        ini = Ini.Set(ini, "GENERAL", "PreprocessorDefinitions", EnsureMvProvider(Ini.Get(ini, "GENERAL", "PreprocessorDefinitions")));
        ini = Ini.Set(ini, "ADDON", "AddonPath", @".\");
        if (x64) ini = ApplyConsumerSettings(ini, helper: false);

        // Never edit a preset shared with other games: fall back to a local one.
        string presetFile;
        try { presetFile = Path.GetFullPath(Path.Combine(job.ExeDir, presetPath.Trim().Trim('"'))); }
        catch { presetFile = ""; }
        if (presetFile.Length == 0 || !presetFile.StartsWith(job.S.GameDir + "\\", StringComparison.OrdinalIgnoreCase))
        {
            job.Log($"The preset path {presetPath} points outside the game; using a local ReShadePreset.ini instead.");
            ini = Ini.Set(ini, "GENERAL", "PresetPath", @".\ReShadePreset.ini");
            presetFile = Path.Combine(job.ExeDir, "ReShadePreset.ini");
        }
        job.S.WriteTracked(iniPath, ini);

        // Preset: Feeder techniques first, then the user's own.
        var preset = ToCrlf(Ini.ReadText(presetFile));
        preset = Ini.Set(preset, "", "Techniques", MergeTechniques(Ini.Get(preset, "", "Techniques")));
        preset = Ini.Set(preset, "", "TechniqueSorting", MergeTechniques(Ini.Get(preset, "", "TechniqueSorting")));
        preset = Ini.Set(preset, "", "PreprocessorDefinitions", EnsureMvProvider(Ini.Get(preset, "", "PreprocessorDefinitions")));
        preset = Ini.Set(preset, "DLSS5_Feed.fx", "PreprocessorDefinitions", EnsureMvProvider(Ini.Get(preset, "DLSS5_Feed.fx", "PreprocessorDefinitions")));
        job.S.WriteTracked(presetFile, ToCrlf(preset));
        job.Log($"Configured ReShade.ini and {job.S.Rel(presetFile)} for the Feeder.");

        var cfgPath = Path.Combine(job.ExeDir, "dlss5-feed.cfg");
        job.S.WriteTracked(cfgPath, BuildFeedCfg(File.Exists(cfgPath) ? Ini.ReadText(cfgPath) : ""));

        // The neural consumer runs in a 64-bit process: the game itself, or the helper for 32-bit games.
        var hostDir = x64 ? job.ExeDir : Path.Combine(job.ExeDir, "host64");
        OneConsumer(job, hostDir, ConsumerAddon);
        job.S.CopyTracked(job.Consumer!, Path.Combine(hostDir, ConsumerAddon), "addon");
        PlaceRuntime(job, job.Runtime[NeuralDll], Path.Combine(hostDir, NeuralDll), 64);
        PlaceRuntime(job, job.Runtime[DlssDll], Path.Combine(hostDir, DlssDll), 64);

        if (!x64)
        {
            job.S.CopyTracked(payload.Host64, Path.Combine(hostDir, "dlss5-feed-host64.exe"), "tool");
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
        EnableAddon(job, hostDir, ConsumerAddon);
        job.Log("In the ReShade overlay, enable DLSS5_Feed and vort_MotionEffects.");
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

    internal static string EnsureMvProvider(string? current)
    {
        var result = new List<string> { "DLSS5_MV_PROVIDER=2" };
        foreach (var item in SplitList(current))
        {
            var name = item.Split('=')[0].Trim();
            if (name.Equals("DLSS5_MV_PROVIDER", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(item);
        }
        return string.Join(",", result);
    }

    internal static string MergeTechniques(string? current)
    {
        var result = new List<string>(FeedTechniques);
        foreach (var item in SplitList(current))
        {
            var name = item.Split('@')[0].Trim();
            if (ReplacedTechniqueNames.Contains(name)) continue;
            if (!result.Contains(item, StringComparer.OrdinalIgnoreCase)) result.Add(item);
        }
        return string.Join(",", result);
    }

    private static string ToCrlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    internal static string BuildFeedCfg(string existing)
    {
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
        return string.Join("\r\n", lines) + "\r\n";
    }

    // ------------------------------------------------------------------ OptiScaler

    private static readonly HashSet<string> OptiConflictStems = new(StringComparer.OrdinalIgnoreCase)
    { "dxgi", "winmm", "version", "dbghelp", "dbgcore", "d3d12", "d3d11", "wininet", "winhttp", "OptiScaler" };

    private static void RunOptiScaler(Job job)
    {
        var build = job.Build!;
        var hook = job.Req.OptiHook;
        var pkg = job.OptiFolder!;
        var exeDir = job.ExeDir;

        // 1. Another proxy DLL or ASI loader in the way?
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

        // 2. Record what is being installed.
        job.S.Manifest.OptiScalerBuild = build.Id;
        job.S.Manifest.OptiScalerHook = hook;

        if (build.NeuralRendering)
        {
            var forwarder = Path.Combine(exeDir, ForwarderDll);
            if (!build.Forwarded && File.Exists(forwarder))
            {
                job.S.DeleteTracked(forwarder, "optiscaler");
                job.Log($"Removed {ForwarderDll}: it would silently disable this build's neural pass.");
            }
            job.S.CopyTracked(Path.Combine(pkg, "OptiScaler.dll"), Path.Combine(exeDir, hook), "optiscaler");
            if (build.Forwarded)
            {
                var src = Path.Combine(pkg, ForwarderDll);
                if (!File.Exists(src)) throw new InstallException($"The {build.Label} package is missing {ForwarderDll}.");
                job.S.CopyTracked(src, forwarder, "optiscaler");
            }
            foreach (var lib in Components.OptiLibraries)
            {
                var src = Path.Combine(pkg, "OptiScaler", lib);
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
            if (File.Exists(model))
                job.Log($"Keeping the {NeuralDll} already next to the game ({Pe.GetFileVersion(model)}).");
            else if (job.Runtime.TryGetValue(NeuralDll, out var nr))
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
        job.S.CopyTracked(job.MfgFile!, Path.Combine(job.ExeDir, Components.MfgAddon), "addon");
        var mfgVersion = Updates.InUse(Updates.Sources.First(s => s.Id == "mfgunlock")).Version;
        job.S.Manifest.MfgUnlock = mfgVersion;
        job.Log($"Added the MFG unlock add-on {mfgVersion}.");

        var fresh = Scanner.Scan(job.S.GameDir, job.Req.ExePath);
        var frameGen = fresh.Upscalers.Where(u => u.Kind == UpscalerKind.DlssFG && u.Bitness == 64).ToList();
        if (job.Runtime.TryGetValue(FrameGenDll, out var fgSrc))
            foreach (var f in frameGen) PlaceRuntime(job, fgSrc, f.Path, 64);
        foreach (var f in frameGen)
        {
            var v = Pe.GetFileVersion(f.Path);
            if (Scanner.CompareVersions(v, "310.0.0.0") < 0)
                job.Log($"Warning: {f.Rel} is {v}; the MFG unlock wants DLSS Frame Generation 310.x. Use the Swap button to update it.");
        }
        EnableAddon(job, job.ExeDir, Components.MfgAddon);
        job.Log("Turn on DLSS Frame Generation in the game and pick 3x or 4x, or set it in the ReShade Add-ons tab.");
    }

    // ------------------------------------------------------------------ other operations

    public static Task RestoreAsync(string gameDir, string? exePath, Action<string> log) => Task.Run(() =>
    {
        if (!string.IsNullOrWhiteSpace(exePath)) AssertGameClosed(exePath);
        if (!Session.Restore(gameDir, log)) log("Nothing to restore: DLSS 5 Master has no install record in this game.");
    });

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
        if (components.RuntimeFiles().TryGetValue(name, out var rt))
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

    public static string ImportDll(string file)
    {
        var name = Path.GetFileName(file);
        if (Scanner.Classify(name) is null)
            throw new InstallException($"{name} is not a DLSS, Streamline, FSR or XeSS DLL.");
        var version = Pe.GetFileVersion(file) ?? "unknown";
        var bits = Pe.GetBitness(file);
        var dest = Path.Combine(AppPaths.DllLibrary, name.ToLowerInvariant(), $"{version}-x{bits}", name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (!Path.GetFullPath(file).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            Journal.ClearReadOnly(dest);
            File.Copy(file, dest, true);
        }
        return dest;
    }
}
