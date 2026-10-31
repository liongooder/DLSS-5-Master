using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DLSS5Master.Core;

// Keep the real %LOCALAPPDATA%\DLSS5Master (log, settings, install ID) out of the tests.
var testData = Path.Combine(Path.GetTempPath(), "d5m-tests-" + Guid.NewGuid().ToString("N")[..8]);
Environment.SetEnvironmentVariable("DLSS5MASTER_DATA", testData);

int passed = 0, failed = 0;
void Check(string name, bool ok, string? detail = null)
{
    if (ok) passed++;
    else { failed++; Console.WriteLine($"FAIL {name}{(detail is null ? "" : "  -> " + detail)}"); }
}
void Throws<T>(string name, Action a) where T : Exception
{
    try { a(); Check(name, false, "no exception"); }
    catch (T) { Check(name, true); }
    catch (Exception e) { Check(name, false, e.GetType().Name + ": " + e.Message); }
}
string Show(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

// ====================================================================== Ini
{
    var crlf = "; c\r\n[Menu]\r\nShortcutKey = auto\r\n\r\n[X]\r\na=1\r\n";
    var r = Ini.Set(crlf, "Menu", "ShortcutKey", "0x24");
    Check("ini crlf replace keeps prefix", r == "; c\r\n[Menu]\r\nShortcutKey = 0x24\r\n\r\n[X]\r\na=1\r\n", Show(r));
    r = Ini.Set(r, "New", "k", "v");
    Check("ini crlf new section", r == "; c\r\n[Menu]\r\nShortcutKey = 0x24\r\n\r\n[X]\r\na=1\r\n\r\n[New]\r\nk=v\r\n", Show(r));
    r = Ini.Set(r, "menu", "Other", "5");
    Check("ini insert into existing section after last non-blank", r == "; c\r\n[Menu]\r\nShortcutKey = 0x24\r\nOther=5\r\n\r\n[X]\r\na=1\r\n\r\n[New]\r\nk=v\r\n", Show(r));
    Check("ini get case-insensitive", Ini.Get(r, "MENU", "shortcutkey") == "0x24");

    var lf = "# top comment\nTechniques=A@a.fx\nKey2 = b\n\n[S]\n; about x\n; second line\nx= 1 \n[T]\ny=2";
    Check("ini lf root get", Ini.Get(lf, "", "Techniques") == "A@a.fx");
    Check("ini get trims", Ini.Get(lf, "S", "x") == "1");
    Check("ini missing section", Ini.Get(lf, "Nope", "x") is null);
    Check("ini missing key", Ini.Get(lf, "S", "nope") is null);
    Check("ini comment is not key", Ini.Get("[A]\n;k=1\n", "A", ";k") is null && Ini.Get("[A]\n#k=1\n", "A", "#k") is null);
    var r2 = Ini.Set(lf, "", "New", "v");
    Check("ini lf root insert", r2 == "# top comment\nTechniques=A@a.fx\nKey2 = b\nNew=v\n\n[S]\n; about x\n; second line\nx= 1 \n[T]\ny=2\n", Show(r2));
    var r3 = Ini.Set(lf, "T", "y", "3");
    Check("ini lf no CR introduced + trailing newline", !r3.Contains('\r') && r3.EndsWith("y=3\n") && !r3.EndsWith("\n\n"), Show(r3));
    Check("ini comments preserved", r3.Contains("; about x\n; second line\n") && r3.StartsWith("# top comment\n"));
    var root = Ini.Set("[A]\nk=1\n", "", "Top", "1");
    Check("ini root key into file without root lines", root == "Top=1\n[A]\nk=1\n", Show(root));
    Check("ini null input", Ini.Set(null, "S", "k", "v") == "[S]\nk=v\n", Show(Ini.Set(null, "S", "k", "v")));
    Check("ini empty root", Ini.Set("", "", "k", "v") == "k=v\n");
    Check("ini bom ignored", Ini.Get("\uFEFFa=1\n[S]\nb=2", "", "a") == "1");
    Check("ini trailing blank lines collapse", Ini.Set("[S]\nk=1\n\n\n", "S", "k", "2") == "[S]\nk=2\n");

    var e = Ini.Entries(lf);
    Check("ini entries count", e.Count == 4, e.Count.ToString());
    Check("ini entries root comment", e[0] == ("", "Techniques", "A@a.fx", "top comment"), e[0].ToString());
    Check("ini entries no comment", e[1] == ("", "Key2", "b", ""), e[1].ToString());
    Check("ini entries multi-line comment", e[2] == ("S", "x", "1", "about x\nsecond line"), e[2].ToString());
    Check("ini entries section", e[3] == ("T", "y", "2", ""), e[3].ToString());
    Check("ini entries crlf", Ini.Entries(crlf).Count == 2 && Ini.Entries(crlf)[0].section == "Menu");

    var tmp = Path.Combine(Path.GetTempPath(), "dlss5cr-ini-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    File.WriteAllBytes(Path.Combine(tmp, "u16.ini"), new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[A]\r\nk=ü\r\n")).ToArray());
    File.WriteAllBytes(Path.Combine(tmp, "u8.ini"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[A]\nk=é\n")).ToArray());
    Check("ini readtext utf16", Ini.Get(Ini.ReadText(Path.Combine(tmp, "u16.ini")), "A", "k") == "ü");
    Check("ini readtext utf8 bom", Ini.ReadText(Path.Combine(tmp, "u8.ini")) == "[A]\nk=é\n");
    Check("ini readtext missing", Ini.ReadText(Path.Combine(tmp, "missing.ini")) == "");
    Directory.Delete(tmp, true);
}

// ====================================================================== Journal / Session
{
    var game = Path.Combine(Path.GetTempPath(), "dlss5cr-game-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(game, "bin", "sub"));
    var exe = Path.Combine(game, "bin", "Game.exe");
    File.WriteAllBytes(exe, RandomNumberGenerator.GetBytes(4096));
    File.WriteAllText(Path.Combine(game, "bin", "config.ini"), "[A]\r\nk=1\r\n");
    File.WriteAllBytes(Path.Combine(game, "bin", "sub", "data.bin"), RandomNumberGenerator.GetBytes(10000));
    var ro = Path.Combine(game, "bin", "readonly.dll");
    File.WriteAllBytes(ro, RandomNumberGenerator.GetBytes(2000));
    File.SetAttributes(ro, FileAttributes.ReadOnly);
    Directory.CreateDirectory(Path.Combine(game, "emptyexisting"));

    Dictionary<string, string> Snapshot()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Directory.EnumerateDirectories(game, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(game, d);
            if (rel.StartsWith(Journal.BackupDir, StringComparison.OrdinalIgnoreCase)) continue;
            map["D:" + rel] = "";
        }
        foreach (var f in Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(game, f);
            if (rel.StartsWith(Journal.BackupDir, StringComparison.OrdinalIgnoreCase)) continue;
            map["F:" + rel] = Components.Sha256(f) + "|" + ((File.GetAttributes(f) & FileAttributes.ReadOnly) != 0);
        }
        return map;
    }
    bool SameSnap(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    var before = Snapshot();
    var logs = new List<string>();
    var src = Path.Combine(Path.GetTempPath(), "dlss5cr-src-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(src, "tree", "deeper"));
    File.WriteAllBytes(Path.Combine(src, "new.dll"), RandomNumberGenerator.GetBytes(3000));
    File.WriteAllBytes(Path.Combine(src, "tree", "t1.txt"), RandomNumberGenerator.GetBytes(30));
    File.WriteAllBytes(Path.Combine(src, "tree", "deeper", "t2.txt"), RandomNumberGenerator.GetBytes(30));

    Check("journal no manifest", Journal.ReadManifest(game) is null);
    Check("journal restore without manifest = false", Session.Restore(game, logs.Add) == false);
    Throws<InstallException>("journal safepath escape", () => Journal.SafePath(game, @"..\outside.txt"));
    Throws<InstallException>("journal safepath rooted", () => Journal.SafePath(game, @"C:\Windows\x.txt"));
    Check("journal safepath ok", Journal.SafePath(game, @"bin\x.dll") == Path.Combine(game, "bin", "x.dll"));
    Check("journal canwrite", Journal.CanWrite(game) && Directory.GetFiles(game).Length == 0);

    var s = Session.Begin(game, exe, "test", "dxgi", 64, logs.Add);
    s.Save();
    Check("session exe relative", s.Manifest.Exe == @"bin\Game.exe", s.Manifest.Exe);
    Check("session prefix", s.Manifest.BackupPrefix.StartsWith("originals/") && s.Manifest.BackupPrefix.Length == "originals/".Length + 32);

    // add a file in brand-new nested folders
    var nested = Path.Combine(game, "bin", "new1", "new2", "new3", "new.dll");
    s.CopyTracked(Path.Combine(src, "new.dll"), nested, "test");
    Check("session added", s.WasAdded(@"bin\new1\new2\new3\new.dll"));
    Check("session addeddirs", new[] { @"bin\new1", @"bin\new1\new2", @"bin\new1\new2\new3" }.All(d => s.Manifest.AddedDirs.Contains(d)), string.Join(";", s.Manifest.AddedDirs));
    // replace existing files (one read-only)
    s.WriteTracked(Path.Combine(game, "bin", "config.ini"), "[A]\r\nk=2\r\n");
    s.CopyTracked(Path.Combine(src, "new.dll"), ro, "test");
    s.WriteTracked(Path.Combine(game, "bin", "config.ini"), "[A]\r\nk=3\r\n"); // second write must not re-backup
    Check("session replaced count", s.Manifest.Replaced.Count == 2, s.Manifest.Replaced.Count.ToString());
    // delete a tracked original
    s.DeleteTracked(Path.Combine(game, "bin", "sub", "data.bin"));
    Check("session deleted original", !File.Exists(Path.Combine(game, "bin", "sub", "data.bin")) && s.Manifest.Replaced.Count == 3);
    // add then delete our own file
    var temp = Path.Combine(game, "bin", "temp.txt");
    s.WriteTracked(temp, "x");
    s.DeleteTracked(temp);
    Check("session delete own removes from Added", !s.WasAdded(@"bin\temp.txt") && !File.Exists(temp));
    // a tracked-only file that the "game" creates later
    s.TrackBeforeWrite(Path.Combine(game, "bin", "ReShade.log"));
    File.WriteAllText(Path.Combine(game, "bin", "ReShade.log"), "created by game");
    // copy a tree into an existing empty folder and a new one
    s.CopyTreeTracked(Path.Combine(src, "tree"), Path.Combine(game, "emptyexisting", "shaders"));
    Check("session tree copied", File.Exists(Path.Combine(game, "emptyexisting", "shaders", "deeper", "t2.txt")));
    s.Save();

    var m = Journal.ReadManifest(game);
    Check("manifest roundtrip", m is not null && m.Added.Count == s.Manifest.Added.Count && m.Replaced.Count == 3 && m.Route == "test");
    var json = File.ReadAllText(Path.Combine(Journal.BackupRoot(game), "manifest.json"));
    Check("manifest pascal-case indented", json.Contains("\"BackupPrefix\"") && json.Contains("\n  "));

    // reuse on a second Begin keeps lists and prefix
    var s2 = Session.Begin(game, exe, "test2", "dxgi", 64, logs.Add);
    Check("session begin reuses manifest", s2.Manifest.BackupPrefix == s.Manifest.BackupPrefix && s2.Manifest.Added.Count == s.Manifest.Added.Count);
    s2.Save();

    // missing backup -> exception and no change
    var prefixDir = Path.Combine(Journal.BackupRoot(game), s.Manifest.BackupPrefix.Replace('/', '\\'));
    var aBackup = Path.Combine(prefixDir, @"bin\config.ini");
    var held = File.ReadAllBytes(aBackup);
    File.Delete(aBackup);
    var mid = Snapshot();
    Throws<InstallException>("restore refuses with missing backup", () => Session.Restore(game, logs.Add));
    Check("restore changed nothing on failure", SameSnap(mid, Snapshot()) && File.Exists(Path.Combine(Journal.BackupRoot(game), "manifest.json")));
    File.WriteAllBytes(aBackup, held);

    Check("restore returns true", Session.Restore(game, logs.Add));
    var after = Snapshot();
    Check("restore byte-identical", SameSnap(before, after),
        string.Join(", ", after.Keys.Except(before.Keys).Concat(before.Keys.Except(after.Keys)).Concat(before.Keys.Where(k => after.ContainsKey(k) && after[k] != before[k]))));
    Check("restore manifest renamed", !File.Exists(Path.Combine(Journal.BackupRoot(game), "manifest.json"))
        && Directory.GetFiles(Journal.BackupRoot(game), "manifest.json.done-*").Length == 1);
    Check("restore originals deleted", !Directory.Exists(prefixDir));
    Check("restore logged", logs.Any(l => l.Contains("Restored")) && logs.Any(l => l.Contains("Removed")));

    // a record whose backup folder points outside the backup root: Restore must refuse, and never delete that folder
    var victim = Path.Combine(Path.GetTempPath(), "dlss5cr-victim-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(victim);
    File.WriteAllText(Path.Combine(victim, "keep.txt"), "not the game's");
    foreach (var (label, prefix) in new[] { ("dotdot", @"..\..\" + Path.GetFileName(victim)), ("absolute", victim), ("root itself", ".") })
    {
        File.WriteAllText(Path.Combine(Journal.BackupRoot(game), "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(new Manifest { BackupPrefix = prefix }));
        Throws<InstallException>($"restore refuses a backup folder outside the root ({label})", () => Session.Restore(game, logs.Add));
        Check($"restore left that folder alone ({label})", File.Exists(Path.Combine(victim, "keep.txt")) && Directory.Exists(Journal.BackupRoot(game)));
    }
    Directory.Delete(victim, true);

    // the DLL library's folder name comes from the DLL's own version text: it must stay one folder inside the library
    foreach (var v in new[] { @"..\..\outside", "C:/Windows/System32", "3.7.10.0", ".." })
    {
        var folder = Installers.LibraryFolder(v, 64);
        var lib = Path.Combine(Path.GetTempPath(), "lib");
        var full = Path.GetFullPath(Path.Combine(lib, "nvngx_dlss.dll", folder, "nvngx_dlss.dll"));
        Check($"dll library folder stays inside ({v})", full.StartsWith(Path.Combine(lib, "nvngx_dlss.dll") + "\\", StringComparison.OrdinalIgnoreCase)
            && folder.IndexOfAny(new[] { '\\', '/', ':' }) < 0, folder);
    }
    Check("dll library folder keeps a normal version", Installers.LibraryFolder("3.7.10.0", 64) == "3.7.10.0-x64");

    // corrupt manifest
    File.WriteAllText(Path.Combine(Journal.BackupRoot(game), "manifest.json"), "{ not json");
    Throws<InstallException>("corrupt manifest", () => Journal.ReadManifest(game));

    File.SetAttributes(ro, FileAttributes.Normal);
    Directory.Delete(game, true);
    Directory.Delete(src, true);
}

// ====================================================================== Restore: what the add-ons write while the game runs
{
    var game = Path.Combine(Path.GetTempPath(), "dlss5cr-runtime-" + Guid.NewGuid().ToString("N"));
    var bin = Path.Combine(game, "bin");
    Directory.CreateDirectory(bin);
    var exe = Path.Combine(bin, "Game.exe");
    File.WriteAllBytes(exe, RandomNumberGenerator.GetBytes(4096));
    // There before the install: the crash log of a RenoDX set up by hand earlier.
    var earlier = Path.Combine(bin, "RenoDX-DLSS5-crash.log");
    File.WriteAllText(earlier, "from before");
    File.SetCreationTimeUtc(earlier, DateTime.UtcNow.AddHours(-1));

    var logs = new List<string>();
    var s = Session.Begin(game, exe, "feeder", "dxgi", 32, logs.Add);
    s.WriteTracked(Path.Combine(bin, "ReShade.ini"), "[ADDON]\r\nAddonPath=.\\\r\n");
    s.WriteTracked(Path.Combine(bin, "host64", "ReShade.ini"), "[ADDON]\r\nAddonPath=.\\\r\n");
    s.Save();

    // The game runs later (past the file system's clock tick): the add-ons write their files, the player keeps a screenshot.
    Thread.Sleep(50);
    var written = new[] { "dlss5-feed.log", "dlss5-feed-crash.dmp", "ReShade.ini.v10-backup", "RenoDX-DLSS5-crash-4242-1.dmp",
        "MFGAdaUnlock.ini", @"host64\dlss5-feed-host.log", @"host64\dlss5-feed-host-crash.dmp" };
    foreach (var w in written) File.WriteAllText(Path.Combine(bin, w), "written while the game ran");
    File.WriteAllText(earlier, "rewritten by this run");
    Directory.CreateDirectory(Path.Combine(bin, "DLSS5 Screenshots"));
    File.WriteAllText(Path.Combine(bin, "DLSS5 Screenshots", "shot.png"), "the player's");
    File.WriteAllText(Path.Combine(bin, "save.dat"), "the game's");
    Check("runtime leftovers found", Journal.RuntimeLeftovers(game, s.Manifest).Count == written.Length,
        string.Join(", ", Journal.RuntimeLeftovers(game, s.Manifest).Select(f => Path.GetRelativePath(bin, f))));

    Check("restore after a run", Session.Restore(game, logs.Add));
    Check("restore removes what the add-ons wrote while the game ran", written.All(w => !File.Exists(Path.Combine(bin, w))),
        string.Join(", ", written.Where(w => File.Exists(Path.Combine(bin, w)))));
    Check("restore removes the host64 folder those files kept", !Directory.Exists(Path.Combine(bin, "host64")));
    Check("restore keeps a same-named file from before the install", File.Exists(earlier));
    Check("restore keeps the player's and the game's own files",
        File.Exists(Path.Combine(bin, "DLSS5 Screenshots", "shot.png")) && File.Exists(Path.Combine(bin, "save.dat")) && File.Exists(exe));
    Check("restore logs the runtime removals", logs.Count(l => l.Contains("written by an add-on while the game ran")) == written.Length);
    File.WriteAllText(Path.Combine(bin, "dlss5-feed.log"), "a later run");
    Check("runtime sweep needs the record's date", Journal.RuntimeLeftovers(game, new Manifest { Exe = @"bin\Game.exe" }).Count == 0
        && Journal.RuntimeLeftovers(game, new Manifest { Exe = @"bin\Game.exe", Date = DateTime.UtcNow.AddMinutes(-1) }).Count == 1);

    // A host64 planted as a junction out of the game folder: the sweep must not reach through it (audit 0.9.5, DEF-017).
    var outside = Path.Combine(Path.GetTempPath(), "dlss5cr-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    var outsideLog = Path.Combine(outside, "dlss5-feed-host.log");
    File.WriteAllText(outsideLog, "not the game's");
    var link = Path.Combine(bin, "host64");
    using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
        mk!.WaitForExit();
    var swept = Journal.RuntimeLeftovers(game, new Manifest { Exe = @"bin\Game.exe", Date = DateTime.UtcNow.AddMinutes(-1) });
    Check("runtime sweep never reaches through a junction out of the game", Directory.Exists(link)
        && !swept.Any(f => f.StartsWith(link, StringComparison.OrdinalIgnoreCase)) && File.Exists(outsideLog), string.Join(", ", swept));
    Directory.Delete(link);
    Directory.Delete(outside, true);
    Directory.Delete(game, true);
}

// ====================================================================== Install: DLSS 5 add-ons that other installs left behind
{
    var game = Path.Combine(Path.GetTempPath(), "dlss5cr-addons-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(game);
    var exe = Path.Combine(game, "Game.exe");
    File.WriteAllBytes(exe, RandomNumberGenerator.GetBytes(4096));
    // Left by another tool (as in House Party: an older MFG unlock), next to unrelated add-ons the player put there.
    var others = new[] { "renodx-mfgunlock.addon64", "renodx-dlss.addon64", "dlss5-feed-helper.addon64", "dlss5-feed.addon32" };
    var unrelated = new[] { "renodx-houseparty.addon64", "ShaderToggler.addon64" };
    foreach (var n in others.Concat(unrelated).Append("renodx-dlss5.addon64"))
        File.WriteAllBytes(Path.Combine(game, n), RandomNumberGenerator.GetBytes(512));
    var before = Directory.EnumerateFiles(game).ToDictionary(f => new FileInfo(f).Name, Components.Sha256, StringComparer.OrdinalIgnoreCase);

    var logs = new List<string>();
    var s = Session.Begin(game, exe, "native", "dxgi", 64, logs.Add);
    var setAside = Installers.SetAsideOtherAddons(s, game, "renodx-dlss5.addon64", null);
    s.Save();
    Check("DLSS 5 add-ons from other installs are set aside", setAside.Count == others.Length && others.All(n => !File.Exists(Path.Combine(game, n))),
        string.Join(", ", setAside));
    Check("the add-on this install uses and unrelated add-ons stay",
        File.Exists(Path.Combine(game, "renodx-dlss5.addon64")) && unrelated.All(n => File.Exists(Path.Combine(game, n))));
    Check("each one set aside is logged", logs.Count(l => l.StartsWith("Set aside ")) == others.Length);
    var bin = Path.Combine(game, "bin");
    Directory.CreateDirectory(bin);
    File.WriteAllBytes(Path.Combine(bin, "renodx-mfgunlock.addon64"), RandomNumberGenerator.GetBytes(512));
    Check("the MFG unlock stays when this install adds it", Installers.SetAsideOtherAddons(s, bin, "renodx-dlss5.addon64", "renodx-mfgunlock.addon64").Count == 0
        && File.Exists(Path.Combine(bin, "renodx-mfgunlock.addon64")));
    Check("restore puts the set-aside add-ons back byte for byte", Session.Restore(game, logs.Add)
        && before.All(kv => File.Exists(Path.Combine(game, kv.Key)) && Components.Sha256(Path.Combine(game, kv.Key)) == kv.Value));
    Directory.Delete(game, true);
}

// ====================================================================== Install: add-ons ReShade loads as soon as it starts (LoadFromDllMain)
{
    // Phantom Blade Zero's layout; ReShade could not start the add-ons there until they were loaded early.
    var game = Path.Combine(Path.GetTempPath(), "dlss5cr-early-" + Guid.NewGuid().ToString("N"));
    var bin = Path.Combine(game, "platform", "steam");
    Directory.CreateDirectory(bin);
    var exe = Path.Combine(bin, "PBZTest.exe");
    File.WriteAllBytes(exe, RandomNumberGenerator.GetBytes(4096));
    foreach (var n in new[] { "renodx-dlss5.addon64", "renodx-mfgunlock.addon64", "ShaderToggler.addon64" })
        File.WriteAllBytes(Path.Combine(bin, n), RandomNumberGenerator.GetBytes(512));
    var logs = new List<string>();
    Session.Begin(game, exe, "native", "dxgi", 64, logs.Add).Save();
    Manifest Record() => Journal.ReadManifest(game)!;

    // The lines ReShade 6.8 wrote there, once per graphics device the game created.
    var reshadeLog = Path.Combine(bin, "ReShade.log");
    File.WriteAllText(reshadeLog, string.Join("\r\n",
        $"09:34:24:610 [29068] | INFO  | Loading add-on from '{bin}\\renodx-dlss5.addon64' ...",
        $"09:34:24:673 [29068] | ERROR | Failed to load add-on from '{bin}\\renodx-dlss5.addon64' with error code 1114!",
        $"09:34:24:673 [29068] | ERROR | Failed to load add-on from '{bin}\\renodx-mfgunlock.addon64' with error code 1114!",
        $"09:34:27:169 [29068] | ERROR | Failed to load add-on from '{bin}\\renodx-dlss5.addon64' with error code 1114!",
        $"09:34:27:170 [29068] | ERROR | Failed to load add-on from '{bin}\\ShaderToggler.addon64' with error code 1114!",
        $"09:34:27:171 [29068] | ERROR | Failed to load add-on from '{bin}\\renodx-dlss.addon64' with error code 126!"));
    var failing = Installers.AddonsToLoadEarly(game, Record());
    Check("error 1114 in ReShade.log names the DLSS 5 add-ons to load early",
        failing.SequenceEqual(new[] { "renodx-dlss5.addon64", "renodx-mfgunlock.addon64" }), string.Join(", ", failing));
    File.SetLastWriteTimeUtc(reshadeLog, DateTime.UtcNow.AddDays(-1));
    Check("a ReShade.log from before the install counts for nothing", Installers.AddonsToLoadEarly(game, Record()).Count == 0);
    File.SetLastWriteTimeUtc(reshadeLog, DateTime.UtcNow.AddMinutes(1));
    using (new FileStream(reshadeLog, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        Check("ReShade.log is read while ReShade holds it open", Installers.AddonsToLoadEarly(game, Record()).Count == 2);

    // The player's own ReShade.ini, naming an add-on that is gone and an entry that is not a file.
    var ini = Path.Combine(bin, "ReShade.ini");
    var original = "[GENERAL]\r\nPresetPath=.\\ReShadePreset.ini\r\n\r\n[ADDON]\r\nLoadFromDllMain=old-gone.addon64,1\r\n";
    File.WriteAllText(ini, original);
    Installers.LoadAddonsEarly(game, logs.Add);
    var line = Ini.Get(Ini.ReadText(ini), "ADDON", "LoadFromDllMain");
    Check("load early lists the folder's DLSS 5 add-ons and drops the gone one", line == "1,renodx-dlss5.addon64,renodx-mfgunlock.addon64", line);
    Check("the rest of ReShade.ini stays", Ini.Get(Ini.ReadText(ini), "GENERAL", "PresetPath") == @".\ReShadePreset.ini");
    Check("nothing is offered once they load early", Installers.AddonsToLoadEarly(game, Record()).Count == 0);
    Check("loading early twice changes nothing", !Installers.UpdateEarlyAddons(new Session(game, Record(), logs.Add), bin, loadEarly: true));
    File.Delete(Path.Combine(bin, "renodx-mfgunlock.addon64"));
    var later = new Session(game, Record(), logs.Add);
    Check("a setup takes an add-on that is gone off the line", Installers.UpdateEarlyAddons(later, bin, loadEarly: false)
        && Ini.Get(Ini.ReadText(ini), "ADDON", "LoadFromDllMain") == "1,renodx-dlss5.addon64");
    later.Save();
    Check("restore puts the player's ReShade.ini back", Session.Restore(game, logs.Add) && File.ReadAllText(ini) == original);
    Check("with no ReShade.ini and nothing to load early, none is written",
        !Installers.UpdateEarlyAddons(Session.Begin(game, exe, "native", "dxgi", 64, logs.Add), Path.Combine(game, "platform"), loadEarly: false)
        && !File.Exists(Path.Combine(game, "platform", "ReShade.ini")));
    Directory.Delete(game, true);
}

// ====================================================================== Library: games added by hand (Unreal Engine layouts)
{
    var lib = Path.Combine(Path.GetTempPath(), "dlss5cr-lib-" + Guid.NewGuid().ToString("N"));
    void Make(string path, int bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(bytes)); }
    string EngineDlss(string game) => Path.Combine(game, "Engine", "Plugins", "Runtime", "Nvidia", "DLSS", "Binaries", "ThirdParty", "Win64", "nvngx_dlss.dll");

    // The usual layout: a launcher stub on top, the game in <Project>\Binaries\Win64.
    var root = Path.Combine(lib, "Phantom Test™ café");
    var launcher = Path.Combine(root, "PhantomTest.exe");
    var shipping = Path.Combine(root, "PTGame", "Binaries", "Win64", "PTGame-Win64-Shipping.exe");
    Make(launcher, 300_000);
    Make(shipping, 2_000_000);
    Make(EngineDlss(root), 1000);
    Make(Path.Combine(root, "Engine", "Binaries", "Win64", "CrashReportClient.exe"), 4096);
    // Phantom Blade Zero's layout: the game in PBZ\platform\steam, its DLSS under PBZ\Engine\Plugins.
    var pbz = Path.Combine(lib, "PBZ");
    var pbzExe = Path.Combine(pbz, "platform", "steam", "PBZ.exe");
    Make(pbzExe, 2_000_000);
    Make(EngineDlss(pbz), 1000);

    Check("unreal launcher stub leads to the shipping exe", Scanner.UnrealGameFor(launcher) == shipping, Scanner.UnrealGameFor(launcher));
    Check("a game exe is not a launcher stub", Scanner.UnrealGameFor(shipping) is null && Scanner.UnrealGameFor(pbzExe) is null);
    Check("unreal top folder from Binaries\\Win64", Scanner.UnrealRootFor(shipping) == root, Scanner.UnrealRootFor(shipping));
    Check("unreal top folder from platform\\steam", Scanner.UnrealRootFor(pbzExe) == pbz, Scanner.UnrealRootFor(pbzExe));
    var far = Path.Combine(lib, "n1", "n2", "n3", "Plain2", "p.exe");
    Make(far, 10);
    Check("no unreal top folder without an Engine folder near the exe", Scanner.UnrealRootFor(far) is null, Scanner.UnrealRootFor(far));
    Directory.Delete(Path.Combine(lib, "n1"), true);
    Check("a picked launcher stub scans as the shipping exe", Scanner.Scan(root, launcher).Primary?.Path == shipping);
    Check("phantom blade zero's own DLSS is found from its top folder", Scanner.Scan(pbz, pbzExe).HasNativeDlss);
    // Another tool's backup beside the game holds copies, not the game's files: setup must not update the DLLs in it.
    var otherBackup = Path.Combine(pbz, "platform", "steam", "codex-dlss-manager-backup-20261030");
    Make(Path.Combine(otherBackup, "nvngx_dlss.dll"), 1000);
    Make(Path.Combine(otherBackup, "nvngx_dlssnr.dll"), 1000);
    var pbzScan = Scanner.Scan(pbz, pbzExe);
    Check("another tool's backup folder is not scanned for DLSS", pbzScan.Upscalers.Count == 1 && pbzScan.Upscalers[0].Path == EngineDlss(pbz),
        string.Join("; ", pbzScan.Upscalers.Select(u => u.Rel)));
    Directory.Delete(otherBackup, true);

    List<GameEntry> Ours(AppSettings st) => Library.Discover(st).Where(g => g.Dir.StartsWith(lib, StringComparison.OrdinalIgnoreCase)).ToList();
    string Describe(List<GameEntry> l) => string.Join(" | ", l.Select(g => $"{g.Name} @ {g.Dir} ({g.Exe})"));
    var byLauncher = Ours(new AppSettings { ManualExes = { launcher } });
    Check("adding the launcher stub adds the game at its top folder", byLauncher.Count == 1 && byLauncher[0].Dir == root && byLauncher[0].Exe == shipping
        && byLauncher[0].Name == "Phantom Test™ café", Describe(byLauncher));
    var byShipping = Ours(new AppSettings { ManualExes = { shipping } });
    Check("adding the shipping exe adds the game at its top folder", byShipping.Count == 1 && byShipping[0].Dir == root, Describe(byShipping));
    var byPbz = Ours(new AppSettings { ManualExes = { pbzExe } });
    Check("adding phantom blade zero's exe adds the game at its top folder", byPbz.Count == 1 && byPbz[0].Dir == pbz && byPbz[0].Exe == pbzExe, Describe(byPbz));
    var ownFolder = Ours(new AppSettings { ExtraFolders = { root } });
    Check("adding an unreal game's own folder adds that one game", ownFolder.Count == 1 && ownFolder[0].Dir == root, Describe(ownFolder));
    var pbzFolder = Ours(new AppSettings { ExtraFolders = { pbz } });
    Check("adding phantom blade zero's own folder adds that one game", pbzFolder.Count == 1 && pbzFolder[0].Dir == pbz, Describe(pbzFolder));
    var folderOfGames = Ours(new AppSettings { ExtraFolders = { lib } });
    Check("a folder of games still lists each game", folderOfGames.Count == 2 && folderOfGames.Any(g => g.Dir == root && g.Name == "Phantom Test™ café")
        && folderOfGames.Any(g => g.Dir == pbz), Describe(folderOfGames));

    // A plain game: its exe at the top, data below. Its own folder adds it; the folder of games now lists three.
    var plain = Path.Combine(lib, "Plain Game");
    Directory.CreateDirectory(Path.Combine(plain, "data"));
    File.WriteAllBytes(Path.Combine(plain, "PlainGame.exe"), RandomNumberGenerator.GetBytes(1_500_000));
    File.WriteAllBytes(Path.Combine(plain, "unins000.exe"), RandomNumberGenerator.GetBytes(2_000_000));
    var plainOwn = Ours(new AppSettings { ExtraFolders = { plain } });
    Check("adding a plain game's own folder adds that one game", plainOwn.Count == 1 && plainOwn[0].Dir == plain, Describe(plainOwn));
    var all = Ours(new AppSettings { ExtraFolders = { lib } });
    Check("a folder of three games lists all three", all.Count == 3, Describe(all));

    // An install made while the game's folder was read as a folder of games keeps working from where it is.
    var oldInstall = Path.Combine(root, "PTGame");
    Directory.CreateDirectory(Path.Combine(oldInstall, Journal.BackupDir));
    File.WriteAllText(Path.Combine(oldInstall, Journal.BackupDir, "manifest.json"), "{}");
    var kept = Ours(new AppSettings { ExtraFolders = { root } });
    Check("an older install below a folder keeps that folder's old reading", kept.Any(g => g.Dir == oldInstall), Describe(kept));
    Directory.Delete(lib, true);
}

// ====================================================================== Scanner
{
    Check("classify sr", Scanner.Classify("NVNGX_DLSS.dll") == UpscalerKind.DlssSR);
    Check("classify fg", Scanner.Classify("nvngx_dlssg.dll") == UpscalerKind.DlssFG);
    Check("classify rr", Scanner.Classify("nvngx_dlssd.dll") == UpscalerKind.DlssRR);
    Check("classify nr", Scanner.Classify("nvngx_dlssnr.dll") == UpscalerKind.DlssNR);
    Check("classify ngx", Scanner.Classify("nvngx.dll") == UpscalerKind.NgxCore && Scanner.Classify("_nvngx.dll") == UpscalerKind.NgxCore);
    Check("classify sl", Scanner.Classify("sl.interposer.dll") == UpscalerKind.Streamline && Scanner.Classify("sl.dlss_g.dll") == UpscalerKind.Streamline);
    Check("classify xess", Scanner.Classify("libxess_dx11.dll") == UpscalerKind.XeSS);
    Check("classify fsr", new[] { "amd_fidelityfx_dx12.dll", "ffx_fsr2_api_x64.dll", "ffx_backend_dx12_x64.dll", "ffx_frameinterpolation_x64.dll" }.All(n => Scanner.Classify(n) == UpscalerKind.Fsr));
    Check("classify other", Scanner.Classify("dxgi.dll") is null && Scanner.Classify("nvngx_dlss.txt") is null && Scanner.Classify("nvngx_dlssx.dll") is null);
    Check("classify path", Scanner.Classify(@"C:\g\bin\nvngx_dlss.dll") == UpscalerKind.DlssSR);

    Check("cmp newer", Scanner.CompareVersions("310.2.1.0", "310.1.0.0") > 0);
    Check("cmp older", Scanner.CompareVersions("3.7.10", "3.7.20.0") < 0);
    Check("cmp equal 3 vs 4", Scanner.CompareVersions("1.2.3", "1.2.3.0") == 0);
    Check("cmp numeric not lexical", Scanner.CompareVersions("10.0.0.0", "9.9.9.9") > 0);
    Check("cmp embedded", Scanner.CompareVersions("DLSS v310.5.0.0 build", "310.4.0") > 0);
    Check("cmp unparsable", Scanner.CompareVersions(null, "1.0.0") == 0 && Scanner.CompareVersions("abc", "1.0.0") == 0 && Scanner.CompareVersions("1.2", "1.0.0") == 0);

    HashSet<string> I(params string[] n) => new(n, StringComparer.OrdinalIgnoreCase);
    Check("api dx12", Scanner.ApiFromImports(I("kernel32.dll", "d3d12.dll", "d3d11.dll", "dxgi.dll")) == RenderApi.DX12);
    Check("api dx11", Scanner.ApiFromImports(I("d3d11.dll", "vulkan-1.dll")) == RenderApi.DX11);
    Check("api dx10", Scanner.ApiFromImports(I("d3d10_1.dll", "dxgi.dll")) == RenderApi.DX10);
    Check("api dxgi only", Scanner.ApiFromImports(I("dxgi.dll", "d3d9.dll")) == RenderApi.DX11);
    Check("api vulkan", Scanner.ApiFromImports(I("vulkan-1.dll", "opengl32.dll")) == RenderApi.Vulkan);
    Check("api dx9", Scanner.ApiFromImports(I("d3d9.dll", "ddraw.dll")) == RenderApi.DX9);
    Check("api dx8", Scanner.ApiFromImports(I("d3d8.dll")) == RenderApi.DX8);
    Check("api ddraw", Scanner.ApiFromImports(I("ddraw.dll", "opengl32.dll")) == RenderApi.DirectDraw);
    Check("api gl", Scanner.ApiFromImports(I("opengl32.dll")) == RenderApi.OpenGL);
    Check("api unknown", Scanner.ApiFromImports(I("kernel32.dll")) == RenderApi.Unknown);
    Check("labels", Scanner.Label(RenderApi.DX12) == "DirectX 12" && Scanner.Label(RenderApi.Unknown) == "Unknown" && Scanner.KindLabel(UpscalerKind.NgxCore) == "NGX");

    // Fake game folder made from a Windows executable (no real game involved).
    var fake = Path.Combine(Path.GetTempPath(), "dlss5cr-scan-" + Guid.NewGuid().ToString("N"));
    var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
    Directory.CreateDirectory(Path.Combine(fake, "Redist"));
    Directory.CreateDirectory(Path.Combine(fake, "EasyAntiCheat"));
    File.Copy(notepad, Path.Combine(fake, "FakeGame.exe"));
    File.Copy(notepad, Path.Combine(fake, "unins000.exe"));
    File.Copy(notepad, Path.Combine(fake, "Redist", "Other.exe"));
    File.WriteAllText(Path.Combine(fake, "nvngx_dlss.dll"), "not a pe");
    var scan = Scanner.Scan(fake);
    Check("scan primary", scan.Primary?.Rel == "FakeGame.exe", scan.Primary?.ToString());
    Check("scan ignores helpers and redist", scan.Executables.Count == 1, string.Join(";", scan.Executables.Select(x => x.Rel)));
    Check("scan anti-cheat", scan.AntiCheat);
    Check("scan upscaler", scan.Upscalers.Count == 1 && scan.Dlss?.Name == "nvngx_dlss.dll" && scan.HasNativeDlss && !scan.HasFrameGen);
    var pref = Scanner.Scan(fake, Path.Combine(fake, "unins000.exe"));
    Check("scan preferred wins", pref.Primary?.Rel == "unins000.exe");
    Check("exe tostring", scan.Primary!.ToString().StartsWith("FakeGame.exe — ") && scan.Primary.ToString().Contains("-bit — ") && scan.Primary.ToString().EndsWith(" MB"), scan.Primary.ToString());
    Directory.Delete(fake, true);
}

// ====================================================================== Installers
{
    var r = Installers.RoutesFor(RenderApi.DX12, 64, true);
    Check("routes order", r.Select(o => o.Route).SequenceEqual(new[] { Route.Native, Route.Multipass, Route.Feeder, Route.OptiScaler }));
    Check("routes dx12 x64 all", r.All(o => o.Unavailable is null));
    Check("routes labels", r[0].Label == "Native DLSS-NR (RenoDX DLSS5)" && r[1].Label == "RenoDX DLSS Tool (multipass)" && r[2].Label == "DLSS5 Feeder" && r[3].Label == "OptiScaler");
    Check("routes descriptions", r.All(o => o.Description.Length > 0));
    r = Installers.RoutesFor(RenderApi.DX11, 64, false);
    Check("routes dx11", r[0].Unavailable is not null && r[1].Unavailable is null && r[2].Unavailable is null && r[3].Unavailable is null);
    Check("opti desc mentions own DLSS", r[3].Description.Contains("DLSS"));
    r = Installers.RoutesFor(RenderApi.DX11, 32, false);
    Check("routes dx11 x86", r[0].Unavailable is not null && r[1].Unavailable is not null && r[2].Unavailable is null && r[3].Unavailable is not null);
    r = Installers.RoutesFor(RenderApi.DX9, 64, false);
    Check("routes dx9", r[0].Unavailable is not null && r[1].Unavailable is null && r[2].Unavailable!.Contains("dgVoodoo") && r[3].Unavailable is not null);
    r = Installers.RoutesFor(RenderApi.Vulkan, 64, false);
    Check("routes vulkan", r[0].Unavailable is not null && r[1].Unavailable is not null && r[2].Unavailable!.Contains("Vulkan layer") && r[3].Unavailable is null);
    r = Installers.RoutesFor(RenderApi.OpenGL, 64, false);
    Check("routes opengl none", r.All(o => o.Unavailable is not null));

    Check("recommend native", Installers.RecommendedRoute(RenderApi.DX12, 64, true) == Route.Native);
    // Native on DirectX 11 games with their own DLSS; never on a game without it (it would only wait for DLSS calls).
    r = Installers.RoutesFor(RenderApi.DX11, 64, true);
    Check("native: DirectX 11 with the game's own DLSS", r[0].Unavailable is null && r[0].Description.Contains("DirectX 11"));
    Check("recommend native on DirectX 11", Installers.RecommendedRoute(RenderApi.DX11, 64, true) == Route.Native);
    Check("native: not without the game's own DLSS", Installers.RoutesFor(RenderApi.DX12, 64, false)[0].Unavailable?.Contains("Feeder") == true &&
          Installers.RoutesFor(RenderApi.DX11, 64, false)[0].Unavailable?.Contains("Feeder") == true);
    Check("native: still not on DirectX 9 or 32-bit", Installers.RoutesFor(RenderApi.DX9, 64, true)[0].Unavailable is not null &&
          Installers.RoutesFor(RenderApi.DX11, 32, true)[0].Unavailable is not null);
    Check("recommend feeder", Installers.RecommendedRoute(RenderApi.DX12, 64, false) == Route.Feeder);
    Check("recommend first avail", Installers.RecommendedRoute(RenderApi.Vulkan, 64, false) == Route.OptiScaler);
    Check("recommend fallback", Installers.RecommendedRoute(RenderApi.OpenGL, 64, false) == Route.Native);

    Check("mfg ok", Installers.MfgUnavailable(RenderApi.DX12, 64, true) is null);
    Check("mfg 32", Installers.MfgUnavailable(RenderApi.DX12, 32, true) is not null);
    Check("mfg vulkan", Installers.MfgUnavailable(RenderApi.Vulkan, 64, true) is not null);
    Check("mfg no fg", Installers.MfgUnavailable(RenderApi.DX12, 64, false)?.Contains("nvngx_dlssg.dll") == true);
    // Like a Dragon: Ishin! on DirectX 12: the Feeder saw the game's own DLSS loaded and switched itself off.
    Check("feeder note on DirectX 12 with the game's own DLSS", Installers.FeederStandsAside(RenderApi.DX12, true)?.Contains("Use Native") == true);
    Check("no feeder note on DirectX 11 or without DLSS", Installers.FeederStandsAside(RenderApi.DX11, true) is null && Installers.FeederStandsAside(RenderApi.DX12, false) is null);
    Check("the feeder stays selectable there", Installers.RoutesFor(RenderApi.DX12, 64, true).Single(o => o.Route == Route.Feeder).Unavailable is null);

    Check("routelabel", Installers.RouteLabel("native") == "ReShade · Native DLSS-NR"
        && Installers.RouteLabel("multipass") == "ReShade · RenoDX DLSS Tool (multipass)"
        && Installers.RouteLabel("renodx") == "ReShade · RenoDX DLSS Tool (multipass)"
        && Installers.RouteLabel("feeder") == "ReShade · DLSS5 Feeder"
        && Installers.RouteLabel("optiscaler") == "OptiScaler"
        && Installers.RouteLabel("swap") == "DLL swap only"
        && Installers.RouteLabel(null) == "none" && Installers.RouteLabel("") == "none"
        && Installers.RouteLabel("weird") == "weird");
    Check("routekey", Installers.RouteKey(Route.Feeder) == "feeder" && Installers.RouteKey(Route.OptiScaler) == "optiscaler");
    Check("apikey", Installers.ApiKey(RenderApi.DX12) == "dxgi" && Installers.ApiKey(RenderApi.DX11) == "dxgi" && Installers.ApiKey(RenderApi.Unknown) == "unknown" && Installers.ApiKey(RenderApi.OpenGL) == "opengl");
    Check("menukeys", Installers.MenuKeys.Length == 21 && Installers.MenuKeys[0] == ("Insert", "0x2D") && Installers.MenuKeys[17] == ("F12", "0x7B") && Installers.MenuKeys[^1] == ("Scroll Lock", "0x91"));
    Check("opti builds", Components.OptiBuilds.Length == 2 && Components.OptiBuilds[0].Id == "0.9.81-shyvortex" && Components.OptiBuilds.All(b => b.Url is not null && b.Sha256 is { Length: 64 }));
    Check("opti 0.9.81 whole backend", Components.OptiBuilds.Single(b => b.Id == "0.9.81-shyvortex") is { WholeBackend: true, NeuralRendering: true, Forwarded: false, Sha256.Length: 64 });

    // Feeder helpers
    Check("searchpaths merge", Installers.MergeSearchPaths(@".\reshade-shaders\Shaders\, D:\My\Shaders\**\**,./reshade-shaders/Shaders/**", @".\reshade-shaders\Shaders\**")
        == @".\reshade-shaders\Shaders\**,D:\My\Shaders\**", Installers.MergeSearchPaths(@".\reshade-shaders\Shaders\, D:\My\Shaders\**\**,./reshade-shaders/Shaders/**", @".\reshade-shaders\Shaders\**"));
    Check("mv provider", Installers.EnsureMvProvider("A=1,DLSS5_MV_PROVIDER=1, B") == "DLSS5_MV_PROVIDER=2,A=1,B");
    Check("techniques", Installers.MergeTechniques("Launchpad@Launchpad.fx,SMAA@SMAA.fx,DLSS5_Feed@Other.fx")
        == "vort_MotionEffects@vort_Motion.fx,DLSS5_Feed@DLSS5_Feed.fx,SMAA@SMAA.fx");
    var cfg = Installers.BuildFeedCfg("# mine\nMODE = 3\ncustom=7\n");
    Check("feed cfg", cfg.StartsWith("# mine\r\nmode=3\r\ncustom=7\r\nenabled=1\r\nhdr=-1\r\n") && cfg.EndsWith("async_home=1\r\n") && !cfg.Contains("mode=2"), Show(cfg));
}

// ====================================================================== ReShade setup extraction (synthetic)
{
    var dir = Path.Combine(Path.GetTempPath(), "dlss5cr-setup-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var setupName = "ReShade_Setup_Test_" + Guid.NewGuid().ToString("N")[..8] + "_Addon";
    var setup = Path.Combine(dir, setupName + ".exe");
    var payload = RandomNumberGenerator.GetBytes(5000);
    using (var ms = new MemoryStream())
    {
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var w = zip.CreateEntry("ReShade64.dll").Open()) w.Write(payload);
            using (var w = zip.CreateEntry("ReShade32.dll").Open()) w.Write(payload.Take(100).ToArray());
            using (var w = zip.CreateEntry("other.txt").Open()) w.Write(new byte[] { 1 });
        }
        File.WriteAllBytes(setup, RandomNumberGenerator.GetBytes(70000).Concat(ms.ToArray()).ToArray());
    }
    var got = Components.ExtractFromReShadeSetup(setup, "ReShade64.dll");
    Check("setup extract", got is not null && File.ReadAllBytes(got).SequenceEqual(payload), got);
    Check("setup extract skips non-ReShade", got is not null && !File.Exists(Path.Combine(Path.GetDirectoryName(got)!, "other.txt")));
    Check("setup missing entry", Components.ExtractFromReShadeSetup(setup, "ReShade99.dll") is null);
    Check("setup not a setup", Components.ExtractFromReShadeSetup(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "ReShade64.dll") is null);
    if (got is not null) Directory.Delete(Path.GetDirectoryName(got)!, true);
    Directory.Delete(dir, true);
}

// ---------------------------------------------------------------- other-app record for a game that was moved
{
    var tmp = Path.Combine(Path.GetTempPath(), "d5m-moved-" + Guid.NewGuid().ToString("N")[..8]);
    var records = Path.Combine(tmp, "records");
    var game = Path.Combine(tmp, "NewLibrary", "My Game");
    var bin = Path.Combine(game, "Bin");
    Directory.CreateDirectory(bin);
    Directory.CreateDirectory(records);
    File.WriteAllText(Path.Combine(bin, "Game.exe"), "exe");
    File.WriteAllText(Path.Combine(bin, "dxgi.dll"), "added");
    var old = @"C:\OldLibrary\My Game\Bin";
    File.WriteAllText(Path.Combine(records, "steam_1.json"), System.Text.Json.JsonSerializer.Serialize(new
    {
        ExecutablePath = old + @"\Game.exe", InstalledAtUtc = "2026-09-23T01:40:57Z", Mode = "dx12", Multipass = "1",
        Files = new[] { new { TargetPath = old + @"\dxgi.dll", BackupPath = "", WasExisting = false } },
    }));
    var other = Path.Combine(tmp, "NewLibrary", "Other Game");
    Directory.CreateDirectory(other);
    var saved = ForeignInstalls.OtherAppRecordsDir;
    ForeignInstalls.OtherAppRecordsDir = records;
    try
    {
        Check("moved game: record found", Scanner.OtherAppRecordRouteFor(game) == "multipass");
        Check("moved game: other game not matched", Scanner.OtherAppRecordRouteFor(other) is null);
        var found = ForeignInstalls.Find(game);
        Check("moved game: paths rebased", found.Count == 1 && found[0].Problem is null &&
              found[0].Delete.SequenceEqual(new[] { Path.Combine(bin, "dxgi.dll") }, StringComparer.OrdinalIgnoreCase));
        await ForeignInstalls.RemoveAllAsync(game, null, _ => { });
        Check("moved game: removed", !File.Exists(Path.Combine(bin, "dxgi.dll")) && File.Exists(Path.Combine(bin, "Game.exe"))
              && !File.Exists(Path.Combine(records, "steam_1.json")));
    }
    finally { ForeignInstalls.OtherAppRecordsDir = saved; Directory.Delete(tmp, true); }
}

// ---------------------------------------------------------------- ReShade that no app recorded: moved aside, never deleted
{
    var realReShade = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\DLSS5Master\payload\reshade\ReShade64.dll"));
    if (File.Exists(realReShade))
    {
        var tmp = Path.Combine(Path.GetTempPath(), "d5m-unrec-" + Guid.NewGuid().ToString("N")[..8]);
        var bin = Path.Combine(tmp, "Bin");
        Directory.CreateDirectory(Path.Combine(bin, "reshade-shaders", "Shaders"));
        File.WriteAllText(Path.Combine(bin, "Game.exe"), "exe");
        File.WriteAllText(Path.Combine(bin, "game-own.dll"), "keep");
        File.Copy(realReShade, Path.Combine(bin, "dxgi.dll"));
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "x");
        File.WriteAllText(Path.Combine(bin, "renodx-dlss.addon64"), "x");
        File.WriteAllText(Path.Combine(bin, "reshade-shaders", "Shaders", "a.fx"), "x");
        var items = ForeignInstalls.UnrecordedReShadeFiles(bin, "dxgi.dll");
        Check("unrecorded: lists ReShade files only", items.Count == 4 && !items.Any(i => i.EndsWith("game-own.dll")), string.Join(",", items.Select(Path.GetFileName)));
        var dest = await ForeignInstalls.MoveAsideUnrecordedReShadeAsync(tmp, Path.Combine(bin, "Game.exe"), "dxgi.dll", _ => { });
        Check("unrecorded: moved out of the game", !File.Exists(Path.Combine(bin, "dxgi.dll")) && !Directory.Exists(Path.Combine(bin, "reshade-shaders"))
              && File.Exists(Path.Combine(bin, "game-own.dll")) && File.Exists(Path.Combine(bin, "Game.exe")));
        Check("unrecorded: kept in backup", File.Exists(Path.Combine(dest, "Bin", "dxgi.dll")) && File.Exists(Path.Combine(dest, "Bin", "reshade-shaders", "Shaders", "a.fx"))
              && File.Exists(Path.Combine(dest, "README.txt")));
        File.WriteAllText(Path.Combine(bin, "dxgi.dll"), "not reshade");
        Check("unrecorded: non-ReShade dxgi ignored", ForeignInstalls.UnrecordedReShadeFiles(bin, "dxgi.dll").Count == 0);
        Directory.Delete(tmp, true);
    }
    else Check("unrecorded: payload ReShade64.dll present for test", false, realReShade);
}

// ---------------------------------------------------------------- community: game keys and GPU families
{
    Check("community key: steam id", Community.GameKey(new GameEntry { Launcher = "Steam", Id = "3751260", Name = "The Blood of Dawnwalker" }) == "steam:3751260");
    Check("community key: non-steam name", Community.GameKey(new GameEntry { Launcher = "Epic Games", Id = "abc", Name = "Gothic 1 Remake!" }) == "name:gothic1remake");
    Check("community key: steam without numeric id", Community.GameKey(new GameEntry { Launcher = "Steam", Id = "x1", Name = "Odd Game" }) == "name:oddgame");
    Check("community key: empty name", Community.GameKey(new GameEntry { Launcher = "Manual", Name = "***" }) == "name:unknown");
    Check("community key: long name capped", Community.GameKey(new GameEntry { Launcher = "Manual", Name = new string('a', 90) }).Length == "name:".Length + 60);
    Check("gpu family: RTX 4090", Community.GpuFamily("NVIDIA GeForce RTX 4090") == "RTX 40");
    Check("gpu family: RTX 5070 Ti", Community.GpuFamily("NVIDIA GeForce RTX 5070 Ti") == "RTX 50");
    Check("gpu family: RTX PRO 6000", Community.GpuFamily("NVIDIA RTX PRO 6000 Blackwell") == "RTX 60" || Community.GpuFamily("NVIDIA RTX PRO 6000 Blackwell") == "NVIDIA");
    Check("gpu family: GTX", Community.GpuFamily("NVIDIA GeForce GTX 1080 Ti") == "GTX");
    Check("gpu family: AMD", Community.GpuFamily("AMD Radeon RX 7900 XTX") == "AMD");
    Check("gpu family: unknown", Community.GpuFamily(null) == "Other");
    var id1 = Community.InstallId; var id2 = Community.InstallId;
    Check("install id: 32 hex, stable", id1.Length == 32 && id1.All(Uri.IsHexDigit) && id1 == id2);
}

// ---------------------------------------------------------------- community: the PC a report ran on, and every report on a game's page
{
    Check("cpu: AMD core count dropped", PcSpecs.CleanCpu("AMD Ryzen 7 7800X3D 8-Core Processor           ") == "AMD Ryzen 7 7800X3D", PcSpecs.CleanCpu("AMD Ryzen 7 7800X3D 8-Core Processor           "));
    Check("cpu: Intel trademarks and clock dropped", PcSpecs.CleanCpu("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz") == "Intel Core i7-8700K", PcSpecs.CleanCpu("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz"));
    Check("cpu: Intel 13th gen", PcSpecs.CleanCpu("13th Gen Intel(R) Core(TM) i7-13700K") == "13th Gen Intel Core i7-13700K");
    Check("cpu: Core Ultra", PcSpecs.CleanCpu("Intel(R) Core(TM) Ultra 9 285K") == "Intel Core Ultra 9 285K");
    Check("cpu: built-in Radeon dropped", PcSpecs.CleanCpu("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics") == "AMD Ryzen 7 7840HS", PcSpecs.CleanCpu("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics"));
    Check("cpu: with Radeon Graphics dropped", PcSpecs.CleanCpu("AMD Ryzen 5 5600G with Radeon Graphics") == "AMD Ryzen 5 5600G");
    Check("cpu: unknown", PcSpecs.CleanCpu(null) == "" && PcSpecs.CleanCpu("   ") == "");
    Check("cpu: capped at 64", PcSpecs.CleanCpu(new string('x', 90)).Length == 64);
    Check("nvidia driver: 581.57", Gpu.NvidiaDriver("32.0.15.8157") == "581.57", Gpu.NvidiaDriver("32.0.15.8157"));
    Check("nvidia driver: 536.23", Gpu.NvidiaDriver("31.0.15.3623") == "536.23", Gpu.NvidiaDriver("31.0.15.3623"));
    Check("nvidia driver: 456.71", Gpu.NvidiaDriver("27.21.14.5671") == "456.71", Gpu.NvidiaDriver("27.21.14.5671"));
    Check("nvidia driver: odd values kept", Gpu.NvidiaDriver("garbage") == "garbage" && Gpu.NvidiaDriver("1.2.3") == "1.2.3" && Gpu.NvidiaDriver("32.0.15.81570") == "32.0.15.81570");
    Check("windows: 11 with release", PcSpecs.WindowsLabel(26100, "24H2") == "Windows 11 24H2 (26100)", PcSpecs.WindowsLabel(26100, "24H2"));
    Check("windows: 10", PcSpecs.WindowsLabel(19045, "22H2") == "Windows 10 22H2 (19045)");
    Check("windows: odd release left out", PcSpecs.WindowsLabel(22631, "x y") == "Windows 11 (22631)" && PcSpecs.WindowsLabel(22631, null) == "Windows 11 (22631)");
    Check("windows: unknown", PcSpecs.WindowsLabel(0, "24H2") == "");
    Check("specs line: full", Community.SpecsLine("NVIDIA GeForce RTX 4070", "581.57", "AMD Ryzen 7 7800X3D", 32, "Windows 11 24H2 (26100)")
        == "NVIDIA GeForce RTX 4070 · driver 581.57 · AMD Ryzen 7 7800X3D · 32 GB RAM · Windows 11 24H2 (26100)");
    Check("specs line: unknowns left out", Community.SpecsLine("NVIDIA GeForce RTX 4070", "", "", 0, "") == "NVIDIA GeForce RTX 4070");
    Check("versions line: known add-ons only", Community.VersionsLine("0.9.8", new Dictionary<string, string> { ["renodx-dlss5"] = "10.8.0", ["mfgunlock"] = "1.7.0", ["made-up"] = "1.0", ["optiscaler"] = "0.9.81" })
        == "DLSS 5 Master 0.9.8 · RenoDX DLSS5 add-on 10.8.0 · MFG unlock (MFGAdaUnlock) 1.7.0 · OptiScaler 0.9.81",
        Community.VersionsLine("0.9.8", new Dictionary<string, string> { ["renodx-dlss5"] = "10.8.0", ["mfgunlock"] = "1.7.0", ["made-up"] = "1.0", ["optiscaler"] = "0.9.81" }));
    Check("versions line: text that is not a version is not shown", Community.VersionsLine("free text", new Dictionary<string, string> { ["renodx-dlss5"] = "<b>hello</b>" }) == "");

    // This PC's own values must pass the community server's checks (community/src/worker.js), or they would be stored empty.
    var cpu = PcSpecs.Cpu; var os = PcSpecs.Windows; var driver = Gpu.DriverFor(Community.LocalGpu);
    Check("this pc: processor fits the server", cpu.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(cpu, @"^[A-Za-z0-9 ()@.,+_/-]{1,64}$"), cpu);
    Check("this pc: Windows fits the server", System.Text.RegularExpressions.Regex.IsMatch(os, @"^Windows 1[01]( [0-9A-Za-z]{2,8})? \(\d{4,6}\)$"), os);
    Check("this pc: driver fits the server", driver.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(driver, @"^\d{1,5}(\.\d{1,6}){1,3}$"), driver);
    Check("this pc: memory", PcSpecs.RamGb is > 0 and <= 4096, PcSpecs.RamGb.ToString());
    Check("this pc: app version fits the server", System.Text.RegularExpressions.Regex.IsMatch(Updates.CurrentAppVersion.ToString(), @"^\d{1,3}(\.\d{1,5}){1,3}$"), Updates.CurrentAppVersion.ToString());

    var page = Community.ParseSummary("steam:1", """
        {"key":"steam:1","total":3,"counts":{"works":1,"partly":0,"broken":2},
         "byRoute":[{"route":"native","mfg":1,"gpu_family":"RTX 40","works":0,"partly":0,"broken":1}],
         "reports":[
           {"id":7,"rating":"broken","route":"native","mfg":true,"api":"dx12","gpu":"NVIDIA GeForce RTX 4070","gpu_family":"RTX 40","driver":"581.57",
            "cpu":"AMD Ryzen 7 7800X3D","ram_gb":32,"os":"Windows 11 24H2 (26100)","versions":{"renodx-dlss5":"10.8.0"},"app_version":"0.9.8",
            "note":"","hidden":false,"updated_at":1793404800000,"nickname":null,"is_dev":false},
           {"id":6,"rating":"broken","route":"feeder","mfg":false,"api":"","gpu":"","gpu_family":"RTX 30","driver":"","cpu":"","ram_gb":16,"os":"",
            "versions":{},"app_version":"0.9.5","note":"","hidden":true,"updated_at":1793304800000,"nickname":null,"is_dev":false}],
         "notes":[],"mine":{"id":6,"rating":"broken","route":"feeder","mfg":0,"note":"","hidden":1,"updated_at":1793304800000}}
        """);
    Check("page: every report", page.AllReports && page.Reports.Count == 2 && page.Reports[0].Id == 7 && page.Reports[1].Hidden);
    var first = page.Reports[0];
    Check("page: a report's PC", first.Cpu == "AMD Ryzen 7 7800X3D" && first.RamGb == 32 && first.Driver == "581.57" && first.Os == "Windows 11 24H2 (26100)"
        && first.Mfg && first.Api == "dx12" && first.Versions.GetValueOrDefault("renodx-dlss5") == "10.8.0", first.ToString());
    Check("page: own report", page.Mine is { Id: 6, Hidden: true });
    var older = Community.ParseSummary("steam:1", """
        {"key":"steam:1","total":1,"counts":{"works":0,"partly":0,"broken":1},
         "byRoute":[{"route":"native","mfg":1,"gpu_family":"RTX 40","works":0,"partly":0,"broken":1}],
         "notes":[{"id":3,"rating":"broken","route":"native","mfg":true,"gpu":"NVIDIA GeForce RTX 4070","gpu_family":"RTX 40","versions":{},
                   "app_version":"0.9.7","note":"Blocked by the anti-cheat","updated_at":1793404800000,"nickname":null,"is_dev":false}],"mine":null}
        """);
    Check("page: a server before 0.9.8 sends only notes", !older.AllReports && older.Reports.Count == 1 && older.Reports[0].Note == "Blocked by the anti-cheat"
        && older.Reports[0].Cpu == "" && older.Reports[0].RamGb == 0 && older.Combos[0].Mfg && older.Mine is null);
}

// ---------------------------------------------------------------- add-on updates the app now includes are dropped
{
    var mfg = Updates.Sources.First(s => s.Id == "mfgunlock");
    var feeder = Updates.Sources.First(s => s.Id == "feeder");
    var state = new UpdateState();
    state.Active["mfgunlock"] = new ActiveUpdate { Version = "old", Published = mfg.BundledDate.AddDays(-3) };   // older than built-in
    state.Active["feeder"] = new ActiveUpdate { Version = "same", Published = feeder.BundledDate };          // the built-in release
    state.Active["renodx-dlss"] = new ActiveUpdate { Version = "newer", Published = DateTime.UtcNow.AddYears(5) };
    var gone = Updates.Superseded(state);
    Check("superseded: older and same dropped, newer kept", gone.Count == 2 && gone.Contains("mfgunlock") && gone.Contains("feeder") && !gone.Contains("renodx-dlss"),
          string.Join(",", gone));
    Check("tests use their own data folder", AppPaths.Root == testData);

    // Downloaded app installers: this version's and older ones go, a newer one waiting to be installed stays.
    var updates = Path.Combine(AppPaths.Root, "app-update");
    Directory.CreateDirectory(updates);
    var older = new Version(Updates.CurrentAppVersion.Major, Updates.CurrentAppVersion.Minor, Math.Max(Updates.CurrentAppVersion.Build - 1, 0));
    var names = new[] { $"DLSS5Master-Setup-{older}.exe", $"DLSS5Master-Setup-{Updates.CurrentAppVersion}.exe", $"DLSS5Master-Setup-{older}.exe.part",
                        "DLSS5Master-Setup-99.0.0.exe", "DLSS5Master-Setup-notes.txt" };
    foreach (var n in names) File.WriteAllText(Path.Combine(updates, n), "x");
    Updates.DropInstalledAppUpdates();
    var left = Directory.GetFiles(updates).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Check("app installers of this version or older are deleted", !left.Contains(names[0]) && !left.Contains(names[1]) && !left.Contains(names[2]), string.Join(",", left));
    Check("a newer app installer and other files stay", left.Contains("DLSS5Master-Setup-99.0.0.exe") && left.Contains("DLSS5Master-Setup-notes.txt"), string.Join(",", left));
}

// ---------------------------------------------------------------- overlay folder: strays, held files, ReShade.ini
{
    var tmp = Path.Combine(Path.GetTempPath(), "d5m-overlay-" + Guid.NewGuid().ToString("N")[..8]);
    var folder = Path.Combine(tmp, "overlay");
    var outside = Path.Combine(tmp, "elsewhere");
    Directory.CreateDirectory(Path.Combine(folder, "sub"));
    Directory.CreateDirectory(outside);
    string In(string rel) => Path.Combine(folder, rel);
    var own = new[] { "dlss5-overlay.exe", "dxgi.dll", "renodx-dlss5.addon64", @"sub\own.dll" };
    var strays = new[] { "d3d11.dll", "other.addon64", "loader.asi", "tool.exe", @"sub\planted.dll" };
    var data = new[] { "ReShade.ini", "dlss5-overlay.log", @"sub\notes.txt" };
    foreach (var f in own.Concat(strays).Concat(data)) File.WriteAllText(In(f), f);
    File.WriteAllText(Path.Combine(outside, "outside.dll"), "x");
    // A link inside the folder to a folder elsewhere (a junction needs no administrator rights).
    using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{In("link")}\" \"{outside}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
        mk!.WaitForExit();
    var removed = Overlay.RemoveStrays(folder, own.Select(In));
    Check("overlay strays: other program files removed", strays.All(s => !File.Exists(In(s))) && removed.Count == strays.Length + 1, string.Join(",", removed));
    Check("overlay strays: own files and data stay", own.Concat(data).All(f => File.Exists(In(f))));
    Check("overlay strays: link removed, what it points to kept", !Directory.Exists(In("link")) && File.Exists(Path.Combine(outside, "outside.dll")));
    File.WriteAllText(In("locked.dll"), "x");
    using (new FileStream(In("locked.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        Throws<InvalidOperationException>("overlay strays: one that cannot be removed stops the start", () => Overlay.RemoveStrays(folder, own.Select(In)));
    File.Delete(In("locked.dll"));

    // Held files: nothing can change, replace or delete them; checked against the source when asked to.
    var src = Path.Combine(tmp, "src");
    Directory.CreateDirectory(src);
    File.WriteAllText(Path.Combine(src, "a.dll"), "alpha");
    File.WriteAllText(Path.Combine(src, "b.dll"), "bravo");
    File.WriteAllText(Path.Combine(src, "c.ini"), "charlie");
    File.WriteAllText(In("a.dll"), "alpha");
    File.WriteAllText(In("b.dll"), "bravX");   // swapped
    File.WriteAllText(In("c.ini"), "charliX");
    OverlayFile From(string name, bool hold = true) => new(In(name), () => File.OpenRead(Path.Combine(src, name)), hold);
    var held = Overlay.HoldFiles(new[] { From("a.dll"), From("b.dll") }, verify: false);
    bool Blocked(Action a) { try { a(); return false; } catch (IOException) { return true; } }
    Check("overlay held: cannot be written", Blocked(() => File.WriteAllText(In("a.dll"), "evil")));
    Check("overlay held: cannot be deleted or replaced", Blocked(() => File.Delete(In("a.dll"))) && Blocked(() => File.Move(In("a.dll"), In("moved.dll"))));
    Check("overlay held: still readable", File.ReadAllText(In("a.dll")) == "alpha");
    Check("overlay held, not checked: content as found", File.ReadAllText(In("b.dll")) == "bravX");
    foreach (var h in held) h.Dispose();
    Check("overlay released: writable again", !Blocked(() => File.WriteAllText(In("a.dll"), "alpha")));
    held = Overlay.HoldFiles(new[] { From("a.dll"), From("b.dll"), From("c.ini", hold: false) }, verify: true);
    Check("overlay checked: a swapped file is copied again", File.ReadAllText(In("b.dll")) == "bravo" && File.ReadAllText(In("a.dll")) == "alpha");
    Check("overlay checked: settings file copied again but not held", File.ReadAllText(In("c.ini")) == "charlie" && held.Count == 2 &&
          !Blocked(() => File.WriteAllText(In("c.ini"), "charlie")));
    Check("overlay checked: held afterwards", Blocked(() => File.WriteAllText(In("b.dll"), "evil")));
    foreach (var h in held) h.Dispose();
    File.Delete(Path.Combine(src, "b.dll"));
    Throws<FileNotFoundException>("overlay checked: a missing source stops the start", () => Overlay.HoldFiles(new[] { From("a.dll"), From("b.dll") }, verify: true));
    Check("overlay checked: nothing stays held after a failure", !Blocked(() => File.WriteAllText(In("a.dll"), "alpha")));

    // ReShade.ini: add-ons from the overlay folder only, no program after a screenshot, the neural pass settings.
    var planted = "[ADDON]\nAddonPath=C:\\Elsewhere\n\n[SCREENSHOT]\nPostSaveCommand=C:\\Elsewhere\\run.exe\n";
    var ini = Overlay.SettingsText(planted, new AppSettings { OverlayStyle = 1, OverlayStrength = 80, OverlayPasses = 2 }, folder);
    Check("overlay ini: add-ons from its folder only", Ini.Get(ini, "ADDON", "AddonPath") == folder, Show(ini));
    Check("overlay ini: no program after a screenshot", Ini.Get(ini, "SCREENSHOT", "PostSaveCommand") == "", Show(ini));
    Check("overlay ini: neural pass settings", Ini.Get(ini, "RenoDX.DLSS5", "NRStyle") == "1" && Ini.Get(ini, "RenoDX.DLSS5", "NRIntensity") == "0.8" &&
          Ini.Get(ini, "RenoDX.DLSS5", "NRPasses") == "2" && Ini.Get(ini, "RenoDX.DLSS5", "EnableHooks") == "2");
    Directory.Delete(tmp, true);

    // Exclusive fullscreen in the helper's log: now, earlier (until the window is drawn again), or not at all.
    Directory.CreateDirectory(Overlay.Folder);
    const string drawn = "10:00:05.000  DLSS 5 on, 60 captured fps, 60 shown fps, 1280x720, GPU 4.0 ms a frame\n";
    const string on = "10:00:06.000  exclusive fullscreen: the window holds the screen for itself, which hides every overlay\n";
    const string off = "10:00:09.000  exclusive fullscreen ended: drawing again\n";
    File.WriteAllText(Overlay.LogFile, drawn);
    Check("overlay fullscreen: none", Overlay.ExclusiveFullscreen() == Overlay.FullscreenState.None);
    File.WriteAllText(Overlay.LogFile, drawn + on);
    Check("overlay fullscreen: now", Overlay.ExclusiveFullscreen() == Overlay.FullscreenState.Now);
    File.WriteAllText(Overlay.LogFile, drawn + on + off);
    Check("overlay fullscreen: was, kept after switching away", Overlay.ExclusiveFullscreen() == Overlay.FullscreenState.Was);
    File.WriteAllText(Overlay.LogFile, drawn + on + off + drawn);
    Check("overlay fullscreen: drawn again clears it", Overlay.ExclusiveFullscreen() == Overlay.FullscreenState.None);
    // Alone in the Dark, 30 Oct: in and out of exclusive fullscreen, readings without frames in between.
    const string empty = "09:38:14.951  DLSS 5 on, 0 captured fps, 1 shown fps, 2572x1446, GPU 3.3 ms a frame + 0.5 ms per in-between frame\n";
    File.WriteAllText(Overlay.LogFile, on + off + on + off + empty + on + off);
    Check("overlay fullscreen: readings without frames do not clear it", Overlay.ExclusiveFullscreen() == Overlay.FullscreenState.Was);
}

try { Directory.Delete(testData, true); } catch { }
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
