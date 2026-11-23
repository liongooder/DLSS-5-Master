using System.Text.Json;
using DLSS5Master.Core;

internal static class VulkanNrFixtures
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        CheckLegacyState(root, check);

        var game = Directory.CreateDirectory(Path.Combine(root, "detroit-vulkan-fixture")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(root, "vulkan-own-addon")).FullName;
        var runtime = Directory.CreateDirectory(Path.Combine(root, "vulkan-runtime")).FullName;
        var layer = Directory.CreateDirectory(Path.Combine(root, "vulkan-reshade-layer")).FullName;
        var exe = Path.Combine(game, "D5M-Detroit-Vulkan-Fixture.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), exe);
        const string addOnBytes = "synthetic player-provided NR add-on";
        File.WriteAllText(Path.Combine(package, NrAddon.Addon64), addOnBytes);
        var model = Path.Combine(runtime, "nvngx_dlssnr.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), model);
        var manifest = StageLayer(layer, Path.Combine(game, "Different.exe"), 64);
        var apps = Path.Combine(layer, "ReShadeApps.ini");
        var config = Path.Combine(game, "ReShade.ini");
        const string originalConfig = "[RenoDX.DLSS5]\nConfigVersion=12\nNRHookPoint=1\n[Personal]\nValue=Keep\n";
        File.WriteAllText(config, originalConfig);
        var original = FileHashes(game);
        var packageBefore = FileHashes(package);
        var runtimeBefore = FileHashes(runtime);
        var components = new Components(new AppSettings { RuntimeFolder = runtime });
        var request = new InstallRequest
        {
            GameDir = game, ExePath = exe, Api = RenderApi.Vulkan, Bitness = 64, Route = Route.NrAddon,
            NrAddonSource = package, VulkanLayerManifests = [manifest],
        };

        string? refusal = null;
        try { await Installers.InstallAsync(request, components, _ => { }, null, CancellationToken.None); }
        catch (InstallException ex) { refusal = ex.Message; }
        check("Vulkan NR refuses a layer allowlist for a different executable before touching the game",
            refusal?.Contains("ReShade", StringComparison.OrdinalIgnoreCase) == true && original.SequenceEqual(FileHashes(game))
            && Journal.ReadManifest(game) is null && !InstallTransaction.Pending(game));

        File.WriteAllText(apps, "Apps=" + exe + "\n");
        var layerBefore = FileHashes(layer);
        check("Vulkan NR recognizes an enabled, add-on-capable layer for the exact executable",
            ReShadeVulkanLayer.UnavailableReason(exe, [manifest]) is null);
        File.WriteAllText(config, originalConfig + "[ADDON]\nAddonPath=" + package + "\n");
        check("Vulkan NR refuses a ReShade add-on path outside the game's executable folder",
            ReShadeVulkanLayer.UnavailableReason(exe, [manifest])?.Contains("AddonPath", StringComparison.OrdinalIgnoreCase) == true);
        File.WriteAllText(config, originalConfig);
        await Installers.InstallAsync(request, components, _ => { }, null, CancellationToken.None);
        var setup = Journal.ReadManifest(game);
        check("Vulkan NR installs the supplied add-on and neural model without a local ReShade proxy or MFG",
            setup is { Route: "nraddon", MfgUnlock: null }
            && File.ReadAllText(Path.Combine(game, NrAddon.Addon64)) == addOnBytes
            && Components.Sha256(Path.Combine(game, "nvngx_dlssnr.dll")) == Components.Sha256(model)
            && !File.Exists(Path.Combine(game, "dxgi.dll")) && !File.Exists(Path.Combine(game, "vulkan-1.dll"))
            && !File.Exists(Path.Combine(game, Components.MfgAddon)));
        var configured = File.ReadAllText(config);
        check("Vulkan NR keeps existing hook-point choices and unrelated local settings",
            Ini.Get(configured, "RenoDX.DLSS5", "NRHookPoint") == "1" && Ini.Get(configured, "Personal", "Value") == "Keep");
        File.WriteAllText(Path.Combine(game, "ReShade.log"), "simulated layer diagnostic created after install");
        await Installers.RestoreAsync(game, exe, _ => { });
        // Restore closes the install record in the app's backup folder for troubleshooting.
        var restoredFiles = FileHashes(game).Where(file =>
            !file.Name.StartsWith(Journal.BackupDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        check("Vulkan NR Restore originals returns the game's files and leaves shared files and the source package intact",
            original.SequenceEqual(restoredFiles) && layerBefore.SequenceEqual(FileHashes(layer))
            && packageBefore.SequenceEqual(FileHashes(package)) && runtimeBefore.SequenceEqual(FileHashes(runtime))
            && Journal.ReadManifest(game) is null && !InstallTransaction.Pending(game));
    }

    private static void CheckLegacyState(string root, Action<string, bool> check)
    {
        var data = Path.Combine(root, "data");
        if (!AppPaths.Root.Equals(data, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Legacy migration fixtures require an isolated application data directory.");
        Directory.CreateDirectory(data);

        var game = Directory.CreateDirectory(Path.Combine(root, "nr-upgrade-fixture")).FullName;
        var exe = Path.Combine(game, "Game.exe");
        Directory.CreateDirectory(Journal.BackupRoot(game));
        var manifestPath = Journal.ManifestPath(game);
        const string oldManifest = """
            {"Route":"lecramnr","Exe":"Game.exe","Added":["ReShade.ini"]}
            """;
        File.WriteAllText(manifestPath, oldManifest);
        check("0.9.13 NR records use the current route in memory and keep the original on disk",
            Journal.ReadManifest(game) is { Route: "nraddon", Exe: "Game.exe", Added: ["ReShade.ini"] }
            && File.ReadAllText(manifestPath) == oldManifest);

        var settingsFile = AppPaths.SettingsFile;
        var originalSettings = File.Exists(settingsFile) ? File.ReadAllBytes(settingsFile) : null;
        try
        {
            File.WriteAllText(settingsFile, """{"LecramNrFolder":"old-add-on"}""");
            check("0.9.13 settings retain the selected NR folder", AppSettings.Load().NrFolder == "old-add-on");
            File.WriteAllText(settingsFile, """{"LecramNrFolder":"old-add-on","NrFolder":"new-add-on"}""");
            check("an explicit current NR folder takes precedence over the old folder", AppSettings.Load().NrFolder == "new-add-on");
            File.WriteAllText(settingsFile, """{"LecramNrFolder":"old-add-on","NrFolder":""}""");
            check("clearing the current NR folder does not restore the old folder", AppSettings.Load().NrFolder == "");
        }
        finally
        {
            if (originalSettings is null) File.Delete(settingsFile);
            else File.WriteAllBytes(settingsFile, originalSettings);
        }

        var profileDir = Directory.CreateDirectory(Path.Combine(data, "route-profiles", InstallTransaction.Key(game))).FullName;
        var exeKey = InstallTransaction.Key(exe);
        var buildKey = InstallTransaction.Key(Path.Combine(game, "default"));
        void WriteProfile(string route, string tuning)
        {
            var settings = new[] { new KeptSetting("ReShade.ini", System.Text.Encoding.UTF8.GetBytes(tuning)) };
            File.WriteAllText(Path.Combine(profileDir, $"{exeKey}-{route}-{buildKey}.json"),
                JsonSerializer.Serialize(new { Bitness = 64, Settings = settings }));
        }

        string? LoadTuning()
        {
            var setting = RouteProfiles.Load(game, exe, Route.NrAddon, 64, false, null).SingleOrDefault();
            return setting is { Rel: "ReShade.ini" } ? System.Text.Encoding.UTF8.GetString(setting.Bytes) : null;
        }

        WriteProfile("lecramnr", "legacy NR tuning");
        check("0.9.13 NR route profiles are available after upgrade", LoadTuning() == "legacy NR tuning");
        WriteProfile("nraddon", "current NR tuning");
        check("the current NR route profile takes precedence over the old profile", LoadTuning() == "current NR tuning");
    }

    internal static string BundledReShadeFolder => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/DLSS5Master/payload/reshade"));

    internal static string StageLayer(string folder, string enabledExe, int bitness)
    {
        var basename = $"ReShade{bitness}";
        var library = basename + ".dll";
        var manifest = Path.Combine(folder, basename + ".json");
        File.Copy(Path.Combine(BundledReShadeFolder, library), Path.Combine(folder, library));
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            file_format_version = "1.2.0",
            layer = new { name = "VK_LAYER_reshade", type = "GLOBAL", library_path = @".\" + library, api_version = "1.3.0", implementation_version = "1", description = "ReShade Vulkan layer" }
        }));
        File.WriteAllText(Path.Combine(folder, "ReShadeApps.ini"), "Apps=" + enabledExe + "\n");
        return manifest;
    }

    internal static (string Name, string Sha256)[] FileHashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => (Path.GetRelativePath(root, path), Components.Sha256(path))).ToArray();
}
