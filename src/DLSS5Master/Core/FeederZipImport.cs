using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace DLSS5Master.Core;

/// <summary>Stages a user-selected Feeder version without changing the source ZIP or any game's files.</summary>
public static class FeederZipImport
{
    internal const string DesktopPanelName = "dlss5-feed-panel.exe";
    private const string ShaderName = "DLSS5_Feed.fx";
    private const string ShaderPath = "reshade-shaders/Shaders/" + ShaderName;
    private const string Verifier = "Verify-DLSS5Feeder.ps1";
    private const string SourceName = "source-zip.txt";
    private const string DesktopPanelReceipt = "desktop-panel.sha256";
    private const string LiveOptiMarker = "feeder-optiscaler-live.txt";
    private const string LiveOptiVersion = "0.8.93";
    private const string LiveOptiLibrary = "OptiScaler-Feeder.dll";
    private const long MaxDesktopPanelBytes = 128L * 1024 * 1024;
    private const string PayloadPrefix = "payload/feeder/";

    public static string Prepare(string zipPath, string? cacheRoot = null, string? bundledRoot = null)
    {
        if (!File.Exists(zipPath) || !Path.GetExtension(zipPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InstallException("Choose a DLSS5 Feeder ZIP file.");

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Where(entry => entry.Name.Length > 0).ToLookup(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        // Each required file has to come from this release. Using the included binaries for missing members can
        // combine incompatible 32-bit, 64-bit and helper versions.
        var required = Components.FeederHashes.Keys.ToDictionary(name => name, name => Entry(entries, name, required: true)!);
        var verifier = Entry(entries, Verifier, required: false);
        var panel = Entry(entries, DesktopPanelName, required: false);
        var liveOpti = Entry(entries, LiveOptiMarker, required: false);
        var liveLibrary = Entry(entries, LiveOptiLibrary, required: liveOpti is not null);
        if (liveOpti is null && liveLibrary is not null)
            throw new InstallException("The DLSS5 Feeder ZIP is missing its live OptiScaler marker.");
        if (liveLibrary is { Length: > MaxDesktopPanelBytes })
            throw new InstallException($"{LiveOptiLibrary} is larger than 128 MiB.");
        if (liveOpti is not null)
        {
            if (liveOpti.Length > 16) throw new InstallException("The DLSS5 Feeder ZIP has an invalid live OptiScaler marker.");
            using var reader = new StreamReader(liveOpti.Open());
            if (reader.ReadToEnd().Trim() != LiveOptiVersion)
                throw new InstallException("The DLSS5 Feeder ZIP requires an unsupported live OptiScaler version.");
        }
        if (panel is not null && panel.Length > MaxDesktopPanelBytes)
            throw new InstallException($"{DesktopPanelName} is larger than 128 MiB.");
        var root = Path.GetFullPath(cacheRoot ?? Path.Combine(AppPaths.Root, "feeder-imports"));
        var destination = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
            CopySupportFiles(destination, bundledRoot ?? ComponentHealth.IncludedDirectory("feeder", "payload/feeder"));
            foreach (var (name, entry) in required)
            {
                var target = Path.Combine(destination, name == ShaderName ? ShaderPath : name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }
            verifier?.ExtractToFile(Path.Combine(destination, Verifier));
            if (panel is not null)
            {
                var path = Path.Combine(destination, DesktopPanelName);
                panel.ExtractToFile(path);
                var hash = DesktopPanelHash(path) ??
                    throw new InstallException($"{DesktopPanelName} must be a Windows x64 executable.");
                File.WriteAllText(Path.Combine(destination, DesktopPanelReceipt), hash);
            }

            if (Components.FindFeederAny(destination) is not { } payload || !HasValidBinaries(payload))
                throw new InstallException("The ZIP has unreadable Feeder binaries or a shader with no contents.");
            if (liveLibrary is not null)
            {
                var library = Path.Combine(destination, LiveOptiLibrary);
                liveLibrary.ExtractToFile(library);
                if (!HasPinnedLiveLibrary(library))
                    throw new InstallException("The DLSS5 Feeder ZIP does not contain the verified live OptiScaler library.");
                File.WriteAllText(Path.Combine(destination, LiveOptiMarker), LiveOptiVersion);
            }
            File.WriteAllText(Path.Combine(destination, SourceName), Path.GetFileName(zipPath));
            return destination;
        }
        catch
        {
            if (Path.GetFullPath(destination).StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(destination, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    public static string? DisplayName(string directory) => ReadSelection(directory).Name;

    internal static bool HasLiveOptiScalerSelection(string directory) => !string.IsNullOrWhiteSpace(directory) &&
        File.Exists(Path.Combine(directory, LiveOptiMarker)) && File.Exists(Path.Combine(directory, LiveOptiLibrary));

    internal static (string? Name, Components.FeederPayload? Payload) ReadSelection(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return default;
        try
        {
            var source = Path.Combine(directory, SourceName);
            if (!File.Exists(source) || new FileInfo(source).Length is 0 or > 1024) return default;
            var name = File.ReadAllText(source);
            if (name != Path.GetFileName(name) || !Path.GetExtension(name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                return default;
            if (Components.FindFeederAny(directory) is not { } payload || !HasValidBinaries(payload)) return default;

            var liveOpti = Path.Combine(directory, LiveOptiMarker);
            var liveLibrary = Path.Combine(directory, LiveOptiLibrary);
            if (File.Exists(liveOpti) || File.Exists(liveLibrary))
            {
                if (!File.Exists(liveOpti) || new FileInfo(liveOpti).Length != LiveOptiVersion.Length ||
                    File.ReadAllText(liveOpti) != LiveOptiVersion || !HasPinnedLiveLibrary(liveLibrary))
                    return default;
                payload = payload with { LiveOptiScalerLibrary = liveLibrary };
            }

            var panel = Path.Combine(directory, DesktopPanelName);
            var receipt = Path.Combine(directory, DesktopPanelReceipt);
            if (!File.Exists(panel) && !File.Exists(receipt)) return (name, payload);
            if (!File.Exists(panel) || !File.Exists(receipt) || new FileInfo(receipt).Length != 64) return default;
            if (!string.Equals(DesktopPanelHash(panel), File.ReadAllText(receipt), StringComparison.OrdinalIgnoreCase)) return default;
            return (name, payload with { DesktopPanel = panel });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return default; }
    }

    private static ZipArchiveEntry? Entry(ILookup<string, ZipArchiveEntry> entries, string name, bool required)
    {
        var matches = entries[name].ToList();
        if (matches.Count == 1 && matches[0].Length > 0) return matches[0];
        if (matches.Count == 0 && !required) return null;
        throw new InstallException($"The DLSS5 Feeder ZIP must contain one usable copy of {name}.");
    }

    private static bool HasValidBinaries(Components.FeederPayload payload) =>
        Pe.GetBitness(payload.Addon32) == 32 && Pe.GetBitness(payload.Addon64) == 64 &&
        Pe.GetBitness(payload.Host64) == 64 &&
        new FileInfo(Path.Combine(payload.ShaderRoot, "Shaders", ShaderName)).Length > 0;

    private static bool HasPinnedLiveLibrary(string path) => File.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
        new FileInfo(path).Length is > 0 and <= MaxDesktopPanelBytes && Pe.GetBitness(path) == 64 &&
        Components.HashMatches(path, Components.FeederLiveOptiLibrarySha256);

    private static string? DesktopPanelHash(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > MaxDesktopPanelBytes) return null;
        try
        {
            using var image = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var headers = image.PEHeaders;
            const Characteristics kind = Characteristics.ExecutableImage | Characteristics.Dll;
            if (headers.PEHeader?.Magic != PEMagic.PE32Plus || headers.CoffHeader.Machine != Machine.Amd64 ||
                (headers.CoffHeader.Characteristics & kind) != Characteristics.ExecutableImage) return null;
            stream.Position = 0;
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (BadImageFormatException) { return null; }
    }

    private static void CopySupportFiles(string destination, string bundledRoot)
    {
        // The standalone release does not carry the ReShade headers and VORT files the manager configures.
        // Take those exact files from the app's component manifest and leave ZIP entries outside this route alone.
        var group = ComponentHealth.Groups.FirstOrDefault(group => group.Id == "feeder")
                    ?? throw new InstallException("The included Feeder dependencies are unavailable. Repair DLSS5 Feeder in Settings.");
        foreach (var pin in group.Files)
        {
            var relative = pin.RelativePath[PayloadPrefix.Length..];
            if ((!relative.StartsWith("reshade-shaders/", StringComparison.Ordinal) &&
                 !relative.StartsWith("licenses/", StringComparison.Ordinal)) || relative == ShaderPath) continue;
            var source = Path.Combine(bundledRoot, relative);
            if (!ComponentHealth.Matches(source, pin.Sha256))
                throw new InstallException($"The included Feeder file {relative} is missing or damaged. Repair DLSS5 Feeder in Settings.");
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }
}
