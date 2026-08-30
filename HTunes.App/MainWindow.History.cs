using System.Windows;
using System.Windows.Input;

namespace HTunes.App;

public partial class MainWindow
{
    private readonly EditHistory editHistory = new();

    private void RecordEdit(string description, Action undo, Action redo)
    {
        editHistory.Record(description, undo, redo);
        CommandManager.InvalidateRequerySuggested();
    }

    private void DetachPodcastShow(PodcastShow show)
    {
        if (ReferenceEquals(currentPodcastPlaybackShow, show)) FinalizePodcastPlayback();
        PodcastShows.Remove(show);
    }

    private void ApplyHistory(bool redo)
    {
        try
        {
            if (redo) editHistory.Redo(); else editHistory.Undo();
            SaveLibrary();
            SavePodcastLibrary();
            var playlist = PlaylistList.SelectedItem as Playlist;
            RefreshBrowser();
            if (!isIPodView && playlist is not null && Playlists.Contains(playlist)) RefreshPlaylistView(playlist);
            RefreshPodcastShowPanel();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.GetBaseException().Message, "Could not apply edit history", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private sealed record TrackMetadata(string Title, string Artist, string Album, string Genre, int Number, int Disc, int Year, string? Artwork)
    {
        public static TrackMetadata Read(Track track) => new(track.Title, track.Artist, track.Album, track.Genre, track.TrackNumber, track.DiscNumber, track.Year, track.ArtworkPath);
        public void Apply(Track track)
        {
            track.Title = Title; track.Artist = Artist; track.Album = Album; track.Genre = Genre;
            track.TrackNumber = Number; track.DiscNumber = Disc; track.Year = Year; track.ArtworkPath = Artwork;
        }
    }

    private void EditTrackMetadata(List<Track> tracks)
    {
        if (tracks.Count == 0) return;
        var before = tracks.ToDictionary(track => track, TrackMetadata.Read);
        if (new MetadataEditorWindow(tracks) { Owner = this }.ShowDialog() != true) return;
        var after = tracks.ToDictionary(track => track, TrackMetadata.Read);
        var changed = tracks.Where(track => before[track] != after[track]).ToList();
        if (changed.Count > 0)
            RecordEdit("Edit metadata / artwork", () => changed.ForEach(track => before[track].Apply(track)), () => changed.ForEach(track => after[track].Apply(track)));
        SaveLibrary(); RefreshBrowser();
    }

    private Playlist CreateLocalPlaylist(string name, IEnumerable<Guid>? ids = null)
    {
        var playlist = new Playlist { Name = name, TrackIds = ids?.Distinct().ToList() ?? [] };
        var index = Playlists.Count;
        Playlists.Add(playlist);
        RecordEdit("Create playlist", () => Playlists.Remove(playlist), () => Playlists.Insert(Math.Min(index, Playlists.Count), playlist));
        SaveLibrary(); PlaylistList.SelectedItem = playlist;
        return playlist;
    }

    private void ChangePlaylistMembership(Playlist playlist, Action change)
    {
        var before = playlist.TrackIds.ToList();
        change();
        var after = playlist.TrackIds.ToList();
        if (!before.SequenceEqual(after))
            RecordEdit("Change playlist tracks", () => playlist.TrackIds = before.ToList(), () => playlist.TrackIds = after.ToList());
    }

    private sealed record EpisodeState(bool Played, DateTime? PlayedUtc, long Position)
    {
        public static EpisodeState Read(PodcastEpisode episode) => new(episode.IsPlayed, episode.PlayedUtc, episode.PlaybackPositionMs);
        public void Apply(PodcastEpisode episode)
        {
            episode.IsPlayed = Played; episode.PlayedUtc = PlayedUtc; episode.PlaybackPositionMs = Position;
        }
    }

    private void RecordEpisodeChanges(List<PodcastEpisode> episodes, Dictionary<PodcastEpisode, EpisodeState> before, bool played)
    {
        var after = episodes.ToDictionary(episode => episode, EpisodeState.Read);
        var changed = episodes.Where(episode => before[episode] != after[episode]).ToList();
        void Apply(Dictionary<PodcastEpisode, EpisodeState> values)
        {
            foreach (var episode in changed)
            {
                // Stop an active player before restoring flags/progress; don't let its timer undo this edit.
                PreparePodcastFileDeletion(episode);
                values[episode].Apply(episode);
            }
        }
        if (changed.Count > 0)
            RecordEdit(played ? "Mark podcast played" : "Mark podcast unplayed", () => Apply(before), () => Apply(after));
    }
}
