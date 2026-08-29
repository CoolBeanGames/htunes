using System.IO;

namespace HTunes.App;

internal sealed record IPodDevice(string RootPath, string Name, long Capacity, long FreeSpace);

internal static class IPodDetector
{
    public static IPodDevice? FindConnected()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || !Directory.Exists(Path.Combine(drive.RootDirectory.FullName, "iPod_Control"))) continue;
                var label = drive.VolumeLabel.Trim();
                return new IPodDevice(drive.RootDirectory.FullName, string.IsNullOrWhiteSpace(label) ? "iPod" : label, drive.TotalSize, drive.AvailableFreeSpace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
