using Microsoft.Win32;
using System.IO;

namespace HTunes.App;

internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate hTunes for startup.");
            var command = $"\"{executable}\"";
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                command += $" \"{typeof(App).Assembly.Location}\"";
            key.SetValue("hTunes", command + " --watch-ipod", RegistryValueKind.String);
        }
        else key.DeleteValue("hTunes", throwOnMissingValue: false);
        DebugLog.Write("Startup", $"Sign-in watcher enabled={enabled}");
    }
}
