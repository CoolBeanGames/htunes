using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HTunes.App;

internal sealed record YtCompletedFile(string Path, string Identity, string Title);
internal sealed record YtDownloadUpdate(string? Title = null, long? Index = null, long? Count = null, double? Percent = null, bool Processing = false, string? Log = null, string? ArtworkUrl = null);
internal sealed record YtLinkResult(int ExitCode, bool Aborted, IReadOnlyList<YtCompletedFile> Files);

internal static class YtDlpDownloadService
{
    internal static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".wav", ".opus", ".ogg", ".oga", ".alac", ".wma", ".aiff", ".aif" };

    public static IReadOnlyList<string> ParseLinks(string text)
    {
        var links = new List<string>();
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                string.IsNullOrEmpty(uri.Host) || line.Any(char.IsWhiteSpace))
                throw new ArgumentException($"Line {i + 1}: enter one complete HTTP or HTTPS link, without extra text.");
            links.Add(line);
        }
        if (links.Count == 0) throw new ArgumentException("Paste at least one link, one per line.");
        return links;
    }

    public static string Identity(string extractor, string id)
    {
        if (string.IsNullOrWhiteSpace(extractor) || string.IsNullOrWhiteSpace(id) || extractor == "NA" || id == "NA" ||
            extractor.Any(char.IsWhiteSpace) || id.Any(char.IsWhiteSpace)) return "";
        return extractor.ToLowerInvariant() + " " + id;
    }

    public static async Task<IReadOnlyList<string>> ExpandYouTubeMusicArtistAsync(string executable, string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase)) return [url];
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts[0] is not ("channel" or "browse") || !parts[1].StartsWith("UC", StringComparison.Ordinal)) return [url];
        var releasesUrl = $"https://www.youtube.com/channel/{parts[1]}/releases";
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "--no-plugin-dirs", "--flat-playlist", "--dump-single-json", "--playlist-end", "500", "--", releasesUrl }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("yt-dlp could not inspect the artist page.");
        var output = process.StandardOutput.ReadToEndAsync(token); var errors = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token); var json = await output; _ = await errors;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return [url];
        try
        {
            using var document = JsonDocument.Parse(json);
            var links = new List<string>(); Collect(document.RootElement, links);
            return links.Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } albums ? albums : [url];
        }
        catch (JsonException) { return [url]; }

        static void Collect(JsonElement item, ICollection<string> links)
        {
            if (item.ValueKind != JsonValueKind.Object) return;
            if (item.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                foreach (var entry in entries.EnumerateArray()) Collect(entry, links);
            var value = item.TryGetProperty("webpage_url", out var webpage) ? webpage.ToString() : item.TryGetProperty("url", out var direct) ? direct.ToString() : "";
            if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                (parsed.AbsolutePath.Equals("/playlist", StringComparison.OrdinalIgnoreCase) || parsed.Query.Contains("list=", StringComparison.OrdinalIgnoreCase))) links.Add(value);
        }
    }

    public static IEnumerable<string> LibraryArchive(IEnumerable<Track> tracks)
    {
        foreach (var track in tracks.Where(track => File.Exists(track.FilePath)))
        {
            if (IsArchiveIdentity(track.DownloadIdentity)) { yield return track.DownloadIdentity; continue; }
            // Recognize the exact YouTube ID in files imported manually from the same naming convention.
            var match = Regex.Match(Path.GetFileNameWithoutExtension(track.OriginalImportPath.Length > 0 ? track.OriginalImportPath : track.FilePath), @"\[([A-Za-z0-9_-]{11})\](?: \(\d+\))?$");
            if (match.Success) yield return "youtube " + match.Groups[1].Value;
        }
    }

    internal static bool IsArchiveIdentity(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Split(' ') is [var extractor, var id] && Identity(extractor, id) == value;

    internal static ProcessStartInfo BuildStartInfo(string executable, string ffmpeg, string url, AppPreferences settings, string archive, string marker, string? temporaryDirectory = null)
    {
        _ = ParseLinks(url);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = settings.DownloadDirectory
        };
        foreach (var arg in YtDlpSettings.BuildArguments(settings)) start.ArgumentList.Add(arg);
        if (temporaryDirectory is not null) { start.ArgumentList.Add("--paths"); start.ArgumentList.Add("temp:" + temporaryDirectory); }
        const string details = "\"title\":%(title)j,\"index\":%(playlist_index|1)j,\"count\":%(playlist_count,n_entries|null)j,\"playlist\":%(playlist_id,playlist|null)j,\"artwork\":%(thumbnail|null)j";
        foreach (var arg in new[] {
            "--no-plugin-dirs", "--no-exec", "--no-simulate", "--no-quiet", "--newline", "--progress", "--color", "no_color", "--encoding", "utf-8", "--output-na-placeholder", "null",
            "--format", "bestaudio/best", "--ffmpeg-location", ffmpeg, "--js-runtimes", "node", "--download-archive", archive,
            "--no-abort-on-error", "--no-break-on-existing", "--progress-delta", "0.3", "--socket-timeout", "30", "--retries", "3", "--fragment-retries", "3",
            "--print", "video:" + marker + "{\"type\":\"track\"," + details + "}",
            "--print", "playlist:" + marker + "{\"type\":\"playlist\",\"count\":%(n_entries,playlist_count|null)j}",
            "--print", "after_move:" + marker + "{\"type\":\"complete\",\"path\":%(filepath)j,\"extractor\":%(extractor_key)j,\"id\":%(id)j," + details + "}",
            "--progress-template", "download:" + marker + "{\"type\":\"progress\",\"title\":%(info.title)j,\"index\":%(info.playlist_index|1)j,\"count\":%(info.playlist_count,info.n_entries|null)j,\"playlist\":%(info.playlist_id,info.playlist|null)j,\"bytes\":%(progress.downloaded_bytes|0)j,\"total\":%(progress.total_bytes,progress.total_bytes_estimate|0)j}",
            "--progress-template", "postprocess:" + marker + "{\"type\":\"processing\",\"title\":%(info.title)j}",
            "--", url }) start.ArgumentList.Add(arg);
        return start;
    }

    public static async Task<YtLinkResult> DownloadLinkAsync(string executable, string ffmpeg, string url, AppPreferences settings,
        string archive, IProgress<YtDownloadUpdate> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(settings.DownloadDirectory);
        var marker = "HTUNES_" + Guid.NewGuid().ToString("N") + ":";
        var completed = new ConcurrentDictionary<string, YtCompletedFile>(StringComparer.OrdinalIgnoreCase);
        void Receive(string line)
        {
            if (TryParseLine(line, marker, settings.DownloadDirectory, out var update, out var file))
            {
                if (file is not null) completed[file.Path] = file;
                if (update is not null) progress.Report(update);
            }
            else progress.Report(new(Log: line));
        }
        var stagingRoot = Path.GetFullPath(Path.Combine(settings.DownloadDirectory, ".htunes-work"));
        Directory.CreateDirectory(stagingRoot);
        if ((File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0) throw new IOException("The download staging folder cannot be a link or junction.");
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // A fresh staging area prevents a killed FFmpeg's half-written MP3 being mistaken for an existing finished file on retry.
            var start = BuildStartInfo(executable, ffmpeg, url, settings, archive, marker, staging);
            DebugLog.Write("yt-dlp", $"Starting link; format={settings.YtAudioFormat}; quality={settings.YtAudioQuality}");
            var result = await RunProcessAsync(start, Receive, cancellationToken).ConfigureAwait(false);
            DebugLog.Write("yt-dlp", $"Exited code={result.ExitCode}; aborted={result.Aborted}; completed={completed.Count}");
            return new(result.ExitCode, result.Aborted, completed.Values.ToList());
        }
        finally
        {
            try
            {
                var resolved = Path.GetFullPath(staging);
                if (resolved.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParseExact(Path.GetFileName(resolved), "N", out _) && (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(resolved, recursive: true);
            }
            catch (Exception ex) { DebugLog.Write("yt-dlp", "Staging cleanup failed; unfinished files remain separate from library audio", ex); }
        }
    }

    internal static async Task<(int ExitCode, bool Aborted)> RunProcessAsync(ProcessStartInfo start, Action<string> receive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("yt-dlp could not be started.");
        if (start.RedirectStandardInput) process.StandardInput.Close();
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { DebugLog.Write("yt-dlp", "Could not stop process tree", ex); }
        });
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) receive(line);
        }
        // Drain both pipes concurrently to prevent large console output blocking the child process.
        var stdout = Drain(process.StandardOutput);
        var stderr = Drain(process.StandardError);
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return (process.ExitCode, token.IsCancellationRequested);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    internal static bool TryParseLine(string line, string marker, string downloadDirectory, out YtDownloadUpdate? update, out YtCompletedFile? file)
    {
        update = null; file = null;
        if (!line.StartsWith(marker, StringComparison.Ordinal)) return false;
        try
        {
            using var json = JsonDocument.Parse(line[marker.Length..]);
            var root = json.RootElement;
            string Read(string name) => root.TryGetProperty(name, out var value) ? value.ToString() : "";
            long? Number(string name) => long.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
            var kind = Read("type");
            var title = Read("title");
            var index = Number("index");
            var count = Number("count");
            var artwork = Read("artwork");
            // A standalone video has no playlist count. Unknown playlist totals remain unknown.
            if (count is null && index == 1 && Read("playlist") is "" or "NA" or "null") count = 1;
            var total = Number("total");
            double? percent = total > 0 ? Math.Clamp((Number("bytes") ?? 0) * 100d / total.Value, 0, 100) : null;
            if (kind == "complete")
            {
                var path = Path.GetFullPath(Read("path"));
                if (!IsCompletedAudioPath(path, downloadDirectory))
                {
                    update = new(Log: "[hTunes] Ignored an invalid or unfinished output path."); return true;
                }
                file = new(path, Identity(Read("extractor"), Read("id")), title);
                update = new(title, index, count, 100, Log: $"[hTunes] Audio ready: {Path.GetFileName(path)}", ArtworkUrl: artwork);
            }
            else if (kind == "playlist") update = new(Count: count);
            else if (kind is "track" or "progress" or "processing")
                update = new(title, index, count, percent, kind == "processing", kind == "track" ? $"[hTunes] Track: {title}" : null, artwork);
            else return false;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { DebugLog.Write("yt-dlp", "Ignored invalid progress message", ex); return false; }
    }

    internal static bool IsCompletedAudioPath(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) || !AudioExtensions.Contains(Path.GetExtension(path)) || !File.Exists(path) || new FileInfo(path).Length == 0) return false;
        // Do not follow a link out of the configured download area when importing/moving results.
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null; current = current is FileInfo info ? info.Directory : ((DirectoryInfo)current).Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase)) break;
        }
        return true;
    }
}
