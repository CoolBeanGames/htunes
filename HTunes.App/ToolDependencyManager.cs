using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;

namespace HTunes.App;

internal enum ExternalTool
{
    FFmpeg,
    YtDlp
}

internal sealed record ToolDownloadProgress(string Message, double? Percent = null);
internal enum ToolIssueKind { Missing, UpdateAvailable, Reinstall }
internal sealed record ToolIssue(ExternalTool Tool, ToolIssueKind Kind);

internal static class ToolDependencyManager
{
    private const string FFmpegArchiveUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string FFmpegChecksumUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";
    private const string FFmpegVersionUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.ver";
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtDlpChecksumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    private const string YtDlpReleaseApiUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    public static string ToolsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "tools");

    public static IReadOnlyList<ExternalTool> MissingTools()
    {
        var missing = new List<ExternalTool>();
        if (FindExecutable("ffmpeg.exe", "FFMPEG_PATH") is null) missing.Add(ExternalTool.FFmpeg);
        if (FindExecutable("yt-dlp.exe", "YTDLP_PATH") is null) missing.Add(ExternalTool.YtDlp);
        return missing;
    }

    public static async Task<IReadOnlyList<ToolIssue>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var issues = MissingTools().Select(tool => new ToolIssue(tool, ToolIssueKind.Missing)).ToList();
        if (issues.Count > 0) return issues;
        var checks = new List<Task<ToolIssue?>>();
        if (issues.All(issue => issue.Tool != ExternalTool.FFmpeg)) checks.Add(CheckFFmpegUpdateAsync(cancellationToken));
        if (issues.All(issue => issue.Tool != ExternalTool.YtDlp)) checks.Add(CheckYtDlpUpdateAsync(cancellationToken));
        if (checks.Count > 0)
        {
            var results = await Task.WhenAll(checks);
            issues.AddRange(results.OfType<ToolIssue>());
        }
        return issues;
    }

    public static string? FindExecutable(string fileName, string environmentVariable)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) configured = Path.Combine(configured, fileName);
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(ToolsDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "tools", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", fileName)
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), fileName)));
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public static async Task DownloadMissingAsync(
        IReadOnlyCollection<ExternalTool> tools,
        IProgress<ToolDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ToolsDirectory);
        if (tools.Contains(ExternalTool.YtDlp)) await DownloadYtDlpAsync(progress, cancellationToken);
        if (tools.Contains(ExternalTool.FFmpeg)) await DownloadFFmpegAsync(progress, cancellationToken);
        progress?.Report(new ToolDownloadProgress("All required tools are ready.", 100));
    }

    private static async Task DownloadYtDlpAsync(IProgress<ToolDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(ToolsDirectory, "yt-dlp.exe.download");
        try
        {
            progress?.Report(new ToolDownloadProgress("Checking yt-dlp…"));
            var checksums = await Client.GetStringAsync(YtDlpChecksumsUrl, cancellationToken);
            var expectedHash = ReadChecksum(checksums, "yt-dlp.exe");
            await DownloadFileAsync(YtDlpUrl, temporaryPath, "Downloading yt-dlp", progress, cancellationToken);
            VerifySha256(temporaryPath, expectedHash, "yt-dlp");
            File.Move(temporaryPath, Path.Combine(ToolsDirectory, "yt-dlp.exe"), true);
        }
        finally { TryDelete(temporaryPath); }
    }

    private static async Task DownloadFFmpegAsync(IProgress<ToolDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(ToolsDirectory, "ffmpeg-release-essentials.zip.download");
        var ffmpegTemporary = Path.Combine(ToolsDirectory, "ffmpeg.exe.download");
        var ffprobeTemporary = Path.Combine(ToolsDirectory, "ffprobe.exe.download");
        try
        {
            progress?.Report(new ToolDownloadProgress("Checking the FFmpeg package…"));
            var checksumText = await Client.GetStringAsync(FFmpegChecksumUrl, cancellationToken);
            var expectedHash = ReadFirstHash(checksumText);
            await DownloadFileAsync(FFmpegArchiveUrl, archivePath, "Downloading FFmpeg (about 106 MB)", progress, cancellationToken);
            VerifySha256(archivePath, expectedHash, "FFmpeg");
            progress?.Report(new ToolDownloadProgress("Installing FFmpeg…"));
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                ExtractExecutable(archive, "ffmpeg.exe", ffmpegTemporary);
                ExtractExecutable(archive, "ffprobe.exe", ffprobeTemporary);
            }
            File.Move(ffmpegTemporary, Path.Combine(ToolsDirectory, "ffmpeg.exe"), true);
            File.Move(ffprobeTemporary, Path.Combine(ToolsDirectory, "ffprobe.exe"), true);
            File.WriteAllText(Path.Combine(ToolsDirectory, "ffmpeg-package.sha256"), expectedHash);
        }
        finally
        {
            TryDelete(archivePath);
            TryDelete(ffmpegTemporary);
            TryDelete(ffprobeTemporary);
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destination,
        string message,
        IProgress<ToolDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long received = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            received += count;
            progress?.Report(new ToolDownloadProgress(message, total > 0 ? received * 100d / total.Value : null));
        }
    }

    private static void ExtractExecutable(ZipArchive archive, string fileName, string destination)
    {
        var entry = archive.Entries.FirstOrDefault(item =>
            item.FullName.EndsWith($"/bin/{fileName}", StringComparison.OrdinalIgnoreCase));
        if (entry is null) throw new InvalidDataException($"The FFmpeg package did not contain {fileName}.");
        entry.ExtractToFile(destination, true);
    }

    private static string ReadChecksum(string contents, string fileName)
    {
        foreach (var line in contents.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase)) return parts[0];
        }
        throw new InvalidDataException($"The published checksum for {fileName} could not be found.");
    }

    private static string ReadFirstHash(string contents)
    {
        var value = contents.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return value is { Length: 64 } ? value : throw new InvalidDataException("The published FFmpeg checksum was not valid.");
    }

    private static void VerifySha256(string path, string expected, string name)
    {
        using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(input));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The downloaded {name} file did not match its published checksum and was not installed.");
    }

    private static async Task<ToolIssue?> CheckYtDlpUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var executable = FindExecutable("yt-dlp.exe", "YTDLP_PATH");
            if (executable is null) return null;
            var installedVersion = (await ReadVersionAsync(executable, "--version", cancellationToken)).Trim();
            var releaseJson = await Client.GetStringAsync(YtDlpReleaseApiUrl, cancellationToken);
            using var release = JsonDocument.Parse(releaseJson);
            var latestVersion = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            return installedVersion.Equals(latestVersion, StringComparison.OrdinalIgnoreCase) ? null : new ToolIssue(ExternalTool.YtDlp, ToolIssueKind.UpdateAvailable);
        }
        catch when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private static async Task<ToolIssue?> CheckFFmpegUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var executable = FindExecutable("ffmpeg.exe", "FFMPEG_PATH");
            if (executable is null) return null;
            var expectedPackageHash = ReadFirstHash(await Client.GetStringAsync(FFmpegChecksumUrl, cancellationToken));
            var managedExecutable = Path.Combine(ToolsDirectory, "ffmpeg.exe");
            var manifestPath = Path.Combine(ToolsDirectory, "ffmpeg-package.sha256");
            if (Path.GetFullPath(executable).Equals(Path.GetFullPath(managedExecutable), StringComparison.OrdinalIgnoreCase) && File.Exists(manifestPath))
            {
                var installedPackageHash = File.ReadAllText(manifestPath).Trim();
                return installedPackageHash.Equals(expectedPackageHash, StringComparison.OrdinalIgnoreCase) ? null : new ToolIssue(ExternalTool.FFmpeg, ToolIssueKind.UpdateAvailable);
            }

            var latestVersion = (await Client.GetStringAsync(FFmpegVersionUrl, cancellationToken)).Trim();
            var installedVersion = await ReadVersionAsync(executable, "-version", cancellationToken);
            return installedVersion.Contains(latestVersion, StringComparison.OrdinalIgnoreCase) ? null : new ToolIssue(ExternalTool.FFmpeg, ToolIssueKind.UpdateAvailable);
        }
        catch when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private static async Task<string> ReadVersionAsync(string executable, string argument, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("hTunes/1.0");
        return client;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
