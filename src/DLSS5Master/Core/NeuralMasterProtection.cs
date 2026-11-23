namespace DLSS5Master.Core;

/// <summary>Keep ordinary game operations away from a separate NeuralMaster installation.</summary>
internal static class NeuralMasterProtection
{
    private const string DevelopmentSetup = "This game has a NeuralMaster development install. Use the NeuralMaster bar on its game page.";

    internal static string? Conflict(GameScan scan) => Conflict(scan.Dir, scan.Primary?.Path, scan.Manifest);

    internal static string? Conflict(string gameDir, string? exePath, Manifest? manifest)
    {
        if (NeuralMasterSnapshots.HasPendingOperation(gameDir))
            return "A NeuralMaster change needs recovery. Open the affected game's NeuralMaster bar.";
        if (manifest?.Route.Equals("neuralmaster", StringComparison.OrdinalIgnoreCase) == true)
            return DevelopmentSetup;
        if (string.IsNullOrWhiteSpace(exePath) && manifest?.Exe is { Length: > 0 } relativeExe)
            exePath = Journal.SafePath(gameDir, relativeExe);
        exePath ??= Path.Combine(gameDir, "game.exe");
        return NeuralMasterSnapshots.FindLoadableAddon(gameDir, exePath) is { } addon ? AddonConflict(addon) : null;
    }

    internal static void Check(string gameDir, string? exePath)
    {
        if (Conflict(gameDir, exePath, Journal.ReadManifest(gameDir)) is { } message)
            throw new InstallException(message);
    }

    private static string AddonConflict(string addon) =>
        $"NeuralMaster is active in this game ({addon}). Use the NeuralMaster bar on its game page.";
}
