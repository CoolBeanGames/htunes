using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace HTunes.App;

public partial class MainWindow
{
    private bool contextActionRunning;
    private bool ContextActionsAvailable => !contextActionRunning && !isSyncing && !isReconcilingPlayCounts && !autoSyncRunning && activePodcastDownloads == 0 && podcastFeedOperations == 0;

    private void UpdateBusyWorkspaces()
    {
        MusicWorkspace.IsEnabled = PodcastWorkspace.IsEnabled = ContextActionsAvailable;
    }

    private void InitializeContextMenus()
    {
        AttachItemMenu(TracksGrid, menu => BuildTrackMenu(menu, SelectedTracks()));
        AttachItemMenu(PrimaryList, menu => BuildTrackMenu(menu, ContextCategoryTracks(false)));
        AttachItemMenu(SecondaryList, menu => BuildTrackMenu(menu, ContextCategoryTracks(true)));
        AttachItemMenu(PlaylistList, BuildPlaylistMenu);
        AttachItemMenu(PodcastShowsList, BuildPodcastShowMenu);
        AttachItemMenu(PodcastEpisodesGrid, BuildEpisodeMenu);
        AttachItemMenu(PodcastSearchResultsList, menu =>
        {
            if (PodcastSearchResultsList.SelectedItem is PodcastSearchResult result)
                AddMenuAction(menu, "Subscribe", () => SubscribeAsync(result));
        });
        DeviceStrip.ContextMenu = new ContextMenu();
        DeviceStrip.ContextMenuOpening += (_, e) =>
        {
            var menu = DeviceStrip.ContextMenu;
            menu.Items.Clear();
            AddMenuAction(menu, "Open iPod tab", () => IPodTab.IsChecked = true, currentDevice is not null);
            if (isPodcastView) AddMenuAction(menu, "Sync podcasts", SyncAllPodcastsAsync, currentDevice is not null);
            else AddMenuAction(menu, "Sync all music", () => SyncTracksAsync(allTracks.Select(track => track.Id), true), currentDevice is not null);
            AddMenuAction(menu, "Eject iPod", () => Eject_Click(this, new RoutedEventArgs()), currentDevice is not null);
            e.Handled = menu.Items.Count == 0;
        };
    }

    private static void AttachItemMenu(ItemsControl control, Action<ContextMenu> build)
    {
        // Keep a menu attached before the first click; WPF also opens it for the Menu key / Shift+F10.
        control.ContextMenu = new ContextMenu();
        control.PreviewMouseRightButtonDown += (_, e) =>
        {
            var container = ItemsControl.ContainerFromElement(control, e.OriginalSource as DependencyObject);
            var item = container is DataGridRow row ? row.Item
                : container is ListBoxItem ? control.ItemContainerGenerator.ItemFromContainer(container) : null;
            SelectContextItem(control, item == DependencyProperty.UnsetValue ? null : item);
            e.Handled = true;
        };
        control.ContextMenuOpening += (_, e) =>
        {
            control.ContextMenu.Items.Clear();
            build(control.ContextMenu);
            e.Handled = control.ContextMenu.Items.Count == 0;
        };
    }

    private static void SelectContextItem(ItemsControl control, object? item)
    {
        if (control is DataGrid grid)
        {
            if (item is not null)
            {
                if (!grid.SelectedItems.Contains(item)) { grid.SelectedItems.Clear(); grid.SelectedItem = item; }
            }
            else grid.UnselectAll();
        }
        else if (control is ListBox list)
        {
            if (item is not null)
            {
                if (!list.SelectedItems.Contains(item)) { list.UnselectAll(); list.SelectedItem = item; }
            }
            else list.UnselectAll();
        }
    }

    private MenuItem AddMenuAction(ItemsControl menu, string title, Action action, bool enabled = true) =>
        AddMenuAction(menu, title, () => { action(); return Task.CompletedTask; }, enabled);

    private MenuItem AddMenuAction(ItemsControl menu, string title, Func<Task> action, bool enabled = true)
    {
        // TextBlock prevents underscores in user-supplied playlist names from becoming access keys.
        var item = new MenuItem { Header = new TextBlock { Text = title }, IsEnabled = enabled && ContextActionsAvailable };
        item.Click += async (_, e) =>
        {
            e.Handled = true;
            if (!ContextActionsAvailable) return;
            contextActionRunning = true;
            MusicWorkspace.IsEnabled = PodcastWorkspace.IsEnabled = false;
            try { await action(); }
            catch (Exception ex)
            {
                DebugLog.Write("Action", title, ex);
                MessageBox.Show(this, ex.GetBaseException().Message, "Action could not be completed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                contextActionRunning = false;
                UpdateBusyWorkspaces();
            }
        };
        menu.Items.Add(item);
        return item;
    }

    private List<Track> ContextCategoryTracks(bool secondary)
    {
        var selected = (secondary ? SecondaryList : PrimaryList).SelectedItems.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var artists = PrimaryList.SelectedItems.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return SourceTracks.Where(track => (!isIPodView || track.IsPodcast == (category == "Podcast")) &&
            (secondary ? artists.Contains(track.Artist) && selected.Contains(track.Album) : category switch
            {
                "Artist" => selected.Contains(track.Artist),
                "Album" or "Podcast" => selected.Contains(track.Album),
                "Genre" => selected.Contains(track.Genre),
                _ => false
            })).ToList();
    }

    private void BuildTrackMenu(ItemsControl menu, List<Track> tracks)
    {
        if (tracks.Count == 0) return;
        var playlist = !isIPodView ? PlaylistList.SelectedItem as Playlist : null;
        var show = isIPodView && tracks.Count == 1 && tracks[0].IsPodcast
            ? PodcastShows.FirstOrDefault(item => Same(item.Title, tracks[0].Album)) : null;
        var episode = show?.Episodes.FirstOrDefault(item => Same(item.Title, tracks[0].Title));
        if (show is not null && episode is not null)
            AddMenuAction(menu, "Play / resume in hTunes (download if needed)", () => PlayPodcastEpisodeAsync(show, episode));
        else AddMenuAction(menu, "Play", () => PlayContextTracks(tracks));
        if (!isIPodView)
        {
            AddMenuAction(menu, "Sync selected to iPod", () => SyncTracksAsync(tracks.Select(track => track.Id), false), currentDevice is not null);
            AddMenuAction(menu, "Edit metadata / artwork…", () => EditTrackMetadata(tracks));
            var addTo = new MenuItem { Header = "Add to playlist", IsEnabled = ContextActionsAvailable };
            menu.Items.Add(addTo);
            AddMenuAction(addTo, "New playlist…", () =>
            {
                var name = AskPlaylistName("New playlist", "New Playlist");
                if (name is null) return;
                CreateLocalPlaylist(name, tracks.Select(track => track.Id));
            });
            foreach (var target in Playlists.ToList())
                AddMenuAction(addTo, target.Name, () =>
                {
                    ChangePlaylistMembership(target, () =>
                    {
                        foreach (var track in tracks)
                            if (!target.TrackIds.Contains(track.Id)) target.TrackIds.Add(track.Id);
                    });
                    SaveLibrary();
                    RefreshPlaylistView(target);
                }, tracks.Any(track => !target.TrackIds.Contains(track.Id)));
            menu.Items.Add(new Separator());
            if (playlist is not null)
                AddMenuAction(menu, "Remove from this playlist", () =>
                {
                    var ids = tracks.Select(track => track.Id).ToHashSet();
                    ChangePlaylistMembership(playlist, () => playlist.TrackIds.RemoveAll(ids.Contains));
                    SaveLibrary();
                    RefreshPlaylistView(playlist);
                });
            AddMenuAction(menu, "Remove from library…", () => RemoveContextTracks(tracks));
        }
        if (tracks.Count == 1)
            AddMenuAction(menu, "Show file in Explorer", () => RevealFile(tracks[0].FilePath), File.Exists(tracks[0].FilePath));
    }

    private void PlayContextTracks(List<Track> tracks)
    {
        if (tracks.Count == 0) return;
        SetVisibleTracks(tracks, preserveOrder: true);
        TracksGrid.SelectedItem = tracks[0];
        PlaySelected();
    }

    private void RemoveContextTracks(List<Track> tracks)
    {
        if (MessageBox.Show(this, $"Remove {tracks.Count} selected track(s) from the library and all playlists?\n\nThe original files will NOT be deleted.",
            "Remove from library", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var ids = tracks.Select(track => track.Id).ToHashSet();
        var before = allTracks.ToList();
        var membershipsBefore = Playlists.ToDictionary(playlist => playlist, playlist => playlist.TrackIds.ToList());
        allTracks.RemoveAll(track => ids.Contains(track.Id));
        foreach (var playlist in Playlists) playlist.TrackIds.RemoveAll(ids.Contains);
        var after = allTracks.ToList();
        var membershipsAfter = Playlists.ToDictionary(playlist => playlist, playlist => playlist.TrackIds.ToList());
        RecordEdit("Remove from library", () =>
        {
            allTracks = before.ToList();
            foreach (var (playlist, members) in membershipsBefore) playlist.TrackIds = members.ToList();
        }, () =>
        {
            allTracks = after.ToList();
            foreach (var (playlist, members) in membershipsAfter) playlist.TrackIds = members.ToList();
        });
        SaveLibrary();
        RefreshBrowser();
    }

    private void BuildPlaylistMenu(ItemsControl menu)
    {
        if (isIPodView) return;
        if (PlaylistList.SelectedItem is Playlist playlist)
        {
            var tracks = playlist.TrackIds.Select(id => allTracks.FirstOrDefault(track => track.Id == id)).OfType<Track>().ToList();
            AddMenuAction(menu, "Play playlist", () => PlayContextTracks(tracks), tracks.Count > 0);
            AddMenuAction(menu, "Sync playlist to iPod", () => SyncTracksAsync(playlist.TrackIds, false, playlist), currentDevice is not null);
            AddMenuAction(menu, "Rename playlist…", () =>
            {
                var name = AskPlaylistName("Rename playlist", playlist.Name);
                if (name is null) return;
                var before = playlist.Name;
                playlist.Name = name;
                if (before != name) RecordEdit("Rename playlist", () => playlist.Name = before, () => playlist.Name = name);
                SaveLibrary();
                RefreshPlaylistView(playlist);
            });
            AddMenuAction(menu, "Delete playlist…", () =>
            {
                if (MessageBox.Show(this, $"Delete the playlist ‘{playlist.Name}’?\n\nIts tracks will remain in your library.", "Delete playlist",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var index = Playlists.IndexOf(playlist);
                Playlists.Remove(playlist);
                RecordEdit("Delete playlist", () => Playlists.Insert(Math.Min(index, Playlists.Count), playlist), () => Playlists.Remove(playlist));
                SaveLibrary();
                RefreshBrowser();
            });
            menu.Items.Add(new Separator());
        }
        AddMenuAction(menu, "New playlist…", () =>
        {
            var name = AskPlaylistName("New playlist", "New Playlist");
            if (name is null) return;
            CreateLocalPlaylist(name);
        });
    }

    private void RefreshPlaylistView(Playlist playlist)
    {
        PlaylistList.Items.Refresh();
        PlaylistList.SelectedItem = playlist;
        PlaylistList_SelectionChanged(this, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
    }

    private string? AskPlaylistName(string title, string initial)
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12), MinWidth = 300 };
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 75 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(input); panel.Children.Add(buttons);
        var dialog = new Window { Title = title, Owner = this, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private void BuildPodcastShowMenu(ItemsControl menu)
    {
        if (SelectedPodcastShow is not { } show) return;
        AddMenuAction(menu, "Refresh episodes", () => RefreshShowsAsync([show]));
        AddMenuAction(menu, "Download episodes selected by sync rule", async () =>
        {
            foreach (var episode in PodcastService.EpisodesForSync(show).Where(episode => !episode.IsDownloaded).ToList())
                await DownloadPodcastEpisodeAsync(show, episode);
        });
        AddMenuAction(menu, "Sync this show to iPod", () => SyncPodcastSelectionsAsync(
            PodcastService.EpisodesForSync(show).Select(episode => new PodcastEpisodeSelection(show, episode)).ToList(), false), currentDevice is not null);
        menu.Items.Add(new Separator());
        AddMenuAction(menu, "Mark all episodes played…", () =>
        {
            if (MessageBox.Show(this, "Mark every episode in this show played? Local downloads follow your deletion setting; synced copies will be removed during iPod reconciliation.",
                "Mark all played", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                SetEpisodesPlayed(show.Episodes.ToList(), true);
        });
        AddMenuAction(menu, "Mark all episodes unplayed / reset progress…", () =>
        {
            if (MessageBox.Show(this, "Mark every episode in this show unplayed and reset saved progress?", "Reset show progress",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                SetEpisodesPlayed(show.Episodes.ToList(), false);
        });
        AddMenuAction(menu, "Delete all downloaded episodes…", () => DeleteEpisodeDownloads(show.Episodes.Where(episode => episode.IsDownloaded).ToList()), show.DownloadedCount > 0);
        AddMenuAction(menu, "Unsubscribe…", () => PodcastUnsubscribe_Click(this, new RoutedEventArgs()));
    }

    private void BuildEpisodeMenu(ItemsControl menu)
    {
        if (SelectedPodcastShow is not { } show) return;
        var episodes = PodcastEpisodesGrid.SelectedItems.OfType<PodcastEpisode>().ToList();
        if (episodes.Count == 0) return;
        AddMenuAction(menu, "Play / resume episode", () => PlayPodcastEpisodeAsync(show, episodes[0]), episodes.Count == 1);
        AddMenuAction(menu, "Download selected episodes", async () =>
        {
            foreach (var episode in episodes.Where(episode => !episode.IsDownloaded)) await DownloadPodcastEpisodeAsync(show, episode);
        }, episodes.Any(episode => !episode.IsDownloaded));
        AddMenuAction(menu, "Sync selected unplayed episodes to iPod", () => SyncPodcastSelectionsAsync(
            episodes.Where(episode => !episode.IsPlayed).Select(episode => new PodcastEpisodeSelection(show, episode)).ToList(), false), currentDevice is not null && episodes.Any(episode => !episode.IsPlayed));
        menu.Items.Add(new Separator());
        AddMenuAction(menu, "Mark played…", () =>
        {
            if (MessageBox.Show(this, $"Mark {episodes.Count} selected episode(s) played?\n\nLocal downloads follow your deletion setting; synced copies are removed during iPod reconciliation.",
                "Mark episodes played", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                SetEpisodesPlayed(episodes, true);
        }, episodes.Any(episode => !episode.IsPlayed));
        AddMenuAction(menu, "Mark unplayed / reset progress", () => SetEpisodesPlayed(episodes, false), episodes.Any(episode => episode.IsPlayed || episode.PlaybackPositionMs > 0));
        AddMenuAction(menu, "Delete downloaded files…", () => DeleteEpisodeDownloads(episodes.Where(episode => episode.IsDownloaded).ToList()), episodes.Any(episode => episode.IsDownloaded));
        if (episodes.Count == 1 && episodes[0].IsDownloaded)
            AddMenuAction(menu, "Show downloaded file in Explorer", () => RevealFile(episodes[0].LocalPath!));
    }

    private void SetEpisodesPlayed(List<PodcastEpisode> episodes, bool played)
    {
        var before = episodes.ToDictionary(episode => episode, EpisodeState.Read);
        foreach (var episode in episodes)
        {
            PreparePodcastFileDeletion(episode);
            if (played) PodcastService.MarkPlayed(episode);
            else PodcastService.MarkUnplayed(episode);
        }
        RecordEpisodeChanges(episodes, before, played);
        SavePodcastLibrary();
        RefreshPodcastShowPanel();
        if (played) ScheduleConnectedPodcastCleanup();
    }

    private void DeleteEpisodeDownloads(List<PodcastEpisode> episodes)
    {
        if (episodes.Count == 0 || MessageBox.Show(this, $"Delete {episodes.Count} downloaded episode file(s) from this computer?\n\nEpisode entries and played status will be kept. You can download them again if the feed still offers them.",
            "Delete podcast downloads", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var episode in episodes) { PreparePodcastFileDeletion(episode); PodcastService.DeleteDownload(episode); }
        SavePodcastLibrary();
        RefreshPodcastShowPanel();
    }

    private static void RevealFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("This file is no longer available.", path);
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{Path.GetFullPath(path)}\"", UseShellExecute = true });
    }
}
