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
    public bool EpisodeSeenStateInitialized { get; set; }
    public List<string> SeenEpisodeIds { get; set; } = [];
    public List<PodcastEpisode> Episodes { get; set; } = [];

    [JsonIgnore] public string ArtworkDisplay => File.Exists(ArtworkPath) ? ArtworkPath! : ArtworkUrl;
    [JsonIgnore] public int UnplayedCount => Episodes.Count(episode => !episode.IsPlayed);
    [JsonIgnore] public int DownloadedCount => Episodes.Count(episode => episode.IsDownloaded);
    [JsonIgnore] public string Summary => $"{UnplayedCount} unplayed  •  {DownloadedCount} downloaded";
    [JsonIgnore] public bool HasNewEpisodes => EpisodeSeenStateInitialized && Episodes.Any(episode => !SeenEpisodeIds.Contains(episode.Id, StringComparer.OrdinalIgnoreCase));
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
    public long PlaybackPositionMs { get; set; }
    public long DurationMs { get; set; }
    public int FeedOrder { get; set; }

    [JsonIgnore] public bool IsDownloaded => !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);
    [JsonIgnore] public bool IsNotDownloaded => !IsDownloaded;
    [JsonIgnore] public bool IsUnplayed => !IsPlayed;
    [JsonIgnore] public string ArtworkDisplay => ArtworkUrl;
    [JsonIgnore] public string EpisodeNumberDisplay => string.IsNullOrWhiteSpace(EpisodeNumber) ? "—" : EpisodeNumber;
    [JsonIgnore] public int? NaturalEpisodeNumber => PodcastEpisodeOrdering.Number(this);
    [JsonIgnore] public string DurationDisplay => DurationMs <= 0 ? "—" : Time(DurationMs) + (PlaybackPositionMs > 0 ? $"  •  left off {Time(PlaybackPositionMs)}" : "");
    [JsonIgnore] public string PublishedDisplay => PublishedUtc == default ? "—" : PublishedUtc.ToLocalTime().ToString("MMM d, yyyy");
    [JsonIgnore] public double PlaybackPercent => DurationMs > 0 ? Math.Clamp(PlaybackPositionMs * 100d / DurationMs, 0, 100) : 0;
    [JsonIgnore] public string ProgressDisplay => PlaybackPositionMs <= 0 || DurationMs <= 0 ? "" : $"  •  {Time(PlaybackPositionMs)} of {Time(DurationMs)} ({PlaybackPercent:0}%)";
    [JsonIgnore] public string StateDisplay => $"{(IsPlayed ? "Played" : "Unplayed")}  •  {(IsDownloaded ? "Downloaded" : "Not downloaded")}{ProgressDisplay}";

    private static string Time(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }
}

internal static class PodcastEpisodeOrdering
{
    public static int? Number(PodcastEpisode episode)
    {
        if (int.TryParse(episode.EpisodeNumber.Trim(), out var direct)) return direct;
        var match = System.Text.RegularExpressions.Regex.Match(episode.Title, @"(?<!\d)(\d{1,6})(?!\d)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var titleNumber) ? titleNumber : null;
    }

    // Sync selection ("keep X newest/oldest") must follow the real publication age of each episode,
    // not its episode number or feed position. Those only break ties when a feed omits pubDate.
    public static IEnumerable<PodcastEpisode> ByAge(IEnumerable<PodcastEpisode> episodes, bool oldest)
    {
        var list = episodes.ToList();
        return oldest
            ? list.OrderBy(episode => episode.PublishedUtc).ThenByDescending(episode => episode.FeedOrder).ThenByDescending(episode => Number(episode) ?? int.MinValue)
            : list.OrderByDescending(episode => episode.PublishedUtc).ThenBy(episode => episode.FeedOrder).ThenBy(episode => Number(episode) ?? int.MaxValue);
    }

    public static IOrderedEnumerable<PodcastEpisode> Order(IEnumerable<PodcastEpisode> episodes, bool oldest)
    {
        var numbered = episodes.Any(episode => Number(episode) is not null);
        if (numbered) return oldest
            ? episodes.OrderBy(episode => Number(episode) ?? int.MaxValue).ThenByDescending(episode => episode.FeedOrder)
            : episodes.OrderByDescending(episode => Number(episode) ?? int.MinValue).ThenBy(episode => episode.FeedOrder);
        var hasFeedOrder = episodes.Any(episode => episode.FeedOrder != 0);
        if (hasFeedOrder) return oldest ? episodes.OrderByDescending(episode => episode.FeedOrder) : episodes.OrderBy(episode => episode.FeedOrder);
        return oldest ? episodes.OrderBy(episode => episode.PublishedUtc) : episodes.OrderByDescending(episode => episode.PublishedUtc);
    }
}

public sealed class PodcastLibraryData
{
    public List<PodcastShow> Shows { get; set; } = [];
}

public sealed record PodcastSearchResult(string Title, string Author, string FeedUrl, string ArtworkUrl);

internal sealed record PodcastEpisodeSelection(PodcastShow Show, PodcastEpisode Episode);
