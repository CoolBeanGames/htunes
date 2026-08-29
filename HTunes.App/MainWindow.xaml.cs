using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HTunes.App;

public partial class MainWindow : Window
{
    private static readonly string[] AudioExtensions = [".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".ogg"];
    private readonly string dataFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "library.json");
    private readonly MediaPlayer player = new();
    private Point dragStart;
    private string category = "Artist";
    private List<Track> allTracks = [];

    public ObservableCollection<Track> VisibleTracks { get; } = [];
    public ObservableCollection<Playlist> Playlists { get; } = [];

    public MainWindow()
    {
        InitializeComponent(); DataContext = this; LoadLibrary(); RefreshBrowser();
        player.MediaEnded += (_, _) => NextTrack();
    }

    private void LoadLibrary()
    {
        try
        {
            if (!File.Exists(dataFile)) return;
            var data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(dataFile));
            if (data is null) return;
            allTracks = data.Tracks.Where(t => File.Exists(t.FilePath)).ToList();
            foreach (var playlist in data.Playlists) Playlists.Add(playlist);
        }
        catch { }
    }

    private void SaveLibrary()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!);
        File.WriteAllText(dataFile, JsonSerializer.Serialize(new LibraryData { Tracks = allTracks, Playlists = Playlists.ToList() }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RefreshBrowser()
    {
        var search = SearchBox?.Text?.Trim() ?? "";
        var source = allTracks.Where(t => MatchesSearch(t, search)).ToList();
        PlaylistList.SelectedItem = null;
        PageTitle.Text = category switch { "Artist" => "Artists", "Album" => "Albums", "Genre" => "Genres", _ => "Songs" };
        LibrarySummary.Text = $"{allTracks.Count} song{(allTracks.Count == 1 ? "" : "s")} in your library";
        PrimaryPanel.Visibility = category == "Songs" ? Visibility.Collapsed : Visibility.Visible;
        PrimaryColumn.Width = category == "Songs" ? new GridLength(0) : new GridLength(240);
        SecondaryPanel.Visibility = category == "Artist" ? Visibility.Visible : Visibility.Collapsed;
        SecondaryColumn.Width = category == "Artist" ? new GridLength(240) : new GridLength(0);
        PrimaryHeading.Text = PageTitle.Text;
        PrimaryList.ItemsSource = category switch
        {
            "Artist" => source.Select(t => t.Artist).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
            "Album" => source.Select(t => t.Album).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
            "Genre" => source.Select(t => t.Genre).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
            _ => null
        };
        SecondaryList.ItemsSource = null;
        SetVisibleTracks(category == "Songs" ? source : []);
    }

    private void SetVisibleTracks(IEnumerable<Track> tracks)
    {
        VisibleTracks.Clear();
        foreach (var track in tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title)) VisibleTracks.Add(track);
        EmptyState.Visibility = VisibleTracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool MatchesSearch(Track t, string search) => string.IsNullOrEmpty(search) || new[] { t.Title, t.Artist, t.Album, t.Genre }.Any(v => v.Contains(search, StringComparison.OrdinalIgnoreCase));
    private void Category_Checked(object sender, RoutedEventArgs e) { if (!IsLoaded || sender is not RadioButton button) return; category = button.Tag?.ToString() ?? "Artist"; RefreshBrowser(); }

    private void PrimaryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrimaryList.SelectedItem is not string selected) return;
        if (category == "Artist")
        {
            SecondaryHeading.Text = $"Albums by {selected}";
            SecondaryList.ItemsSource = allTracks.Where(t => Same(t.Artist, selected)).Select(t => t.Album).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
            SetVisibleTracks(allTracks.Where(t => Same(t.Artist, selected)));
        }
        else if (category == "Album") SetVisibleTracks(allTracks.Where(t => Same(t.Album, selected)));
        else if (category == "Genre") SetVisibleTracks(allTracks.Where(t => Same(t.Genre, selected)));
    }

    private void SecondaryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrimaryList.SelectedItem is string artist && SecondaryList.SelectedItem is string album) SetVisibleTracks(allTracks.Where(t => Same(t.Artist, artist) && Same(t.Album, album)));
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
        var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories) : [p]).Where(p => AudioExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase));
        var added = 0;
        foreach (var file in files)
        {
            var fullPath = Path.GetFullPath(file);
            if (allTracks.Any(t => Same(t.FilePath, fullPath))) continue;
            allTracks.Add(new Track { FilePath = fullPath, Title = Path.GetFileNameWithoutExtension(file), DateAdded = DateTime.Now }); added++;
        }
        if (added > 0) { SaveLibrary(); RefreshBrowser(); }
    }

    private List<Track> SelectedTracks() => TracksGrid.SelectedItems.Cast<Track>().ToList();
    private void EditMetadata_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTracks();
        if (selected.Count == 0) { MessageBox.Show(this, "Select one or more songs first. Use Ctrl or Shift to select several.", "Edit metadata"); return; }
        if (new MetadataEditorWindow(selected) { Owner = this }.ShowDialog() == true) { SaveLibrary(); RefreshBrowser(); }
    }

    private void RemoveTracks_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTracks();
        if (selected.Count == 0 || MessageBox.Show(this, $"Remove {selected.Count} selected song(s) from the library? The original files will not be deleted.", "Remove songs", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        allTracks.RemoveAll(selected.Contains);
        foreach (var playlist in Playlists) playlist.TrackIds.RemoveAll(id => selected.Any(t => t.Id == id));
        SaveLibrary(); RefreshBrowser();
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var playlist = new Playlist { Name = $"New Playlist {Playlists.Count + 1}" }; Playlists.Add(playlist); SaveLibrary(); PlaylistList.SelectedItem = playlist;
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not Playlist playlist) return;
        PageTitle.Text = playlist.Name; LibrarySummary.Text = $"{playlist.TrackIds.Count} song{(playlist.TrackIds.Count == 1 ? "" : "s")}";
        PrimaryPanel.Visibility = SecondaryPanel.Visibility = Visibility.Collapsed; PrimaryColumn.Width = SecondaryColumn.Width = new GridLength(0);
        SetVisibleTracks(playlist.TrackIds.Select(id => allTracks.FirstOrDefault(t => t.Id == id)).Where(t => t is not null).Cast<Track>());
    }

    private void TracksGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => dragStart = e.GetPosition(null);
    private void TracksGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Math.Abs(e.GetPosition(null).X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        var tracks = SelectedTracks(); if (tracks.Count > 0) DragDrop.DoDragDrop(TracksGrid, new DataObject("hTunesTracks", tracks.Select(t => t.Id).ToArray()), DragDropEffects.Copy);
    }
    private void Playlist_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent("hTunesTracks") ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Playlist_Drop(object sender, DragEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(PlaylistList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var playlist = container?.DataContext as Playlist ?? PlaylistList.SelectedItem as Playlist;
        if (playlist is null || e.Data.GetData("hTunesTracks") is not Guid[] ids) return;
        foreach (var id in ids.Where(id => !playlist.TrackIds.Contains(id))) playlist.TrackIds.Add(id);
        SaveLibrary(); PlaylistList.Items.Refresh(); PlaylistList.SelectedItem = playlist;
        PlaylistList_SelectionChanged(this, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, new List<object>(), new List<object>()));
    }

    private void TracksGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected();
    private void Play_Click(object sender, RoutedEventArgs e) { if (player.Source is null) PlaySelected(); else player.Play(); }
    private void PlaySelected()
    {
        if (TracksGrid.SelectedItem is not Track track) return;
        player.Open(new Uri(track.FilePath)); player.Play(); track.PlayCount++;
        NowPlayingTitle.Text = track.Title; NowPlayingArtist.Text = $"{track.Artist} — {track.Album}"; SaveLibrary(); TracksGrid.Items.Refresh();
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => player.Pause();
    private void Stop_Click(object sender, RoutedEventArgs e) => player.Stop();
    private void Previous_Click(object sender, RoutedEventArgs e) => MoveSelection(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NextTrack();
    private void NextTrack() => MoveSelection(1);
    private void MoveSelection(int offset)
    {
        if (VisibleTracks.Count == 0) return;
        TracksGrid.SelectedIndex = (Math.Max(0, TracksGrid.SelectedIndex) + offset + VisibleTracks.Count) % VisibleTracks.Count; TracksGrid.ScrollIntoView(TracksGrid.SelectedItem); PlaySelected();
    }
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "hTunes\nA modern Windows music library and iPod companion.", "About hTunes");
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { SaveLibrary(); player.Close(); base.OnClosing(e); }
}

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid(); public string FilePath { get; set; } = ""; public string Title { get; set; } = "Unknown title";
    public string Artist { get; set; } = "Unknown Artist"; public string Album { get; set; } = "Unknown Album"; public string Genre { get; set; } = "Unknown Genre";
    public int TrackNumber { get; set; } public int DiscNumber { get; set; } = 1; public int Year { get; set; } public int PlayCount { get; set; }
    public string? ArtworkPath { get; set; } public DateTime DateAdded { get; set; }
}
public sealed class Playlist { public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = "New Playlist"; public List<Guid> TrackIds { get; set; } = []; }
public sealed class LibraryData { public List<Track> Tracks { get; set; } = []; public List<Playlist> Playlists { get; set; } = []; }
