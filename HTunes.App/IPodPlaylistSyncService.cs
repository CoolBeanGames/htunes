using Clickwheel;
using System.IO;
using IPodPlaylist = Clickwheel.Parsers.iTunesDB.Playlist;
using IPodTrack = Clickwheel.Parsers.iTunesDB.Track;

namespace HTunes.App;

internal sealed record IPodPlaylistSyncResult(string Name, int Tracks, int Missing)
{
    public string Summary => $"Playlist “{Name}” updated with {Tracks} song{(Tracks == 1 ? "" : "s")}" +
        (Missing == 0 ? "." : $"; {Missing} unavailable song{(Missing == 1 ? " was" : "s were")} left out.");
}

internal static class IPodPlaylistSyncService
{
    // Full library -> iPod playlist reconciliation: every library playlist is upserted (membership rebuilt),
    // and device playlists left behind by a rename are removed. On-device playlists hTunes never created are untouched.
    public static IReadOnlyList<IPodPlaylistSyncResult> SyncAll(string rootPath, IReadOnlyCollection<Playlist> playlists, IReadOnlyCollection<Track> library)
    {
        var currentNames = playlists.Select(playlist => playlist.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var renamedAway = playlists.SelectMany(playlist => playlist.PreviousNames)
            .Where(name => !currentNames.Contains(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (renamedAway.Count > 0) RemovePlaylists(rootPath, renamedAway);
        var results = new List<IPodPlaylistSyncResult>();
        foreach (var source in playlists) results.Add(Sync(rootPath, source, library));
        return results;
    }

    private static void RemovePlaylists(string rootPath, IReadOnlySet<string> names)
    {
        var ipod = IPod.GetiPodByDrive(rootPath, IPodLoadAction.NoSync);
        ipod.AssertIsWritable();
        var backup = BackupDatabase(rootPath);
        Clickwheel.IPodBackup.EnableBackups = false;
        ipod.AcquireLock();
        try
        {
            var all = new List<IPodPlaylist>();
            foreach (var playlist in ipod.Playlists) all.Add(playlist);
            foreach (var playlist in all)
                if (!playlist.IsMaster && !playlist.IsSmartPlaylist && !playlist.IsPodcastPlaylist && names.Contains(playlist.Name))
                    ipod.Playlists.Remove(playlist, false);
            ipod.SaveChanges();
        }
        catch { try { RestoreDatabase(backup); } catch { } throw; }
        finally { try { ipod.ReleaseLock(); } catch { } }
    }

    public static IPodPlaylistSyncResult Sync(string rootPath, Playlist source, IReadOnlyCollection<Track> library)
    {
        var ipod = IPod.GetiPodByDrive(rootPath, IPodLoadAction.NoSync);
        ipod.AssertIsWritable();
        var backup = BackupDatabase(rootPath);
        Clickwheel.IPodBackup.EnableBackups = false;
        ipod.AcquireLock();
        try
        {
            var playlists = new List<IPodPlaylist>();
            foreach (var item in ipod.Playlists) playlists.Add(item);
            var playlist = playlists.FirstOrDefault(item =>
                !item.IsMaster && item.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
            if (playlist?.IsSmartPlaylist == true)
                throw new InvalidOperationException($"The iPod already has a smart playlist named {source.Name}. Rename one of the playlists before syncing.");
            playlist ??= ipod.Playlists.Add(source.Name);

            foreach (var existing in playlist.Tracks.ToList()) playlist.RemoveTrack(existing);
            var trackList = new List<IPodTrack>();
            foreach (var item in ipod.Tracks) trackList.Add(item);
            var ipodTracks = trackList
                .GroupBy(track => TrackIdentity.Key(track.Title, track.Artist, track.Album, checked((int)track.TrackNumber)), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var ipodTracksByLocalId = trackList.Where(track => TrackIdentity.MarkerId(track.Comment) is not null)
                .GroupBy(track => TrackIdentity.MarkerId(track.Comment)!.Value).ToDictionary(group => group.Key, group => group.First());
            var added = 0;
            var missing = 0;
            foreach (var id in source.TrackIds)
            {
                var local = library.FirstOrDefault(track => track.Id == id);
                IPodTrack? ipodTrack = null;
                if (local is not null) ipodTracksByLocalId.TryGetValue(local.Id, out ipodTrack);
                if (local is null || (ipodTrack is null && !ipodTracks.TryGetValue(TrackIdentity.Key(local.Title, local.Artist, local.Album, local.TrackNumber), out ipodTrack)))
                {
                    missing++;
                    continue;
                }
                playlist.AddTrack(ipodTrack);
                added++;
            }
            ipod.SaveChanges();
            return new IPodPlaylistSyncResult(source.Name, added, missing);
        }
        catch
        {
            try { RestoreDatabase(backup); } catch { }
            throw;
        }
        finally { try { ipod.ReleaseLock(); } catch { } }
    }

    private static string BackupDatabase(string rootPath)
    {
        var source = Path.Combine(rootPath, "iPod_Control", "iTunes", "iTunesDB");
        if (!File.Exists(source)) source = Path.Combine(rootPath, "iPod_Control", "iTunes", "iTunesCDB");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "ipod-backups");
        Directory.CreateDirectory(directory);
        var backup = Path.Combine(directory, $"{Path.GetFileName(source)}-playlist-{DateTime.Now:yyyyMMdd-HHmmssfff}.backup");
        File.Copy(source, backup, true);
        foreach (var old in new DirectoryInfo(directory).GetFiles("*.backup").OrderByDescending(file => file.CreationTimeUtc).Skip(5)) old.Delete();
        return backup + "|" + source;
    }

    private static void RestoreDatabase(string backupInfo)
    {
        var parts = backupInfo.Split('|', 2);
        if (parts.Length == 2 && File.Exists(parts[0])) File.Copy(parts[0], parts[1], true);
    }
}
