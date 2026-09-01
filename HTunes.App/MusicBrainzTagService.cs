using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace HTunes.App;

internal sealed record AutoTagMatch(IReadOnlyDictionary<string, string> Fields, string Description);

internal static class MusicBrainzTagService
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime lastRequestUtc;

    public static async Task<AutoTagMatch?> FindAsync(Track track, bool force, CancellationToken token)
    {
        if (track.AutoTagged && !force) return null;
        var title = Clean(track.Title, "Unknown title");
        var artist = Clean(track.Artist, "Unknown Artist");
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(track.FilePath);
        var query = $"recording:\"{Escape(title)}\"" + (artist.Length > 0 ? $" AND artist:\"{Escape(artist)}\"" : "") +
            (!force && !Missing(track.Album, "Unknown Album") ? $" AND release:\"{Escape(track.Album)}\"" : "");
        using var document = await GetJsonAsync("recording/?fmt=json&limit=5&query=" + Uri.EscapeDataString(query), token);
        if (!document.RootElement.TryGetProperty("recordings", out var recordings) || recordings.GetArrayLength() == 0) return null;
        var recording = recordings.EnumerateArray().OrderByDescending(item => Integer(item, "score")).First();
        if (Integer(recording, "score") < 70) return null;

        var resultTitle = String(recording, "title");
        var resultArtist = ArtistCredit(recording);
        var release = recording.TryGetProperty("releases", out var releases) ? releases.EnumerateArray().FirstOrDefault() : default;
        var resultAlbum = release.ValueKind == JsonValueKind.Object ? String(release, "title") : "";
        var year = ParseYear(String(recording, "first-release-date"));
        var tags = ReadTags(recording).ToList();
        var trackNumber = 0;
        var albumArtist = "";
        if (release.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(String(release, "id")))
        {
            using var releaseDocument = await GetJsonAsync($"release/{String(release, "id")}?fmt=json&inc=recordings+artist-credits+release-groups+tags", token);
            albumArtist = ArtistCredit(releaseDocument.RootElement);
            tags.AddRange(ReadTags(releaseDocument.RootElement));
            if (releaseDocument.RootElement.TryGetProperty("media", out var media))
                foreach (var medium in media.EnumerateArray())
                    if (medium.TryGetProperty("tracks", out var tracks))
                        foreach (var item in tracks.EnumerateArray())
                            if (item.TryGetProperty("recording", out var itemRecording) && String(itemRecording, "id") == String(recording, "id"))
                                trackNumber = Integer(item, "position");
        }

        var genre = SimplifyGenre(tags, resultArtist, resultAlbum);
        if (genre == "Game Music") albumArtist = GameSoundTeam(albumArtist, resultArtist, resultAlbum);
        var fields = new Dictionary<string, string>();
        Add("Title", resultTitle, Missing(track.Title, "Unknown title"));
        Add("Artist", resultArtist, Missing(track.Artist, "Unknown Artist"));
        Add("Album", resultAlbum, Missing(track.Album, "Unknown Album"));
        Add("AlbumArtist", albumArtist, string.IsNullOrWhiteSpace(track.AlbumArtist));
        Add("Genre", genre, Missing(track.Genre, "Unknown Genre") || track.Genre.Equals("Music", StringComparison.OrdinalIgnoreCase));
        if (trackNumber > 0 && (force || track.TrackNumber <= 0)) fields["TrackNumber"] = trackNumber.ToString();
        if (year > 0 && (force || track.Year <= 0)) fields["Year"] = year.ToString();
        return new AutoTagMatch(fields, $"{resultArtist} — {resultAlbum}");

        void Add(string key, string value, bool missing)
        {
            if (!string.IsNullOrWhiteSpace(value) && (force || missing)) fields[key] = value.Trim();
        }
    }

    internal static string SimplifyGenre(IEnumerable<string> source, string artist, string album)
    {
        var value = string.Join(' ', source.Append(artist).Append(album)).ToLowerInvariant();
        if (Contains(value, "soundtrack", "video game", "videogame", "game music", "vgm", "original game score")) return "Game Music";
        if (Contains(value, "panic! at the disco", "panic at the disco", "my chemical romance", "the used", "emo")) return "Emo";
        if (Contains(value, "youtuber", "youtube musician")) return "Youtuber";
        if (Contains(value, "metal")) return "Metal";
        if (Contains(value, "alternative", "grunge", "indie rock")) return "Alternative";
        if (Contains(value, "classic rock", "hard rock", "psychedelic rock", "progressive rock")) return "Classic Rock";
        if (Contains(value, "rock")) return "Rock";
        if (Contains(value, "hip hop", "hip-hop", "rap", "r&b", "rhythm and blues")) return "Rap";
        if (Contains(value, "pop")) return "Pop";
        if (Contains(value, "country")) return "Country";
        if (Contains(value, "jazz")) return "Jazz";
        if (Contains(value, "classical")) return "Classical";
        if (Contains(value, "electronic", "electronica", "edm", "house", "techno", "trance")) return "Electronic";
        return "";
    }

    private static string GameSoundTeam(string albumArtist, string artist, string album)
    {
        var combined = albumArtist + " " + artist + " " + album;
        foreach (var company in new[] { "Atlus", "Nintendo", "Sega", "Capcom", "Konami", "Square Enix", "Bandai Namco", "Ubisoft", "Bethesda" })
            if (combined.Contains(company, StringComparison.OrdinalIgnoreCase)) return company + " Sound Team";
        return albumArtist.Contains("Sound Team", StringComparison.OrdinalIgnoreCase) ? albumArtist : "";
    }

    private static async Task<JsonDocument> GetJsonAsync(string relative, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - lastRequestUtc);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
            using var response = await Client.GetAsync(relative, token);
            lastRequestUtc = DateTime.UtcNow;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: token);
        }
        finally { Gate.Release(); }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://musicbrainz.org/ws/2/"), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("hTunes/1.0 (https://github.com/CoolBeanGames/htunes)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static IEnumerable<string> ReadTags(JsonElement element) => element.TryGetProperty("tags", out var tags)
        ? tags.EnumerateArray().OrderByDescending(tag => Integer(tag, "count")).Select(tag => String(tag, "name")) : [];
    private static string ArtistCredit(JsonElement element) => element.TryGetProperty("artist-credit", out var credit)
        ? string.Concat(credit.EnumerateArray().Select(item => String(item, "name") + String(item, "joinphrase"))) : "";
    private static string String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static int Integer(JsonElement element, string name) => int.TryParse(String(element, name), out var value) ? value : 0;
    private static int ParseYear(string value) => value.Length >= 4 && int.TryParse(value[..4], out var year) ? year : 0;
    private static string Clean(string value, string placeholder) => Missing(value, placeholder) ? "" : value.Trim();
    private static bool Missing(string value, string placeholder) => string.IsNullOrWhiteSpace(value) || value.Equals(placeholder, StringComparison.OrdinalIgnoreCase);
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static bool Contains(string value, params string[] options) => options.Any(option => value.Contains(option, StringComparison.OrdinalIgnoreCase));
}
