using Clickwheel;
using Clickwheel.Exceptions;
using Clickwheel.Parsers.iTunesDB;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using IPodPlaylist = Clickwheel.Parsers.iTunesDB.Playlist;
using IPodTrack = Clickwheel.Parsers.iTunesDB.Track;

namespace HTunes.App;

internal sealed record SyncProgress(int Completed, int Total, string Message);
internal sealed record SyncResult(int Added, int Replaced, int AlreadyPresent, int Unsupported, int Missing, int NoSpace)
{
    // Set when the user stopped the sync: the songs copied so far were kept and committed.
    public bool Cancelled { get; init; }

    public string Summary => (Cancelled ? "Sync stopped. " : "") +
        $"Added {Added} song{(Added == 1 ? "" : "s")}, replaced {Replaced}. " +
        $"{AlreadyPresent} already current on iPod, {Unsupported} unsupported, {Missing} missing, {NoSpace} skipped for space.";
}

internal static class IPodSyncService
{
    private sealed record SyncCandidate(Track Source, IPodTrack? Existing, string Fingerprint);
    private sealed record ReplacedMedia(string OriginalPath, string BackupPath);
    private sealed record PlaylistMembership(IPodPlaylist Playlist, int Position);
    private static readonly string[] SupportedExtensions = [".mp3", ".m4a", ".aac", ".wav", ".m4b", ".aa"];
    private const long SpaceReserve = 20L * 1024 * 1024;

    public static SyncResult Sync(string rootPath, IReadOnlyCollection<Track> requested, IReadOnlyCollection<Track> library, bool randomFill, TranscodePreset preset, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
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
        var deviceId = IPodPlayCountService.DeviceId(ipod, rootPath);

        var ipodTrackList = new List<IPodTrack>();
        foreach (var track in ipod.Tracks) ipodTrackList.Add(track);
        var existingByKeyGroups = ipodTrackList
            .GroupBy(t => TrackIdentity.Key(t.Title, t.Artist, t.Album, checked((int)t.TrackNumber)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var existingByKey = existingByKeyGroups.ToDictionary(item => item.Key, item => item.Value[0], StringComparer.OrdinalIgnoreCase);
        var existingByMarker = ipodTrackList.Where(track => TrackIdentity.MarkerId(track.Comment) is not null)
            .GroupBy(track => TrackIdentity.MarkerId(track.Comment)!.Value).ToDictionary(group => group.Key, group => group.First());
        var candidates = new List<SyncCandidate>();
        var current = new List<(Track Source, string Fingerprint)>();
        var claimed = new HashSet<int>();
        var databaseDirty = false;
        var alreadyPresent = 0;
        foreach (var source in eligible)
        {
            if (cancellationToken.IsCancellationRequested)
                return new SyncResult(0, 0, alreadyPresent, unsupported, missing, 0) { Cancelled = true };
            existingByMarker.TryGetValue(source.Id, out var existing);
            var markerMatched = existing is not null;
            var desiredKey = TrackIdentity.Key(source.Title, source.Artist, source.Album, source.TrackNumber);
            if (existing is null) existingByKey.TryGetValue(desiredKey, out existing);
            if (existing is null)
                foreach (var previous in source.PreviousMetadataIdentities ?? [])
                    if (existingByKey.TryGetValue(previous, out existing)) break;
            if (existing is not null && existingByKeyGroups.TryGetValue(desiredKey, out var identityMatches) &&
                FindDifferentReference(identityMatches, existing) is { } duplicate)
            {
                // iTunesDB files can already contain duplicate identities (for example after tags
                // were edited by another application). The old single-value lookup only inspected
                // the first item in the group, so a second copy could remain hidden until Add threw.
                // Adopt the destination copy and leave the unrelated/old record alone.
                duplicate.Comment = TrackIdentity.Marker(source.Id);
                if (markerMatched) existing.Comment = "";
                claimed.Add(duplicate.Id);
                databaseDirty = true;
                var duplicateFingerprint = DesiredFingerprint(source, preset);
                alreadyPresent++;
                current.Add((source, duplicateFingerprint));
                DebugLog.Write("Music sync", $"Adopted duplicate iPod identity for track={source.Id}");
                continue;
            }
            if (existing is not null && !claimed.Add(existing.Id)) existing = null;
            if (existing is not null && TrackIdentity.MarkerId(existing.Comment) != source.Id)
            {
                existing.Comment = TrackIdentity.Marker(source.Id); databaseDirty = true;
            }
            var fingerprint = DesiredFingerprint(source, preset);
            var knownFingerprint = source.SyncedIPodFingerprints?.GetValueOrDefault(deviceId);
            var fingerprintCurrent = FingerprintMatchesLastSync(source, deviceId, fingerprint);
            var retaggedLegacy = existing is not null && !markerMatched && knownFingerprint is null && source.MetadataManagedByLibrary;
            bool needsSync;
            if (existing is null)
                needsSync = true;
            else if (fingerprintCurrent)
                // The last sync to THIS device already carried this exact track/file/artwork state.
                // Trust the fingerprint and leave the iPod copy alone; only recopy if its media file
                // has since disappeared from the device. Do NOT fall through to the NeedsReplacement
                // heuristic here - its metadata/bitrate probes throw false positives that were causing
                // unchanged tracks to be replaced on every sync.
                needsSync = !IPodMediaPresent(rootPath, existing);
            else
                needsSync = retaggedLegacy || knownFingerprint is not null || NeedsReplacement(rootPath, existing, source, preset);
            if (needsSync)
                candidates.Add(new SyncCandidate(source, existing, fingerprint));
            else { alreadyPresent++; current.Add((source, fingerprint)); }
        }
        if (randomFill) Shuffle(candidates);

        var remaining = Math.Max(0, new DriveInfo(rootPath).AvailableFreeSpace - SpaceReserve);
        var noSpace = 0;
        if (candidates.Count == 0 && !databaseDirty)
        {
            foreach (var item in current) RememberFingerprint(item.Source, deviceId, item.Fingerprint);
            return new SyncResult(0, 0, alreadyPresent, unsupported, missing, noSpace);
        }

        var backup = BackupDatabase(rootPath);
        var added = new List<IPodTrack>();
        var replacedMedia = new List<ReplacedMedia>();
        var replaced = 0;
        var synced = new List<(Track Source, string Fingerprint)>();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"hTunes-transcode-{Guid.NewGuid():N}");
        Clickwheel.IPodBackup.EnableBackups = false;
        ipod.AcquireLock();
        var cancelled = false;
        try
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                // Stop cleanly after the current file: the songs already copied are committed below.
                if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
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
                            synced.Add((source, candidate.Fingerprint));
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

                    IPodTrack replacement;
                    try
                    {
                        replacement = ipod.Tracks.Add(CreateNewTrack(source, library, syncPath));
                    }
                    catch (TrackAlreadyExistsException collision)
                    {
                        // Be defensive against malformed databases whose duplicate was not visible
                        // during candidate discovery. The old copy is already backed up and removed;
                        // adopt Clickwheel's surviving copy instead of rolling back the whole sync.
                        replacement = collision.ExistingTrack;
                        replacement.Comment = TrackIdentity.Marker(source.Id);
                        foreach (var membership in memberships)
                            if (!membership.Playlist.ContainsTrack(replacement))
                                membership.Playlist.AddTrack(replacement, Math.Min(membership.Position, membership.Playlist.TrackCount));
                        databaseDirty = true;
                        alreadyPresent++;
                        synced.Add((source, candidate.Fingerprint));
                        remaining += reclaimed;
                        DebugLog.Write("Music sync", $"Adopted late duplicate iPod identity for track={source.Id}");
                        continue;
                    }
                    PreserveIPodState(existing, replacement);
                    foreach (var membership in memberships)
                        membership.Playlist.AddTrack(replacement, Math.Min(membership.Position, membership.Playlist.TrackCount));
                    added.Add(replacement);
                    synced.Add((source, candidate.Fingerprint));
                    replaced++;
                    remaining = remaining + reclaimed - size;
                }
                finally
                {
                    if (temporarySyncPath is not null) FFmpegTranscoder.TryDelete(temporarySyncPath);
                }
            }
            if (added.Count > 0 || databaseDirty)
            {
                progress?.Report(new SyncProgress(candidates.Count, candidates.Count, "Updating the iPod library"));
                ipod.SaveChanges();
            }
            foreach (var item in current.Concat(synced)) RememberFingerprint(item.Source, deviceId, item.Fingerprint);
            return new SyncResult(added.Count - replaced, replaced, alreadyPresent, unsupported, missing, noSpace) { Cancelled = cancelled };
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

    // True when the track's current tag/file/artwork state is byte-for-byte the state that was
    // last committed to this specific device. When this holds, the iPod copy is up to date and
    // must not be re-copied - the fingerprint is authoritative, not the NeedsReplacement heuristic.
    internal static bool FingerprintMatchesLastSync(Track source, string deviceId, string fingerprint) =>
        source.SyncedIPodFingerprints?.GetValueOrDefault(deviceId) is { } known && known == fingerprint;

    // Identity groups can contain more than one physical iPod record. Do not use First() for
    // collision detection: the first record may be the exact record about to be replaced.
    internal static T? FindDifferentReference<T>(IEnumerable<T> matches, T existing) where T : class =>
        matches.FirstOrDefault(item => !ReferenceEquals(item, existing));

    private static bool IPodMediaPresent(string rootPath, IPodTrack existing) =>
        File.Exists(ResolveIPodPath(rootPath, existing.FilePath));

    private static bool NeedsReplacement(string rootPath, IPodTrack existing, Track source, TranscodePreset preset)
    {
        var desiredFormat = preset.IsOriginal ? AudioFormat(source.FilePath) : PresetFormat(preset);
        var existingPath = ResolveIPodPath(rootPath, existing.FilePath);
        if (!File.Exists(existingPath)) return true;
        var existingFormat = AudioFormat(existingPath, existing.FilePath);
        if (!FormatsMatch(desiredFormat, existingFormat)) return true;

        if (!Same(existing.Title, source.Title) || !Same(existing.Artist, source.Artist) || !Same(existing.AlbumArtist, string.IsNullOrWhiteSpace(source.AlbumArtist) ? source.Artist : source.AlbumArtist) || !Same(existing.Album, source.Album) ||
            !Same(existing.Genre, source.Genre) || existing.TrackNumber != (uint)Math.Max(0, source.TrackNumber) ||
            existing.DiscNumber != (uint)Math.Max(1, source.DiscNumber) || existing.Year != (uint)Math.Max(0, source.Year)) return true;

        var desiredBitrate = preset.BitrateKbps ?? (preset.IsOriginal ? ReadBitrate(source.FilePath) : null);
        return desiredBitrate is > 0 && existing.Bitrate > 0 && Math.Abs((long)existing.Bitrate - desiredBitrate.Value) > 8;
    }

    internal static string DesiredFingerprint(Track source, TranscodePreset preset)
    {
        var file = new FileInfo(source.FilePath);
        var artwork = source.ArtworkPath is not null && File.Exists(source.ArtworkPath) ? new FileInfo(source.ArtworkPath) : null;
        var value = string.Join('\u001f', source.Id, source.Title, source.Artist, source.AlbumArtist, source.Album, source.Genre, source.TrackNumber, source.DiscNumber, source.Year,
            preset.Id, file.Exists ? file.Length : -1, file.Exists ? file.LastWriteTimeUtc.Ticks : 0,
            artwork?.FullName ?? "", artwork?.Length ?? -1, artwork?.LastWriteTimeUtc.Ticks ?? 0);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static void RememberFingerprint(Track source, string deviceId, string fingerprint)
    {
        source.SyncedIPodFingerprints ??= [];
        source.SyncedIPodFingerprints[deviceId] = fingerprint;
        source.IsNew = false;
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
            AlbumArtist = string.IsNullOrWhiteSpace(source.AlbumArtist) ? source.Artist : source.AlbumArtist,
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
            ArtworkFile = source.ArtworkPath is not null && File.Exists(source.ArtworkPath) ? source.ArtworkPath : null,
            Comments = TrackIdentity.Marker(source.Id)
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
