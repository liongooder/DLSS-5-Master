using System.Reflection;
using System.Text;
using DLSS5Master.Core;

static class ReinstallSettingsRaceTests
{
    public static void Run(Action<string, bool, string?> check)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nm-settings-preflight-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var restore = typeof(Installers).GetMethod("RestoreForReinstall", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            foreach (var deleted in new[] { false, true })
            {
                var game = Path.Combine(root, deleted ? "deleted" : "edited");
                Directory.CreateDirectory(game);
                var ini = Path.Combine(game, "ReShade.ini");
                var kept = new[] { new KeptSetting("ReShade.ini", Encoding.UTF8.GetBytes("[Player]\nChoice=A\n")) };
                var latest = "[Player]\nChoice=B\n";
                if (!deleted) File.WriteAllText(ini, latest);
                var manifest = Journal.ManifestPath(game);
                Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                File.WriteAllText(manifest, "invalid test record: preflight must refuse before reading this");
                var provider = Path.Combine(game, "nvngx_dlssg.dll");
                File.WriteAllText(provider, "owned CPU provider sentinel");
                var refused = false;
                try { restore.Invoke(null, new object[] { game, kept, (Action<string>)(_ => { }), (Action<string>)(_ => { }) }); }
                catch (TargetInvocationException e) when (e.InnerException is InstallException) { refused = true; }
                catch (TargetInvocationException) { }
                check($"reinstall preflight: {(deleted ? "deleted" : "edited")} settings refuse before restore", refused, null);
                check("reinstall preflight: current user state preserved", deleted ? !File.Exists(ini) : File.ReadAllText(ini) == latest, null);
                check("reinstall preflight: provider preserved", File.ReadAllText(provider) == "owned CPU provider sentinel", null);
                check("reinstall preflight: journal not opened or rewritten", File.ReadAllText(manifest).StartsWith("invalid test record"), null);
            }
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("nm-settings-preflight-"))
                throw new InvalidOperationException("Refusing cleanup outside owned temporary fixture.");
            Directory.Delete(root, true);
        }
    }
}
