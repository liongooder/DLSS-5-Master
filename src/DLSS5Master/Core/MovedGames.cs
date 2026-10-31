using System.Text.Json;

namespace DLSS5Master.Core;

/// <summary>
/// Keeps the choices made on a game's page (executable, rendering API, artwork, Load add-ons early) with the game when it
/// moves to another folder: Steam's "Move install folder", or another launcher moving it. The choices are saved per folder
/// and marked with the game they belong to (launcher and ID, an Xbox game by its name); when that game turns up in a folder
/// that did not hold it before and its old folder is no longer in the library, they move with it. Another copy of the game
/// (a folder that held it in the last read of the library, or whose choices are marked with it) is not where it went, and a
/// hidden game is still in its folder: their choices stay their own. A game added by hand or found in the user's own
/// folders is its folder, so its choices stay where they are.
/// </summary>
public static class MovedGames
{
    /// <summary>
    /// True for a game that can be recognised in another folder: a Steam, Epic Games, GOG or Ubisoft game with its ID,
    /// or an Xbox game (by its name).
    /// </summary>
    internal static bool Followable(GameEntry g) => g.Launcher switch
    {
        "Steam" or "Epic Games" or "GOG" or "Ubisoft" => !string.IsNullOrWhiteSpace(g.Id),
        "Xbox" => !string.IsNullOrWhiteSpace(g.Name),
        _ => false,
    };

    /// <summary>Marks the choices with the game they belong to, when it is one that can be followed.</summary>
    internal static void Stamp(GameOverride o, GameEntry g)
    {
        if (!Followable(g)) return;
        o.Launcher = g.Launcher;
        o.Id = g.Id;
        o.Name = g.Name;
    }

    /// <summary>
    /// Moves the choices of games that are now in another folder, and brings together and marks the choices of the games
    /// read. <paramref name="before"/> is the library as last read (at start, as the last run read it: <see cref="LastLibrary"/>),
    /// <paramref name="now"/> the library just read. Choices whose game is not found stay where they are: a folder that is
    /// gone may be on a drive that is not connected. True when the choices changed (they need saving).
    /// </summary>
    public static bool Follow(AppSettings s, IReadOnlyList<GameEntry> before, IReadOnlyList<GameEntry> now, Action<string> log)
    {
        var here = Folders(now.Select(g => g.Dir));
        var hidden = Folders(s.HiddenGames);
        // A folder that left the library; a game the user hid is still in its folder.
        bool Gone(string dir) => FolderKey(dir) is { } key && !here.Contains(key) && !(hidden.Contains(key) && Directory.Exists(dir));

        // The games that can be followed, by who they are; a game found twice (two copies) is followed nowhere.
        var byIdentity = new Dictionary<string, GameEntry?>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in now.Where(Followable))
            if (Identity(g.Launcher, g.Id, g.Name) is { } id) byIdentity[id] = byIdentity.ContainsKey(id) ? null : g;
        // Where a game went: its one folder now, when that folder is new to it. One that held it in the last read, or whose
        // choices are marked with it already, holds another copy of it: the copy that stays when the other is hidden,
        // uninstalled or on a drive that is not connected.
        var lastRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in before)
            if (Place(g) is { } place) lastRead.Add(place);
        bool Arrived(GameEntry g) => Place(g) is { } place && !lastRead.Contains(place);
        bool Marked(GameEntry g, string identity) =>
            s.OverrideOf(g) is { } o && string.Equals(Identity(o.Launcher, o.Id, o.Name), identity, StringComparison.OrdinalIgnoreCase);
        GameEntry? WentTo(string? identity) =>
            identity is not null && byIdentity.GetValueOrDefault(identity) is { } to && Arrived(to) && !Marked(to, identity) ? to : null;

        // Moved while the app was open, or since the last run: a game of the last read whose folder left the library.
        var left = new List<(string Dir, GameEntry To)>();
        foreach (var g in before.Where(Followable))
            if (WentTo(Identity(g.Launcher, g.Id, g.Name)) is { } to && Gone(g.Dir)) left.Add((g.Dir, to));
        // Moved while the app was closed: choices marked with a game whose folder left the library, unless a game of the
        // last read went there (then these are a gone copy's).
        var marked = new List<(string Dir, GameEntry To)>();
        foreach (var (dir, o) in s.Overrides)
            if (o.Launcher is not null && WentTo(Identity(o.Launcher, o.Id, o.Name)) is { } to && !left.Any(m => m.To == to) && Gone(dir))
                marked.Add((dir, to));
        bool changed = false;
        foreach (var (dir, to) in OneEach(left).Concat(OneEach(marked)).ToList()) changed |= Move(s, dir, to, log);

        // Choices saved by an older version, before they were marked with their game: a Steam game whose folder is gone, now
        // in another Steam library under the same folder name, when nothing else fits.
        var legacy = new List<(string Dir, GameEntry To)>();
        foreach (var (dir, o) in s.Overrides)
        {
            if (o.Launcher is not null || !Gone(dir) || SteamFolderName(dir) is not { } name) continue;
            var same = now.Where(g => g.Launcher == "Steam" && string.Equals(SteamFolderName(g.Dir), name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (same.Count != 1 || !Arrived(same[0]) || s.OverrideOf(same[0]) is not null || Directory.Exists(dir)) continue;
            if (o.Exe is not null && RebaseExe(o.Exe, dir, same[0].Dir) is null) continue;
            legacy.Add((dir, same[0]));
        }
        foreach (var (dir, to) in OneEach(legacy).ToList()) changed |= Move(s, dir, to, log);

        // The games just read. Choices an older version saved under two spellings of one folder (as the launcher gives it and
        // as the app writes it) come together, those under the launcher's winning as the page showed them; and they are
        // marked with who they are, so they can follow them next time.
        var spellings = s.Overrides.Keys.Select(k => (Key: k, Folder: FolderKey(k))).Where(k => k.Folder is not null)
                                        .ToLookup(k => k.Folder!, k => k.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var g in now)
        {
            if (FolderKey(g.Dir) is not { } folder) continue;
            var keys = spellings[folder].Where(s.Overrides.ContainsKey)
                                        .OrderByDescending(k => k.Equals(g.Dir, StringComparison.OrdinalIgnoreCase)).ToList();
            if (keys.Count == 0) continue;
            var o = s.Overrides[keys[0]];
            foreach (var key in keys.Skip(1))
            {
                o.MergeFrom(s.Overrides[key]);
                s.Overrides.Remove(key);
                changed = true;
            }
            if (!Followable(g) || (o.Launcher == g.Launcher && o.Id == g.Id && o.Name == g.Name)) continue;
            Stamp(o, g);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// The library as the last run read it last (<see cref="AppPaths.LibraryCache"/>), the <c>before</c> of a run's first
    /// <see cref="Follow"/>; empty when there is none or it cannot be read.
    /// </summary>
    public static List<GameEntry> LastLibrary()
    {
        try
        {
            if (File.Exists(AppPaths.LibraryCache))
                return (JsonSerializer.Deserialize<List<GameEntry?>>(File.ReadAllText(AppPaths.LibraryCache)) ?? new()).OfType<GameEntry>().ToList();
        }
        catch { }
        return new();
    }

    /// <summary>Keeps the library just read for the next run (<see cref="LastLibrary"/>).</summary>
    public static void KeepLibrary(IReadOnlyList<GameEntry> games)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = AppPaths.LibraryCache + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(games));
        File.Move(tmp, AppPaths.LibraryCache, true);
    }

    /// <summary>
    /// The moves that are the only one into their folder: when two old folders lead to one game, which of them held it is
    /// not known, so neither moves.
    /// </summary>
    private static IEnumerable<(string Dir, GameEntry To)> OneEach(List<(string Dir, GameEntry To)> moves) =>
        moves.Where(m => !moves.Any(x => x.To == m.To && !SameFolder(x.Dir, m.Dir)));

    /// <summary>Moves the choices saved for folder <paramref name="oldDir"/>, under any spelling of it, to the game <paramref name="to"/>.</summary>
    private static bool Move(AppSettings s, string oldDir, GameEntry to, Action<string> log)
    {
        // The spelling the page used comes first: its choices win over ones saved under another spelling of the folder.
        var keys = s.Overrides.Keys.Where(k => SameFolder(k, oldDir))
                                   .OrderByDescending(k => k.Equals(oldDir, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keys.Count == 0) return false;   // nothing saved for it, or moved already
        GameOverride Take(string key)
        {
            var o = s.Overrides[key];
            s.Overrides.Remove(key);
            o.Exe = RebaseExe(o.Exe, key, to.Dir);
            return o;
        }
        var moved = Take(keys[0]);
        foreach (var key in keys.Skip(1)) moved.MergeFrom(Take(key));
        // Choices already made in the new folder win; the moved ones fill in what those leave open.
        if (s.OverrideOf(to) is { } there)
        {
            there.MergeFrom(moved);
            moved = there;
        }
        else s.Overrides[to.Dir] = moved;
        Stamp(moved, to);
        log($"Kept the page choices for {to.Name}: it moved from {oldDir} to {to.Dir}.");
        return true;
    }

    /// <summary>
    /// The picked executable of a game that moved from <paramref name="oldDir"/> to <paramref name="newDir"/>: the same file
    /// in the new folder when it was inside the old one, the same path when it was elsewhere; null when there was none or
    /// that file is not there (the automatic choice then applies).
    /// </summary>
    internal static string? RebaseExe(string? exe, string oldDir, string newDir)
    {
        if (exe is null) return null;
        try
        {
            var rel = Path.GetRelativePath(oldDir, exe);
            bool inside = rel is not ("." or "..") && !rel.StartsWith(@"..\", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
            var path = inside ? Path.GetFullPath(Path.Combine(newDir, rel)) : exe;
            return File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    /// <summary>A folder as <see cref="Library.Normalize"/> writes it (full, no trailing \ or /, lower case), or null when it is not a path.</summary>
    internal static string? FolderKey(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try { return Library.Normalize(dir); }
        catch { return null; }
    }

    /// <summary>The folders as <see cref="FolderKey"/> writes them, leaving out what is not a path.</summary>
    private static HashSet<string> Folders(IEnumerable<string?> dirs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
            if (FolderKey(dir) is { } key) set.Add(key);
        return set;
    }

    private static bool SameFolder(string a, string b) =>
        FolderKey(a) is { } key && string.Equals(key, FolderKey(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Who a game is: "launcher|ID", for an Xbox game "Xbox|name"; null when that is not known.</summary>
    private static string? Identity(string? launcher, string? id, string? name)
    {
        var who = string.IsNullOrWhiteSpace(id) ? name : id;
        return string.IsNullOrWhiteSpace(launcher) || string.IsNullOrWhiteSpace(who) ? null : launcher + "|" + who;
    }

    /// <summary>
    /// A game that can be followed in its folder: "launcher|ID|folder" (a folder has no "|"); null for one that cannot be
    /// followed, or that is not in a folder.
    /// </summary>
    private static string? Place(GameEntry g) =>
        Followable(g) && Identity(g.Launcher, g.Id, g.Name) is { } id && FolderKey(g.Dir) is { } key ? id + "|" + key : null;

    /// <summary>"name" for a game's folder in a Steam library (...\steamapps\common\name), else null.</summary>
    private static string? SteamFolderName(string dir)
    {
        const string common = @"\steamapps\common\";
        if (FolderKey(dir) is not { } key) return null;
        int at = key.LastIndexOf(common, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var name = key[(at + common.Length)..];
        return name.Length > 0 && !name.Contains('\\') ? name : null;
    }
}
