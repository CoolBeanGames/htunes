using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HTunes.App;

public partial class MainWindow
{
    private readonly string podcastDataFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "podcasts.json");
    private bool isPodcastView;
    private bool podcastsRefreshedThisSession;
    private PodcastEpisode? currentPodcastEpisode;
    private PodcastShow? currentPodcastPlaybackShow;
    private readonly DispatcherTimer podcastPlaybackTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime lastPodcastProgressSaveUtc;
    private int activePodcastDownloads;
    private int podcastFeedOperations;
    private readonly Dictionary<PodcastEpisode, Task<bool>> podcastDownloads = [];
    public ObservableCollection<PodcastShow> PodcastShows { get; } = [];
    public ObservableCollection<PodcastSearchResult> PodcastSearchResults { get; } = [];
    public ObservableCollection<PodcastEpisode> PodcastEpisodeView { get; } = [];

    private void InitializePodcastUi(bool loadLibrary = true)
    {
        if (loadLibrary) LoadPodcastLibrary();
        PodcastShowsList.ItemsSource = PodcastShows;
        PodcastSearchResultsList.ItemsSource = PodcastSearchResults;
        PodcastEpisodesGrid.ItemsSource = PodcastEpisodeView;
        podcastPlaybackTimer.Tick += (_, _) => CapturePodcastPlaybackProgress();
        player.MediaOpened += (_, _) => PodcastMediaOpened();
        RefreshPodcastShowPanel();
    }

    private void LoadPodcastLibrary()
    {
        try
        {
            if (!File.Exists(podcastDataFile)) return;
            var data = JsonSerializer.Deserialize<PodcastLibraryData>(File.ReadAllText(podcastDataFile));
            if (data is null) return;
            foreach (var show in data.Shows)
            {
                if (!show.EpisodeSeenStateInitialized)
                {
                    show.SeenEpisodeIds = show.Episodes.Select(episode => episode.Id).ToList();
                    show.EpisodeSeenStateInitialized = true;
                }
                foreach (var episode in show.Episodes)
                {
                    if (!string.IsNullOrWhiteSpace(episode.LocalPath) && !File.Exists(episode.LocalPath)) episode.LocalPath = null;
                    else if (episode.IsDownloaded && episode.DurationMs <= 0)
                    {
                        try
                        {
                            using var media = TagLib.File.Create(episode.LocalPath!);
                            episode.DurationMs = Math.Max(0, (long)media.Properties.Duration.TotalMilliseconds);
                        }
                        catch { }
                    }
                }
                PodcastShows.Add(show);
            }
        }
        catch (Exception ex) { DebugLog.Write("Podcast library", "Could not load saved subscriptions", ex); }
    }

    private void SavePodcastLibrary()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(podcastDataFile)!);
        File.WriteAllText(podcastDataFile, JsonSerializer.Serialize(new PodcastLibraryData { Shows = PodcastShows.ToList() }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private PodcastShow? SelectedPodcastShow => PodcastShowsList.SelectedItem as PodcastShow;

    private async Task EnterPodcastViewAsync()
    {
        RefreshPodcastShowPanel();
        if (!SettingsStore.Current.PodcastRefreshOnOpen || podcastsRefreshedThisSession || PodcastShows.Count == 0) return;
        podcastsRefreshedThisSession = true;
        await RefreshShowsAsync(PodcastShows.Where(show => DateTime.UtcNow - show.LastRefreshedUtc > TimeSpan.FromMinutes(15)).ToList());
    }

    private async Task RefreshPodcastsInBackgroundAsync()
    {
        if (!SettingsStore.Current.PodcastRefreshOnOpen || podcastsRefreshedThisSession || PodcastShows.Count == 0) return;
        podcastsRefreshedThisSession = true;
        await RefreshShowsAsync(PodcastShows.Where(show => DateTime.UtcNow - show.LastRefreshedUtc > TimeSpan.FromMinutes(15)).ToList());
    }

    private async void PodcastSearch_Click(object sender, RoutedEventArgs e) => await SearchPodcastsAsync();

    private async void PodcastSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchPodcastsAsync(); }
    }

    private async Task SearchPodcastsAsync()
    {
        var query = PodcastSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;
        podcastFeedOperations++; UpdateBusyWorkspaces();
        PodcastSearchButton.IsEnabled = false;
        try
        {
            if (Uri.TryCreate(query, UriKind.Absolute, out var feedUri) && feedUri.Scheme is "http" or "https")
            {
                await SubscribeAsync(new PodcastSearchResult("Podcast feed", "", feedUri.AbsoluteUri, ""));
                return;
            }
            var results = await PodcastService.SearchAsync(query);
            PodcastSearchResults.Clear();
            foreach (var result in results) PodcastSearchResults.Add(result);
            PodcastSearchResultsHeading.Visibility = PodcastSearchResultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (results.Count == 0) MessageBox.Show(this, "No podcast shows matched that search.", "No results");
        }
        catch (Exception ex) { MessageBox.Show(this, $"Podcast search failed.\n\n{ex.GetBaseException().Message}", "Search failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { PodcastSearchButton.IsEnabled = true; podcastFeedOperations--; UpdateBusyWorkspaces(); }
    }

    private async void PodcastSubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastSearchResult result }) await SubscribeAsync(result);
    }

    private async Task SubscribeAsync(PodcastSearchResult result)
    {
        var existing = PodcastShows.FirstOrDefault(show => Same(show.FeedUrl, result.FeedUrl));
        if (existing is not null) { PodcastShowsList.SelectedItem = existing; OpenPodcastShow(); return; }
        var show = new PodcastShow { Title = result.Title, Author = result.Author, FeedUrl = result.FeedUrl, ArtworkUrl = result.ArtworkUrl,
            SyncEpisodeCount = SettingsStore.Current.PodcastDefaultCount, SyncOrder = SettingsStore.Current.PodcastDefaultOrder };
        podcastFeedOperations++; UpdateBusyWorkspaces();
        try
        {
            PodcastSearchButton.IsEnabled = false;
            await PodcastService.RefreshShowAsync(show);
            show.EpisodeSeenStateInitialized = true;
            show.SeenEpisodeIds = [];
            var index = PodcastShows.Count;
            PodcastShows.Add(show);
            RecordEdit("Subscribe to podcast", () => DetachPodcastShow(show), () => PodcastShows.Insert(Math.Min(index, PodcastShows.Count), show));
            SavePodcastLibrary();
            PodcastShowsList.Items.Refresh();
            PodcastShowsList.SelectedItem = show;
            OpenPodcastShow();
            await AutoDownloadShowAsync(show);
        }
        catch (Exception ex) { MessageBox.Show(this, $"hTunes could not subscribe to this feed.\n\n{ex.GetBaseException().Message}", "Subscription failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { PodcastSearchButton.IsEnabled = true; podcastFeedOperations--; UpdateBusyWorkspaces(); }
    }

    private void PodcastShowsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPodcastShowPanel();

    private void RefreshPodcastShowPanel()
    {
        UpdatePodcastNavigation();
        var show = SelectedPodcastShow;
        PodcastShowTitle.Text = show?.Title ?? "Select a podcast";
        PodcastShowSummary.Text = show is null ? "Search for a show above or paste its RSS feed URL." : $"{show.Author}  •  {show.UnplayedCount} unplayed  •  {show.DownloadedCount} downloaded";
        PodcastEpisodeView.Clear();
        if (show is not null)
            foreach (var episode in PodcastEpisodeOrdering.Order(show.Episodes, oldest: false))
                PodcastEpisodeView.Add(episode);
        PodcastEmptyState.Visibility = show is null || show.Episodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PodcastSyncCountBox.Text = show?.SyncEpisodeCount.ToString() ?? "3";
        PodcastSyncOrderCombo.SelectedIndex = show?.SyncOrder.Equals("Oldest", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
        PodcastRuleHint.Text = SettingsStore.Current.PodcastIncludeDownloaded ? "unplayed episodes, plus manually downloaded episodes" : "unplayed episodes only";
        SetPodcastArtwork(show?.ArtworkDisplay);
        PodcastShowsList.Items.Refresh();
        PodcastEpisodesGrid.Items.Refresh();
    }

    private void SetPodcastArtwork(string? path)
    {
        try { PodcastShowArtwork.Source = string.IsNullOrWhiteSpace(path) ? null : new BitmapImage(new Uri(path, UriKind.Absolute)); }
        catch { PodcastShowArtwork.Source = null; }
    }

    private async void PodcastRefreshShow_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPodcastShow is { } show) await RefreshShowsAsync([show]);
    }

    private async Task RefreshShowsAsync(IReadOnlyCollection<PodcastShow> shows)
    {
        podcastFeedOperations++; UpdateBusyWorkspaces();
        try
        {
            foreach (var show in shows)
            {
                try
                {
                    PodcastShowSummary.Text = $"Refreshing {show.Title}…";
                    await PodcastService.RefreshShowAsync(show);
                    await AutoDownloadShowAsync(show);
                }
                catch (Exception ex) { DebugLog.Write("Podcast", $"Feed refresh failed for {show.Id}", ex); PodcastShowSummary.Text = $"Could not refresh {show.Title}: {ex.GetBaseException().Message}"; }
            }
            SavePodcastLibrary();
            RefreshPodcastShowPanel();
        }
        finally { podcastFeedOperations--; UpdateBusyWorkspaces(); }
    }

    private async Task AutoDownloadShowAsync(PodcastShow show)
    {
        if (!SettingsStore.Current.PodcastAutoDownloadOnRefresh) return;
        foreach (var episode in PodcastService.EpisodesForSync(show))
            if (!episode.IsDownloaded) await DownloadPodcastEpisodeAsync(show, episode);
    }

    private void PodcastSaveRule_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPodcastShow is not { } show) return;
        if (!int.TryParse(PodcastSyncCountBox.Text, out var count) || count < 0 || count > 999)
        {
            MessageBox.Show(this, "Enter an episode count between 0 and 999.", "Invalid episode count");
            return;
        }
        var previousCount = show.SyncEpisodeCount;
        var previousOrder = show.SyncOrder;
        var order = (PodcastSyncOrderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Newest";
        show.SyncEpisodeCount = count;
        show.SyncOrder = order;
        if (previousCount != count || previousOrder != order)
            RecordEdit("Change podcast sync rule", () => { show.SyncEpisodeCount = previousCount; show.SyncOrder = previousOrder; },
                () => { show.SyncEpisodeCount = count; show.SyncOrder = order; });
        SavePodcastLibrary();
        RefreshPodcastShowPanel();
    }

    private void PodcastMarkAllPlayed_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPodcastShow is not { } show) return;
        SetEpisodesPlayed(show.Episodes.ToList(), true);
    }

    private void PodcastDeleteDownloads_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPodcastShow is not { } show) return;
        foreach (var episode in show.Episodes) { PreparePodcastFileDeletion(episode); PodcastService.DeleteDownload(episode); }
        SavePodcastLibrary(); RefreshPodcastShowPanel();
    }

    private void PodcastUnsubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPodcastShow is not { } show || MessageBox.Show(this, $"Unsubscribe from {show.Title}? Downloaded episodes will be deleted.", "Unsubscribe", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var episode in show.Episodes) { PreparePodcastFileDeletion(episode); PodcastService.DeleteDownload(episode); }
        var index = PodcastShows.IndexOf(show);
        PodcastShows.Remove(show);
        RecordEdit("Unsubscribe (subscription only)", () => PodcastShows.Insert(Math.Min(index, PodcastShows.Count), show), () => DetachPodcastShow(show));
        SavePodcastLibrary(); RefreshPodcastShowPanel();
    }

    private async void PodcastDownloadEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastEpisode episode } && SelectedPodcastShow is { } show) await DownloadPodcastEpisodeAsync(show, episode);
    }

    private async Task<bool> DownloadPodcastEpisodeAsync(PodcastShow show, PodcastEpisode episode)
    {
        if (podcastDownloads.TryGetValue(episode, out var pending)) return await pending;
        var task = DownloadPodcastEpisodeCoreAsync(show, episode);
        podcastDownloads[episode] = task;
        try { return await task; }
        finally { podcastDownloads.Remove(episode); }
    }

    private async Task<bool> DownloadPodcastEpisodeCoreAsync(PodcastShow show, PodcastEpisode episode)
    {
        if (episode.IsDownloaded) return true;
        activePodcastDownloads++; UpdateBusyWorkspaces();
        try
        {
            PodcastShowSummary.Text = $"Downloading {episode.Title}…";
            await PodcastService.DownloadEpisodeAsync(show, episode, new Progress<double>(value => PodcastShowSummary.Text = $"Downloading {episode.Title}… {value:0}%"));
            SavePodcastLibrary(); RefreshPodcastShowPanel(); return true;
        }
        catch (Exception ex) { DebugLog.Write("Podcast download", "Failed", ex); MessageBox.Show(this, $"Episode download failed.\n\n{ex.GetBaseException().Message}", "Download failed", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        finally { activePodcastDownloads--; UpdateBusyWorkspaces(); }
    }

    private void PodcastDeleteEpisodeDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastEpisode episode }) { PreparePodcastFileDeletion(episode); PodcastService.DeleteDownload(episode); SavePodcastLibrary(); RefreshPodcastShowPanel(); }
    }

    private void PodcastMarkEpisodePlayed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastEpisode episode }) SetEpisodesPlayed([episode], true);
    }

    private void PodcastMarkEpisodeUnplayed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastEpisode episode }) SetEpisodesPlayed([episode], false);
    }

    private async void PodcastPlayEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PodcastEpisode episode } || SelectedPodcastShow is not { } show) return;
        await PlayPodcastEpisodeAsync(show, episode);
    }

    private async Task PlayPodcastEpisodeAsync(PodcastShow show, PodcastEpisode episode)
    {
        if (ReferenceEquals(currentPodcastEpisode, episode)) { player.Play(); return; }
        if (!episode.IsDownloaded && !await DownloadPodcastEpisodeAsync(show, episode)) return;
        if (currentPodcastEpisode is not null) FinalizePodcastPlayback();
        currentPodcastEpisode = episode;
        currentPodcastPlaybackShow = show;
        player.Open(new Uri(episode.LocalPath!)); player.Play();
        podcastPlaybackTimer.Start();
        NowPlayingTitle.Text = episode.Title; NowPlayingArtist.Text = show.Title;
    }

    private void PodcastMediaOpened()
    {
        if (currentPodcastEpisode is not { } episode) return;
        if (player.NaturalDuration.HasTimeSpan)
            episode.DurationMs = Math.Max(0, (long)player.NaturalDuration.TimeSpan.TotalMilliseconds);
        if (!episode.IsPlayed && episode.PlaybackPositionMs > 0 && (episode.DurationMs <= 0 || episode.PlaybackPositionMs < episode.DurationMs))
            player.Position = TimeSpan.FromMilliseconds(episode.PlaybackPositionMs);
        podcastPlaybackTimer.Start();
    }

    private void CapturePodcastPlaybackProgress()
    {
        if (currentPodcastEpisode is not { } episode) return;
        try
        {
            if (player.NaturalDuration.HasTimeSpan)
                episode.DurationMs = Math.Max(0, (long)player.NaturalDuration.TimeSpan.TotalMilliseconds);
            episode.PlaybackPositionMs = Math.Max(episode.PlaybackPositionMs, (long)player.Position.TotalMilliseconds);
            if (!episode.IsPlayed && PodcastService.ReachedPlayedThreshold(episode.PlaybackPositionMs, episode.DurationMs, SettingsStore.Current.PodcastPlayedPercent))
            {
                PodcastService.MarkPlayed(episode, deleteDownload: false);
                SavePodcastLibrary();
                lastPodcastProgressSaveUtc = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - lastPodcastProgressSaveUtc >= TimeSpan.FromSeconds(10))
            {
                SavePodcastLibrary();
                lastPodcastProgressSaveUtc = DateTime.UtcNow;
            }
            if (isPodcastView) PodcastEpisodesGrid.Items.Refresh();
        }
        catch (InvalidOperationException) { }
    }

    private void PodcastPlaybackEnded()
    {
        if (currentPodcastEpisode is null) return;
        CapturePodcastPlaybackProgress();
        if (currentPodcastEpisode.DurationMs > 0) currentPodcastEpisode.PlaybackPositionMs = currentPodcastEpisode.DurationMs;
        podcastPlaybackTimer.Stop();
        player.Close();
        PodcastService.MarkPlayed(currentPodcastEpisode);
        currentPodcastEpisode = null;
        currentPodcastPlaybackShow = null;
        SavePodcastLibrary(); RefreshPodcastShowPanel(); ScheduleConnectedPodcastCleanup();
    }

    private void FinalizePodcastPlayback()
    {
        if (currentPodcastEpisode is not { } episode) return;
        CapturePodcastPlaybackProgress();
        podcastPlaybackTimer.Stop();
        player.Stop();
        player.Close();
        if (episode.IsPlayed) PodcastService.MarkPlayed(episode);
        currentPodcastEpisode = null;
        currentPodcastPlaybackShow = null;
        SavePodcastLibrary();
        if (isPodcastView) RefreshPodcastShowPanel();
        if (episode.IsPlayed) ScheduleConnectedPodcastCleanup();
    }

    private void ScheduleConnectedPodcastCleanup()
    {
        if (currentDevice is null) return;
        playCountSyncTimer.Stop();
        playCountSyncTimer.Start();
    }

    private void PreparePodcastFileDeletion(PodcastEpisode episode)
    {
        if (!ReferenceEquals(currentPodcastEpisode, episode)) return;
        CapturePodcastPlaybackProgress();
        podcastPlaybackTimer.Stop();
        player.Stop(); player.Close();
        currentPodcastEpisode = null;
        currentPodcastPlaybackShow = null;
    }

    private async void PodcastSyncEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastEpisode episode } && SelectedPodcastShow is { } show)
            await SyncPodcastSelectionsAsync([new PodcastEpisodeSelection(show, episode)], mirrorSubscriptions: false);
    }

    private Task SyncAllPodcastsAsync() => SyncAllPodcastsAsync(showSummary: true);

    private async Task SyncAllPodcastsAsync(bool showSummary)
    {
        var selections = PodcastShows.SelectMany(show => PodcastService.EpisodesForSync(show).Select(episode => new PodcastEpisodeSelection(show, episode))).ToList();
        await SyncPodcastSelectionsAsync(selections, mirrorSubscriptions: SettingsStore.Current.PodcastMirrorOnSync, showSummary);
    }

    private async Task SyncPodcastSelectionsAsync(IReadOnlyCollection<PodcastEpisodeSelection> selections, bool mirrorSubscriptions, bool showSummary = true)
    {
        if (isRenaming || isTagSaving || isSyncing || isReconcilingPlayCounts || currentDevice is null) return;
        var device = currentDevice;
        isSyncing = true; syncCancellation = new CancellationTokenSource(); var syncToken = syncCancellation.Token; deviceTimer.Stop(); SyncCurrentButton.IsEnabled = SyncAllButton.IsEnabled = EjectButton.IsEnabled = false; StopSyncButton.Visibility = Visibility.Visible; StopSyncButton.IsEnabled = true; SyncAllButton.Content = "Syncing…";
        UpdateBusyWorkspaces();
        try
        {
            DebugLog.Write("Podcast sync", $"Starting episodes={selections.Count}; mirror={mirrorSubscriptions}");
            if (!SettingsStore.Current.PodcastDownloadOnSync && selections.Any(selection => !selection.Episode.IsDownloaded))
                throw new InvalidOperationException("Download the selected episodes first, or enable download-on-sync in Settings. No changes were made to the iPod.");
            if (!await EnsureIPodPreparedAsync(device)) return;
            var completed = 0;
            foreach (var selection in selections)
            {
                syncToken.ThrowIfCancellationRequested();
                if (!selection.Episode.IsDownloaded && !await DownloadPodcastEpisodeAsync(selection.Show, selection.Episode))
                    throw new InvalidOperationException($"{selection.Episode.Title} could not be downloaded. No changes were made to the iPod.");
                completed++;
                DeviceDetailsText.Text = $"  •  Preparing podcasts  ({completed}/{selections.Count})";
            }
            var ready = selections.Where(selection => selection.Episode.IsDownloaded).ToList();
            syncToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() => PodcastIPodSyncService.Sync(device.RootPath, ready, PodcastShows.ToList(), mirrorSubscriptions), syncToken);
            await LoadIPodTracksAsync(device);
            DebugLog.Write("Podcast sync", result.Summary);
            if (showSummary) MessageBox.Show(this, result.Summary, "Podcast sync complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { DebugLog.Write("Podcast sync", "Cancelled by user"); if (showSummary) MessageBox.Show(this, "Podcast sync stopped safely.", "Sync stopped"); }
        catch (Exception ex) { DebugLog.Write("Podcast sync", "Failed", ex); MessageBox.Show(this, $"Podcast sync failed.\n\n{ex.GetBaseException().Message}", "Podcast sync failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { isSyncing = false; syncCancellation?.Dispose(); syncCancellation = null; StopSyncButton.Visibility = Visibility.Collapsed; SyncAllButton.Content = "Sync all"; deviceTimer.Start(); RefreshDevice(); UpdateDeviceStripMode(); UpdateBusyWorkspaces(); }
    }
}
