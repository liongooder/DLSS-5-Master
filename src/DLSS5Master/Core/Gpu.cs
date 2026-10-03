using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DLSS5Master.Core;

/// <summary>Display adapters from the registry (no WMI dependency).</summary>
public static partial class Gpu
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    [GeneratedRegex(@"RTX\s*40\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex Rtx40();

    private static readonly Lazy<List<string>> _names = new(Read);
    public static IReadOnlyList<string> Names => _names.Value;

    /// <summary>MFGAdaUnlock targets GeForce RTX 40-series (Ada) cards.</summary>
    public static bool HasRtx40 => Names.Any(n => Rtx40().IsMatch(n));
    public static string? Nvidia => Names.FirstOrDefault(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));

    private static List<string> Read()
    {
        var list = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls is null) return list;
            foreach (var sub in cls.GetSubKeyNames().Where(s => Regex.IsMatch(s, @"^\d{4}$")))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string name && !list.Contains(name)) list.Add(name);
            }
        }
        catch { }
        return list;
    }
}
