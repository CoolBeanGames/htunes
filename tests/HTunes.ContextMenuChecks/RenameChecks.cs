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
    private static void CheckRenameEditor()
    {
        var sample = new Track { FilePath = @"C:\Music\01_Song_demo_tail.MP3", Artist = "A/B", Album = "Album: 2", Title = "Song?" };
        Require(new RenameOptions(Replace: true, Find: "_", Replacement: " ", Remove: true, RemoveText: "demo", TrimFront: true, FrontCount: 3, TrimEnd: true, EndCount: 5, Prepend: true, Prefix: "New ", Append: true, Suffix: "!").Transform(sample) == "New Song !", "Rules run replace/remove/front/end/prepend/append in order on the stem only.");
        Require(new RenameOptions(RenameMode.MetadataFileName).Transform(sample) == "A_B - Album_ 2 - Song_", "Metadata templates sanitize Windows-invalid characters without moving folders.");
        Require(new RenameOptions(RenameMode.FileNameToTitle, TrimFront: true, FrontCount: 999).Transform(sample) == "01_Song_demo_tail", "Filename-to-title ignores filename transformation rules and excludes the extension.");
        Require(new RenameOptions(Remove: true, RemoveText: "song", IgnoreCase: true).Transform(sample) == "01__demo_tail", "Ignore-case removal must operate literally.");
        Require(new RenameOptions(Replace: true, Find: "SONG", Replacement: "Tune", IgnoreCase: true).Transform(sample) == "01_Tune_demo_tail", "Ignore-case replacement must preserve nonmatching text.");
        Require(new RenameOptions(TrimFront: true, FrontCount: 2).Transform(new Track { FilePath = @"C:\A😀B.mp3" }) == "B", "Trimming must not split a Unicode character.");
        foreach (var name in new[] { "CON.mp3", "LPT1.mp3", "NUL.extra.mp3", "bad/name.mp3", "bad:name.mp3", "bad?.mp3", "x.", new string('a', 256) })
            Require(LibraryRenameService.ValidateFileName(name, "x") is not null, "Reject invalid or reserved Windows filenames: " + name);
        ExpectFailure(() => new RenameOptions(Replace: true).Validate(), "Empty search strings must not be accepted.");
        ExpectFailure(() => new RenameOptions(TrimEnd: true, EndCount: -1).Validate(), "Negative trim counts must not be accepted.");

        InTemporaryDirectory(directory =>
        {
            Track Create(string name, string content) { var path = Path.Combine(directory, name); File.WriteAllText(path, content); return new Track { FilePath = path, OriginalImportPath = path, Title = name, Artist = "Artist", Album = "Album" }; }
            var first = Create("a.MP3", "first audio bytes"); var second = Create("xa.MP3", "second audio bytes");
            var alias = new Track { FilePath = first.FilePath, OriginalImportPath = first.FilePath };
            var copy = Create("copy.MP3", "copy bytes"); copy.OriginalImportPath = first.FilePath;
            var library = new List<Track> { first, second, alias, copy };
            var ids = library.Select(t => t.Id).ToArray(); var before = first.FilePath;
            var saves = 0;
            var rename = LibraryRenameService.Apply([first, second], library, new RenameOptions(Prepend: true, Prefix: "x"), false, () => saves++);
            Require(File.ReadAllText(first.FilePath) == "first audio bytes" && File.ReadAllText(second.FilePath) == "second audio bytes", "Overlapping rename chains preserve each file's contents.");
            Require(Path.GetFileName(first.FilePath) == "xa.MP3" && Path.GetFileName(second.FilePath) == "xxa.MP3" && Path.GetExtension(first.FilePath) == ".MP3", "Renaming preserves extension spelling and case.");
            Require(alias.FilePath == first.FilePath && alias.OriginalImportPath == first.FilePath && copy.OriginalImportPath == first.FilePath && Path.GetFileName(copy.FilePath) == "copy.MP3", "Every alias and matching original-import reference must follow the renamed file.");
            Require(library.Select(t => t.Id).SequenceEqual(ids), "Track IDs must stay stable for playlists and listening counts.");
            rename.Undo(); Require(first.FilePath == before && File.ReadAllText(before) == "first audio bytes" && copy.OriginalImportPath == before, "Undo restores files and library paths together.");
            rename.Redo(); Require(saves == 3 && File.Exists(second.FilePath), "Redo must save the renamed library again.");
            File.WriteAllText(before, "unrelated new file");
            ExpectFailure(rename.Undo, "Undo must refuse to overwrite a file created at the old name.");
            Require(File.ReadAllText(before) == "unrelated new file" && File.Exists(first.FilePath), "Blocked undo preserves both unrelated and renamed files.");
            File.Delete(before); rename.Undo();
            var collision = LibraryRenameService.Preview([first], library, new RenameOptions(Prepend: true, Prefix: "x"), false);
            var extensionOnly = LibraryRenameService.Preview([first], library, new RenameOptions(Remove: true, RemoveText: ".MP3"), false);
            Require(extensionOnly.Single().Proposed == "a.MP3" && !extensionOnly.Single().Changed, "A rule matching only the extension must leave the file completely unchanged.");
            Require(collision.Single().Error == "Destination already exists", "An unselected existing destination must block renaming.");
            var empty = LibraryRenameService.Preview([first], library, new RenameOptions(TrimFront: true, FrontCount: 100), false);
            Require(empty.Single().Error is not null, "Trimming away the complete stem must not create an extension-only filename.");
            first.Title = second.Title = "Same";
            Require(LibraryRenameService.Preview([first, second], library, new RenameOptions(RenameMode.MetadataFileName), false).All(r => r.Error == "Duplicate destination name"), "Duplicate generated filenames must be detected before writes.");
            var originalPath = first.FilePath;
            var caseEdit = LibraryRenameService.Apply([first], library, new RenameOptions(Replace: true, Find: "a", Replacement: "A"), false, () => { });
            Require(Path.GetFileName(first.FilePath) == "A.MP3" && Directory.GetFiles(directory).Any(p => Path.GetFileName(p) == "A.MP3"), "Case-only renames must work on Windows.");
            caseEdit.Undo(); Require(first.FilePath == originalPath, "Undo must restore original casing.");
            var failCount = 0;
            ExpectFailure(() => LibraryRenameService.Apply([first, second], library, new RenameOptions(Append: true, Suffix: " changed"), false, () => { if (++failCount == 1) throw new IOException("Simulated library failure"); }), "A failed save must trigger rollback.");
            Require(first.FilePath == originalPath && File.Exists(first.FilePath) && File.ReadAllText(second.FilePath) == "second audio bytes" && !Directory.GetFiles(directory, ".htunes-rename-*").Any(), "Rollback restores every file/path and clears owned staging names.");
            var ghost = new Track { FilePath = Path.Combine(directory, "a missing.MP3") };
            Require(LibraryRenameService.Preview([first], [first, ghost], new RenameOptions(Append: true, Suffix: " missing"), false).Single().Error is not null, "A destination claimed by another library entry remains protected even if its file is absent.");
            CheckRenameSwap(directory);
            CheckRenameTitles(directory);
            CheckRenameWorkspace(directory);
        });
    }

    private static void CheckRenameSwap(string directory)
    {
        var one = new Track { Artist = "A", Album = "X", Title = "Two", FilePath = Path.Combine(directory, "A - X - One.mp3") };
        var two = new Track { Artist = "A", Album = "X", Title = "One", FilePath = Path.Combine(directory, "A - X - Two.mp3") };
        File.WriteAllText(one.FilePath, "ONE"); File.WriteAllText(two.FilePath, "TWO");
        var edit = LibraryRenameService.Apply([one, two], [one, two], new RenameOptions(RenameMode.MetadataFileName), false, () => { });
        Require(File.ReadAllText(one.FilePath) == "ONE" && File.ReadAllText(two.FilePath) == "TWO", "Swaps must stage both files and keep their contents attached to the right tracks.");
        edit.Undo(); Require(Path.GetFileName(one.FilePath) == "A - X - One.mp3", "Swaps must be undoable.");
    }

    private static void CheckRenameTitles(string directory)
    {
        var first = new Track { FilePath = Path.Combine(directory, "First.display.wav"), Title = "Old first", Artist = "Keep artist", PlayCount = 4 };
        var second = new Track { FilePath = Path.Combine(directory, "Second display.wav"), Title = "Old second" };
        foreach (var track in new[] { first, second }) { File.WriteAllBytes(track.FilePath, SyntheticWav()); using var audio = TagLib.File.Create(track.FilePath); audio.Tag.Title = track.Title; audio.Tag.Performers = [track.Artist]; audio.Save(); }
        var edit = LibraryRenameService.Apply([first, second], [first, second], new RenameOptions(RenameMode.FileNameToTitle), true, () => { });
        Require(first.Title == "First.display" && second.Title == "Second display" && File.Exists(first.FilePath), "Each title comes from its own filename, excluding only the extension.");
        using (var audio = TagLib.File.Create(first.FilePath)) Require(audio.Tag.Title == first.Title && audio.Tag.FirstPerformer == "Keep artist", "Title mode writes only the title tag, preserving unrelated metadata.");
        first.PlayCount = 9; edit.Undo(); Require(first.Title == "Old first" && first.PlayCount == 9, "Title undo must not rewind play counts.");
        edit.Redo(); Require(first.Title == "First.display", "Title redo must work.");
        var missing = new Track { FilePath = Path.Combine(directory, "Missing track.wav"), Title = "Old" };
        LibraryRenameService.Apply([missing], [missing], new RenameOptions(RenameMode.FileNameToTitle), false, () => { });
        Require(missing.Title == "Missing track", "Library-only title mode must work for missing files.");
    }

    private static void CheckRenameWorkspace(string directory)
    {
        var libraryFile = Path.Combine(directory, "rename-library.json");
        var window = new MainWindow(false, libraryFile);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var fields = (Dictionary<string, (CheckBox Check, TextBox Value, TextBox? Second)>)typeof(MainWindow).GetField("renameFields", flags)!.GetValue(window)!;
        var titles = new[] { "Northbound", "Golden Hour", "Signal Fires", "Open Road", "Evening Light", "Paper Planes", "Still Water", "Sunday Morning", "Blue Horizon", "After the Rain", "Last Train", "Home Again" };
        var tracks = titles.Select((title, index) => new Track { Title = title, Artist = index < 4 ? "The Coastline" : "Juniper Lane", Album = index < 4 ? "Northbound" : "Quiet Hours", FilePath = Path.Combine(directory, $"{index + 1:00}_{title.Replace(' ', '_')} [demo]" + (index < 4 ? ".flac" : ".mp3")) }).ToList();
        try
        {
            foreach (var track in tracks) File.WriteAllText(track.FilePath, "Sample audio for rename checks");
            Set("allTracks", tracks); Set("isRenameView", true); Control<RadioButton>("RenameTab").IsChecked = true;
            window.Playlists.Add(new Playlist { Name = "Keep membership", TrackIds = tracks.Take(4).Select(t => t.Id).ToList() });
            Control<FrameworkElement>("MusicWorkspace").Visibility = Visibility.Collapsed; Control<FrameworkElement>("RenameWorkspace").Visibility = Visibility.Visible;
            Call("RefreshRenameLibrary"); Call("UpdateDeviceStripMode");
            var grid = Control<DataGrid>("RenameTracksGrid");
            foreach (var row in grid.Items.Cast<RenameGridRow>().Take(4)) grid.SelectedItems.Add(row);
            fields["Replace"].Value.Text = "_"; fields["Replace"].Second!.Text = " ";
            fields["Remove"].Value.Text = " [demo]"; fields["Front"].Value.Text = "3"; fields["Prepend"].Value.Text = "The Coastline - ";
            Require(grid.SelectedItems.Count == 4 && Control<Button>("RenameApplyButton").IsEnabled, "The Rename tab must support selected-only live previews.");
            var rows = grid.Items.Cast<RenameGridRow>().ToList();
            Require(rows[0].Proposed == "The Coastline - Northbound.flac" && rows[4].Status == "Not in scope", "Only selected rows should show the transformed filename.");
            Set("isTagSaving", true); Call("UpdateBusyWorkspaces");
            Require(Control<FrameworkElement>("RenameWorkspace").IsEnabled && !Control<Button>("RenameApplyButton").IsEnabled, "Tag saves must keep browsing available while preventing a conflicting rename write.");
            Set("isTagSaving", false); Call("UpdateBusyWorkspaces");
            Control<TextBlock>("RenameStatus").Text = "Preview with sample tracks • 4 files will be renamed. Extensions and audio contents stay unchanged.";
            CaptureRenameScreenshot(window, "rename-editor");
            Control<TextBox>("RenameSearchBox").Text = "Northbound";
            Control<ComboBox>("RenameScopeCombo").SelectedIndex = 2;
            Require(((List<Track>)Call("RenameTargets")!).Count == 12, "Entire-library scope must include hidden tracks.");
            Control<ComboBox>("RenameScopeCombo").SelectedIndex = 1;
            Require(((List<Track>)Call("RenameTargets")!).Count == 4, "Filtered scope must contain only visible rows.");
            Control<ComboBox>("RenameScopeCombo").SelectedIndex = 0; Call("SelectAllActiveItems");
            Require(grid.SelectedItems.Count == 4, "Ctrl+A selects visible Rename rows.");
            RunUiTask(async () => { Call("RenameApply_Click", window, new RoutedEventArgs()); while ((bool)typeof(MainWindow).GetField("isRenaming", flags)!.GetValue(window)!) await Task.Delay(10); });
            Require(Control<TextBlock>("RenameStatus").Text.StartsWith("Applied 4 changes"), "Apply must rename through the actual UI handler.");
            var saved = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(libraryFile))!;
            Require(saved.Tracks.Count == 12 && saved.Tracks.All(t => File.Exists(t.FilePath)) && saved.Tracks.Take(4).All(t => Path.GetFileName(t.FilePath).StartsWith("The Coastline - ")), "Apply must persist all changed paths without dropping unselected tracks.");
            Require(saved.Playlists.Single().TrackIds.SequenceEqual(tracks.Take(4).Select(t => t.Id)), "Playlist membership must survive the persisted rename batch.");
            Call("RenameReset_Click", window, new RoutedEventArgs());
            Require(!Control<Button>("RenameApplyButton").IsEnabled, "Reset clears operations without changing files.");
            Control<ComboBox>("RenameModeCombo").SelectedIndex = 2;
            Control<CheckBox>("RenameWriteTitles").IsChecked = false;
            Require(!Control<FrameworkElement>("RenameRulesPanel").IsEnabled && Control<CheckBox>("RenameWriteTitles").Visibility == Visibility.Visible && grid.Items.Cast<RenameGridRow>().First().Proposed == "The Coastline - Northbound", "Title mode previews the stem and disables filename rules.");
        }
        finally { window.Close(); }
    }

    private static void CaptureRenameScreenshot(MainWindow window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("HTUNES_UI_CHECK_OUTPUT"); if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var root = (Grid)window.Content; root.Background = window.Background;
        root.Measure(new Size(1440, 900)); root.Arrange(new Rect(0, 0, 1440, 900)); root.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
}
