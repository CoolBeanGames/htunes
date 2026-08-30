using System.Diagnostics;
using System.IO;

namespace HTunes.App;

internal static class FFmpegTranscoder
{
    public static string FindExecutable()
    {
        var executable = ToolDependencyManager.FindExecutable("ffmpeg.exe", "FFMPEG_PATH");
        return executable ?? throw new FileNotFoundException(
            "FFmpeg was not found. Restart hTunes to open the automatic setup, or add FFmpeg to Windows PATH.");
    }

    public static string Transcode(string executable, string inputPath, TranscodePreset preset, string temporaryDirectory)
    {
        if (preset.IsOriginal) return inputPath;
        Directory.CreateDirectory(temporaryDirectory);
        var outputPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}{preset.Extension}");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", inputPath, "-map", "0:a:0", "-map_metadata", "0", "-vn", "-c:a", preset.Codec! })
            start.ArgumentList.Add(argument);
        if (preset.BitrateKbps is int bitrate)
        {
            start.ArgumentList.Add("-b:a");
            start.ArgumentList.Add($"{bitrate}k");
        }
        if (preset.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("-id3v2_version");
            start.ArgumentList.Add("3");
        }
        start.ArgumentList.Add(outputPath);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg could not be started.");
        var errorTask = process.StandardError.ReadToEndAsync();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit();
        var error = errorTask.GetAwaiter().GetResult();
        _ = outputTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            TryDelete(outputPath);
            var detail = string.IsNullOrWhiteSpace(error) ? $"FFmpeg exited with code {process.ExitCode}." : error.Trim();
            throw new InvalidDataException(detail);
        }
        return outputPath;
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
