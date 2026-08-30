using System.ComponentModel;
using System.Windows;

namespace HTunes.App;

public partial class DependencySetupWindow : Window
{
    private readonly IReadOnlyList<ExternalTool> tools;
    private bool isDownloading;

    internal DependencySetupWindow(IReadOnlyList<ToolIssue> issues)
    {
        InitializeComponent();
        tools = issues.Select(issue => issue.Tool).Distinct().ToList();
        var hasMissing = issues.Any(issue => issue.Kind == ToolIssueKind.Missing);
        var isManualUpdate = issues.All(issue => issue.Kind == ToolIssueKind.Reinstall);
        HeadingText.Text = isManualUpdate ? "Update hTunes tools" : hasMissing ? "Some hTunes tools are missing" : "Tool updates are available";
        DownloadButton.Content = isManualUpdate ? "Update both tools" : hasMissing && issues.All(issue => issue.Kind == ToolIssueKind.Missing) ? "Download missing tools" : "Install updates";
        MissingToolsList.ItemsSource = issues.Select(issue => issue.Tool switch
        {
            ExternalTool.FFmpeg => new { Name = ToolName("FFmpeg", issue.Kind), Detail = "Needed for sync-time transcoding and audio conversion." },
            ExternalTool.YtDlp => new { Name = ToolName("yt-dlp", issue.Kind), Detail = "Needed for the upcoming music-download feature." },
            _ => new { Name = issue.Tool.ToString(), Detail = "Needed for an optional hTunes feature." }
        }).ToList();
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        isDownloading = true;
        DownloadButton.IsEnabled = ContinueButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        var progress = new Progress<ToolDownloadProgress>(update =>
        {
            ProgressText.Text = update.Message;
            DownloadProgress.IsIndeterminate = update.Percent is null;
            if (update.Percent is double percent) DownloadProgress.Value = percent;
        });
        try
        {
            await ToolDependencyManager.DownloadMissingAsync(tools, progress);
            isDownloading = false;
            MessageBox.Show(this, "The missing tools are ready to use.", "Tools installed", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            isDownloading = false;
            ProgressText.Text = "The tools were not installed.";
            DownloadProgress.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, $"hTunes could not download the missing tools. You can continue using the rest of the app and try again next time.\n\n{ex.GetBaseException().Message}", "Download failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            DownloadButton.IsEnabled = ContinueButton.IsEnabled = true;
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string ToolName(string name, ToolIssueKind kind) => kind switch
    {
        ToolIssueKind.Missing => $"{name} — missing",
        ToolIssueKind.UpdateAvailable => $"{name} — update available",
        _ => name
    };

    protected override void OnClosing(CancelEventArgs e)
    {
        if (isDownloading) e.Cancel = true;
        base.OnClosing(e);
    }
}
