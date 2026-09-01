using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HTunes.App;

internal static partial class Program
{
    private static readonly MethodInfo SelectTarget = typeof(MainWindow).GetMethod("SelectContextItem", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo AttachMenu = typeof(MainWindow).GetMethod("AttachItemMenu", BindingFlags.NonPublic | BindingFlags.Static)!;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--yt-test-child") return YtTestChild(args);
        try
        {
            // Exercise controls without constructing MainWindow, opening a window, reading user data,
            // or connecting to an iPod. This exercises the actual selection helper used by every menu.
            CheckListSelection();
            CheckGridSelection();
            CheckHistory();
            CheckMetadataHistoryIsolation();
            CheckTopMenuRebuild();
            CheckSettings();
            CheckImportFileSafety();
            CheckPodcastPolicies();
            CheckNewStateAndSyncIdentity();
            CheckSettingsWindow();
            CheckSinglePanelNavigation();
            CheckYtDlp();
            CheckTagEditor();
            CheckRenameEditor();
            if (args is ["--check-ytdlp-tools", var yt, var ffmpeg]) CheckLocalYtDlp(yt, ffmpeg);
            Console.WriteLine("PASS: menus/history, settings, import safety, podcasts, navigation, yt-dlp, batch tags, and Rename operations/rollback/scope/UI.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckListSelection()
    {
        var list = new ListBox { SelectionMode = SelectionMode.Extended };
        list.Items.Add("First"); list.Items.Add("Second"); list.Items.Add("Third");
        list.SelectedItems.Add("First"); list.SelectedItems.Add("Second");
        Select(list, "Second");
        Require(list.SelectedItems.Count == 2, "Right-click must preserve an existing list selection.");
        Select(list, "Third");
        Require(list.SelectedItems.Count == 1 && Equals(list.SelectedItem, "Third"), "An unselected list item must become the only target.");
        Select(list, null);
        Require(list.SelectedItems.Count == 0, "Empty list space must clear the target.");
        AttachMenu.Invoke(null, [list, (Action<ContextMenu>)(menu => menu.Items.Add(new MenuItem { Header = "Test" }))]);
        Require(list.ContextMenu is not null, "Attach a menu before the first mouse or keyboard opening.");
    }

    private static void CheckGridSelection()
    {
        var grid = new DataGrid { AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow };
        grid.Columns.Add(new DataGridTextColumn { Header = "Title", Binding = new System.Windows.Data.Binding("Title") });
        var first = new PodcastEpisode { Title = "First" };
        var second = new PodcastEpisode { Title = "Second" };
        var third = new PodcastEpisode { Title = "Third" };
        grid.ItemsSource = new[] { first, second, third };
        grid.SelectedItems.Add(first); grid.SelectedItems.Add(second);
        Select(grid, second);
        Require(grid.SelectedItems.Count == 2, "Right-click must preserve the selected grid rows.");
        Select(grid, third);
        Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItem, third), "An unselected grid row must replace the old targets.");
        Select(grid, null);
        Require(grid.SelectedItems.Count == 0, "Empty grid space must not act on stale rows.");
    }

    private static void Select(ItemsControl control, object? target) => SelectTarget.Invoke(null, [control, target]);

    private static void CheckHistory()
    {
        var history = new EditHistory(2);
        var value = 1;
        history.Record("first", () => value = 0, () => value = 1);
        Require(history.CanUndo && !history.CanRedo && history.UndoDescription == "first", "Record must expose an undo description.");
        history.Undo();
        Require(value == 0 && history.CanRedo && !history.CanUndo, "Undo must move the edit to redo.");
        history.Redo();
        Require(value == 1 && history.CanUndo && !history.CanRedo, "Redo must replay the edit.");
        value = 2;
        history.Record("second", () => value = 1, () => value = 2);
        value = 3;
        history.Record("third", () => value = 2, () => value = 3);
        history.Undo(); history.Undo(); history.Undo();
        Require(value == 1 && !history.CanUndo, "History must evict edits beyond its limit.");
        history.Record("new branch", () => value = 1, () => value = 4);
        Require(!history.CanRedo, "A new edit after undo must discard the old redo branch.");

        var failing = new EditHistory();
        failing.Record("failure", () => throw new InvalidOperationException("expected"), () => { });
        try { failing.Undo(); }
        catch (InvalidOperationException) { }
        Require(failing.CanUndo && !failing.CanRedo, "A failed undo must not remove its history entry.");
    }

    private static void CheckNewStateAndSyncIdentity()
    {
        InTemporaryDirectory(directory =>
        {
            var path = Path.Combine(directory, "track.wav");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var track = new Track { FilePath = path, Title = "Song", Artist = "Artist", Album = "Album", Genre = "Rock", TrackNumber = 1, Year = 2020, IsNew = true };
            var marker = TrackIdentity.Marker(track.Id);
            Require(TrackIdentity.MarkerId(marker) == track.Id && TrackIdentity.MarkerId("ordinary comment") is null, "Stable hTunes iPod markers must round-trip without treating normal comments as IDs.");
            TrackIdentity.RememberPrevious(track, "Old song", "Artist", "Album", 1);
            Require(track.PreviousMetadataIdentities.Count == 1, "Retagging must retain an old iPod identity for migration.");
            var preset = TranscodePresets.Get("original");
            var fingerprint = IPodSyncService.DesiredFingerprint(track, preset);
            track.Genre = "Alternative";
            Require(IPodSyncService.DesiredFingerprint(track, preset) != fingerprint, "Genre edits must make an existing iPod copy eligible for replacement.");
            fingerprint = IPodSyncService.DesiredFingerprint(track, preset);
            track.AlbumArtist = "Artist Sound Team";
            Require(IPodSyncService.DesiredFingerprint(track, preset) != fingerprint, "Album-artist edits must make an existing iPod copy eligible for replacement.");
            Require(MusicBrainzTagService.SimplifyGenre(["hip hop", "r&b"], "", "") == "Rap" &&
                MusicBrainzTagService.SimplifyGenre(["video game soundtrack", "metal"], "", "") == "Game Music" &&
                MusicBrainzTagService.SimplifyGenre([], "My Chemical Romance", "") == "Emo", "Auto-tag genre policy must apply the requested broad categories and soundtrack priority.");

            var second = new Track { Artist = "Artist", Album = "Other", IsNew = false };
            var grouped = new[] { track, second }.GroupBy(item => item.Artist).Single();
            Require(grouped.Any(item => item.IsNew) && new[] { track, second }.Where(item => item.Album == "Album").Single().IsNew,
                "A new song must roll up only to its own album and artist.");
            Require(PodcastEpisodeOrdering.Number(new PodcastEpisode { EpisodeNumber = "10" }) == 10 &&
                PodcastEpisodeOrdering.Order([new() { EpisodeNumber = "10" }, new() { EpisodeNumber = "2" }], oldest: true).First().EpisodeNumber == "2",
                "Podcast episode ordering must be numeric rather than lexical.");
        });
    }

    private static void CheckMetadataHistoryIsolation()
    {
        var track = new Track { Title = "Original", PlayCount = 3, FilePath = "unchanged.mp3" };
        var type = typeof(MainWindow).GetNestedType("TrackMetadata", BindingFlags.NonPublic)!;
        var read = type.GetMethod("Read")!;
        var apply = type.GetMethod("Apply")!;
        var before = read.Invoke(null, [track]);
        track.Title = "Edited";
        var after = read.Invoke(null, [track]);
        var history = new EditHistory();
        history.Record("metadata", () => apply.Invoke(before, [track]), () => apply.Invoke(after, [track]));
        track.PlayCount = 7; // Listening after an edit is not itself part of edit history.
        history.Undo();
        Require(track.Title == "Original" && track.PlayCount == 7 && track.FilePath == "unchanged.mp3", "Undo metadata must preserve playback counts and file identity.");
        history.Redo();
        Require(track.Title == "Edited" && track.PlayCount == 7, "Redo metadata must preserve later playback counts.");
    }

    private static void CheckTopMenuRebuild()
    {
        var attach = typeof(MainWindow).GetMethod("AttachTopMenu", BindingFlags.NonPublic | BindingFlags.Static)!;
        var menu = new MenuItem { Header = "Edit" };
        menu.Items.Add(new MenuItem { Header = "Placeholder" });
        var count = 0;
        attach.Invoke(null, [menu, (Action<ItemsControl>)(target => { count++; target.Items.Add(new MenuItem { Header = $"Action {count}" }); })]);
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));
        Require(count == 1 && menu.Items.Count == 1 && Equals(((MenuItem)menu.Items[0]).Header, "Action 1"), "Top menu must rebuild its actions when opened.");
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu.Items[0]));
        Require(count == 1, "Opening a nested submenu must not rebuild its parent.");
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));
        Require(count == 2 && menu.Items.Count == 1, "Reopening must replace, not duplicate, menu actions.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckSettings()
    {
        InTemporaryDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"TranscodePresetId\":\"mp3-192\"}");
            var legacy = SettingsStore.Read(path);
            Require(legacy.TranscodePresetId == "mp3-192" && legacy.PodcastPlayedPercent == 50 && legacy.ImportMode == ImportFileMode.Reference,
                "Old settings must retain the preset and receive safe new defaults.");
            Require(!legacy.OpenOnIPodConnection && !legacy.AutoSyncOnConnection && !legacy.DebugLogging, "Background launch, sync, and logging must be opt-in.");
            var changed = legacy.Clone();
            changed.DownloadDirectory = Path.Combine(directory, "downloads with spaces & punctuation");
            changed.PodcastDirectory = Path.Combine(directory, "podcasts");
            changed.ImportDirectory = Path.Combine(directory, "music");
            changed.ImportMode = ImportFileMode.Copy;
            changed.PodcastDefaultCount = 8; changed.PodcastPlayedPercent = 75;
            changed.PodcastDeletePlayedDownloads = false; changed.PodcastIncludeDownloaded = false;
            changed.YtPlaylistAsAlbum = true; changed.YtEmbedMetadata = false;
            changed.YtAudioFormat = "flac"; changed.YtAudioQuality = "0";
            changed.CheckToolUpdatesOnStartup = false;
            SettingsStore.Write(path, changed);
            var loaded = SettingsStore.Read(path);
            Require(JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(changed), "All settings must round-trip.");
            Require(SettingsStore.Read(path + ".bak").TranscodePresetId == "mp3-192", "Saving must preserve the preceding settings as a backup.");
            loaded.TranscodePresetId = "original";
            SettingsStore.Write(path, loaded);
            Require(SettingsStore.Read(path).PodcastPlayedPercent == 75, "Saving a transcode preference must not erase other settings.");
            Require(legacy.ImportMode == ImportFileMode.Reference && legacy.PodcastDefaultCount == 3, "Editing a clone must not change live preferences before Save.");
            var arguments = YtDlpSettings.BuildArguments(changed).ToList();
            Require(arguments.Contains("--embed-metadata") && arguments.Contains("%(playlist_title,album|)s:%(meta_album)s"), "Playlist album metadata requires metadata embedding and must preserve album metadata when no playlist exists.");
            Require(arguments[arguments.IndexOf("--paths") + 1] == changed.DownloadDirectory, "A path must be one literal argument, never shell text.");
            Require(arguments.Contains("--no-playlist") && !arguments.Contains("--yes-playlist"), "Playlist download must default off.");
            var invalid = changed.Clone(); invalid.PodcastPlayedPercent = 0;
            ExpectFailure(() => SettingsStore.Write(path, invalid), "Invalid settings must be rejected before replacing the saved file.");
            Require(SettingsStore.Read(path).PodcastPlayedPercent == 75, "Rejected settings must leave the saved file unchanged.");
            invalid = changed.Clone(); invalid.ImportDirectory = "relative\\folder";
            ExpectFailure(() => SettingsStore.Validate(invalid), "Relative storage paths must be rejected.");
            invalid.ImportDirectory = Path.Combine(directory, "invalid*folder");
            ExpectFailure(() => SettingsStore.Validate(invalid), "Invalid folder names must be rejected.");
            invalid.ImportDirectory = path;
            ExpectFailure(() => SettingsStore.Validate(invalid), "A file cannot be used as a storage folder.");
            Require(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Settings writes must clean temporary files.");
            File.WriteAllText(path, "not json");
            ExpectFailure(() => SettingsStore.Read(path), "Corrupt JSON must be detected.");
        });
        var redacted = DebugLog.Sanitize("failure https://example.org/private?token=secret\r\nsecond line");
        Require(!redacted.Contains("secret") && !redacted.Contains('\n') && redacted.Contains("[URL removed]"), "Debug logs must redact URLs and normalize newlines.");
    }

    private static void CheckImportFileSafety()
    {
        InTemporaryDirectory(directory =>
        {
            var sourceFolder = Path.Combine(directory, "source");
            Directory.CreateDirectory(sourceFolder);
            var source = Path.Combine(sourceFolder, "track.mp3");
            File.WriteAllText(source, "test audio bytes");
            var settings = new AppPreferences { ImportDirectory = Path.Combine(directory, "managed") };
            var reference = ImportFileService.Prepare(source, settings);
            Require(reference.LibraryPath == source && !reference.DeleteSourceAfterSave && !Directory.Exists(settings.ImportDirectory), "Reference import must leave files untouched.");
            settings.ImportMode = ImportFileMode.Copy;
            var copy = ImportFileService.Prepare(source, settings);
            Require(File.ReadAllText(copy.LibraryPath) == "test audio bytes" && File.Exists(source) && !copy.DeleteSourceAfterSave, "Copy import must preserve original bytes and file.");
            var second = ImportFileService.Prepare(source, settings);
            Require(second.LibraryPath != copy.LibraryPath && File.Exists(copy.LibraryPath), "Name collisions must not overwrite existing files.");
            var alreadyManaged = ImportFileService.Prepare(copy.LibraryPath, settings);
            Require(alreadyManaged.LibraryPath == copy.LibraryPath && !alreadyManaged.DeleteSourceAfterSave, "Files already in the managed folder must not copy onto themselves.");
            settings.ImportMode = ImportFileMode.Move;
            var move = ImportFileService.Prepare(source, settings);
            Require(move.DeleteSourceAfterSave && File.Exists(source) && File.Exists(move.LibraryPath), "Preparing a move must keep the original until the library save completes.");
            ImportFileService.CompleteMove(move);
            Require(!File.Exists(source) && File.ReadAllText(move.LibraryPath) == "test audio bytes", "Completing a verified move removes only the original.");
            File.WriteAllText(source, "original");
            var changed = ImportFileService.Prepare(source, settings);
            File.WriteAllText(source, "changed after copy");
            ExpectFailure(() => ImportFileService.CompleteMove(changed), "Changed original must be retained.");
            Require(File.Exists(source), "Original must survive failed verification.");
            var damagedCopy = ImportFileService.Prepare(source, settings);
            File.WriteAllText(damagedCopy.LibraryPath, "damaged destination");
            ExpectFailure(() => ImportFileService.CompleteMove(damagedCopy), "Changed destination must prevent original deletion.");
            Require(File.Exists(source), "Original must survive a damaged copy.");
            Require(!Directory.EnumerateFiles(settings.ImportDirectory, "*.tmp").Any(), "Import must clean staging files.");
        });
    }

    private static void CheckPodcastPolicies()
    {
        Require(!PodcastService.ReachedPlayedThreshold(499, 1000, 50) && PodcastService.ReachedPlayedThreshold(500, 1000, 50), "Default threshold is exactly 50 percent.");
        Require(!PodcastService.ReachedPlayedThreshold(749, 1000, 75) && PodcastService.ReachedPlayedThreshold(750, 1000, 75), "Custom threshold must apply exactly.");
        Require(!PodcastService.ReachedPlayedThreshold(100, 0, 50), "Unknown duration must not mark an episode played.");
        Require(PodcastService.ReachedPlayedThreshold(long.MaxValue, long.MaxValue, 100), "Threshold calculation must not overflow.");
        InTemporaryDirectory(directory =>
        {
            var downloaded = Path.Combine(directory, "old.mp3"); File.WriteAllText(downloaded, "test");
            var oldest = new PodcastEpisode { Id = "old", PublishedUtc = DateTime.UtcNow.AddDays(-2), LocalPath = downloaded };
            var newest = new PodcastEpisode { Id = "new", PublishedUtc = DateTime.UtcNow };
            var played = new PodcastEpisode { Id = "played", PublishedUtc = DateTime.UtcNow.AddDays(1), IsPlayed = true };
            var show = new PodcastShow { SyncEpisodeCount = 1, SyncOrder = "Newest", Episodes = [oldest, newest, played] };
            Require(PodcastService.EpisodesForSync(show, false).SequenceEqual([newest]), "Strict rule must select only the newest unplayed episode.");
            Require(PodcastService.EpisodesForSync(show, true).SequenceEqual([newest, oldest]), "Include-downloads must retain downloaded episodes outside the rule.");
            show.SyncOrder = "Oldest";
            Require(PodcastService.EpisodesForSync(show, false).SequenceEqual([oldest]), "Oldest selection must be supported.");
            show.SyncEpisodeCount = 0;
            Require(PodcastService.EpisodesForSync(show, false).Count == 0, "Zero with include-downloads off must select no episodes.");
            using (var locked = new FileStream(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                PodcastService.DeleteDownload(oldest);
                Require(oldest.LocalPath == downloaded, "Failed deletion must retain the file reference for retry.");
            }
            PodcastService.DeleteDownload(oldest);
            Require(oldest.LocalPath is null && !File.Exists(downloaded), "A successful deletion must clear the file reference.");
        });
    }

    private static void CheckSettingsWindow()
    {
        var settings = new AppPreferences();
        var saved = false;
        var window = new SettingsWindow(settings, _ => saved = true);
        var root = (DockPanel)window.Content;
        root.Measure(new Size(730, 600)); root.Arrange(new Rect(0, 0, 730, 600)); root.UpdateLayout();
        var tabs = root.Children.OfType<TabControl>().Single();
        Require(tabs.Items.Count == 5, "Settings must expose all five sections.");
        foreach (TabItem tab in tabs.Items)
        {
            tabs.SelectedItem = tab; root.UpdateLayout();
            Require(((ScrollViewer)tab.Content).Content is StackPanel panel && panel.Children.Count > 0, "Every settings section must have working controls.");
        }
        Require(!saved && settings.ImportMode == ImportFileMode.Reference, "Opening settings must not save or change user data.");
        window.Close();
    }

    private static void ExpectFailure(Action action, string message)
    {
        try { action(); } catch (Exception) { return; }
        throw new InvalidOperationException(message);
    }

    private static void CheckSinglePanelNavigation()
    {
        var window = new MainWindow(initializeServices: false);
        const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        void Set(string field, object value) => typeof(MainWindow).GetField(field, privateInstance)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, privateInstance)!.Invoke(window, args);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Capture(string name)
        {
            var directory = Environment.GetEnvironmentVariable("HTUNES_UI_CHECK_OUTPUT");
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            var root = (Grid)window.Content;
            root.Background = window.Background;
            root.Measure(new Size(1280, 750)); root.Arrange(new Rect(0, 0, 1280, 750)); root.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1280, 750, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
        void Page(string name)
        {
            foreach (var panel in new[] { "PrimaryPanel", "SecondaryPanel", "TracksPanel" })
                Require(Control<FrameworkElement>(panel).Visibility == (panel == name ? Visibility.Visible : Visibility.Collapsed), "Exactly one music panel must be visible: " + name);
        }
        try
        {
            var first = new Track { Title = "First", Artist = "Alpha", Album = "Shared", Genre = "Rock" };
            var second = new Track { Title = "Second", Artist = "Alpha", Album = "Other", Genre = "Rock" };
            var third = new Track { Title = "Third", Artist = "Beta", Album = "Shared", Genre = "Jazz" };
            Set("allTracks", new List<Track> { first, second, third });
            Call("RefreshBrowser"); Page("PrimaryPanel");
            Capture("artists");
            var primary = Control<ListBox>("PrimaryList");
            var secondary = Control<ListBox>("SecondaryList");
            BrowseItem Item(ListBox list, string name) => list.Items.Cast<BrowseItem>().Single(item => item.Name == name);
            primary.SelectedItem = Item(primary, "Alpha");
            Page("PrimaryPanel"); // Programmatic/right-click/keyboard selection alone must not navigate.
            primary.SelectedItems.Add(Item(primary, "Beta"));
            Require(((List<Track>)Call("ContextCategoryTracks", false)!).Count == 3, "Multiple artists must still resolve to all their tracks.");
            primary.SelectedItems.Clear(); primary.SelectedItem = Item(primary, "Alpha");
            Call("OpenBrowseItem", primary); Page("SecondaryPanel");
            Capture("artist-albums");
            Require(secondary.Items.Count == 2 && window.VisibleTracks.Count == 0, "Opening an artist must show albums, not songs alongside them.");
            secondary.SelectedItem = Item(secondary, "Shared");
            Require(((List<Track>)Call("ContextCategoryTracks", true)!).SequenceEqual([first]), "Album actions must stay scoped to the opened artist.");
            Call("OpenBrowseItem", secondary); Page("TracksPanel");
            Capture("album-songs");
            Require(window.VisibleTracks.SequenceEqual([first]), "Artist album drill-down must exclude another artist's same-named album.");
            Call("RefreshBrowser"); Page("TracksPanel");
            Require(window.VisibleTracks.SequenceEqual([first]), "Refresh must preserve the current drill-down.");
            Control<TextBox>("SearchBox").Text = "missing"; Call("RefreshBrowser");
            Require(window.VisibleTracks.Count == 0, "Search must filter the opened page without escaping to the root.");
            Control<TextBox>("SearchBox").Text = ""; Call("RefreshBrowser");
            Call("GoBack"); Page("SecondaryPanel");
            Require((secondary.SelectedItem as BrowseItem)?.Name == "Shared", "Back must preserve the previously opened album selection.");
            Call("GoBack"); Page("PrimaryPanel");
            Require((primary.SelectedItem as BrowseItem)?.Name == "Alpha" && Control<Button>("MusicBackButton").Visibility == Visibility.Collapsed, "Back from artist albums must return to artists without another Back level.");
            Set("category", "Album"); Call("ResetMusicNavigation");
            primary.SelectedItem = Item(primary, "Other"); Call("OpenBrowseItem", primary); Page("TracksPanel");
            Require(window.VisibleTracks.SequenceEqual([second]), "Albums must open directly to songs.");
            Call("GoBack"); Page("PrimaryPanel");
            Set("category", "Genre"); Call("ResetMusicNavigation");
            primary.SelectedItem = Item(primary, "Rock"); Call("OpenBrowseItem", primary); Page("TracksPanel");
            Require(window.VisibleTracks.Count == 2, "Genres must open their songs in the same single-panel layout.");
            Set("category", "Songs"); Call("ResetMusicNavigation"); Page("TracksPanel");
            Require(window.VisibleTracks.Count == 3, "Songs must show the complete library directly.");
            var playlist = new Playlist { Name = "Favorites", TrackIds = [second.Id, first.Id] };
            window.Playlists.Add(playlist);
            Control<ListBox>("PlaylistList").ItemsSource = window.Playlists; // No dispatcher/render loop in this isolated check.
            Control<ListBox>("PlaylistList").SelectedItem = playlist; Page("TracksPanel");
            Require(window.VisibleTracks.SequenceEqual([second, first]), "Playlist pages must preserve playlist order.");
            Call("GoBack"); Page("TracksPanel");
            Require(Control<ListBox>("PlaylistList").SelectedItem is null && window.VisibleTracks.Count == 3, "Back must leave a playlist for the library root.");

            var deviceEpisode = new Track { Title = "Device episode", Artist = "Host", Album = "Device show", IsPodcast = true };
            Set("ipodTracks", new List<Track> { first, deviceEpisode }); Set("isIPodView", true);
            Set("category", "Artist"); Call("ResetMusicNavigation"); Page("PrimaryPanel");
            Require(primary.Items.Count == 1 && (primary.Items[0] as BrowseItem)?.Name == "Alpha", "iPod music browsing must exclude podcasts.");
            Set("category", "Podcast"); Call("ResetMusicNavigation");
            primary.SelectedItem = Item(primary, "Device show"); Call("OpenBrowseItem", primary); Page("TracksPanel");
            Require(window.VisibleTracks.SequenceEqual([deviceEpisode]), "iPod shows must open just their device episodes without device IO.");
            Set("isIPodView", false);

            var episode = new PodcastEpisode { Title = "Episode one" };
            var show = new PodcastShow { Title = "Test show", Episodes = [episode] };
            window.PodcastShows.Add(show);
            Set("isPodcastView", true);
            Control<RadioButton>("PodcastsTab").IsChecked = true;
            Call("UpdateDeviceStripMode");
            Control<Grid>("MusicWorkspace").Visibility = Visibility.Collapsed;
            Control<Grid>("PodcastWorkspace").Visibility = Visibility.Visible;
            var shows = Control<ListBox>("PodcastShowsList");
            shows.SelectedItem = show;
            Capture("podcast-shows");
            Require(Control<Grid>("PodcastHomePanel").Visibility == Visibility.Visible && Control<Grid>("PodcastShowPanel").Visibility == Visibility.Collapsed, "Selecting a show for a context menu must not open it.");
            Call("OpenBrowseItem", shows);
            Capture("podcast-episodes");
            Require(Control<Grid>("PodcastHomePanel").Visibility == Visibility.Collapsed && Control<Grid>("PodcastShowPanel").Visibility == Visibility.Visible, "Opening a show must replace the home list with its page.");
            Require(Control<DataGrid>("PodcastEpisodesGrid").Items.Count == 1 && Control<Button>("PodcastBackButton").Visibility == Visibility.Visible, "The show page must contain its episodes and Back button.");
            Call("RefreshPodcastShowPanel");
            Require(Control<Grid>("PodcastShowPanel").Visibility == Visibility.Visible, "Playback/download refresh must not leave the show page.");
            Call("GoBack");
            Require(Control<Grid>("PodcastHomePanel").Visibility == Visibility.Visible && ReferenceEquals(shows.SelectedItem, show), "Podcast Back must return to shows and preserve selection.");
            Call("OpenBrowseItem", shows);
            window.PodcastShows.Remove(show); Call("RefreshPodcastShowPanel");
            Require(Control<Grid>("PodcastHomePanel").Visibility == Visibility.Visible, "Removing the open show must return to the show list.");

            Set("isPodcastView", false); Set("isDownloadView", true);
            Control<RadioButton>("DownloadTab").IsChecked = true;
            Control<Grid>("PodcastWorkspace").Visibility = Visibility.Collapsed;
            Control<Grid>("DownloadWorkspace").Visibility = Visibility.Visible;
            Control<TextBox>("DownloadLinksBox").Text = "https://example.com/first\nhttps://example.com/second";
            Control<TextBlock>("DownloadTrackTitle").Text = "Sample track";
            Control<TextBlock>("DownloadLinkProgress").Text = "Link 1 of 2";
            Control<TextBlock>("DownloadTrackProgress").Text = "Track 3 of 12 in this link";
            Control<System.Windows.Controls.ProgressBar>("DownloadProgressBar").Value = 45;
            Call("AppendDownloadConsole", "[download] Sample output from yt-dlp\n[ExtractAudio] Converting with hTunes FFmpeg");
            Call("UpdateDeviceStripMode");
            Call("UpdateDownloadControls");
            Require(Control<TextBox>("DownloadLinksBox").AcceptsReturn && Control<Button>("DownloadStartButton").IsEnabled, "Download tab must accept multiple links and expose its start button.");
            Set("isYtDownloading", true); Set("ytDownloadCancellation", new CancellationTokenSource());
            Call("UpdateBusyWorkspaces");
            Require(!Control<Button>("DownloadStartButton").IsEnabled && Control<Button>("DownloadAbortButton").IsEnabled && Control<TextBox>("DownloadLinksBox").IsReadOnly, "An active queue must disable restart/editing while keeping Abort available.");
            Capture("downloads");
            Call("AbortDownloads_Click", window, new RoutedEventArgs());
            Require(!Control<Button>("DownloadAbortButton").IsEnabled, "Abort must not remain enabled after cancellation.");
            ((CancellationTokenSource)typeof(MainWindow).GetField("ytDownloadCancellation", privateInstance)!.GetValue(window)!).Dispose();
            Set("isYtDownloading", false);
        }
        finally { window.Close(); }
    }

    private static void InTemporaryDirectory(Action<string> check)
    {
        var directory = Directory.CreateTempSubdirectory("htunes-settings-check-").FullName;
        try { check(directory); }
        finally
        {
            var fullPath = Path.GetFullPath(directory);
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(fullPath).StartsWith("htunes-settings-check-", StringComparison.Ordinal))
                Directory.Delete(fullPath, recursive: true);
        }
    }
}
