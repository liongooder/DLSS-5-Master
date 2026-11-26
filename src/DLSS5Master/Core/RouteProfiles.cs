using System.Text.Json;

namespace DLSS5Master.Core;

/// <summary>Local settings snapshots, scoped to game, executable, route, architecture and OptiScaler build.</summary>
internal static class RouteProfiles
{
    private sealed record Profile(int Bitness, List<KeptSetting> Settings);
    private static string FileFor(string game, string exe, string route, string? build) => Path.Combine(AppPaths.Root,
        "route-profiles", InstallTransaction.Key(game), InstallTransaction.Key(exe) + "-" + route + "-" +
        InstallTransaction.Key(Path.Combine(game, build ?? "default")) + ".json");
    internal static bool Allowed(string route) => route is "native" or "multipass" or "feeder" or "nraddon" or "optiscaler" or "mfgonly" or "uplift";
    internal static void Save(string game, Manifest m, Action<string> log)
    {
        if (!Allowed(m.Route) || !Enum.TryParse<Route>(m.Route, true, out var route)) return;
        var exe = Journal.SafePath(game, m.Exe);
        var settings = Installers.KeepSettings(game, m, Path.GetDirectoryName(exe)!, route, m.Bitness,
            m.MfgUnlock is not null, m.OptiScalerBuild, _ => { });
        if (settings.Sum(s => (long)s.Bytes.Length) > 8 * 1024 * 1024) throw new InstallException("Route settings exceed the 8 MB profile limit; nothing was changed.");
        foreach (var s in settings) InstallTransaction.CheckPath(game, Journal.SafePath(game, s.Rel));
        if (m.Route == "feeder" && m.OptiScalerBuild is not null)
        {
            var optiSettings = settings.Where(IsOptiSetting).ToList();
            if (optiSettings.Count > 0)
                Write(FileFor(game, exe, m.Route, m.OptiScalerBuild), m.Bitness, optiSettings);
            settings.RemoveAll(IsOptiSetting);
        }
        Write(FileFor(game, exe, m.Route, m.Route == "optiscaler" ? m.OptiScalerBuild : null), m.Bitness, settings);
        log("Saved this route's in-game settings locally.");
    }

    private static bool IsOptiSetting(KeptSetting s) =>
        Path.GetFileName(s.Rel).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase);

    private static void Write(string file, int bits, List<KeptSetting> settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new Profile(bits, settings)));
        File.Move(file + ".tmp", file, true);
    }

    internal static List<KeptSetting> LoadFeederOptiSettings(string game, string exe, int bits, string build) =>
        LoadFile(FileFor(game, exe, "feeder", build), game, exe, bits).Where(IsOptiSetting).ToList();

    internal static List<KeptSetting> Load(string game, string exe, Route route, int bits, bool mfg, string? build)
    {
        var key = Installers.RouteKey(route);
        if (!Allowed(key)) return new();
        var file = FileFor(game, exe, key, route == Route.OptiScaler ? build : null);
        if (route == Route.NrAddon && !File.Exists(file))
            file = FileFor(game, exe, "lecramnr", null);
        if (route == Route.OptiScaler && build == Components.BundledOptiBuildId && !File.Exists(file))
        {
            // Older app versions keyed compatible settings by their original build IDs.
            foreach (var previous in Components.PreviousOptiBuildIds)
            {
                var candidate = FileFor(game, exe, key, previous);
                if (File.Exists(candidate)) { file = candidate; break; }
            }
        }
        var settings = LoadFile(file, game, exe, bits);
        if (route == Route.Feeder && build is not null)
            settings.AddRange(LoadFeederOptiSettings(game, exe, bits, build));
        return settings.Where(s => mfg || !Path.GetFileName(s.Rel).StartsWith("MFGAdaUnlock", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<KeptSetting> LoadFile(string file, string game, string exe, int bits)
    {
        if (!File.Exists(file)) return new();
        if (new FileInfo(file).Length > 12 * 1024 * 1024) throw new InstallException("Saved route settings are too large.");
        var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(file));
        if (p is null || p.Bitness != bits) return new();
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        // Only settings beside this executable (or its known helper) can be restored. Never accept DLLs from a profile.
        foreach (var s in p.Settings)
        {
            var path = Journal.SafePath(game, s.Rel);
            InstallTransaction.CheckPath(game, path);
            var parent = Path.GetDirectoryName(path)!;
            if (!(parent.Equals(dir, StringComparison.OrdinalIgnoreCase) || parent.Equals(Path.Combine(dir, "host64"), StringComparison.OrdinalIgnoreCase))
                || Path.GetExtension(path).ToLowerInvariant() is not (".ini" or ".cfg" or ".json" or ".txt"))
                throw new InstallException("Saved route settings contain an unsupported path.");
        }
        return p.Settings;
    }
}
