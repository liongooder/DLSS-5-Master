using System.IO.Compression;

namespace DLSS5Master.Core;

/// <summary>Imports the player's supported add-on without modifying the source or any game.</summary>
public static class OwnAddonImport
{
    public static string Prepare(string source, string? cacheRoot = null)
    {
        bool folder = Directory.Exists(source);
        if (folder)
            source = NrAddon.ResolveFolder(source)
                ?? throw new InstallException("No dlss5-nr.addon64 was found. Choose the extracted DLSS5 NR add-on folder.");
        if (!folder && (!File.Exists(source) || !Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
            throw new InstallException("Choose an add-on folder or a ZIP file.");

        var root = Path.GetFullPath(cacheRoot ?? Path.Combine(AppPaths.Root, "own-addon-imports"));
        if (folder)
        {
            source = Path.GetFullPath(source);
            // Already imported copies remain usable without growing the cache.
            if (source.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return source;
            if (root.Equals(source, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InstallException("Choose the add-on release folder, not a parent of the import cache.");
        }
        var imported = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(imported);
        try
        {
            // ExtractToDirectory rejects entries that escape the destination.
            // A unique directory keeps incomplete imports out of the active copy.
            if (folder) CopyFolder(source, imported);
            else ZipFile.ExtractToDirectory(source, imported);
            var copy = NrAddon.ResolveFolder(imported);
            if (copy is null)
            {
                // ZIPs can contain more than one enclosing release folder.
                var candidates = Directory.EnumerateFiles(imported, NrAddon.Addon64, SearchOption.AllDirectories)
                    .Select(f => Path.GetDirectoryName(f)!)
                    .Where(d => !Path.GetFileName(d).Equals(NrAddon.HostFolder, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (candidates.Count == 1) copy = candidates[0];
                else if (candidates.Count > 1)
                    throw new InstallException("This ZIP contains several add-on copies. Extract it and choose the folder you want.");
            }
            return copy ?? throw new InstallException("This ZIP does not contain dlss5-nr.addon64. Choose the DLSS5 NR add-on ZIP.");
        }
        catch
        {
            // Remove only the unique extraction directory owned by this import.
            var full = Path.GetFullPath(imported);
            if (full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(full, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static void CopyFolder(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InstallException("Choose an add-on folder containing ordinary files, without symbolic links or junctions.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attrs = File.GetAttributes(entry);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new InstallException("The add-on folder contains a symbolic link or junction. Choose an extracted release with ordinary files.");
            var dest = Path.Combine(target, Path.GetFileName(entry));
            if ((attrs & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(dest);
                CopyFolder(entry, dest);
            }
            else File.Copy(entry, dest);
        }
    }
}
