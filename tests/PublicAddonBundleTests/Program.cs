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
    Check("MFG metadata matches bundle", Components.MfgVersion == "1.9.0" && Updates.InUse(Updates.Sources.Single(s => s.Id == "mfgunlock")).Version == "1.9.0");

    var feeder = await components.FeederAsync(null, deadline.Token);
    Check("Feeder is bundled, not downloaded", feeder.Addon64.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    foreach (var (name, hash) in Components.FeederHashes)
    {
        var path = name switch
        {
            "dlss5-feed.addon64" => feeder.Addon64,
            "dlss5-feed.addon32" => feeder.Addon32,
            "dlss5-feed-host64.exe" => feeder.Host64,
            _ => Path.Combine(feeder.ShaderRoot, "Shaders", name)
        };
        Check("Feeder pinned file: " + name, Components.Sha256(path) == hash);
    }
    Check("Feeder metadata matches bundle", Updates.InUse(Updates.Sources.Single(s => s.Id == "feeder")).Version == "2.0.0");
    Check("existing VORT provider stays supported", File.ReadAllText(Path.Combine(feeder.ShaderRoot, "Shaders", "DLSS5_Feed.fx")).Contains("MotVectTexVort") && File.Exists(Path.Combine(feeder.ShaderRoot, "Shaders", "vort_Motion.fx")));

    var build = Components.OptiBuilds[0];
    var zip = Path.Combine(AppContext.BaseDirectory, "payload", "optiscaler", Components.BundledOptiZipName(build));
    Check("OptiScaler bundle matches published SHA-256", Components.Sha256(zip) == build.Sha256);
    var opti = await components.OptiScalerAsync(build, null, deadline.Token);
    Check("OptiScaler extracted without downloading", !File.Exists(Path.Combine(AppPaths.Components, $"OptiScaler-{build.Id}.zip")));
    Check("OptiScaler has 64-bit DLL and whole backend", Pe.GetBitness(Path.Combine(opti, "OptiScaler.dll")) == 64 && Directory.Exists(Path.Combine(opti, "OptiScaler")));
    Check("OptiScaler license included", File.Exists(Path.Combine(opti, "LICENSE")));
    Check("OptiScaler metadata matches bundle", build.Id == "0.9.90-shyvortex" && Updates.InUse(Updates.Sources.Single(s => s.Id == "optiscaler-nr")).Version == "0.9.90");
    Console.WriteLine($"{passed} passed, 0 failed; bundled components only, no DLLs loaded or games changed");
}
finally
{
    if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(scratch, true);
}
