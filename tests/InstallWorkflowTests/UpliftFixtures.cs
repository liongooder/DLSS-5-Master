using DLSS5Master.Core;

internal static class UpliftFixtures
{
    private const string ArchiveSha = "1eac38a62339e993cc98fd4f4773dd8a327b098b47c4d21fc070a9d3d6aac255";
    private const string Addon64Sha = "fa18fbbc2f67e31cde2d25b585b78c9d8359566eb916ea44e02fa54277eb40c2";
    private const string Addon32Sha = "baec0d6d78a0503ff0bb175f4bd2b1a1fb443887bdd7758115a3b261b5396a71";
    private const string HelperSha = "9c593aefee8db43f6d1d971666a1eb3564eed59d1c9b0cdfbc6d1b671de697f6";
    private const string ShaderSha = "488512e79ab257b67499bba4351815ccf92f3eef257a39a67cddc9a43c8c7e43";
    private const string MaskSha = "98c3bfb871d67a6d1ec3a492305b21b45ef46a1667b87060de1823e36bc0c2d2";

    internal static async Task Run(string root, Action<string, bool> check)
    {
        var archive = Path.Combine(AppContext.BaseDirectory, "payload", "gitc-uplift", "GITC-Uplift-2.0.0.zip");
        if (!HasSha(archive, ArchiveSha))
        {
            check("GITC Uplift workflow uses the byte-exact bundled release", false);
            return;
        }

        var runtime = Directory.CreateDirectory(Path.Combine(root, "uplift-runtime")).FullName;
        var model = Path.Combine(runtime, "nvngx_dlssnr.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), model);
        var components = new Components(new AppSettings
        {
            RuntimeFolder = runtime,
            ReShadeSource = VulkanNrFixtures.BundledReShadeFolder,
        });
        var modelSha = Components.Sha256(model);
        await AssertInstallAndRestore(root, RenderApi.DX12, 64, "dxgi.dll", modelSha, components, check);
        await AssertInstallAndRestore(root, RenderApi.DX12, 64, "dxgi.dll", modelSha, components, check, nestedAddonPath: true);
        await AssertInstallAndRestore(root, RenderApi.OpenGL, 64, "opengl32.dll", modelSha, components, check);
        await AssertInstallAndRestore(root, RenderApi.Vulkan, 32, null, modelSha, components, check);
        check("GITC Uplift leaves the bundled archive and supplied model byte-exact",
            HasSha(archive, ArchiveSha) && HasSha(model, modelSha));
    }

    private static async Task AssertInstallAndRestore(string root, RenderApi api, int bits, string? proxy,
        string modelSha, Components components, Action<string, bool> check, bool nestedAddonPath = false)
    {
        var label = $"GITC Uplift {api} {bits}-bit" + (nestedAddonPath ? " custom AddonPath" : "");
        var id = $"uplift-{api}-{bits}" + (nestedAddonPath ? "-custom" : "");
        var game = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        var exe = Path.Combine(game, $"D5M-{id}-Fixture.exe");
        var system = bits == 64 ? Environment.SystemDirectory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
        File.Copy(Path.Combine(system, "notepad.exe"), exe);
        var nativeModel = Path.Combine(game, "nvngx_dlssnr.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "winmm.dll"), nativeModel);
        var nativeSha = Components.Sha256(nativeModel);
        var shaderFolder = Path.Combine(game, "reshade-shaders", "Shaders");
        var addonFolder = nestedAddonPath ? Directory.CreateDirectory(Path.Combine(game, "LocalAddons")).FullName : game;
        Directory.CreateDirectory(shaderFolder);
        File.WriteAllText(Path.Combine(shaderFolder, "Uplift.fx"), "pre-existing shader to restore");
        File.WriteAllText(Path.Combine(addonFolder, "uplift.addon64"), "legacy Uplift add-on to restore");
        var settings = "[GENERAL]\nEffectSearchPaths=.\\UserShaders\n[Uplift]\nStartEnabled=0\n";
        if (nestedAddonPath) settings += "[ADDON]\nAddonPath=.\\LocalAddons\n";
        File.WriteAllText(Path.Combine(game, "ReShade.ini"), settings);
        const string preset = "Techniques=Keep@User.fx\nTechniqueSorting=Keep@User.fx\n";
        File.WriteAllText(Path.Combine(game, "ReShadePreset.ini"), preset);
        var original = VulkanNrFixtures.FileHashes(game);
        var layer = Directory.CreateDirectory(Path.Combine(root, id + "-layer")).FullName;
        var manifests = api == RenderApi.Vulkan ? new[] { VulkanNrFixtures.StageLayer(layer, exe, bits) } : Array.Empty<string>();
        var originalLayer = VulkanNrFixtures.FileHashes(layer);
        var request = new InstallRequest
        {
            GameDir = game, ExePath = exe, Api = api, Bitness = bits, Route = Route.Uplift,
            VulkanLayerManifests = manifests,
        };

        InstallPreview? preview = null;
        await Installers.InstallAsync(request, components, _ => { }, null, CancellationToken.None, result => preview = result, previewOnly: true);
        check($"{label} preview leaves the original game bytes and install record intact",
            preview is not null && original.SequenceEqual(VulkanNrFixtures.FileHashes(game)) && Journal.ReadManifest(game) is null);
        await Installers.InstallAsync(request, components, _ => { }, null, CancellationToken.None);

        var record = Journal.ReadManifest(game);
        if (nestedAddonPath)
            check("MFG-only setup reports the Uplift custom add-on folder before writing files",
                Installers.MfgOnlyConflict(game, exe, record)?.Contains("add-on folder", StringComparison.OrdinalIgnoreCase) == true);
        check($"{label} installs the proper ReShade loader and manifest",
            record is { Route: "uplift", MfgUnlock: null } && record.ReShadeHook == proxy
            && (proxy is null
                ? !File.Exists(Path.Combine(game, "dxgi.dll")) && !File.Exists(Path.Combine(game, "vulkan-1.dll"))
                : Pe.GetBitness(Path.Combine(game, proxy)) == bits));
        check($"{label} installs Uplift add-ons, helper, and shaders from one release",
            HasSha(Path.Combine(addonFolder, "gitc-uplift.addon32"), Addon32Sha)
            && HasSha(Path.Combine(addonFolder, "gitc-uplift.addon64"), Addon64Sha)
            && HasSha(Path.Combine(addonFolder, "gitc-uplift-helper64.exe"), HelperSha)
            && HasSha(Path.Combine(shaderFolder, "Uplift.fx"), ShaderSha)
            && HasSha(Path.Combine(shaderFolder, "UpliftMask.fx"), MaskSha)
            && !File.Exists(Path.Combine(addonFolder, "uplift.addon64")));
        var ini = File.ReadAllText(Path.Combine(game, "ReShade.ini"));
        check($"{label} preserves native NR and user effects while installing the private Uplift model",
            HasSha(nativeModel, nativeSha) && HasSha(Path.Combine(addonFolder, "GITC-Uplift", "nvngx_dlssnr.dll"), modelSha)
            && Ini.Get(ini, "Uplift", "StartEnabled") == "0"
            && Ini.Get(ini, "GENERAL", "EffectSearchPaths") is { } searchPaths
            && searchPaths.Contains("reshade-shaders", StringComparison.OrdinalIgnoreCase)
            && searchPaths.Contains("UserShaders", StringComparison.OrdinalIgnoreCase)
            && File.ReadAllText(Path.Combine(game, "ReShadePreset.ini")) == preset);

        await Installers.RestoreAsync(game, exe, _ => { });
        var restored = VulkanNrFixtures.FileHashes(game).Where(file =>
            !file.Name.StartsWith(Journal.BackupDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        check($"{label} restores original game files and leaves the shared Vulkan layer intact",
            original.SequenceEqual(restored) && originalLayer.SequenceEqual(VulkanNrFixtures.FileHashes(layer))
            && Journal.ReadManifest(game) is null && !InstallTransaction.Pending(game));
    }

    private static bool HasSha(string file, string sha) => File.Exists(file)
        && Components.Sha256(file).Equals(sha, StringComparison.OrdinalIgnoreCase);
}
