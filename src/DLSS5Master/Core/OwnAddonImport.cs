using System.IO.Compression;

namespace DLSS5Master.Core;

/// <summary>Imports the player's supported add-on without modifying the source or any game.</summary>
public static class OwnAddonImport
{
    public static string Prepare(string source, string? cacheRoot = null)
    {
        if (Directory.Exists(source))
            return LecramNr.ResolveFolder(source)
                ?? throw new InstallException("No dlss5-nr.addon64 was found. Choose the extracted DLSS5 NR add-on folder.");
        if (!File.Exists(source) || !Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InstallException("Choose an add-on folder or a ZIP file.");

        var root = Path.GetFullPath(cacheRoot ?? Path.Combine(AppPaths.Root, "own-addon-imports"));
        var imported = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(imported);
        try
        {
            // ExtractToDirectory rejects entries that escape the destination.
            // A unique directory keeps incomplete imports out of the active copy.
            ZipFile.ExtractToDirectory(source, imported);
            var copy = LecramNr.ResolveFolder(imported);
            if (copy is null)
            {
                // ZIPs can contain more than one enclosing release folder.
                var candidates = Directory.EnumerateFiles(imported, LecramNr.Addon64, SearchOption.AllDirectories)
                    .Select(f => Path.GetDirectoryName(f)!)
                    .Where(d => !Path.GetFileName(d).Equals(LecramNr.HostFolder, StringComparison.OrdinalIgnoreCase))
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
}
