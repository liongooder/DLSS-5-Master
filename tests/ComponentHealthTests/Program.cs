using DLSS5Master.Core;
using System.Security.Cryptography;
using System.Text;

var root = Path.Combine(Path.GetTempPath(), "d5m-health-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("DLSS5MASTER_DATA", Path.Combine(root, "data"));
int passed = 0;
void Check(string name, bool ok)
{
    if (!ok) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}
string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
try
{
    Check("catalog has seven public component sets", ComponentHealth.Groups.Count == 7);
    Check("catalog includes every payload pin plus MFG", ComponentHealth.Groups.Sum(g => g.Files.Count) == 51);
    Check("catalog excludes private add-ons and NeuralMaster", ComponentHealth.Groups.All(g => g.Id is not ("own-addon" or "neuralmaster")) &&
        ComponentHealth.Groups.SelectMany(g => g.Files).All(p => !p.RelativePath.Contains("dlss5-nr", StringComparison.OrdinalIgnoreCase) && !p.RelativePath.Contains("neuralmaster", StringComparison.OrdinalIgnoreCase)));
    var bundle = Path.Combine(root, "bundle");
    Directory.CreateDirectory(Path.Combine(bundle, "payload"));
    var a = new ComponentPin("payload/a.bin", "fixture", Hash("good-a"));
    var b = new ComponentPin("payload/b.bin", "fixture", Hash("good-b"));
    var c = new ComponentPin("payload/c.bin", "fixture", Hash("good-c"));
    var group = new ComponentGroup("fixture", "Fixture", "1", new[] { a, b, c });
    File.WriteAllText(Path.Combine(bundle, "payload/a.bin"), "damaged");
    File.WriteAllText(Path.Combine(bundle, "payload/b.bin"), "good-b");
    var before = ComponentHealth.Inspect(group, bundle);
    Check("detects missing and corrupted files separately", before is { Missing: 1, Different: 1, Ready: false });
    int fetched = 0;
    Task Fetch(ComponentPin pin, string target, CancellationToken ct)
    {
        fetched++;
        return File.WriteAllTextAsync(target, "good-" + Path.GetFileNameWithoutExtension(pin.RelativePath), ct);
    }
    await ComponentHealth.RepairAsync(group, null, default, bundle, Fetch);
    Check("fetches only bad or missing files", fetched == 2);
    Check("repair verifies the full set", ComponentHealth.Inspect(group, bundle).Ready);
    Check("repair preserves original bundled bytes", File.ReadAllText(Path.Combine(bundle, "payload/a.bin")) == "damaged" && !File.Exists(Path.Combine(bundle, "payload/c.bin")));
    var repairedA = ComponentHealth.Resolve(group, a, bundle);
    Check("repair paths remain in app data", repairedA.StartsWith(AppPaths.Components + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    File.WriteAllText(repairedA, "damaged repair");
    Check("later repair corruption is visible", ComponentHealth.Inspect(group, bundle).Different == 1);
    bool rejected = false;
    try
    {
        await ComponentHealth.RepairAsync(group, null, default, bundle,
            (_, target, ct) => File.WriteAllTextAsync(target, "wrong bytes", ct));
    }
    catch (InstallException) { rejected = true; }
    Check("bad download is rejected", rejected);
    Check("failed repair leaves previous selection intact", ComponentHealth.Resolve(group, a, bundle) == repairedA);
    await ComponentHealth.RepairAsync(group, null, default, bundle, Fetch);
    Check("retry repairs a damaged cache", ComponentHealth.Inspect(group, bundle).Ready && ComponentHealth.Resolve(group, a, bundle) != repairedA);
    var current = ComponentHealth.Resolve(group, a, bundle);
    using var stop = new CancellationTokenSource();
    stop.Cancel();
    bool cancelled = false;
    try { await ComponentHealth.RepairAsync(group, null, stop.Token, bundle, Fetch); }
    catch (OperationCanceledException) { cancelled = true; }
    Check("cancellation preserves complete selection", cancelled && ComponentHealth.Resolve(group, a, bundle) == current);
    var escaping = new ComponentGroup("escape", "Escape", "1", new[] { a with { RelativePath = "../escaped.bin" } });
    rejected = false;
    try { await ComponentHealth.RepairAsync(escaping, null, default, bundle, Fetch); }
    catch (InstallException) { rejected = true; }
    Check("unsafe file paths rejected before writing", rejected && !File.Exists(Path.Combine(root, "escaped.bin")));
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "installer/payload-manifest.csv"))) dir = dir.Parent;
    if (dir is null) throw new Exception("Repository payload not found for integration checks");
    var realBundle = Path.Combine(dir.FullName, "src/DLSS5Master");
    foreach (var component in ComponentHealth.Groups)
        Check("real bundle verifies: " + component.Name, ComponentHealth.Inspect(component, realBundle).Ready);
    var native = ComponentHealth.Groups.Single(g => g.Id == "renodx-dlss5");
    var emptyBundle = Path.Combine(root, "missing-app-files");
    Directory.CreateDirectory(emptyBundle);
    await ComponentHealth.RepairAsync(native, null, default, emptyBundle, (pin, target, ct) =>
    {
        File.Copy(Journal.SafePath(realBundle, pin.RelativePath), target);
        return Task.CompletedTask;
    });
    var actualSource = await new Components(new AppSettings()).RenoDxConsumerAsync(null, default);
    Check("installer resolves the verified repair copy", actualSource == ComponentHealth.IncludedFile(native.Files[0].RelativePath) &&
        actualSource.StartsWith(AppPaths.Components, StringComparison.OrdinalIgnoreCase));
    var customRuntime = Path.Combine(root, "custom-runtime");
    Directory.CreateDirectory(customRuntime);
    File.WriteAllText(Path.Combine(customRuntime, "nvngx_dlssnr.dll"), "user-supplied fixture");
    var custom = new Components(new AppSettings { RuntimeFolder = customRuntime });
    Check("custom runtime selection remains in control", custom.RuntimeDir == customRuntime &&
        File.ReadAllText(custom.RuntimeFiles()["nvngx_dlssnr.dll"]) == "user-supplied fixture");
    Console.WriteLine($"{passed} passed, 0 failed; temporary repair cache only, no app or game files changed");
}
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}
