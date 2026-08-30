using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HTunes.App;

internal sealed record TagPatch(IReadOnlyDictionary<string, string> Fields, bool ChangeArtwork = false, string? Artwork = null,
    bool ResizeArtwork = false, int Width = 600, int Height = 600)
{
    public bool HasChanges => Fields.Count > 0 || ChangeArtwork || ResizeArtwork;
    public void Validate()
    {
        foreach (var (key, value) in Fields)
        {
            if (key is not ("Title" or "Artist" or "Album" or "Genre" or "TrackNumber" or "DiscNumber" or "Year")) throw new ArgumentException("Unknown tag field.");
            if (key is "TrackNumber" or "DiscNumber" or "Year" && (!int.TryParse(value, out var number) || number < 0 || number > (key == "Year" ? 9999 : 65535)))
                throw new ArgumentException(key + " must be a whole number from 0 to " + (key == "Year" ? "9999." : "65535."));
        }
        if (ResizeArtwork && (Width < 16 || Height < 16 || Width > 4096 || Height > 4096)) throw new ArgumentException("Artwork dimensions must be between 16 and 4096 pixels.");
    }
}

internal static class TagArtwork
{
    public static BitmapSource Read(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000) throw new InvalidDataException("Choose artwork smaller than 40 megapixels.");
        frame.Freeze(); return frame;
    }

    public static string Prepare(string path, string directory, int? width = null, int? height = null)
    {
        BitmapSource bitmap = Read(path);
        if (width is not null && height is not null)
        {
            var scale = Math.Min((double)width.Value / bitmap.PixelWidth, (double)height.Value / bitmap.PixelHeight);
            if (Math.Abs(scale - 1) > 0.001) { bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)); bitmap.Freeze(); }
        }
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "tag-" + Guid.NewGuid().ToString("N") + ".png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(destination); encoder.Save(output);
        return destination;
    }
}

// A batch remembers only the edited fields. Undo never rewinds play counts or unrelated metadata.
internal sealed class TagBatchEdit
{
    private sealed record Values(string Title, string Artist, string Album, string Genre, int Number, int Disc, int Year, string? Artwork, bool Managed)
    {
        public static Values Read(Track t) => new(t.Title, t.Artist, t.Album, t.Genre, t.TrackNumber, t.DiscNumber, t.Year, t.ArtworkPath, t.MetadataManagedByLibrary);
        public Values Change(TagPatch patch, string artworkDirectory)
        {
            string Text(string field, string fallback) => patch.Fields.TryGetValue(field, out var value) ? value.Trim() : fallback;
            int NumberValue(string field, int fallback) => patch.Fields.TryGetValue(field, out var value) ? int.Parse(value) : fallback;
            var art = patch.ChangeArtwork ? patch.Artwork : Artwork;
            if (art is not null && (patch.ChangeArtwork || patch.ResizeArtwork))
                art = TagArtwork.Prepare(art, artworkDirectory, patch.ResizeArtwork ? patch.Width : null, patch.ResizeArtwork ? patch.Height : null);
            return new(Text("Title", Title), Text("Artist", Artist), Text("Album", Album), Text("Genre", Genre), NumberValue("TrackNumber", Number), NumberValue("DiscNumber", Disc), NumberValue("Year", Year), art, true);
        }
        public void Apply(Track t, TagPatch patch)
        {
            t.MetadataManagedByLibrary = Managed;
            if (patch.Fields.ContainsKey("Title")) t.Title = Title;
            if (patch.Fields.ContainsKey("Artist")) t.Artist = Artist;
            if (patch.Fields.ContainsKey("Album")) t.Album = Album;
            if (patch.Fields.ContainsKey("Genre")) t.Genre = Genre;
            if (patch.Fields.ContainsKey("TrackNumber")) t.TrackNumber = Number;
            if (patch.Fields.ContainsKey("DiscNumber")) t.DiscNumber = Disc;
            if (patch.Fields.ContainsKey("Year")) t.Year = Year;
            if (patch.ChangeArtwork || patch.ResizeArtwork) t.ArtworkPath = Artwork;
        }
    }

    private sealed record FileTags(string? Title, string[] Artists, string? Album, string[] Genres, uint Number, uint Disc, uint Year, TagLib.IPicture[] Pictures)
    {
        public static FileTags Read(string path)
        {
            using var file = TagLib.File.Create(path); var t = file.Tag;
            return new(t.Title, t.Performers.ToArray(), t.Album, t.Genres.ToArray(), t.Track, t.Disc, t.Year,
                t.Pictures.Select(p => (TagLib.IPicture)new TagLib.Picture(new TagLib.ByteVector(p.Data.Data.ToArray())) { MimeType = p.MimeType, Type = p.Type, Description = p.Description }).ToArray());
        }
        public FileTags Change(Values v, TagPatch patch) => new(v.Title, patch.Fields.ContainsKey("Artist") ? (v.Artist.Length == 0 ? [] : [v.Artist]) : Artists,
            v.Album, patch.Fields.ContainsKey("Genre") ? (v.Genre.Length == 0 ? [] : [v.Genre]) : Genres, (uint)v.Number, (uint)v.Disc, (uint)v.Year,
            patch.ChangeArtwork || patch.ResizeArtwork ? (v.Artwork is null ? [] : [new TagLib.Picture(v.Artwork) { Type = TagLib.PictureType.FrontCover }]) : Pictures);

        public void Write(string path, TagPatch patch)
        {
            // Keep a byte-for-byte recovery copy until this file has been saved successfully.
            var backup = Path.Combine(Path.GetTempPath(), "htunes-tag-recovery-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            File.Copy(path, backup);
            var keepBackup = false;
            try
            {
                using var file = TagLib.File.Create(path); var t = file.Tag;
                if (patch.Fields.ContainsKey("Title")) t.Title = Title;
                if (patch.Fields.ContainsKey("Artist")) t.Performers = Artists;
                if (patch.Fields.ContainsKey("Album")) t.Album = Album;
                if (patch.Fields.ContainsKey("Genre")) t.Genres = Genres;
                if (patch.Fields.ContainsKey("TrackNumber")) t.Track = Number;
                if (patch.Fields.ContainsKey("DiscNumber")) t.Disc = Disc;
                if (patch.Fields.ContainsKey("Year")) t.Year = Year;
                if (patch.ChangeArtwork || patch.ResizeArtwork) t.Pictures = Pictures;
                file.Save();
            }
            catch
            {
                try { File.Copy(backup, path, true); }
                catch (Exception ex) { keepBackup = true; throw new IOException("Could not restore the audio file. Recovery copy: " + backup, ex); }
                throw;
            }
            finally { if (!keepBackup) { try { File.Delete(backup); } catch { } } }
        }
    }

    private sealed record Entry(Track Track, Values Before, Values After, FileTags? FileBefore, FileTags? FileAfter);
    private readonly List<Entry> entries;
    private readonly TagPatch patch;
    private readonly Action persist;
    private TagBatchEdit(List<Entry> entries, TagPatch patch, Action persist) { this.entries = entries; this.patch = patch; this.persist = persist; }

    public static TagBatchEdit Apply(IReadOnlyList<Track> tracks, TagPatch patch, bool writeFiles, string artworkDirectory, Action persist)
    {
        patch.Validate();
        if (tracks.Count == 0 || !patch.HasChanges) throw new ArgumentException("Select tracks and change at least one field.");
        var entries = new List<Entry>();
        foreach (var track in tracks.Distinct())
        {
            var before = Values.Read(track); var after = before.Change(patch, artworkDirectory);
            FileTags? disk = null;
            if (writeFiles)
            {
                if (!File.Exists(track.FilePath)) throw new FileNotFoundException("Audio file missing; disable 'Write tags to audio files' for a library-only edit.", track.FilePath);
                if ((File.GetAttributes(track.FilePath) & FileAttributes.ReadOnly) != 0) throw new IOException("Audio file is read-only: " + track.FilePath);
                disk = FileTags.Read(track.FilePath);
            }
            entries.Add(new(track, before, after, disk, disk?.Change(after, patch)));
        }
        var edit = new TagBatchEdit(entries, patch, persist); edit.Redo(); return edit;
    }

    public void Undo() => Set(false);
    public void Redo() => Set(true);
    private void Set(bool after)
    {
        var applied = new List<(Entry Entry, Values Library, FileTags? Disk)>();
        try
        {
            foreach (var entry in entries)
            {
                var disk = after ? entry.FileAfter : entry.FileBefore;
                var previous = disk is null ? null : FileTags.Read(entry.Track.FilePath);
                var library = Values.Read(entry.Track);
                disk?.Write(entry.Track.FilePath, patch);
                applied.Add((entry, library, previous));
                (after ? entry.After : entry.Before).Apply(entry.Track, patch);
            }
            persist();
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            foreach (var saved in applied.AsEnumerable().Reverse())
            {
                saved.Library.Apply(saved.Entry.Track, patch);
                try { saved.Disk?.Write(saved.Entry.Track.FilePath, patch); } catch (Exception ex) { failures.Add(ex); }
            }
            try { persist(); } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count > 1) throw new AggregateException("Tag save failed. Some recovery operations also failed; review the error details before retrying.", failures);
            throw;
        }
    }
}
