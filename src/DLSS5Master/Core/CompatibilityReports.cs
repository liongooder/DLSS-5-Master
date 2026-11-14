namespace DLSS5Master.Core;

public static class CompatibilityReports
{
    public static List<CommunityReport> Filter(IEnumerable<CommunityReport> reports, string? route = null, string? gpu = null,
        string? driver = null, string? result = null) => reports.Where(r => !r.Hidden && Match(route, r.Route) &&
            Match(gpu, r.Gpu.Length > 0 ? r.Gpu : r.GpuFamily) && Match(driver, r.Driver) && Match(result, r.Rating))
            .OrderByDescending(r => r.When).ToList();
    private static bool Match(string? filter, string value) => filter is null || string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);
}
