using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace DLSS5Master.Core;

internal static class ReShadeVulkanLayer
{
    internal const string NotAvailable = "Needs an existing 64-bit ReShade 6.8 or newer Vulkan layer with full add-on support, enabled for this game.";
    internal const string FeederNotAvailable = "Needs an existing ReShade 6.8 or newer Vulkan layer with full add-on support and the same bitness as the game, enabled for this game.";
    internal const string Need64Bit = "Needs a 64-bit Vulkan game.";

    private const string RegistryPath = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    internal static string? UnavailableReason(string exePath, IReadOnlyList<string>? manifestPaths = null)
    {
        var exe = Path.GetFullPath(exePath);
        if (Pe.GetBitness(exe) != 64) return Need64Bit;
        return CheckLayer(exe, 64, manifestPaths, NotAvailable);
    }

    internal static string? FeederUnavailableReason(string exePath, IReadOnlyList<string>? manifestPaths = null)
    {
        var exe = Path.GetFullPath(exePath);
        var bits = Pe.GetBitness(exe);
        if (bits is not (32 or 64)) return "Needs a 32-bit or 64-bit Vulkan game executable.";
        return CheckLayer(exe, bits, manifestPaths, FeederNotAvailable);
    }

    private static string? CheckLayer(string exe, int bits, IReadOnlyList<string>? manifestPaths, string unavailable)
    {
        var module = $"ReShade{bits}";
        string? pathConflict = null;
        foreach (var manifest in (manifestPaths ?? RegisteredManifests(bits, module)).Distinct(Paths))
        {
            try
            {
                if (ReadLibrary(manifest, exe, bits, module) is not { } library) continue;
                pathConflict = CheckGameConfig(exe, library);
                if (pathConflict is null) return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                or ArgumentException or NotSupportedException or JsonException or InvalidOperationException) { }
        }
        return pathConflict ?? unavailable;
    }

    private static IReadOnlyList<string> RegisteredManifests(int bits, string module)
    {
        var paths = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, bits == 64 ? RegistryView.Registry64 : RegistryView.Registry32);
                using var layers = root.OpenSubKey(RegistryPath);
                if (layers is null) continue;
                foreach (var path in layers.GetValueNames())
                    if (Paths.Equals(Path.GetFileName(path), module + ".json") && layers.GetValue(path) is 0)
                        paths.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return paths;
    }

    private static string? ReadLibrary(string manifest, string exe, int bits, string module)
    {
        if (!Path.IsPathFullyQualified(manifest) || !Paths.Equals(Path.GetFileName(manifest), module + ".json")
            || !File.Exists(manifest) || new FileInfo(manifest).Length is <= 0 or > 65536) return null;
        var moduleDir = Path.GetDirectoryName(manifest)!;
        using var json = JsonDocument.Parse(File.ReadAllBytes(manifest));
        if (!json.RootElement.TryGetProperty("layer", out var layer)
            || !Paths.Equals(StringValue(layer, "name"), "VK_LAYER_reshade")
            || !Paths.Equals(StringValue(layer, "type"), "GLOBAL")
            || StringValue(layer, "library_path") is not { Length: > 0 } libraryPath) return null;
        if (layer.TryGetProperty("disable_environment", out var disable) && disable.ValueKind == JsonValueKind.Object &&
            disable.EnumerateObject().Any(rule => rule.Value.ValueKind == JsonValueKind.String &&
                Environment.GetEnvironmentVariable(rule.Name) == rule.Value.GetString())) return null;
        if (layer.TryGetProperty("enable_environment", out var enable) && enable.ValueKind == JsonValueKind.Object &&
            enable.EnumerateObject().Any(rule => rule.Value.ValueKind != JsonValueKind.String ||
                Environment.GetEnvironmentVariable(rule.Name) != rule.Value.GetString())) return null;

        var library = Path.GetFullPath(libraryPath, moduleDir);
        if (!Paths.Equals(Path.GetFileName(library), module + ".dll") || !ListsGame(exe, moduleDir, module)
            || Pe.GetBitness(library) != bits || !Pe.VersionMentions(library, "ReShade")
            || Pe.GetFileVersion(library) is not { } version || !Scanner.IsVersion(version) || Scanner.CompareVersions(version, "6.8.0") < 0
            || !Scanner.HasAddonSupport(library)
            || Pe.Contains(library, "because this build of ReShade has only limited add-on functionality")) return null;
        return library;
    }

    private static string? StringValue(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool ListsGame(string exe, string layerDir, string module)
    {
        var apps = Path.Combine(layerDir, "ReShadeApps.ini");
        // Newer official setups place the manifest and DLL in an architecture folder.
        if (!File.Exists(apps) && Paths.Equals(Path.GetFileName(layerDir), module))
            apps = Path.Combine(Path.GetDirectoryName(layerDir)!, "ReShadeApps.ini");
        var list = Ini.Get(Ini.ReadText(apps), "", "Apps");
        return list is not null && list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(path => Paths.Equals(path.Trim('"'), exe));
    }

    private static string? CheckGameConfig(string exe, string library)
    {
        var exeDir = Path.GetDirectoryName(exe)!;
        var config = Ini.ReadText(Path.Combine(exeDir, "ReShade.ini"));
        var basePath = Ini.Get(config, "INSTALL", "BasePath");
        if (!UsesGameFolder(basePath, Path.GetDirectoryName(library)!, exeDir))
            return "ReShade's BasePath points away from the game executable folder.";
        var addonPath = Ini.Get(config, "ADDON", "AddonPath");
        if (!UsesGameFolder(addonPath, exeDir, exeDir))
            return "ReShade's AddonPath points away from the game executable folder.";
        return null;
    }

    private static bool UsesGameFolder(string? configured, string parent, string exeDir) =>
        string.IsNullOrWhiteSpace(configured) ||
        Paths.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured.Trim('"'), parent)), exeDir);
}
