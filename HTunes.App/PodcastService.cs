using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace HTunes.App;

internal static class PodcastService
{
    private static readonly HttpClient Client = CreateClient();
    private static string PodcastDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "podcasts");

    public static async Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var url = $"https://itunes.apple.com/search?media=podcast&entity=podcast&country=US&limit=25&term={Uri.EscapeDataString(query)}";
        using var response = await Client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var results = new List<PodcastSearchResult>();
        foreach (var item in json.RootElement.GetProperty("results").EnumerateArray())
        {
            var feed = String(item, "feedUrl");
            if (string.IsNullOrWhiteSpace(feed)) continue;
            results.Add(new PodcastSearchResult(
                String(item, "collectionName", "trackName"),
                String(item, "artistName"),
                feed,
                String(item, "artworkUrl600", "artworkUrl100")));
        }
        return results.DistinctBy(result => result.FeedUrl, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task RefreshShowAsync(PodcastShow show, CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(show.FeedUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        var channel = document.Descendants().FirstOrDefault(element => element.Name.LocalName is "channel" or "feed") ?? document.Root;
        if (channel is null) throw new InvalidDataException("This address did not contain a podcast feed.");

        show.Title = ChildValue(channel, "title") ?? show.Title;
        show.Author = ChildValue(channel, "author", "managingEditor") ?? show.Author;
        var feedArtwork = ArtworkUrl(channel);
        if (!string.IsNullOrWhiteSpace(feedArtwork)) show.ArtworkUrl = feedArtwork;

        var existing = show.Episodes
            .GroupBy(episode => episode.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var byUrl = show.Episodes.Where(episode => !string.IsNullOrWhiteSpace(episode.EnclosureUrl))
            .GroupBy(episode => episode.EnclosureUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var refreshed = new List<PodcastEpisode>();
        foreach (var item in document.Descendants().Where(element => element.Name.LocalName is "item" or "entry"))
        {
            var enclosure = Enclosure(item);
            if (string.IsNullOrWhiteSpace(enclosure.Url)) continue;
            var id = ChildValue(item, "guid", "id") ?? enclosure.Url;
            var episode = existing.GetValueOrDefault(id) ?? byUrl.GetValueOrDefault(enclosure.Url) ?? new PodcastEpisode { Id = id };
            episode.Id = id;
            episode.Title = ChildValue(item, "title") ?? episode.Title;
            episode.Description = StripMarkup(ChildValue(item, "description", "summary", "content") ?? episode.Description);
            episode.EpisodeNumber = ChildValue(item, "episode") ?? episode.EpisodeNumber;
            episode.PublishedUtc = ParseDate(ChildValue(item, "pubDate", "published", "updated"), episode.PublishedUtc);
            episode.EnclosureUrl = enclosure.Url;
            episode.MimeType = enclosure.Type;
            episode.EnclosureLength = enclosure.Length;
            episode.ArtworkUrl = ArtworkUrl(item) ?? show.ArtworkUrl;
            refreshed.Add(episode);
        }
        var refreshedIds = refreshed.Select(episode => episode.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var refreshedUrls = refreshed.Where(episode => !string.IsNullOrWhiteSpace(episode.EnclosureUrl))
            .Select(episode => episode.EnclosureUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
        refreshed.AddRange(show.Episodes.Where(episode =>
            !refreshedIds.Contains(episode.Id) &&
            (string.IsNullOrWhiteSpace(episode.EnclosureUrl) || !refreshedUrls.Contains(episode.EnclosureUrl))));
        show.Episodes = refreshed.OrderByDescending(episode => episode.PublishedUtc).ToList();
        show.LastRefreshedUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(show.ArtworkUrl)) show.ArtworkPath = await DownloadArtworkAsync(show, cancellationToken) ?? show.ArtworkPath;
    }

    public static IReadOnlyList<PodcastEpisode> EpisodesForSync(PodcastShow show)
    {
        var candidates = show.Episodes.Where(episode => !episode.IsPlayed);
        candidates = show.SyncOrder.Equals("Oldest", StringComparison.OrdinalIgnoreCase)
            ? candidates.OrderBy(episode => episode.PublishedUtc)
            : candidates.OrderByDescending(episode => episode.PublishedUtc);
        return candidates.Take(Math.Max(0, show.SyncEpisodeCount)).ToList();
    }

    public static async Task DownloadEpisodeAsync(PodcastShow show, PodcastEpisode episode, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (episode.IsDownloaded) return;
        var showDirectory = Path.Combine(PodcastDirectory, show.Id.ToString("N"));
        Directory.CreateDirectory(showDirectory);
        var extension = Extension(episode.EnclosureUrl, episode.MimeType);
        var fileName = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(episode.Id))).ToLowerInvariant() + extension;
        var finalPath = Path.Combine(showDirectory, fileName);
        var temporaryPath = finalPath + ".download";
        try
        {
            using var response = await Client.GetAsync(episode.EnclosureUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    if (total > 0) progress?.Report(received * 100d / total.Value);
                }
            }
            File.Move(temporaryPath, finalPath, true);
            episode.LocalPath = finalPath;
        }
        finally { TryDelete(temporaryPath); }
    }

    public static void MarkPlayed(PodcastEpisode episode)
    {
        episode.IsPlayed = true;
        episode.PlayedUtc = DateTime.UtcNow;
        DeleteDownload(episode);
    }

    public static void MarkUnplayed(PodcastEpisode episode)
    {
        episode.IsPlayed = false;
        episode.PlayedUtc = null;
    }

    public static void DeleteDownload(PodcastEpisode episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.LocalPath)) TryDelete(episode.LocalPath);
        episode.LocalPath = null;
    }

    private static async Task<string?> DownloadArtworkAsync(PodcastShow show, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.Combine(PodcastDirectory, show.Id.ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "show-artwork.jpg");
            var bytes = await Client.GetByteArrayAsync(show.ArtworkUrl, cancellationToken);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return path;
        }
        catch { return null; }
    }

    private static (string Url, string Type, long Length) Enclosure(XElement item)
    {
        var element = item.Elements().FirstOrDefault(child => child.Name.LocalName == "enclosure") ??
            item.Elements().FirstOrDefault(child => child.Name.LocalName == "link" && (string?)child.Attribute("rel") == "enclosure");
        var url = (string?)element?.Attribute("url") ?? (string?)element?.Attribute("href") ?? "";
        _ = long.TryParse((string?)element?.Attribute("length"), out var length);
        return (url, (string?)element?.Attribute("type") ?? "", length);
    }

    private static string? ArtworkUrl(XElement element)
    {
        var image = element.Elements().FirstOrDefault(child => child.Name.LocalName == "image");
        return (string?)image?.Attribute("href") ?? image?.Elements().FirstOrDefault(child => child.Name.LocalName == "url")?.Value?.Trim();
    }

    private static string? ChildValue(XElement parent, params string[] names) =>
        parent.Elements().FirstOrDefault(child => names.Contains(child.Name.LocalName, StringComparer.OrdinalIgnoreCase))?.Value?.Trim();

    private static DateTime ParseDate(string? value, DateTime fallback) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed.UtcDateTime : fallback;

    private static string StripMarkup(string value)
    {
        try { return System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " ")).Trim(); }
        catch { return value; }
    }

    private static string Extension(string url, string mimeType)
    {
        var extension = Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "").ToLowerInvariant();
        if (extension is ".mp3" or ".m4a" or ".m4b" or ".aac" or ".wav" or ".ogg") return extension;
        return mimeType.ToLowerInvariant() switch { "audio/mp4" or "audio/x-m4a" => ".m4a", "audio/aac" => ".aac", _ => ".mp3" };
    }

    private static string String(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        return "";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("hTunes/1.0 podcast-client");
        return client;
    }

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
    }
}
