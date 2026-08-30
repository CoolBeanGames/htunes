using Clickwheel;
using Clickwheel.Parsers.iTunesDB;
using System.IO;
using IPodPlaylist = Clickwheel.Parsers.iTunesDB.Playlist;
using IPodTrack = Clickwheel.Parsers.iTunesDB.Track;

namespace HTunes.App;

internal sealed record PodcastSyncResult(int Added, int Removed, int AlreadyPresent, int Missing)
{
    public string Summary => $"Added {Added} podcast episode{(Added == 1 ? "" : "s")}, removed {Removed}, " +
        $"{AlreadyPresent} already on the iPod, and {Missing} could not be copied.";
}

internal sealed record PodcastPlaybackUpdate(string EpisodeId, string ShowTitle, long PositionMs, long DurationMs, bool IsPlayed);

internal static class PodcastIPodSyncService
{
    private sealed record RemovedMedia(string OriginalPath, string BackupPath);

    public static PodcastSyncResult Sync(string rootPath, IReadOnlyCollection<PodcastEpisodeSelection> selections, IReadOnlyCollection<PodcastShow> subscriptions, bool mirrorSubscriptions)
    {
        var ipod = IPod.GetiPodByDrive(rootPath, IPodLoadAction.NoSync);
        ipod.AssertIsWritable();
        var backup = BackupDatabase(rootPath, "podcasts");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"hTunes-podcast-sync-{Guid.NewGuid():N}");
        var removedMedia = new List<RemovedMedia>();
        var addedTracks = new List<IPodTrack>();
        Clickwheel.IPodBackup.EnableBackups = false;
        ipod.AcquireLock();
        try
        {
            var tracks = Tracks(ipod);
            var desired = selections.Select(selection => EpisodeKey(selection.Show.Title, selection.Episode)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var subscribedTitles = subscriptions.Select(show => show.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = 0;
            if (mirrorSubscriptions)
            {
                foreach (var track in tracks.Where(track => IsPodcast(track) && subscribedTitles.Contains(track.Album) && !desired.Contains(TrackKey(track))).ToList())
                {
                    BackupMedia(rootPath, track, temporaryDirectory, removedMedia);
                    ipod.Tracks.Remove(track);
                    removed++;
                }
            }

            var (podcastPlaylist, needsPlaylistRepair) = GetWritablePodcastPlaylist(ipod);
            // Establish/repair the normal Podcasts playlist before the final save. Clickwheel then mirrors it
            // into the special grouped podcast section used by the stock iPod Podcasts menu.
            ipod.SaveChanges();
            if (needsPlaylistRepair)
            {
                foreach (var track in podcastPlaylist.Tracks.ToList())
                {
                    podcastPlaylist.RemoveTrack(track);
                    podcastPlaylist.AddTrack(track);
                }
            }
            var alreadyPresent = 0;
            var missing = 0;
            foreach (var selection in selections)
            {
                var episode = selection.Episode;
                var show = selection.Show;
                var currentTracks = Tracks(ipod);
                var existing = currentTracks.FirstOrDefault(track => IsPodcast(track) && TrackMatches(track, show, episode));
                if (existing is not null)
                {
                    alreadyPresent++;
                    if (!podcastPlaylist.ContainsTrack(existing)) podcastPlaylist.AddTrack(existing);
                    continue;
                }
                if (!episode.IsDownloaded || string.IsNullOrWhiteSpace(episode.LocalPath)) { missing++; continue; }
                var added = ipod.Tracks.Add(CreateTrack(show, episode));
                added.PodcastFlag = true;
                added.RememberPlaybackPosition = true;
                added.MediaType = MediaType.Podcast;
                podcastPlaylist.AddTrack(added);
                addedTracks.Add(added);
            }
            ipod.SaveChanges();
            return new PodcastSyncResult(addedTracks.Count, removed, alreadyPresent, missing);
        }
        catch
        {
            foreach (var track in addedTracks)
            {
                try { var path = ResolveIPodPath(rootPath, track.FilePath); if (File.Exists(path)) File.Delete(path); } catch { }
            }
            foreach (var media in removedMedia)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(media.OriginalPath)!); File.Copy(media.BackupPath, media.OriginalPath, true); } catch { }
            }
            try { RestoreDatabase(backup); } catch { }
            throw;
        }
        finally
        {
            try { ipod.ReleaseLock(); } catch { }
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true); } catch { }
        }
    }

    private static NewTrack CreateTrack(PodcastShow show, PodcastEpisode episode)
    {
        uint length = 0, bitrate = 0;
        try
        {
            using var media = TagLib.File.Create(episode.LocalPath!);
            length = (uint)Math.Clamp(media.Properties.Duration.TotalMilliseconds, 0, uint.MaxValue);
            bitrate = (uint)Math.Max(0, media.Properties.AudioBitrate);
        }
        catch { }
        _ = uint.TryParse(episode.EpisodeNumber, out var number);
        return new NewTrack
        {
            FilePath = episode.LocalPath!, Title = episode.Title, Artist = show.Author, AlbumArtist = show.Author,
            Album = show.Title, Genre = "Podcast", Comments = episode.Id, DescriptionText = episode.Description,
            TrackNumber = number, AlbumTrackCount = (uint)show.Episodes.Count, Year = (uint)Math.Max(0, episode.PublishedUtc.Year),
            DiscNumber = 1, TotalDiscCount = 1, Length = length, Bitrate = bitrate, IsVideo = false,
            ArtworkFile = File.Exists(show.ArtworkPath) ? show.ArtworkPath : null
        };
    }

    private static (IPodPlaylist Playlist, bool NeedsRepair) GetWritablePodcastPlaylist(IPod ipod)
    {
        var playlists = new List<IPodPlaylist>();
        foreach (var playlist in ipod.Playlists) playlists.Add(playlist);
        var result = playlists.FirstOrDefault(playlist => playlist.Name.Equals("Podcasts", StringComparison.OrdinalIgnoreCase));
        if (result is null)
        {
            result = ipod.Playlists.Add("Podcasts");
            return (result, false);
        }
        if (result.IsPodcastPlaylist) return (result, false);

        // Older hTunes builds accidentally made this a normal playlist. Setting the name marks the
        // playlist dirty, and the second save in Sync rebuilds its special podcast group entries.
        result.Name = "Podcasts";
        result.IsPodcastPlaylist = true;
        return (result, true);
    }

    private static List<IPodTrack> Tracks(IPod ipod)
    {
        var result = new List<IPodTrack>();
        foreach (var track in ipod.Tracks) result.Add(track);
        return result;
    }

    private static bool IsPodcast(IPodTrack track) => track.PodcastFlag || track.MediaType is MediaType.Podcast or MediaType.VideoPodcast || string.Equals(track.Genre, "Podcast", StringComparison.OrdinalIgnoreCase);
    private static string EpisodeKey(string showTitle, PodcastEpisode episode) => $"{showTitle.Trim()}\u001f{episode.Id.Trim()}\u001f{episode.Title.Trim()}";
    private static string TrackKey(IPodTrack track) => $"{track.Album.Trim()}\u001f{track.Comment.Trim()}\u001f{track.Title.Trim()}";
    private static bool TrackMatches(IPodTrack track, PodcastShow show, PodcastEpisode episode) =>
        Same(track.Album, show.Title) && ((!string.IsNullOrWhiteSpace(track.Comment) && Same(track.Comment, episode.Id)) || Same(track.Title, episode.Title));

    private static void BackupMedia(string rootPath, IPodTrack track, string temporaryDirectory, ICollection<RemovedMedia> backups)
    {
        var original = ResolveIPodPath(rootPath, track.FilePath);
        if (!File.Exists(original)) return;
        Directory.CreateDirectory(temporaryDirectory);
        var backup = Path.Combine(temporaryDirectory, $"removed-{Guid.NewGuid():N}{Path.GetExtension(original)}");
        File.Copy(original, backup, true); backups.Add(new RemovedMedia(original, backup));
    }

    private static string ResolveIPodPath(string rootPath, string storedPath)
    {
        if (Path.IsPathFullyQualified(storedPath)) return storedPath;
        return Path.Combine(rootPath, storedPath.Replace(':', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
    }

    private static string BackupDatabase(string rootPath, string suffix)
    {
        var source = Path.Combine(rootPath, "iPod_Control", "iTunes", "iTunesDB");
        if (!File.Exists(source)) source = Path.Combine(rootPath, "iPod_Control", "iTunes", "iTunesCDB");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "ipod-backups");
        Directory.CreateDirectory(directory);
        var backup = Path.Combine(directory, $"{Path.GetFileName(source)}-{suffix}-{DateTime.Now:yyyyMMdd-HHmmssfff}.backup");
        File.Copy(source, backup, true);
        foreach (var old in new DirectoryInfo(directory).GetFiles("*.backup").OrderByDescending(file => file.CreationTimeUtc).Skip(10)) try { old.Delete(); } catch { }
        return backup + "|" + source;
    }

    private static void RestoreDatabase(string backupInfo)
    {
        var parts = backupInfo.Split('|', 2);
        if (parts.Length == 2 && File.Exists(parts[0])) File.Copy(parts[0], parts[1], true);
    }

    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
