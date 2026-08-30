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
        foreach (var list in new[] { PrimaryList, SecondaryList, PodcastShowsList })
        {
            list.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(BrowseList_MouseLeftButtonUp), true);
            list.KeyDown += BrowseList_KeyDown;
        }
        PodcastShowsList.PreviewMouseLeftButtonDown += CategoryList_PreviewMouseLeftButtonDown;
        InputBindings.Add(new KeyBinding(NavigationCommands.BrowseBack, new KeyGesture(Key.Left, ModifierKeys.Alt)));
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack, (_, _) => GoBack(),
            (_, e) => e.CanExecute = !isRenameView && !isTagView && !isDownloadView && ContextActionsAvailable && (isPodcastView ? podcastShowOpen : musicBrowse.CanGoBack || PlaylistList.SelectedItem is Playlist)));
    }

    // Open on release, not selection-change: Ctrl/Shift, right-click, keyboard selection and drags must stay on the list.
    private void BrowseList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || categoryDragPerformed || browseClickModified || Keyboard.Modifiers != ModifierKeys.None || list.SelectedItems.Count != 1) return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(position.Y - dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance) return;
        var item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is null || !item.IsSelected) return;
        OpenBrowseItem(list);
        e.Handled = true;
    }

    private void BrowseList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ListBox list || list.SelectedItems.Count != 1) return;
        OpenBrowseItem(list); e.Handled = true;
    }

    private void OpenBrowseItem(ListBox list)
    {
        if (!ContextActionsAvailable) return;
        if (list == PodcastShowsList && list.SelectedItem is PodcastShow) { OpenPodcastShow(); return; }
        if (list.SelectedItem is not string name) return;
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
        if (isPodcastView) { podcastShowOpen = false; RefreshPodcastShowPanel(); lastActionSource = PodcastShowsList; PodcastShowsList.Focus(); return; }
        if (PlaylistList.SelectedItem is Playlist) ResetMusicNavigation();
        else
        {
            var opened = musicBrowse.Album ?? musicBrowse.Group;
            musicBrowse.Back(); RefreshBrowser();
            var list = musicBrowse.ShowsAlbums ? SecondaryList : PrimaryList;
            list.SelectedItem = list.Items.Cast<string>().FirstOrDefault(item => string.Equals(item, opened, StringComparison.OrdinalIgnoreCase));
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

    private static void ReplaceBrowseItems(ListBox list, IEnumerable<string>? items)
    {
        var selected = list.SelectedItems.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        list.ItemsSource = items?.ToList();
        foreach (var item in list.Items.Cast<string>().Where(selected.Contains)) list.SelectedItems.Add(item);
    }
}
