using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace HTunes.App;

public partial class MainWindow
{
    private ItemsControl? lastActionSource;
    private const string HistoryScope = "Session history (up to 100 edits). Undo restores local library/playlist metadata, subscriptions, sync rules, and manual played state. It does not restore deleted downloads, reverse iPod operations, or rewind automatic listening counts.";

    private void InitializeTopMenus()
    {
        foreach (var control in new ItemsControl[] { TracksGrid, PrimaryList, SecondaryList, PlaylistList, PodcastShowsList, PodcastEpisodesGrid, PodcastSearchResultsList })
        {
            control.PreviewGotKeyboardFocus += (_, _) => lastActionSource = control;
            control.PreviewMouseDown += (_, _) => lastActionSource = control;
        }
        AttachTopMenu(FileMenu, BuildFileMenu);
        AttachTopMenu(EditMenu, BuildEditMenu);
        AttachTopMenu(ViewMenu, BuildViewMenu);
        AttachTopMenu(PlaybackMenu, BuildPlaybackMenu);

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, e) => { ApplyHistory(false); e.Handled = true; },
            (_, e) => { e.CanExecute = ContextActionsAvailable && editHistory.CanUndo && Keyboard.FocusedElement is not TextBoxBase; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, e) => { ApplyHistory(true); e.Handled = true; },
            (_, e) => { e.CanExecute = ContextActionsAvailable && editHistory.CanRedo && Keyboard.FocusedElement is not TextBoxBase; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, (_, e) => { SelectAllActiveItems(); e.Handled = true; },
            (_, e) => { e.CanExecute = ContextActionsAvailable; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => AddFiles_Click(this, new RoutedEventArgs()),
            (_, e) => e.CanExecute = ContextActionsAvailable));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.New, (_, _) => CreatePlaylistFromMenu(),
            (_, e) => e.CanExecute = ContextActionsAvailable));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => FocusSearch(),
            (_, e) => e.CanExecute = ContextActionsAvailable));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Redo, new KeyGesture(Key.Z, ModifierKeys.Control | ModifierKeys.Shift)));
    }

    private static void AttachTopMenu(MenuItem menu, Action<ItemsControl> build)
    {
        menu.Items.Clear();
        menu.ItemsSource = new object[] { new MenuItem { Header = "Loading…", IsEnabled = false } };
        menu.SubmenuOpened += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, menu)) return;
            // Replace the menu in one step rather than temporarily removing all its items while open.
            var staging = new ContextMenu();
            build(staging);
            var items = staging.Items.Cast<object>().ToArray();
            staging.Items.Clear();
            menu.ItemsSource = items;
        };
    }

    private ItemsControl ActiveActionSource()
    {
        if (isPodcastView)
        {
            if (lastActionSource?.IsVisible == true && (lastActionSource == PodcastShowsList || lastActionSource == PodcastEpisodesGrid || lastActionSource == PodcastSearchResultsList))
                return lastActionSource;
            return podcastShowOpen ? PodcastEpisodesGrid : PodcastShowsList;
        }
        if (lastActionSource?.IsVisible == true && (lastActionSource == TracksGrid || lastActionSource == PrimaryList || lastActionSource == SecondaryList || (!isIPodView && lastActionSource == PlaylistList)))
            return lastActionSource!;
        return PlaylistList.SelectedItem is Playlist ? TracksGrid : musicBrowse.ShowsGroups ? PrimaryList : musicBrowse.ShowsAlbums ? SecondaryList : TracksGrid;
    }

    private void BuildFileMenu(ItemsControl menu)
    {
        menu.Items.Add(new MenuItem { Header = "Add files to library…", Command = ApplicationCommands.Open, InputGestureText = "Ctrl+O" });
        AddMenuAction(menu, "Add folder to library…", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Add folder and all subfolders to library", Multiselect = true };
            if (dialog.ShowDialog(this) == true) ImportPaths(dialog.FolderNames);
        });
        menu.Items.Add(new MenuItem { Header = "New playlist…", Command = ApplicationCommands.New, InputGestureText = "Ctrl+N" });
        AddMenuAction(menu, "Find / subscribe to a podcast…", () => { PodcastsTab.IsChecked = true; FocusSearch(); });
        AddMenuAction(menu, "Download audio from links…", () => DownloadTab.IsChecked = true);
        if (isYtDownloading)
        {
            var abort = new MenuItem { Header = "Abort downloads" };
            abort.Click += AbortDownloads_Click;
            menu.Items.Add(abort);
        }
        menu.Items.Add(new Separator());
        AddMenuAction(menu, "Sync all music to iPod", () => SyncTracksAsync(allTracks.Select(track => track.Id), true), currentDevice is not null);
        AddMenuAction(menu, "Sync all podcasts to iPod", SyncAllPodcastsAsync, currentDevice is not null);
        AddMenuAction(menu, "Eject iPod", () => Eject_Click(this, new RoutedEventArgs()), currentDevice is not null);
        menu.Items.Add(new Separator());
        AddMenuAction(menu, "Update FFmpeg and yt-dlp…", () => UpdateTools_Click(this, new RoutedEventArgs()));
        var exit = new MenuItem { Header = "Exit", IsEnabled = !isSyncing && !isReconcilingPlayCounts };
        exit.Click += (_, _) => ((App)Application.Current).ExitApplication();
        menu.Items.Add(exit);
    }

    private void BuildEditMenu(ItemsControl menu)
    {
        menu.Items.Add(new MenuItem { Header = editHistory.CanUndo ? $"Undo {editHistory.UndoDescription}" : "Undo",
            Command = ApplicationCommands.Undo, InputGestureText = "Ctrl+Z", ToolTip = HistoryScope });
        menu.Items.Add(new MenuItem { Header = editHistory.CanRedo ? $"Redo {editHistory.RedoDescription}" : "Redo",
            Command = ApplicationCommands.Redo, InputGestureText = "Ctrl+Y", ToolTip = HistoryScope });
        menu.Items.Add(new MenuItem { Header = "Select all", Command = ApplicationCommands.SelectAll, InputGestureText = "Ctrl+A" });
        menu.Items.Add(new Separator());
        if (isDownloadView)
        {
            menu.Items.Add(new MenuItem { Header = "Cut links", Command = ApplicationCommands.Cut, CommandTarget = DownloadLinksBox });
            menu.Items.Add(new MenuItem { Header = "Copy links", Command = ApplicationCommands.Copy, CommandTarget = DownloadLinksBox });
            menu.Items.Add(new MenuItem { Header = "Paste links", Command = ApplicationCommands.Paste, CommandTarget = DownloadLinksBox });
            return;
        }
        var source = ActiveActionSource();
        var before = menu.Items.Count;
        if (source == PodcastShowsList) BuildPodcastShowMenu(menu);
        else if (source == PodcastEpisodesGrid) BuildEpisodeMenu(menu);
        else if (source == PodcastSearchResultsList && PodcastSearchResultsList.SelectedItem is PodcastSearchResult result)
            AddMenuAction(menu, "Subscribe", () => SubscribeAsync(result));
        else if (source == PlaylistList) BuildPlaylistMenu(menu);
        else if (source == PrimaryList || source == SecondaryList) BuildTrackMenu(menu, ContextCategoryTracks(source == SecondaryList));
        else if (source == TracksGrid) BuildTrackMenu(menu, SelectedTracks());
        if (menu.Items.Count == before) menu.Items.Add(new MenuItem { Header = "Select content to see available actions", IsEnabled = false });
    }

    private void BuildViewMenu(ItemsControl menu)
    {
        menu.Items.Add(new MenuItem { Header = "Back", Command = NavigationCommands.BrowseBack, InputGestureText = "Alt+Left" });
        menu.Items.Add(new Separator());
        AddMenuAction(menu, "Music", () => MusicTab.IsChecked = true).IsChecked = !isIPodView && !isPodcastView && !isDownloadView;
        AddMenuAction(menu, "Podcasts", () => PodcastsTab.IsChecked = true).IsChecked = isPodcastView;
        AddMenuAction(menu, "iPod", () => IPodTab.IsChecked = true, currentDevice is not null).IsChecked = isIPodView;
        AddMenuAction(menu, "Download", () => DownloadTab.IsChecked = true).IsChecked = isDownloadView;
        menu.Items.Add(new Separator());
        foreach (var (tag, title) in new[] { ("Artist", "Artists"), ("Album", "Albums"), ("Genre", "Genres"), ("Songs", "Songs") })
            AddMenuAction(menu, title, () => SelectBrowserCategory(tag)).IsChecked = !isPodcastView && !isDownloadView && category == tag;
        AddMenuAction(menu, "Podcasts on iPod", () => { IPodTab.IsChecked = true; IPodPodcastsCategoryButton.IsChecked = true; }, currentDevice is not null);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Search this view", Command = ApplicationCommands.Find, InputGestureText = "Ctrl+F" });
        AddMenuAction(menu, "Refresh", async () =>
        {
            if (isPodcastView) await RefreshShowsAsync(SelectedPodcastShow is { } show ? [show] : PodcastShows.ToList());
            else if (isIPodView && currentDevice is { } device) await LoadIPodTracksAsync(device);
            else RefreshBrowser();
        });
    }

    private void BuildPlaybackMenu(ItemsControl menu)
    {
        AddMenuAction(menu, "Play / resume", async () =>
        {
            if (isPodcastView && SelectedPodcastShow is { } show && PodcastEpisodesGrid.SelectedItem is PodcastEpisode episode)
                await PlayPodcastEpisodeAsync(show, episode);
            else if (player.Source is not null) player.Play();
            else
            {
                var source = ActiveActionSource();
                PlayContextTracks(source == PrimaryList || source == SecondaryList ? ContextCategoryTracks(source == SecondaryList) : SelectedTracks());
            }
        });
        AddMenuAction(menu, "Pause", () => Pause_Click(this, new RoutedEventArgs()), player.Source is not null);
        AddMenuAction(menu, "Stop", () => Stop_Click(this, new RoutedEventArgs()), player.Source is not null);
        AddMenuAction(menu, "Previous", () => Previous_Click(this, new RoutedEventArgs()), !isPodcastView && VisibleTracks.Count > 0);
        AddMenuAction(menu, "Next", () => Next_Click(this, new RoutedEventArgs()), !isPodcastView && VisibleTracks.Count > 0);
    }

    private void SelectBrowserCategory(string tag)
    {
        if (isPodcastView || isDownloadView) MusicTab.IsChecked = true;
        var panel = (Panel)ArtistCategoryButton.Parent;
        var button = panel.Children.OfType<RadioButton>().First(item => Equals(item.Tag, tag));
        button.IsChecked = true;
        // Re-selecting an already checked category should also leave a playlist view.
        ResetMusicNavigation();
    }

    private void SelectAllActiveItems()
    {
        if (isDownloadView) { DownloadLinksBox.Focus(); DownloadLinksBox.SelectAll(); return; }
        if (ActiveActionSource() is DataGrid grid) grid.SelectAll();
        else if (ActiveActionSource() is ListBox list && list.SelectionMode != SelectionMode.Single) list.SelectAll();
    }

    private void FocusSearch()
    {
        if (isPodcastView) { podcastShowOpen = false; RefreshPodcastShowPanel(); }
        var box = isDownloadView ? DownloadLinksBox : isPodcastView ? PodcastSearchBox : SearchBox;
        Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }));
    }

    private void CreatePlaylistFromMenu()
    {
        var name = AskPlaylistName("New playlist", "New Playlist");
        if (name is not null) { MusicTab.IsChecked = true; CreateLocalPlaylist(name); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!ContextActionsAvailable || activePodcastDownloads > 0 || autoSyncRunning) return;
        new SettingsWindow(SettingsStore.Current, settings =>
        {
            var previous = SettingsStore.Current;
            var startupChanged = previous.OpenOnIPodConnection != settings.OpenOnIPodConnection;
            if (startupChanged) StartupRegistration.Apply(settings.OpenOnIPodConnection);
            try { SettingsStore.Save(settings); }
            catch
            {
                if (startupChanged) StartupRegistration.Apply(previous.OpenOnIPodConnection);
                throw;
            }
            ((App)Application.Current).ConfigureWatcher();
            DebugLog.Write("Settings", $"Saved; import={settings.ImportMode}; autoSync={settings.AutoSyncOnConnection}; playedPercent={settings.PodcastPlayedPercent}");
            RefreshPodcastShowPanel();
            if (DebugLog.LastWriteError is { } error) MessageBox.Show(this, "Settings saved, but debug logging could not write: " + error, "Debug logging");
        }) { Owner = this }.ShowDialog();
    }
}
