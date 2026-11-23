using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DLSS5Master.Core;

internal static class NeuralMasterFixtures
{
    private const string Addon = "NeuralMaster-experimental-in-call.addon64";
    private const string AlternateAddon = "dlss5-nr.addon64";
    private const string AddonBytes = "NeuralMaster fixture build A";
    private const string HostBytes = "ReShade fixture host A";
    private const string AlternateBytes = "Alternate add-on fixture build B";
    private const string AlternateHostBytes = "ReShade fixture host B";
    private const string Inis = "[GENERAL]\nEffectSearchPaths=.\\Shaders\n[NeuralMaster]\nEnable=1\n[NeuralMaster.Profiles]\nPreset=fixture\n[FG]\nPreset=keep\n";
    private const int InterruptedExitCode = 74;

    internal static async Task Run(string root, Action<string, bool> check)
    {
        var svc = Service();
        AssertGameStateVisibility(root, svc, check);
        var single = Create(root, "nm-exe-only", "bin", "bin");
        var installed = svc.Inspect(single.Dir, single.Exe);
        check("NeuralMaster identifies the verified executable-directory-only build",
            installed is { State: NeuralMasterSnapshotState.Installed, Build: "Fixture A", CanUninstall: true }
            && HasPath(installed.UninstallPaths, "bin/" + Addon));
        svc.Uninstall(single.Dir, single.Exe, installed.Fingerprint);
        var singleMissing = svc.Inspect(single.Dir, single.Exe);
        check("NeuralMaster snapshots a single executable-directory load location outside the game",
            singleMissing is { State: NeuralMasterSnapshotState.Uninstalled, CanRestore: true, BackupDate: not null, BackupLocation: not null }
            && !Under(singleMissing.BackupLocation, single.Dir)
            && BackupContains(singleMissing.BackupLocation, AddonBytes)
            && BackupContains(singleMissing.BackupLocation, HostBytes)
            && BackupContains(singleMissing.BackupLocation, Inis)
            && BackupContains(singleMissing.BackupLocation, "private sidecar fixture")
            && !File.Exists(single.At("bin/" + Addon)));

        var mirrored = Create(root, "nm-root-and-storage", "", "", "_storage_");
        var beforeMirrored = Capture(mirrored);
        svc.Uninstall(mirrored.Dir, mirrored.Exe);
        var mirroredMissing = svc.Inspect(mirrored.Dir, mirrored.Exe);
        check("NeuralMaster uninstalls both mirrored add-ons and preserves all other game files",
            mirroredMissing is { State: NeuralMasterSnapshotState.Uninstalled, CanRestore: true }
            && HasPath(mirroredMissing.RestorePaths, Addon)
            && HasPath(mirroredMissing.RestorePaths, "_storage_/" + Addon)
            && SameFiles(beforeMirrored.Where(kv => !kv.Key.EndsWith(Addon, StringComparison.OrdinalIgnoreCase)), Capture(mirrored)));
        check("NeuralMaster keeps independent snapshots for different games",
            !string.Equals(mirroredMissing.BackupLocation, singleMissing.BackupLocation, StringComparison.OrdinalIgnoreCase)
            && !File.Exists(single.At("bin/" + Addon)));

        // A missing host is recoverable from the snapshot. The host in the other load location is already the right one.
        File.Delete(mirrored.At("_storage_/dxgi.dll"));
        var currentHostTimestamp = File.GetLastWriteTimeUtc(mirrored.At("dxgi.dll"));
        svc.Restore(mirrored.Dir, mirrored.Exe);
        check("NeuralMaster restores both load locations and leaves the matching host in place",
            SameFiles(beforeMirrored, Capture(mirrored))
            && File.GetLastWriteTimeUtc(mirrored.At("dxgi.dll")) == currentHostTimestamp
            && !File.Exists(single.At("bin/" + Addon)));
        svc.Restore(single.Dir, single.Exe);
        check("NeuralMaster restores the other game's independent snapshot",
            svc.Inspect(single.Dir, single.Exe).State == NeuralMasterSnapshotState.Installed
            && File.ReadAllText(single.At("bin/" + Addon)) == AddonBytes);

        var unknown = Create(root, "nm-unknown", "", "", "_storage_");
        unknown.Put("_storage_/" + Addon, "a different build in one load location");
        var unknownBefore = Capture(unknown);
        var unknownStatus = svc.Inspect(unknown.Dir, unknown.Exe);
        Reject(() => svc.Uninstall(unknown.Dir, unknown.Exe));
        check("NeuralMaster does not trust a filename when the installed pair has an unknown build",
            unknownStatus is { State: NeuralMasterSnapshotState.NeedsAttention, CanUninstall: false }
            && HasPath(unknownStatus.ConflictPaths, "_storage_/" + Addon) && SameFiles(unknownBefore, Capture(unknown)));

        AssertExplicitImport(root, check);
        AssertAutomaticReleaseRecognition(root, check);

        var configured = Create(root, "nm-configured", "bin", "mods/custom");
        configured.Put("bin/ReShade.ini", Inis + "[ADDON]\nAddonPath=../mods/custom\n");
        var configuredBefore = Capture(configured);
        var configuredStatus = svc.Inspect(configured.Dir, configured.Exe);
        check("NeuralMaster discovers the configured add-on directory",
            configuredStatus.CanUninstall && HasPath(configuredStatus.UninstallPaths, "mods/custom/" + Addon));
        check("the NeuralMaster cover badge detects an active add-on in the configured load directory",
            Scanner.Scan(configured.Dir, configured.Exe).NeuralMasterAddon is { } configuredAddon
            && HasPath([configuredAddon], "mods/custom/" + Addon));
        svc.Uninstall(configured.Dir, configured.Exe, configuredStatus.Fingerprint);
        check("NeuralMaster removes the configured-directory copy", !File.Exists(configured.At("mods/custom/" + Addon)));
        svc.Restore(configured.Dir, configured.Exe);
        check("NeuralMaster restores the configured load location with its original settings",
            SameFiles(configuredBefore, Capture(configured)));

        var conflictGame = Create(root, "nm-conflict", "", "", "_storage_");
        svc.Uninstall(conflictGame.Dir, conflictGame.Exe);
        foreach (var lane in conflictGame.Lanes)
        {
            conflictGame.Put(Rel(lane, AlternateAddon), AlternateBytes);
            conflictGame.Put(Rel(lane, "dxgi.dll"), AlternateHostBytes);
        }
        conflictGame.Put("ReShade.ini", Inis.Replace("Preset=keep", "Preset=changed") + "[OtherAddon]\nUseHDR=1\n");
        conflictGame.Put("_storage_/ReShade.ini", Inis.Replace("Enable=1", "Enable=0") + "[APP]\nVersion=custom\n");
        var alternateBefore = Capture(conflictGame);
        var conflict = svc.Inspect(conflictGame.Dir, conflictGame.Exe);
        check("NeuralMaster previews conflicts in both load locations before consent",
            conflict is { State: NeuralMasterSnapshotState.NeedsAttention, CanRestore: false, CanSwitch: true }
            && HasPath(conflict.ConflictPaths, AlternateAddon)
            && HasPath(conflict.ConflictPaths, "_storage_/" + AlternateAddon)
            && HasPath(conflict.ConflictPaths, "dxgi.dll") && HasPath(conflict.ConflictPaths, "_storage_/dxgi.dll"));
        Reject(() => svc.Restore(conflictGame.Dir, conflictGame.Exe));
        check("NeuralMaster cancellation and ordinary restore leave alternate files and settings untouched",
            SameFiles(alternateBefore, Capture(conflictGame)));
        var modifiedAlternate = conflictGame.Put("_storage_/dxgi.dll", "unknown/mixed alternate host");
        var mixed = svc.Inspect(conflictGame.Dir, conflictGame.Exe);
        Reject(() => svc.Restore(conflictGame.Dir, conflictGame.Exe, switchAlternate: true, expectedFingerprint: conflict.Fingerprint));
        check("NeuralMaster refuses a mixed or changed host pair even with switch requested",
            mixed is { State: NeuralMasterSnapshotState.NeedsAttention, CanRestore: false, CanSwitch: false }
            && HasPath(mixed.ConflictPaths, "_storage_/dxgi.dll") && File.ReadAllText(modifiedAlternate) == "unknown/mixed alternate host");
        conflictGame.Put("_storage_/dxgi.dll", AlternateHostBytes);
        conflict = svc.Inspect(conflictGame.Dir, conflictGame.Exe);
        svc.Restore(conflictGame.Dir, conflictGame.Exe, switchAlternate: true, expectedFingerprint: conflict.Fingerprint);
        var alternateBackups = Directory.EnumerateDirectories(Path.GetDirectoryName(conflict.BackupLocation!)!, "alternate-*").ToArray();
        var switchedFiles = Capture(conflictGame);
        check("NeuralMaster explicit switch backs up the alternate and reinstates the complete NeuralMaster pair",
            alternateBackups.Length == 1 && BackupMatches(alternateBackups[0], AlternateBytes) == 2
            && BackupMatches(alternateBackups[0], AlternateHostBytes) == 2
            && conflictGame.Lanes.All(lane =>
               !File.Exists(conflictGame.At(Rel(lane, AlternateAddon)))
               && File.ReadAllText(conflictGame.At(Rel(lane, Addon))) == AddonBytes
               && File.ReadAllText(conflictGame.At(Rel(lane, "dxgi.dll"))) == HostBytes));
        check("NeuralMaster switch preserves both edited ReShade INIs, the MFG record, and unrelated files",
            alternateBefore.Where(kv => !kv.Key.EndsWith(AlternateAddon, StringComparison.OrdinalIgnoreCase)
                && !kv.Key.EndsWith("dxgi.dll", StringComparison.OrdinalIgnoreCase))
                .All(kv => switchedFiles.TryGetValue(kv.Key, out var actual) && actual == kv.Value));

        var thirdPartyGame = Create(root, "nm-third-party-conflict", "", "", "_storage_");
        svc.Uninstall(thirdPartyGame.Dir, thirdPartyGame.Exe);
        foreach (var filename in new[] { "gitc-uplift.addon64", "deep-fried-chicken.addon64", "dlss5-nr-custom.addon64" })
        {
            var relativePath = Rel("_storage_", filename);
            var path = thirdPartyGame.Put(relativePath, "unverified third-party Neural Rendering add-on");
            var before = Capture(thirdPartyGame);
            var status = svc.Inspect(thirdPartyGame.Dir, thirdPartyGame.Exe);
            Reject(() => svc.Restore(thirdPartyGame.Dir, thirdPartyGame.Exe, expectedFingerprint: status.Fingerprint));
            Reject(() => svc.Restore(thirdPartyGame.Dir, thirdPartyGame.Exe, switchAlternate: true, expectedFingerprint: status.Fingerprint));
            check($"NeuralMaster blocks {filename} even with the original host present",
                status is { State: NeuralMasterSnapshotState.NeedsAttention, CanRestore: false, CanSwitch: false }
                && HasPath(status.ConflictPaths, relativePath) && SameFiles(before, Capture(thirdPartyGame))
                && !status.Message.Contains("Restore originals", StringComparison.OrdinalIgnoreCase));
            File.Delete(path);
        }

        var ownedConflict = Create(root, "nm-managed-conflict", "", "");
        svc.Uninstall(ownedConflict.Dir, ownedConflict.Exe);
        var owner = new Session(ownedConflict.Dir, Journal.ReadManifest(ownedConflict.Dir)!, _ => { });
        owner.WriteTracked(ownedConflict.At("dxgi.dll"), AlternateHostBytes);
        owner.WriteTracked(ownedConflict.At(AlternateAddon), AlternateBytes, "addon");
        var ownedBefore = Capture(ownedConflict);
        var ownedStatus = svc.Inspect(ownedConflict.Dir, ownedConflict.Exe);
        Reject(() => svc.Restore(ownedConflict.Dir, ownedConflict.Exe, switchAlternate: true));
        check("NeuralMaster directs a recorded alternate setup to Restore originals",
            ownedStatus is { State: NeuralMasterSnapshotState.NeedsAttention, CanRestore: false, CanSwitch: false }
            && ownedStatus.Message.Contains("Restore originals", StringComparison.OrdinalIgnoreCase)
            && SameFiles(ownedBefore, Capture(ownedConflict)));

        var corruptGame = Create(root, "nm-corrupt", "", "");
        svc.Uninstall(corruptGame.Dir, corruptGame.Exe);
        var corruptPlan = svc.Inspect(corruptGame.Dir, corruptGame.Exe);
        var dumpedAddon = Directory.GetFiles(corruptPlan.BackupLocation!, "*", SearchOption.AllDirectories)
            .Single(p => Components.Sha256(p) == Hash(AddonBytes));
        File.WriteAllText(dumpedAddon, "corrupt payload");
        var beforeCorrupt = Capture(corruptGame);
        var corruptStatus = svc.Inspect(corruptGame.Dir, corruptGame.Exe);
        Reject(() => svc.Restore(corruptGame.Dir, corruptGame.Exe));
        check("NeuralMaster refuses a corrupt backup without writing to the game",
            corruptStatus is { State: NeuralMasterSnapshotState.NeedsAttention, CanRestore: false }
            && SameFiles(beforeCorrupt, Capture(corruptGame)));

        var lockedGame = Create(root, "nm-locked", "", "", "_storage_");
        svc.Uninstall(lockedGame.Dir, lockedGame.Exe);
        var savedLocked = svc.Inspect(lockedGame.Dir, lockedGame.Exe).BackupLocation;
        svc.Restore(lockedGame.Dir, lockedGame.Exe);
        var beforeLocked = Capture(lockedGame);
        using (new FileStream(lockedGame.At("_storage_/" + Addon), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => svc.Uninstall(lockedGame.Dir, lockedGame.Exe));
        check("NeuralMaster preserves game files and the last valid backup when a target is locked",
            SameFiles(beforeLocked, Capture(lockedGame))
            && savedLocked is not null && BackupContains(savedLocked, AddonBytes)
            && !InstallTransaction.Pending(lockedGame.Dir));

        await AssertGenericProtectionAsync(root, check);
        AssertGenericRecoveryOwnership(root, check);
        await AssertConcurrentCallsAsync(root, check);
        await AssertInterruptionAsync(root, check);
    }

    private static void AssertGameStateVisibility(string root, NeuralMasterSnapshots svc, Action<string, bool> check)
    {
        var game = Create(root, "nm-game-bar", "bin", "bin");
        var manifest = Journal.ReadManifest(game.Dir);
        check("the NeuralMaster game bar is relevant for an installed build under a normal MFG record",
            manifest?.Route == "mfgonly" && NeuralMasterSnapshots.HasGameState(game.Dir, game.Exe, manifest));

        game.Put("bin/" + Addon, "unverified NeuralMaster build");
        check("the NeuralMaster game bar can report an unknown build without permitting removal",
            NeuralMasterSnapshots.HasGameState(game.Dir, game.Exe, manifest)
            && svc.Inspect(game.Dir, game.Exe) is { State: NeuralMasterSnapshotState.NeedsAttention, CanUninstall: false });
        game.Put("bin/" + Addon, AddonBytes);
        var otherExe = game.Put("other/nm-fixture-other.exe", "unrelated executable in the same game directory");
        var scan = Scanner.Scan(game.Dir, game.Exe);
        check("the NeuralMaster cover badge detects the active add-on for the selected executable",
            scan.NeuralMasterAddon is { } addon && HasPath([addon], "bin/" + Addon));
        // Synthetic executables are scanned only when explicitly selected.
        scan.Primary = Scanner.Scan(game.Dir, otherExe).Primary;
        Scanner.InspectExeDir(scan, Path.GetDirectoryName(otherExe)!);
        check("the NeuralMaster cover badge clears when selecting an executable without a loadable add-on",
            scan.Primary?.Path.Equals(otherExe, StringComparison.OrdinalIgnoreCase) == true && scan.NeuralMasterAddon is null);

        svc.Uninstall(game.Dir, game.Exe);
        check("the NeuralMaster game bar shows a saved backup for its executable only",
            NeuralMasterSnapshots.HasGameState(game.Dir, game.Exe, manifest) && svc.Inspect(game.Dir, game.Exe).CanRestore
            && !NeuralMasterSnapshots.HasGameState(game.Dir, otherExe, manifest));
        check("the NeuralMaster cover badge stays hidden when only a saved backup is available",
            Scanner.Scan(game.Dir, game.Exe).NeuralMasterAddon is null);

        var ordinary = Create(root, "nm-hidden-game-bar", "", "");
        File.Move(ordinary.At(Addon), ordinary.At(Addon + ".parked-fixture"));
        var ordinaryManifest = Journal.ReadManifest(ordinary.Dir);
        check("ordinary games and parked NeuralMaster copies do not show the NeuralMaster game bar",
            ordinaryManifest?.Route == "mfgonly"
            && !NeuralMasterSnapshots.HasGameState(ordinary.Dir, ordinary.Exe, ordinaryManifest)
            && svc.Inspect(ordinary.Dir, ordinary.Exe).State == NeuralMasterSnapshotState.NotFound);
        check("parked NeuralMaster add-ons do not show a cover badge",
            Scanner.Scan(ordinary.Dir, ordinary.Exe).NeuralMasterAddon is null);
        using (InstallTransaction.Begin(ordinary.Dir)) { }
        check("an ordinary pending game setup does not show the NeuralMaster game bar",
            InstallTransaction.Pending(ordinary.Dir)
            && !NeuralMasterSnapshots.HasGameState(ordinary.Dir, ordinary.Exe, ordinaryManifest));
        InstallTransaction.Recover(ordinary.Dir);
    }

    private static void AssertExplicitImport(string root, Action<string, bool> check)
    {
        const string importedBytes = "Synthetic support build that is not pre-approved";
        const string importedHostBytes = "Synthetic explicit-import matching ReShade host";
        var game = Create(root, "nm-import", "", "", "_storage_");
        var other = Create(root, "nm-import-untrusted", "", "");
        var svc = Service();
        svc.Uninstall(game.Dir, game.Exe);
        var priorBackup = svc.Inspect(game.Dir, game.Exe);
        check("an older NeuralMaster build is backed up before verifying a future build",
            priorBackup is { State: NeuralMasterSnapshotState.Uninstalled, Build: "Fixture A", CanRestore: true, BackupLocation: not null });
        foreach (var fixture in new[] { game, other })
        foreach (var lane in fixture.Lanes)
        {
            fixture.Put(Rel(lane, Addon), importedBytes);
            fixture.Put(Rel(lane, "dxgi.dll"), importedHostBytes);
        }
        var before = Capture(game);
        var importDir = Path.Combine(root, "nm-original-build");
        Directory.CreateDirectory(importDir);
        var addonSource = Path.Combine(importDir, Addon);
        var hostSource = Path.Combine(importDir, "ReShade64.dll");
        File.WriteAllText(addonSource, importedBytes);
        File.WriteAllText(hostSource, "wrong host from a different build");
        check("unknown NeuralMaster builds remain protected before explicit trust is recorded",
            svc.Inspect(game.Dir, game.Exe) is { State: NeuralMasterSnapshotState.NeedsAttention, CanUninstall: false, CanRestore: false });
        Reject(() => svc.ImportBuild(game.Dir, game.Exe, addonSource, hostSource, "Fixture import"));
        check("a mismatched reference import cannot authorize NeuralMaster file removal",
            !Service().Inspect(game.Dir, game.Exe).CanUninstall && SameFiles(before, Capture(game)));
        File.WriteAllText(hostSource, importedHostBytes);
        svc.ImportBuild(game.Dir, game.Exe, addonSource, hostSource, "Fixture import");
        svc = Service();
        check("matching reference import persists for one game and keeps both the references and game files intact",
            svc.Inspect(game.Dir, game.Exe) is { State: NeuralMasterSnapshotState.Installed, CanUninstall: true, Build: "Fixture import" }
            && !svc.Inspect(other.Dir, other.Exe).CanUninstall
            && SameFiles(before, Capture(game))
            && File.ReadAllText(addonSource) == importedBytes
            && File.ReadAllText(hostSource) == importedHostBytes);
        var verified = svc.Inspect(game.Dir, game.Exe);
        check("verifying a future build preserves the previous NeuralMaster backup",
            verified.BackupLocation == priorBackup.BackupLocation && verified.BackupDate == priorBackup.BackupDate
            && verified.BackupLocation is { } saved && BackupContains(saved, AddonBytes) && BackupContains(saved, HostBytes));
        svc.Uninstall(game.Dir, game.Exe);
        var importedSnapshot = svc.Inspect(game.Dir, game.Exe);
        var promoted = new NeuralMasterSnapshots([new("Canonical release label", Hash(importedBytes), Hash(importedHostBytes))]);
        var promotedSnapshot = promoted.Inspect(game.Dir, game.Exe);
        check("a saved NeuralMaster snapshot keeps its imported label when the release becomes built in",
            promotedSnapshot is { State: NeuralMasterSnapshotState.Uninstalled, Build: "Fixture import", CanRestore: true }
            && promotedSnapshot.BackupLocation == importedSnapshot.BackupLocation
            && promotedSnapshot.BackupDate == importedSnapshot.BackupDate);
        promoted.Restore(game.Dir, game.Exe, expectedFingerprint: promotedSnapshot.Fingerprint);
        check("explicitly imported NeuralMaster builds uninstall and restore byte-for-byte",
            SameFiles(before, Capture(game))
            && promoted.Inspect(game.Dir, game.Exe) is { State: NeuralMasterSnapshotState.Installed, Build: "Canonical release label", CanUninstall: true });
    }

    private static void AssertAutomaticReleaseRecognition(string root, Action<string, bool> check)
    {
        const string addonBytes = "Future NeuralMaster release";
        const string hostBytes = "ReShade host matching future release";
        const string label = "Future release";
        var game = Create(root, "nm-release-registered", "", "", "_storage_");
        var svc = Service();
        svc.Uninstall(game.Dir, game.Exe);
        var previous = svc.Inspect(game.Dir, game.Exe);
        foreach (var lane in game.Lanes)
        {
            game.Put(Rel(lane, Addon), addonBytes);
            game.Put(Rel(lane, "dxgi.dll"), hostBytes);
        }

        var release = new NeuralMasterKnownBuild(label, Hash(addonBytes), Hash(hostBytes));
        var receiptName = release.AddonSha256 + ".json";
        game.Put(receiptName, JsonSerializer.Serialize(release));
        var activeFiles = Capture(game);
        check("the game directory cannot register its own future build",
            svc.Inspect(game.Dir, game.Exe) is { State: NeuralMasterSnapshotState.NeedsAttention, CanUninstall: false });

        var catalogue = Path.Combine(AppPaths.Root, NeuralMasterSnapshots.ReleaseCatalogueFolderName);
        Directory.CreateDirectory(catalogue);
        var receiptPath = Path.Combine(catalogue, receiptName);
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(release with { AddonSha256 = Hash(AddonBytes) }));
        check("a release receipt must name the exact installed add-on digest",
            !svc.Inspect(game.Dir, game.Exe).CanUninstall);
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(release));

        game.Put("_storage_/dxgi.dll", "incompatible host");
        check("automatic release recognition refuses a mismatched mirrored host",
            !svc.Inspect(game.Dir, game.Exe).CanUninstall);
        game.Put("_storage_/dxgi.dll", hostBytes);
        var status = Service().Inspect(game.Dir, game.Exe);
        check("a published release is recognized on rescan without per-game import or file changes",
            status is { State: NeuralMasterSnapshotState.Installed, Build: label, CanUninstall: true }
            && status.BackupLocation == previous.BackupLocation
            && status.BackupDate == previous.BackupDate
            && SameFiles(activeFiles, Capture(game)));

        svc.Uninstall(game.Dir, game.Exe, status.Fingerprint);
        var saved = Service().Inspect(game.Dir, game.Exe);
        check("an automatically recognized release can be backed up and restored",
            saved is { State: NeuralMasterSnapshotState.Uninstalled, Build: label, CanRestore: true });
        Service().Restore(game.Dir, game.Exe, expectedFingerprint: saved.Fingerprint);
        check("the automatic release restores both verified copies byte-for-byte", SameFiles(activeFiles, Capture(game)));
    }

    private static async Task AssertGenericProtectionAsync(string root, Action<string, bool> check)
    {
        var game = Create(root, "nm-generic-protection", "", "_storage_");
        var before = Capture(game);
        var components = new Components(new AppSettings());
        foreach (var route in new[] { Route.Native, Route.MfgOnly })
        {
            var request = new InstallRequest
            {
                GameDir = game.Dir, ExePath = game.Exe, Api = RenderApi.DX12, Bitness = 64, Route = route
            };
            await RejectProtectedAsync(() => Installers.InstallAsync(request, components, _ => { }, null, CancellationToken.None));
        }
        await RejectProtectedAsync(() => Installers.RestoreAsync(game.Dir, game.Exe, _ => { }));

        var entry = new GameEntry { Name = "NeuralMaster guard fixture", Dir = game.Dir, Exe = game.Exe };
        var choices = new GameOverride { Exe = game.Exe, Api = "DX12" };
        var scan = Scanner.Scan(game.Dir, game.Exe);
        var bulkGame = BulkSetup.Read(entry, scan, choices);
        var context = new BulkContext(true, true, true, false, null, null);
        var direct = BulkSetup.Plan(bulkGame, new BulkOptions(Route.MfgOnly, Components.OptiBuilds[0], true, true), context);
        var managed = ManagedGameUpdates.Plan(entry, choices, context);
        check("ordinary setups, restore, bulk and managed updates protect a manual NeuralMaster add-on under an MFG record",
            scan.Manifest?.Route == "mfgonly"
            && direct.Request is null && direct.Skip?.Contains("NeuralMaster bar", StringComparison.OrdinalIgnoreCase) == true
            && managed.Request is null && managed.Skip?.Contains("NeuralMaster bar", StringComparison.OrdinalIgnoreCase) == true
            && SameFiles(before, Capture(game)));
        var svc = Service();
        svc.Uninstall(game.Dir, game.Exe);
        var afterRemoval = Scanner.Scan(game.Dir, game.Exe);
        // Supply architecture facts that were intentionally omitted from the non-executable fixtures.
        afterRemoval.Primary = afterRemoval.Primary! with { Bitness = 64, Api = RenderApi.DX12 };
        var fg = afterRemoval.Upscalers.FindIndex(f => f.Kind == UpscalerKind.DlssFG);
        afterRemoval.Upscalers[fg] = afterRemoval.Upscalers[fg] with { Bitness = 64 };
        var eligible = BulkSetup.Plan(BulkSetup.Read(entry, afterRemoval, choices),
            new BulkOptions(Route.MfgOnly, Components.OptiBuilds[0], true, true), context);
        check("dedicated uninstall clears the protection guard and retains the ordinary MFG setup",
            eligible.Request is { Route: Route.MfgOnly, MfgUnlock: true } && afterRemoval.Manifest?.Route == "mfgonly"
            && SameFiles(before.Where(kv => !kv.Key.EndsWith(Addon, StringComparison.OrdinalIgnoreCase)), Capture(game)));
        svc.Restore(game.Dir, game.Exe);
        File.Move(game.At("_storage_/" + Addon), game.At("_storage_/" + Addon + ".parked-fixture"));
        check("parked NeuralMaster copies do not block ordinary game operations",
            NeuralMasterProtection.Conflict(Scanner.Scan(game.Dir, game.Exe)) is null);
    }

    private static void AssertGenericRecoveryOwnership(string root, Action<string, bool> check)
    {
        var game = Create(root, "nm-generic-pending", "", "");
        var svc = Service();
        svc.Uninstall(game.Dir, game.Exe);
        var before = Capture(game);
        using (InstallTransaction.Begin(game.Dir))
        {
            InstallTransaction.Capture(game.At("ReShade.ini"));
            game.Put("ReShade.ini", "ordinary transaction interrupted after changing this file");
        }
        var interrupted = Capture(game);
        Reject(() => svc.Recover(game.Dir, game.Exe));
        check("dedicated NeuralMaster recovery cannot replay an ordinary pending transaction",
            InstallTransaction.Pending(game.Dir) && !svc.Inspect(game.Dir, game.Exe).CanRecover
            && SameFiles(interrupted, Capture(game)));
        InstallTransaction.Recover(game.Dir);
        check("ordinary management retains ownership of and can recover its pending transaction",
            !InstallTransaction.Pending(game.Dir) && SameFiles(before, Capture(game))
            && svc.Inspect(game.Dir, game.Exe).CanRestore);
    }

    private static async Task RejectProtectedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InstallException ex) when (ex.Message.Contains("NeuralMaster bar", StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected the ordinary game operation to be blocked by NeuralMaster protection.");
    }

    private static async Task AssertConcurrentCallsAsync(string root, Action<string, bool> check)
    {
        var game = Create(root, "nm-concurrent", "", "", "_storage_");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = Service(afterChange: _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Concurrent fixture did not release the operation.");
        });
        var first = Task.Run(() => hold.Uninstall(game.Dir, game.Exe));
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent fixture never changed its first file.");
            var second = Task.Run(() => Reject(() => Service().Uninstall(game.Dir, game.Exe)));
            await second.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            release.Set();
            await first;
        }
        check("NeuralMaster simultaneous operations leave a single coherent removal and usable backup",
            Service().Inspect(game.Dir, game.Exe) is { CanRestore: true, State: NeuralMasterSnapshotState.Uninstalled }
            && game.Lanes.All(lane => !File.Exists(game.At(Rel(lane, Addon)))));
    }

    private static async Task AssertInterruptionAsync(string root, Action<string, bool> check)
    {
        var game = Create(root, "nm-interrupted", "bin", "bin", "bin/_storage_");
        var otherExe = game.Put("other/nm-fixture-other.exe", "unrelated executable in the same game directory");
        var svc = Service();
        svc.Uninstall(game.Dir, game.Exe);
        var before = Capture(game);
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test host path.");
        var start = new ProcessStartInfo(process)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(NeuralMasterFixtures).Assembly.Location);
        start.ArgumentList.Add("--nm-interrupted-restore");
        start.ArgumentList.Add(root);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start interrupted-restore fixture.");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync(); throw; }
        if (child.ExitCode != InterruptedExitCode)
            throw new Exception($"Unexpected interrupted fixture exit {child.ExitCode}. {await output} {await errors}");
        check("NeuralMaster simulates an interruption between writing the two add-on copies",
            InstallTransaction.Pending(game.Dir)
            && game.Lanes.Count(lane => File.Exists(game.At(Rel(lane, Addon)))) == 1
            && svc.Inspect(game.Dir, game.Exe).CanRecover
            && NeuralMasterSnapshots.HasGameState(game.Dir, game.Exe, Journal.ReadManifest(game.Dir)));
        var incomplete = Capture(game);
        Reject(() => InstallTransaction.Recover(game.Dir));
        check("generic recovery cannot replay a NeuralMaster-owned pending transaction",
            InstallTransaction.Pending(game.Dir) && SameFiles(incomplete, Capture(game)));
        var otherStatus = svc.Inspect(game.Dir, otherExe);
        Reject(() => svc.Recover(game.Dir, otherExe));
        check("the NeuralMaster game bar can direct an interrupted game to the original executable",
            NeuralMasterSnapshots.FindLoadableAddon(game.Dir, otherExe) is null
            && NeuralMasterSnapshots.HasGameState(game.Dir, otherExe, Journal.ReadManifest(game.Dir))
            && otherStatus.Message.Contains(game.Exe, StringComparison.OrdinalIgnoreCase));
        check("NeuralMaster recovery refuses a different executable in the same game directory",
            otherStatus is { State: NeuralMasterSnapshotState.NeedsAttention, CanRecover: false }
            && InstallTransaction.Pending(game.Dir) && SameFiles(incomplete, Capture(game)));
        svc.Recover(game.Dir, game.Exe);
        check("NeuralMaster recovers the previous coherent state and retains the backup after interruption",
            !InstallTransaction.Pending(game.Dir) && SameFiles(before, Capture(game))
            && svc.Inspect(game.Dir, game.Exe) is { CanRestore: true });
        svc.Restore(game.Dir, game.Exe);
        check("NeuralMaster can restore both copies byte-for-byte after recovery",
            game.Lanes.All(lane => File.ReadAllText(game.At(Rel(lane, Addon))) == AddonBytes));
    }

    internal static void InterruptedRestoreChild(string root)
    {
        // This entry point is only allowed to address the parent process's synthetic test tree.
        root = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var rootName = Path.GetFileName(root);
        const string prefix = "d5m-workflow-";
        if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase)
            || !rootName.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(rootName[prefix.Length..], "N", out _))
            throw new ArgumentException("Interrupted-restore fixture requires an isolated workflow test root.");
        Environment.SetEnvironmentVariable("DLSS5MASTER_DATA", Path.Combine(root, "data"));
        var dir = Path.Combine(root, "nm-interrupted");
        var exe = Path.Combine(dir, "bin", "nm-fixture-game.exe");
        if (File.ReadAllText(exe) != "workflow fixture executable")
            throw new InvalidOperationException("Interrupted-restore fixture game marker is missing.");
        var crashing = Service(afterChange: rel =>
        {
            if (rel.EndsWith(Addon, StringComparison.OrdinalIgnoreCase)) Environment.Exit(InterruptedExitCode);
        });
        crashing.Restore(dir, exe);
        throw new InvalidOperationException("Interrupted restore completed without calling its mutation hook.");
    }

    private static NeuralMasterSnapshots Service(Action<string>? afterChange = null) => new(
        new[] { new NeuralMasterKnownBuild("Fixture A", Hash(AddonBytes), Hash(HostBytes)) },
        new[] { new NeuralMasterKnownBuild("Fixture B", Hash(AlternateBytes), Hash(AlternateHostBytes)) },
        afterChange);

    private static Fixture Create(string root, string folder, string exeDir, params string[] lanes)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        var game = new Fixture(dir, Path.Combine(dir, Rel(exeDir, "nm-fixture-game.exe")), lanes);
        game.Put(Rel(exeDir, "nm-fixture-game.exe"), "workflow fixture executable");
        game.Put(Rel(exeDir, "NeuralMaster-native-call.ini"), "private sidecar fixture");
        var owned = Session.Begin(dir, game.Exe, "mfgonly", "dxgi", 64, _ => { });
        owned.Manifest.MfgUnlock = "fixture";
        owned.WriteTracked(game.At(Rel(exeDir, "renodx-mfgunlock.addon64")), "managed MFG file");
        owned.WriteTracked(game.At(Rel(exeDir, "dxgi.dll")), HostBytes);
        owned.WriteTracked(game.At(Rel(exeDir, "ReShade.ini")), Inis);
        foreach (var lane in lanes)
        {
            game.Put(Rel(lane, Addon), AddonBytes);
            game.Put(Rel(lane, "dxgi.dll"), HostBytes);
            game.Put(Rel(lane, "ReShade.ini"), Inis);
            game.Put(Rel(lane, "nvngx_dlssg.dll"), "unrelated frame generation provider");
            game.Put(Rel(lane, "custom.addon64"), "untouched third-party add-on");
            game.Put(Rel(lane, AlternateAddon + ".parked-fixture"), "previously parked unknown add-on");
        }
        return game;
    }

    private static string Rel(string lane, string name) => Path.Combine(lane, name);
    private static bool HasPath(IEnumerable<string> paths, string match) =>
        paths.Any(path => path.Replace('\\', '/').Equals(match.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    private static bool Under(string path, string parent) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    private static string Hash(string contents) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contents)));
    private static bool BackupContains(string backup, string contents) => BackupMatches(backup, contents) > 0;
    private static int BackupMatches(string backup, string contents) => Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories)
        .Count(p => Components.Sha256(p) == Hash(contents));
    private static Dictionary<string, string> Capture(Fixture game) => Directory.EnumerateFiles(game.Dir, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(game.Dir, p), Components.Sha256, StringComparer.OrdinalIgnoreCase);
    private static bool SameFiles(IEnumerable<KeyValuePair<string, string>> expected, IReadOnlyDictionary<string, string> actual) =>
        expected.Count() == actual.Count && expected.All(kv => actual.TryGetValue(kv.Key, out var hash) && hash == kv.Value);
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InstallException) { return; }
        throw new Exception("Expected NeuralMaster operation to be blocked.");
    }

    private sealed record Fixture(string Dir, string Exe, string[] Lanes)
    {
        internal string At(string rel) => Journal.SafePath(Dir, rel);
        internal string Put(string rel, string contents)
        {
            var path = At(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }
    }
}
