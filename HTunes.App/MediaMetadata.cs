using System.IO;

namespace HTunes.App;

internal static class MediaMetadata
{
    public static void ReadInto(Track track, bool onlyMissing = false)
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
            if (track.ArtworkPath is null && tag.Pictures.Length > 0) track.ArtworkPath = SaveArtwork(track, tag.Pictures[0]);
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
