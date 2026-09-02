using Clickwheel;
using System.IO;
using IPodPlaylist = Clickwheel.Parsers.iTunesDB.Playlist;

namespace HTunes.App;

internal sealed record IPodPlaylistView(string Name, bool IsSmart, bool IsPodcast, IReadOnlyList<string> TrackKeys, int TrackCount)
{
    public string Display => $"{Name}  ({TrackCount})";
}

internal static class IPodPlaylistReader
{
    public static List<IPodPlaylistView> Read(string rootPath)
    {
        var ipod = IPod.GetiPodByDrive(rootPath, IPodLoadAction.NoSync);
        var result = new List<IPodPlaylistView>();
        foreach (var playlist in ipod.Playlists)
        {
            if (playlist.IsMaster) continue;
            var keys = new List<string>();
            foreach (var track in playlist.Tracks)
                keys.Add(TrackIdentity.Key(track.Title, track.Artist, track.Album, checked((int)track.TrackNumber)));
            result.Add(new IPodPlaylistView(playlist.Name, playlist.IsSmartPlaylist, playlist.IsPodcastPlaylist, keys, keys.Count));
        }
        return result;
    }

    public static void Delete(string rootPath, string name)
    {
        var ipod = IPod.GetiPodByDrive(rootPath, IPodLoadAction.NoSync);
        ipod.AssertIsWritable();
        var backup = BackupDatabase(rootPath);
        Clickwheel.IPodBackup.EnableBackups = false;
        ipod.AcquireLock();
        try
        {
            IPodPlaylist? target = null;
            foreach (var playlist in ipod.Playlists)
                if (!playlist.IsMaster && playlist.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { target = playlist; break; }
            if (target is null) return;
            if (target.IsSmartPlaylist) throw new InvalidOperationException("Smart playlists cannot be deleted from hTunes.");
            ipod.Playlists.Remove(target, false); // false = keep the songs on the iPod, drop only the playlist
            ipod.SaveChanges();
        }
        catch { try { RestoreDatabase(backup); } catch { } throw; }
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
        foreach (var old in new DirectoryInfo(directory).GetFiles("*.backup").OrderByDescending(file => file.CreationTimeUtc).Skip(10)) try { old.Delete(); } catch { }
        return backup + "|" + source;
    }

    private static void RestoreDatabase(string backupInfo)
    {
        var parts = backupInfo.Split('|', 2);
        if (parts.Length == 2 && File.Exists(parts[0])) File.Copy(parts[0], parts[1], true);
    }
}
