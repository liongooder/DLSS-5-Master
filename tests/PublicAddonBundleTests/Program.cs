using System.IO.Compression;
using DLSS5Master.Core;

var scratch = Path.Combine(Path.GetTempPath(), "d5m-bundle-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("DLSS5MASTER_DATA", scratch);
int passed = 0;
void Check(string name, bool ok)
{
    if (!ok) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    passed++;
}
try
{
    var components = new Components(new AppSettings());
    // Fail any unexpected network fallback promptly. These calls must use the bundle.
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var mfg = await components.MfgUnlockAsync(null, deadline.Token);
    Check("MFG is bundled, not downloaded", mfg == Path.Combine(AppContext.BaseDirectory, "addons", Components.MfgAddon));
    Check("MFG bundle matches published SHA-256", Components.Sha256(mfg) == Components.MfgSha256);
    var mfgSource = Updates.Sources.Single(s => s.Id == "mfgunlock");
    Updates.State.Active[mfgSource.Id] = new ActiveUpdate { Version = "1.12.0", Published = new DateTime(2026, 11, 23, 16, 3, 0, DateTimeKind.Utc) };
    Updates.DropSuperseded();
    Check("MFG metadata matches bundle and supersedes the same downloaded release",
        Components.MfgVersion == "1.12.0" && Updates.InUse(mfgSource) is { Version: "1.12.0", Updated: false });

    var upliftGroup = ComponentHealth.Groups.Single(group => group.Id == "gitc-uplift");
    Check("Uplift 2.0.0 archive matches the published pin", upliftGroup.Version == "2.0.0" &&
        ComponentHealth.Inspect(upliftGroup).Ready);
    string upliftStaging;
    using (var uplift = await components.UpliftAsync(null, deadline.Token))
    {
        upliftStaging = Path.GetDirectoryName(uplift.AddonDirectory)!;
        Check("Uplift stages both add-on architectures and the matching helper",
            Pe.GetBitness(Path.Combine(uplift.AddonDirectory, "gitc-uplift.addon64")) == 64 &&
            Pe.GetBitness(Path.Combine(uplift.AddonDirectory, "gitc-uplift.addon32")) == 32 &&
            Pe.GetBitness(Path.Combine(uplift.AddonDirectory, "gitc-uplift-helper64.exe")) == 64);
        Check("Uplift stages both release shaders",
            File.Exists(Path.Combine(uplift.ShaderDirectory, "Uplift.fx")) &&
            File.Exists(Path.Combine(uplift.ShaderDirectory, "UpliftMask.fx")));
    }
    Check("Uplift releases its temporary staging files", !Directory.Exists(upliftStaging));

    var feeder = await components.FeederAsync(null, deadline.Token);
    Check("Feeder is bundled, not downloaded", feeder.Addon64.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    var feederGroup = ComponentHealth.Groups.Single(group => group.Id == "feeder");
    Check("Feeder bundle matches its component manifest", ComponentHealth.Inspect(feederGroup).Ready);
    var liveControls = Components.FeederWithLiveControls();
    Check("live controls use the original Feeder and the bundled companion",
        liveControls.Addon64 == feeder.Addon64 && liveControls.InGameControls is { } panel && File.Exists(panel));
    foreach (var name in Components.FeederHashes.Keys)
    {
        var path = name switch
        {
            "dlss5-feed.addon64" => feeder.Addon64,
            "dlss5-feed.addon32" => feeder.Addon32,
            "dlss5-feed-host64.exe" => feeder.Host64,
            _ => Path.Combine(feeder.ShaderRoot, "Shaders", name)
        };
        var relativePath = Path.GetRelativePath(AppContext.BaseDirectory, path).Replace('\\', '/');
        var pin = feederGroup.Files.Single(f => f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        Check("Bundled Feeder pinned file: " + name, ComponentHealth.Matches(path, pin.Sha256));
    }
    Check("Feeder metadata matches bundle", Updates.InUse(Updates.Sources.Single(s => s.Id == "feeder")).Version == "2.0.0");
    Check("existing VORT provider stays supported", File.ReadAllText(Path.Combine(feeder.ShaderRoot, "Shaders", "DLSS5_Feed.fx")).Contains("MotVectTexVort") && File.Exists(Path.Combine(feeder.ShaderRoot, "Shaders", "vort_Motion.fx")));

    var importRoot = Path.Combine(scratch, "feeder-imports");
    var bundledFeeder = Path.Combine(AppContext.BaseDirectory, "payload", "feeder");
    var feederArchive = Path.Combine(scratch, "DLSS5-Feeder-fixture.zip");
    var incompleteArchive = Path.Combine(scratch, "DLSS5-Feeder-incomplete.zip");
    WriteFeederZip(feederArchive, includeHost: true);
    WriteFeederZip(incompleteArchive, includeHost: false);

    var selectedFeeder = FeederZipImport.Prepare(feederArchive, importRoot, bundledFeeder);
    var settings = new AppSettings { FeederFolder = selectedFeeder };
    Check("Feeder import records the chosen ZIP name", FeederZipImport.DisplayName(selectedFeeder) == Path.GetFileName(feederArchive));
    var importedComponents = new Components(settings);
    Updates.State.Active["feeder"] = new ActiveUpdate { Version = "fixture", Path = bundledFeeder };
    var imported = await importedComponents.FeederAsync(null, deadline.Token);
    Check("the chosen Feeder ZIP takes precedence over a downloaded update",
        imported.Addon64.StartsWith(selectedFeeder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && Components.Sha256(imported.Addon64) == Components.Sha256(feeder.Addon64)
        && Components.Sha256(imported.Host64) == Components.Sha256(feeder.Host64));
    Check("official-style Feeder ZIP gets the bundled shader dependencies",
        File.Exists(Path.Combine(imported.ShaderRoot, "Shaders", "vort_Motion.fx"))
        && File.Exists(Path.Combine(imported.ShaderRoot, "Shaders", "ReShade.fxh"))
        && File.Exists(Path.Combine(imported.ShaderRoot, "Shaders", "Includes", "vort_MotionVectors.fxh"))
        && File.Exists(Path.Combine(imported.ShaderRoot, "Textures", "vort_BlueNoise.png")));
    Check("Feeder import only stages supported payload files", imported.Verifier is null
        && !Directory.EnumerateFiles(selectedFeeder, "Unrelated.dll", SearchOption.AllDirectories).Any());
    int imports = Directory.EnumerateDirectories(importRoot).Count();
    bool rejected = false;
    try { FeederZipImport.Prepare(incompleteArchive, importRoot, bundledFeeder); }
    catch (InstallException) { rejected = true; }
    Check("a ZIP with no matching Feeder host cannot use the bundled binary to pass validation",
        rejected && Directory.EnumerateDirectories(importRoot).Count() == imports);
    settings.FeederFolder = Path.Combine(importRoot, "missing");
    rejected = false;
    try { await importedComponents.FeederAsync(null, deadline.Token); }
    catch (InstallException) { rejected = true; }
    Check("a missing explicit Feeder import does not silently replace it with a different version", rejected);
    settings.FeederFolder = "";
    Updates.State.Active.Remove("feeder");
    Check("clearing the ZIP selection restores normal Feeder resolution",
        (await importedComponents.FeederAsync(null, deadline.Token)).Addon64 == feeder.Addon64);

    void WriteFeederZip(string path, bool includeHost)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(feeder.Addon64, "dlss5-feed.addon64");
        archive.CreateEntryFromFile(feeder.Addon32, "dlss5-feed.addon32");
        if (includeHost) archive.CreateEntryFromFile(feeder.Host64, "host64/dlss5-feed-host64.exe");
        archive.CreateEntryFromFile(Path.Combine(feeder.ShaderRoot, "Shaders", "DLSS5_Feed.fx"), "reshade-shaders/Shaders/DLSS5_Feed.fx");
        using var ignore = new StreamWriter(archive.CreateEntry("reshade-shaders/Shaders/Unrelated.dll").Open());
        ignore.Write("unrelated");
    }

    var optiSource = Updates.Sources.Single(s => s.Id == "optiscaler-nr");
    var build = Components.OptiBuilds[0];
    Updates.State.Active[optiSource.Id] = new ActiveUpdate { Version = "0.9.96", Published = new DateTime(2026, 11, 25, 16, 5, 9, DateTimeKind.Utc) };
    Updates.DropSuperseded();
    Check("OptiScaler 0.9.96 upgrade replaces an already accepted copy with the bundle",
        !Updates.State.Active.ContainsKey(optiSource.Id) && Updates.InUse(optiSource) is { Version: "0.9.96", Updated: false });
    var zip = Path.Combine(AppContext.BaseDirectory, "payload", "optiscaler", Components.BundledOptiZipName(build));
    Check("OptiScaler bundle matches published SHA-256", Components.Sha256(zip) == build.Sha256);
    using (var archive = ZipFile.OpenRead(zip))
    {
        const string prefix = "OptiScaler/dlssg_sm86/";
        Check("OptiScaler archive carries the RTX 20/30 overlay payload at the expected paths",
            archive.GetEntry(prefix + "dlssg_sm86.dll") is { Length: > 0 }
            && archive.GetEntry(prefix + "dlssg_sm86.ini") is { Length: > 0 }
            && archive.GetEntry(prefix + "THIRD_PARTY_NOTICES.txt") is { Length: > 0 }
            && archive.Entries.Any(entry => entry.FullName.StartsWith(prefix + "dlssg_sm86/", StringComparison.OrdinalIgnoreCase) && entry.Length > 0));
    }
    var opti = await components.OptiScalerAsync(build, null, deadline.Token);
    Check("OptiScaler extracted without downloading", !File.Exists(Path.Combine(AppPaths.Components, $"OptiScaler-{build.Id}.zip")));
    Check("OptiScaler has 64-bit DLL and whole backend", Pe.GetBitness(Path.Combine(opti, "OptiScaler.dll")) == 64 && Directory.Exists(Path.Combine(opti, "OptiScaler")));
    Check("OptiScaler license included", File.Exists(Path.Combine(opti, "LICENSE")));
    Check("OptiScaler metadata matches bundle", build.Id == "0.9.96-shyvortex" && optiSource.BundledVersion == "0.9.96");
    Console.WriteLine($"{passed} passed, 0 failed; bundled components only, no DLLs loaded or games changed");
}
finally
{
    if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(scratch, true);
}
