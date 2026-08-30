using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace HTunes.App;

public partial class MainWindow
{
    private bool isDownloadView;
    private bool isYtDownloading;
    private CancellationTokenSource? ytDownloadCancellation;
    private readonly StringBuilder ytConsoleBuffer = new();
    private int ytImportedCount;
    private int ytImportFailures;

    private async void DownloadLinks_Click(object sender, RoutedEventArgs e)
    {
        if (isYtDownloading || StartupCheckInProgress || !ContextActionsAvailable) return;
        IReadOnlyList<string> links;
        try { links = YtDlpDownloadService.ParseLinks(DownloadLinksBox.Text); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check download links"); return; }

        var yt = ToolDependencyManager.FindExecutable("yt-dlp.exe", "YTDLP_PATH");
        var ffmpeg = ToolDependencyManager.FindExecutable("ffmpeg.exe", "FFMPEG_PATH");
        var issues = new List<ToolIssue>();
        if (yt is null) issues.Add(new(ExternalTool.YtDlp, ToolIssueKind.Missing));
        if (ffmpeg is null || !File.Exists(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe")))
            issues.Add(new(ExternalTool.FFmpeg, ffmpeg is null ? ToolIssueKind.Missing : ToolIssueKind.Reinstall));
        if (issues.Count > 0)
        {
            new DependencySetupWindow(issues) { Owner = this }.ShowDialog();
            yt = ToolDependencyManager.FindExecutable("yt-dlp.exe", "YTDLP_PATH");
            ffmpeg = ToolDependencyManager.FindExecutable("ffmpeg.exe", "FFMPEG_PATH");
            if (yt is null || ffmpeg is null || !File.Exists(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe")))
            { MessageBox.Show(this, "Downloading requires yt-dlp, FFmpeg, and ffprobe beside FFmpeg. Install the tools first, or check any configured tool path overrides.", "Tools required"); return; }
        }

        isYtDownloading = true;
        ytDownloadCancellation = new CancellationTokenSource();
        var token = ytDownloadCancellation.Token;
        var settings = SettingsStore.Current.Clone();
        string? workingDirectory = null;
        var failedLinks = 0;
        var finishedLinks = 0;
        ytImportedCount = ytImportFailures = 0;
        DownloadBatchSummary.Text = "";
        ytConsoleBuffer.Clear(); DownloadConsole.Clear();
        UpdateBusyWorkspaces();
        AppendDownloadConsole($"[hTunes] Queued {links.Count} links. Format: {settings.YtAudioFormat}; quality: {settings.YtAudioQuality}; import: {settings.ImportMode}.");
        AppendDownloadConsole($"[hTunes] Destination: {settings.DownloadDirectory}");
        AppendDownloadConsole("[hTunes] YouTube may require a current Deno or Node.js runtime installed on PATH. yt-dlp diagnostics appear below.");
        try
        {
            SettingsStore.Validate(settings);
            workingDirectory = Directory.CreateTempSubdirectory("htunes-ytdlp-").FullName;
            var archive = Path.Combine(workingDirectory, "library-archive.txt");
            for (var i = 0; i < links.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                DownloadLinkProgress.Text = $"Link {i + 1} of {links.Count}";
                DownloadTrackTitle.Text = "Reading link…";
                DownloadTrackProgress.Text = "Track — of — in this link";
                DownloadProgressBar.IsIndeterminate = true;
                DownloadStatus.Text = "Downloading…";
                // Rebuild from files still present in the library, not a permanent 'ever downloaded' list.
                File.WriteAllLines(archive, YtDlpDownloadService.LibraryArchive(allTracks).Distinct(StringComparer.Ordinal));
                AppendDownloadConsole($"[hTunes] Starting link {i + 1} of {links.Count}: {links[i]}");
                long? currentIndex = null, totalTracks = null;
                var linkIndex = i;
                var progress = new Progress<YtDownloadUpdate>(update =>
                {
                    if (!isYtDownloading || i != linkIndex) return;
                    if (update.Log is not null) AppendDownloadConsole(update.Log);
                    if (!string.IsNullOrWhiteSpace(update.Title)) DownloadTrackTitle.Text = update.Title;
                    currentIndex = update.Index ?? currentIndex;
                    totalTracks = update.Count ?? totalTracks;
                    DownloadTrackProgress.Text = $"Track {currentIndex?.ToString() ?? "—"} of {totalTracks?.ToString() ?? "—"} in this link";
                    if (update.Title is not null)
                    {
                        DownloadProgressBar.IsIndeterminate = update.Percent is null;
                        if (update.Percent is double percent) DownloadProgressBar.Value = percent;
                        DownloadStatus.Text = token.IsCancellationRequested ? "Aborting…" : update.Processing ? "Converting / embedding metadata with FFmpeg…" : "Downloading…";
                    }
                });
                try
                {
                    var result = await YtDlpDownloadService.DownloadLinkAsync(yt!, ffmpeg!, links[i], settings, archive, progress, token);
                    // yt-dlp has closed its files now. Import successful tracks even if another item failed or Abort was pressed.
                    foreach (var file in result.Files)
                    {
                        DownloadStatus.Text = "Adding finished audio to the library…";
                        await ImportYtAudioAsync(file, settings);
                    }
                    if (result.Aborted) break;
                    finishedLinks++;
                    if (result.ExitCode != 0) { failedLinks++; AppendDownloadConsole($"[hTunes] Link {i + 1} finished with errors (exit {result.ExitCode}). Continuing with the next link."); }
                    else if (result.Files.Count == 0) AppendDownloadConsole("[hTunes] No new audio: items may already be in the library or were skipped. See yt-dlp output above.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { failedLinks++; finishedLinks++; AppendDownloadConsole("[hTunes] Link failed: " + ex.Message); DebugLog.Write("yt-dlp", "Link failed", ex); }
                DownloadBatchSummary.Text = $"{finishedLinks}/{links.Count} links finished  •  {ytImportedCount} added  •  {failedLinks} links with errors  •  {ytImportFailures} import errors";
            }
            DownloadStatus.Text = token.IsCancellationRequested ? "Aborted. Finished audio was kept and imported; retry the links to download unfinished tracks again." : "Queue complete.";
            DownloadBatchSummary.Text = $"{finishedLinks}/{links.Count} links finished  •  {ytImportedCount} added  •  {failedLinks} links with errors  •  {ytImportFailures} import errors";
            AppendDownloadConsole("[hTunes] " + DownloadStatus.Text + " " + DownloadBatchSummary.Text);
        }
        catch (Exception ex) { DownloadStatus.Text = "Download stopped: " + ex.Message; AppendDownloadConsole("[hTunes] " + ex.Message); DebugLog.Write("yt-dlp", "Queue failed", ex); }
        finally
        {
            isYtDownloading = false;
            ytDownloadCancellation.Dispose(); ytDownloadCancellation = null;
            DownloadProgressBar.IsIndeterminate = false;
            if (workingDirectory is not null)
            {
                // Only this run's exact archive is temporary. Never delete audio/partials or recurse into the download folder.
                try { File.Delete(Path.Combine(workingDirectory, "library-archive.txt")); Directory.Delete(workingDirectory); }
                catch (Exception ex) { DebugLog.Write("yt-dlp", "Temporary archive cleanup failed", ex); }
            }
            RefreshBrowser(); RefreshDevice(); UpdateBusyWorkspaces();
        }
    }

    private async Task ImportYtAudioAsync(YtCompletedFile file, AppPreferences settings)
    {
        try
        {
            if (!YtDlpDownloadService.IsCompletedAudioPath(file.Path, settings.DownloadDirectory)) throw new IOException("The finished audio is no longer available in the download folder.");
            var existing = allTracks.FirstOrDefault(track => File.Exists(track.FilePath) &&
                ((!string.IsNullOrEmpty(file.Identity) && track.DownloadIdentity == file.Identity) || Same(track.FilePath, file.Path) || Same(track.OriginalImportPath, file.Path)));
            if (existing is not null)
            {
                var previous = existing.DownloadIdentity;
                existing.DownloadIdentity = file.Identity;
                try { SaveLibrary(); } catch { existing.DownloadIdentity = previous; throw; }
                AppendDownloadConsole("[hTunes] Already in library: " + file.Title); return;
            }
            var imported = await Task.Run(() => ImportFileService.Prepare(file.Path, settings));
            var track = new Track { FilePath = imported.LibraryPath, OriginalImportPath = file.Path, DownloadIdentity = file.Identity,
                Title = string.IsNullOrWhiteSpace(file.Title) ? Path.GetFileNameWithoutExtension(file.Path) : file.Title, DateAdded = DateTime.Now };
            await Task.Run(() => MediaMetadata.ReadInto(track));
            var before = allTracks.ToList();
            allTracks.Add(track);
            try { SaveLibrary(); } catch { allTracks.Remove(track); throw; }
            var after = allTracks.ToList();
            RecordEdit("Import downloaded audio (library entry only)", () => allTracks = before.ToList(), () => allTracks = after.ToList());
            ytImportedCount++;
            AppendDownloadConsole("[hTunes] Added to library: " + track.Title);
            if (imported.DeleteSourceAfterSave)
            {
                try { await Task.Run(() => ImportFileService.CompleteMove(imported)); }
                catch (Exception ex) { AppendDownloadConsole("[hTunes] Copy imported; original retained: " + ex.Message); DebugLog.Write("yt-dlp import", "Move kept original", ex); }
            }
        }
        catch (Exception ex) { ytImportFailures++; AppendDownloadConsole("[hTunes] Could not import finished audio; kept on disk: " + ex.Message); DebugLog.Write("yt-dlp import", "Failed", ex); }
    }

    private void AbortDownloads_Click(object sender, RoutedEventArgs e)
    {
        if (!isYtDownloading) return;
        DownloadAbortButton.IsEnabled = false;
        DownloadStatus.Text = "Aborting… finished files will still be added to the library.";
        ytDownloadCancellation?.Cancel();
    }

    internal void UpdateDownloadControls()
    {
        DownloadStartButton.IsEnabled = !StartupCheckInProgress && !isYtDownloading && ContextActionsAvailable;
        DownloadSettingsButton.IsEnabled = ContextActionsAvailable;
        DownloadLinksBox.IsReadOnly = isYtDownloading;
        DownloadAbortButton.IsEnabled = isYtDownloading && ytDownloadCancellation?.IsCancellationRequested == false;
        if (isYtDownloading) SyncAllButton.IsEnabled = EjectButton.IsEnabled = false;
    }

    private void AppendDownloadConsole(string line)
    {
        line = line.Replace("\0", "");
        if (line.Length > 8192) line = line[..8192] + " …";
        ytConsoleBuffer.AppendLine(line);
        if (ytConsoleBuffer.Length > 200_000) ytConsoleBuffer.Remove(0, ytConsoleBuffer.Length - 150_000);
        DownloadConsole.Text = ytConsoleBuffer.ToString();
        DownloadConsole.ScrollToEnd();
        DebugLog.Write("yt-dlp output", line);
    }
}
