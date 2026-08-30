using System.IO;

namespace HTunes.App;

internal static class MediaMetadata
{
    public static void ReadInto(Track track, bool onlyMissing = false, bool extractArtwork = true)
    {
        try
        {
            using var media = TagLib.File.Create(track.FilePath);
            var tag = media.Tag;
            track.Title = Value(track.Title, tag.Title, "Unknown title", onlyMissing);
            track.Artist = Value(track.Artist, First(tag.FirstPerformer, tag.FirstAlbumArtist), "Unknown Artist", onlyMissing);
            track.Album = Value(track.Album, tag.Album, "Unknown Album", onlyMissing);
            track.Genre = Value(track.Genre, tag.FirstGenre, "Unknown Genre", onlyMissing);
            if (!onlyMissing || track.TrackNumber == 0) track.TrackNumber = checked((int)tag.Track);
            if (!onlyMissing || track.DiscNumber <= 1) track.DiscNumber = tag.Disc == 0 ? 1 : checked((int)tag.Disc);
            if (!onlyMissing || track.Year == 0) track.Year = checked((int)tag.Year);
            if (!onlyMissing || track.BitrateKbps == 0) track.BitrateKbps = Math.Max(0, media.Properties.AudioBitrate);
            if (!onlyMissing || string.IsNullOrWhiteSpace(track.Format)) track.Format = ReadFormat(media, track.FilePath);
            if (extractArtwork && (string.IsNullOrWhiteSpace(track.ArtworkPath) || !File.Exists(track.ArtworkPath)) && tag.Pictures.Length > 0)
                track.ArtworkPath = SaveArtwork(track, tag.Pictures[0]);
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException)
        {
            // Keep filename-derived fallback values for unreadable or unsupported files.
        }
    }

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Value(string target, string? value, string missingValue, bool onlyMissing)
    {
        if (string.IsNullOrWhiteSpace(value)) return target;
        return !onlyMissing || string.IsNullOrWhiteSpace(target) || target == missingValue ? value.Trim() : target;
    }

    private static string ReadFormat(TagLib.File media, string path)
    {
        var descriptions = string.Join(" ", media.Properties.Codecs.Select(codec => codec.Description));
        if (descriptions.Contains("Apple Lossless", StringComparison.OrdinalIgnoreCase) || descriptions.Contains("ALAC", StringComparison.OrdinalIgnoreCase)) return "ALAC";
        if (descriptions.Contains("AAC", StringComparison.OrdinalIgnoreCase)) return "AAC";
        if (descriptions.Contains("MPEG", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase)) return "MP3";
        return Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch { "M4A" => "AAC", "OGG" => "Ogg Vorbis", var value => value };
    }

    private static string? SaveArtwork(Track track, TagLib.IPicture picture)
    {
        try
        {
            var artworkDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "artwork");
            Directory.CreateDirectory(artworkDirectory);
            var extension = picture.MimeType?.ToLowerInvariant() switch { "image/png" => ".png", "image/bmp" => ".bmp", _ => ".jpg" };
            var path = Path.Combine(artworkDirectory, track.Id + extension);
            File.WriteAllBytes(path, picture.Data.Data);
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
