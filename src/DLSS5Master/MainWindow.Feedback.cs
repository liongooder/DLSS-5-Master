using System.Diagnostics;
using Microsoft.UI.Xaml;
using DLSS5Master.Core;

namespace DLSS5Master;

/// <summary>About → Feedback: opens a pre-filled GitHub issue in the browser.</summary>
public sealed partial class MainWindow
{
    /// <summary>The project's GitHub repository. Issues are opened here.</summary>
    public const string RepoUrl = "https://github.com/Liongooder/DLSS-5-Master";

    private void ReportProblem_Click(object sender, RoutedEventArgs e) =>
        OpenIssue("bug", "[Bug] ",
            "**Game:** \n\n**Route / OptiScaler build:** \n\n**What happened:** \n\n**What you expected:** \n\n" + SystemInfo() +
            "\n\n_Attach activity.log if you can (About → Open log folder)._");

    private void SuggestFeature_Click(object sender, RoutedEventArgs e) =>
        OpenIssue("enhancement", "[Idea] ", "**Your idea:** \n\n**Why it would help:** \n\n" + SystemInfo());

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
    }

    /// <summary>Only the app version, Windows build and GPU names: nothing that identifies the user.</summary>
    private static string SystemInfo() =>
        "---\n" +
        $"DLSS 5 Master {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n" +
        $"Windows {Environment.OSVersion.Version}\n" +
        $"GPU: {(Gpu.Names.Count > 0 ? string.Join(", ", Gpu.Names) : "unknown")}";

    private static void OpenIssue(string label, string title, string body)
    {
        var url = $"{RepoUrl}/issues/new?labels={label}&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
