using System.IO.Compression;

namespace DLSS5Master.Core;

/// <summary>Stages a user-selected Feeder version without changing the source ZIP or any game's files.</summary>
public static class FeederZipImport
{
    private const string ShaderName = "DLSS5_Feed.fx";
    private const string ShaderPath = "reshade-shaders/Shaders/" + ShaderName;
    private const string Verifier = "Verify-DLSS5Feeder.ps1";
    private const string SourceName = "source-zip.txt";
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

            if (Components.FindFeederAny(destination) is not { } payload || !HasValidBinaries(payload))
                throw new InstallException("The ZIP has unreadable Feeder binaries or a shader with no contents.");
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
            return Components.FindFeederAny(directory) is { } payload && HasValidBinaries(payload) ? (name, payload) : default;
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
