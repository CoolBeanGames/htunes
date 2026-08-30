using System.IO;
using System.Windows;

namespace HTunes.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--prepare-ipod")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var xml = DeviceSysInfoReader.Read(e.Args[1]);
                File.WriteAllText(Path.Combine(e.Args[1], "iPod_Control", "Device", "SysInfoExtended"), xml);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"hTunes could not prepare the iPod for syncing.\n\n{ex.Message}", "iPod setup failed", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
