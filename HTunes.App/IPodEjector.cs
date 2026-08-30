using System.IO;
using System.Runtime.InteropServices;

namespace HTunes.App;

internal static class IPodEjector
{
    public static bool TryEject(string rootPath, out string error)
    {
        object? shellObject = null;
        object? drivesObject = null;
        object? driveItemObject = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) throw new InvalidOperationException("Windows Shell is unavailable.");
            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null) throw new InvalidOperationException("Windows Shell could not be started.");

            dynamic shell = shellObject;
            drivesObject = shell.NameSpace(17);
            if (drivesObject is null) throw new InvalidOperationException("Windows could not open This PC.");
            dynamic drives = drivesObject;
            var driveName = rootPath.TrimEnd(Path.DirectorySeparatorChar);
            driveItemObject = drives.ParseName(driveName);
            if (driveItemObject is null) throw new InvalidOperationException($"Windows could not find {driveName}.");
            dynamic driveItem = driveItemObject;
            driveItem.InvokeVerb("Eject");
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Windows could not safely eject the iPod. Close any files or Explorer windows using it, then try again.\n\n{ex.Message}";
            return false;
        }
        finally
        {
            Release(driveItemObject); Release(drivesObject); Release(shellObject);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
