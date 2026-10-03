using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DLSS5Master.Core;

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

    // corrupt manifest
    File.WriteAllText(Path.Combine(Journal.BackupRoot(game), "manifest.json"), "{ not json");
    Throws<InstallException>("corrupt manifest", () => Journal.ReadManifest(game));

    File.SetAttributes(ro, FileAttributes.Normal);
    Directory.Delete(game, true);
    Directory.Delete(src, true);
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
    Check("recommend feeder", Installers.RecommendedRoute(RenderApi.DX12, 64, false) == Route.Feeder);
    Check("recommend first avail", Installers.RecommendedRoute(RenderApi.Vulkan, 64, false) == Route.OptiScaler);
    Check("recommend fallback", Installers.RecommendedRoute(RenderApi.OpenGL, 64, false) == Route.Native);

    Check("mfg ok", Installers.MfgUnavailable(RenderApi.DX12, 64, true) is null);
    Check("mfg 32", Installers.MfgUnavailable(RenderApi.DX12, 32, true) is not null);
    Check("mfg vulkan", Installers.MfgUnavailable(RenderApi.Vulkan, 64, true) is not null);
    Check("mfg no fg", Installers.MfgUnavailable(RenderApi.DX12, 64, false)?.Contains("nvngx_dlssg.dll") == true);

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
    Check("opti builds", Components.OptiBuilds.Length == 6 && Components.OptiBuilds[0].Id == "0.8.3-rtx40-mfg" && Components.OptiBuilds[^1].Url is null);

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

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
