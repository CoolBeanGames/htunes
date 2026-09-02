using Clickwheel;
using System.IO;
using IPodTrack = Clickwheel.Parsers.iTunesDB.Track;

namespace HTunes.App;

// Removes non-podcast tracks from a connected iPod: drops the iTunesDB entries and
// deletes their media files, with a database backup + media restore on failure.
internal static class IPodTrackRemovalService
{
    private sealed record RemovedMedia(string OriginalPath, string BackupPath);

    public static int Remove(string rootPath, IReadOnlyCollection<string> deviceFilePaths)
    {
        var targets = deviceFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return 0;

        var ipod = IPod.GetiPodByDrive(rootPath, IPodLoadAction.NoSync);
        ipod.AssertIsWritable();
        var backup = BackupDatabase(rootPath);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"hTunes-remove-{Guid.NewGuid():N}");
        var removedMedia = new List<RemovedMedia>();
        Clickwheel.IPodBackup.EnableBackups = false;
        ipod.AcquireLock();
        try
        {
            var toRemove = new List<IPodTrack>();
            foreach (var track in ipod.Tracks)
                if (targets.Contains(Path.GetFullPath(ResolveIPodPath(rootPath, track.FilePath)))) toRemove.Add(track);

            var removed = 0;
            foreach (var track in toRemove)
            {
                BackupMedia(rootPath, track, temporaryDirectory, removedMedia);
                if (ipod.Tracks.Remove(track)) removed++;
            }
            ipod.SaveChanges();
            foreach (var media in removedMedia)
                try { if (File.Exists(media.OriginalPath)) File.Delete(media.OriginalPath); } catch { }
            return removed;
        }
        catch
        {
            foreach (var media in removedMedia)
                try { Directory.CreateDirectory(Path.GetDirectoryName(media.OriginalPath)!); File.Copy(media.BackupPath, media.OriginalPath, true); } catch { }
            try { RestoreDatabase(backup); } catch { }
            throw;
        }
        finally
        {
            try { ipod.ReleaseLock(); } catch { }
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true); } catch { }
        }
    }

    private static void BackupMedia(string rootPath, IPodTrack track, string temporaryDirectory, ICollection<RemovedMedia> backups)
    {
        var original = ResolveIPodPath(rootPath, track.FilePath);
        if (!File.Exists(original)) return;
        Directory.CreateDirectory(temporaryDirectory);
        var backup = Path.Combine(temporaryDirectory, $"removed-{Guid.NewGuid():N}{Path.GetExtension(original)}");
        File.Copy(original, backup, true);
        backups.Add(new RemovedMedia(original, backup));
    }

    private static string ResolveIPodPath(string rootPath, string storedPath)
    {
        if (Path.IsPathFullyQualified(storedPath)) return storedPath;
        return Path.Combine(rootPath, storedPath.Replace(':', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
    }

    private static string BackupDatabase(string rootPath)
    {
        var source = Path.Combine(rootPath, "iPod_Control", "iTunes", "iTunesDB");
        if (!File.Exists(source)) source = Path.Combine(rootPath, "iPod_Control", "iTunes", "iTunesCDB");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "ipod-backups");
        Directory.CreateDirectory(directory);
        var backup = Path.Combine(directory, $"{Path.GetFileName(source)}-remove-{DateTime.Now:yyyyMMdd-HHmmssfff}.backup");
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
