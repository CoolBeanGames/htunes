using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HTunes.App;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportFileMode { Reference, Copy, Move }

public sealed class AppPreferences
{
    public string TranscodePresetId { get; set; } = "original";
    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "hTunes");
    public ImportFileMode ImportMode { get; set; } = ImportFileMode.Reference;
    public string ImportDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "hTunes");
    public string PodcastDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes", "podcasts");
    public bool OpenOnIPodConnection { get; set; }
    public bool AutoSyncOnConnection { get; set; }
    public bool AutoSyncMusic { get; set; } = true;
    public bool AutoSyncPodcasts { get; set; } = true;
    public bool CheckToolUpdatesOnStartup { get; set; } = true;
    public bool DebugLogging { get; set; }
    public string YtAudioFormat { get; set; } = "mp3";
    public string YtAudioQuality { get; set; } = "192K";
    public bool YtEmbedMetadata { get; set; } = true;
    public bool YtEmbedArtwork { get; set; } = true;
    public bool YtPlaylistAsAlbum { get; set; }
    public bool YtDownloadPlaylist { get; set; }
    public bool YtPlaylistSubfolders { get; set; } = true;
    public int PodcastDefaultCount { get; set; } = 3;
    public string PodcastDefaultOrder { get; set; } = "Newest";
    public bool PodcastIncludeDownloaded { get; set; } = true;
    public bool PodcastRefreshOnOpen { get; set; } = true;
    public bool PodcastAutoDownloadOnRefresh { get; set; }
    public bool PodcastDownloadOnSync { get; set; } = true;
    public bool PodcastMirrorOnSync { get; set; } = true;
    public int PodcastPlayedPercent { get; set; } = 50;
    public bool PodcastDeletePlayedDownloads { get; set; } = true;

    public AppPreferences Clone() => JsonSerializer.Deserialize<AppPreferences>(JsonSerializer.Serialize(this))!;
}

internal static class SettingsStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hTunes");
    public static string FilePath => Path.Combine(DataDirectory, "settings.json");
    public static AppPreferences Current { get; private set; } = new();
    private static bool initialized;
    public static string? LoadWarning { get; private set; }

    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        try { Current = Read(FilePath); }
        catch (Exception ex)
        {
            LoadWarning = "Settings could not be read; defaults are in use. " + ex.Message;
            try { Current = Read(FilePath + ".bak"); } catch { Current = new(); }
        }
        DebugLog.Configure(Current.DebugLogging);
    }

    public static AppPreferences Read(string path)
    {
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path)) ?? throw new InvalidDataException("Settings are empty.") : new();
        Validate(settings);
        return settings;
    }

    public static void Save(AppPreferences settings)
    {
        Write(FilePath, settings);
        Current = settings;
        DebugLog.Configure(settings.DebugLogging);
    }

    internal static void Write(string path, AppPreferences settings)
    {
        Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Validate(AppPreferences settings)
    {
        foreach (var path in new[] { settings.DownloadDirectory, settings.ImportDirectory, settings.PodcastDirectory })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ArgumentException("Storage locations must be full Windows folder paths.");
            if (File.Exists(path)) throw new ArgumentException("A storage location refers to a file, not a folder.");
            var fullPath = Path.GetFullPath(path);
            if (fullPath[(Path.GetPathRoot(fullPath)?.Length ?? 0)..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new ArgumentException("A storage location contains an invalid folder name.");
        }
        if (!Enum.IsDefined(settings.ImportMode)) throw new ArgumentException("Choose a valid import behavior.");
        if (settings.PodcastDefaultCount is < 0 or > 999) throw new ArgumentException("Podcast episode count must be between 0 and 999.");
        if (settings.PodcastPlayedPercent is < 1 or > 100) throw new ArgumentException("Played threshold must be between 1 and 100 percent.");
        if (settings.PodcastDefaultOrder is not ("Newest" or "Oldest")) throw new ArgumentException("Choose newest or oldest episodes.");
        if (!YtDlpSettings.AudioFormats.Contains(settings.YtAudioFormat) || !YtDlpSettings.AudioQualities.Contains(settings.YtAudioQuality))
            throw new ArgumentException("Choose a supported yt-dlp audio format and quality.");
    }
}

internal static class YtDlpSettings
{
    public static readonly string[] AudioFormats = ["best", "mp3", "m4a", "opus", "flac", "wav"];
    public static readonly string[] AudioQualities = ["0", "128K", "192K", "256K", "320K"];

    // Ready for the Download workflow: pass these through ProcessStartInfo.ArgumentList, never a shell.
    public static IReadOnlyList<string> BuildArguments(AppPreferences settings)
    {
        SettingsStore.Validate(settings);
        var args = new List<string> { "--ignore-config", "--windows-filenames", "--no-overwrites", "--extract-audio", "--audio-format", settings.YtAudioFormat,
            "--audio-quality", settings.YtAudioQuality, "--paths", settings.DownloadDirectory,
            "--output", settings.YtPlaylistSubfolders ? "%(playlist_title|Singles)s/%(title)s [%(id)s].%(ext)s" : "%(title)s [%(id)s].%(ext)s",
            settings.YtDownloadPlaylist ? "--yes-playlist" : "--no-playlist" };
        if (settings.YtEmbedMetadata || settings.YtPlaylistAsAlbum) args.Add("--embed-metadata");
        if (settings.YtEmbedArtwork) args.Add("--embed-thumbnail");
        if (settings.YtPlaylistAsAlbum) args.AddRange(["--parse-metadata", "playlist_title:%(meta_album)s"]);
        return args;
    }
}
