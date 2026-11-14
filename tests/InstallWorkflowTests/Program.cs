using DLSS5Master.Core;
using System.IO.Compression;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "d5m-workflow-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("DLSS5MASTER_DATA", Path.Combine(root, "data"));
int passed = 0;
void Check(string name, bool ok) { if (!ok) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
void Reject(string name, Action action) { bool threw = false; try { action(); } catch (Exception e) when (e is IOException or InstallException) { threw = true; } Check(name, threw); }
string Game(string id) { var dir = Path.Combine(root, id); Directory.CreateDirectory(dir); return dir; }
string Put(string game, string rel, string text) { var p = Journal.SafePath(game, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); return p; }
try
{
    var game = Game("transaction");
    var exe = Put(game, "bin/game.exe", "fixture");
    var original = Put(game, "bin/a.dll", "original");
    var s = Session.Begin(game, exe, "native", "dxgi", 64, _ => { });
    s.WriteTracked(original, "installed");
    s.WriteTracked(Journal.SafePath(game, "bin/ReShade.ini"), "[Tune]\nValue=42\n");
    var record = File.ReadAllText(Journal.ManifestPath(game));
    using (var tx = InstallTransaction.Begin(game))
    {
        await Task.Run(() => Session.Restore(game, _ => { }));
        var changed = Session.Begin(game, exe, "multipass", "dxgi", 64, _ => { });
        await Task.Run(() => changed.WriteTracked(original, "broken upgrade"));
        changed.WriteTracked(Journal.SafePath(game, "new/deep/new.dll"), "new");
        tx.Rollback();
    }
    Check("failed route change restores installed bytes", File.ReadAllText(original) == "installed");
    Check("rollback restores exact old record", File.ReadAllText(Journal.ManifestPath(game)) == record);
    Check("rollback restores original-file backup", File.ReadAllText(Path.Combine(Journal.OriginalsDir(game, Journal.ReadManifest(game)!), "bin/a.dll")) == "original");
    Check("rollback removes newly added files and empty directories", !Directory.Exists(Path.Combine(game, "new")));
    Check("rollback preserves route settings", File.ReadAllText(Journal.SafePath(game, "bin/ReShade.ini")).Contains("42"));
    Check("successful rollback clears pending marker", !InstallTransaction.Pending(game));
    using (var tx = InstallTransaction.Begin(game))
    {
        s = new Session(game, Journal.ReadManifest(game)!, _ => { });
        s.WriteTracked(original, "interrupted");
        // Dispose simulates process loss: the durable pending marker intentionally survives.
    }
    Check("interrupted operation retains recovery record", InstallTransaction.Pending(game));
    InstallTransaction.Recover(game);
    Check("recovery after interruption restores old bytes and record", File.ReadAllText(original) == "installed" && File.ReadAllText(Journal.ManifestPath(game)) == record);
    using (var tx = InstallTransaction.Begin(game))
    {
        InstallTransaction.Capture(original);
        using (new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read)) tx.Rollback();
    }
    Check("rollback leaves a locked but unchanged file alone", File.ReadAllText(original) == "installed");
    using (var tx = InstallTransaction.Begin(game))
    {
        Reject("transaction refuses paths outside game", () => InstallTransaction.Capture(Path.Combine(root, "outside.dll")));
        Reject("transaction prevents nested operations", () => InstallTransaction.Begin(game));
        tx.Commit();
    }
    RouteProfiles.Save(game, Journal.ReadManifest(game)!, _ => { });
    var native = RouteProfiles.Load(game, exe, Route.Native, 64, false, null);
    Check("route profile saves tuning", native.Any(k => k.Rel.EndsWith("ReShade.ini") && System.Text.Encoding.UTF8.GetString(k.Bytes).Contains("42")));
    Check("route profiles isolate executable and architecture", RouteProfiles.Load(game, Path.Combine(game, "different.exe"), Route.Native, 64, false, null).Count == 0 && RouteProfiles.Load(game, exe, Route.Native, 32, false, null).Count == 0);
    Check("route profiles isolate routes including native and private add-on", RouteProfiles.Load(game, exe, Route.LecramNr, 64, false, null).Count == 0);
    Check("development route profiles are never saved or loaded", !RouteProfiles.Allowed("neuralmaster") && RouteProfiles.Load(game, exe, Route.NeuralMaster, 64, false, null).Count == 0);

    var install = Game("preview");
    var installExe = Path.Combine(install, "OfflineFixture.exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), installExe);
    Put(install, "nvngx_dlss.dll", "game original");
    var runtime = Game("runtime"); var reshade = Game("reshade"); var addons = Game("addons");
    foreach (var name in new[] { "nvngx_dlss.dll", "nvngx_dlssnr.dll" }) File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(runtime, name));
    Put(reshade, "ReShade64.dll", "fixture Searching for add-ons");
    var addon = Put(addons, "renodx-dlss5.addon64", "fixture addon");
    Updates.State.Active["renodx-dlss5"] = new ActiveUpdate { Version = "fixture", Published = DateTime.UtcNow, Path = addon };
    var settings = new AppSettings { RuntimeFolder = runtime, ReShadeSource = reshade };
    var components = new Components(settings);
    var req = new InstallRequest { GameDir = install, ExePath = installExe, Api = RenderApi.DX12, Bitness = 64, Route = Route.Native };
    InstallPreview? preview = null;
    var before = Directory.GetFiles(install).ToDictionary(f => Path.GetFileName(f)!, Components.Sha256);
    await Installers.InstallAsync(req, components, _ => { }, null, CancellationToken.None, p => preview = p, previewOnly: true);
    Check("bulk preview identifies copies, versions and original bytes", preview is not null && preview.Copies.Any(c => c.RelativePath == "nvngx_dlss.dll") && preview.BackupBytes > 0);
    Check("preview accounts for rollback storage", preview!.RollbackBytes > 0 && preview.Describe().Contains("Rollback reserve"));
    Check("preview writes no game payload or install record", Journal.ReadManifest(install) is null && before.Count == Directory.GetFiles(install).Length && before.All(k => Components.Sha256(Path.Combine(install, k.Key!)) == k.Value));
    await Installers.InstallAsync(req, components, _ => { }, null, CancellationToken.None);
    var ini = Path.Combine(install, "ReShade.ini");
    File.AppendAllText(ini, "\n[Personal]\nValue=99\n");
    var entry = new GameEntry { Name = "Offline fixture", Dir = install, Exe = installExe };
    var managed = ManagedGameUpdates.Plan(entry, new GameOverride { Api = "DX12" }, BulkContext.Read(settings, components));
    Check("managed updater retains installed route and API", managed.Request is { Route: Route.Native, Api: RenderApi.DX12 });
    await Installers.InstallAsync(managed.Request!, components, _ => { }, null, CancellationToken.None);
    Check("managed update retains personal settings", File.ReadAllText(ini).Contains("Value=99"));
    var recordBeforeFailure = File.ReadAllText(Journal.ManifestPath(install));
    var iniBeforeFailure = File.ReadAllText(ini);
    var badModel = Path.Combine(runtime, "nvngx_dlssnr.dll");
    File.WriteAllText(badModel, "not a PE");
    bool failedInstall = false;
    try { await Installers.InstallAsync(req, components, _ => { }, null, CancellationToken.None); } catch (InstallException) { failedInstall = true; }
    Check("real installer failure automatically restores exact working record and tuning", failedInstall && !InstallTransaction.Pending(install) && File.ReadAllText(Journal.ManifestPath(install)) == recordBeforeFailure && File.ReadAllText(ini) == iniBeforeFailure);
    File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), badModel, true);
    var multiAddon = Put(addons, "renodx-dlss.addon64", "fixture multipass");
    Updates.State.Active["renodx-dlss"] = new ActiveUpdate { Version = "fixture", Published = DateTime.UtcNow, Path = multiAddon };
    var multiReq = new InstallRequest { GameDir = install, ExePath = installExe, Api = RenderApi.DX12, Bitness = 64, Route = Route.Multipass };
    await Installers.InstallAsync(multiReq, components, _ => { }, null, CancellationToken.None);
    File.AppendAllText(ini, "\n[MultiPersonal]\nValue=77\n");
    await Installers.InstallAsync(req, components, _ => { }, null, CancellationToken.None);
    Check("switching back restores the native route's tuning", File.ReadAllText(ini).Contains("Value=99") && !File.ReadAllText(ini).Contains("MultiPersonal"));
    await Installers.InstallAsync(multiReq, components, _ => { }, null, CancellationToken.None);
    Check("switching back independently restores multipass tuning", File.ReadAllText(ini).Contains("Value=77"));
    await Installers.InstallAsync(req, components, _ => { }, null, CancellationToken.None);
    Put(install, "EasyAntiCheat/EasyAntiCheat_EOS.exe", "fixture");
    Check("managed updater skips anti-cheat", ManagedGameUpdates.Plan(entry, null, BulkContext.Read(settings, components)).Request is null);
    var m = Journal.ReadManifest(install)!; m.Route = "neuralmaster"; new Session(install, m, _ => { }).Save();
    Check("managed updater excludes NeuralMaster", ManagedGameUpdates.Plan(entry, null, BulkContext.Read(settings, components)).Skip?.Contains("development") == true);
    m.Route = "native"; new Session(install, m, _ => { }).Save();

    Check("metadata-derived add-on identity sanitizes names", AddonIdentity.SafeName(" DLSS5 NR ") == "DLSS5 NR" && AddonIdentity.SafeName("bad\nname") == "DLSS5 NR");
    var copy = Environment.GetEnvironmentVariable("DLSS5MASTER_TEST_NR_FOLDER");
    if (Directory.Exists(copy)) Check("real local add-on identifies itself without executing it", AddonIdentity.Read(copy) is { Name: "DLSS5 NR", Version: not null });
    var clean = DiagnosticsBundle.Redact("failure C:\\Users\\person\\Game\\a.dll\nAuthorization: Bearer abc\ncontact test@example.com\ninstall-id=private\nnormal");
    Check("diagnostics redact paths and identity secrets", !clean.Contains("person") && !clean.Contains("abc") && !clean.Contains("private") && !clean.Contains("test@example.com") && clean.Contains("normal"));
    var bundle = DiagnosticsBundle.Preview(new[] { entry }, components);
    Check("diagnostics use an explicit allowlist", bundle.Keys.All(n => n is "readme.txt" or "system.json" or "components.json" or "activity.log" || n.StartsWith("games/")) && bundle.Values.All(v => !v.Contains(AppPaths.Root)));
    var zip = Path.Combine(root, "support.zip"); DiagnosticsBundle.Export(zip, bundle);
    using (var archive = ZipFile.OpenRead(zip)) Check("export exactly matches preview contents", archive.Entries.Count == bundle.Count && archive.Entries.All(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd() == bundle[e.FullName]; }));
    Reject("diagnostics never overwrite an existing export", () => DiagnosticsBundle.Export(zip, bundle));
    Reject("diagnostics reject traversal entries", () => DiagnosticsBundle.Export(Path.Combine(root, "bad.zip"), new Dictionary<string, string> { ["../bad"] = "bad" }));

    CommunityReport Report(long id, string route, string gpu, string driver, string rating, bool hidden = false) => new(id, rating, route, false, "dx12", gpu, "RTX 40", driver, "", 0, "", "", new Dictionary<string, string>(), "", hidden, DateTime.UtcNow.AddMinutes(id));
    var reports = new[] { Report(1, "native", "RTX 4090", "600.1", "works"), Report(2, "feeder", "RTX 4080", "600.2", "broken"), Report(3, "native", "RTX 4090", "600.1", "broken", true) };
    Check("compatibility filters exclude hidden reports", CompatibilityReports.Filter(reports).Count == 2);
    Check("compatibility filters combine route GPU driver and result", CompatibilityReports.Filter(reports, "native", "RTX 4090", "600.1", "works").Single().Id == 1);
    Check("compatibility filters return no fabricated matches", CompatibilityReports.Filter(reports, driver: "missing").Count == 0);
    Console.WriteLine($"{passed} passed; isolated fixtures only.");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(root, true);
}
