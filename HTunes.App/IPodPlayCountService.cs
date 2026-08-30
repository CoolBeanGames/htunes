using Clickwheel;
using System.IO;

namespace HTunes.App;

internal sealed record PlayCountUpdate(Guid TrackId, int Count, string DeviceId);

internal static class TrackIdentity
{
    public static string Key(string title, string artist, string album, int trackNumber) =>
        $"{title.Trim()}\u001f{artist.Trim()}\u001f{album.Trim()}\u001f{Math.Max(0, trackNumber)}";
}

internal static class IPodPlayCountService
{
    public static IReadOnlyList<PlayCountUpdate> Reconcile(string rootPath, IReadOnlyCollection<Track> library)
    {
        var dbPath = DatabasePath(rootPath);
        var playCountsPath = Path.Combine(rootPath, "iPod_Control", "iTunes", "Play Counts");
        var backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "ipod-backups");
        Directory.CreateDirectory(backupDirectory);
        var token = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
        var dbBackup = Path.Combine(backupDirectory, $"{Path.GetFileName(dbPath)}-plays-{token}.backup");
        var countsBackup = Path.Combine(backupDirectory, $"PlayCounts-{token}.backup");
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
            foreach (var ipodTrack in ipod.Tracks)
            {
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
            return updates;
        }
        catch
        {
            try { File.Copy(dbBackup, dbPath, true); } catch { }
            try { if (File.Exists(countsBackup)) File.Copy(countsBackup, playCountsPath, true); } catch { }
            throw;
        }
        finally
        {
            try { ipod?.ReleaseLock(); } catch { }
        }
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
