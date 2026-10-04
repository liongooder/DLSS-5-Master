using System.Text.Json;

namespace DLSS5Master.Core;

/// <summary>One install made in a game by another tool, as described by that tool's own install record.</summary>
public sealed class ForeignInstall
{
    public required string Tool { get; init; }               // "backup" (per-game manifest) or "records" (per-game JSON records)
    public required string Record { get; init; }             // the record file
    public DateTime Date { get; init; }
    /// <summary>(target file, backup to copy back) for files that existed before the other tool replaced them.</summary>
    public List<(string Target, string Backup)> Restore { get; } = new();
    /// <summary>Files the other tool added; deleted on removal.</summary>
    public List<string> Delete { get; } = new();
    /// <summary>Folders that may have been created by the other tool; removed only if empty.</summary>
    public List<string> Dirs { get; } = new();
    /// <summary>Why this install cannot be removed safely here, or null.</summary>
    public string? Problem { get; set; }
}

/// <summary>
/// Reads other tools' install records for a game and undoes those installs: replaced files are copied back from the
/// tool's own backups, added files are deleted, and the tool's record is retired so it no longer lists the game.
/// Formats handled:
///  • a per-game "_DLSS5_Backup\manifest.json" (version 1: backupPrefix, replaced[{rel}], added[rel], addedDirs[rel],
///    game.exe, reshade{installedByUs, file, filesAdded[]}, optional vulkanLayer);
///  • per-game JSON records in %LOCALAPPDATA%\DLSS5Manager\Installs (ExecutablePath, InstalledAtUtc,
///    Files[{TargetPath, BackupPath, WasExisting}]).
/// </summary>
public static class ForeignInstalls
{
    /// <summary>Where the second format's records live. Settable for tests.</summary>
    public static string OtherAppRecordsDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager", "Installs");

    private static string Root(string gameDir) => Path.GetFullPath(gameDir).TrimEnd('\\') + "\\";

    /// <summary>
    /// The game folder a second-format record was made in, given the executable it names, when that record belongs to
    /// gameDir: the same folder, or the same game moved elsewhere (a folder with the same name that holds the same
    /// executable at the same place, e.g. after Steam moved the game to another library). Null when it is another game.
    /// </summary>
    public static string? RecordedRoot(string exe, string gameDir)
    {
        var root = Root(gameDir);
        var full = Path.GetFullPath(exe);
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return root;
        var name = Path.GetFileName(root.TrimEnd('\\'));
        for (var dir = Path.GetDirectoryName(full); dir is not null; dir = Path.GetDirectoryName(dir))
            if (Path.GetFileName(dir).Equals(name, StringComparison.OrdinalIgnoreCase))
                return File.Exists(Path.Combine(root, Path.GetRelativePath(dir, full))) ? dir.TrimEnd('\\') + "\\" : null;
        return null;
    }

    /// <summary>A path from a record made in recordedRoot, moved to the same place under gameDir.</summary>
    private static string Rebase(string path, string recordedRoot, string gameDir)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(recordedRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(gameDir, full[recordedRoot.Length..])) : full;
    }
    private static bool Inside(string path, string root) => Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);

    public static List<ForeignInstall> Find(string gameDir)
    {
        var list = new List<ForeignInstall>();
        if (ReadBackupManifest(gameDir) is { } a) list.Add(a);
        list.AddRange(ReadManagerRecords(gameDir));
        return list;
    }

    // ------------------------------------------------------------------ _DLSS5_Backup\manifest.json

    private static ForeignInstall? ReadBackupManifest(string gameDir)
    {
        var root = Root(gameDir);
        var backupDir = Path.Combine(gameDir, "_DLSS5_Backup");
        var file = Path.Combine(backupDir, "manifest.json");
        if (!File.Exists(file)) return null;
        var fi = new ForeignInstall { Tool = "backup", Record = file, Date = File.GetLastWriteTimeUtc(file) };
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            if (r.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String && DateTime.TryParse(d.GetString(), out var dt))
                fi = new ForeignInstall { Tool = fi.Tool, Record = file, Date = dt.ToUniversalTime() };
            if (r.TryGetProperty("vulkanLayer", out var vk) && vk.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                fi.Problem = "This install registered a Vulkan layer for all games. Remove it with the app that installed it.";
            var prefix = r.TryGetProperty("backupPrefix", out var bp) && bp.ValueKind == JsonValueKind.String ? bp.GetString() ?? "" : "";
            var originals = Path.Combine(backupDir, prefix.Replace('/', '\\'));

            string Full(string rel)
            {
                var full = Path.GetFullPath(Path.Combine(gameDir, rel));
                if (!Inside(full, root)) throw new InvalidDataException("path outside the game folder: " + rel);
                return full;
            }
            var replacedRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (r.TryGetProperty("replaced", out var rep) && rep.ValueKind == JsonValueKind.Array)
                foreach (var item in rep.EnumerateArray())
                    if (item.TryGetProperty("rel", out var rel) && rel.GetString() is { } relPath)
                    {
                        replacedRels.Add(relPath);
                        fi.Restore.Add((Full(relPath), Path.GetFullPath(Path.Combine(originals, relPath))));
                    }
            if (r.TryGetProperty("added", out var add) && add.ValueKind == JsonValueKind.Array)
                foreach (var item in add.EnumerateArray())
                    if (item.GetString() is { } relPath) fi.Delete.Add(Full(relPath));
            if (r.TryGetProperty("addedDirs", out var dirs) && dirs.ValueKind == JsonValueKind.Array)
                foreach (var item in dirs.EnumerateArray())
                    if (item.GetString() is { } relPath) fi.Dirs.Add(Full(relPath));

            // ReShade leftovers live next to the game's executable.
            string? exeRel = r.TryGetProperty("game", out var g) && g.TryGetProperty("exe", out var ex) ? ex.GetString() : null;
            if (exeRel is not null && r.TryGetProperty("reshade", out var rs) && rs.ValueKind == JsonValueKind.Object)
            {
                var exeDir = Path.GetDirectoryName(Full(exeRel))!;
                if (rs.TryGetProperty("filesAdded", out var fa) && fa.ValueKind == JsonValueKind.Array)
                    foreach (var item in fa.EnumerateArray())
                        if (item.GetString() is { } name) AddLeftover(fi, Path.Combine(exeDir, name), root, replacedRels, gameDir);
                if (rs.TryGetProperty("installedByUs", out var by) && by.ValueKind == JsonValueKind.True &&
                    rs.TryGetProperty("file", out var hook) && hook.GetString() is { } hookName)
                    AddLeftover(fi, Path.Combine(exeDir, hookName), root, replacedRels, gameDir);
            }
        }
        catch (Exception e) { fi.Problem = "Its install record could not be read (" + e.Message + ")."; }
        return fi;
    }

    private static void AddLeftover(ForeignInstall fi, string path, string root, HashSet<string> replacedRels, string gameDir)
    {
        var full = Path.GetFullPath(path);
        if (!Inside(full, root) || replacedRels.Contains(Path.GetRelativePath(gameDir, full))) return;
        if (!fi.Delete.Contains(full, StringComparer.OrdinalIgnoreCase)) fi.Delete.Add(full);
    }

    // ------------------------------------------------------------------ DLSS5Manager\Installs\*.json

    private static IEnumerable<ForeignInstall> ReadManagerRecords(string gameDir)
    {
        if (!Directory.Exists(OtherAppRecordsDir)) yield break;
        var root = Root(gameDir);
        foreach (var file in Directory.EnumerateFiles(OtherAppRecordsDir, "*.json"))
        {
            ForeignInstall? fi = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var exe = Str(r, "ExecutablePath");
                if (string.IsNullOrEmpty(exe) || RecordedRoot(exe, gameDir) is not { } recorded) continue;
                var date = DateTime.TryParse(Str(r, "InstalledAtUtc"), out var dt) ? dt.ToUniversalTime() : File.GetLastWriteTimeUtc(file);
                fi = new ForeignInstall { Tool = "records", Record = file, Date = date };
                if (r.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Array)
                    foreach (var f in files.EnumerateArray())
                    {
                        var target = Str(f, "TargetPath");
                        if (string.IsNullOrEmpty(target)) continue;
                        target = Rebase(target, recorded, gameDir);
                        if (!Inside(target, root)) { fi.Problem = "Its record lists a file outside the game folder: " + target; continue; }
                        var backup = Str(f, "BackupPath");
                        if (!string.IsNullOrEmpty(backup)) backup = Rebase(backup, recorded, gameDir);
                        bool existed = f.TryGetProperty("WasExisting", out var we) && we.ValueKind == JsonValueKind.True;
                        if (existed)
                        {
                            if (string.IsNullOrEmpty(backup)) fi.Problem = "Its record has no backup for the replaced file " + target;
                            else fi.Restore.Add((Path.GetFullPath(target), Path.GetFullPath(backup)));
                        }
                        else
                        {
                            fi.Delete.Add(Path.GetFullPath(target));
                            // Folders under the game folder that this file needed (e.g. OptiScaler\, Licenses\).
                            for (var dir = Path.GetDirectoryName(Path.GetFullPath(target)); dir is not null && Inside(dir + "\\", root) && dir.Length + 1 > root.Length; dir = Path.GetDirectoryName(dir))
                                if (!fi.Dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) fi.Dirs.Add(dir);
                        }
                    }
            }
            catch (Exception e)
            {
                fi ??= new ForeignInstall { Tool = "records", Record = file };
                fi.Problem = "Its install record could not be read (" + e.Message + ").";
            }
            if (fi is not null) yield return fi;
        }
    }

    // ------------------------------------------------------------------ ReShade that no tool recorded

    /// <summary>
    /// ReShade's own files next to the game's executable, for a ReShade install that no tool has a record of
    /// (put there by hand or by an older tool): the hook DLL (only when it identifies itself as ReShade), ReShade's
    /// .ini/.log files, the reshade-shaders folder, ReShade add-ons (*.addon64 / *.addon32) and nvngx_dlssnr.dll
    /// (the neural rendering runtime those add-ons load; games do not ship it). The game's own files are never listed.
    /// </summary>
    public static List<string> UnrecordedReShadeFiles(string exeDir, string hookFile)
    {
        var list = new List<string>();
        var hook = Path.Combine(exeDir, hookFile);
        if (!File.Exists(hook) || !Pe.VersionMentions(hook, "ReShade")) return list;
        list.Add(hook);
        foreach (var pattern in new[] { "ReShade*.ini", "ReShade*.log", "*.addon64", "*.addon32", "nvngx_dlssnr.dll" })
            list.AddRange(Directory.EnumerateFiles(exeDir, pattern));
        var shaders = Path.Combine(exeDir, "reshade-shaders");
        if (Directory.Exists(shaders)) list.Add(shaders);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Moves the files of an unrecorded ReShade install into _DLSS5Master_Backup\removed\&lt;time&gt;\ (same relative
    /// paths), so the game no longer loads ReShade and nothing is lost. Returns that folder.
    /// </summary>
    public static Task<string> MoveAsideUnrecordedReShadeAsync(string gameDir, string exePath, string hookFile, Action<string> log) => Task.Run(() =>
    {
        Installers.AssertGameClosed(exePath);
        var exeDir = Path.GetDirectoryName(exePath)!;
        var items = UnrecordedReShadeFiles(exeDir, hookFile);
        if (items.Count == 0) throw new InstallException("No ReShade found next to the game's executable. Nothing was changed.");
        var dest = Path.Combine(gameDir, Journal.BackupDir, "removed", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var root = Root(gameDir);
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var item in items)
            {
                var to = Path.Combine(dest, Path.GetRelativePath(root, item));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (Directory.Exists(item)) Directory.Move(item, to);
                else { File.SetAttributes(item, FileAttributes.Normal); File.Move(item, to); }
                moved.Add((item, to));
            }
        }
        catch (Exception e)
        {
            // Put back whatever was already moved, so the game is never left half done.
            foreach (var (from, to) in Enumerable.Reverse(moved))
                try { if (Directory.Exists(to)) Directory.Move(to, from); else File.Move(to, from); } catch { }
            throw new InstallException($"Could not move {Path.GetFileName(items[moved.Count])} ({e.Message}). Nothing was changed.");
        }
        File.WriteAllLines(Path.Combine(dest, "README.txt"), new[]
        {
            "DLSS 5 Master moved these ReShade files out of the game on " + DateTime.Now.ToString("g") + ".",
            "No app had a record of this ReShade install. To put it back, move the folders in here back into the game folder.",
            "",
        }.Concat(moved.Select(m => Path.GetRelativePath(root, m.From))));
        log($"Moved {moved.Count} ReShade item(s) out of the game into {Path.GetRelativePath(root, dest)}.");
        log("If the game's own DLSS files were changed by that install, use Steam's \"Verify integrity of game files\" to get the originals back.");
        AppPaths.Log($"[{Path.GetFileName(root.TrimEnd('\\'))}] moved aside unrecorded ReShade: {moved.Count} item(s) -> {dest}");
        return dest;
    });

    // ------------------------------------------------------------------ removal

    /// <summary>A short summary for the confirmation dialog.</summary>
    public static (int Restore, int Delete, string? Problem) Summary(string gameDir)
    {
        var all = Find(gameDir);
        return (all.Sum(f => f.Restore.Count), all.Sum(f => f.Delete.Count), all.Select(f => f.Problem).FirstOrDefault(p => p is not null) ?? MissingBackup(all));
    }

    private static string? MissingBackup(IEnumerable<ForeignInstall> installs)
    {
        foreach (var fi in installs)
            foreach (var (target, backup) in fi.Restore)
                if (!File.Exists(backup)) return $"The other app's backup of {Path.GetFileName(target)} is missing, so the original cannot be put back.";
        return null;
    }

    /// <summary>
    /// Undoes every other-app install in the game, newest first. Everything is checked before the first change;
    /// on any problem nothing is touched and an InstallException explains why.
    /// </summary>
    public static Task<int> RemoveAllAsync(string gameDir, string? exePath, Action<string> log) => Task.Run(() =>
    {
        if (exePath is not null) Installers.AssertGameClosed(exePath);
        var installs = Find(gameDir).OrderByDescending(f => f.Date).ToList();
        if (installs.Count == 0) { log("No other app install found in this game."); return 0; }
        if (installs.Select(f => f.Problem).FirstOrDefault(p => p is not null) is { } problem) throw new InstallException(problem);
        if (MissingBackup(installs) is { } missing) throw new InstallException(missing + " Nothing was changed.");

        foreach (var fi in installs)
        {
            int restored = 0, removed = 0;
            foreach (var (target, backup) in fi.Restore)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                File.Copy(backup, target, true);
                restored++;
            }
            var restoredTargets = new HashSet<string>(fi.Restore.Select(r => r.Target), StringComparer.OrdinalIgnoreCase);
            foreach (var file in fi.Delete)
            {
                if (restoredTargets.Contains(file) || !File.Exists(file)) continue;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                removed++;
            }
            foreach (var dir in fi.Dirs.OrderByDescending(d => d.Length))
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }

            // Retire the other tool's record so it no longer lists this game as installed.
            if (fi.Tool == "backup") File.Move(fi.Record, fi.Record + ".done-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            else File.Delete(fi.Record);
            log($"Removed another app's install: {restored} original file(s) put back, {removed} added file(s) deleted.");
            AppPaths.Log($"[{Path.GetFileName(gameDir.TrimEnd('\\'))}] removed other-app install ({fi.Tool}): {restored} restored, {removed} deleted");
        }
        return installs.Count;
    });
}
