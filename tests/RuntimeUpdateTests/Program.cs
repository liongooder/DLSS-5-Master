using DLSS5Master.Core;
using System.IO.Compression;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "d5m-runtime-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("DLSS5MASTER_DATA", Path.Combine(root, "data"));
int passed = 0;
void Check(string name, bool ok)
{
    if (!ok) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}
object Release(RuntimeSource source, string tag, string asset, string sha, bool pre = false, string? url = null) => new
{
    tag_name = tag, draft = false, prerelease = pre, published_at = "2026-11-13T10:00:00Z",
    assets = new[] { new { name = asset, digest = "sha256:" + sha, browser_download_url = url ?? $"https://github.com/{source.Repo}/releases/download/{tag}/{asset}" } }
};
RuntimeRelease? Pick(RuntimeSource source, params object[] releases)
{
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(releases));
    return RuntimeUpdates.SelectRelease(source, doc.RootElement);
}
string Zip(string id, params string[] names)
{
    var file = Path.Combine(root, id + ".zip");
    using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
    foreach (var name in names)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write("fixture " + name);
    }
    return file;
}
RuntimeRelease Image(string zip, string version) => new("fixture", version, DateTime.UtcNow,
    "https://github.com/test/release.zip", Components.Sha256(zip), "https://github.com/test");
async Task Reject(string name, Func<Task> action)
{
    bool rejected = false;
    try { await action(); }
    catch (Exception e) when (e is InstallException or InvalidDataException or IOException) { rejected = true; }
    Check(name, rejected);
}
try
{
    var sr = RuntimeUpdates.Sources.Single(s => s.Id == "dlss-sr");
    var nr = RuntimeUpdates.Sources.Single(s => s.Id == "dlss-nr");
    var sl = RuntimeUpdates.Sources.Single(s => s.Streamline);
    var sha = new string('a', 64);
    Check("five runtime update controls", RuntimeUpdates.Sources.Length == 5);
    Check("Streamline uses the requested release mirror", sl.Repo == "RankFTW/rhi-repo" && RuntimeUpdates.Sources.Where(s => !s.Streamline).All(s => s.Repo == "RankFTW/rhi-repo"));
    Check("versions compare numerically", RuntimeUpdates.Compare("310.10.0", "310.9.16.0") > 0 && RuntimeUpdates.Compare("310.9.16", "310.9.16.0") == 0);
    Check("stable newest DLSS selected", Pick(sr,
        Release(sr, "dlss-310.9.16", "nvngx_dlss_310.9.16.zip", sha), Release(sr, "dlss-311.0.0", "nvngx_dlss_311.0.0.zip", sha))?.Version == "311.0.0");
    Check("unverified archive rejected", Pick(sr, Release(sr, "dlss-311.0.0", "nvngx_dlss_311.0.0.zip", "")) is null);
    Check("pre-release rejected", Pick(sr, Release(sr, "dlss-311.0.0", "nvngx_dlss_311.0.0.zip", sha, true)) is null);
    Check("wrong repository download rejected", Pick(sr, Release(sr, "dlss-311.0.0", "nvngx_dlss_311.0.0.zip", sha, url: "https://github.com/other/repo/releases/download/x/file.zip")) is null);
    Check("community NR variant is not silently selected", Pick(nr, Release(nr, "dlssnr-310.8.community", "nvngx_dlssnr_310.8.community.zip", sha)) is null);
    Check("Streamline excludes ARM SDK", Pick(sl, Release(sl, "v2.14.16", "streamline-sdk-v2.14.16-aarch64.zip", sha)) is null);
    Check("Streamline stays on the approved 2.13 release", Pick(sl, Release(sl, "streamline-2.13.0.0", "streamline_2.13.0.0.zip", sha))?.Version == "2.13.0.0");

    Check("Streamline 2.14 is excluded pending compatibility tests", Pick(sl, Release(sl, "streamline-2.14.16.0-v2", "streamline_2.14.16.0-v2.zip", sha)) is null);
    var single = Zip("single", "release/nvngx_dlss.dll");
    var release = Image(single, "311.0.0");
    await RuntimeUpdates.ActivateArchiveAsync(sr, release, single, _ => 64, _ => "311.0.0.0");
    Check("verified DLL activated in private cache", RuntimeUpdates.SelectionReady(sr));
    var selected = RuntimeUpdates.State.Active[sr.Id];
    var previous = selected.Folder;
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [sr.FileName] = "original.dll", ["nvngx_dlssnr.dll"] = "model.dll" };
    RuntimeUpdates.ApplySelections(map);
    Check("runtime resolver uses selected update", map[sr.FileName].StartsWith(AppPaths.Components));
    Check("other runtime files retained", map["nvngx_dlssnr.dll"] == "model.dll");
    var custom = Path.Combine(root, "custom");
    Directory.CreateDirectory(custom);
    File.WriteAllText(Path.Combine(custom, sr.FileName), "custom unchanged");
    Check("custom folder keeps priority over active updates", new Components(new AppSettings { RuntimeFolder = custom }).RuntimeFiles()[sr.FileName] == Path.Combine(custom, sr.FileName));
    await Reject("wrong checksum never activates", () => RuntimeUpdates.ActivateArchiveAsync(sr, release with { Sha256 = new string('0', 64) }, single, _ => 64, _ => "311.0.0"));
    await Reject("wrong architecture never activates", () => RuntimeUpdates.ActivateArchiveAsync(sr, release, single, _ => 32, _ => "311.0.0"));
    await Reject("wrong embedded version never activates", () => RuntimeUpdates.ActivateArchiveAsync(sr, release, single, _ => 64, _ => "310.0.0"));
    Check("failures preserve previous selected build", RuntimeUpdates.State.Active[sr.Id].Folder == previous);
    using (var stop = new CancellationTokenSource())
    {
        stop.Cancel();
        bool cancelled = false;
        try { await RuntimeUpdates.ActivateArchiveAsync(sr, release, single, _ => 64, _ => "311.0.0", stop.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check("cancelled update preserves previous selection", cancelled && RuntimeUpdates.State.Active[sr.Id].Folder == previous);
    }
    var duplicate = Zip("duplicate", "one/nvngx_dlss.dll", "two/nvngx_dlss.dll");
    await Reject("ambiguous DLL archive rejected", () => RuntimeUpdates.ActivateArchiveAsync(sr, Image(duplicate, "311.0.0"), duplicate, _ => 64, _ => "311.0.0"));
    var traversal = Zip("traversal", "../nvngx_dlss.dll");
    await Reject("archive traversal rejected", () => RuntimeUpdates.ActivateArchiveAsync(sr, Image(traversal, "311.0.0"), traversal, _ => 64, _ => "311.0.0"));

    var sdkFiles = new[] { "sl.common.dll", "sl.interposer.dll", "sl.dlss.dll", "sl.dlss_g.dll", "sl.reflex.dll" };
    var sdk = Zip("sdk", sdkFiles.Select(n => n).Concat(new[] { "bin/x64/development/sl.interposer.dll", "_artifacts/sl.dlss_g/Develop_x64/sl.dlss_g.dll", "reflex.license.txt" }).ToArray());
    await RuntimeUpdates.ActivateArchiveAsync(sl, Image(sdk, "2.13.0.0"), sdk, _ => 64, _ => "2.13.0.0");
    Check("complete Streamline set activated", RuntimeUpdates.SelectionReady(sl));
    Check("development DLLs excluded", RuntimeUpdates.State.Active[sl.Id].Files.Count == 6);
    map["sl.dlss_nr.dll"] = "old-community-plugin.dll";
    map["sl.interposer.dll"] = "old-interposer.dll";
    RuntimeUpdates.ApplySelections(map);
    Check("old Streamline plugins never mixed into new set", !map.ContainsKey("sl.dlss_nr.dll") && sdkFiles.All(map.ContainsKey));
    var goodSdk = RuntimeUpdates.State.Active[sl.Id].Folder;
    var incomplete = Zip("incomplete", "sl.interposer.dll");
    await Reject("incomplete Streamline set rejected", () => RuntimeUpdates.ActivateArchiveAsync(sl, Image(incomplete, "2.13.0.0"), incomplete, _ => 64, _ => "2.13.0.0"));
    Check("failed Streamline update keeps complete previous set", RuntimeUpdates.State.Active[sl.Id].Folder == goodSdk);
    var activeFile = map[sr.FileName];
    File.WriteAllText(activeFile, "damaged update bytes");
    Check("active cache corruption detected", !RuntimeUpdates.SelectionReady(sr));
    var fallback = new Dictionary<string, string> { [sr.FileName] = "included.dll" };
    RuntimeUpdates.ApplySelections(fallback);
    Check("corrupt update falls back to included DLL", fallback[sr.FileName] == "included.dll");
    await RuntimeUpdates.RevertAsync(sr);
    await RuntimeUpdates.RevertAsync(sl);
    Check("Use included clears selections without deleting builds", RuntimeUpdates.State.Active.Count == 0 && File.Exists(activeFile));

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "installer/payload-manifest.csv"))) dir = dir.Parent;
    if (dir is null) throw new Exception("Repository fixture not found");
    var fg = RuntimeUpdates.Sources.Single(s => s.Id == "dlss-fg");
    var realZip = Path.Combine(root, "real-fg.zip");
    var realFile = Path.Combine(dir.FullName, "src/DLSS5Master/payload/runtime/nvngx_dlssg.dll");
    using (var package = ZipFile.Open(realZip, ZipArchiveMode.Create)) package.CreateEntryFromFile(realFile, fg.FileName);
    await RuntimeUpdates.ActivateArchiveAsync(fg, Image(realZip, Pe.GetFileVersion(realFile)!), realZip, Pe.GetBitness, Pe.GetFileVersion);
    Check("real DLL passes production architecture and version validation", RuntimeUpdates.SelectionReady(fg));
    await RuntimeUpdates.RevertAsync(fg);

    if (args.Contains("--live"))
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await RuntimeUpdates.CheckAsync(deadline.Token);
        Check("live update check has five verified sources", RuntimeUpdates.State.Latest.Count == 5);
        Check("live Streamline release comes from requested mirror", RuntimeUpdates.State.Latest[sl.Id].Url.StartsWith("https://github.com/RankFTW/rhi-repo/releases/download/"));
        Check("live update check records download sizes", RuntimeUpdates.State.Latest.Values.All(r => r.Size > 0));
        Check("live runtime check persists results", File.Exists(Path.Combine(AppPaths.Root, "runtime-updates.json")) && RuntimeUpdates.State.LastCheck is not null);
    }
    Console.WriteLine($"{passed} passed, 0 failed; temporary fixtures only, no DLLs loaded or games changed");
}
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}
