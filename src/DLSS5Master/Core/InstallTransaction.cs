using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DLSS5Master.Core;

/// <summary>Write-ahead snapshots of only the files this operation changes, including its original-file journal.</summary>
internal sealed class InstallTransaction : IDisposable
{
    private static readonly AsyncLocal<InstallTransaction?> Current = new();
    internal sealed record Entry(string Rel, bool Existed, FileAttributes Attributes, DateTime Written);
    internal sealed class State
    {
        public string Game { get; set; } = "";
        public List<Entry> Files { get; set; } = new();
        public List<string> Directories { get; set; } = new();
    }
    private readonly State _state;
    private readonly string _folder;
    private readonly FileStream _lease;
    private InstallTransaction(string folder, State state, FileStream lease) { _folder = folder; _state = state; _lease = lease; }
    internal static string Key(string game) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(game).TrimEnd('\\').ToUpperInvariant())));
    private static string Folder(string game) => Path.Combine(AppPaths.Root, "install-transactions", Key(game));
    internal static bool Pending(string game) => File.Exists(Path.Combine(Folder(game), "pending.json"));
    internal static InstallTransaction Begin(string game)
    {
        if (Current.Value is not null) throw new InstallException("A file transaction is already running.");
        var folder = Folder(game);
        Directory.CreateDirectory(folder);
        var lease = new FileStream(Path.Combine(folder, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (Pending(game)) throw new InstallException("An unfinished file transaction needs recovery before another install.");
            var t = new InstallTransaction(folder, new State { Game = Path.GetFullPath(game) }, lease);
            t.Save();
            Current.Value = t;
            return t;
        }
        catch { lease.Dispose(); throw; }
    }
    private void Save()
    {
        var path = Path.Combine(_folder, "pending.json");
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, _state);
            stream.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
    }
    internal static void Capture(string file) => Current.Value?.Remember(file);
    internal static void CheckPath(string game, string file)
    {
        var rel = Path.GetRelativePath(game, file);
        var safe = Journal.SafePath(game, rel);
        for (string? p = safe; p is not null && p.Length > Path.GetFullPath(game).TrimEnd('\\').Length; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InstallException("A link or junction cannot be changed by this operation: " + rel);
        if ((File.GetAttributes(game) & FileAttributes.ReparsePoint) != 0) throw new InstallException("The game folder is a link or junction.");
    }
    private void Remember(string file)
    {
        CheckPath(_state.Game, file);
        var rel = Path.GetRelativePath(_state.Game, file);
        if (_state.Files.Any(e => e.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase))) return;
        bool exists = File.Exists(file);
        var entry = new Entry(rel, exists, exists ? File.GetAttributes(file) : FileAttributes.Normal,
            exists ? File.GetLastWriteTimeUtc(file) : default);
        if (exists) File.Copy(file, Path.Combine(_folder, _state.Files.Count.ToString()), true);
        for (var dir = Path.GetDirectoryName(file); dir is not null && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir))
        {
            var d = Path.GetRelativePath(_state.Game, dir);
            _ = Journal.SafePath(_state.Game, d);
            if (!_state.Directories.Contains(d, StringComparer.OrdinalIgnoreCase)) _state.Directories.Add(d);
        }
        _state.Files.Add(entry);
        Save(); // durable record before the caller writes
    }
    internal static void Recover(string game)
    {
        var folder = Folder(game);
        var path = Path.Combine(folder, "pending.json");
        if (!File.Exists(path)) return;
        using var lease = new FileStream(Path.Combine(folder, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(path)) return; // another process may have committed before the lock was acquired
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? throw new InstallException("Unreadable rollback record.");
        if (!Path.GetFullPath(game).Equals(state.Game, StringComparison.OrdinalIgnoreCase)) throw new InstallException("Rollback record belongs to another folder.");
        new InstallTransaction(folder, state, lease).Rollback();
    }
    internal void Rollback()
    {
        Current.Value = null;
        // Validate every snapshot first. A failed recovery retains all snapshots for retry.
        foreach (var (entry, i) in _state.Files.Select((e, i) => (e, i)))
        {
            CheckPath(_state.Game, Journal.SafePath(_state.Game, entry.Rel));
            if (entry.Existed && !File.Exists(Path.Combine(_folder, i.ToString()))) throw new InstallException("A rollback snapshot is missing.");
        }
        for (int i = _state.Files.Count - 1; i >= 0; i--)
        {
            var e = _state.Files[i];
            var target = Journal.SafePath(_state.Game, e.Rel);
            if (e.Existed && File.Exists(target) && new FileInfo(target).Length == new FileInfo(Path.Combine(_folder, i.ToString())).Length &&
                Components.Sha256(target) == Components.Sha256(Path.Combine(_folder, i.ToString())))
            {
                if (File.GetAttributes(target) != e.Attributes) File.SetAttributes(target, e.Attributes);
                continue;
            }
            Journal.ClearReadOnly(target);
            if (e.Existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(_folder, i.ToString()), target, true);
                File.SetLastWriteTimeUtc(target, e.Written);
                File.SetAttributes(target, e.Attributes);
            }
            else if (File.Exists(target)) File.Delete(target);
        }
        foreach (var rel in _state.Directories.OrderByDescending(s => s.Length))
        {
            var dir = Journal.SafePath(_state.Game, rel);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        Commit();
    }
    internal void Commit()
    {
        // Removing the durable marker commits; leftover snapshots are harmless.
        File.Delete(Path.Combine(_folder, "pending.json"));
        Current.Value = null;
        // Keep the lock path stable across processes; remove only this operation's numbered snapshots while holding it.
        for (int i = 0; i < _state.Files.Count; i++)
        {
            var file = Path.Combine(_folder, i.ToString());
            try { Journal.ClearReadOnly(file); File.Delete(file); } catch { }
        }
        _lease.Dispose();
    }
    public void Dispose() { if (Current.Value == this) Current.Value = null; _lease.Dispose(); }
}
