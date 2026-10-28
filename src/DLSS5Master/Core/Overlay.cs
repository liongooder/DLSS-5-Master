using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DLSS5Master.Core;

/// <summary>A window the overlay can be started on.</summary>
public sealed record OverlayWindow(nint Handle, string Title, string Process, string? ExePath);

/// <summary>
/// DLSS 5 on any window, with no game file changed: dlss5-overlay.exe captures the window, runs DLSS with the
/// neural pass on every frame and draws the result in a click-through window exactly over it. It runs from
/// its own folder in the data folder, assembled from files the app already has: the helper (overlay\ in the
/// install folder), ReShade x64 as dxgi.dll, the RenoDX DLSS5 add-on and the NVIDIA runtimes. Its settings go
/// into that folder's ReShade.ini, which the add-on reads when it starts. A game that runs as administrator
/// needs an overlay that does too (Windows hides its keyboard from normal programs), so the overlay is then
/// started elevated and stopped through a file both can see.
/// </summary>
public static class Overlay
{
    public static string Folder => Path.Combine(AppPaths.Root, "overlay");
    private static string StopFile => Path.Combine(Folder, "stop");
    public static string LogFile => Path.Combine(Folder, "dlss5-overlay.log");
    private static string HelperDir => Path.Combine(AppContext.BaseDirectory, "overlay");
    private static readonly string[] HelperFiles = { "dlss5-overlay.exe", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" };

    private static Process? _process;

    /// <summary>The window the running overlay draws over, or null when none runs.</summary>
    public static OverlayWindow? Target { get; private set; }

    public static bool Running { get { try { return _process is { HasExited: false }; } catch { return false; } } }

    /// <summary>Raised (on a worker thread) when the overlay starts or stops.</summary>
    public static event Action? Changed;

    public static bool HelperInstalled => File.Exists(Path.Combine(HelperDir, "dlss5-overlay.exe"));

    /// <summary>Copies what the overlay needs into its folder; only files that changed are copied again.</summary>
    public static async Task PrepareAsync(Components components, int frameGen, IProgress<string>? p, CancellationToken ct)
    {
        if (!HelperInstalled) throw new InvalidOperationException("The overlay helper is missing from the install folder. Reinstall DLSS 5 Master.");
        var runtime = components.RuntimeFiles();
        if (!runtime.TryGetValue("nvngx_dlssnr.dll", out var neural) || !runtime.TryGetValue("nvngx_dlss.dll", out var dlss))
            throw new InvalidOperationException("The NVIDIA DLSS and neural rendering runtimes were not found (Settings → NVIDIA runtime).");
        var reshade = components.ReShadeDll(64) ?? throw new InvalidOperationException("ReShade x64 with add-on support was not found (Settings → ReShade).");
        var addon = await components.RenoDxConsumerAsync(p, ct);

        var copies = new List<(string From, string To)>();
        foreach (var f in HelperFiles) copies.Add((Path.Combine(HelperDir, f), f));
        copies.Add((reshade, "dxgi.dll"));
        copies.Add((addon, "renodx-dlss5.addon64"));
        copies.Add((dlss, "nvngx_dlss.dll"));
        copies.Add((neural, "nvngx_dlssnr.dll"));
        if (runtime.TryGetValue("nvngx_dlssg.dll", out var framegen)) copies.Add((framegen, "nvngx_dlssg.dll"));
        // Frame generation unlocks, only when asked for and never both (each warns against the other):
        // RTX 20 / 30 run DLSS Frame Generation through dlssg_for_sm86, taken from the bundled OptiScaler package;
        // RTX 40 needs the MFG unlock add-on for 3x and 4x. RTX 50 needs neither.
        var sm86 = frameGen >= 1 && Gpu.HasRtx20or30;
        if (sm86) await Task.Run(() => ExtractSm86(ct), ct);
        else RemoveSm86();
        var mfg = Path.Combine(Folder, Components.MfgAddon);
        if (frameGen >= 2 && !sm86) copies.Add((await components.MfgUnlockAsync(p, ct), Components.MfgAddon));
        else { try { File.Delete(mfg); } catch { } }
        Directory.CreateDirectory(Folder);
        await Task.Run(() =>
        {
            foreach (var (from, to) in copies)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(from)) continue;   // a runtime DLL the helper's own folder does not ship (vcruntime140_1 on older toolsets)
                var dest = Path.Combine(Folder, to);
                var src = new FileInfo(from);
                var dst = new FileInfo(dest);
                if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) continue;
                p?.Report($"Preparing the overlay: {to}");
                File.Copy(from, dest, true);
            }
        }, ct);
    }

    private const string Sm86Prefix = "OptiScaler/dlssg_sm86/";

    /// <summary>
    /// dlssg_for_sm86 (sdli1995) enables DLSS Frame Generation on RTX 20 / 30. The OptiScaler package bundled with
    /// the app ships it (OptiScaler sideloads it the same way), so it is unpacked from there: dlssg_sm86.dll, its
    /// ini and its dlssg_sm86\ folder go beside the helper, which loads the DLL before NGX starts.
    /// </summary>
    private static void ExtractSm86(CancellationToken ct)
    {
        var zip = Path.Combine(Components.BundledPayload, "optiscaler", Components.BundledOptiZipName(Components.OptiBuilds[0]));
        if (!File.Exists(zip)) throw new InvalidOperationException("Frame generation on this graphics card needs the bundled OptiScaler package, which is missing. Reinstall DLSS 5 Master.");
        var marker = Path.Combine(Folder, "dlssg_sm86", "unpacked-from.txt");
        var stamp = $"{Path.GetFileName(zip)} {new FileInfo(zip).Length}";
        if (File.Exists(marker) && File.ReadAllText(marker) == stamp) return;
        RemoveSm86();
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(Sm86Prefix, StringComparison.OrdinalIgnoreCase) || entry.Name.Length == 0) continue;
            var rel = entry.FullName[Sm86Prefix.Length..].Replace('/', '\\');
            // The DLL, its ini and its folder; the notices go into the folder with it. Docs and readmes stay out.
            if (rel.Equals("THIRD_PARTY_NOTICES.txt", StringComparison.OrdinalIgnoreCase)) rel = @"dlssg_sm86\THIRD_PARTY_NOTICES.txt";
            else if (!rel.Equals("dlssg_sm86.dll", StringComparison.OrdinalIgnoreCase) && !rel.Equals("dlssg_sm86.ini", StringComparison.OrdinalIgnoreCase) &&
                     !rel.StartsWith(@"dlssg_sm86\", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.GetFullPath(Path.Combine(Folder, rel));
            if (!dest.StartsWith(Folder + "\\", StringComparison.OrdinalIgnoreCase)) continue;   // never outside the folder
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, dest, true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, stamp);
        AppPaths.Log("Overlay: unpacked dlssg_for_sm86 (DLSS Frame Generation on RTX 20 / 30) from " + Path.GetFileName(zip));
    }

    private static void RemoveSm86()
    {
        try { File.Delete(Path.Combine(Folder, "dlssg_sm86.dll")); } catch { }
        try { File.Delete(Path.Combine(Folder, "dlssg_sm86.ini")); } catch { }
        try { Directory.Delete(Path.Combine(Folder, "dlssg_sm86"), true); } catch { }
    }

    /// <summary>Writes the neural renderer's settings where the add-on reads them at start.</summary>
    private static void WriteSettings(AppSettings s)
    {
        var ini = Path.Combine(Folder, "ReShade.ini");
        var text = File.Exists(ini) ? Ini.ReadText(ini) : "";
        void Set(string key, string value) => text = Ini.Set(text, "RenoDX.DLSS5", key, value);
        Set("EnableHooks", "2");        // NGX only: the helper calls NGX itself, no Streamline
        Set("NeuralUplift", "1");
        Set("NREnableUpscaling", "0");  // 1:1 DLAA
        Set("NRStyle", Math.Clamp(s.OverlayStyle, 0, 2).ToString(CultureInfo.InvariantCulture));
        Set("NRIntensity", (Math.Clamp(s.OverlayStrength, 0, 100) / 100.0).ToString("0.##", CultureInfo.InvariantCulture));
        Set("NRPasses", Math.Clamp(s.OverlayPasses, 1, 2).ToString(CultureInfo.InvariantCulture));
        // The add-on's own resolution scale saved next to nothing (14.3 ms at 50 % against 12.5 ms at 100 % in
        // Bloodborne); Quality now sets the helper's processing size instead (--scale), so this stays at 100 %.
        Set("NRResolutionScale", "1");
        File.WriteAllText(ini, text);
    }

    /// <summary>Starts the overlay on a window (stopping one that runs). Asks Windows for administrator
    /// rights when the window's program has them.</summary>
    public static async Task StartAsync(Components components, AppSettings settings, OverlayWindow target, IProgress<string>? p, CancellationToken ct)
    {
        await StopAsync();
        if (!IsWindow(target.Handle)) throw new InvalidOperationException("That window is not open any more.");
        var frameGen = Math.Clamp(settings.OverlayFrameGen, 0, 3);
        await PrepareAsync(components, frameGen, p, ct);
        WriteSettings(settings);
        try { File.Delete(StopFile); } catch { }
        try { File.Delete(LogFile); } catch { }

        var elevated = !IsElevatedSelf() && IsElevated(target.Handle);
        // Quality: everything runs on the window's picture at 100 / 67 / 50 % of its size, stretched back over it.
        var scale = Math.Clamp(settings.OverlayQuality, 0, 2) switch { 1 => "0.67", 2 => "0.5", _ => "1" };
        var args = $"--hwnd {(ulong)target.Handle} --stop-file \"{StopFile}\" --scale {scale}" + (frameGen > 0 ? $" --fg {frameGen}" : "");
        var psi = new ProcessStartInfo(Path.Combine(Folder, "dlss5-overlay.exe"), args) { WorkingDirectory = Folder };
        if (elevated) { psi.UseShellExecute = true; psi.Verb = "runas"; }
        p?.Report(elevated ? "Starting the overlay as administrator (the game runs that way)…" : "Starting the overlay…");
        try { _process = Process.Start(psi); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        { throw new InvalidOperationException("The overlay needs administrator rights because the game has them, and Windows was told no."); }
        if (_process is null) throw new InvalidOperationException("The overlay did not start.");
        Target = target;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => { Target = null; AppPaths.Log("Overlay stopped. " + (LastLogError() ?? "")); Changed?.Invoke(); };
        AppPaths.Log($"Overlay started on \"{target.Title}\" ({target.Process}){(elevated ? " as administrator" : "")}: style {settings.OverlayStyle}, " +
                     $"strength {settings.OverlayStrength} %, {settings.OverlayPasses} pass(es), quality {settings.OverlayQuality}, " +
                     $"frame generation {(frameGen == 0 ? "off" : $"{frameGen + 1}x")}.");
        Changed?.Invoke();

        // A window that cannot be captured ends the helper within a few seconds; say why here.
        for (int i = 0; i < 40 && Running; i++) await Task.Delay(100, ct);
        if (!Running) throw new InvalidOperationException(LastLogError() ?? "The overlay stopped right after starting.");
    }

    /// <summary>Asks the overlay to stop (through the stop file, which works whatever its rights) and waits for it.</summary>
    public static async Task StopAsync()
    {
        // Also one this session did not start (the app was closed and opened again while it ran).
        static bool AnyRunning() { try { return Running || Process.GetProcessesByName("dlss5-overlay").Length > 0; } catch { return Running; } }
        if (!AnyRunning()) { _process = null; Target = null; return; }
        try { Directory.CreateDirectory(Folder); File.WriteAllText(StopFile, ""); } catch { }
        for (int i = 0; i < 40 && AnyRunning(); i++) await Task.Delay(100);
        if (Running) { try { _process!.Kill(); } catch { } }
        _process = null;
        Target = null;
    }

    /// <summary>What the running helper says about frame generation, when it could not give what was asked for.</summary>
    public static string? FrameGenNotice()
    {
        try
        {
            using var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var log = new StreamReader(fs).ReadToEnd();
            if (log.Contains("memory: graphics memory is still full"))
                return "Graphics memory is full: lower the game's settings, turn off the game's own frame generation, or set Quality to Performance.";
            if (log.Contains("memory: graphics memory is full"))
                return "Graphics memory was full, so the overlay turned its frame generation off. Lowering the game's settings frees room for it.";
            if (log.Contains("frame generation is not available")) return "Frame generation is not available on this graphics card or driver, so it is off.";
            if (log.Contains("dlssg_for_sm86 did not load")) return "Frame generation could not start on this graphics card (it needs NVIDIA driver R580 or newer).";
            // The helper steps aside when the game leaves it no GPU time, until F9 brings DLSS 5 back.
            var aside = log.LastIndexOf("stepped aside:", StringComparison.Ordinal);
            var back = System.Text.RegularExpressions.Regex.Matches(log, @"DLSS 5 on\r?$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (aside >= 0 && (back.Count == 0 || back[^1].Index < aside))
                return "The game was using the whole graphics card, so the overlay stepped aside. " +
                       (FpsLimit(log) is { } cap ? $"Set the game's own FPS limit to {cap}" : "Set a lower FPS limit in the game") +
                       ", then press F9 in the game to bring DLSS 5 back.";
            var limit = FpsLimit(log) is { } fps && !(Target is { } t && NoFpsLimit.Contains(t.Process))
                ? $" For the smoothest result, set the game's own FPS limit to {fps}." : "";
            // The helper fits frame generation to the screen; the latest decision is what runs now.
            var fit = System.Text.RegularExpressions.Regex.Matches(log, @"frame generation now (\d)x: the game runs at (\d+) fps on a (\d+) Hz screen");
            if (fit.Count > 0)
            {
                var g = fit[^1].Groups;
                return (g[1].Value == "1"
                    ? $"Frame generation is resting: the game already runs at {g[2].Value} fps, as fast as your {g[3].Value} Hz screen."
                    : $"Frame generation {g[1].Value}x: the game runs at {g[2].Value} fps on your {g[3].Value} Hz screen.") + limit;
            }
            var m = System.Text.RegularExpressions.Regex.Match(log, @"asked for (\d) in-between frames, this GPU allows (\d)");
            if (m.Success) return $"This graphics card allows frame generation up to {int.Parse(m.Groups[2].Value) + 1}x, so {int.Parse(m.Groups[2].Value) + 1}x is used." + limit;
            return limit.Length > 0 ? limit.TrimStart() : null;
        }
        catch { return null; }
    }

    /// <summary>With frame generation on, a game drawing more frames than the screen can show of it (refresh / multiplier)
    /// only takes GPU time from the overlay. The game's own FPS limit is the one that reliably holds it there
    /// (NVIDIA's driver cap does not reach a game that is already running).</summary>
    private static int? FpsLimit(string log)
    {
        var fg = System.Text.RegularExpressions.Regex.Match(log, @"frame generation (\d)x \(\d+ in-between");
        var hz = System.Text.RegularExpressions.Regex.Match(log, @"screen refresh: (\d+) Hz");
        if (!fg.Success || !hz.Success || fg.Groups[1].Value == "1") return null;
        return (int)Math.Round(int.Parse(hz.Groups[1].Value, CultureInfo.InvariantCulture) / (double)int.Parse(fg.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>Browsers, the desktop and players: no FPS limit to set.</summary>
    private static readonly HashSet<string> NoFpsLimit = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "explorer", "vlc", "mpc-hc64", "mpc-be64",
        "PotPlayerMini64", "Spotify", "Discord", "ApplicationFrameHost",
    };

    /// <summary>The helper's last "error: ..." line, for telling the user why it stopped.</summary>
    public static string? LastLogError()
    {
        try
        {
            using var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            var line = r.ReadToEnd().Split('\n').LastOrDefault(l => l.Contains("error: "));
            var msg = line?[(line.IndexOf("error: ", StringComparison.Ordinal) + 7)..].Trim();
            return string.IsNullOrEmpty(msg) ? null : char.ToUpperInvariant(msg[0]) + msg[1..] + ".";
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ windows

    /// <summary>Visible top-level windows with a title, other than this app's own, for the window picker.</summary>
    public static List<OverlayWindow> Windows()
    {
        var list = new List<OverlayWindow>();
        var self = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != 0) return true;
            if ((GetWindowLongPtr(h, -20 /* GWL_EXSTYLE */) & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(512);
            if (GetWindowText(h, title, title.Capacity) == 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == self) return true;
            string name = "?";
            string? path = null;
            try { using var proc = Process.GetProcessById((int)pid); name = proc.ProcessName; path = ProcessPath(pid); } catch { }
            if (name is "ApplicationFrameHost" or "TextInputHost" or "SystemSettings" or "dlss5-overlay") return true;
            list.Add(new OverlayWindow(h, title.ToString(), name, path));
            return true;
        }, 0);
        return list;
    }

    private static string? ProcessPath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>True when the window's program runs as administrator. Its token cannot even be opened then.</summary>
    private static bool IsElevated(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var h = OpenProcess(0x1000, false, pid);
        if (h == 0) return true;
        try
        {
            if (!OpenProcessToken(h, 0x8 /* TOKEN_QUERY */, out var token)) return true;
            try { return GetTokenInformation(token, 20 /* TokenElevation */, out int e, 4, out _) && e != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(h); }
    }

    private static bool IsElevatedSelf()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x8, out var token)) return false;
        try { return GetTokenInformation(token, 20, out int e, 4, out _) && e != 0; }
        finally { CloseHandle(token); }
    }

    private delegate bool EnumProc(nint hwnd, nint lp);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint lp);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder name, ref int size);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(nint token, int cls, out int value, int size, out int returned);
}
