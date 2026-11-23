using DLSS5Master.Core;

internal static class FeederRendererFixtures
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        var feeder = Directory.CreateDirectory(Path.Combine(root, "feeder-fixture-package")).FullName;
        var shaders = Directory.CreateDirectory(Path.Combine(feeder, "reshade-shaders", "Shaders")).FullName;
        foreach (var name in Components.FeederHashes.Keys)
            File.WriteAllText(Path.Combine(name.EndsWith(".fx", StringComparison.OrdinalIgnoreCase) ? shaders : feeder, name), "fixture " + name);
        File.WriteAllText(Path.Combine(shaders, "vort_Motion.fx"), "fixture motion shader");
        File.WriteAllText(Path.Combine(shaders, "ReShade.fxh"), "fixture shader header");
        var consumer = Path.Combine(feeder, "renodx-dlss5.addon64");
        File.WriteAllText(consumer, "fixture consumer");
        var runtime = Directory.CreateDirectory(Path.Combine(root, "feeder-fixture-runtime")).FullName;
        var dll = Path.Combine(Environment.SystemDirectory, "version.dll");
        foreach (var name in new[] { "nvngx_dlss.dll", "nvngx_dlssnr.dll" }) File.Copy(dll, Path.Combine(runtime, name));
        var components = new Components(new AppSettings { RuntimeFolder = runtime, ReShadeSource = VulkanNrFixtures.BundledReShadeFolder });
        var updates = Updates.State.Active;
        updates.TryGetValue("feeder", out var previousFeeder);
        updates.TryGetValue("renodx-dlss5", out var previousConsumer);
        updates["feeder"] = new ActiveUpdate { Version = "fixture", Path = feeder };
        updates["renodx-dlss5"] = new ActiveUpdate { Version = "fixture", Path = consumer };
        try
        {
            foreach (var (api, bits, proxy) in new[]
            {
                (RenderApi.Vulkan, 64, (string?)null), (RenderApi.Vulkan, 32, null),
                (RenderApi.OpenGL, 64, "opengl32.dll"), (RenderApi.OpenGL, 32, "opengl32.dll"),
                (RenderApi.DX11, 64, "dxgi.dll"), (RenderApi.DX12, 64, "dxgi.dll"),
            })
                await AssertInstallAndRestore(root, api, bits, proxy, components, check);
        }
        finally
        {
            if (previousFeeder is null) updates.Remove("feeder"); else updates["feeder"] = previousFeeder;
            if (previousConsumer is null) updates.Remove("renodx-dlss5"); else updates["renodx-dlss5"] = previousConsumer;
        }
    }

    private static async Task AssertInstallAndRestore(string root, RenderApi api, int bits, string? proxy,
        Components components, Action<string, bool> check)
    {
        var id = $"feeder-{api}-{bits}";
        var game = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        var exe = Path.Combine(game, $"D5M-{id}-fixture.exe");
        var system = bits == 64 ? Environment.SystemDirectory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
        File.Copy(Path.Combine(system, "notepad.exe"), exe);
        File.WriteAllText(Path.Combine(game, "ReShade.ini"), "[Personal]\nValue=Keep\n");
        File.WriteAllText(Path.Combine(game, "ReShadePreset.ini"), "Techniques=Keep@User.fx\nTechniquesSorting=Keep@User.fx\n");
        var layer = Directory.CreateDirectory(Path.Combine(root, id + "-layer")).FullName;
        var manifests = api == RenderApi.Vulkan ? new[] { VulkanNrFixtures.StageLayer(layer, exe, bits) } : Array.Empty<string>();
        var layerBefore = VulkanNrFixtures.FileHashes(layer);
        var original = VulkanNrFixtures.FileHashes(game);
        var request = new InstallRequest
        {
            GameDir = game, ExePath = exe, Api = api, Bitness = bits, Route = Route.Feeder,
            VulkanLayerManifests = manifests,
        };

        if (api is RenderApi.Vulkan or RenderApi.OpenGL)
        {
            var native = Path.Combine(game, "nvngx_dlss.dll");
            File.Copy(Path.Combine(system, "version.dll"), native);
            var withNative = VulkanNrFixtures.FileHashes(game);
            string? reason = null;
            try { await Installers.InstallAsync(request, components, _ => { }, null, CancellationToken.None); }
            catch (InstallException ex) { reason = ex.Message; }
            check($"{api} {bits}-bit Feeder declines games with native DLSS before modifying them",
                reason?.Contains("DLSS", StringComparison.OrdinalIgnoreCase) == true
                && Scanner.Scan(game, exe).HasNativeDlss && withNative.SequenceEqual(VulkanNrFixtures.FileHashes(game)));
            File.Delete(native);
        }

        InstallPreview? preview = null;
        await Installers.InstallAsync(request, components, _ => { }, null, CancellationToken.None, result => preview = result, previewOnly: true);
        check($"{api} {bits}-bit Feeder preview resolves the correct rendering files without writing",
            preview is not null && original.SequenceEqual(VulkanNrFixtures.FileHashes(game))
            && (proxy is null ? !preview.Copies.Any(copy => copy.RelativePath is "dxgi.dll" or "vulkan-1.dll" or "opengl32.dll")
                              : preview.Copies.Any(copy => copy.RelativePath == proxy)));
        await Installers.InstallAsync(request, components, _ => { }, null, CancellationToken.None);
        var record = Journal.ReadManifest(game);
        var ini = File.ReadAllText(Path.Combine(game, "ReShade.ini"));
        var preset = File.ReadAllText(Path.Combine(game, "ReShadePreset.ini"));
        bool builtInMotion = api == RenderApi.OpenGL;
        check($"{api} {bits}-bit Feeder installs as the selected API with the expected loader and shader setup",
            record is { Route: "feeder", MfgUnlock: null } && record.ReShadeHook == proxy
            && File.Exists(Path.Combine(game, $"dlss5-feed.addon{bits}"))
            && File.Exists(Path.Combine(game, "reshade-shaders", "Shaders", "DLSS5_Feed.fx"))
            && (proxy is null ? !File.Exists(Path.Combine(game, "dxgi.dll")) && !File.Exists(Path.Combine(game, "vulkan-1.dll"))
                              : File.Exists(Path.Combine(game, proxy)))
            && Ini.Get(ini, "Personal", "Value") == "Keep"
            && Ini.Get(ini, "GENERAL", "PreprocessorDefinitions") == (builtInMotion ? "DLSS5_MV_PROVIDER=5" : "DLSS5_MV_PROVIDER=2")
            && Ini.Get(preset, "", "Techniques") is { } techniques && techniques.Contains("DLSS5_Feed@DLSS5_Feed.fx")
            && techniques.Contains("Keep@User.fx") && techniques.Contains("vort_MotionEffects@vort_Motion.fx") != builtInMotion);
        if (bits == 32)
        {
            var helper = Path.Combine(game, "host64");
            check($"{api} 32-bit Feeder installs its 64-bit consumer and separate helper proxy",
                File.Exists(Path.Combine(helper, "dlss5-feed-host64.exe")) && File.Exists(Path.Combine(helper, "renodx-dlss5.addon64"))
                && Pe.GetBitness(Path.Combine(helper, "dxgi.dll")) == 64 && !File.Exists(Path.Combine(game, "renodx-dlss5.addon64")));
        }

        await Installers.RestoreAsync(game, exe, _ => { });
        var restored = VulkanNrFixtures.FileHashes(game).Where(file =>
            !file.Name.StartsWith(Journal.BackupDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        check($"{api} {bits}-bit Feeder restores original game files and leaves its shared layer unchanged",
            original.SequenceEqual(restored) && layerBefore.SequenceEqual(VulkanNrFixtures.FileHashes(layer))
            && Journal.ReadManifest(game) is null && !InstallTransaction.Pending(game));
    }
}
