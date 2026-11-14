namespace DLSS5Master.Core;

/// <summary>A proxy DLL or ASI plugin beside a game's exe, and whose it is by its version resource.</summary>
public sealed record LoaderFile(string Name, bool ReShade, bool OptiScaler, bool Microsoft);

/// <summary>One game for "Set up several games", with what its folder holds that a setup depends on (<see cref="BulkSetup.Read"/>).</summary>
public sealed class BulkGame
{
    public required GameEntry Entry { get; init; }
    public GameScan? Scan { get; init; }
    /// <summary>The choices saved on the game's page: its .exe, Rendering API and loading add-ons early.</summary>
    public GameOverride? Choices { get; init; }
    public bool FolderGone { get; init; }
    /// <summary>lecram's own setup has NR installed beside the game's exe (<see cref="LecramNr.SetupInstalled"/>).</summary>
    public bool LecramSetup { get; init; }
    /// <summary>lecram's own setup installed NR after DLSS 5 Master did (<see cref="Installers.LecramSetupAfterUs"/>).</summary>
    public bool LecramSetupAfterUs { get; init; }
    /// <summary>lecram's DLSS5 NR add-on beside the exe: its version, "" when it has none; null when the add-on is not there.</summary>
    public string? LecramAddon { get; init; }
    /// <summary>The game holds a copy of lecram's DLSS5 NR to set up from (<see cref="LecramNr.InGame"/>).</summary>
    public bool LecramInGame { get; init; }
    /// <summary>The menu key in OptiScaler.ini beside the exe; null when there is none or it is "auto".</summary>
    public string? OptiMenuKey { get; init; }
    /// <summary>NVIDIA's neural runtime (nvngx_dlssnr.dll) is beside the exe.</summary>
    public bool NeuralRuntimeHere { get; init; }
    /// <summary>Proxy DLLs and ASI plugins beside the exe that this app's install did not add: a route refuses to go next to some.</summary>
    public IReadOnlyList<LoaderFile> Loaders { get; init; } = Array.Empty<LoaderFile>();
    /// <summary>
    /// The ReShade the setup finds beside the exe once a reinstall's restore has run (the earlier setup's own ReShade gone,
    /// one it replaced back): the one it keeps or refuses. Null when unknown; <see cref="GameScan.ReShade"/> stands in then.
    /// </summary>
    public ReShadeState? ReShadeAfterRestore { get; init; }
    /// <summary>The bitness of <see cref="ReShadeAfterRestore"/>'s file; 0 when there is none or it cannot be read.</summary>
    public int ReShadeAfterRestoreBits { get; init; }
    /// <summary>The game's own copy of lecram's DLSS5 NR has its dlss5-nr-host folder beside the exe.</summary>
    public bool LecramInGameHasHost { get; init; }
}

/// <summary>What every game in the batch shares: the NVIDIA runtime and ReShade found, the player's lecram copy, the saved menu key.</summary>
public sealed record BulkContext(bool NeuralRuntime, bool DlssRuntime, bool ReShade64, bool ReShade32, string? LecramCopy, string? SavedMenuKey,
                                  bool LecramCopyHasHost = true, string? ReShade64Version = null, string? ReShade32Version = null)
{
    /// <summary>Reads them once for the batch. Slow (file checks; a ReShade setup may be unpacked): call it off the UI thread.</summary>
    public static BulkContext Read(AppSettings settings, Components components)
    {
        var runtime = components.RuntimeFiles();
        string? reshade64 = components.ReShadeDll(64), reshade32 = components.ReShadeDll(32);
        var copy = LecramNr.IsCopy(settings.LecramNrFolder) ? settings.LecramNrFolder : LecramNr.DropCopy();
        bool host;
        try { host = copy is not null && Directory.Exists(Path.Combine(copy, LecramNr.HostFolder)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { host = false; }
        return new(runtime.ContainsKey("nvngx_dlssnr.dll"), runtime.ContainsKey("nvngx_dlss.dll"), reshade64 is not null, reshade32 is not null,
                   copy, settings.OptiScalerMenuKey, host,
                   reshade64 is null ? null : Pe.GetFileVersion(reshade64), reshade32 is null ? null : Pe.GetFileVersion(reshade32));
    }
}

/// <summary>
/// The batch's choices: the route, the OptiScaler build, the MFG unlock for games set up for the first time (games set up
/// again keep their own), and whether games DLSS 5 Master has set up already are set up again.
/// </summary>
public sealed record BulkOptions(Route Route, OptiBuild Build, bool Mfg, bool Again);

/// <summary>One game's line in the plan: the setup to run, or why the game is left as it is.</summary>
public sealed class BulkRow
{
    public required BulkGame Game { get; init; }
    /// <summary>The setup, with the defaults the game's own page would pick; null when the game is left as it is.</summary>
    public InstallRequest? Request { get; init; }
    /// <summary>Why the game is left as it is; null when it is set up.</summary>
    public string? Skip { get; init; }
    /// <summary>The game has anti-cheat, so it is left as it is: only its own page sets it up, after a warning.</summary>
    public bool AntiCheat { get; init; }
    /// <summary>DLSS 5 Master has set up this game already: setting it up again is a reinstall.</summary>
    public bool Reinstall { get; init; }
    /// <summary>The route fits the game, which is set up already: it is left as it is unless <see cref="BulkOptions.Again"/>.</summary>
    public bool NeedsAgain { get; init; }
    /// <summary>What to know about this game's setup (in-game settings kept or starting fresh, the MFG unlock); "" when nothing.</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// "Set up several games": one route for many games at once. The plan is worked out before anything changes, the way a
/// game's own page decides (its defaults, its warnings and its questions), so the games the route does not fit, and every
/// game with anti-cheat, are left as they are, each with the reason. The games are then set up one at a time:
/// <see cref="Installers.InstallAsync"/> unpacks components into folders that all games share.
/// </summary>
public static class BulkSetup
{
    /// <summary>The routes offered, in the order a game's page offers them.</summary>
    public static readonly Route[] Routes = { Route.Native, Route.Multipass, Route.Feeder, Route.OptiScaler, Route.LecramNr };

    /// <summary>The Rendering API choices on a game's page; a choice saved there is one of these names.</summary>
    public static readonly RenderApi[] ApiChoices =
        { RenderApi.DX12, RenderApi.DX11, RenderApi.DX10, RenderApi.DX9, RenderApi.DX8, RenderApi.DirectDraw, RenderApi.Vulkan, RenderApi.OpenGL };

    public const string NoNeuralRuntime = "The NVIDIA neural runtime (nvngx_dlssnr.dll) was not found. Pick the NVIDIA runtime folder in Settings.";
    public const string NoDlssRuntime = "The Feeder needs nvngx_dlss.dll in the NVIDIA runtime folder. Pick it in Settings.";
    public const string NoReShade = "No ReShade with add-on support was found. Pick a ReShade_Setup_*_Addon.exe in Settings.";

    private static readonly string[] ReShadeSlots = { "dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "opengl32.dll" };

    /// <summary>The API a setup uses: the one chosen on the game's page, else the one the scan found.</summary>
    public static RenderApi EffectiveApi(ExeInfo? exe, string? chosen)
    {
        var picked = ApiChoices.FirstOrDefault(a => a.ToString() == chosen);
        return picked != RenderApi.Unknown ? picked : exe?.Api ?? RenderApi.Unknown;
    }

    /// <summary>Reads what <paramref name="scan"/>'s game holds beside its exe. Reads files: call it off the UI thread.</summary>
    public static BulkGame Read(GameEntry entry, GameScan? scan, GameOverride? choices)
    {
        bool gone;
        try { gone = !Directory.Exists(entry.Dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { gone = true; }
        if (gone || scan?.Primary is not { } exe) return new BulkGame { Entry = entry, Scan = scan, Choices = choices, FolderGone = gone };

        var exeDir = Path.GetDirectoryName(exe.Path)!;
        bool lecramSetup = false, lecramAfterUs = false, lecramInGame = false, runtimeHere = false, inGameHost = false;
        string? lecramAddon = null, menuKey = null;
        var loaders = new List<LoaderFile>();
        ReShadeState? reshadeAfter = null;
        int reshadeBits = 0;
        try
        {
            lecramSetup = LecramNr.SetupInstalled(exeDir);
            lecramAfterUs = lecramSetup && Installers.LecramSetupAfterUs(scan.Dir);
            if (new[] { LecramNr.Addon64, LecramNr.Addon32 }.Select(a => Path.Combine(exeDir, a)).FirstOrDefault(File.Exists) is { } addon)
                lecramAddon = Pe.GetFileVersion(addon) ?? "";
            lecramInGame = LecramNr.InGame(scan.Dir, exeDir, scan.Manifest) is not null;
            inGameHost = Directory.Exists(Path.Combine(exeDir, LecramNr.HostFolder));
            runtimeHere = File.Exists(Path.Combine(exeDir, "nvngx_dlssnr.dll"));

            // The page reads the key the same way: "auto" or none means the one saved in the settings.
            var ini = Path.Combine(exeDir, "OptiScaler.ini");
            var key = File.Exists(ini) ? Ini.Get(Ini.ReadText(ini), "Menu", "ShortcutKey") : null;
            menuKey = key is null or "auto" ? null : key;

            // The folder as the restore that starts a reinstall leaves it: the files an earlier setup of this app added go,
            // and the originals it replaced come back, so those are judged by their backup copies.
            var root = Path.GetFullPath(scan.Dir).TrimEnd('\\', '/');
            var m = scan.Manifest;
            var added = new HashSet<string>(m?.Added ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var replaced = new HashSet<string>(m?.Replaced.Select(r => r.Rel) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var exeRel = Path.GetRelativePath(root, exeDir);
            if (exeRel == ".") exeRel = "";
            var names = new SortedSet<string>(Directory.EnumerateFiles(exeDir).Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
            foreach (var rel in replaced)
                if (string.Equals(Path.GetDirectoryName(rel) ?? "", exeRel, StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileName(rel));
            // The file a name stands for once the restore has run: its backup copy, nothing, or the file beside the exe.
            string? AfterRestore(string name)
            {
                var rel = Path.Combine(exeRel, name);
                var file = replaced.Contains(rel) ? Path.Combine(Journal.OriginalsDir(root, m!), rel)
                         : added.Contains(rel) ? null
                         : Path.Combine(exeDir, name);
                return file is not null && File.Exists(file) ? file : null;
            }
            foreach (var name in names)
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                bool asi = Path.GetExtension(name).Equals(".asi", StringComparison.OrdinalIgnoreCase);
                bool dll = Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                           && (Installers.OptiConflictStems.Contains(stem) || ReShadeSlots.Contains(name, StringComparer.OrdinalIgnoreCase));
                if (!asi && !dll || AfterRestore(name) is not { } file) continue;
                bool microsoft = (stem.Equals("dbghelp", StringComparison.OrdinalIgnoreCase) || stem.Equals("dbgcore", StringComparison.OrdinalIgnoreCase))
                                 && Pe.VersionMentions(file, "Microsoft");
                loaders.Add(new LoaderFile(name, Pe.VersionMentions(file, "ReShade"), Pe.VersionMentions(file, "OptiScaler"), microsoft));
            }
            // The ReShade the setup finds then, in Scanner.InspectReShade's order: its hook DLLs, then ASI plugins.
            reshadeAfter = new ReShadeState(false, null, null, false, false);
            foreach (var name in Scanner.ReShadeHooks.Concat(names.Where(n => Path.GetExtension(n).Equals(".asi", StringComparison.OrdinalIgnoreCase))))
            {
                if (AfterRestore(name) is not { } file || !Pe.VersionMentions(file, "ReShade")) continue;
                reshadeAfter = new ReShadeState(true, name, Pe.GetFileVersion(file), Scanner.HasAddonSupport(file), name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase));
                reshadeBits = Pe.GetBitness(file);
                break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InstallException) { reshadeAfter = null; }
        return new BulkGame
        {
            Entry = entry, Scan = scan, Choices = choices, LecramSetup = lecramSetup, LecramSetupAfterUs = lecramAfterUs,
            LecramAddon = lecramAddon, LecramInGame = lecramInGame, OptiMenuKey = menuKey, NeuralRuntimeHere = runtimeHere, Loaders = loaders,
            ReShadeAfterRestore = reshadeAfter, ReShadeAfterRestoreBits = reshadeBits, LecramInGameHasHost = inGameHost,
        };
    }

    /// <summary>What stops <paramref name="o"/>'s route in every game, said once above the list; null when nothing does.</summary>
    public static string? Blocker(BulkOptions o, BulkContext c)
    {
        bool reshade = o.Route != Route.OptiScaler;
        if (reshade && !c.NeuralRuntime) return NoNeuralRuntime;
        if (o.Route == Route.Feeder && !c.DlssRuntime) return NoDlssRuntime;
        if (reshade && !c.ReShade64 && !c.ReShade32) return NoReShade;
        if (o.Route == Route.LecramNr && OlderThanLecramNeeds(c.ReShade64Version))
            return $"lecram's DLSS5 NR needs ReShade 6.8 or newer, and the ReShade DLSS 5 Master installs is {c.ReShade64Version}. Pick ReShade 6.8 or newer in Settings.";
        return null;
    }

    /// <summary>lecram's DLSS5 NR does not start with a ReShade older than 6.8 (RunLecramNr says so after the setup).</summary>
    /// <remarks>Scanner's versions have three parts at least: "6.8" alone would not read as one.</remarks>
    internal static bool OlderThanLecramNeeds(string? version) => Scanner.IsVersion(version) && Scanner.CompareVersions(version, "6.8.0") < 0;

    public static List<BulkRow> Plan(IEnumerable<BulkGame> games, BulkOptions o, BulkContext c) => games.Select(g => Plan(g, o, c)).ToList();

    /// <summary>
    /// The setup of one game with <paramref name="o"/>'s route, or why it is left as it is. The page's warnings and questions
    /// become reasons here: nobody is there to answer them while several games are set up.
    /// </summary>
    public static BulkRow Plan(BulkGame g, BulkOptions o, BulkContext c)
    {
        var m = g.Scan?.Manifest;
        BulkRow Out(string why, bool antiCheat = false) => new() { Game = g, Skip = why, AntiCheat = antiCheat, Reinstall = m is not null };

        if (g.FolderGone) return Out("Its folder is gone: the game was moved or uninstalled. Rescan the library to find it.");
        if (g.Scan is not { } scan) return Out("It could not be scanned.");
        if (scan.Primary is not { } exe) return Out("No game .exe was found in its folder.");
        if (scan.AntiCheatName is { } antiCheat)
            return Out($"It has anti-cheat ({antiCheat}). Only its own page sets it up, after a warning.", antiCheat: true);
        if (scan.OtherAppBackup || scan.OtherAppRecord) return Out("Another app has an install in this game. Remove it on the game's page first.");
        if (g.LecramSetup)
            return Out(g.LecramSetupAfterUs
                ? "lecram's own setup installed NR in this game after DLSS 5 Master. Remove it on the game's page first."
                : "lecram's own setup has NR installed in this game. Remove it on the game's page first.");
        var api = EffectiveApi(exe, g.Choices?.Api);
        if (api == RenderApi.Unknown) return Out("Its renderer could not be detected. Choose the Rendering API on its page.");
        int bits = exe.Bitness;
        if (bits is not (32 or 64)) return Out($"{Path.GetFileName(exe.Path)} could not be read as a 32- or 64-bit program.");

        var exeDir = Path.GetDirectoryName(exe.Path)!;
        string? lecram = null;
        if (o.Route == Route.LecramNr)
        {
            // As LecramNr.Source: the copy picked in Settings or kept in DLSS 5 Master's folder, else the game's own.
            lecram = c.LecramCopy ?? (g.LecramInGame ? exeDir : null);
            if (lecram is null) return Out("There is no copy of lecram's DLSS5 NR for it. Add yours in Settings.");
        }
        var option = Installers.RoutesFor(api, bits, scan.HasNativeDlss, scan.HasFrameGen, lecramCopy: o.Route == Route.LecramNr)
            .FirstOrDefault(r => r.Route == o.Route);
        if (option is null) return Out("This route is not offered for this game.");
        if (option.Unavailable is { } unavailable) return Out(unavailable);
        // The page only warns about these two; set up unattended they would do nothing.
        if (o.Route == Route.Feeder && Installers.FeederStandsAside(api, scan.HasNativeDlss) is { } aside) return Out(aside);
        if (o.Route == Route.OptiScaler && o.Build.NeuralRendering && !scan.HasNativeDlss)
            return Out("DLSS-NR OptiScaler builds need a game that ships its own DLSS, and this one has none.");

        // What InstallAsync needs before it touches the game.
        bool reshade = o.Route != Route.OptiScaler;
        if (reshade && !c.NeuralRuntime) return Out(NoNeuralRuntime);
        if (!reshade && o.Build.NeuralRendering && !c.NeuralRuntime && !g.NeuralRuntimeHere)
            return Out("This OptiScaler build needs the NVIDIA neural runtime (nvngx_dlssnr.dll). Pick the NVIDIA runtime folder in Settings.");
        if (o.Route == Route.Feeder && !c.DlssRuntime) return Out(NoDlssRuntime);
        if (reshade && !(bits == 64 ? c.ReShade64 : c.ReShade32))
            return Out($"No {bits}-bit ReShade with add-on support was found. Pick a ReShade_Setup_*_Addon.exe in Settings.");
        if (o.Route == Route.Feeder && bits != 64 && !c.ReShade64)
            return Out("The Feeder's 64-bit helper needs a 64-bit ReShade with add-on support. Pick a ReShade_Setup_*_Addon.exe in Settings.");
        // Another ReShade route sets lecram's add-on aside; the page asks first. Never set the player's own copy aside unseen.
        if (reshade && o.Route != Route.LecramNr && g.LecramAddon is { } version)
            return Out($"It has lecram's DLSS5 NR{(version == "" ? "" : " " + version)}, which this route would set aside. Pick lecram's NR, or set it up on its page.");
        var reshadeThen = g.ReShadeAfterRestore ?? scan.ReShade;
        if (o.Route == Route.LecramNr)
        {
            // RunLecramNr finishes these setups and only logs that NR will not start: set up unattended, they would do nothing.
            bool host = c.LecramCopy is not null ? c.LecramCopyHasHost : g.LecramInGameHasHost;
            if (!host && (bits != 64 || api is RenderApi.DX9 or RenderApi.DX10 or RenderApi.OpenGL))
                return Out($"Your copy of lecram's DLSS5 NR has no {LecramNr.HostFolder} folder, which NR needs in 32-bit, Direct3D 9 and 10 and OpenGL games. Pick the whole unzipped add-on folder in Settings.");
            // PlaceReShade keeps an add-on ReShade of the game's bitness (or an add-on ASI one), whatever its version.
            bool keeps = reshadeThen.Installed && reshadeThen.AddonSupport && (reshadeThen.IsAsi || g.ReShadeAfterRestoreBits == bits);
            var kept = keeps ? reshadeThen.Version : bits == 64 ? c.ReShade64Version : c.ReShade32Version;
            if (OlderThanLecramNeeds(kept))
                return Out(keeps
                    ? $"lecram's DLSS5 NR needs ReShade 6.8 or newer, and this game would keep its ReShade {kept}. Remove that ReShade on the game's page first."
                    : $"lecram's DLSS5 NR needs ReShade 6.8 or newer, and the {bits}-bit ReShade DLSS 5 Master installs is {kept}. Pick ReShade 6.8 or newer in Settings.");
        }

        // The page's defaults.
        var hook = m?.OptiScalerHook is { } recorded && Installers.OptiHookNames.Contains(recorded) ? recorded
                 : api == RenderApi.Vulkan ? "winmm.dll" : "dxgi.dll";
        var wantedKey = g.OptiMenuKey ?? c.SavedMenuKey;
        var key = Installers.MenuKeys.FirstOrDefault(k => string.Equals(k.Code, wantedKey, StringComparison.OrdinalIgnoreCase)).Code
                  ?? Installers.MenuKeys[0].Code;
        var proxy = string.Equals(m?.ReShadeHook, "d3d11.dll", StringComparison.OrdinalIgnoreCase) ? "d3d11.dll" : "dxgi.dll";
        bool mfg = reshade && (m is not null ? m.MfgUnlock is not null : o.Mfg) && Installers.MfgUnavailable(api, bits, scan.HasFrameGen) is null;
        var req = new InstallRequest
        {
            GameDir = scan.Dir, ExePath = exe.Path, Api = api, Bitness = bits, Route = o.Route,
            OptiBuild = o.Build, OptiHook = hook, MenuKey = key, ReShadeProxy = proxy, MfgUnlock = mfg,
            LoadAddonsEarly = g.Choices?.LoadAddonsEarly == true,
            // Never acknowledged here: InstallAsync still refuses a game whose anti-cheat shows up only now.
            AntiCheatAcknowledged = false,
            LecramNrSource = lecram,
        };
        // These refusals would come after the setup had started changing the game: check them first.
        if (Conflict(req, reshadeThen, g.Loaders) is { } conflict) return Out(conflict);

        var notes = new List<string>();
        if (m is not null) notes.Add(ReinstallNote(m, scan.Dir, exeDir, o));
        else if (reshade && scan.ReShade.Installed) notes.Add($"ReShade {scan.ReShade.Version} that no app recorded is in it already");
        if (mfg) notes.Add("with the MFG unlock");
        var note = string.Join(" · ", notes);
        if (m is not null && !o.Again)
            return new BulkRow { Game = g, Skip = $"Set up already ({Installers.RouteLabel(m.Route)}).", Reinstall = true, NeedsAgain = true, Note = note };
        return new BulkRow { Game = g, Request = req, Reinstall = m is not null, Note = note };
    }

    /// <summary>
    /// What setting a game up again means for its in-game settings, by the rule Installers.KeepSettings follows: kept with the
    /// same route family, the same exe folder and, for OptiScaler, the same build; otherwise they start fresh.
    /// </summary>
    private static string ReinstallNote(Manifest m, string gameDir, string exeDir, BulkOptions o)
    {
        var was = Installers.RouteLabel(m.Route);
        // A setup first restores the record it finds, so a DLL swap is undone; other records keep no in-game settings.
        if (m.Route == "swap") return "Its swapped DLLs go back to the game's own first";
        if (m.Route is not ("native" or "multipass" or "feeder" or "optiscaler" or "lecramnr")) return $"Set up over {was}: in-game settings start fresh";
        string? dirBefore = null;
        try { if (m.Exe.Length > 0) dirBefore = Path.GetDirectoryName(Journal.SafePath(gameDir, m.Exe)); }
        catch (InstallException) { }
        if (dirBefore is null || !dirBefore.Equals(Path.GetFullPath(exeDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return "Set up again: its .exe is in another folder now, so in-game settings start fresh";
        static string Family(string key) => key == "lecramnr" ? "native" : key;
        if (Family(m.Route) != Family(Installers.RouteKey(o.Route))) return $"Switches from {was}: in-game settings start fresh";
        if (o.Route == Route.OptiScaler && m.OptiScalerBuild != o.Build.Id) return "Set up again with another OptiScaler build: in-game settings start fresh";
        return "Set up again: in-game settings are kept";
    }

    /// <summary>
    /// Why the setup would refuse the files beside the exe partway, after it had started changing the game (PlaceReShade and
    /// RunOptiScaler check the same, on the folder as a reinstall's restore leaves it); null when nothing is in the way. The
    /// game's page is offered as a way round only when the page's choice would get past every file in the way.
    /// </summary>
    /// <param name="reshade">The ReShade the setup finds beside the exe then (<see cref="BulkGame.ReShadeAfterRestore"/>).</param>
    internal static string? Conflict(InstallRequest req, ReShadeState reshade, IReadOnlyList<LoaderFile> loaders)
    {
        if (req.Route != Route.OptiScaler)
        {
            var hook = Installers.ReShadeHookFor(req);
            if (reshade.Installed && reshade.IsAsi)
            {
                if (req.Route == Route.Feeder)
                    return $"ReShade is loaded here as an ASI plugin ({reshade.File}), and the Feeder needs its own ReShade as {hook}. Remove the ASI ReShade first.";
                if (!reshade.AddonSupport)
                    return $"ReShade is loaded here as an ASI plugin ({reshade.File}) without add-on support. Replace it with the add-on build of ReShade, or remove it.";
                return null;   // kept as it is
            }
            // PlaceReShade refuses a file at the hook's name that is not ReShade, and OptiScaler under any of ReShade's names.
            static bool Blocks(LoaderFile f, string name) => ReShadeSlots.Contains(f.Name, StringComparer.OrdinalIgnoreCase) && !f.ReShade
                && (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || f.OptiScaler);
            if (loaders.FirstOrDefault(f => Blocks(f, hook)) is not { } blocker) return null;
            // Only in a DirectX 11 game can ReShade load under the other name (the page's choice), and only if nothing blocks that.
            var alternative = hook.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) ? "d3d11.dll" : "dxgi.dll";
            var other = req.Api == RenderApi.DX11 && !loaders.Any(f => Blocks(f, alternative)) ? alternative : null;
            return $"{blocker.Name} next to the game belongs to another mod (for example OptiScaler or a wrapper). Remove it, or restore it in the tool that installed it" +
                   (other is null ? "." : $", or have ReShade load as {other} on the game's page.");
        }
        // RunOptiScaler refuses every proxy DLL or ASI plugin that is not ReShade, whatever OptiScaler's own name; a ReShade under
        // that name only needs another name, which the page offers, so it is named only when nothing else is in the way.
        LoaderFile? clash = null;
        foreach (var f in loaders)
        {
            bool candidate = Path.GetExtension(f.Name).Equals(".asi", StringComparison.OrdinalIgnoreCase)
                             || Installers.OptiConflictStems.Contains(Path.GetFileNameWithoutExtension(f.Name));
            if (!candidate) continue;
            if ((f.Name.Equals("dbghelp.dll", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("dbgcore.dll", StringComparison.OrdinalIgnoreCase)) && f.Microsoft) continue;
            if (f.ReShade)
            {
                if (f.Name.Equals(req.OptiHook, StringComparison.OrdinalIgnoreCase)) clash ??= f;
                continue;
            }
            return $"{f.Name} next to the game belongs to another mod or loader (maybe an older OptiScaler). Remove it, or restore it in the tool that installed it.";
        }
        return clash is null ? null : $"{clash.Name} is ReShade, with the same name as the OptiScaler DLL. Choose another OptiScaler DLL name on the game's page.";
    }
}
