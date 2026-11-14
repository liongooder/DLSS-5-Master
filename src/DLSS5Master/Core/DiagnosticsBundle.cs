using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSS5Master.Core;

public static class DiagnosticsBundle
{
    public static string Redact(string text)
    {
        // Export is an allowlist. The log additionally removes paths, addresses and identity/credential lines.
        var safe = string.Join("\n", text.Split('\n').Where(l => !Regex.IsMatch(l, @"(?i)(install[-_ ]?id|moderator|authorization|bearer |password|secret|token|api[-_ ]?key)")));
        safe = Regex.Replace(safe, @"(?i)[a-z]:[\\/][^\r\n""<>|]*", "[local path]");
        safe = Regex.Replace(safe, @"\\\\[^\s\r\n]+", "[network path]");
        safe = Regex.Replace(safe, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[email]");
        var user = Environment.UserName;
        if (user.Length > 1) safe = Regex.Replace(safe, Regex.Escape(user), "[user]", RegexOptions.IgnoreCase);
        return safe;
    }
    public static Dictionary<string, string> Preview(IEnumerable<GameEntry> games, Components components)
    {
        string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        var files = new Dictionary<string, string>
        {
            ["readme.txt"] = "Local support bundle. No DLLs, private add-ons, full settings, community identity or credentials are included. Review every entry before exporting. Nothing is uploaded.",
            ["system.json"] = Json(new { App = Updates.CurrentAppVersion, Framework = Environment.Version.ToString(),
                GPU = Community.LocalGpu, Driver = Gpu.DriverFor(Community.LocalGpu), Windows = PcSpecs.Windows, RAM = PcSpecs.RamGb }),
            ["components.json"] = Json(components.RuntimeFiles().OrderBy(k => k.Key).Select(k => new { File = k.Key, Version = Pe.GetFileVersion(k.Value) }))
        };
        int index = 0;
        foreach (var game in games)
        {
            try
            {
                var m = Journal.ReadManifest(game.Dir);
                if (m is null || !RouteProfiles.Allowed(m.Route)) continue;
                files[$"games/game-{++index}.json"] = Json(new { Game = Redact(game.Name), m.Route, m.Api, m.Bitness, m.Date,
                    m.OptiScalerBuild, m.MfgUnlock, m.ReShadeHook,
                    Files = m.Added.Concat(m.Replaced.Select(r => r.Rel)).Select(Redact).ToArray(),
                    Replaced = m.Replaced.Select(r => new { File = Redact(r.Rel), r.OldVersion, r.NewVersion, r.Kind }),
                    RecoveryPending = InstallTransaction.Pending(game.Dir) });
            }
            catch (Exception e) { files[$"games/error-{++index}.txt"] = Redact(e.Message); }
        }
        if (File.Exists(AppPaths.LogFile))
        {
            using var stream = new FileStream(AppPaths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 200_000), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            if (stream.Position > 0) reader.ReadLine();
            files["activity.log"] = Redact(reader.ReadToEnd());
        }
        return files;
    }
    public static void Export(string path, IReadOnlyDictionary<string, string> preview)
    {
        using var output = new FileStream(path, FileMode.CreateNew);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var (name, text) in preview)
        {
            if (Path.IsPathRooted(name) || name.Split('/', '\\').Any(p => p is ".." or "")) throw new InstallException("Invalid diagnostic entry.");
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
    }
}
