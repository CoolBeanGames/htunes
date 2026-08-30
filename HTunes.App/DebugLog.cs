using System.IO;
using System.Text.RegularExpressions;

namespace HTunes.App;

internal static class DebugLog
{
    private static readonly object Gate = new();
    private static bool enabled;
    public static string DirectoryPath => Path.Combine(SettingsStore.DataDirectory, "logs");
    public static string FilePath => Path.Combine(DirectoryPath, "debug.log");
    public static string? LastWriteError { get; private set; }
    public static void Configure(bool value) { lock (Gate) enabled = value; }

    public static void Write(string area, string message, Exception? error = null)
    {
        lock (Gate)
        {
            if (!enabled) return;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 5 * 1024 * 1024)
                {
                    for (var i = 2; i >= 1; i--)
                        if (File.Exists(FilePath + "." + i)) File.Move(FilePath + "." + i, FilePath + "." + (i + 1), true);
                    File.Move(FilePath, FilePath + ".1", true);
                }
                var details = error is null ? "" : $" | {error.GetType().Name}: {error.Message} {error.StackTrace}";
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} [{area}] {Sanitize(message + details)}{Environment.NewLine}");
                LastWriteError = null;
            }
            catch (Exception ex) { LastWriteError = ex.Message; } // Logging must not crash the application.
        }
    }

    internal static string Sanitize(string text)
    {
        text = Regex.Replace(text, @"https?://[^\s]+", "[URL removed]", RegexOptions.IgnoreCase);
        return text.Replace('\r', ' ').Replace('\n', ' ');
    }
}
