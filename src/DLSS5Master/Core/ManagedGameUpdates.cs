namespace DLSS5Master.Core;

public static class ManagedGameUpdates
{
    public static BulkRow Plan(GameEntry entry, GameOverride? choices, BulkContext context)
    {
        var m = Journal.ReadManifest(entry.Dir);
        var game = new BulkGame { Entry = entry };
        BulkRow Skip(string why) => new() { Game = game, Skip = why };
        if (m is null) return Skip("No DLSS 5 Master installation.");
        if (!RouteProfiles.Allowed(m.Route) || !Enum.TryParse<Route>(m.Route, true, out var route))
            return Skip("This development or unsupported route is excluded from managed updates.");
        if (InstallTransaction.Pending(entry.Dir)) return Skip("An interrupted installation needs recovery on its game page first.");
        var exe = Journal.SafePath(entry.Dir, m.Exe);
        Installers.AssertGameClosed(exe);
        var scan = Scanner.Scan(entry.Dir, exe);
        var build = route == Route.OptiScaler ? Components.AllOptiBuilds.FirstOrDefault(b => b.Id == m.OptiScalerBuild) : Components.OptiBuilds[0];
        if (build is null) return Skip("The recorded OptiScaler build is no longer available; choose a build on the game page.");
        // Keep the recorded API when dxgi alone cannot distinguish DirectX 11 from 12.
        var api = m.RenderingApi ?? choices?.Api ?? (m.Api == "dxgi" ? null : m.Api switch { "d3d9" => "DX9", "d3d10" => "DX10", "vulkan" => "Vulkan", "opengl" => "OpenGL", _ => null });
        var effective = new GameOverride { Exe = exe, Api = api, LoadAddonsEarly = choices?.LoadAddonsEarly };
        game = BulkSetup.Read(entry, scan, effective);
        return BulkSetup.Plan(game, new BulkOptions(route, build, m.MfgUnlock is not null, true), context);
    }
}
