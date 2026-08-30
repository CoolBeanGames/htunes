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
    public ObservableCollection<PodcastShow> PodcastShows { get; } = [];
    public ObservableCollection<PodcastSearchResult> PodcastSearchResults { get; } = [];

    private void InitializePodcastUi()
    {
        LoadPodcastLibrary();
        PodcastShowsList.ItemsSource = PodcastShows;
        PodcastSearchResultsList.ItemsSource = PodcastSearchResults;
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
        catch { }
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
        if (podcastsRefreshedThisSession || PodcastShows.Count == 0) return;
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
        finally { PodcastSearchButton.IsEnabled = true; }
    }

    private async void PodcastSubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastSearchResult result }) await SubscribeAsync(result);
    }

    private async Task SubscribeAsync(PodcastSearchResult result)
    {
        var existing = PodcastShows.FirstOrDefault(show => Same(show.FeedUrl, result.FeedUrl));
        if (existing is not null) { PodcastShowsList.SelectedItem = existing; return; }
        var show = new PodcastShow { Title = result.Title, Author = result.Author, FeedUrl = result.FeedUrl, ArtworkUrl = result.ArtworkUrl };
        try
        {
            PodcastSearchButton.IsEnabled = false;
            await PodcastService.RefreshShowAsync(show);
            PodcastShows.Add(show);
            SavePodcastLibrary();
            PodcastShowsList.Items.Refresh();
            PodcastShowsList.SelectedItem = show;
        }
        catch (Exception ex) { MessageBox.Show(this, $"hTunes could not subscribe to this feed.\n\n{ex.GetBaseException().Message}", "Subscription failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { PodcastSearchButton.IsEnabled = true; }
    }

    private void PodcastShowsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPodcastShowPanel();

    private void RefreshPodcastShowPanel()
    {
        var show = SelectedPodcastShow;
        PodcastShowTitle.Text = show?.Title ?? "Select a podcast";
        PodcastShowSummary.Text = show is null ? "Search for a show above or paste its RSS feed URL." : $"{show.Author}  •  {show.UnplayedCount} unplayed  •  {show.DownloadedCount} downloaded";
        PodcastEpisodesGrid.ItemsSource = show?.Episodes;
        PodcastEmptyState.Visibility = show is null || show.Episodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PodcastSyncCountBox.Text = show?.SyncEpisodeCount.ToString() ?? "3";
        PodcastSyncOrderCombo.SelectedIndex = show?.SyncOrder.Equals("Oldest", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
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
        foreach (var show in shows)
        {
            try
            {
                PodcastShowSummary.Text = $"Refreshing {show.Title}…";
                await PodcastService.RefreshShowAsync(show);
            }
            catch (Exception ex) { PodcastShowSummary.Text = $"Could not refresh {show.Title}: {ex.GetBaseException().Message}"; }
        }
        SavePodcastLibrary();
        RefreshPodcastShowPanel();
    }

    private void PodcastSaveRule_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPodcastShow is not { } show) return;
        if (!int.TryParse(PodcastSyncCountBox.Text, out var count) || count < 0 || count > 999)
        {
            MessageBox.Show(this, "Enter an episode count between 0 and 999.", "Invalid episode count");
            return;
        }
        show.SyncEpisodeCount = count;
        show.SyncOrder = (PodcastSyncOrderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Newest";
        SavePodcastLibrary();
        RefreshPodcastShowPanel();
    }

    private void PodcastMarkAllPlayed_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPodcastShow is not { } show) return;
        foreach (var episode in show.Episodes) { PreparePodcastFileDeletion(episode); PodcastService.MarkPlayed(episode); }
        SavePodcastLibrary(); RefreshPodcastShowPanel(); ScheduleConnectedPodcastCleanup();
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
        PodcastShows.Remove(show); SavePodcastLibrary(); RefreshPodcastShowPanel();
    }

    private async void PodcastDownloadEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastEpisode episode } && SelectedPodcastShow is { } show) await DownloadPodcastEpisodeAsync(show, episode);
    }

    private async Task<bool> DownloadPodcastEpisodeAsync(PodcastShow show, PodcastEpisode episode)
    {
        if (episode.IsDownloaded) return true;
        try
        {
            PodcastShowSummary.Text = $"Downloading {episode.Title}…";
            await PodcastService.DownloadEpisodeAsync(show, episode, new Progress<double>(value => PodcastShowSummary.Text = $"Downloading {episode.Title}… {value:0}%"));
            SavePodcastLibrary(); RefreshPodcastShowPanel(); return true;
        }
        catch (Exception ex) { MessageBox.Show(this, $"Episode download failed.\n\n{ex.GetBaseException().Message}", "Download failed", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    private void PodcastDeleteEpisodeDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastEpisode episode }) { PreparePodcastFileDeletion(episode); PodcastService.DeleteDownload(episode); SavePodcastLibrary(); RefreshPodcastShowPanel(); }
    }

    private void PodcastMarkEpisodePlayed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastEpisode episode }) { PreparePodcastFileDeletion(episode); PodcastService.MarkPlayed(episode); SavePodcastLibrary(); RefreshPodcastShowPanel(); ScheduleConnectedPodcastCleanup(); }
    }

    private void PodcastMarkEpisodeUnplayed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PodcastEpisode episode }) { PodcastService.MarkUnplayed(episode); SavePodcastLibrary(); RefreshPodcastShowPanel(); }
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
            if (!episode.IsPlayed && episode.DurationMs > 0 && episode.PlaybackPositionMs * 2L >= episode.DurationMs)
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

    private async Task SyncAllPodcastsAsync()
    {
        var selections = PodcastShows.SelectMany(show => PodcastService.EpisodesForSync(show).Select(episode => new PodcastEpisodeSelection(show, episode))).ToList();
        await SyncPodcastSelectionsAsync(selections, mirrorSubscriptions: true);
    }

    private async Task SyncPodcastSelectionsAsync(IReadOnlyCollection<PodcastEpisodeSelection> selections, bool mirrorSubscriptions)
    {
        if (isSyncing || isReconcilingPlayCounts || currentDevice is null) return;
        var device = currentDevice;
        isSyncing = true; deviceTimer.Stop(); SyncAllButton.IsEnabled = EjectButton.IsEnabled = false; SyncAllButton.Content = "Syncing…";
        try
        {
            if (!await EnsureIPodPreparedAsync(device)) return;
            var completed = 0;
            foreach (var selection in selections)
            {
                if (!selection.Episode.IsDownloaded && !await DownloadPodcastEpisodeAsync(selection.Show, selection.Episode))
                    throw new InvalidOperationException($"{selection.Episode.Title} could not be downloaded. No changes were made to the iPod.");
                completed++;
                DeviceDetailsText.Text = $"  •  Preparing podcasts  ({completed}/{selections.Count})";
            }
            var ready = selections.Where(selection => selection.Episode.IsDownloaded).ToList();
            var result = await Task.Run(() => PodcastIPodSyncService.Sync(device.RootPath, ready, PodcastShows.ToList(), mirrorSubscriptions));
            await LoadIPodTracksAsync(device);
            MessageBox.Show(this, result.Summary, "Podcast sync complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, $"Podcast sync failed and the previous iPod database was restored.\n\n{ex.GetBaseException().Message}", "Podcast sync failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { isSyncing = false; SyncAllButton.Content = "Sync podcasts"; deviceTimer.Start(); RefreshDevice(); UpdateDeviceStripMode(); }
    }
}
