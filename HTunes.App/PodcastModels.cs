using System.IO;
using System.Text.Json.Serialization;

namespace HTunes.App;

public sealed class PodcastShow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Untitled Podcast";
    public string Author { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public string ArtworkUrl { get; set; } = "";
    public string? ArtworkPath { get; set; }
    public int SyncEpisodeCount { get; set; } = 3;
    public string SyncOrder { get; set; } = "Newest";
    public DateTime LastRefreshedUtc { get; set; }
    public List<PodcastEpisode> Episodes { get; set; } = [];

    [JsonIgnore] public string ArtworkDisplay => File.Exists(ArtworkPath) ? ArtworkPath! : ArtworkUrl;
    [JsonIgnore] public int UnplayedCount => Episodes.Count(episode => !episode.IsPlayed);
    [JsonIgnore] public int DownloadedCount => Episodes.Count(episode => episode.IsDownloaded);
    [JsonIgnore] public string Summary => $"{UnplayedCount} unplayed  •  {DownloadedCount} downloaded";
}

public sealed class PodcastEpisode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled Episode";
    public string Description { get; set; } = "";
    public string EpisodeNumber { get; set; } = "";
    public DateTime PublishedUtc { get; set; }
    public string EnclosureUrl { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long EnclosureLength { get; set; }
    public string ArtworkUrl { get; set; } = "";
    public string? LocalPath { get; set; }
    public bool IsPlayed { get; set; }
    public DateTime? PlayedUtc { get; set; }

    [JsonIgnore] public bool IsDownloaded => !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);
    [JsonIgnore] public bool IsNotDownloaded => !IsDownloaded;
    [JsonIgnore] public bool IsUnplayed => !IsPlayed;
    [JsonIgnore] public string ArtworkDisplay => ArtworkUrl;
    [JsonIgnore] public string EpisodeNumberDisplay => string.IsNullOrWhiteSpace(EpisodeNumber) ? "—" : EpisodeNumber;
    [JsonIgnore] public string PublishedDisplay => PublishedUtc == default ? "—" : PublishedUtc.ToLocalTime().ToString("MMM d, yyyy");
    [JsonIgnore] public string StateDisplay => $"{(IsPlayed ? "Played" : "Unplayed")}  •  {(IsDownloaded ? "Downloaded" : "Not downloaded")}";
}

public sealed class PodcastLibraryData
{
    public List<PodcastShow> Shows { get; set; } = [];
}

public sealed record PodcastSearchResult(string Title, string Author, string FeedUrl, string ArtworkUrl);

internal sealed record PodcastEpisodeSelection(PodcastShow Show, PodcastEpisode Episode);
