using Clickwheel;
using Clickwheel.Exceptions;
using Clickwheel.Parsers.iTunesDB;
using System.IO;
using IPodPlaylist = Clickwheel.Parsers.iTunesDB.Playlist;
using IPodTrack = Clickwheel.Parsers.iTunesDB.Track;

namespace HTunes.App;

internal sealed record SyncProgress(int Completed, int Total, string Message);
internal sealed record SyncResult(int Added, int Replaced, int AlreadyPresent, int Unsupported, int Missing, int NoSpace)
{
    public string Summary => $"Added {Added} song{(Added == 1 ? "" : "s")}, replaced {Replaced}. " +
        $"{AlreadyPresent} already current on iPod, {Unsupported} unsupported, {Missing} missing, {NoSpace} skipped for space.";
}

internal static class IPodSyncService
{
    private sealed record SyncCandidate(Track Source, IPodTrack? Existing);
    private sealed record ReplacedMedia(string OriginalPath, string BackupPath);
    private sealed record PlaylistMembership(IPodPlaylist Playlist, int Position);
    private static readonly string[] SupportedExtensions = [".mp3", ".m4a", ".aac", ".wav", ".m4b", ".aa"];
    private const long SpaceReserve = 20L * 1024 * 1024;

    public static SyncResult Sync(string rootPath, IReadOnlyCollection<Track> requested, IReadOnlyCollection<Track> library, bool randomFill, TranscodePreset preset, IProgress<SyncProgress>? progress = null)
    {
        var unique = requested.DistinctBy(t => t.Id).ToList();
        var missing = unique.Count(t => !File.Exists(t.FilePath));
        var eligible = unique.Where(t => File.Exists(t.FilePath)).ToList();
        var unsupported = preset.IsOriginal
            ? eligible.RemoveAll(t => !SupportedExtensions.Contains(Path.GetExtension(t.FilePath), StringComparer.OrdinalIgnoreCase) || new FileInfo(t.FilePath).Length > uint.MaxValue)
            : 0;
        var ffmpeg = preset.IsOriginal ? null : FFmpegTranscoder.FindExecutable();
        var ipod = IPod.GetiPodByDrive(rootPath, IPodLoadAction.NoSync);
        ipod.AssertIsWritable();

        var ipodTrackList = new List<IPodTrack>();
        foreach (var track in ipod.Tracks) ipodTrackList.Add(track);
        var existingByKey = ipodTrackList
            .GroupBy(t => TrackIdentity.Key(t.Title, t.Artist, t.Album, checked((int)t.TrackNumber)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var candidates = new List<SyncCandidate>();
        var alreadyPresent = 0;
        foreach (var source in eligible)
        {
            existingByKey.TryGetValue(TrackIdentity.Key(source.Title, source.Artist, source.Album, source.TrackNumber), out var existing);
            if (existing is null || NeedsReplacement(rootPath, existing, source, preset)) candidates.Add(new SyncCandidate(source, existing));
            else alreadyPresent++;
        }
        if (randomFill) Shuffle(candidates);

        var remaining = Math.Max(0, new DriveInfo(rootPath).AvailableFreeSpace - SpaceReserve);
        var noSpace = 0;
        if (candidates.Count == 0) return new SyncResult(0, 0, alreadyPresent, unsupported, missing, noSpace);

        var backup = BackupDatabase(rootPath);
        var added = new List<IPodTrack>();
        var replacedMedia = new List<ReplacedMedia>();
        var replaced = 0;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"hTunes-transcode-{Guid.NewGuid():N}");
        Clickwheel.IPodBackup.EnableBackups = false;
        ipod.AcquireLock();
        try
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var source = candidate.Source;
                var syncPath = source.FilePath;
                string? temporarySyncPath = null;
                try
                {
                    if (!preset.IsOriginal)
                    {
                        progress?.Report(new SyncProgress(index, candidates.Count, $"Transcoding {source.Title} to {preset.DisplayName}"));
                        try
                        {
                            temporarySyncPath = FFmpegTranscoder.Transcode(ffmpeg!, source.FilePath, preset, temporaryDirectory);
                            syncPath = temporarySyncPath;
                        }
                        catch (InvalidDataException) { unsupported++; continue; }
                    }
                    var size = new FileInfo(syncPath).Length;
                    if (size > uint.MaxValue) { unsupported++; continue; }
                    var reclaimed = candidate.Existing is null ? 0 : ExistingFileSize(rootPath, candidate.Existing);
                    if (size > remaining + reclaimed) { noSpace++; continue; }

                    progress?.Report(new SyncProgress(index, candidates.Count, candidate.Existing is null ? $"Copying {source.Title}" : $"Replacing {source.Title}"));
                    if (candidate.Existing is null)
                    {
                        try
                        {
                            added.Add(ipod.Tracks.Add(CreateNewTrack(source, library, syncPath)));
                            remaining -= size;
                        }
                        catch (TrackAlreadyExistsException) { alreadyPresent++; }
                        continue;
                    }

                    var existing = candidate.Existing;
                    var memberships = CapturePlaylistMemberships(ipod, existing);
                    BackupExistingMedia(rootPath, existing, temporaryDirectory, replacedMedia);
                    foreach (var membership in memberships)
                        if (membership.Playlist.ContainsTrack(existing)) membership.Playlist.RemoveTrack(existing);
                    if (!ipod.Tracks.Remove(existing)) throw new InvalidOperationException($"The existing iPod copy of {source.Title} could not be replaced.");

                    var replacement = ipod.Tracks.Add(CreateNewTrack(source, library, syncPath));
                    PreserveIPodState(existing, replacement);
                    foreach (var membership in memberships)
                        membership.Playlist.AddTrack(replacement, Math.Min(membership.Position, membership.Playlist.TrackCount));
                    added.Add(replacement);
                    replaced++;
                    remaining = remaining + reclaimed - size;
                }
                finally
                {
                    if (temporarySyncPath is not null) FFmpegTranscoder.TryDelete(temporarySyncPath);
                }
            }
            if (added.Count == 0) return new SyncResult(0, 0, alreadyPresent, unsupported, missing, noSpace);
            progress?.Report(new SyncProgress(candidates.Count, candidates.Count, "Updating the iPod library"));
            ipod.SaveChanges();
            return new SyncResult(added.Count - replaced, replaced, alreadyPresent, unsupported, missing, noSpace);
        }
        catch
        {
            foreach (var track in added)
            {
                try
                {
                    var copiedPath = ResolveIPodPath(rootPath, track.FilePath);
                    if (File.Exists(copiedPath)) File.Delete(copiedPath);
                }
                catch { }
            }
            foreach (var media in replacedMedia)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(media.OriginalPath)!);
                    File.Copy(media.BackupPath, media.OriginalPath, true);
                }
                catch { }
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

    private static bool NeedsReplacement(string rootPath, IPodTrack existing, Track source, TranscodePreset preset)
    {
        var desiredFormat = preset.IsOriginal ? AudioFormat(source.FilePath) : PresetFormat(preset);
        var existingPath = ResolveIPodPath(rootPath, existing.FilePath);
        var existingFormat = AudioFormat(existingPath, existing.FilePath);
        if (!FormatsMatch(desiredFormat, existingFormat)) return true;

        var desiredBitrate = preset.BitrateKbps ?? (preset.IsOriginal ? ReadBitrate(source.FilePath) : null);
        return desiredBitrate is > 0 && existing.Bitrate > 0 && Math.Abs((long)existing.Bitrate - desiredBitrate.Value) > 8;
    }

    private static string PresetFormat(TranscodePreset preset) => preset.Codec switch
    {
        "libmp3lame" => "mp3",
        "aac" => "aac",
        "alac" => "alac",
        _ => preset.Extension.TrimStart('.').ToLowerInvariant()
    };

    private static string AudioFormat(string physicalPath, string? fallbackPath = null)
    {
        var extension = Path.GetExtension(File.Exists(physicalPath) ? physicalPath : fallbackPath ?? physicalPath).ToLowerInvariant();
        if (extension is ".m4a" or ".m4b" or ".mp4")
        {
            try
            {
                using var media = TagLib.File.Create(physicalPath);
                var description = string.Join(" ", media.Properties.Codecs.Select(codec => codec.Description));
                if (description.Contains("Apple Lossless", StringComparison.OrdinalIgnoreCase) || description.Contains("ALAC", StringComparison.OrdinalIgnoreCase)) return "alac";
                if (description.Contains("AAC", StringComparison.OrdinalIgnoreCase)) return "aac";
            }
            catch { }
            return "m4a";
        }
        return extension switch { ".aac" => "aac", ".mp3" => "mp3", _ => extension.TrimStart('.') };
    }

    private static bool FormatsMatch(string desired, string existing) =>
        desired.Equals(existing, StringComparison.OrdinalIgnoreCase) ||
        (existing == "m4a" && desired is "aac" or "alac");

    private static int? ReadBitrate(string path)
    {
        try { using var media = TagLib.File.Create(path); return media.Properties.AudioBitrate; }
        catch { return null; }
    }

    private static long ExistingFileSize(string rootPath, IPodTrack track)
    {
        var path = ResolveIPodPath(rootPath, track.FilePath);
        return File.Exists(path) ? new FileInfo(path).Length : track.FileSize.ByteCount;
    }

    private static List<PlaylistMembership> CapturePlaylistMemberships(IPod ipod, IPodTrack track)
    {
        var result = new List<PlaylistMembership>();
        foreach (var playlist in ipod.Playlists)
        {
            if (playlist.IsMaster) continue;
            var tracks = playlist.Tracks.ToList();
            var position = tracks.FindIndex(item => item.Id == track.Id);
            if (position >= 0) result.Add(new PlaylistMembership(playlist, position));
        }
        return result;
    }

    private static void PreserveIPodState(IPodTrack source, IPodTrack replacement)
    {
        replacement.PlayCount = source.PlayCount;
        replacement.DateLastPlayed = source.DateLastPlayed;
        replacement.Rating = source.Rating;
        replacement.DateAdded = source.DateAdded;
    }

    private static void BackupExistingMedia(string rootPath, IPodTrack track, string temporaryDirectory, ICollection<ReplacedMedia> backups)
    {
        var originalPath = ResolveIPodPath(rootPath, track.FilePath);
        if (!File.Exists(originalPath)) return;
        Directory.CreateDirectory(temporaryDirectory);
        var backupPath = Path.Combine(temporaryDirectory, $"replaced-{Guid.NewGuid():N}{Path.GetExtension(originalPath)}");
        File.Copy(originalPath, backupPath, true);
        backups.Add(new ReplacedMedia(originalPath, backupPath));
    }

    private static string ResolveIPodPath(string rootPath, string storedPath)
    {
        if (Path.IsPathFullyQualified(storedPath)) return storedPath;
        var relative = storedPath.Replace(':', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(rootPath, relative);
    }

    private static NewTrack CreateNewTrack(Track source, IReadOnlyCollection<Track> library, string syncPath)
    {
        uint length = 0, bitrate = 0;
        try
        {
            using var media = TagLib.File.Create(syncPath);
            length = (uint)Math.Clamp(media.Properties.Duration.TotalMilliseconds, 0, uint.MaxValue);
            bitrate = (uint)Math.Max(0, media.Properties.AudioBitrate);
        }
        catch { }
        var albumTracks = library.Where(t => Same(t.Artist, source.Artist) && Same(t.Album, source.Album)).ToList();
        return new NewTrack
        {
            FilePath = syncPath,
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
