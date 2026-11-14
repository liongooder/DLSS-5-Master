namespace DLSS5Master.Core;

public sealed record PlannedCopy(string RelativePath, string? InstalledVersion, string? SelectedVersion, long Bytes);
public sealed record InstallPreview(IReadOnlyList<PlannedCopy> Copies, long RequiredBytes, long BackupBytes,
    IReadOnlyList<string> Settings, long RollbackBytes = 0)
{
    public string Describe() => $"{Copies.Count} planned copies · {RequiredBytes / 1048576.0:N1} MB file/backup estimate · {BackupBytes / 1048576.0:N1} MB originals\n" +
        $"Rollback reserve: {RollbackBytes / 1048576.0:N1} MB in local app data. The installer can keep an existing newer runtime. Small generated settings are additional.\n" +
        string.Join("\n", Copies.Select(f => $"{f.RelativePath}   {f.InstalledVersion ?? "absent / unversioned"} → {f.SelectedVersion ?? "unversioned"}   ({f.Bytes / 1048576.0:N2} MB)")) +
        "\nSettings merged/generated: " + string.Join(", ", Settings);
}
