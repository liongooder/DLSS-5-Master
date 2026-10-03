using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSS5Master.Core;

public enum RenderApi { Unknown, DirectDraw, DX8, DX9, DX10, DX11, DX12, Vulkan, OpenGL }

public enum UpscalerKind { DlssSR, DlssFG, DlssRR, DlssNR, Streamline, Fsr, XeSS, NgxCore }

public sealed record ExeInfo(string Path, string Rel, int Bitness, RenderApi Api, long Size, int Score)
{
    public override string ToString()
    {
        var bits = Bitness switch { 64 => "64-bit", 32 => "32-bit", _ => "?" };
        long mb = (Size + 512 * 1024) / (1024 * 1024);
        return $"{Rel} — {Scanner.Label(Api)} — {bits} — {mb} MB";
    }
}

public sealed record UpscalerFile(string Path, string Rel, string Name, UpscalerKind Kind, string? Version, int Bitness);

public sealed record ReShadeState(bool Installed, string? File, string? Version, bool AddonSupport, bool IsAsi);

/// <summary>What the scanner learned about one game folder.</summary>
public sealed class GameScan
{
    public string Dir { get; init; } = "";
    public List<ExeInfo> Executables { get; init; } = new();
    public ExeInfo? Primary { get; set; }
    public List<UpscalerFile> Upscalers { get; init; } = new();
    public ReShadeState ReShade { get; set; } = new(false, null, null, false, false);
    public string? OptiScalerHook { get; set; }
    public bool OptiScalerIni { get; set; }
    public List<string> RenoDxAddons { get; set; } = new();
    public Manifest? Manifest { get; set; }
    public bool OtherAppBackup { get; set; }
    public string? OtherAppBackupRoute { get; set; }
    /// <summary>Another tool has an install in this game (from its records in %LOCALAPPDATA%\DLSS5Manager\Installs).</summary>
    public bool OtherAppRecord => OtherAppRecordRoute is not null;
    public string? OtherAppRecordRoute { get; set; }
    public bool AntiCheat { get; set; }

    /// <summary>The main DLSS super-resolution DLL (shortest relative path wins).</summary>
    public UpscalerFile? Dlss => Upscalers.Where(u => u.Kind == UpscalerKind.DlssSR).OrderBy(u => u.Rel.Length).FirstOrDefault();

    public bool HasFrameGen => Upscalers.Any(u => u.Kind == UpscalerKind.DlssFG && u.Bitness == 64);

    /// <summary>The game ships DLSS itself (ignoring copies this app added).</summary>
    public bool HasNativeDlss => Upscalers.Any(u => u.Kind == UpscalerKind.DlssSR &&
        !(Manifest?.Added.Any(a => a.Equals(u.Rel, StringComparison.OrdinalIgnoreCase)) ?? false));
}

public static partial class Scanner
{
    public static readonly string[] ReShadeHooks = { "dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d9.dll", "opengl32.dll", "dinput8.dll" };

    private static readonly string[] OptiScalerProbe = { "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll", "OptiScaler.asi" };

    private const int MaxDepth = 9;
    private const int MaxEntries = 40_000;
    private const long OneMb = 1024 * 1024;

    // ------------------------------------------------------------------ labels

    public static string Label(RenderApi api) => api switch
    {
        RenderApi.DirectDraw => "DirectDraw",
        RenderApi.DX8 => "DirectX 8",
        RenderApi.DX9 => "DirectX 9",
        RenderApi.DX10 => "DirectX 10",
        RenderApi.DX11 => "DirectX 11",
        RenderApi.DX12 => "DirectX 12",
        RenderApi.Vulkan => "Vulkan",
        RenderApi.OpenGL => "OpenGL",
        _ => "Unknown",
    };

    public static UpscalerKind? Classify(string name)
    {
        var n = System.IO.Path.GetFileName(name).ToLowerInvariant();
        if (!n.EndsWith(".dll")) return null;
        switch (n)
        {
            case "nvngx_dlss.dll": return UpscalerKind.DlssSR;
            case "nvngx_dlssg.dll": return UpscalerKind.DlssFG;
            case "nvngx_dlssd.dll": return UpscalerKind.DlssRR;
            case "nvngx_dlssnr.dll": return UpscalerKind.DlssNR;
            case "nvngx.dll":
            case "_nvngx.dll": return UpscalerKind.NgxCore;
        }
        if (n.StartsWith("sl.")) return UpscalerKind.Streamline;
        if (n.StartsWith("libxess")) return UpscalerKind.XeSS;
        if (n.StartsWith("amd_fidelityfx") || n.StartsWith("ffx_fsr") || n.StartsWith("ffx_backend") || n.StartsWith("ffx_frameinterpolation"))
            return UpscalerKind.Fsr;
        return null;
    }

    public static string KindLabel(UpscalerKind k) => k switch
    {
        UpscalerKind.DlssSR => "DLSS",
        UpscalerKind.DlssFG => "DLSS Frame Gen",
        UpscalerKind.DlssRR => "DLSS Ray Reconstruction",
        UpscalerKind.DlssNR => "DLSS Neural Rendering",
        UpscalerKind.Streamline => "Streamline",
        UpscalerKind.Fsr => "FSR",
        UpscalerKind.XeSS => "XeSS",
        _ => "NGX",
    };

    [GeneratedRegex(@"(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();

    public static int CompareVersions(string? a, string? b)
    {
        var x = ParseVersion(a);
        var y = ParseVersion(b);
        if (x is null || y is null) return 0;
        for (int i = 0; i < 4; i++)
        {
            int c = x[i].CompareTo(y[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    internal static bool IsVersion(string? s) => ParseVersion(s) is not null;

    private static long[]? ParseVersion(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = VersionPattern().Match(s);
        if (!m.Success) return null;
        var parts = new long[4];
        for (int i = 0; i < 4; i++)
        {
            var g = m.Groups[i + 1];
            if (g.Success && !long.TryParse(g.Value, out parts[i])) return null;
        }
        return parts;
    }

    public static RenderApi ApiFromImports(HashSet<string> imports)
    {
        bool Has(string dll) => imports.Contains(dll) || imports.Any(i => i.Equals(dll, StringComparison.OrdinalIgnoreCase));
        if (Has("d3d12.dll")) return RenderApi.DX12;
        if (Has("d3d11.dll")) return RenderApi.DX11;
        if (Has("d3d10.dll") || Has("d3d10_1.dll")) return RenderApi.DX10;
        if (Has("dxgi.dll")) return RenderApi.DX11;
        if (Has("vulkan-1.dll")) return RenderApi.Vulkan;
        if (Has("d3d9.dll")) return RenderApi.DX9;
        if (Has("d3d8.dll")) return RenderApi.DX8;
        if (Has("ddraw.dll")) return RenderApi.DirectDraw;
        if (Has("opengl32.dll")) return RenderApi.OpenGL;
        return RenderApi.Unknown;
    }

    // ------------------------------------------------------------------ folder walk

    // Folder names that never contain the game's renderer.
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        Journal.BackupDir, "_DLSS5_Backup", "reshade-shaders",
        "_CommonRedist", "CommonRedist", "Redist", "Redistributable", "Redistributables", "redistrib",
        "Installer", "Installers", "__Installer", "_Installer", "Prerequisites", "Prereqs",
        "DirectX", "DirectX9", "DXSETUP", "vcredist", "VC_redist", "VCRedist", "MSVC", "dotnet", "DotNetFX", ".NET", "NetFX",
        "ShaderCache", "Shader_Cache", "ShadersCache", "ShaderCaches", "WebCache", "Web_Cache", "GPUCache", "Code Cache", "webcache_",
        "Movie", "Movies", "Music", "Sound", "Sounds", "Audio", "Video", "Videos", "Cinematics",
        "Localization", "Localisation", "Paks", ".egstore", "CrashReportClient", ".git",
    };

    private static bool SkipDir(string name) =>
        SkipDirs.Contains(name) || name.Contains("redist", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DirectX", StringComparison.OrdinalIgnoreCase) || name.Contains("shadercache", StringComparison.OrdinalIgnoreCase);

    // Helper programs that are never the game itself (prefix match on the file name).
    private static readonly string[] IgnoredExePrefixes =
    {
        "unins", "uninstall", "setup", "install", "vc_redist", "vcredist", "vc20", "dxsetup", "dxwebsetup", "directx",
        "dotnet", "ndp4", "netfx", "windowsdesktop-runtime", "oalinst", "openal",
        "crashreport", "crashhandler", "crashpad", "crash_reporter", "unitycrashhandler", "bugsplat", "bssndrpt", "crs-", "redengineerrorreporter",
        "easyanticheat", "start_protected_game", "eac", "beservice", "battleye", "be_", "vanguard", "anticheat",
        "patcher", "patch", "updater", "update", "launcher", "benchmark", "readme", "config", "settingstool",
        "modmanager", "mod_manager", "modorganizer", "steamerrorreporter", "unrealcefsubprocess", "cefsharp", "cef_", "cefprocess",
        "qtwebengineprocess", "epicwebhelper", "createdump", "7z", "python", "ueprereqsetup", "prereqsetup", "gamelaunchhelper",
        "rockstar-games-launcher", "redprelauncher", "playgtav", "dlss5-feed-host", "dgvoodoocpl", "reshade_setup",
    };

    private static bool IsHelperExe(string fileName) =>
        IgnoredExePrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static GameScan Scan(string gameDir, string? preferredExe = null, CancellationToken ct = default)
    {
        var root = System.IO.Path.GetFullPath(gameDir).TrimEnd('\\', '/');
        var exes = new List<string>();
        var upscalers = new List<UpscalerFile>();
        bool antiCheat = false;
        int budget = MaxEntries;

        var pending = new Stack<(string dir, int depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0 && budget > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = pending.Pop();
            IEnumerable<string> files, subdirs;
            try
            {
                files = Directory.EnumerateFiles(dir).ToList();
                subdirs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch { continue; }

            foreach (var f in files)
            {
                if (--budget <= 0) break;
                var name = System.IO.Path.GetFileName(f);
                if (name.StartsWith("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) || name.StartsWith("BEService", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("start_protected_game.exe", StringComparison.OrdinalIgnoreCase))
                    antiCheat = true;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exes.Add(f);
                else if (Classify(name) is { } kind)
                    upscalers.Add(new UpscalerFile(f, System.IO.Path.GetRelativePath(root, f), name, kind, Pe.GetFileVersion(f), Pe.GetBitness(f)));
            }

            foreach (var sub in subdirs)
            {
                if (--budget <= 0) break;
                var name = System.IO.Path.GetFileName(sub);
                if (name.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) || name.Equals("BattlEye", StringComparison.OrdinalIgnoreCase))
                {
                    antiCheat = true;
                    continue;
                }
                if (SkipDir(name) || depth + 1 > MaxDepth) continue;
                try
                {
                    // Junctions/symlinks can loop; do not follow them.
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                pending.Push((sub, depth + 1));
            }
        }

        // The user's choice always counts, even if it lives in a skipped folder or looks like a helper.
        string? preferred = null;
        if (!string.IsNullOrWhiteSpace(preferredExe) && File.Exists(preferredExe))
        {
            preferred = System.IO.Path.GetFullPath(preferredExe);
            if (!exes.Any(e => e.Equals(preferred, StringComparison.OrdinalIgnoreCase))) exes.Add(preferred);
        }

        var folderName = GameFolderName(root);
        var infos = new List<ExeInfo>();
        foreach (var exe in exes)
        {
            ct.ThrowIfCancellationRequested();
            bool isPreferred = preferred is not null && exe.Equals(preferred, StringComparison.OrdinalIgnoreCase);
            var name = System.IO.Path.GetFileName(exe);
            if (!isPreferred && IsHelperExe(name)) continue;
            long size;
            try { size = new FileInfo(exe).Length; } catch { continue; }
            int bits = Pe.GetBitness(exe);
            if (bits == 0 && size < OneMb && !isPreferred) continue;
            var api = bits == 0 ? RenderApi.Unknown : ApiFromImports(Pe.GetImports(exe));
            var rel = System.IO.Path.GetRelativePath(root, exe);
            infos.Add(new ExeInfo(exe, rel, bits, api, size, ScoreOf(rel, name, size, bits, api, folderName)));
        }
        infos = Rank(infos, preferred);

        // Look closer at the leading candidates whose API might be wrong or missing.
        for (int i = 0; i < Math.Min(3, infos.Count); i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = infos[i];
            if (e.Api == RenderApi.DX12 || e.Bitness == 0) continue;
            var refined = RefineApi(e.Path, e.Api);
            if (refined != e.Api)
                infos[i] = e with { Api = refined, Score = ScoreOf(e.Rel, System.IO.Path.GetFileName(e.Path), e.Size, e.Bitness, refined, folderName) };
        }
        infos = Rank(infos, preferred);

        upscalers = upscalers.OrderBy(u => u.Kind).ThenBy(u => u.Rel, StringComparer.OrdinalIgnoreCase).ToList();

        Manifest? manifest = null;
        try { manifest = Journal.ReadManifest(root); }
        catch (InstallException e) { AppPaths.Log($"[{System.IO.Path.GetFileName(root)}] {e.Message}"); }

        var otherManifest = System.IO.Path.Combine(root, "_DLSS5_Backup", "manifest.json");
        bool otherBackup = File.Exists(otherManifest);

        var scan = new GameScan
        {
            Dir = root,
            Executables = infos,
            Primary = infos.FirstOrDefault(),
            Upscalers = upscalers,
            Manifest = manifest,
            OtherAppBackup = otherBackup,
            OtherAppBackupRoute = otherBackup ? ReadBackupRoute(otherManifest) : null,
            OtherAppRecordRoute = OtherAppRecordRouteFor(root),
            AntiCheat = antiCheat,
        };
        if (scan.Primary is not null) InspectExeDir(scan, System.IO.Path.GetDirectoryName(scan.Primary.Path)!);
        return scan;
    }

    private static List<ExeInfo> Rank(List<ExeInfo> infos, string? preferred) =>
        infos.OrderByDescending(e => preferred is not null && e.Path.Equals(preferred, StringComparison.OrdinalIgnoreCase))
             .ThenByDescending(e => e.Score)
             .ThenBy(e => e.Rel, StringComparer.OrdinalIgnoreCase)
             .ToList();

    private static string GameFolderName(string root)
    {
        var name = System.IO.Path.GetFileName(root);
        // Xbox installs point at "...\<Game>\Content".
        if (name.Equals("Content", StringComparison.OrdinalIgnoreCase))
            name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(root) ?? "") ?? name;
        return name;
    }

    private static string Simplify(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static int ScoreOf(string rel, string fileName, long size, int bits, RenderApi api, string folderName)
    {
        if (bits == 0) return -1000 + (int)Math.Min(100, size / OneMb); // unreadable (e.g. encrypted Store exe): always last

        double mb = size / (double)OneMb;
        int score = (int)Math.Min(45, Math.Log2(mb + 1) * 6);

        var lower = fileName.ToLowerInvariant();
        var stem = System.IO.Path.GetFileNameWithoutExtension(lower);
        if (lower.Contains("-shipping")) score += 40;

        var relLower = "\\" + rel.ToLowerInvariant();
        if (relLower.Contains("\\binaries\\win64\\") || relLower.Contains("\\binaries\\wingdk\\")) score += 15;

        var a = Simplify(stem.Replace("-shipping", "").Replace("-win64", "").Replace("-wingdk", ""));
        var b = Simplify(folderName);
        if (a.Length >= 3 && b.Length >= 3)
        {
            if (a == b) score += 30;
            else if (a.Contains(b) || b.Contains(a)) score += 22;
            else if (a.Length >= 4 && b.Length >= 4 && a[..4] == b[..4]) score += 10;
        }

        if (api != RenderApi.Unknown) score += 20;
        if (bits == 64) score += 5;
        if (size < OneMb) score -= 25;
        if (lower.Contains("launcher") || lower.Contains("config")) score -= 30;

        int depth = rel.Count(c => c == '\\');
        if (depth > 3) score -= (depth - 3) * 6;
        return score;
    }

    private static readonly string[] D3D12Markers = { "D3D12CreateDevice", "D3D12SDKVersion" };
    private static readonly string[] ExeMarkers = { "D3D12CreateDevice", "D3D12SDKVersion", "D3D11CreateDevice", "vkCreateInstance", "Direct3DCreate9", "wglCreateContext" };

    /// <summary>Second opinion for an executable whose imports did not say DX12.</summary>
    private static RenderApi RefineApi(string exe, RenderApi api)
    {
        var dir = System.IO.Path.GetDirectoryName(exe)!;

        // D3D12 Agility SDK shipped next to the game.
        if (File.Exists(System.IO.Path.Combine(dir, "D3D12", "D3D12Core.dll"))) return RenderApi.DX12;

        List<string> dlls;
        try { dlls = Directory.EnumerateFiles(dir, "*.dll").ToList(); }
        catch { dlls = new(); }

        if (api == RenderApi.Unknown)
        {
            var names = dlls.Select(d => System.IO.Path.GetFileName(d).ToLowerInvariant())
                            .Where(n => !n.StartsWith("amd_") && !n.StartsWith("libxess") && !n.StartsWith("nvngx") && !n.StartsWith("ffx"))
                            .ToList();
            bool Inside(string n, params string[] tokens) => tokens.Any(t => n.IndexOf(t, StringComparison.Ordinal) > 0);
            if (names.Any(n => Inside(n, "d3d12", "dx12"))) api = RenderApi.DX12;
            else if (names.Any(n => Inside(n, "d3d11", "dx11"))) api = RenderApi.DX11;
            else if (names.Any(n => Inside(n, "vulkan"))) api = RenderApi.Vulkan;
        }

        var markers = Pe.FindMarkers(exe, ExeMarkers);
        if (D3D12Markers.Any(markers.Contains)) return RenderApi.DX12;

        if (api == RenderApi.Unknown)
        {
            var unity = System.IO.Path.Combine(dir, "UnityPlayer.dll");
            if (File.Exists(unity))
            {
                var u = Pe.FindMarkers(unity, new[] { "D3D11CreateDevice", "D3D12CreateDevice" });
                if (u.Contains("D3D11CreateDevice")) return RenderApi.DX11;
                if (u.Contains("D3D12CreateDevice")) return RenderApi.DX12;
            }
        }

        if (api == RenderApi.Unknown && markers.Count == 0)
        {
            // Engines that keep the renderer in a big side DLL.
            var big = dlls.Select(d => { try { return new FileInfo(d); } catch { return null; } })
                          .Where(f => f is not null && f.Length > 30 * OneMb)
                          .OrderByDescending(f => f!.Length).FirstOrDefault();
            if (big is not null && Pe.FindMarkers(big.FullName, D3D12Markers).Count > 0) return RenderApi.DX12;
        }

        if (api == RenderApi.Unknown)
        {
            if (markers.Contains("D3D11CreateDevice")) return RenderApi.DX11;
            if (markers.Contains("vkCreateInstance")) return RenderApi.Vulkan;
            if (markers.Contains("Direct3DCreate9")) return RenderApi.DX9;
            if (markers.Contains("wglCreateContext")) return RenderApi.OpenGL;
        }
        return api;
    }

    private static string? ReadBackupRoute(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Name.Equals("route", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Another tool keeps one JSON record per installed game in %LOCALAPPDATA%\DLSS5Manager\Installs, with the game's
    /// ExecutablePath and Mode ("optiscaler", "dx12", ...) plus flags such as Multipass / MfgUnlock ("1" when set).
    /// Returns a short label for the install whose executable lives inside gameDir, or null.
    /// </summary>
    public static string? OtherAppRecordRouteFor(string gameDir)
    {
        var dir = ForeignInstalls.OtherAppRecordsDir;
        if (!Directory.Exists(dir)) return null;
        var root = System.IO.Path.GetFullPath(gameDir).TrimEnd('\\') + "\\";
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var r = doc.RootElement;
                    string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var exe = Str("ExecutablePath");
                    if (string.IsNullOrEmpty(exe) || ForeignInstalls.RecordedRoot(exe, root) is null) continue;
                    var mode = Str("Mode") ?? "";
                    var label = mode.Equals("optiscaler", StringComparison.OrdinalIgnoreCase) ? "OptiScaler"
                        : Str("Multipass") == "1" ? "multipass"
                        : Str("Neural") == "1" ? "neural"
                        : mode == "" ? "installed" : mode;
                    return Str("MfgUnlock") == "1" ? label + " + MFG" : label;
                }
                catch { /* unreadable record: ignore it */ }
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ exe folder details

    public static void InspectExeDir(GameScan scan, string exeDir)
    {
        scan.ReShade = InspectReShade(exeDir);
        scan.OptiScalerIni = File.Exists(System.IO.Path.Combine(exeDir, "OptiScaler.ini"));
        scan.OptiScalerHook = OptiScalerProbe.FirstOrDefault(n =>
        {
            var f = System.IO.Path.Combine(exeDir, n);
            return File.Exists(f) && Pe.VersionMentions(f, "OptiScaler");
        });
        var addons = new List<string>();
        try
        {
            if (Directory.Exists(exeDir))
                foreach (var f in Directory.EnumerateFiles(exeDir, "*.addon*"))
                {
                    var n = System.IO.Path.GetFileName(f);
                    if (n.StartsWith("renodx", StringComparison.OrdinalIgnoreCase) || n.StartsWith("dlss5", StringComparison.OrdinalIgnoreCase))
                        addons.Add(n);
                }
        }
        catch { }
        scan.RenoDxAddons = addons;
    }

    public static bool HasAddonSupport(string file) => Pe.Contains(file, "Searching for add-ons");

    public static ReShadeState InspectReShade(string exeDir)
    {
        var candidates = new List<string>();
        candidates.AddRange(ReShadeHooks.Select(h => System.IO.Path.Combine(exeDir, h)));
        try
        {
            if (Directory.Exists(exeDir)) candidates.AddRange(Directory.EnumerateFiles(exeDir, "*.asi"));
        }
        catch { }
        foreach (var f in candidates)
        {
            if (!File.Exists(f) || !Pe.VersionMentions(f, "ReShade")) continue;
            bool asi = f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase);
            return new ReShadeState(true, System.IO.Path.GetFileName(f), Pe.GetFileVersion(f), HasAddonSupport(f), asi);
        }
        return new ReShadeState(false, null, null, false, false);
    }
}
