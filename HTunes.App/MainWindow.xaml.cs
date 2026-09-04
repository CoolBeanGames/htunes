using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HTunes.App;

public partial class MainWindow : Window
{
    private static readonly string[] AudioExtensions = [".mp3", ".m4a", ".m4b", ".aac", ".wav", ".wma", ".flac", ".ogg", ".oga", ".opus", ".alac", ".aif", ".aiff", ".aa"];
    private readonly string dataFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "library.json");
    private readonly MediaPlayer player = new();
    private bool repeatPlayback;
    private bool shufflePlayback;
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Point dragStart;
    private string category = "Artist";
    private List<Track> allTracks = [];
    private List<Track> ipodTracks = [];
    private List<IPodPlaylistView> ipodPlaylists = [];
    private IPodDevice? currentDevice;
    private bool isIPodView;
    private bool isIPodLoading;
    private bool isSyncing;
    private CancellationTokenSource? syncCancellation;
    private bool lastMusicSyncCompleted;
    private bool isReconcilingPlayCounts;
    private readonly bool initializeServices;
    private CancellationTokenSource? ipodLoadCancellation;
    private readonly DispatcherTimer playCountSyncTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public ObservableCollection<Track> VisibleTracks { get; } = [];
    public ObservableCollection<Playlist> Playlists { get; } = [];

    public MainWindow() : this(true) { }

    // Isolated UI checks use false: no preferences/library IO, device detection, or background timers.
    internal MainWindow(bool initializeServices, string? isolatedLibraryFile = null)
    {
        this.initializeServices = initializeServices;
        if (!initializeServices && isolatedLibraryFile is not null) dataFile = Path.GetFullPath(isolatedLibraryFile);
        TextBoxBehaviors.InstallTabSelectAll();
        InitializeComponent(); DataContext = this;
        if (initializeServices) { LoadPreferences(); LoadLibrary(); }
        InitializePodcastUi(initializeServices); InitializeContextMenus(); InitializeTagEditor(); InitializeRenameEditor(); InitializeColumnSorting(); InitializeDownloadOverrides(); InitializeTopMenus(); InitializeNavigation(); InitializeSyncAnimation(); RefreshBrowser();
        if (!initializeServices) return;
        RefreshDevice();
        player.MediaEnded += (_, _) => { if (currentPodcastEpisode is not null) PodcastPlaybackEnded(); else if (repeatPlayback) { player.Position = TimeSpan.Zero; player.Play(); } else NextTrack(); };
        deviceTimer.Tick += (_, _) => RefreshDevice();
        playCountSyncTimer.Tick += async (_, _) => { playCountSyncTimer.Stop(); if (currentDevice is not null) await ReconcilePlayCountsAsync(currentDevice); };
        playbackTimer.Tick += (_, _) => UpdatePlaybackDisplay();
        player.MediaOpened += (_, _) => UpdatePlaybackDisplay();
        deviceTimer.Start();
        _ = RefreshPodcastsInBackgroundAsync();
    }

    private void LoadLibrary()
    {
        try
        {
            if (!File.Exists(dataFile)) return;
            var data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(dataFile));
            if (data is null) return;
            allTracks = data.Tracks;
            foreach (var track in allTracks.Where(t => !t.MetadataManagedByLibrary)) MediaMetadata.ReadInto(track, onlyMissing: true);
            foreach (var playlist in data.Playlists)
            {
                if (playlist.TrackIds.Distinct().Count() != playlist.TrackIds.Count) playlist.TrackIds = playlist.TrackIds.Distinct().ToList();
                Playlists.Add(playlist);
            }
            DebugLog.Write("Library", $"Loaded tracks={allTracks.Count}; playlists={Playlists.Count}");
        }
        catch (Exception ex) { DebugLog.Write("Library", "Could not load saved library", ex); }
    }

    private void SaveLibrary()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!);
        var temporary = dataFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new LibraryData { Tracks = allTracks, Playlists = Playlists.ToList() }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, dataFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void LoadPreferences()
    {
        SettingsStore.Initialize();
        var preferences = SettingsStore.Current;
        if (TranscodeComboBox.Items.Cast<ComboBoxItem>().Any(item => Same(item.Tag?.ToString() ?? "", preferences.TranscodePresetId)))
            TranscodeComboBox.SelectedValue = preferences.TranscodePresetId;
    }

    private void SavePreferences()
    {
        var preferences = SettingsStore.Current.Clone();
        preferences.TranscodePresetId = TranscodeComboBox.SelectedValue as string ?? "original";
        SettingsStore.Save(preferences);
    }

    private void InitializeColumnSorting()
    {
        foreach (var grid in new[] { TracksGrid, PodcastEpisodesGrid, TagTracksGrid, RenameTracksGrid })
        {
            grid.CanUserSortColumns = true;
            foreach (var column in grid.Columns)
            {
                column.CanUserSort = true;
                if (column is DataGridBoundColumn bound && string.IsNullOrWhiteSpace(column.SortMemberPath) &&
                    bound.Binding is Binding binding && !string.IsNullOrWhiteSpace(binding.Path?.Path))
                    column.SortMemberPath = binding.Path.Path;
            }
        }
        TracksGrid.Columns.First(column => Equals(column.Header, "Bitrate")).SortMemberPath = nameof(Track.BitrateKbps);
        PodcastEpisodesGrid.Columns.First(column => Equals(column.Header, "Published")).SortMemberPath = nameof(PodcastEpisode.PublishedUtc);
        TracksGrid.Sorting += TrackGrid_Sorting;
        TagTracksGrid.Sorting += TrackGrid_Sorting;
    }

    // Sorting by a grouping column cascades into a sensible sub-order: album within artist,
    // then disc / track number, then title for tracks that carry no number.
    private static readonly string[] ArtistSortChain = [nameof(Track.Artist), nameof(Track.Album), nameof(Track.DiscNumber), nameof(Track.TrackNumber), nameof(Track.Title)];
    private static readonly string[] AlbumSortChain = [nameof(Track.Album), nameof(Track.DiscNumber), nameof(Track.TrackNumber), nameof(Track.Title)];
    private static readonly string[] GenreSortChain = [nameof(Track.Genre), nameof(Track.Artist), nameof(Track.Album), nameof(Track.DiscNumber), nameof(Track.TrackNumber), nameof(Track.Title)];

    private void TrackGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        var chain = e.Column.SortMemberPath switch
        {
            nameof(Track.Artist) => ArtistSortChain,
            nameof(Track.Album) => AlbumSortChain,
            nameof(Track.Genre) => GenreSortChain,
            _ => null
        };
        if (chain is null || sender is not DataGrid grid) return;
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in grid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        var view = grid.Items;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            for (var index = 0; index < chain.Length; index++)
                view.SortDescriptions.Add(new SortDescription(chain[index], index == 0 ? direction : ListSortDirection.Ascending));
        }
    }

    private void TranscodeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        try { SavePreferences(); }
        catch (Exception ex) { DebugLog.Write("Settings", "Transcode preference save failed", ex); MessageBox.Show(this, ex.Message, "Could not save preference"); }
    }

    private void RefreshBrowser()
    {
        if (isTagView) RefreshTagLibrary();
        if (isRenameView) RefreshRenameLibrary();
        var search = SearchBox?.Text?.Trim() ?? "";
        var viewTracks = SourceTracks;
        var categoryTracks = isIPodView
            ? viewTracks.Where(track => category == "Podcast" ? track.IsPodcast : !track.IsPodcast)
            : viewTracks;
        var source = categoryTracks.Where(t => MatchesSearch(t, search)).ToList();
        if (musicBrowse.Category != category) musicBrowse.Reset(category);
        PageTitle.Text = musicBrowse.Title;
        LibrarySummary.Text = isIPodView
            ? isIPodLoading ? $"Loading content from {currentDevice?.Name ?? "iPod"}…" : category == "Podcast"
                ? $"{source.Count} podcast episode{(source.Count == 1 ? "" : "s")} on {currentDevice?.Name ?? "iPod"}"
                : $"{viewTracks.Count(track => !track.IsPodcast)} song{(viewTracks.Count(track => !track.IsPodcast) == 1 ? "" : "s")} on {currentDevice?.Name ?? "iPod"}"
            : $"{viewTracks.Count} song{(viewTracks.Count == 1 ? "" : "s")} in your library";
        PlaylistsHeading.Visibility = PlaylistList.Visibility = isIPodView ? Visibility.Collapsed : Visibility.Visible;
        NewPlaylistButton.Visibility = isIPodView ? Visibility.Collapsed : Visibility.Visible;
        IPodPlaylistsHeading.Visibility = IPodPlaylistList.Visibility = isIPodView ? Visibility.Visible : Visibility.Collapsed;
        PlaylistsHeadingRow.Height = isIPodView ? GridLength.Auto : GridLength.Auto;
        NewPlaylistRow.Height = isIPodView ? new GridLength(0) : GridLength.Auto;
        PlaylistsListRow.Height = new GridLength(150);
        if (isIPodView && !ReferenceEquals(IPodPlaylistList.ItemsSource, ipodPlaylists))
        {
            var selectedName = (IPodPlaylistList.SelectedItem as IPodPlaylistView)?.Name;
            IPodPlaylistList.ItemsSource = ipodPlaylists;
            IPodPlaylistList.SelectedItem = ipodPlaylists.FirstOrDefault(p => p.Name == selectedName);
        }
        else if (isIPodView) IPodPlaylistList.Items.Refresh();
        EmptyStateTitle.Text = isIPodView ? (isIPodLoading ? "Reading iPod content…" : category == "Podcast" ? "No podcasts found on this iPod" : "No music found on this iPod") : "Drop music here to add it";
        EmptyStateDetail.Text = isIPodView ? "Content stored by the stock iPod OS appears here" : "or choose File → Add files to library";
        PrimaryPanel.Visibility = musicBrowse.ShowsGroups ? Visibility.Visible : Visibility.Collapsed;
        SecondaryPanel.Visibility = musicBrowse.ShowsAlbums ? Visibility.Visible : Visibility.Collapsed;
        TracksPanel.Visibility = musicBrowse.ShowsTracks ? Visibility.Visible : Visibility.Collapsed;
        MusicBackButton.Visibility = musicBrowse.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        MusicBackButton.Content = musicBrowse.Album is not null ? $"← Back to albums by {musicBrowse.Group}" : $"← Back to {musicBrowse.RootTitle.ToLowerInvariant()}";
        PrimaryHeading.Text = musicBrowse.RootTitle + " — click to open; Ctrl/Shift-click to select; Enter to open selection";
        SecondaryHeading.Text = $"Albums by {musicBrowse.Group}";
        refreshingNavigation = true;
        ReplaceBrowseItems(PrimaryList, category switch
        {
            "Artist" => GroupBrowseItems(source, track => track.Artist),
            "Album" => GroupBrowseItems(source, track => track.Album),
            "Genre" => GroupBrowseItems(source, track => track.Genre),
            "Podcast" => GroupBrowseItems(source, track => track.Album),
            _ => null
        });
        ReplaceBrowseItems(SecondaryList, category == "Artist" && musicBrowse.Group is not null
            ? GroupBrowseItems(source.Where(t => Same(t.Artist, musicBrowse.Group)), track => track.Album) : null);
        refreshingNavigation = false;
        BrowseEmptyState.Visibility = (musicBrowse.ShowsGroups && PrimaryList.Items.Count == 0) || (musicBrowse.ShowsAlbums && SecondaryList.Items.Count == 0)
            ? Visibility.Visible : Visibility.Collapsed;
        BrowseEmptyState.Text = isIPodView ? (isIPodLoading ? "Reading iPod content…" : "No matching content on this iPod.") : string.IsNullOrEmpty(search) ? "No items found. Drop music here to add it." : "No matching items. Try a different search.";
        var scoped = musicBrowse.Filter(source).ToList();
        if (musicBrowse.CanGoBack)
        {
            var noun = category == "Podcast" ? "episode" : "song";
            LibrarySummary.Text = $"{scoped.Count} {noun}{(scoped.Count == 1 ? "" : "s")}" +
                (musicBrowse.ShowsAlbums ? $"  •  {SecondaryList.Items.Count} albums" : musicBrowse.Album is not null ? $"  •  {musicBrowse.Group}" : "") +
                (isIPodView ? $"  •  on {currentDevice?.Name ?? "iPod"}" : "");
        }
        ShowArtwork([]);
        SetVisibleTracks(musicBrowse.ShowsTracks ? scoped : []);
        if (!musicBrowse.ShowsTracks) ShowArtwork(scoped);
        if (!isIPodView && PlaylistList.SelectedItem is Playlist)
            PlaylistList_SelectionChanged(this, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
        if (isIPodView && IPodPlaylistList.SelectedItem is IPodPlaylistView plView)
            ShowIPodPlaylistTracks(plView);
    }

    private void IPodPlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshBrowser();

    private void ShowIPodPlaylistTracks(IPodPlaylistView view)
    {
        var keys = view.TrackKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tracks = ipodTracks.Where(track => keys.Contains(TrackIdentity.Key(track.Title, track.Artist, track.Album, track.TrackNumber))).ToList();
        PageTitle.Text = view.Name;
        LibrarySummary.Text = $"{tracks.Count} of {view.TrackCount} song{(view.TrackCount == 1 ? "" : "s")} on this iPod playlist{(view.IsSmart ? "  •  smart playlist" : "")}";
        PrimaryPanel.Visibility = SecondaryPanel.Visibility = BrowseEmptyState.Visibility = Visibility.Collapsed;
        TracksPanel.Visibility = MusicBackButton.Visibility = Visibility.Visible;
        MusicBackButton.Content = "← Back to iPod music";
        SetVisibleTracks(tracks, preserveOrder: true);
    }

    private List<Track> SourceTracks => isIPodView ? ipodTracks : allTracks;

    private static List<BrowseItem> GroupBrowseItems(IEnumerable<Track> tracks, Func<Track, string> key) => tracks
        .GroupBy(key, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key)
        .Select(group => new BrowseItem(group.Key, group.Any(track => track.IsNew))).ToList();

    private void SetVisibleTracks(IEnumerable<Track> tracks, bool preserveOrder = false)
    {
        var ordered = preserveOrder ? tracks.ToList() : tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title).ToList();
        suppressSelectionHistory = true;
        VisibleTracks.Clear();
        foreach (var track in ordered) VisibleTracks.Add(track);
        suppressSelectionHistory = false;
        previousTrackSelection = TracksGrid.SelectedItems.Cast<Track>().Select(t => t.Id).ToList();
        EmptyState.Visibility = VisibleTracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowArtwork(ordered);
    }

    private void ShowArtwork(IEnumerable<Track> tracks)
    {
        var path = tracks.Select(t => t.ArtworkPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        if (path is null)
        {
            AlbumArtworkImage.Source = null;
            AlbumArtworkPlaceholder.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            AlbumArtworkImage.Source = image;
            AlbumArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            AlbumArtworkImage.Source = null;
            AlbumArtworkPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private static bool MatchesSearch(Track t, string search) => string.IsNullOrEmpty(search) || new[] { t.Title, t.Artist, t.Album, t.Genre }.Any(v => v.Contains(search, StringComparison.OrdinalIgnoreCase));
    private void TopTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton button) return;
        var tab = button.Tag?.ToString();
        isPodcastView = tab == "Podcasts";
        isDownloadView = tab == "Download";
        isTagView = tab == "Tag";
        isRenameView = tab == "Rename";
        isIPodView = tab == "IPod";
        IPodPodcastsCategoryButton.Visibility = isIPodView ? Visibility.Visible : Visibility.Collapsed;
        if (!isIPodView && category == "Podcast") ArtistCategoryButton.IsChecked = true;
        MusicWorkspace.Visibility = isPodcastView || isDownloadView || isTagView || isRenameView ? Visibility.Collapsed : Visibility.Visible;
        PodcastWorkspace.Visibility = isPodcastView ? Visibility.Visible : Visibility.Collapsed;
        DownloadWorkspace.Visibility = isDownloadView ? Visibility.Visible : Visibility.Collapsed;
        TagWorkspace.Visibility = isTagView ? Visibility.Visible : Visibility.Collapsed;
        RenameWorkspace.Visibility = isRenameView ? Visibility.Visible : Visibility.Collapsed;
        UpdateDeviceStripMode();
        if (isRenameView) RefreshRenameLibrary();
        else if (isTagView) RefreshTagLibrary();
        else if (isPodcastView && !isYtDownloading && !isTagSaving && !isRenaming) _ = EnterPodcastViewAsync(); else if (!isDownloadView && !isPodcastView) ResetMusicNavigation();
        UpdateDownloadControls();
    }
    private void Category_Checked(object sender, RoutedEventArgs e) { if (!IsLoaded || sender is not RadioButton button) return; category = button.Tag?.ToString() ?? "Artist"; ResetMusicNavigation(); }

    private void PrimaryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshingNavigation && IsLoaded) ShowArtwork(ContextCategoryTracks(false));
    }

    private void SecondaryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshingNavigation && IsLoaded) ShowArtwork(ContextCategoryTracks(true));
    }

    private void TracksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RecordTrackSelectionChange();
        var selected = SelectedTracks();
        if (selected.Count > 0) ShowArtwork(selected);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) RefreshBrowser(); }
    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) ImportPaths(paths); }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "Audio files|*.mp3;*.m4a;*.aac;*.wav;*.wma;*.flac;*.ogg|All files|*.*" };
        if (dialog.ShowDialog(this) == true) ImportPaths(dialog.FileNames);
    }

    private void ImportPaths(IEnumerable<string> paths)
    {
        if (isSyncing || isReconcilingPlayCounts || autoSyncRunning || isTagSaving || isRenaming) return;
        var before = allTracks.ToList();
        var files = paths.SelectMany(path => Directory.Exists(path) ? EnumerateFilesSafely(path) : [path])
            .Where(path => File.Exists(path) && AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var added = 0;
        var moves = new List<ImportedFile>();
        var failures = new List<string>();
        DebugLog.Write("Import", $"Starting {files.Count} files; mode={SettingsStore.Current.ImportMode}");
        foreach (var file in files)
        {
            var fullPath = Path.GetFullPath(file);
            if (allTracks.Any(t => Same(t.FilePath, fullPath) || Same(t.OriginalImportPath, fullPath))) continue;
            try
            {
                var track = new Track { FilePath = fullPath, OriginalImportPath = fullPath, Title = Path.GetFileNameWithoutExtension(file), DateAdded = DateTime.Now, IsNew = true };
                MediaMetadata.ReadInto(track);
                var imported = ImportFileService.Prepare(fullPath, SettingsStore.Current, track.Artist, track.AlbumArtist, track.Album);
                track.FilePath = imported.LibraryPath;
                allTracks.Add(track); added++;
                if (imported.DeleteSourceAfterSave) moves.Add(imported);
            }
            catch (Exception ex) { DebugLog.Write("Import", $"Failed: {fullPath}", ex); failures.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }
        if (added > 0)
        {
            var after = allTracks.ToList();
            try { SaveLibrary(); }
            catch (Exception ex)
            {
                allTracks = before; RefreshBrowser();
                DebugLog.Write("Import", "Library save failed; original files retained", ex);
                MessageBox.Show(this, "The library could not be saved. Original files have been kept; any verified copies remain in the destination folder.\n\n" + ex.Message, "Import stopped");
                return;
            }
            foreach (var move in moves)
                try { ImportFileService.CompleteMove(move); }
                catch (Exception ex) { DebugLog.Write("Import", "Original retained after copy", ex); failures.Add($"{Path.GetFileName(move.SourcePath)}: copied, but original retained: {ex.Message}"); }
            RecordEdit("Import music (library entries only)", () => allTracks = before.ToList(), () => allTracks = after.ToList());
            RefreshBrowser();
        }
        DebugLog.Write("Import", $"Finished: added={added}, failures={failures.Count}");
        if (failures.Count > 0) MessageBox.Show(this, $"Added {added} tracks. {failures.Count} issue(s):\n\n" + string.Join("\n", failures.Take(10)), "Import results");
    }

    private static IEnumerable<string> EnumerateFilesSafely(string rootDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files) yield return file;
            foreach (var child in directories)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private List<Track> SelectedTracks() => isRenameView ? RenameSelection : isTagView ? TagSelection : TracksGrid.SelectedItems.Cast<Track>().ToList();
    private void EditMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (isIPodView) return;
        var selected = musicBrowse.ShowsGroups && PlaylistList.SelectedItem is null ? ContextCategoryTracks(false)
            : musicBrowse.ShowsAlbums && PlaylistList.SelectedItem is null ? ContextCategoryTracks(true) : SelectedTracks();
        if (selected.Count == 0) { MessageBox.Show(this, "Select one or more songs first. Use Ctrl or Shift to select several.", "Edit metadata"); return; }
        EditTrackMetadata(selected);
    }

    private void RemoveTracks_Click(object sender, RoutedEventArgs e)
    {
        if (isIPodView) return;
        var selected = SelectedTracks();
        if (selected.Count > 0) RemoveContextTracks(selected);
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = AskPlaylistName("New playlist", $"New Playlist {Playlists.Count + 1}");
        if (name is null) return;
        CreateLocalPlaylist(name);
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not Playlist playlist) return;
        PageTitle.Text = playlist.Name; LibrarySummary.Text = $"{playlist.TrackIds.Count} song{(playlist.TrackIds.Count == 1 ? "" : "s")}";
        PrimaryPanel.Visibility = SecondaryPanel.Visibility = Visibility.Collapsed;
        BrowseEmptyState.Visibility = Visibility.Collapsed;
        TracksPanel.Visibility = MusicBackButton.Visibility = Visibility.Visible;
        MusicBackButton.Content = $"← Back to {musicBrowse.RootTitle.ToLowerInvariant()}";
        SetVisibleTracks(playlist.TrackIds.Select(id => allTracks.FirstOrDefault(t => t.Id == id)).Where(t => t is not null).Cast<Track>()
            .Where(t => MatchesSearch(t, SearchBox.Text.Trim())), preserveOrder: true);
    }

    private void PlaylistList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => dragStart = e.GetPosition(null);

    private void PlaylistList_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(null);
        if (isIPodView || e.LeftButton != MouseButtonState.Pressed || PlaylistList.SelectedItem is not Playlist playlist ||
            (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
             Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)) return;
        DragDrop.DoDragDrop(PlaylistList, new DataObject("hTunesPlaylist", playlist.Id), DragDropEffects.Copy);
    }

    private void TracksGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(null);
        var row = ItemsControl.ContainerFromElement(TracksGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (Keyboard.Modifiers == ModifierKeys.None && row?.IsSelected == true && TracksGrid.SelectedItems.Count > 1) e.Handled = true;
    }
    private void TracksGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Math.Abs(e.GetPosition(null).X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        var tracks = SelectedTracks(); if (tracks.Count > 0) DragDrop.DoDragDrop(TracksGrid, new DataObject("hTunesTracks", tracks.Select(t => t.Id).ToArray()), DragDropEffects.Copy);
    }

    private void CategoryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(null);
        categoryDragPerformed = false;
        browseClickModified = Keyboard.Modifiers != ModifierKeys.None;
        if (sender is not ListBox list) return;
        var item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (Keyboard.Modifiers == ModifierKeys.None && item?.IsSelected == true && list.SelectedItems.Count > 1) e.Handled = true;
    }
    private void PrimaryList_MouseMove(object sender, MouseEventArgs e)
    {
        if (isIPodView) return;
        StartCategoryDrag(e, category switch
        {
            "Artist" => allTracks.Where(t => PrimaryList.SelectedItems.Cast<BrowseItem>().Any(v => Same(t.Artist, v.Name))),
            "Album" => allTracks.Where(t => PrimaryList.SelectedItems.Cast<BrowseItem>().Any(v => Same(t.Album, v.Name))),
            "Genre" => allTracks.Where(t => PrimaryList.SelectedItems.Cast<BrowseItem>().Any(v => Same(t.Genre, v.Name))),
            _ => []
        });
    }
    private void SecondaryList_MouseMove(object sender, MouseEventArgs e)
    {
        if (isIPodView || category != "Artist" || musicBrowse.Group is not string artist) return;
        StartCategoryDrag(e, allTracks.Where(t => Same(t.Artist, artist) && SecondaryList.SelectedItems.Cast<BrowseItem>().Any(v => Same(t.Album, v.Name))));
    }
    private void StartCategoryDrag(MouseEventArgs e, IEnumerable<Track> tracks)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (Math.Abs(e.GetPosition(null).X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(e.GetPosition(null).Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)) return;
        var ids = tracks.Select(t => t.Id).Distinct().ToArray();
        if (ids.Length > 0) { categoryDragPerformed = true; DragDrop.DoDragDrop((DependencyObject)e.Source, new DataObject("hTunesTracks", ids), DragDropEffects.Copy); }
    }
    private void Playlist_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent("hTunesTracks") ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Playlist_Drop(object sender, DragEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(PlaylistList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var playlist = container?.DataContext as Playlist ?? PlaylistList.SelectedItem as Playlist;
        if (playlist is null || e.Data.GetData("hTunesTracks") is not Guid[] ids) return;
        ChangePlaylistMembership(playlist, () =>
        {
            foreach (var id in ids.Where(id => !playlist.TrackIds.Contains(id))) playlist.TrackIds.Add(id);
        });
        SaveLibrary(); PlaylistList.Items.Refresh(); PlaylistList.SelectedItem = playlist;
        PlaylistList_SelectionChanged(this, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, new List<object>(), new List<object>()));
    }

    private void TracksGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DoubleClickOnRow(TracksGrid, e)) PlaySelected();
    }

    private async void PodcastEpisodesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!DoubleClickOnRow(PodcastEpisodesGrid, e) || SelectedPodcastShow is not { } show || PodcastEpisodesGrid.SelectedItem is not PodcastEpisode episode) return;
        await PlayPodcastEpisodeAsync(show, episode);
    }

    private static bool DoubleClickOnRow(ItemsControl grid, RoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(grid, source) is null) return false;
        for (var node = source; node is Visual or System.Windows.Media.Media3D.Visual3D; node = VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase) return false;
        return true;
    }

    private void RefreshDevice()
    {
        if (isSyncing || isReconcilingPlayCounts || isTagSaving || isRenaming) return;
        var device = IPodDetector.FindConnected();
        if (device is null)
        {
            var wasConnected = currentDevice is not null;
            if (wasConnected) DebugLog.Write("Device", "iPod disconnected");
            pendingAutoSyncRoot = null;
            currentDevice = null;
            ipodLoadCancellation?.Cancel();
            ipodTracks = [];
            isIPodLoading = false;
            DeviceIndicator.Fill = new SolidColorBrush(Color.FromRgb(146, 154, 167));
            DeviceStrip.Background = new SolidColorBrush(Color.FromRgb(227, 230, 234));
            DeviceNameText.Text = "No iPod connected";
            DeviceDetailsText.Text = "  •  Connect an iPod to view capacity and sync music";
            SyncCurrentButton.IsEnabled = SyncAllButton.IsEnabled = EjectButton.IsEnabled = false;
            DeviceStatusArea.Cursor = Cursors.Arrow;
            IPodTab.Visibility = Visibility.Collapsed;
            DeviceStrip.Tag = null;
            if (wasConnected && isIPodView) MusicTab.IsChecked = true;
            return;
        }
        var isNewDevice = currentDevice is null || !Same(currentDevice.RootPath, device.RootPath);
        currentDevice = device;
        DeviceIndicator.Fill = new SolidColorBrush(Color.FromRgb(46, 160, 90));
        DeviceStrip.Background = new SolidColorBrush(Color.FromRgb(221, 238, 255));
        DeviceNameText.Text = device.Name;
        DeviceDetailsText.Text = $"  •  {FormatBytes(device.Capacity)} capacity  •  {FormatBytes(device.FreeSpace)} free";
        SyncCurrentButton.IsEnabled = SyncAllButton.IsEnabled = EjectButton.IsEnabled = true;
        DeviceStatusArea.Cursor = Cursors.Hand;
        IPodTab.Visibility = Visibility.Visible;
        DeviceStrip.Tag = device;
        if (isNewDevice) { DebugLog.Write("Device", $"Connected: {device.RootPath}; capacity={device.Capacity}; free={device.FreeSpace}"); _ = InitializeIPodAsync(device); }
        else TryAutoSync();
    }

    private async Task InitializeIPodAsync(IPodDevice device)
    {
        await ReconcilePlayCountsAsync(device);
        if (currentDevice is not null && Same(currentDevice.RootPath, device.RootPath))
        {
            await LoadIPodTracksAsync(device);
            if (SettingsStore.Current.AutoSyncOnConnection) pendingAutoSyncRoot = device.RootPath;
        }
    }

    private async Task ReconcilePlayCountsAsync(IPodDevice device, bool duringSync = false)
    {
        if (isRenaming || isTagSaving || isReconcilingPlayCounts || (isSyncing && !duringSync)) return;
        var sysInfoPath = Path.Combine(device.RootPath, "iPod_Control", "Device", "SysInfoExtended");
        if (!File.Exists(sysInfoPath) || new FileInfo(sysInfoPath).Length == 0) return;
        isReconcilingPlayCounts = true;
        UpdateBusyWorkspaces();
        SyncCurrentButton.IsEnabled = SyncAllButton.IsEnabled = EjectButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => IPodPlayCountService.Reconcile(device.RootPath, allTracks, PodcastShows.ToList()));
            foreach (var update in result.MusicUpdates)
            {
                var track = allTracks.FirstOrDefault(t => t.Id == update.TrackId);
                if (track is null) continue;
                track.PlayCount = update.Count;
                track.SyncedPlayCounts[update.DeviceId] = update.Count;
            }
            foreach (var update in result.PodcastUpdates)
            {
                var show = PodcastShows.FirstOrDefault(item => item.Title.Equals(update.ShowTitle, StringComparison.OrdinalIgnoreCase));
                var episode = show?.Episodes.FirstOrDefault(item => item.Id.Equals(update.EpisodeId, StringComparison.OrdinalIgnoreCase));
                if (episode is null) continue;
                if (update.DurationMs > 0) episode.DurationMs = update.DurationMs;
                episode.PlaybackPositionMs = Math.Max(episode.PlaybackPositionMs, update.PositionMs);
                if (update.IsPlayed) PodcastService.MarkPlayed(episode, deleteDownload: !ReferenceEquals(episode, currentPodcastEpisode));
            }
            SaveLibrary(); SavePodcastLibrary(); TracksGrid.Items.Refresh();
            if (isPodcastView) RefreshPodcastShowPanel();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Listening", "Reconciliation failed", ex);
            var msg = ex.GetBaseException().Message;
            DeviceDetailsText.Text = $"  •  Play counts could not be synchronized: {(msg.Length > 50 ? msg[..49] + "…" : msg)}";
        }
        finally { isReconcilingPlayCounts = false; RefreshDevice(); UpdateBusyWorkspaces(); }
    }

    private async Task LoadIPodTracksAsync(IPodDevice device)
    {
        ipodLoadCancellation?.Cancel();
        ipodLoadCancellation = new CancellationTokenSource();
        var token = ipodLoadCancellation.Token;
        isIPodLoading = true;
        if (isIPodView) RefreshBrowser();
        try
        {
            var tracks = await Task.Run(() => ReadIPodMusicTracks(device.RootPath, token), token);
            var playlists = await Task.Run(() => { try { return IPodPlaylistReader.Read(device.RootPath); } catch { return new List<IPodPlaylistView>(); } }, token);
            if (currentDevice is not null && Same(currentDevice.RootPath, device.RootPath))
            {
                foreach (var ipodTrack in tracks)
                {
                    var local = FindLocalTrackForIPod(ipodTrack);
                    if (local is not null)
                    {
                        ipodTrack.PlayCount = local.PlayCount;
                        ipodTrack.ArtworkPath = local.ArtworkPath;
                    }
                }
                ipodTracks = tracks;
                ipodPlaylists = playlists;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ipodTracks = [];
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                isIPodLoading = false;
                if (isIPodView) RefreshBrowser();
            }
        }
    }

    private static List<Track> ReadIPodMusicTracks(string rootPath, CancellationToken token)
    {
        var result = new List<Track>();
        var ipod = Clickwheel.IPod.GetiPodByDrive(rootPath, Clickwheel.IPodLoadAction.NoSync);
        foreach (var item in ipod.Tracks)
        {
            token.ThrowIfCancellationRequested();
            var isPodcast = item.PodcastFlag || item.MediaType is Clickwheel.Parsers.iTunesDB.MediaType.Podcast or Clickwheel.Parsers.iTunesDB.MediaType.VideoPodcast ||
                string.Equals(item.Genre, "Podcast", StringComparison.OrdinalIgnoreCase);
            var file = ResolveIPodMediaPath(rootPath, item.FilePath);
            if (!File.Exists(file)) continue;
            result.Add(new Track
            {
                FilePath = file,
                Title = string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileNameWithoutExtension(file) : item.Title,
                Artist = string.IsNullOrWhiteSpace(item.Artist) ? "Unknown Artist" : item.Artist,
                AlbumArtist = item.AlbumArtist ?? "",
                Album = string.IsNullOrWhiteSpace(item.Album) ? "Unknown Album" : item.Album,
                Genre = string.IsNullOrWhiteSpace(item.Genre) ? "Unknown Genre" : item.Genre,
                TrackNumber = checked((int)item.TrackNumber),
                DiscNumber = checked((int)Math.Max(1u, item.DiscNumber)),
                Year = checked((int)item.Year),
                PlayCount = Math.Max(0, item.PlayCount),
                Format = Path.GetExtension(file).TrimStart('.').ToUpperInvariant(),
                BitrateKbps = checked((int)item.Bitrate),
                IsPodcast = isPodcast,
                DownloadIdentity = item.Comment
            });
        }
        return result;
    }

    private static string ResolveIPodMediaPath(string rootPath, string storedPath)
    {
        if (Path.IsPathFullyQualified(storedPath)) return storedPath;
        return Path.Combine(rootPath, storedPath.Replace(':', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private void DeviceStrip_DragOver(object sender, DragEventArgs e) { e.Effects = DeviceStrip.Tag is IPodDevice && !isSyncing && (e.Data.GetDataPresent("hTunesTracks") || e.Data.GetDataPresent("hTunesPlaylist")) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void DeviceStrip_Drop(object sender, DragEventArgs e)
    {
        if (DeviceStrip.Tag is not IPodDevice) return;
        if (e.Data.GetData("hTunesPlaylist") is Guid playlistId && Playlists.FirstOrDefault(item => item.Id == playlistId) is Playlist playlist)
        {
            await SyncTracksAsync(playlist.TrackIds, randomFill: false, playlist);
            return;
        }
        if (e.Data.GetData("hTunesTracks") is Guid[] ids) await SyncTracksAsync(ids, randomFill: false);
    }

    private async void SyncCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (isPodcastView) { await SyncAllPodcastsAsync(); return; }
        if (isTagView || isRenameView)
        {
            var preset = TranscodePresets.Get(TranscodeComboBox.SelectedValue as string);
            var changed = allTracks.Where(track => track.IsNew || track.SyncedIPodFingerprints.Count == 0 || !track.SyncedIPodFingerprints.Values.Contains(IPodSyncService.DesiredFingerprint(track, preset), StringComparer.Ordinal)).Select(track => track.Id);
            await SyncTracksAsync(changed, randomFill: false); return;
        }
        await SyncTracksAsync(allTracks.Select(track => track.Id), randomFill: true);
    }

    private async void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        await SyncTracksAsync(allTracks.Select(track => track.Id), randomFill: true, showSummary: false);
        if (lastMusicSyncCompleted && currentDevice is not null) await SyncAllPodcastsAsync();
    }

    private void StopSync_Click(object sender, RoutedEventArgs e)
    {
        StopSyncButton.IsEnabled = false;
        try { syncCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        DeviceDetailsText.Text = "  •  Stopping safely after the current file…";
    }

    private void UpdateDeviceStripMode()
    {
        if (TranscodeComboBox is null || SyncAllButton is null) return;
        TranscodeComboBox.Visibility = isPodcastView || isDownloadView || isTagView || isRenameView ? Visibility.Collapsed : Visibility.Visible;
        SyncCurrentText.Text = isPodcastView ? "Sync podcasts" : isTagView || isRenameView ? "Sync changes" : "Sync music";
        var preset = TranscodePresets.Get(TranscodeComboBox.SelectedValue as string);
        var musicChanges = allTracks.Any(track => track.IsNew || track.SyncedIPodFingerprints.Count == 0 ||
            !track.SyncedIPodFingerprints.Values.Contains(IPodSyncService.DesiredFingerprint(track, preset), StringComparer.Ordinal));
        var podcastChanges = PodcastShows.SelectMany(show => PodcastService.EpisodesForSync(show))
            .Any(episode => !ipodTracks.Any(track => track.IsPodcast && track.DownloadIdentity.Equals(episode.Id, StringComparison.OrdinalIgnoreCase)));
        SyncCurrentDot.Visibility = (isPodcastView ? podcastChanges : musicChanges) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SyncTracksAsync(IEnumerable<Guid> ids, bool randomFill, Playlist? playlist = null, bool showSummary = true)
    {
        if (isRenaming || isTagSaving || isSyncing || isReconcilingPlayCounts || currentDevice is null) return;
        var requestedIds = ids.ToHashSet();
        var requested = allTracks.Where(t => requestedIds.Contains(t.Id)).ToList();
        if (requested.Count == 0 && playlist is null) { if (showSummary) MessageBox.Show(this, "There are no library tracks in this selection.", "Nothing to sync"); return; }
        // Drop entries whose audio file has vanished from disk instead of silently skipping them.
        var removedMissing = RemoveMissingFilesFromLibrary(requested.Where(track => !File.Exists(track.FilePath)).ToList(), "sync");
        if (removedMissing > 0) requested = requested.Where(track => File.Exists(track.FilePath)).ToList();
        var device = currentDevice;
        var preset = TranscodePresets.Get(TranscodeComboBox.SelectedValue as string);
        lastMusicSyncCompleted = false; isSyncing = true; syncCancellation = new CancellationTokenSource(); var syncToken = syncCancellation.Token; deviceTimer.Stop();
        UpdateBusyWorkspaces();
        SyncCurrentButton.IsEnabled = SyncAllButton.IsEnabled = EjectButton.IsEnabled = false; StopSyncButton.Visibility = Visibility.Visible; StopSyncButton.IsEnabled = true;
        TranscodeComboBox.IsEnabled = false;
        SyncAllButton.Content = "Syncing…";
        StartSyncAnimation(SyncGlyphSource.Music);
        try
        {
            DebugLog.Write("Music sync", $"Starting {requested.Count} tracks; randomFill={randomFill}; preset={TranscodeComboBox.SelectedValue}");
            if (!await EnsureIPodPreparedAsync(device)) return;
            var progress = new Progress<SyncProgress>(p => DeviceDetailsText.Text = $"  •  {(p.Message.Length > 40 ? p.Message[..39] + "…" : p.Message)}  ({Math.Min(p.Completed + 1, p.Total)}/{p.Total})");
            var result = requested.Count == 0
                ? new SyncResult(0, 0, 0, 0, 0, 0)
                : await Task.Run(() => IPodSyncService.Sync(device.RootPath, requested, allTracks, randomFill, preset, progress, syncToken), syncToken);
            SaveLibrary(); // Persist per-device sync fingerprints/markers before any following playlist operation.
            string? playlistSummary = null;
            if (result.Cancelled)
            {
                // Don't start playlist reconciliation after a stop - just show what made it across.
                if (IPodDetector.FindConnected() is { } stoppedDevice) { currentDevice = stoppedDevice; await LoadIPodTracksAsync(stoppedDevice); }
                DebugLog.Write("Music sync", result.Summary);
                lastMusicSyncCompleted = false;
                if (showSummary) MessageBox.Show(this, result.Summary, "Sync stopped", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (playlist is not null)
            {
                DeviceDetailsText.Text = $"  •  Updating playlist {(playlist.Name.Length > 40 ? playlist.Name[..39] + "…" : playlist.Name)}";
                playlistSummary = (await Task.Run(() => IPodPlaylistSyncService.Sync(device.RootPath, playlist, allTracks))).Summary;
            }
            else if (randomFill && Playlists.Count > 0)
            {
                DeviceDetailsText.Text = "  •  Updating playlists";
                var snapshot = Playlists.ToList();
                var results = await Task.Run(() => IPodPlaylistSyncService.SyncAll(device.RootPath, snapshot, allTracks));
                playlistSummary = $"{results.Count} playlist{(results.Count == 1 ? "" : "s")} synced.";
                foreach (var pl in snapshot) pl.PreviousNames.Clear();
                SaveLibrary();
            }
            currentDevice = IPodDetector.FindConnected();
            if (currentDevice is not null)
            {
                await ReconcilePlayCountsAsync(currentDevice, duringSync: true);
                await LoadIPodTracksAsync(currentDevice);
                if (showSummary) IPodTab.IsChecked = true;
            }
            var summary = playlistSummary is null ? result.Summary : $"{result.Summary}\n{playlistSummary}";
            if (removedMissing > 0) summary += $"\nRemoved {removedMissing} track{(removedMissing == 1 ? "" : "s")} with missing files from the library.";
            DebugLog.Write("Music sync", summary);
            lastMusicSyncCompleted = true;
            if (showSummary) MessageBox.Show(this, summary, "Sync complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { DebugLog.Write("Music sync", "Cancelled by user"); if (showSummary) MessageBox.Show(this, "Sync stopped safely. Completed tracks were kept.", "Sync stopped"); }
        catch (Exception ex)
        {
            DebugLog.Write("Music sync", "Sync failed", ex);
            MessageBox.Show(this, $"The sync was stopped and the previous iPod database was restored.\n\n{ex.GetBaseException().Message}", "Sync failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            isSyncing = false; syncCancellation?.Dispose(); syncCancellation = null; StopSyncButton.Visibility = Visibility.Collapsed; TranscodeComboBox.IsEnabled = true; SyncAllButton.Content = "Sync all"; StopSyncAnimation(); deviceTimer.Start(); RefreshDevice(); UpdateBusyWorkspaces();
        }
    }

    private async Task<bool> EnsureIPodPreparedAsync(IPodDevice device)
    {
        var sysInfoPath = Path.Combine(device.RootPath, "iPod_Control", "Device", "SysInfoExtended");
        if (File.Exists(sysInfoPath) && new FileInfo(sysInfoPath).Length > 0) return true;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("hTunes could not locate its device setup helper.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("--prepare-ipod"); start.ArgumentList.Add(device.RootPath);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The iPod setup helper could not be started.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !File.Exists(sysInfoPath) || new FileInfo(sysInfoPath).Length == 0) throw new InvalidOperationException("The iPod setup step did not complete.");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(this, "Syncing was cancelled because the one-time administrator permission was declined.", "Sync cancelled");
            return false;
        }
    }

    private void DeviceStatusArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (currentDevice is not null) IPodTab.IsChecked = true;
    }

    private void Eject_Click(object sender, RoutedEventArgs e)
    {
        if (currentDevice is null || isSyncing || isReconcilingPlayCounts || autoSyncRunning || isTagSaving || isRenaming) return;
        pendingAutoSyncRoot = null;
        DebugLog.Write("Device", $"Eject requested: {currentDevice.RootPath}");
        if (currentPodcastEpisode is not null) FinalizePodcastPlayback();
        else { player.Stop(); player.Close(); }
        EjectButton.IsEnabled = SyncCurrentButton.IsEnabled = SyncAllButton.IsEnabled = false;
        if (!IPodEjector.TryEject(currentDevice.RootPath, out var error))
        {
            DebugLog.Write("Device", "Eject failed: " + error);
            EjectButton.IsEnabled = SyncCurrentButton.IsEnabled = SyncAllButton.IsEnabled = true;
            MessageBox.Show(this, error, "Could not eject iPod", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void Play_Click(object sender, RoutedEventArgs e) { if (player.Source is null) PlaySelected(); else player.Play(); }
    private void PlaySelected()
    {
        if (isRenaming || isTagSaving || (isRenameView ? (RenameTracksGrid.SelectedItem as RenameGridRow)?.Track : isTagView ? TagTracksGrid.SelectedItem : TracksGrid.SelectedItem) is not Track track) return;
        if (currentPodcastEpisode is not null) FinalizePodcastPlayback();
        player.Open(new Uri(track.FilePath)); player.Play();
        var countedTrack = track;
        if (isIPodView)
        {
            countedTrack = FindLocalTrackForIPod(track) ?? track;
        }
        countedTrack.PlayCount++;
        track.PlayCount = countedTrack.PlayCount;
        SetNowPlaying(track.Title, $"{track.Artist} — {track.Album}", track.ArtworkPath); SaveLibrary(); TracksGrid.Items.Refresh();
        if (currentDevice is not null && allTracks.Contains(countedTrack)) { playCountSyncTimer.Stop(); playCountSyncTimer.Start(); }
    }
    private Track? FindLocalTrackForIPod(Track track)
    {
        if (TrackIdentity.MarkerId(track.DownloadIdentity) is Guid id && allTracks.FirstOrDefault(item => item.Id == id) is { } marked) return marked;
        var key = TrackIdentity.Key(track.Title, track.Artist, track.Album, track.TrackNumber);
        return allTracks.FirstOrDefault(item => TrackIdentity.Key(item.Title, item.Artist, item.Album, item.TrackNumber).Equals(key, StringComparison.OrdinalIgnoreCase) ||
            (item.PreviousMetadataIdentities ?? []).Contains(key, StringComparer.OrdinalIgnoreCase));
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { player.Pause(); CapturePodcastPlaybackProgress(); SavePodcastLibrary(); }
    private void Stop_Click(object sender, RoutedEventArgs e) { if (currentPodcastEpisode is not null) FinalizePodcastPlayback(); else { player.Stop(); ResetNowPlaying(); } }
    private void Previous_Click(object sender, RoutedEventArgs e) => MoveSelection(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NextTrack();
    private void NextTrack() => MoveSelection(1);

    private void ShuffleButton_Click(object sender, RoutedEventArgs e) => shufflePlayback = ShuffleButton.IsChecked == true;
    private void RepeatButton_Click(object sender, RoutedEventArgs e) => repeatPlayback = RepeatButton.IsChecked == true;

    private void SetNowPlaying(string title, string subtitle, string? artwork)
    {
        NowPlayingTitle.Text = string.IsNullOrWhiteSpace(title) ? "Nothing playing" : title;
        NowPlayingArtist.Text = subtitle;
        NowPlayingArt.Source = null;
        NowPlayingArtPlaceholder.Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(artwork) && (artwork.StartsWith("http", StringComparison.OrdinalIgnoreCase) || File.Exists(artwork)))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(artwork, UriKind.Absolute);
                image.EndInit();
                NowPlayingArt.Source = image;
                NowPlayingArtPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch { NowPlayingArt.Source = null; NowPlayingArtPlaceholder.Visibility = Visibility.Visible; }
        }
        playbackTimer.Start();
        UpdatePlaybackDisplay();
    }

    private void ResetNowPlaying()
    {
        SetNowPlaying("", "", null);
        playbackTimer.Stop();
        PlaybackPositionText.Text = PlaybackDurationText.Text = "0:00";
        PlaybackProgressBar.Value = 0;
        QueuePositionText.Text = QueueLabelText.Text = "";
    }

    private void UpdatePlaybackDisplay()
    {
        if (PlaybackProgressBar is null) return;
        var hasMedia = player.Source is not null;
        var duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        var position = hasMedia ? player.Position : TimeSpan.Zero;
        PlaybackPositionText.Text = FormatPlaybackTime(position);
        PlaybackDurationText.Text = duration > TimeSpan.Zero ? FormatPlaybackTime(duration) : "0:00";
        PlaybackProgressBar.Value = duration > TimeSpan.Zero ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds * 100, 0, 100) : 0;
        if (currentPodcastEpisode is not null)
        {
            QueuePositionText.Text = "Podcast";
            QueueLabelText.Text = "episode";
        }
        else if (hasMedia && VisibleTracks.Count > 0 && TracksGrid.SelectedIndex >= 0)
        {
            QueuePositionText.Text = $"{TracksGrid.SelectedIndex + 1} of {VisibleTracks.Count}";
            QueueLabelText.Text = "in queue";
        }
        else
        {
            QueuePositionText.Text = QueueLabelText.Text = "";
        }
    }

    private static string FormatPlaybackTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    private void SyncShufflePlayback(bool value) { shufflePlayback = value; if (ShuffleButton is not null) ShuffleButton.IsChecked = value; }
    private void SyncRepeatPlayback(bool value) { repeatPlayback = value; if (RepeatButton is not null) RepeatButton.IsChecked = value; }
    private void MoveSelection(int offset)
    {
        if (isRenameView)
        {
            if (isRenaming || RenameTracksGrid.Items.Count == 0) return;
            RenameTracksGrid.SelectedIndex = (Math.Max(0, RenameTracksGrid.SelectedIndex) + offset + RenameTracksGrid.Items.Count) % RenameTracksGrid.Items.Count;
            RenameTracksGrid.ScrollIntoView(RenameTracksGrid.SelectedItem); PlaySelected(); return;
        }
        if (isTagView)
        {
            if (isTagSaving || TagTracksGrid.Items.Count == 0) return;
            TagTracksGrid.SelectedIndex = (Math.Max(0, TagTracksGrid.SelectedIndex) + offset + TagTracksGrid.Items.Count) % TagTracksGrid.Items.Count;
            TagTracksGrid.ScrollIntoView(TagTracksGrid.SelectedItem); PlaySelected(); return;
        }
        if (VisibleTracks.Count == 0) return;
        if (shufflePlayback && offset > 0 && VisibleTracks.Count > 1)
        {
            var candidates = Enumerable.Range(0, VisibleTracks.Count).Where(index => index != TracksGrid.SelectedIndex && !VisibleTracks[index].ExcludeFromShuffle).ToArray();
            if (candidates.Length == 0) candidates = Enumerable.Range(0, VisibleTracks.Count).Where(index => index != TracksGrid.SelectedIndex).ToArray();
            TracksGrid.SelectedIndex = Random.Shared.GetItems(candidates, 1)[0];
        }
        else TracksGrid.SelectedIndex = (Math.Max(0, TracksGrid.SelectedIndex) + offset + VisibleTracks.Count) % VisibleTracks.Count;
        TracksGrid.ScrollIntoView(TracksGrid.SelectedItem); PlaySelected();
    }
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "hTunes\nA modern Windows music library and iPod companion.", "About hTunes");
    private void UpdateTools_Click(object sender, RoutedEventArgs e)
    {
        ToolIssue[] tools =
        [
            new(ExternalTool.FFmpeg, ToolIssueKind.Reinstall),
            new(ExternalTool.YtDlp, ToolIssueKind.Reinstall)
        ];
        new DependencySetupWindow(tools) { Owner = this }.ShowDialog();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!initializeServices) { player.Close(); base.OnClosing(e); return; }
        if (isRenaming || isTagSaving || isYtDownloading || isSyncing || isReconcilingPlayCounts || activePodcastDownloads > 0 || podcastFeedOperations > 0 || autoSyncRunning || OwnedWindows.Count > 0)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Please finish the current operation and close any hTunes dialogs before exiting.", "hTunes is busy");
            base.OnClosing(e); return;
        }
        try
        {
            if (currentPodcastEpisode is not null) FinalizePodcastPlayback();
            SavePreferences(); SavePodcastLibrary(); SaveLibrary();
        }
        catch (Exception ex) { e.Cancel = true; DebugLog.Write("App", "Save before closing failed", ex); MessageBox.Show(this, ex.Message, "Could not save library"); base.OnClosing(e); return; }
        deviceTimer.Stop(); playCountSyncTimer.Stop(); podcastPlaybackTimer.Stop(); playbackTimer.Stop(); syncParticleTimer.Stop(); ipodLoadCancellation?.Cancel(); player.Close();
        DebugLog.Write("App", "Library window closed");
        base.OnClosing(e);
    }
}

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid(); public string FilePath { get; set; } = ""; public string Title { get; set; } = "Unknown title";
    public string Artist { get; set; } = "Unknown Artist"; public string AlbumArtist { get; set; } = ""; public string Album { get; set; } = "Unknown Album"; public string Genre { get; set; } = "Unknown Genre";
    public int TrackNumber { get; set; } public int DiscNumber { get; set; } = 1; public int Year { get; set; } public int PlayCount { get; set; }
    public string Format { get; set; } = ""; public int BitrateKbps { get; set; }
    public bool IsPodcast { get; set; }
    public string OriginalImportPath { get; set; } = "";
    public string DownloadIdentity { get; set; } = "";
    public bool MetadataManagedByLibrary { get; set; }
    public bool IsNew { get; set; }
    public bool AutoTagged { get; set; }
    public bool ExcludeFromShuffle { get; set; }
    [JsonIgnore] public string BitrateDisplay => BitrateKbps > 0 ? $"{BitrateKbps} kbps" : "—";
    [JsonIgnore] public bool HasMissingMetadata => Missing(Title) || Missing(Artist) || Missing(Album) || Missing(Genre);
    private static bool Missing(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("missing", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("music", StringComparison.OrdinalIgnoreCase);
    public string? ArtworkPath { get; set; } public DateTime DateAdded { get; set; }
    public Dictionary<string, int> SyncedPlayCounts { get; set; } = [];
    public Dictionary<string, string> SyncedIPodFingerprints { get; set; } = [];
    public List<string> PreviousMetadataIdentities { get; set; } = [];
}
public sealed class Playlist { public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = "New Playlist"; public List<Guid> TrackIds { get; set; } = []; public List<string> PreviousNames { get; set; } = []; }
public sealed class LibraryData { public List<Track> Tracks { get; set; } = []; public List<Playlist> Playlists { get; set; } = []; }
