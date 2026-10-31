using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DLSS5Master.Core;

/// <summary>Display adapters from the registry (no WMI dependency).</summary>
public static partial class Gpu
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    [GeneratedRegex(@"RTX\s*40\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex Rtx40();

    // Turing and Ampere: GeForce RTX 20 / 30, RTX A-series and Quadro RTX workstation cards.
    [GeneratedRegex(@"RTX\s*(20|30)\d{2}|RTX\s*A\d{3,4}\b|Quadro\s*RTX", RegexOptions.IgnoreCase)]
    private static partial Regex Rtx20or30();

    private static readonly Lazy<List<(string Name, string Driver)>> _adapters = new(Read);
    public static IReadOnlyList<string> Names => _adapters.Value.Select(a => a.Name).ToList();

    /// <summary>MFGAdaUnlock's default DLSS-G mode targets GeForce RTX 40-series (Ada) cards.</summary>
    public static bool HasRtx40 => Names.Any(n => Rtx40().IsMatch(n));

    /// <summary>An RTX 20 / 30 card: DLSS Frame Generation needs the dlssg_for_sm86 unlock there.</summary>
    public static bool HasRtx20or30 => Names.Any(n => Rtx20or30().IsMatch(n));

    /// <summary>Any GeForce RTX card (20-series or newer): enough for MFGAdaUnlock's XeMFG mode.</summary>
    public static bool HasRtx => Names.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && n.Contains("RTX", StringComparison.OrdinalIgnoreCase));
    public static string? Nvidia => Names.FirstOrDefault(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The driver of the adapter called <paramref name="name"/>: NVIDIA's own numbering for an NVIDIA card ("581.57"),
    /// Windows' version for any other ("32.0.101.6979"); "" when unknown.
    /// </summary>
    public static string DriverFor(string? name)
    {
        var version = _adapters.Value.FirstOrDefault(a => a.Name == name).Driver;
        if (string.IsNullOrEmpty(version)) return "";
        return name!.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? NvidiaDriver(version) : version;
    }

    /// <summary>
    /// NVIDIA's driver number from Windows' version of it: the last five digits of its last two parts, so 32.0.15.8157 is
    /// driver 581.57. Anything else comes back unchanged.
    /// </summary>
    internal static string NvidiaDriver(string windowsVersion)
    {
        var parts = windowsVersion.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)) || parts[3].Length > 4) return windowsVersion;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return windowsVersion;
        digits = digits[^5..];
        return int.Parse(digits[..3], NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "." + digits[3..];
    }

    private static List<(string Name, string Driver)> Read()
    {
        var list = new List<(string Name, string Driver)>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls is null) return list;
            // A card taken out keeps its driver key (with its old name and driver): only the adapters present now count.
            var present = PresentDriverKeys();
            var subkeys = cls.GetSubKeyNames().Where(s => Regex.IsMatch(s, @"^\d{4}$")).ToList();
            if (present is not null && subkeys.Any(present.Contains)) subkeys = subkeys.Where(present.Contains).ToList();
            foreach (var sub in subkeys)
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string name && !list.Any(a => a.Name == name))
                    list.Add((name, k.GetValue("DriverVersion") as string ?? ""));
            }
        }
        catch { }
        return list;
    }

    // ------------------------------------------------------------------ adapters present now (SetupAPI)

    private const uint DIGCF_PRESENT = 0x2;
    private const uint SPDRP_DRIVER = 0x9;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    /// <summary>
    /// The display class subkeys ("0000") whose adapters Windows lists as present, from each one's driver key
    /// "{4d36e968-…}\0000"; null when Windows cannot say (then every subkey counts, as before).
    /// </summary>
    private static HashSet<string>? PresentDriverKeys()
    {
        var display = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        var set = SetupDiGetClassDevsW(ref display, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
        try
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buffer = new byte[512];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SPDRP_DRIVER, out _, buffer, (uint)buffer.Length, out var size) || size < 4) continue;
                var driverKey = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
                var slash = driverKey.LastIndexOf('\\');
                if (slash >= 0) keys.Add(driverKey[(slash + 1)..]);
            }
            return keys.Count > 0 ? keys : null;
        }
        catch { return null; }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
