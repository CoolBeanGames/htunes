namespace HTunes.App;

public partial class MainWindow
{
    private string? pendingAutoSyncRoot;
    private bool autoSyncRunning;
    internal bool StartupCheckInProgress { get; set; }

    private void TryAutoSync()
    {
        if (pendingAutoSyncRoot is not { } root || !IsLoaded || StartupCheckInProgress || autoSyncRunning || !ContextActionsAvailable || isIPodLoading ||
            activePodcastDownloads > 0 || podcastFeedOperations > 0 || OwnedWindows.Count > 0) return;
        pendingAutoSyncRoot = null; // Consume before starting: do not loop/retry every detection tick.
        if (SettingsStore.Current.AutoSyncOnConnection && currentDevice?.RootPath == root) _ = AutoSyncAsync(root);
    }

    private async Task AutoSyncAsync(string root)
    {
        autoSyncRunning = true;
        UpdateBusyWorkspaces();
        DebugLog.Write("Auto sync", "Starting connection sync");
        try
        {
            var settings = SettingsStore.Current;
            if (settings.AutoSyncMusic && allTracks.Count > 0)
                await SyncTracksAsync(allTracks.Select(track => track.Id), randomFill: true, showSummary: false);
            if (settings.AutoSyncPodcasts && PodcastShows.Count > 0 && currentDevice?.RootPath == root)
                await SyncAllPodcastsAsync(showSummary: false);
            DebugLog.Write("Auto sync", "Connection sync finished");
        }
        catch (Exception ex) { DebugLog.Write("Auto sync", "Connection sync failed", ex); }
        finally { autoSyncRunning = false; UpdateBusyWorkspaces(); }
    }
}
