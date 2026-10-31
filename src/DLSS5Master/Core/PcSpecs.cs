using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DLSS5Master.Core;

/// <summary>
/// The PC a community report ran on besides its graphics card (see <see cref="Gpu"/>): processor, memory and Windows.
/// Read from the registry and Windows itself (no WMI), once per run.
/// </summary>
public static partial class PcSpecs
{
    private static readonly Lazy<string> _cpu = new(() => CleanCpu(ReadString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")));
    private static readonly Lazy<int> _ramGb = new(ReadRamGb);
    private static readonly Lazy<string> _windows = new(ReadWindows);

    /// <summary>The processor, such as "AMD Ryzen 7 7800X3D"; "" when unknown.</summary>
    public static string Cpu => _cpu.Value;

    /// <summary>Installed memory in GB; 0 when unknown.</summary>
    public static int RamGb => _ramGb.Value;

    /// <summary>Windows, such as "Windows 11 24H2 (26100)"; "" when unknown.</summary>
    public static string Windows => _windows.Value;

    [GeneratedRegex(@"\((R|TM)\)", RegexOptions.IgnoreCase)]
    private static partial Regex Trademarks();

    // " CPU @ 3.70GHz" (Intel before Core Ultra)
    [GeneratedRegex(@"\s*(CPU\s*)?@\s*[\d.]+\s*[GM]Hz", RegexOptions.IgnoreCase)]
    private static partial Regex Clock();

    // " 8-Core Processor" (AMD)
    [GeneratedRegex(@"\s+\d+-Core\s+Processor\b", RegexOptions.IgnoreCase)]
    private static partial Regex CoreCount();

    // " with Radeon Graphics", " w/ Radeon 780M Graphics" (AMD processors with built-in graphics)
    [GeneratedRegex(@"\s+(with|w/)\s+Radeon\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex BuiltInGraphics();

    [GeneratedRegex(@"\s+Processor\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingProcessor();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex("^[0-9A-Za-z]{2,8}$")]
    private static partial Regex ReleaseName();

    /// <summary>
    /// The processor's name without trademarks, clock speed, core count and built-in graphics:
    /// "Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz" is "Intel Core i7-8700K", "AMD Ryzen 7 7800X3D 8-Core Processor" is "AMD Ryzen 7 7800X3D".
    /// </summary>
    internal static string CleanCpu(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = Trademarks().Replace(raw, "");
        s = Clock().Replace(s, "");
        s = CoreCount().Replace(s, "");
        s = BuiltInGraphics().Replace(s, "");
        s = TrailingProcessor().Replace(s, "");
        s = Spaces().Replace(s, " ").Trim();
        return s.Length > 64 ? s[..64].TrimEnd() : s;
    }

    /// <summary>"Windows 11 24H2 (26100)". Windows 11 is build 22000 on, though its registry still calls itself Windows 10.</summary>
    internal static string WindowsLabel(int build, string? release)
    {
        if (build <= 0) return "";
        release = release?.Trim() ?? "";
        return $"Windows {(build >= 22000 ? "11" : "10")}{(ReleaseName().IsMatch(release) ? " " + release : "")} ({build.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string ReadWindows()
    {
        int build = Environment.OSVersion.Version.Build;
        string? release = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (int.TryParse(k?.GetValue("CurrentBuildNumber") as string, NumberStyles.None, CultureInfo.InvariantCulture, out var b)) build = b;
            // "24H2" since Windows 10 20H2; ReleaseId ("2004") before that.
            release = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string;
        }
        catch { }
        return WindowsLabel(build, release);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    private static int ReadRamGb()
    {
        try
        {
            // The memory sticks' total, as the firmware lists them; fails in some virtual machines (then unknown).
            if (GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0) return (int)Math.Round(kb / (1024.0 * 1024.0));
        }
        catch { }
        return 0;
    }

    private static string? ReadString(string key, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(name) as string;
        }
        catch { return null; }
    }
}
