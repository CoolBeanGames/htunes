using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Point dragStart;
    private string category = "Artist";
    private List<Track> allTracks = [];
    private List<Track> ipodTracks = [];
    private IPodDevice? currentDevice;
    private bool isIPodView;
    private bool isIPodLoading;
    private bool isSyncing;
    private bool isReconcilingPlayCounts;
    private readonly bool initializeServices;
    private CancellationTokenSource? ipodLoadCancellation;
    private readonly DispatcherTimer playCountSyncTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public ObservableCollection<Track> VisibleTracks { get; } = [];
    public ObservableCollection<Playlist> Playlists { get; } = [];

    public MainWindow() : this(true) { }

    // Isolated UI checks use false: no preferences/library IO, device detection, or background timers.
    internal MainWindow(bool initializeServices, string? isolatedLibraryFile = null)
    {
        this.initializeServices = initializeServices;
        if (!initializeServices && isolatedLibraryFile is not null) dataFile = Path.GetFullPath(isolatedLibraryFile);
        InitializeComponent(); DataContext = this;
        if (initializeServices) { LoadPreferences(); LoadLibrary(); }
        InitializePodcastUi(initializeServices); InitializeContextMenus(); InitializeTagEditor(); InitializeTopMenus(); InitializeNavigation(); RefreshBrowser();
        if (!initializeServices) return;
        RefreshDevice();
        player.MediaEnded += (_, _) => { if (currentPodcastEpisode is not null) PodcastPlaybackEnded(); else NextTrack(); };
        deviceTimer.Tick += (_, _) => RefreshDevice();
        playCountSyncTimer.Tick += async (_, _) => { playCountSyncTimer.Stop(); if (currentDevice is not null) await ReconcilePlayCountsAsync(currentDevice); };
        deviceTimer.Start();
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
            foreach (var playlist in data.Playlists) Playlists.Add(playlist);
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

    private void TranscodeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        try { SavePreferences(); }
        catch (Exception ex) { DebugLog.Write("Settings", "Transcode preference save failed", ex); MessageBox.Show(this, ex.Message, "Could not save preference"); }
    }

    private void RefreshBrowser()
    {
        if (isTagView) RefreshTagLibrary();
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
        PlaylistsHeading.Visibility = PlaylistList.Visibility = NewPlaylistButton.Visibility = isIPodView ? Visibility.Collapsed : Visibility.Visible;
        PlaylistsHeadingRow.Height = NewPlaylistRow.Height = isIPodView ? new GridLength(0) : GridLength.Auto;
        PlaylistsListRow.Height = isIPodView ? new GridLength(0) : new GridLength(150);
        EditMetadataButton.Visibility = isIPodView ? Visibility.Collapsed : Visibility.Visible;
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
            "Artist" => source.Select(t => t.Artist).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
            "Album" => source.Select(t => t.Album).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
            "Genre" => source.Select(t => t.Genre).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
            "Podcast" => source.Select(t => t.Album).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
            _ => null
        });
        ReplaceBrowseItems(SecondaryList, category == "Artist" && musicBrowse.Group is not null
            ? source.Where(t => Same(t.Artist, musicBrowse.Group)).Select(t => t.Album).Distinct(StringComparer.OrdinalIgnoreCase).Order() : null);
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
    }

    private List<Track> SourceTracks => isIPodView ? ipodTracks : allTracks;

    private void SetVisibleTracks(IEnumerable<Track> tracks, bool preserveOrder = false)
    {
        var ordered = preserveOrder ? tracks.ToList() : tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title).ToList();
        VisibleTracks.Clear();
        foreach (var track in ordered) VisibleTracks.Add(track);
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
        isIPodView = tab == "IPod";
        IPodPodcastsCategoryButton.Visibility = isIPodView ? Visibility.Visible : Visibility.Collapsed;
        if (!isIPodView && category == "Podcast") ArtistCategoryButton.IsChecked = true;
        MusicWorkspace.Visibility = isPodcastView || isDownloadView || isTagView ? Visibility.Collapsed : Visibility.Visible;
        PodcastWorkspace.Visibility = isPodcastView ? Visibility.Visible : Visibility.Collapsed;
        DownloadWorkspace.Visibility = isDownloadView ? Visibility.Visible : Visibility.Collapsed;
        TagWorkspace.Visibility = isTagView ? Visibility.Visible : Visibility.Collapsed;
        UpdateDeviceStripMode();
        if (isTagView) RefreshTagLibrary();
        else if (isPodcastView && !isYtDownloading && !isTagSaving) _ = EnterPodcastViewAsync(); else if (!isDownloadView && !isPodcastView) ResetMusicNavigation();
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
        if (isSyncing || isReconcilingPlayCounts || autoSyncRunning || isYtDownloading || isTagSaving) return;
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
                var imported = ImportFileService.Prepare(fullPath, SettingsStore.Current);
                var track = new Track { FilePath = imported.LibraryPath, OriginalImportPath = fullPath, Title = Path.GetFileNameWithoutExtension(file), DateAdded = DateTime.Now };
                MediaMetadata.ReadInto(track);
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

    private List<Track> SelectedTracks() => isTagView ? TagSelection : TracksGrid.SelectedItems.Cast<Track>().ToList();
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
        CreateLocalPlaylist($"New Playlist {Playlists.Count + 1}");
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
            "Artist" => allTracks.Where(t => PrimaryList.SelectedItems.Cast<string>().Any(v => Same(t.Artist, v))),
            "Album" => allTracks.Where(t => PrimaryList.SelectedItems.Cast<string>().Any(v => Same(t.Album, v))),
            "Genre" => allTracks.Where(t => PrimaryList.SelectedItems.Cast<string>().Any(v => Same(t.Genre, v))),
            _ => []
        });
    }
    private void SecondaryList_MouseMove(object sender, MouseEventArgs e)
    {
        if (isIPodView || category != "Artist" || musicBrowse.Group is not string artist) return;
        StartCategoryDrag(e, allTracks.Where(t => Same(t.Artist, artist) && SecondaryList.SelectedItems.Cast<string>().Any(v => Same(t.Album, v))));
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

    private void TracksGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected();

    private void RefreshDevice()
    {
        if (isSyncing || isReconcilingPlayCounts || isYtDownloading || isTagSaving) return;
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
            DeviceNameText.Text = "No iPod connected";
            DeviceDetailsText.Text = "  •  Connect an iPod to view capacity and sync music";
            SyncAllButton.IsEnabled = EjectButton.IsEnabled = false;
            DeviceStatusArea.Cursor = Cursors.Arrow;
            IPodTab.Visibility = Visibility.Collapsed;
            DeviceStrip.Tag = null;
            if (wasConnected && isIPodView) MusicTab.IsChecked = true;
            return;
        }
        var isNewDevice = currentDevice is null || !Same(currentDevice.RootPath, device.RootPath);
        currentDevice = device;
        DeviceIndicator.Fill = new SolidColorBrush(Color.FromRgb(46, 160, 90));
        DeviceNameText.Text = device.Name;
        DeviceDetailsText.Text = $"  •  {FormatBytes(device.Capacity)} capacity  •  {FormatBytes(device.FreeSpace)} free";
        SyncAllButton.IsEnabled = EjectButton.IsEnabled = true;
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
        if (isTagSaving || isYtDownloading || isReconcilingPlayCounts || (isSyncing && !duringSync)) return;
        var sysInfoPath = Path.Combine(device.RootPath, "iPod_Control", "Device", "SysInfoExtended");
        if (!File.Exists(sysInfoPath) || new FileInfo(sysInfoPath).Length == 0) return;
        isReconcilingPlayCounts = true;
        UpdateBusyWorkspaces();
        SyncAllButton.IsEnabled = EjectButton.IsEnabled = false;
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
            DeviceDetailsText.Text = $"  •  Play counts could not be synchronized: {ex.GetBaseException().Message}";
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
            if (currentDevice is not null && Same(currentDevice.RootPath, device.RootPath))
            {
                foreach (var ipodTrack in tracks)
                {
                    var local = allTracks.FirstOrDefault(t => TrackIdentity.Key(t.Title, t.Artist, t.Album, t.TrackNumber)
                        .Equals(TrackIdentity.Key(ipodTrack.Title, ipodTrack.Artist, ipodTrack.Album, ipodTrack.TrackNumber), StringComparison.OrdinalIgnoreCase));
                    if (local is not null)
                    {
                        ipodTrack.PlayCount = local.PlayCount;
                        ipodTrack.ArtworkPath = local.ArtworkPath;
                    }
                }
                ipodTracks = tracks;
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
                Album = string.IsNullOrWhiteSpace(item.Album) ? "Unknown Album" : item.Album,
                Genre = string.IsNullOrWhiteSpace(item.Genre) ? "Unknown Genre" : item.Genre,
                TrackNumber = checked((int)item.TrackNumber),
                Year = checked((int)item.Year),
                PlayCount = Math.Max(0, item.PlayCount),
                Format = Path.GetExtension(file).TrimStart('.').ToUpperInvariant(),
                BitrateKbps = checked((int)item.Bitrate),
                IsPodcast = isPodcast
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

    private async void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        if (isPodcastView) await SyncAllPodcastsAsync();
        else await SyncTracksAsync(allTracks.Select(t => t.Id), randomFill: true);
    }

    private void UpdateDeviceStripMode()
    {
        if (TranscodeComboBox is null || SyncAllButton is null) return;
        TranscodeComboBox.Visibility = isPodcastView || isDownloadView || isTagView ? Visibility.Collapsed : Visibility.Visible;
        if (!isSyncing) SyncAllButton.Content = isPodcastView ? "Sync podcasts" : "Sync all";
    }

    private async Task SyncTracksAsync(IEnumerable<Guid> ids, bool randomFill, Playlist? playlist = null, bool showSummary = true)
    {
        if (isTagSaving || isYtDownloading || isSyncing || isReconcilingPlayCounts || currentDevice is null) return;
        var requestedIds = ids.ToHashSet();
        var requested = allTracks.Where(t => requestedIds.Contains(t.Id)).ToList();
        if (requested.Count == 0 && playlist is null) { if (showSummary) MessageBox.Show(this, "There are no library tracks in this selection.", "Nothing to sync"); return; }
        var device = currentDevice;
        var preset = TranscodePresets.Get(TranscodeComboBox.SelectedValue as string);
        isSyncing = true; deviceTimer.Stop();
        UpdateBusyWorkspaces();
        SyncAllButton.IsEnabled = EjectButton.IsEnabled = false;
        TranscodeComboBox.IsEnabled = false;
        SyncAllButton.Content = "Syncing…";
        try
        {
            DebugLog.Write("Music sync", $"Starting {requested.Count} tracks; randomFill={randomFill}; preset={TranscodeComboBox.SelectedValue}");
            if (!await EnsureIPodPreparedAsync(device)) return;
            var progress = new Progress<SyncProgress>(p => DeviceDetailsText.Text = $"  •  {p.Message}  ({Math.Min(p.Completed + 1, p.Total)}/{p.Total})");
            var result = requested.Count == 0
                ? new SyncResult(0, 0, 0, 0, 0, 0)
                : await Task.Run(() => IPodSyncService.Sync(device.RootPath, requested, allTracks, randomFill, preset, progress));
            IPodPlaylistSyncResult? playlistResult = null;
            if (playlist is not null)
            {
                DeviceDetailsText.Text = $"  •  Updating playlist {playlist.Name}";
                playlistResult = await Task.Run(() => IPodPlaylistSyncService.Sync(device.RootPath, playlist, allTracks));
            }
            currentDevice = IPodDetector.FindConnected();
            if (currentDevice is not null)
            {
                await ReconcilePlayCountsAsync(currentDevice, duringSync: true);
                await LoadIPodTracksAsync(currentDevice);
                if (showSummary) IPodTab.IsChecked = true;
            }
            var summary = playlistResult is null ? result.Summary : $"{result.Summary}\n{playlistResult.Summary}";
            DebugLog.Write("Music sync", summary);
            if (showSummary) MessageBox.Show(this, summary, "Sync complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Music sync", "Sync failed", ex);
            MessageBox.Show(this, $"The sync was stopped and the previous iPod database was restored.\n\n{ex.GetBaseException().Message}", "Sync failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            isSyncing = false; TranscodeComboBox.IsEnabled = true; SyncAllButton.Content = "Sync all"; deviceTimer.Start(); RefreshDevice(); UpdateBusyWorkspaces();
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
        if (currentDevice is null || isSyncing || isReconcilingPlayCounts || autoSyncRunning || isYtDownloading || isTagSaving) return;
        pendingAutoSyncRoot = null;
        DebugLog.Write("Device", $"Eject requested: {currentDevice.RootPath}");
        if (currentPodcastEpisode is not null) FinalizePodcastPlayback();
        else { player.Stop(); player.Close(); }
        EjectButton.IsEnabled = SyncAllButton.IsEnabled = false;
        if (!IPodEjector.TryEject(currentDevice.RootPath, out var error))
        {
            DebugLog.Write("Device", "Eject failed: " + error);
            EjectButton.IsEnabled = SyncAllButton.IsEnabled = true;
            MessageBox.Show(this, error, "Could not eject iPod", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void Play_Click(object sender, RoutedEventArgs e) { if (player.Source is null) PlaySelected(); else player.Play(); }
    private void PlaySelected()
    {
        if (isTagSaving || (isTagView ? TagTracksGrid.SelectedItem : TracksGrid.SelectedItem) is not Track track) return;
        if (currentPodcastEpisode is not null) FinalizePodcastPlayback();
        player.Open(new Uri(track.FilePath)); player.Play();
        var countedTrack = track;
        if (isIPodView)
        {
            countedTrack = allTracks.FirstOrDefault(t => TrackIdentity.Key(t.Title, t.Artist, t.Album, t.TrackNumber)
                .Equals(TrackIdentity.Key(track.Title, track.Artist, track.Album, track.TrackNumber), StringComparison.OrdinalIgnoreCase)) ?? track;
        }
        countedTrack.PlayCount++;
        track.PlayCount = countedTrack.PlayCount;
        NowPlayingTitle.Text = track.Title; NowPlayingArtist.Text = $"{track.Artist} — {track.Album}"; SaveLibrary(); TracksGrid.Items.Refresh();
        if (currentDevice is not null && allTracks.Contains(countedTrack)) { playCountSyncTimer.Stop(); playCountSyncTimer.Start(); }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { player.Pause(); CapturePodcastPlaybackProgress(); SavePodcastLibrary(); }
    private void Stop_Click(object sender, RoutedEventArgs e) { if (currentPodcastEpisode is not null) FinalizePodcastPlayback(); else player.Stop(); }
    private void Previous_Click(object sender, RoutedEventArgs e) => MoveSelection(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NextTrack();
    private void NextTrack() => MoveSelection(1);
    private void MoveSelection(int offset)
    {
        if (isTagView)
        {
            if (isTagSaving || TagTracksGrid.Items.Count == 0) return;
            TagTracksGrid.SelectedIndex = (Math.Max(0, TagTracksGrid.SelectedIndex) + offset + TagTracksGrid.Items.Count) % TagTracksGrid.Items.Count;
            TagTracksGrid.ScrollIntoView(TagTracksGrid.SelectedItem); PlaySelected(); return;
        }
        if (VisibleTracks.Count == 0) return;
        TracksGrid.SelectedIndex = (Math.Max(0, TracksGrid.SelectedIndex) + offset + VisibleTracks.Count) % VisibleTracks.Count; TracksGrid.ScrollIntoView(TracksGrid.SelectedItem); PlaySelected();
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
        if (isTagSaving || isYtDownloading || isSyncing || isReconcilingPlayCounts || activePodcastDownloads > 0 || podcastFeedOperations > 0 || autoSyncRunning || OwnedWindows.Count > 0)
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
        deviceTimer.Stop(); playCountSyncTimer.Stop(); podcastPlaybackTimer.Stop(); ipodLoadCancellation?.Cancel(); player.Close();
        DebugLog.Write("App", "Library window closed");
        base.OnClosing(e);
    }
}

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid(); public string FilePath { get; set; } = ""; public string Title { get; set; } = "Unknown title";
    public string Artist { get; set; } = "Unknown Artist"; public string Album { get; set; } = "Unknown Album"; public string Genre { get; set; } = "Unknown Genre";
    public int TrackNumber { get; set; } public int DiscNumber { get; set; } = 1; public int Year { get; set; } public int PlayCount { get; set; }
    public string Format { get; set; } = ""; public int BitrateKbps { get; set; }
    public bool IsPodcast { get; set; }
    public string OriginalImportPath { get; set; } = "";
    public string DownloadIdentity { get; set; } = "";
    public bool MetadataManagedByLibrary { get; set; }
    [JsonIgnore] public string BitrateDisplay => BitrateKbps > 0 ? $"{BitrateKbps} kbps" : "—";
    public string? ArtworkPath { get; set; } public DateTime DateAdded { get; set; }
    public Dictionary<string, int> SyncedPlayCounts { get; set; } = [];
}
public sealed class Playlist { public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = "New Playlist"; public List<Guid> TrackIds { get; set; } = []; }
public sealed class LibraryData { public List<Track> Tracks { get; set; } = []; public List<Playlist> Playlists { get; set; } = []; }
