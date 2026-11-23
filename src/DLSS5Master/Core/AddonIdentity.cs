using System.Diagnostics;

namespace DLSS5Master.Core;

public sealed record AddonIdentity(string Name, string? Version)
{
    public static string SafeName(string? text) => !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= 64 &&
        !text.Any(c => char.IsControl(c) || c is '<' or '>' or '/' or '\\') ? text.Trim() : "DLSS5 NR";
    public static AddonIdentity Read(string? folder, int bits = 64)
    {
        if (folder is not null)
        {
            foreach (var name in bits == 32 ? new[] { NrAddon.Addon32, NrAddon.Addon64 } : new[] { NrAddon.Addon64, NrAddon.Addon32 })
            {
                var file = Path.Combine(folder, name);
                if (!File.Exists(file)) continue;
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(file);
                    return new(SafeName(info.ProductName), string.IsNullOrWhiteSpace(info.FileVersion) ? Pe.GetFileVersion(file) : info.FileVersion.Trim());
                }
                catch { }
            }
        }
        return new("DLSS5 NR", null);
    }
    public static string InstalledName(string game, Manifest m)
    {
        if (!string.IsNullOrWhiteSpace(m.AddonName)) return SafeName(m.AddonName);
        try { return Read(Path.GetDirectoryName(Journal.SafePath(game, m.Exe)), m.Bitness).Name; }
        catch { return "DLSS5 NR"; }
    }
}
