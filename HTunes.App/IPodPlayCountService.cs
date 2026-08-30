using Clickwheel;
using System.IO;

namespace HTunes.App;

internal sealed record PlayCountUpdate(Guid TrackId, int Count, string DeviceId);
internal sealed record PlayCountReconcileResult(IReadOnlyList<PlayCountUpdate> MusicUpdates, IReadOnlyList<PlayedPodcastEpisode> PlayedPodcasts);

internal static class TrackIdentity
{
    public static string Key(string title, string artist, string album, int trackNumber) =>
        $"{title.Trim()}\u001f{artist.Trim()}\u001f{album.Trim()}\u001f{Math.Max(0, trackNumber)}";
}

internal static class IPodPlayCountService
{
    public static PlayCountReconcileResult Reconcile(string rootPath, IReadOnlyCollection<Track> library, IReadOnlyCollection<PodcastShow> podcastShows)
    {
        var dbPath = DatabasePath(rootPath);
        var playCountsPath = Path.Combine(rootPath, "iPod_Control", "iTunes", "Play Counts");
        var backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "ipod-backups");
        Directory.CreateDirectory(backupDirectory);
        var token = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
        var dbBackup = Path.Combine(backupDirectory, $"{Path.GetFileName(dbPath)}-plays-{token}.backup");
        var countsBackup = Path.Combine(backupDirectory, $"PlayCounts-{token}.backup");
        var mediaBackupDirectory = Path.Combine(Path.GetTempPath(), $"hTunes-podcast-playback-{Guid.NewGuid():N}");
        var removedMedia = new List<(string Original, string Backup)>();
        File.Copy(dbPath, dbBackup, true);
        if (File.Exists(playCountsPath)) File.Copy(playCountsPath, countsBackup, true);

        Clickwheel.IPod? ipod = null;
        try
        {
            ipod = Clickwheel.IPod.GetiPodByDrive(rootPath, IPodLoadAction.SyncPlayCounts);
            ipod.AssertIsWritable();
            ipod.AcquireLock();
            var deviceId = DeviceId(ipod, rootPath);
            var localByKey = library.GroupBy(t => TrackIdentity.Key(t.Title, t.Artist, t.Album, t.TrackNumber), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var updates = new List<PlayCountUpdate>();
            var ipodTracks = new List<Clickwheel.Parsers.iTunesDB.Track>();
            foreach (var item in ipod.Tracks) ipodTracks.Add(item);
            var playedPodcasts = new List<PlayedPodcastEpisode>();
            foreach (var ipodTrack in ipodTracks)
            {
                var podcast = FindPodcastEpisode(podcastShows, ipodTrack);
                if (podcast is not null && (podcast.Value.Episode.IsPlayed || ipodTrack.PlayCount > 0))
                {
                    playedPodcasts.Add(new PlayedPodcastEpisode(podcast.Value.Episode.Id, podcast.Value.Show.Title));
                    BackupMedia(rootPath, ipodTrack.FilePath, mediaBackupDirectory, removedMedia);
                    ipod.Tracks.Remove(ipodTrack);
                    continue;
                }
                var key = TrackIdentity.Key(ipodTrack.Title, ipodTrack.Artist, ipodTrack.Album, checked((int)ipodTrack.TrackNumber));
                if (!localByKey.TryGetValue(key, out var local)) continue;
                var ipodCount = Math.Max(0, ipodTrack.PlayCount);
                var localCount = Math.Max(0, local.PlayCount);
                int combined;
                local.SyncedPlayCounts ??= [];
                if (local.SyncedPlayCounts.TryGetValue(deviceId, out var baseline))
                    combined = baseline + Math.Max(0, localCount - baseline) + Math.Max(0, ipodCount - baseline);
                else
                    combined = Math.Max(localCount, ipodCount);
                ipodTrack.PlayCount = combined;
                updates.Add(new PlayCountUpdate(local.Id, combined, deviceId));
            }
            ipod.SaveChanges();
            CleanupBackups(backupDirectory);
            return new PlayCountReconcileResult(updates, playedPodcasts);
        }
        catch
        {
            try { File.Copy(dbBackup, dbPath, true); } catch { }
            try { if (File.Exists(countsBackup)) File.Copy(countsBackup, playCountsPath, true); } catch { }
            foreach (var media in removedMedia)
                try { Directory.CreateDirectory(Path.GetDirectoryName(media.Original)!); File.Copy(media.Backup, media.Original, true); } catch { }
            throw;
        }
        finally
        {
            try { ipod?.ReleaseLock(); } catch { }
            try { if (Directory.Exists(mediaBackupDirectory)) Directory.Delete(mediaBackupDirectory, true); } catch { }
        }
    }

    private static (PodcastShow Show, PodcastEpisode Episode)? FindPodcastEpisode(IEnumerable<PodcastShow> shows, Clickwheel.Parsers.iTunesDB.Track track)
    {
        var isPodcast = track.PodcastFlag || track.MediaType is Clickwheel.Parsers.iTunesDB.MediaType.Podcast or Clickwheel.Parsers.iTunesDB.MediaType.VideoPodcast || string.Equals(track.Genre, "Podcast", StringComparison.OrdinalIgnoreCase);
        if (!isPodcast) return null;
        foreach (var show in shows.Where(show => show.Title.Equals(track.Album, StringComparison.OrdinalIgnoreCase)))
            foreach (var episode in show.Episodes)
                if ((!string.IsNullOrWhiteSpace(track.Comment) && episode.Id.Equals(track.Comment, StringComparison.OrdinalIgnoreCase)) || episode.Title.Equals(track.Title, StringComparison.OrdinalIgnoreCase)) return (show, episode);
        return null;
    }

    private static void BackupMedia(string rootPath, string storedPath, string directory, ICollection<(string Original, string Backup)> backups)
    {
        var original = Path.IsPathFullyQualified(storedPath)
            ? storedPath
            : Path.Combine(rootPath, storedPath.Replace(':', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
        if (!File.Exists(original)) return;
        Directory.CreateDirectory(directory);
        var backup = Path.Combine(directory, $"played-{Guid.NewGuid():N}{Path.GetExtension(original)}");
        File.Copy(original, backup, true);
        backups.Add((original, backup));
    }

    private static string DeviceId(Clickwheel.IPod ipod, string rootPath)
    {
        if (!string.IsNullOrWhiteSpace(ipod.DeviceInfo.FirewireId)) return "firewire:" + ipod.DeviceInfo.FirewireId;
        if (!string.IsNullOrWhiteSpace(ipod.DeviceInfo.SerialNumber)) return "serial:" + ipod.DeviceInfo.SerialNumber;
        return "volume:" + new DriveInfo(rootPath).VolumeLabel + ":" + new DriveInfo(rootPath).TotalSize;
    }

    private static string DatabasePath(string rootPath)
    {
        var path = Path.Combine(rootPath, "iPod_Control", "iTunes", "iTunesDB");
        return File.Exists(path) ? path : Path.Combine(rootPath, "iPod_Control", "iTunes", "iTunesCDB");
    }

    private static void CleanupBackups(string directory)
    {
        foreach (var old in new DirectoryInfo(directory).GetFiles("*.backup").OrderByDescending(f => f.CreationTimeUtc).Skip(10))
            try { old.Delete(); } catch { }
    }
}
