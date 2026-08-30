namespace HTunes.App;

internal sealed class MusicBrowseState
{
    public string Category { get; private set; } = "Artist";
    public string? Group { get; private set; }
    public string? Album { get; private set; }
    public bool ShowsGroups => Category != "Songs" && Group is null;
    public bool ShowsAlbums => Category == "Artist" && Group is not null && Album is null;
    public bool ShowsTracks => !ShowsGroups && !ShowsAlbums;
    public bool CanGoBack => Group is not null;
    public string RootTitle => Category switch { "Artist" => "Artists", "Album" => "Albums", "Genre" => "Genres", "Podcast" => "Podcasts", _ => "Songs" };
    public string Title => Album ?? Group ?? RootTitle;
    public void Reset(string category) { Category = category; Group = Album = null; }
    public void OpenGroup(string group) { if (ShowsGroups) { Group = group; Album = null; } }
    public void OpenAlbum(string album) { if (ShowsAlbums) Album = album; }
    public void Back() { if (Album is not null) Album = null; else Group = null; }
    public IEnumerable<Track> Filter(IEnumerable<Track> tracks) => tracks.Where(track =>
        (Group is null || Category switch
        {
            "Artist" => string.Equals(track.Artist, Group, StringComparison.OrdinalIgnoreCase),
            "Album" or "Podcast" => string.Equals(track.Album, Group, StringComparison.OrdinalIgnoreCase),
            "Genre" => string.Equals(track.Genre, Group, StringComparison.OrdinalIgnoreCase),
            _ => true
        }) && (Album is null || string.Equals(track.Album, Album, StringComparison.OrdinalIgnoreCase)));
}
