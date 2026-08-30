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
            Console.WriteLine("PASS: list/grid multi-selection, target replacement, empty-space clearing, and menu attachment.");
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
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
