using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HTunes.App;

internal sealed class RenameGridRow(Track track) : INotifyPropertyChanged
{
    public Track Track { get; } = track;
    public string CurrentName => Path.GetFileName(Track.FilePath);
    public string Extension => Path.GetExtension(Track.FilePath);
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string Album => Track.Album;
    public string DirectoryName => Path.GetDirectoryName(Track.FilePath) ?? "";
    private RenamePreview? preview;
    public string Proposed => preview?.Proposed ?? CurrentName;
    public string Status => preview?.Status ?? "Not in scope";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(RenamePreview? value) { preview = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
}

public partial class MainWindow
{
    private bool isRenameView;
    private bool isRenaming;
    private bool refreshingRename;
    private bool renameReady;
    private readonly Dictionary<string, (CheckBox Check, TextBox Value, TextBox? Second)> renameFields = [];
    private List<RenamePreview> renamePreview = [];

    private void InitializeRenameEditor()
    {
        foreach (var (key, label, initial) in new[] { ("Replace", "1. Replace text", ""), ("Remove", "2. Remove text", ""), ("Front", "3. Trim front characters", "0"), ("End", "4. Trim end characters", "0"), ("Prepend", "5. Prepend text", ""), ("Append", "6. Append text", "") })
        {
            var check = new CheckBox { Content = label, Margin = new Thickness(0, 0, 0, 4) };
            var box = new TextBox { Text = initial, Padding = new Thickness(6, 3, 6, 3), ToolTip = label };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            TextBox? second = key == "Replace" ? new TextBox { Padding = new Thickness(6, 3, 6, 3), ToolTip = "Replacement text (empty removes the match)" } : null;
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) }; panel.Children.Add(check);
            if (second is null) panel.Children.Add(box);
            else
            {
                var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new());
                grid.Children.Add(box); var arrow = new TextBlock { Text = " → ", VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(arrow, 1); grid.Children.Add(arrow); Grid.SetColumn(second, 2); grid.Children.Add(second); panel.Children.Add(grid);
                System.Windows.Automation.AutomationProperties.SetName(second, "Replacement text");
            }
            renameFields[key] = (check, box, second); RenameRulesPanel.Children.Add(panel);
            check.Checked += RenameOptions_Changed; check.Unchecked += RenameOptions_Changed;
            box.TextChanged += (_, _) => { if (!refreshingRename && renameReady) { check.IsChecked = true; RefreshRenamePreview(); } };
            if (second is not null) second.TextChanged += (_, _) => { if (!refreshingRename && renameReady) { check.IsChecked = true; RefreshRenamePreview(); } };
        }
        renameReady = true;
        AttachItemMenu(RenameTracksGrid, BuildRenameMenu);
        RefreshRenameLibrary();
    }

    private List<Track> RenameSelection => RenameTracksGrid.SelectedItems.Cast<RenameGridRow>().Select(r => r.Track).ToList();
    private List<Track> RenameTargets() => RenameScopeCombo.SelectedIndex switch
    {
        2 => allTracks.ToList(),
        1 => RenameTracksGrid.Items.Cast<RenameGridRow>().Select(r => r.Track).ToList(),
        _ => RenameSelection
    };

    private RenameOptions ReadRenameOptions()
    {
        bool Enabled(string key) => renameFields[key].Check.IsChecked == true;
        string Value(string key) => renameFields[key].Value.Text;
        var mode = (RenameMode)Math.Max(0, RenameModeCombo.SelectedIndex);
        int Count(string key)
        {
            if (mode == RenameMode.FileNameToTitle || !Enabled(key)) return 0;
            if (!int.TryParse(Value(key), out var count)) throw new ArgumentException("Trim counts must be whole numbers.");
            return count;
        }
        return new(mode, Enabled("Replace"), Value("Replace"), renameFields["Replace"].Second!.Text, Enabled("Remove"), Value("Remove"),
            Enabled("Front"), Count("Front"), Enabled("End"), Count("End"), Enabled("Prepend"), Value("Prepend"), Enabled("Append"), Value("Append"), RenameIgnoreCase.IsChecked == true);
    }

    private void RefreshRenameLibrary()
    {
        if (!renameReady) return;
        var selected = RenameSelection.Select(t => t.Id).ToHashSet();
        var search = RenameSearchBox.Text.Trim();
        var tracks = allTracks.Where(t => MatchesSearch(t, search) || t.FilePath.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        var sorts = RenameTracksGrid.Items.SortDescriptions.ToList();
        refreshingRename = true;
        try
        {
            RenameTracksGrid.ItemsSource = tracks.Select(t => new RenameGridRow(t)).ToList();
            RenameTracksGrid.Items.SortDescriptions.Clear();
            foreach (var sort in sorts) RenameTracksGrid.Items.SortDescriptions.Add(sort);
            foreach (var row in RenameTracksGrid.Items.Cast<RenameGridRow>().Where(r => selected.Contains(r.Track.Id))) RenameTracksGrid.SelectedItems.Add(row);
        }
        finally { refreshingRename = false; }
        RefreshRenamePreview();
    }
    private void RenameSearch_Changed(object sender, TextChangedEventArgs e) { if (renameReady) RefreshRenameLibrary(); }
    private void RenameSelection_Changed(object sender, SelectionChangedEventArgs e) { if (renameReady && !refreshingRename) RefreshRenamePreview(); }
    private void RenameOptions_Changed(object sender, RoutedEventArgs e) { if (renameReady && !refreshingRename) RefreshRenamePreview(); }

    private void RefreshRenamePreview()
    {
        if (!renameReady || refreshingRename) return;
        var titleMode = RenameModeCombo.SelectedIndex == (int)RenameMode.FileNameToTitle;
        RenameProposedColumn.Header = titleMode ? "New track title" : "New filename";
        RenameRulesPanel.IsEnabled = RenameIgnoreCase.IsEnabled = !titleMode;
        RenameWriteTitles.Visibility = titleMode ? Visibility.Visible : Visibility.Collapsed;
        RenameModeHint.Text = titleMode ? "Copy the filename without its extension into the track title. Files are not renamed."
            : RenameModeCombo.SelectedIndex == 1 ? "Start with Artist - Album - Track Title. Invalid characters in tags become underscores. Then apply rules below."
            : "Rules run top to bottom, before the extension.";
        RenameApplyButton.Content = titleMode ? "Apply track titles" : "Apply renames";
        var targets = RenameTargets();
        try
        {
            renamePreview = LibraryRenameService.Preview(targets, allTracks, ReadRenameOptions(), RenameWriteTitles.IsChecked == true);
            var changed = renamePreview.Count(r => r.Changed && r.Error is null);
            var conflicts = renamePreview.Count(r => r.Error is not null);
            RenameScopeSummary.Text = $"{targets.Count:N0} in scope • {changed:N0} changes • {conflicts:N0} conflicts" + (RenameScopeCombo.SelectedIndex == 2 ? "\nEntire library, including filtered-out tracks." : "");
            RenameScopeSummary.Foreground = conflicts > 0 ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DimGray;
        }
        catch (Exception ex)
        {
            renamePreview = [];
            RenameScopeSummary.Text = ex.Message;
            RenameScopeSummary.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
        var mapping = renamePreview.ToDictionary(r => r.Track.Id);
        foreach (var row in RenameTracksGrid.Items.Cast<RenameGridRow>()) row.Update(mapping.GetValueOrDefault(row.Track.Id));
        RenameLibrarySummary.Text = $"{RenameTracksGrid.Items.Count:N0} of {allTracks.Count:N0} library tracks • {RenameSelection.Count:N0} selected";
        RenameEmptyState.Visibility = RenameTracksGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRenameControls();
    }

    private void UpdateRenameControls()
    {
        if (!renameReady) return;
        RenameApplyButton.IsEnabled = ContextActionsAvailable && renamePreview.Any(r => r.Changed) && renamePreview.All(r => r.Error is null);
        RenameResetButton.IsEnabled = ContextActionsAvailable;
    }
    private void RenameReset_Click(object sender, RoutedEventArgs e)
    {
        refreshingRename = true;
        try
        {
            foreach (var (key, field) in renameFields) { field.Check.IsChecked = false; field.Value.Text = key is "Front" or "End" ? "0" : ""; if (field.Second is not null) field.Second.Text = ""; }
            RenameIgnoreCase.IsChecked = false; RenameModeCombo.SelectedIndex = 0;
        }
        finally { refreshingRename = false; }
        RefreshRenamePreview(); RenameStatus.Text = "Rules reset. No files changed.";
    }

    private async void RenameApply_Click(object sender, RoutedEventArgs e)
    {
        if (!ContextActionsAvailable) return;
        RefreshRenamePreview();
        if (!RenameApplyButton.IsEnabled) return;
        var targets = RenameTargets(); var options = ReadRenameOptions(); var writeTitles = RenameWriteTitles.IsChecked == true;
        if (RenameScopeCombo.SelectedIndex == 2 && MessageBox.Show(this, $"Apply {renamePreview.Count(r => r.Changed):N0} changes across the entire library? This includes tracks hidden by the search filter.", "Apply to entire library", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (player.Source is { IsFile: true } source && targets.Any(t => Same(t.FilePath, source.LocalPath))) { player.Stop(); player.Close(); }
        isRenaming = true; UpdateBusyWorkspaces(); RenameStatus.Text = "Applying changes and updating library paths…";
        var count = renamePreview.Count(r => r.Changed);
        try
        {
            var edit = await Task.Run(() => LibraryRenameService.Apply(targets, allTracks, options, writeTitles, () => Dispatcher.Invoke(SaveLibrary)));
            RecordEdit("Rename / filename titles", edit.Undo, edit.Redo);
            RefreshBrowser(); RefreshRenameLibrary();
            RenameStatus.Text = $"Applied {count:N0} changes. Library saved; playlists preserved. Use Edit → Undo to reverse this batch.";
            DebugLog.Write("Rename", $"Applied {count} changes; mode={options.Mode}; scope={targets.Count}");
        }
        catch (Exception ex)
        {
            RefreshRenameLibrary(); RenameStatus.Text = "Rename failed. " + ex.Message;
            DebugLog.Write("Rename", "Batch failed", ex);
            MessageBox.Show(this, ex.ToString(), "Could not apply renames", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { isRenaming = false; if (initializeServices) RefreshDevice(); UpdateBusyWorkspaces(); }
    }

    private void BuildRenameMenu(ItemsControl menu)
    {
        AddMenuAction(menu, "Apply rename preview", () => Dispatcher.BeginInvoke(new Action(() => RenameApply_Click(this, new RoutedEventArgs()))), renamePreview.Any(r => r.Changed) && renamePreview.All(r => r.Error is null));
        AddMenuAction(menu, "Reset rename rules", () => RenameReset_Click(this, new RoutedEventArgs()));
        if (RenameSelection.Count == 1) AddMenuAction(menu, "Show file in Explorer", () => RevealFile(RenameSelection[0].FilePath), File.Exists(RenameSelection[0].FilePath));
    }
}
