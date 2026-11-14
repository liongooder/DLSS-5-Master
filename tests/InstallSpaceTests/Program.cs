using DLSS5Master.Core;

var root = Path.Combine(Path.GetTempPath(), "d5m-space-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var source = Path.Combine(root, "source.bin");
var game = Path.Combine(root, "game");
Directory.CreateDirectory(game);
File.WriteAllBytes(source, new byte[100]);
int passed = 0;
void Check(string name, long actual, long expected)
{
    if (actual != expected) throw new Exception($"{name}: expected {expected}, got {actual}");
    Console.WriteLine("PASS " + name);
    passed++;
}
try
{
    var a = Path.Combine(game, "a.dll");
    var b = Path.Combine(game, "nested", "b.dll");
    Check("one source copied to two destinations", Installers.CopySpaceNeeded(game, null, new[] { (source, a), (source, b) }), 200);
    Check("same destination counted once", Installers.CopySpaceNeeded(game, null, new[] { (source, a), (source, a.ToUpperInvariant()) }), 100);
    File.WriteAllBytes(a, new byte[70]);
    Check("existing destination backup included", Installers.CopySpaceNeeded(game, null, new[] { (source, a) }), 170);
    Check("backup shared by repeated destination writes", Installers.CopySpaceNeeded(game, null, new[] { (source, a), (source, a) }, new[] { a }), 170);
    Check("deleted file backup included", Installers.CopySpaceNeeded(game, null, Array.Empty<(string, string)>(), new[] { a }), 70);

    var old = new Manifest { BackupPrefix = "originals/space-test" };
    var originalDir = Journal.OriginalsDir(game, old);
    Directory.CreateDirectory(originalDir);
    File.WriteAllBytes(Path.Combine(originalDir, "a.dll"), new byte[300]);
    old.Replaced.Add(new Replacement { Rel = "a.dll" });
    Check("reinstall backs up restored original size", Installers.CopySpaceNeeded(game, old, new[] { (source, a) }), 400);
    File.Delete(a);
    Check("currently missing replacement will be restored", Installers.CopySpaceNeeded(game, old, new[] { (source, a) }), 400);
    File.WriteAllBytes(a, new byte[70]);
    old.Replaced.Clear();
    old.Added.Add("a.dll");
    Check("previously added file needs no new backup", Installers.CopySpaceNeeded(game, old, new[] { (source, a) }), 100);
    var larger = Path.Combine(root, "larger.bin");
    File.WriteAllBytes(larger, new byte[500]);
    Check("largest planned write per destination", Installers.CopySpaceNeeded(game, null, new[] { (source, a), (larger, a) }), 570);

    // Exercise the actual preflight planner, not only its size accumulator.
    var planner = typeof(Installers).GetMethod("InstallSpaceNeeded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var runtime = new Dictionary<string, string> { ["nvngx_dlssnr.dll"] = source };
    var scan = new GameScan { Dir = game };
    scan.Upscalers.Add(new UpscalerFile(a, "a.dll", "nvngx_dlssnr.dll", UpscalerKind.DlssNR, null, 64));
    var request = new InstallRequest { GameDir = game, ExePath = Path.Combine(game, "game.exe"), Route = Route.Native, Bitness = 64, Api = RenderApi.DX12 };
    long Plan(InstallRequest req, Components.FeederPayload? feeder = null) => (long)planner.Invoke(null,
        new object?[] { req, scan, runtime, null, null, null, null, feeder, null, null, null, null })!;
    Check("route planner includes runtime mirror and backups", Plan(request), 270 + 70 + 1024 * 1024);
    // 100 for each NR destination, plus the 70-byte existing destination backup.
    var feederRequest = new InstallRequest { GameDir = game, ExePath = request.ExePath, Route = Route.Feeder, Bitness = 32, Api = RenderApi.DX12 };
    var payload = new Components.FeederPayload(source, source, source, Path.Combine(root, "no-shaders"), null);
    Check("32-bit feeder includes separate host copies", Plan(feederRequest, payload), 470 + 70 + 1024 * 1024);
    Console.WriteLine($"{passed} passed, 0 failed; temporary fixtures only");
}
finally
{
    // All paths removed here belong to the unique temporary fixture directory.
    Directory.Delete(root, true);
}
