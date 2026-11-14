using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DLSS5Master.Core;

public sealed record ComponentPin(string RelativePath, string Source, string Sha256);
public sealed record ComponentGroup(string Id, string Name, string Version, IReadOnlyList<ComponentPin> Files);
public sealed record ComponentInspection(ComponentGroup Group, int Missing, int Different, IReadOnlyList<string> Problems)
{
    public bool Ready => Missing == 0 && Different == 0;
}

/// <summary>Checks included components; repairs write only to a private cache, never the app or games.</summary>
public static class ComponentHealth
{
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Master-ComponentRepair");
        return client;
    }

    private static readonly Lazy<IReadOnlyList<ComponentGroup>> Catalog = new(ReadCatalog);
    public static IReadOnlyList<ComponentGroup> Groups => Catalog.Value;

    private static IReadOnlyList<ComponentGroup> ReadCatalog()
    {
        using var stream = typeof(ComponentHealth).Assembly.GetManifestResourceStream("DLSS5Master.ComponentManifest");
        // Core-only probes can compile without the app's embedded resources.
        if (stream is null) return Array.Empty<ComponentGroup>();
        using var reader = new StreamReader(stream);
        var files = new List<ComponentPin>();
        reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split(',');
            if (fields.Length != 3 || fields[2].Length != 64) throw new InvalidDataException("Invalid component manifest.");
            files.Add(new("payload/" + fields[0].Replace('\\', '/'), fields[1], fields[2]));
        }
        ComponentGroup Group(string id, string name, string version, Func<ComponentPin, bool> pick) =>
            new(id, name, version, files.Where(pick).ToList());
        string VersionOf(string id) => Updates.Sources.Single(s => s.Id == id).BundledVersion;
        return new[]
        {
            Group("runtime", "NVIDIA runtimes and Streamline", "Included runtime set", p => p.RelativePath.StartsWith("payload/runtime/")),
            Group("reshade", "ReShade with add-on support", "6.8.0", p => p.RelativePath.StartsWith("payload/reshade/")),
            Group("renodx-dlss5", "RenoDX DLSS5 add-on", VersionOf("renodx-dlss5"), p => p.RelativePath.EndsWith("/renodx-dlss5.addon64")),
            Group("renodx-dlss", "RenoDX DLSS Tool (multipass)", VersionOf("renodx-dlss"), p => p.RelativePath.EndsWith("/renodx-dlss.addon64")),
            new ComponentGroup("mfgunlock", "MFG Unlock", Components.MfgVersion,
                new[] { new ComponentPin("addons/" + Components.MfgAddon, Components.MfgUrl, Components.MfgSha256) }),
            Group("feeder", "DLSS5 Feeder", VersionOf("feeder"), p => p.RelativePath.StartsWith("payload/feeder/")),
            Group("optiscaler-nr", "OptiScaler NR", VersionOf("optiscaler-nr"), p => p.RelativePath.StartsWith("payload/optiscaler/")),
        };
    }

    private static string RepairRoot => Path.Combine(AppPaths.Components, "repairs");
    private static string Stamp(ComponentGroup group) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        group.Id + string.Concat(group.Files.Select(p => "\n" + p.RelativePath + ":" + p.Sha256)))));
    private static string Pointer(ComponentGroup group) => Path.Combine(RepairRoot, Stamp(group) + ".json");
    private static string? RepairedRoot(ComponentGroup group)
    {
        try
        {
            var id = JsonSerializer.Deserialize<string>(File.ReadAllText(Pointer(group)));
            if (!Guid.TryParseExact(id, "N", out _)) return null;
            var dir = Path.Combine(RepairRoot, id!);
            return Directory.Exists(dir) ? dir : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static string Resolve(ComponentGroup group, ComponentPin file, string? bundledRoot = null) =>
        Journal.SafePath(RepairedRoot(group) ?? bundledRoot ?? AppContext.BaseDirectory, file.RelativePath);

    public static string IncludedFile(string relative)
    {
        var key = relative.Replace('\\', '/');
        var group = Groups.FirstOrDefault(g => g.Files.Any(p => p.RelativePath.Equals(key, StringComparison.OrdinalIgnoreCase)));
        return group is null ? Journal.SafePath(AppContext.BaseDirectory, key)
            : Resolve(group, group.Files.First(p => p.RelativePath.Equals(key, StringComparison.OrdinalIgnoreCase)));
    }

    public static string IncludedDirectory(string groupId, string relative)
    {
        var group = Groups.FirstOrDefault(g => g.Id == groupId);
        return Journal.SafePath(group is null ? AppContext.BaseDirectory : RepairedRoot(group) ?? AppContext.BaseDirectory, relative);
    }

    public static bool Matches(string path, string sha)
    {
        try { return File.Exists(path) && Components.Sha256(path).Equals(sha, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static ComponentInspection Inspect(ComponentGroup group, string? bundledRoot = null)
    {
        int missing = 0, different = 0;
        var problems = new List<string>();
        foreach (var pin in group.Files)
        {
            var file = Resolve(group, pin, bundledRoot);
            if (!File.Exists(file)) { missing++; problems.Add("Missing: " + pin.RelativePath); }
            else if (!Matches(file, pin.Sha256)) { different++; problems.Add("Checksum mismatch or unreadable: " + pin.RelativePath); }
        }
        return new(group, missing, different, problems);
    }

    public static Task RepairAsync(ComponentGroup group, IProgress<string>? progress, CancellationToken ct = default) =>
        RepairAsync(group, progress, ct, AppContext.BaseDirectory, FetchAsync);

    internal static async Task RepairAsync(ComponentGroup group, IProgress<string>? progress, CancellationToken ct,
        string bundledRoot, Func<ComponentPin, string, CancellationToken, Task> fetch)
    {
        Directory.CreateDirectory(RepairRoot);
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(RepairRoot, id);
        bool committed = false;
        try
        {
            foreach (var pin in group.Files)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("Checking " + pin.RelativePath + "…");
                var target = Journal.SafePath(stage, pin.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var current = Resolve(group, pin, bundledRoot);
                var original = Journal.SafePath(bundledRoot, pin.RelativePath);
                if (Matches(current, pin.Sha256)) File.Copy(current, target);
                else if (Matches(original, pin.Sha256)) File.Copy(original, target);
                else
                {
                    progress?.Report("Repairing " + pin.RelativePath + "…");
                    await fetch(pin, target, ct);
                }
                if (!Matches(target, pin.Sha256)) throw new InstallException("Repair failed its checksum: " + pin.RelativePath);
            }
            ct.ThrowIfCancellationRequested();
            // Publish only a complete, checked set. Previous repairs remain available if this one fails.
            var pointer = Pointer(group);
            var temp = pointer + "." + id + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(id), ct);
            File.Move(temp, pointer, true);
            committed = true;
        }
        finally
        {
            if (!committed && Path.GetFullPath(stage).StartsWith(Path.GetFullPath(RepairRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task FetchAsync(ComponentPin pin, string target, CancellationToken ct)
    {
        var cache = Path.Combine(AppPaths.Components, "repair-downloads");
        Directory.CreateDirectory(cache);
        var source = pin.Source;
        if (!source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (pin.RelativePath.StartsWith("payload/reshade/"))
            {
                var setup = Path.Combine(cache, "ReShade_Setup_6_8_0_Addon.exe");
                await Components.DownloadVerifiedAsync("https://reshade.me/downloads/ReShade_Setup_6.8.0_Addon.exe",
                    "afe4c8f13048306307983b8b3d41d5bf00a86820440b0e57dea10950e1176445", setup, null, ct);
                var dll = Components.ExtractFromReShadeSetup(setup, Path.GetFileName(pin.RelativePath))
                    ?? throw new InstallException("ReShade setup does not contain the required DLL.");
                File.Copy(dll, target);
                return;
            }
            if (pin.RelativePath.StartsWith("payload/runtime/"))
                source = "https://github.com/RankFTW/rhi-repo/releases/download/streamline-2.13.0.0/streamline_2.13.0.0.zip";
            else if (pin.RelativePath.StartsWith("payload/feeder/")) source = FeederSource(pin.RelativePath["payload/feeder/".Length..]);
            else throw new InstallException("This file needs the app installer to repair it: " + pin.RelativePath);
        }
        if (!new Uri(source).AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await Components.DownloadVerifiedAsync(source, pin.Sha256, target, null, ct);
            return;
        }
        // Release archives are checked against GitHub's published digest, then against the per-file pin.
        var uri = new Uri(source);
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (uri.Host != "github.com" || parts.Length != 6 || parts[2] != "releases" || parts[3] != "download")
            throw new InstallException("Unsupported repair archive source.");
        using var json = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/tags/{parts[4]}", ct));
        var asset = json.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == Uri.UnescapeDataString(parts[5]));
        var digest = asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (digest is null || !digest.StartsWith("sha256:") || digest.Length != 71)
            throw new InstallException("GitHub did not publish a SHA-256 for this repair archive.");
        var zip = Path.Combine(cache, digest[7..] + ".zip");
        await Components.DownloadVerifiedAsync(source, digest[7..], zip, null, ct);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries.Where(e => e.Name.Equals(Path.GetFileName(pin.RelativePath), StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            entry.ExtractToFile(target, true);
            if (Matches(target, pin.Sha256)) return;
        }
        throw new InstallException("The release archive lacks the expected file: " + pin.RelativePath);
    }

    private static string FeederSource(string relative) => relative switch
    {
        "licenses/DLSS5-Feeder-LICENSE.txt" => "https://raw.githubusercontent.com/jlrouzies-fr/DLSS5-Feeder/v1.19.0/LICENSE",
        "licenses/VORT-LICENSE.txt" => "https://raw.githubusercontent.com/vortigern11/vort_Shaders/b410b9f0c0fbb83c8cb42164aaf1655fab386f4a/LICENSE",
        _ when relative.StartsWith("reshade-shaders/") =>
            "https://raw.githubusercontent.com/" + (Path.GetFileName(relative) is "ReShade.fxh" or "ReShadeUI.fxh"
                ? "crosire/reshade-shaders/ee30868391d4ad103db60489820102d8fd40e3c1/"
                : "vortigern11/vort_Shaders/b410b9f0c0fbb83c8cb42164aaf1655fab386f4a/") + relative["reshade-shaders/".Length..],
        _ => throw new InstallException("Unknown Feeder dependency: " + relative)
    };
}
