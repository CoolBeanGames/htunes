using System.IO;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace HTunes.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private EventWaitHandle? showRequest;
    private RegisteredWaitHandle? requestWait;
    private Forms.NotifyIcon? tray;
    private readonly DispatcherTimer watcher = new() { Interval = TimeSpan.FromSeconds(3) };
    private string? lastDeviceRoot;
    private bool exiting;

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
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SettingsStore.Initialize();
        DebugLog.Write("App", $"Startup version={typeof(App).Assembly.GetName().Version}; watcher={e.Args.Contains("--watch-ipod")}");
        DispatcherUnhandledException += (_, args) => DebugLog.Write("App", "Unhandled UI exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => DebugLog.Write("App", "Unhandled background exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => DebugLog.Write("App", "Unobserved task exception", args.Exception);
        instanceMutex = new Mutex(true, @"Local\hTunes.Application", out var firstInstance);
        showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\hTunes.ShowWindow");
        if (!firstInstance)
        {
            if (!e.Args.Contains("--watch-ipod")) showRequest.Set();
            Shutdown(); return;
        }
        requestWait = ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) => Dispatcher.BeginInvoke(new Action(OpenMainWindow)), null, Timeout.Infinite, false);
        watcher.Tick += (_, _) => CheckForIPod();
        ConfigureWatcher();
        if (!e.Args.Contains("--watch-ipod"))
        {
            var splash = new SplashScreen("Assets/splash.png");
            splash.Show(autoClose: false, topMost: true);
            OpenMainWindow(splash);
        }
        else if (SettingsStore.Current.OpenOnIPodConnection) CheckForIPod();
        else Shutdown();
    }

    public void ConfigureWatcher()
    {
        if (SettingsStore.Current.OpenOnIPodConnection)
        {
            if (tray is null)
            {
                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("Open hTunes", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenMainWindow)));
                menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication)));
                tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "hTunes — watching for an iPod", ContextMenuStrip = menu, Visible = true };
                tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(OpenMainWindow));
            }
            watcher.Start();
        }
        else
        {
            watcher.Stop();
            tray?.ContextMenuStrip?.Dispose(); tray?.Dispose(); tray = null;
        }
    }

    private void CheckForIPod()
    {
        try
        {
            var root = IPodDetector.FindConnected()?.RootPath;
            var newConnection = root is not null && !string.Equals(root, lastDeviceRoot, StringComparison.OrdinalIgnoreCase);
            lastDeviceRoot = root;
            if (newConnection && MainWindow is null) { DebugLog.Write("Watcher", $"iPod connected at {root}"); OpenMainWindow(); }
        }
        catch (Exception ex) { DebugLog.Write("Watcher", "Detection failed", ex); }
    }

    private void OpenMainWindow() => OpenMainWindow(null);

    private void OpenMainWindow(SplashScreen? splash)
    {
        if (exiting) { splash?.Close(TimeSpan.Zero); return; }
        if (MainWindow is { } existing)
        {
            splash?.Close(TimeSpan.Zero);
            existing.Show();
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate(); return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.StartupCheckInProgress = true;
        window.UpdateDownloadControls();
        if (splash is not null) window.ContentRendered += (_, _) => splash.Close(TimeSpan.FromMilliseconds(250));
        window.ContentRendered += CheckDependenciesOnFirstRender;
        window.Closed += (_, _) =>
        {
            MainWindow = null;
            lastDeviceRoot = IPodDetector.FindConnected()?.RootPath;
            if (exiting || !SettingsStore.Current.OpenOnIPodConnection) Shutdown();
        };
        window.Show();
    }

    public void ExitApplication()
    {
        MainWindow?.Close();
        if (MainWindow is not null) return; // A running sync/download may cancel closing.
        exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DebugLog.Write("App", "Shutdown");
        watcher.Stop(); tray?.ContextMenuStrip?.Dispose(); tray?.Dispose();
        requestWait?.Unregister(null); showRequest?.Dispose(); instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private async void CheckDependenciesOnFirstRender(object? sender, EventArgs e)
    {
        if (sender is not MainWindow mainWindow) return;
        mainWindow.ContentRendered -= CheckDependenciesOnFirstRender;
        try
        {
            using var updateCheckTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var issues = SettingsStore.Current.CheckToolUpdatesOnStartup
                ? await ToolDependencyManager.CheckForUpdatesAsync(updateCheckTimeout.Token)
                : ToolDependencyManager.MissingTools().Select(tool => new ToolIssue(tool, ToolIssueKind.Missing)).ToList();
            if (issues.Count > 0 && mainWindow.IsVisible) new DependencySetupWindow(issues) { Owner = mainWindow }.ShowDialog();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Tools", "Startup check failed (opening library anyway)", ex);
        }
        finally { mainWindow.StartupCheckInProgress = false; mainWindow.UpdateDownloadControls(); }
        if (mainWindow.IsVisible && SettingsStore.LoadWarning is { } warning) MessageBox.Show(mainWindow, warning, "Settings recovered");
    }
}
