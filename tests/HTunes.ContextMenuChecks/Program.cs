using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HTunes.App;

internal static class Program
{
    private static readonly MethodInfo SelectTarget = typeof(MainWindow).GetMethod("SelectContextItem", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo AttachMenu = typeof(MainWindow).GetMethod("AttachItemMenu", BindingFlags.NonPublic | BindingFlags.Static)!;

    [STAThread]
    private static int Main()
    {
        try
        {
            // Exercise controls without constructing MainWindow, opening a window, reading user data,
            // or connecting to an iPod. This exercises the actual selection helper used by every menu.
            CheckListSelection();
            CheckGridSelection();
            CheckHistory();
            CheckMetadataHistoryIsolation();
            CheckTopMenuRebuild();
            Console.WriteLine("PASS: menu selection, top-menu refresh, undo/redo, history limits, failure handling, and play-count isolation.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckListSelection()
    {
        var list = new ListBox { SelectionMode = SelectionMode.Extended };
        list.Items.Add("First"); list.Items.Add("Second"); list.Items.Add("Third");
        list.SelectedItems.Add("First"); list.SelectedItems.Add("Second");
        Select(list, "Second");
        Require(list.SelectedItems.Count == 2, "Right-click must preserve an existing list selection.");
        Select(list, "Third");
        Require(list.SelectedItems.Count == 1 && Equals(list.SelectedItem, "Third"), "An unselected list item must become the only target.");
        Select(list, null);
        Require(list.SelectedItems.Count == 0, "Empty list space must clear the target.");
        AttachMenu.Invoke(null, [list, (Action<ContextMenu>)(menu => menu.Items.Add(new MenuItem { Header = "Test" }))]);
        Require(list.ContextMenu is not null, "Attach a menu before the first mouse or keyboard opening.");
    }

    private static void CheckGridSelection()
    {
        var grid = new DataGrid { AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow };
        grid.Columns.Add(new DataGridTextColumn { Header = "Title", Binding = new System.Windows.Data.Binding("Title") });
        var first = new PodcastEpisode { Title = "First" };
        var second = new PodcastEpisode { Title = "Second" };
        var third = new PodcastEpisode { Title = "Third" };
        grid.ItemsSource = new[] { first, second, third };
        grid.SelectedItems.Add(first); grid.SelectedItems.Add(second);
        Select(grid, second);
        Require(grid.SelectedItems.Count == 2, "Right-click must preserve the selected grid rows.");
        Select(grid, third);
        Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItem, third), "An unselected grid row must replace the old targets.");
        Select(grid, null);
        Require(grid.SelectedItems.Count == 0, "Empty grid space must not act on stale rows.");
    }

    private static void Select(ItemsControl control, object? target) => SelectTarget.Invoke(null, [control, target]);

    private static void CheckHistory()
    {
        var history = new EditHistory(2);
        var value = 1;
        history.Record("first", () => value = 0, () => value = 1);
        Require(history.CanUndo && !history.CanRedo && history.UndoDescription == "first", "Record must expose an undo description.");
        history.Undo();
        Require(value == 0 && history.CanRedo && !history.CanUndo, "Undo must move the edit to redo.");
        history.Redo();
        Require(value == 1 && history.CanUndo && !history.CanRedo, "Redo must replay the edit.");
        value = 2;
        history.Record("second", () => value = 1, () => value = 2);
        value = 3;
        history.Record("third", () => value = 2, () => value = 3);
        history.Undo(); history.Undo(); history.Undo();
        Require(value == 1 && !history.CanUndo, "History must evict edits beyond its limit.");
        history.Record("new branch", () => value = 1, () => value = 4);
        Require(!history.CanRedo, "A new edit after undo must discard the old redo branch.");

        var failing = new EditHistory();
        failing.Record("failure", () => throw new InvalidOperationException("expected"), () => { });
        try { failing.Undo(); }
        catch (InvalidOperationException) { }
        Require(failing.CanUndo && !failing.CanRedo, "A failed undo must not remove its history entry.");
    }

    private static void CheckMetadataHistoryIsolation()
    {
        var track = new Track { Title = "Original", PlayCount = 3, FilePath = "unchanged.mp3" };
        var type = typeof(MainWindow).GetNestedType("TrackMetadata", BindingFlags.NonPublic)!;
        var read = type.GetMethod("Read")!;
        var apply = type.GetMethod("Apply")!;
        var before = read.Invoke(null, [track]);
        track.Title = "Edited";
        var after = read.Invoke(null, [track]);
        var history = new EditHistory();
        history.Record("metadata", () => apply.Invoke(before, [track]), () => apply.Invoke(after, [track]));
        track.PlayCount = 7; // Listening after an edit is not itself part of edit history.
        history.Undo();
        Require(track.Title == "Original" && track.PlayCount == 7 && track.FilePath == "unchanged.mp3", "Undo metadata must preserve playback counts and file identity.");
        history.Redo();
        Require(track.Title == "Edited" && track.PlayCount == 7, "Redo metadata must preserve later playback counts.");
    }

    private static void CheckTopMenuRebuild()
    {
        var attach = typeof(MainWindow).GetMethod("AttachTopMenu", BindingFlags.NonPublic | BindingFlags.Static)!;
        var menu = new MenuItem { Header = "Edit" };
        menu.Items.Add(new MenuItem { Header = "Placeholder" });
        var count = 0;
        attach.Invoke(null, [menu, (Action<ItemsControl>)(target => { count++; target.Items.Add(new MenuItem { Header = $"Action {count}" }); })]);
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));
        Require(count == 1 && menu.Items.Count == 1 && Equals(((MenuItem)menu.Items[0]).Header, "Action 1"), "Top menu must rebuild its actions when opened.");
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu.Items[0]));
        Require(count == 1, "Opening a nested submenu must not rebuild its parent.");
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));
        Require(count == 2 && menu.Items.Count == 1, "Reopening must replace, not duplicate, menu actions.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
