using Clickwheel;
using Clickwheel.Exceptions;
using System.IO;

namespace HTunes.App;

internal sealed record SyncProgress(int Completed, int Total, string Message);
internal sealed record SyncResult(int Added, int AlreadyPresent, int Unsupported, int Missing, int NoSpace)
{
    public string Summary => $"Added {Added} song{(Added == 1 ? "" : "s")}. " +
        $"{AlreadyPresent} already on iPod, {Unsupported} unsupported, {Missing} missing, {NoSpace} skipped for space.";
}

internal static class IPodSyncService
{
    private static readonly string[] SupportedExtensions = [".mp3", ".m4a", ".aac", ".wav", ".m4b", ".aa"];
    private const long SpaceReserve = 20L * 1024 * 1024;

    public static SyncResult Sync(string rootPath, IReadOnlyCollection<Track> requested, IReadOnlyCollection<Track> library, bool randomFill, IProgress<SyncProgress>? progress = null)
    {
        var unique = requested.DistinctBy(t => t.Id).ToList();
        var missing = unique.Count(t => !File.Exists(t.FilePath));
        var eligible = unique.Where(t => File.Exists(t.FilePath)).ToList();
        var unsupported = eligible.RemoveAll(t => !SupportedExtensions.Contains(Path.GetExtension(t.FilePath), StringComparer.OrdinalIgnoreCase) || new FileInfo(t.FilePath).Length > uint.MaxValue);
        var ipod = IPod.GetiPodByDrive(rootPath, IPodLoadAction.NoSync);
        ipod.AssertIsWritable();

        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in ipod.Tracks) existingKeys.Add(TrackIdentity.Key(track.Title, track.Artist, track.Album, checked((int)track.TrackNumber)));
        var candidates = eligible.Where(t => !existingKeys.Contains(TrackIdentity.Key(t.Title, t.Artist, t.Album, t.TrackNumber))).ToList();
        var alreadyPresent = eligible.Count - candidates.Count;
        if (randomFill) Shuffle(candidates);

        var remaining = Math.Max(0, new DriveInfo(rootPath).AvailableFreeSpace - SpaceReserve);
        var selected = new List<Track>();
        var noSpace = 0;
        foreach (var track in candidates)
        {
            var size = new FileInfo(track.FilePath).Length;
            if (size <= remaining) { selected.Add(track); remaining -= size; }
            else noSpace++;
        }
        if (selected.Count == 0) return new SyncResult(0, alreadyPresent, unsupported, missing, noSpace);

        var backup = BackupDatabase(rootPath);
        var added = new List<Clickwheel.Parsers.iTunesDB.Track>();
        Clickwheel.IPodBackup.EnableBackups = false;
        ipod.AcquireLock();
        try
        {
            for (var index = 0; index < selected.Count; index++)
            {
                var source = selected[index];
                progress?.Report(new SyncProgress(index, selected.Count, $"Copying {source.Title}"));
                try { added.Add(ipod.Tracks.Add(CreateNewTrack(source, library))); }
                catch (TrackAlreadyExistsException) { alreadyPresent++; }
            }
            progress?.Report(new SyncProgress(selected.Count, selected.Count, "Updating the iPod library"));
            ipod.SaveChanges();
            return new SyncResult(added.Count, alreadyPresent, unsupported, missing, noSpace);
        }
        catch
        {
            foreach (var track in added)
            {
                try
                {
                    var copiedPath = Path.Combine(rootPath, track.FilePath);
                    if (File.Exists(copiedPath)) File.Delete(copiedPath);
                }
                catch { }
            }
            try { RestoreDatabase(backup); } catch { }
            throw;
        }
        finally
        {
            try { ipod.ReleaseLock(); } catch { }
        }
    }

    private static NewTrack CreateNewTrack(Track source, IReadOnlyCollection<Track> library)
    {
        uint length = 0, bitrate = 0;
        try
        {
            using var media = TagLib.File.Create(source.FilePath);
            length = (uint)Math.Clamp(media.Properties.Duration.TotalMilliseconds, 0, uint.MaxValue);
            bitrate = (uint)Math.Max(0, media.Properties.AudioBitrate);
        }
        catch { }
        var albumTracks = library.Where(t => Same(t.Artist, source.Artist) && Same(t.Album, source.Album)).ToList();
        return new NewTrack
        {
            FilePath = source.FilePath,
            Title = source.Title,
            Artist = source.Artist,
            AlbumArtist = source.Artist,
            Album = source.Album,
            Genre = source.Genre,
            TrackNumber = (uint)Math.Max(0, source.TrackNumber),
            AlbumTrackCount = (uint)albumTracks.Count,
            DiscNumber = (uint)Math.Max(1, source.DiscNumber),
            TotalDiscCount = (uint)Math.Max(1, albumTracks.Select(t => t.DiscNumber).DefaultIfEmpty(1).Max()),
            Year = (uint)Math.Max(0, source.Year),
            Length = length,
            Bitrate = bitrate,
            IsVideo = false,
            ArtworkFile = source.ArtworkPath is not null && File.Exists(source.ArtworkPath) ? source.ArtworkPath : null
        };
    }

    private static string BackupDatabase(string rootPath)
    {
        var source = Path.Combine(rootPath, "iPod_Control", "iTunes", "iTunesDB");
        if (!File.Exists(source)) source = Path.Combine(rootPath, "iPod_Control", "iTunes", "iTunesCDB");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "ipod-backups");
        Directory.CreateDirectory(directory);
        var backup = Path.Combine(directory, $"{Path.GetFileName(source)}-{DateTime.Now:yyyyMMdd-HHmmssfff}.backup");
        File.Copy(source, backup, true);
        foreach (var old in new DirectoryInfo(directory).GetFiles("*.backup").OrderByDescending(f => f.CreationTimeUtc).Skip(5)) old.Delete();
        return backup + "|" + source;
    }

    private static void RestoreDatabase(string backupInfo)
    {
        var parts = backupInfo.Split('|', 2);
        if (parts.Length == 2 && File.Exists(parts[0])) File.Copy(parts[0], parts[1], true);
    }

    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static void Shuffle<T>(IList<T> values)
    {
        for (var i = values.Count - 1; i > 0; i--) { var j = Random.Shared.Next(i + 1); (values[i], values[j]) = (values[j], values[i]); }
    }
}
