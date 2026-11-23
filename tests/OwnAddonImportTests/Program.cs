using DLSS5Master.Core;
using System.IO.Compression;

var root = Path.Combine(Path.GetTempPath(), "d5m-addon-import-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("DLSS5MASTER_DATA", Path.Combine(root, "data"));
var cache = Path.Combine(root, "imports");
int passed = 0;
void Check(string name, bool ok)
{
    if (!ok) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}
string Zip(string name, params string[] entries)
{
    var file = Path.Combine(root, name + ".zip");
    using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
    foreach (var entry in entries)
    {
        using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
        writer.Write("fixture:" + entry);
    }
    return file;
}
void Reject(string name, string path)
{
    var before = Directory.Exists(cache) ? Directory.GetDirectories(cache).Length : 0;
    bool rejected = false;
    try { OwnAddonImport.Prepare(path, cache); }
    catch (Exception e) when (e is InstallException or IOException or InvalidDataException) { rejected = true; }
    Check(name, rejected);
    Check(name + " cleans incomplete import", !Directory.Exists(cache) || Directory.GetDirectories(cache).Length == before);
}
try
{
    var folder = Path.Combine(root, "folder");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, NrAddon.Addon64), "existing");
    var importedFolder = OwnAddonImport.Prepare(folder, cache);
    Check("existing folder copied to managed import cache", importedFolder != folder
        && importedFolder.StartsWith(Path.GetFullPath(cache) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && File.ReadAllText(Path.Combine(importedFolder, NrAddon.Addon64)) == "existing");
    Check("source folder unchanged", File.ReadAllText(Path.Combine(folder, NrAddon.Addon64)) == "existing");
    File.WriteAllText(Path.Combine(folder, NrAddon.Addon64), "source changed later");
    Check("managed copy independent of source", File.ReadAllText(Path.Combine(importedFolder, NrAddon.Addon64)) == "existing");
    Check("already managed copy reused", OwnAddonImport.Prepare(importedFolder, cache) == importedFolder);
    var zip = Zip("flat", NrAddon.Addon64, NrAddon.Addon32, "dlss5-nr-host/helper.exe");
    var originalHash = Components.Sha256(zip);
    var copy = OwnAddonImport.Prepare(zip, cache);
    Check("ZIP extracted with complete host", NrAddon.IsCopy(copy) && File.Exists(Path.Combine(copy, NrAddon.Addon32)) && File.Exists(Path.Combine(copy, "dlss5-nr-host/helper.exe")));
    Check("source ZIP preserved", Components.Sha256(zip) == originalHash);
    var wrapped = OwnAddonImport.Prepare(Zip("wrapped", "release/addon/" + NrAddon.Addon64, "release/addon/dlss5-nr-host/" + NrAddon.Addon64), cache);
    Check("nested wrapper accepted without selecting host", NrAddon.IsCopy(wrapped) && Path.GetFileName(wrapped) == "addon");
    Check("repeat imports use independent folders", OwnAddonImport.Prepare(zip, cache) != copy);

    // A ZIP import must be the same usable source for every game in a batch.
    var settings = new AppSettings { NrFolder = copy };
    var context = BulkContext.Read(settings, new Components(settings));
    Check("bulk install finds the imported ZIP source", context.NrCopy == copy && context.NrCopyHasHost);
    context = context with { NeuralRuntime = true, DlssRuntime = true, ReShade64 = true, ReShade32 = true };
    BulkGame Game(string name, RenderApi api = RenderApi.DX12, string? antiCheat = null)
    {
        var dir = Path.Combine(root, name);
        return new BulkGame
        {
            Entry = new GameEntry { Name = name, Dir = dir },
            Scan = new GameScan
            {
                Dir = dir, AntiCheatName = antiCheat,
                Primary = new ExeInfo(Path.Combine(dir, "game.exe"), "game.exe", 64, api, 1000, 0)
            }
        };
    }
    var options = new BulkOptions(Route.NrAddon, Components.OptiBuilds[0], false, false);
    var batch = BulkSetup.Plan(new[] { Game("first"), Game("second"), Game("online", antiCheat: "Easy Anti-Cheat"), Game("vulkan", RenderApi.Vulkan) }, options, context);
    Check("bulk install uses imported copy for multiple games", batch.Take(2).All(r => r.Request?.NrAddonSource == copy));
    Check("bulk install skips anti-cheat games", batch[2].Request is null && batch[2].AntiCheat);
    Check("bulk install skips unsupported games with a reason", batch[3].Request is null && batch[3].Skip is not null);
    Check("bulk install offers no NeuralMaster route", !BulkSetup.Routes.Contains(Route.NeuralMaster));
    var existing = Game("installed");
    existing.Scan!.Manifest = new Manifest { Route = "nraddon", Exe = "game.exe" };
    Check("bulk install keeps existing setups unless reinstall is chosen", BulkSetup.Plan(existing, options, context) is { NeedsAgain: true, Request: null });
    Check("bulk install allows explicit reinstall", BulkSetup.Plan(existing, options with { Again = true }, context).Request is not null);
    Reject("invalid add-on rejected", Zip("wrong", "readme.txt"));
    Reject("host-only ZIP rejected", Zip("host-only", "dlss5-nr-host/" + NrAddon.Addon64));
    var broken = Path.Combine(root, "broken.zip");
    File.WriteAllText(broken, "not a ZIP");
    Reject("corrupt ZIP rejected", broken);
    Reject("archive path traversal rejected", Zip("traversal", "../escaped.txt", NrAddon.Addon64));
    Check("no file escaped import", !File.Exists(Path.Combine(cache, "escaped.txt")));
    foreach (var source in args)
    {
        var prepared = OwnAddonImport.Prepare(source, cache);
        Check("real package accepted: " + Path.GetFileName(source.TrimEnd('\\', '/')), NrAddon.IsCopy(prepared) && Directory.Exists(Path.Combine(prepared, NrAddon.HostFolder)));
        if (File.Exists(source))
        {
            using var archive = ZipFile.OpenRead(source);
            var entry = archive.Entries.Single(e => e.Name == NrAddon.Addon64 && !e.FullName.Contains(NrAddon.HostFolder));
            using var input = entry.Open();
            using var extracted = File.OpenRead(Path.Combine(prepared, NrAddon.Addon64));
            Check("real add-on bytes preserved", System.Security.Cryptography.SHA256.HashData(input).SequenceEqual(System.Security.Cryptography.SHA256.HashData(extracted)));
        }
    }
    Console.WriteLine($"{passed} passed, 0 failed; no DLLs loaded or games changed");
}
finally
{
    // This absolute, uniquely generated directory contains only this test's fixtures.
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(root, true);
}
