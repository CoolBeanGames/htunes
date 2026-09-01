using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace HTunes.App;

public partial class MetadataEditorWindow : Window
{
    private readonly IReadOnlyList<Track> tracks; private string? artworkPath;
    public MetadataEditorWindow(IReadOnlyList<Track> tracks)
    {
        InitializeComponent(); this.tracks = tracks; Heading.Text = tracks.Count == 1 ? tracks[0].Title : $"{tracks.Count} songs selected";
        TitleBox.Text = Shared(t => t.Title); ArtistBox.Text = Shared(t => t.Artist); AlbumArtistBox.Text = Shared(t => t.AlbumArtist); AlbumBox.Text = Shared(t => t.Album); GenreBox.Text = Shared(t => t.Genre);
        TrackBox.Text = Shared(t => t.TrackNumber.ToString()); DiscBox.Text = Shared(t => t.DiscNumber.ToString()); YearBox.Text = Shared(t => t.Year.ToString());
        artworkPath = tracks.Select(t => t.ArtworkPath).Distinct().Count() == 1 ? tracks[0].ArtworkPath : null; ShowArtwork();
    }
    private string Shared(Func<Track, string> value) { var values = tracks.Select(value).Distinct().ToList(); return values.Count == 1 ? values[0] : ""; }
    private void ChooseArtwork_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" }; if (dialog.ShowDialog(this) == true) { artworkPath = dialog.FileName; ShowArtwork(); } }
    private void ShowArtwork() { if (artworkPath is null || !File.Exists(artworkPath)) return; Artwork.Source = new BitmapImage(new Uri(artworkPath)); ArtworkPlaceholder.Visibility = Visibility.Collapsed; }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var track in tracks)
        {
            if (!string.IsNullOrWhiteSpace(TitleBox.Text)) track.Title = TitleBox.Text.Trim(); if (!string.IsNullOrWhiteSpace(ArtistBox.Text)) track.Artist = ArtistBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(AlbumArtistBox.Text)) track.AlbumArtist = AlbumArtistBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(AlbumBox.Text)) track.Album = AlbumBox.Text.Trim(); if (!string.IsNullOrWhiteSpace(GenreBox.Text)) track.Genre = GenreBox.Text.Trim();
            if (int.TryParse(TrackBox.Text, out var number)) track.TrackNumber = number; if (int.TryParse(DiscBox.Text, out var disc)) track.DiscNumber = disc; if (int.TryParse(YearBox.Text, out var year)) track.Year = year;
            if (artworkPath is not null) track.ArtworkPath = artworkPath;
        }
        DialogResult = true;
    }
}
