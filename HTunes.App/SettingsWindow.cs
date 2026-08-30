using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace HTunes.App;

internal sealed class SettingsWindow : Window
{
    private readonly AppPreferences draft;
    private readonly List<Action> readControls = [];
    private readonly TabControl tabs = new();

    public SettingsWindow(AppPreferences settings, Action<AppPreferences> save)
    {
        draft = settings.Clone();
        Title = "hTunes Settings"; Width = 780; Height = 700; MinWidth = 620; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Save", IsDefault = true, MinWidth = 90 };
        apply.Click += (_, _) =>
        {
            try
            {
                foreach (var read in readControls) read();
                SettingsStore.Validate(draft);
                save(draft);
                DialogResult = true;
            }
            catch (Exception ex) { DebugLog.Write("Settings", "Save failed", ex); MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons); root.Children.Add(tabs); Content = root;

        var storage = Page("Storage");
        Note(storage, "Locations apply to future downloads and imports. Existing files stay where they are and remain accessible to hTunes.");
        Folder(storage, "Download location", draft.DownloadDirectory, value => draft.DownloadDirectory = value);
        Choice(storage, "When importing music", Enum.GetNames<ImportFileMode>(), draft.ImportMode.ToString(), value => draft.ImportMode = Enum.Parse<ImportFileMode>(value));
        Note(storage, "Reference: leave files in place. Copy: keep originals and add a verified copy. Move: remove originals only after the copy is verified and the library is saved. Name collisions get a new filename. Undo removes library entries only; it does not reverse file copies or moves.");
        Folder(storage, "Managed music location (Copy / Move)", draft.ImportDirectory, value => draft.ImportDirectory = value);
        Folder(storage, "Podcast storage location", draft.PodcastDirectory, value => draft.PodcastDirectory = value);

        var ipod = Page("iPod");
        Check(ipod, "Open hTunes when an iPod is connected", draft.OpenOnIPodConnection, value => draft.OpenOnIPodConnection = value);
        Note(ipod, "Opt-in background watcher: closing the window leaves hTunes in the notification area, and Windows starts the watcher at sign-in. Keep hTunes at the same installed location. Use File → Exit or the tray menu to stop it until next launch/sign-in.");
        Check(ipod, "Automatically sync on app opening / iPod connection", draft.AutoSyncOnConnection, value => draft.AutoSyncOnConnection = value);
        Check(ipod, "Include the entire music library", draft.AutoSyncMusic, value => draft.AutoSyncMusic = value);
        Check(ipod, "Include podcasts using each show's sync rule", draft.AutoSyncPodcasts, value => draft.AutoSyncPodcasts = value);
        Note(ipod, "Automatic sync runs once per connection, after listening progress is reconciled. It uses the sync-bar transcode choice and fills available music space with random eligible tracks if necessary. Podcast mirroring follows the Podcasts settings. Changes apply on the next opening/connection.");

        var yt = Page("yt-dlp");
        Note(yt, "Used by the Download tab for each new queue. hTunes uses its FFmpeg and ffprobe for conversion and artwork embedding. Playlist totals are discovered by yt-dlp as each link runs.");
        Choice(yt, "Audio format", YtDlpSettings.AudioFormats, draft.YtAudioFormat, value => draft.YtAudioFormat = value);
        Choice(yt, "Audio quality / target bitrate", YtDlpSettings.AudioQualities, draft.YtAudioQuality, value => draft.YtAudioQuality = value);
        Note(yt, "0 = best variable quality. Bitrates apply to lossy conversions; lossless formats ignore them. Selecting a higher bitrate cannot improve the source recording.");
        Check(yt, "Embed metadata", draft.YtEmbedMetadata, value => draft.YtEmbedMetadata = value);
        Check(yt, "Embed artwork / thumbnail where supported", draft.YtEmbedArtwork, value => draft.YtEmbedArtwork = value);
        Check(yt, "Use playlist name as album (also embeds metadata)", draft.YtPlaylistAsAlbum, value => draft.YtPlaylistAsAlbum = value);
        Check(yt, "Download the full playlist when the URL includes a playlist", draft.YtDownloadPlaylist, value => draft.YtDownloadPlaylist = value);
        Check(yt, "Organize downloads into playlist folders", draft.YtPlaylistSubfolders, value => draft.YtPlaylistSubfolders = value);

        var podcasts = Page("Podcasts");
        Number(podcasts, "Default episode count for NEW subscriptions (0–999)", draft.PodcastDefaultCount, value => draft.PodcastDefaultCount = value);
        Choice(podcasts, "Default order for NEW subscriptions", ["Newest", "Oldest"], draft.PodcastDefaultOrder, value => draft.PodcastDefaultOrder = value);
        Note(podcasts, "Existing shows keep their individual episode counts and order. Change those in the Podcasts tab.");
        Check(podcasts, "Also sync manually downloaded unplayed episodes", draft.PodcastIncludeDownloaded, value => draft.PodcastIncludeDownloaded = value);
        Check(podcasts, "Refresh feeds when opening the Podcasts tab", draft.PodcastRefreshOnOpen, value => draft.PodcastRefreshOnOpen = value);
        Check(podcasts, "Download each show's selected episodes after a feed refresh", draft.PodcastAutoDownloadOnRefresh, value => draft.PodcastAutoDownloadOnRefresh = value);
        Check(podcasts, "Download missing selected episodes during sync", draft.PodcastDownloadOnSync, value => draft.PodcastDownloadOnSync = value);
        Check(podcasts, "Mirror subscription selections on Sync all (remove other managed episodes)", draft.PodcastMirrorOnSync, value => draft.PodcastMirrorOnSync = value);
        Number(podcasts, "Count as played at this percentage (1–100; default 50)", draft.PodcastPlayedPercent, value => draft.PodcastPlayedPercent = value);
        Check(podcasts, "Delete local downloads once played", draft.PodcastDeletePlayedDownloads, value => draft.PodcastDeletePlayedDownloads = value);
        Note(podcasts, "An episode still playing in hTunes is deleted after playback stops. Played episodes are removed from the iPod during reconciliation regardless of the local-download setting. Turning off download-on-sync requires all selected episodes to be downloaded first.");

        var tools = Page("Tools & debug");
        Check(tools, "Check FFmpeg and yt-dlp for updates at startup", draft.CheckToolUpdatesOnStartup, value => draft.CheckToolUpdatesOnStartup = value);
        Note(tools, "Missing tools are still reported even when update checks are disabled.");
        ActionButton(tools, "Update / reinstall FFmpeg and yt-dlp…", () => new DependencySetupWindow([
            new ToolIssue(ExternalTool.FFmpeg, ToolIssueKind.Reinstall), new ToolIssue(ExternalTool.YtDlp, ToolIssueKind.Reinstall)]) { Owner = this }.ShowDialog());
        Check(tools, "Write debug data to a text file", draft.DebugLogging, value => draft.DebugLogging = value);
        Note(tools, "Logs include operations, counts, file paths, and errors. Web addresses are redacted, but filenames can still be personal: review logs before sharing. Logging starts after Save. Logs rotate at 5 MB with three backups.");
        Note(tools, DebugLog.FilePath);
        ActionButton(tools, "Open logs folder", () =>
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DebugLog.FilePath)!);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { System.IO.Path.GetDirectoryName(DebugLog.FilePath)! } });
        });
    }

    private StackPanel Page(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        return panel;
    }
    private static void Note(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) });
    private void Check(Panel panel, string text, bool value, Action<bool> save)
    {
        var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(box); readControls.Add(() => save(box.IsChecked == true));
    }
    private void Choice(Panel panel, string label, IEnumerable<string> options, string value, Action<string> save)
    {
        Note(panel, label);
        var box = new ComboBox { ItemsSource = options, SelectedItem = value, MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(box); readControls.Add(() => save(box.SelectedItem as string ?? ""));
    }
    private void Number(Panel panel, string label, int value, Action<int> save)
    {
        Note(panel, label);
        var box = new TextBox { Text = value.ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(box); readControls.Add(() => { if (!int.TryParse(box.Text, out var number)) throw new ArgumentException(label + ": enter a whole number."); save(number); });
    }
    private void Folder(Panel panel, string label, string value, Action<string> save)
    {
        Note(panel, label);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var box = new TextBox { Text = value, VerticalContentAlignment = VerticalAlignment.Center };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        browse.Click += (_, _) => { var dialog = new OpenFolderDialog { Title = label }; if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName; };
        DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(box); panel.Children.Add(row);
        readControls.Add(() => save(box.Text.Trim()));
    }
    private static void ActionButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 6, 0, 14) };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "hTunes"); } };
        panel.Children.Add(button);
    }
}
