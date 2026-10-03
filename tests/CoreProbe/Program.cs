using DLSS5Master.Core;
using System.Diagnostics;

var settings = new AppSettings();
var sw = Stopwatch.StartNew();
var games = Library.Discover(settings);
Console.WriteLine($"Discovered {games.Count} games in {sw.ElapsedMilliseconds} ms");
foreach (var g in games)
{
    sw.Restart();
    try
    {
        var s = Scanner.Scan(g.Dir, g.Exe);
        var p = s.Primary;
        var dlss = s.Dlss?.Version ?? "-";
        var routes = p is null ? "" : string.Join(",", Installers.RoutesFor(p.Api, p.Bitness, s.HasNativeDlss).Where(r => r.Unavailable is null).Select(r => r.Route));
        Console.WriteLine($"[{g.Launcher,-7}] {g.Name,-40} {(p is null ? "NO EXE" : $"{p.Rel} | {Scanner.Label(p.Api)} | {p.Bitness}")} | DLSS {dlss} | RS {(s.ReShade.Installed ? s.ReShade.Version + (s.ReShade.AddonSupport ? "+addon" : "") : "-")} | Opti {s.OptiScalerHook ?? (s.OptiScalerIni ? "ini" : "-")} | other {s.OtherAppBackupRoute ?? (s.OtherAppBackup ? "yes" : "-")} | AC {s.AntiCheat} | routes {routes} | poster {(g.Poster is null ? "no" : "yes")} | {sw.ElapsedMilliseconds}ms");
    }
    catch (Exception e) { Console.WriteLine($"{g.Name}: ERROR {e.Message}"); }
}
var comps = new Components(settings);
Console.WriteLine($"Runtime dir: {comps.RuntimeDir}");
Console.WriteLine($"ReShade64: {comps.ReShadeDll(64)}  addon={Scanner.HasAddonSupport(comps.ReShadeDll(64)!)}");
var t = "; c\r\n[Menu]\r\nShortcutKey = auto\r\n\r\n[X]\r\na=1\r\n";
Console.WriteLine(Ini.Set(Ini.Set(t, "Menu", "ShortcutKey", "0x24"), "New", "k", "v"));
