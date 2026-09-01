using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HTunes.App;

public partial class MainWindow
{
    private readonly MusicBrowseState musicBrowse = new();
    private bool categoryDragPerformed;
    private bool browseClickModified;
    private bool refreshingNavigation;
    private bool podcastShowOpen;

    private void InitializeNavigation()
    {
        foreach (var button in ((Panel)ArtistCategoryButton.Parent).Children.OfType<RadioButton>()) button.Click += Category_Click;
        foreach (var list in new[] { PrimaryList, SecondaryList })
        {
            list.MouseDoubleClick += BrowseList_MouseDoubleClick;
            list.KeyDown += BrowseList_KeyDown;
        }
        PodcastShowsList.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(PodcastShow_MouseLeftButtonUp), true);
        PodcastShowsList.KeyDown += BrowseList_KeyDown;
        PodcastShowsList.PreviewMouseLeftButtonDown += CategoryList_PreviewMouseLeftButtonDown;
        InputBindings.Add(new KeyBinding(NavigationCommands.BrowseBack, new KeyGesture(Key.Left, ModifierKeys.Alt)));
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack, (_, _) => GoBack(),
            (_, e) => e.CanExecute = !isRenameView && !isTagView && !isDownloadView && ContextActionsAvailable && (isPodcastView ? podcastShowOpen : musicBrowse.CanGoBack || PlaylistList.SelectedItem is Playlist)));
    }

    private void BrowseList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || categoryDragPerformed || browseClickModified || Keyboard.Modifiers != ModifierKeys.None || list.SelectedItems.Count != 1) return;
        var item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is null || !item.IsSelected) return;
        OpenBrowseItem(list);
        e.Handled = true;
    }

    private void PodcastShow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || PodcastShowsList.SelectedItems.Count != 1) return;
        if (ItemsControl.ContainerFromElement(PodcastShowsList, e.OriginalSource as DependencyObject) is not ListBoxItem item || !item.IsSelected) return;
        OpenBrowseItem(PodcastShowsList); e.Handled = true;
    }

    private void BrowseList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ListBox list || list.SelectedItems.Count != 1) return;
        OpenBrowseItem(list); e.Handled = true;
    }

    private void OpenBrowseItem(ListBox list)
    {
        if (list == PodcastShowsList && list.SelectedItem is PodcastShow) { OpenPodcastShow(); return; }
        if (list.SelectedItem is not BrowseItem selected) return;
        var name = selected.Name;
        if (list == PrimaryList) musicBrowse.OpenGroup(name); else musicBrowse.OpenAlbum(name);
        RefreshBrowser();
        FocusCurrentMusicPage();
    }

    private void FocusCurrentMusicPage()
    {
        var target = PlaylistList.SelectedItem is Playlist ? TracksGrid : musicBrowse.ShowsGroups ? (Control)PrimaryList : musicBrowse.ShowsAlbums ? SecondaryList : TracksGrid;
        lastActionSource = (ItemsControl)target;
        target.Focus();
    }

    private void ResetMusicNavigation()
    {
        musicBrowse.Reset(category);
        PlaylistList.SelectedItem = null;
        RefreshBrowser();
    }

    private void GoBack()
    {
        if (isPodcastView)
        {
            if (podcastShowOpen && SelectedPodcastShow is { } show)
            {
                show.SeenEpisodeIds = show.Episodes.Select(episode => episode.Id).ToList();
                show.EpisodeSeenStateInitialized = true;
                if (initializeServices) SavePodcastLibrary();
            }
            podcastShowOpen = false; RefreshPodcastShowPanel(); lastActionSource = PodcastShowsList; PodcastShowsList.Focus(); return;
        }
        if (PlaylistList.SelectedItem is Playlist) ResetMusicNavigation();
        else
        {
            var opened = musicBrowse.Album ?? musicBrowse.Group;
            musicBrowse.Back(); RefreshBrowser();
            var list = musicBrowse.ShowsAlbums ? SecondaryList : PrimaryList;
            list.SelectedItem = list.Items.Cast<BrowseItem>().FirstOrDefault(item => string.Equals(item.Name, opened, StringComparison.OrdinalIgnoreCase));
            if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
        }
        FocusCurrentMusicPage();
    }

    private void BrowseBack_Click(object sender, RoutedEventArgs e) => GoBack();
    private void Category_Click(object sender, RoutedEventArgs e) { if (IsLoaded) ResetMusicNavigation(); }

    private void OpenPodcastShow()
    {
        podcastShowOpen = SelectedPodcastShow is not null;
        RefreshPodcastShowPanel();
        lastActionSource = PodcastEpisodesGrid;
        PodcastEpisodesGrid.Focus();
    }

    private void UpdatePodcastNavigation()
    {
        if (SelectedPodcastShow is null || !PodcastShows.Contains(SelectedPodcastShow)) podcastShowOpen = false;
        PodcastHomePanel.Visibility = PodcastSearchPanel.Visibility = podcastShowOpen ? Visibility.Collapsed : Visibility.Visible;
        PodcastShowPanel.Visibility = PodcastBackButton.Visibility = podcastShowOpen ? Visibility.Visible : Visibility.Collapsed;
        PodcastHomeEmpty.Visibility = PodcastShows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ReplaceBrowseItems(ListBox list, IEnumerable<BrowseItem>? items)
    {
        var selected = list.SelectedItems.Cast<BrowseItem>().Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        list.ItemsSource = items?.ToList();
        foreach (var item in list.Items.Cast<BrowseItem>().Where(item => selected.Contains(item.Name))) list.SelectedItems.Add(item);
    }
}

public sealed record BrowseItem(string Name, bool IsNew);
