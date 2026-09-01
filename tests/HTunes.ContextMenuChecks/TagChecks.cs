using HTunes.App;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static void CheckTagEditor()
    {
        InTemporaryDirectory(directory =>
        {
            var artwork = Path.Combine(directory, "cover.png");
            CreateTagTestArtwork(artwork);
            var first = new Track { FilePath = Path.Combine(directory, "first.wav"), Title = "First title", Artist = "First artist", Album = "Before", ArtworkPath = artwork, PlayCount = 12 };
            var second = new Track { FilePath = Path.Combine(directory, "second.wav"), Title = "Second title", Artist = "Second artist", Album = "Before", ArtworkPath = artwork, PlayCount = 6 };
            foreach (var track in new[] { first, second })
            {
                File.WriteAllBytes(track.FilePath, SyntheticWav());
                using var media = TagLib.File.Create(track.FilePath);
                media.Tag.Title = track.Title; media.Tag.Performers = [track.Artist, "Guest performer"]; media.Tag.Album = track.Album;
                media.Tag.Comment = "Unrelated tag must survive"; media.Tag.Pictures = [new TagLib.Picture(artwork)]; media.Save();
            }
            var saveCount = 0;
            var patch = new TagPatch(new Dictionary<string, string> { ["Album"] = "After", ["Year"] = "2004" }, ResizeArtwork: true, Width: 300, Height: 300);
            var edit = TagBatchEdit.Apply([first, second], patch, true, Path.Combine(directory, "art"), () => saveCount++);
            Require(first.Album == "After" && second.Album == "After" && first.Title == "First title" && second.Title == "Second title", "Bulk tag editing must apply only checked fields.");
            Require(first.MetadataManagedByLibrary && second.MetadataManagedByLibrary, "Explicit library edits must survive metadata refresh on restart.");
            var resized = TagArtwork.Read(first.ArtworkPath!);
            Require(resized.PixelWidth == 300 && resized.PixelHeight == 200, "Resize must fit a 300px box while preserving a 3:2 aspect ratio.");
            using (var media = TagLib.File.Create(first.FilePath))
                Require(media.Tag.Album == "After" && media.Tag.Year == 2004 && media.Tag.Performers.Length == 2 && media.Tag.Comment == "Unrelated tag must survive", "Actual file tags change while unchecked tags and multiple performers survive.");
            first.PlayCount = 30;
            edit.Undo();
            Require(first.Album == "Before" && first.PlayCount == 30 && first.ArtworkPath == artwork && !first.MetadataManagedByLibrary, "Undo restores edited metadata/artwork, not listening counts.");
            using (var media = TagLib.File.Create(first.FilePath)) Require(media.Tag.Album == "Before", "Undo restores physical file tags.");
            edit.Redo(); Require(first.Album == "After" && saveCount == 3, "Redo must reapply and persist tags.");
            var removal = TagBatchEdit.Apply([first], new TagPatch(new Dictionary<string, string> { ["Genre"] = "" }, true), true, directory, () => { });
            using (var media = TagLib.File.Create(first.FilePath)) Require(media.Tag.Pictures.Length == 0 && media.Tag.Genres.Length == 0, "Explicit blank fields and artwork removal clear their file tags.");
            removal.Undo();
            var beforeFailure = first.Album;
            ExpectFailure(() => TagBatchEdit.Apply([first, second], new TagPatch(new Dictionary<string, string> { ["Album"] = "Must roll back" }), true, directory, () => throw new IOException("Simulated library save failure")), "Failed library save must roll back batch metadata.");
            Require(first.Album == beforeFailure && second.Album == "After", "A failed save restores all in-memory records.");
            using (var media = TagLib.File.Create(first.FilePath)) Require(media.Tag.Album == "After", "A failed save rolls back already-written physical tags.");
            var missing = new Track { Title = "Missing", FilePath = Path.Combine(directory, "missing.wav") };
            ExpectFailure(() => TagBatchEdit.Apply([first, missing], new TagPatch(new Dictionary<string, string> { ["Album"] = "No partial writes" }), true, directory, () => { }), "Missing files must fail preflight before editing any file.");
            Require(first.Album == "After", "Failed preflight leaves the batch unchanged.");
            File.SetAttributes(second.FilePath, File.GetAttributes(second.FilePath) | FileAttributes.ReadOnly);
            try
            {
                ExpectFailure(() => TagBatchEdit.Apply([first, second], new TagPatch(new Dictionary<string, string> { ["Album"] = "Read-only failure" }), true, directory, () => { }), "Read-only files must fail before writing the batch.");
                Require(first.Album == "After", "Read-only preflight must preserve earlier selected tracks.");
            }
            finally { File.SetAttributes(second.FilePath, File.GetAttributes(second.FilePath) & ~FileAttributes.ReadOnly); }
            TagBatchEdit.Apply([missing], new TagPatch(new Dictionary<string, string> { ["Album"] = "Library only" }), false, directory, () => { });
            Require(missing.Album == "Library only", "Missing files can still receive an explicit library-only edit.");
            foreach (var invalid in new[] { "-1", "abc", "10000" }) ExpectFailure(() => new TagPatch(new Dictionary<string, string> { ["Year"] = invalid }).Validate(), "Invalid years must be rejected.");
            ExpectFailure(() => new TagPatch(new Dictionary<string, string>(), ResizeArtwork: true, Width: 0).Validate(), "Invalid artwork dimensions must be rejected.");
            CheckTagWorkspace(directory, artwork);
        });
    }

    private static void CheckTagWorkspace(string directory, string artwork)
    {
        var window = new MainWindow(false, Path.Combine(directory, "library.json"));
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var fields = (Dictionary<string, (CheckBox Check, TextBox Box)>)typeof(MainWindow).GetField("tagFields", flags)!.GetValue(window)!;
        var titles = new[] { "The Long Way Home", "Golden Hour", "Signal Fires", "Northbound", "Evening Light", "Paper Planes", "Still Water", "Sunday Morning", "Blue Horizon", "Open Road", "After the Rain", "Last Train" };
        var tracks = titles.Select((title, index) => new Track { Title = title, Artist = index < 4 ? "The Coastline" : index < 8 ? "Juniper Lane" : "Miles West", Album = index < 4 ? "Northbound" : index < 8 ? "Quiet Hours" : "Open Road", Genre = index < 4 ? "Indie Rock" : index < 8 ? "Folk" : "Jazz", TrackNumber = index % 4 + 1, DiscNumber = 1, Year = index < 4 ? 2024 : 2023, Format = index < 4 ? "FLAC" : "MP3", BitrateKbps = index < 4 ? 945 : 192, PlayCount = 5 + index * 3, DateAdded = new DateTime(2026, 8, 30), FilePath = Path.Combine(directory, "demo-" + index + ".wav"), ArtworkPath = artwork }).ToList();
        try
        {
            foreach (var track in tracks) File.WriteAllBytes(track.FilePath, SyntheticWav());
            Set("allTracks", tracks); Set("isTagView", true);
            Control<RadioButton>("TagTab").IsChecked = true;
            Control<FrameworkElement>("MusicWorkspace").Visibility = Visibility.Collapsed;
            Control<FrameworkElement>("TagWorkspace").Visibility = Visibility.Visible;
            Call("RefreshTagLibrary");
            var grid = Control<DataGrid>("TagTracksGrid");
            foreach (var track in tracks.Take(4)) grid.SelectedItems.Add(track);
            Require(grid.Items.Count == 12 && grid.SelectedItems.Count == 4 && grid.SelectionMode == DataGridSelectionMode.Extended, "Tag spreadsheet must show every local track and support multi-selection.");
            Require(fields["Title"].Box.Text == "" && fields["Title"].Check.Content.ToString()!.Contains("mixed") && fields["Album"].Box.Text == "Northbound", "Inspector must distinguish mixed values from shared values.");
            Require(!Control<Button>("TagApplyButton").IsEnabled, "Selecting tracks must not automatically mark any fields for writing.");
            fields["Genre"].Box.Text = "Alternative Rock";
            Require(fields["Genre"].Check.IsChecked == true && fields["Title"].Check.IsChecked == false && Control<Button>("TagApplyButton").IsEnabled, "Typing a field must check only that field.");
            Call("RefreshTagLibrary"); Require(fields["Genre"].Box.Text == "Alternative Rock" && fields["Genre"].Check.IsChecked == true, "Revisiting the tab must preserve an unsaved draft for the same selection.");
            Set("isYtDownloading", true); Call("UpdateBusyWorkspaces");
            Require(Control<FrameworkElement>("TagWorkspace").IsEnabled && Control<Button>("TagApplyButton").IsEnabled, "A background download must not freeze tagging or navigation.");
            Set("isYtDownloading", false); Call("UpdateBusyWorkspaces");
            Control<CheckBox>("TagResizeArtwork").IsChecked = true;
            Call("UpdateDeviceStripMode");
            Control<TextBlock>("TagStatus").Text = "Preview with sample tracks • Genre and artwork size will apply to the 4 selected tracks.";
            var screenshots = Environment.GetEnvironmentVariable("HTUNES_UI_CHECK_OUTPUT");
            if (!string.IsNullOrEmpty(screenshots))
            {
                Directory.CreateDirectory(screenshots);
                var root = (Grid)window.Content; root.Background = window.Background;
                root.Measure(new Size(1440, 900)); root.Arrange(new Rect(0, 0, 1440, 900)); root.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); root.UpdateLayout();
                var image = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32); image.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(screenshots, "tag-editor.png")); encoder.Save(stream);
            }
            Call("TagReset_Click", window, new RoutedEventArgs());
            Require(!Control<Button>("TagApplyButton").IsEnabled && fields["Genre"].Box.Text == "Indie Rock", "Reset must discard only the draft.");
            Control<TextBox>("TagSearchBox").Text = "Juniper";
            Require(grid.Items.Count == 4 && grid.SelectedItems.Count == 0, "Search must filter the complete library and clear hidden selections.");
            Call("SelectAllActiveItems"); Require(grid.SelectedItems.Count == 4, "Ctrl+A targets the filtered Tag grid.");
            fields["Album"].Box.Text = "Updated through inspector";
            RunUiTask(async () =>
            {
                Call("TagApply_Click", window, new RoutedEventArgs());
                while ((bool)typeof(MainWindow).GetField("isTagSaving", flags)!.GetValue(window)!) await Task.Delay(10);
            });
            Require(Control<TextBlock>("TagStatus").Text.StartsWith("Saved 4 tracks"), "Apply button must complete a real batch save through the inspector.");
            var saved = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(Path.Combine(directory, "library.json")))!;
            Require(saved.Tracks.Count == 12 && saved.Tracks.Count(t => t.Album == "Updated through inspector") == 4, "Inspector saves only selected tracks while preserving the full library.");
            saved.Tracks.Add(new Track { Title = "Missing file", FilePath = Path.Combine(directory, "unavailable.wav"), MetadataManagedByLibrary = true });
            saved.Tracks[4].Album = "";
            File.WriteAllText(Path.Combine(directory, "library.json"), JsonSerializer.Serialize(saved));
            var reloaded = new MainWindow(false, Path.Combine(directory, "library.json"));
            try
            {
                typeof(MainWindow).GetMethod("LoadLibrary", flags)!.Invoke(reloaded, null);
                var persisted = (List<Track>)typeof(MainWindow).GetField("allTracks", flags)!.GetValue(reloaded)!;
                Require(persisted.Count == 13 && persisted[4].Album == "", "Restart must preserve missing entries and explicit cleared metadata.");
            }
            finally { reloaded.Close(); }
        }
        finally { window.Close(); }
    }

    private static void CreateTagTestArtwork(string path)
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(new LinearGradientBrush(Color.FromRgb(31, 62, 83), Color.FromRgb(146, 189, 179), 90), null, new Rect(0, 0, 900, 600));
            draw.DrawEllipse(new SolidColorBrush(Color.FromRgb(244, 214, 157)), null, new Point(620, 195), 90, 90);
            var mountain = Geometry.Parse("M 0,480 L 240,240 420,400 660,290 900,460 900,600 0,600 Z");
            draw.DrawGeometry(new SolidColorBrush(Color.FromRgb(36, 75, 91)), null, mountain);
            var text = new FormattedText("NORTHBOUND", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 56, Brushes.White, 1);
            draw.DrawText(text, new Point(58, 490));
        }
        var image = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
