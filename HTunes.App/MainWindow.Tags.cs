using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HTunes.App;

public partial class MainWindow
{
    private bool isTagView;
    private bool isTagSaving;
    private bool refreshingTagInspector;
    private bool refreshingTagGrid;
    private bool tagArtworkChanged;
    private string? tagPendingArtwork;
    private readonly Dictionary<string, (CheckBox Check, TextBox Box)> tagFields = [];

    private void InitializeTagEditor()
    {
        var numeric = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var (field, label) in new[] { ("Title", "Title"), ("Artist", "Artist"), ("AlbumArtist", "Album artist (optional)"), ("Album", "Album"), ("Genre", "Genre"), ("TrackNumber", "Track #"), ("DiscNumber", "Disc #"), ("Year", "Year") })
        {
            var check = new CheckBox { Content = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 3), IsTabStop = false };
            var box = new TextBox { Padding = new Thickness(6, 3, 6, 3), MinWidth = 40, Tag = field };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) }; panel.Children.Add(check); panel.Children.Add(box);
            tagFields[field] = (check, box);
            box.TextChanged += (_, _) => { if (!refreshingTagInspector) check.IsChecked = true; UpdateTagControls(); };
            if (field is "Artist" or "AlbumArtist" or "Album" or "Genre")
            {
                var property = typeof(Track).GetProperty(field)!;
                TextBoxAutoComplete.Attach(box, () => allTracks
                    .Select(track => property.GetValue(track)?.ToString() ?? "")
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase));
            }
            check.Checked += TagOptionChanged; check.Unchecked += TagOptionChanged;
            if (field is "TrackNumber" or "DiscNumber" or "Year") { panel.Margin = new Thickness(0, 0, 5, 0); numeric.Children.Add(panel); }
            else TagFieldsPanel.Children.Add(panel);
        }
        TagFieldsPanel.Children.Add(numeric);
        AttachItemMenu(TagTracksGrid, BuildTagMenu);
        RefreshTagLibrary();
    }

    private List<Track> TagSelection => TagTracksGrid.SelectedItems.Cast<Track>().ToList();
    private bool TagHasPendingChanges => tagArtworkChanged || TagResizeArtwork.IsChecked == true || tagFields.Values.Any(f => f.Check.IsChecked == true);

    private void RefreshTagLibrary()
    {
        if (TagTracksGrid is null || tagFields.Count == 0) return;
        var selected = TagSelection.Select(t => t.Id).ToHashSet();
        var search = TagSearchBox.Text.Trim();
        var rows = allTracks.Where(t => MatchesSearch(t, search) || t.FilePath.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        if (TagHasPendingChanges && rows.Select(t => t.Id).Order().SequenceEqual(TagTracksGrid.Items.Cast<Track>().Select(t => t.Id).Order()))
        {
            TagTracksGrid.Items.Refresh(); return; // Keep the current draft when revisiting the tab or refreshing another workspace.
        }
        var sorts = TagTracksGrid.Items.SortDescriptions.ToList();
        if (sorts.Count == 0)
            sorts = [new(nameof(Track.Genre), ListSortDirection.Ascending), new(nameof(Track.Artist), ListSortDirection.Ascending),
                new(nameof(Track.Album), ListSortDirection.Ascending), new(nameof(Track.TrackNumber), ListSortDirection.Ascending)];
        var scroller = FindVisualChild<ScrollViewer>(TagTracksGrid);
        var verticalOffset = scroller?.VerticalOffset ?? 0;
        refreshingTagGrid = true;
        try
        {
            TagTracksGrid.ItemsSource = rows;
            TagTracksGrid.Items.SortDescriptions.Clear();
            foreach (var sort in sorts) TagTracksGrid.Items.SortDescriptions.Add(sort);
            foreach (var row in rows.Where(t => selected.Contains(t.Id))) TagTracksGrid.SelectedItems.Add(row);
        }
        finally { refreshingTagGrid = false; }
        if (scroller is not null) Dispatcher.BeginInvoke(new Action(() => scroller.ScrollToVerticalOffset(verticalOffset)), System.Windows.Threading.DispatcherPriority.Loaded);
        TagLibrarySummary.Text = $"{rows.Count:N0} of {allTracks.Count:N0} library tracks • {TagSelection.Count:N0} selected";
        TagEmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadTagInspector();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void TagSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (tagFields.Count == 0 || refreshingTagInspector) return;
        if (TagHasPendingChanges) TagStatus.Text = "Draft reset because the filter changed. Apply edits before changing the selection or filter.";
        RefreshTagLibrary();
    }
    private void TagSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingTagGrid || tagFields.Count == 0) return;
        if (TagHasPendingChanges) TagStatus.Text = "Draft reset for the new selection. Apply edits before selecting different tracks.";
        LoadTagInspector();
    }

    private void LoadTagInspector()
    {
        refreshingTagInspector = true;
        var selection = TagSelection;
        try
        {
            tagArtworkChanged = false; tagPendingArtwork = null;
            TagResizeArtwork.IsChecked = false; TagCropArtwork.IsChecked = false;
            foreach (var (field, controls) in tagFields)
            {
                var values = selection.Select(t => typeof(Track).GetProperty(field)!.GetValue(t)?.ToString() ?? "").Distinct().ToList();
                controls.Box.Text = values.Count == 1 ? values[0] : "";
                controls.Box.ToolTip = values.Count > 1 ? "Mixed values — leave unchecked to keep each track's value. Check and leave blank to clear a text field." : "Check to apply this value to the selection. Blank clears a text field; 0 clears a number.";
                controls.Check.Content = field switch { "TrackNumber" => "Track #", "DiscNumber" => "Disc #", _ => field };
                if (values.Count > 1) controls.Check.Content += field is "TrackNumber" or "DiscNumber" or "Year" ? " *" : " (mixed)";
                controls.Check.ToolTip = values.Count > 1 ? "Mixed values. Only checked fields will be applied." : "Check this field to apply it to every selected track.";
                controls.Check.IsChecked = false;
            }
            TagSelectionSummary.Text = selection.Count == 0 ? "Select tracks to edit" : $"{selection.Count:N0} track{(selection.Count == 1 ? "" : "s")} selected";
            TagLibrarySummary.Text = $"{TagTracksGrid.Items.Count:N0} of {allTracks.Count:N0} library tracks • {selection.Count:N0} selected";
            var artwork = selection.Select(t => t.ArtworkPath).Distinct().ToList();
            ShowTagArtwork(artwork.FirstOrDefault(p => p is not null && File.Exists(p)), artwork.Count > 1);
            if (selection.Any(t => !File.Exists(t.FilePath))) TagStatus.Text = "Selection includes missing files. Locate them or disable file writing for a library-only edit.";
        }
        finally { refreshingTagInspector = false; UpdateTagControls(); }
    }

    private void ShowTagArtwork(string? path, bool mixed = false)
    {
        TagArtworkImage.Source = null;
        TagArtworkPlaceholder.Visibility = Visibility.Visible;
        TagArtworkInfo.Text = mixed ? "Mixed artwork • first shown" : "No artwork";
        if (path is null) return;
        try
        {
            var bitmap = TagArtwork.Read(path); TagArtworkImage.Source = bitmap;
            TagArtworkPlaceholder.Visibility = Visibility.Collapsed;
            TagArtworkInfo.Text = (mixed ? "Mixed • preview " : "") + $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px";
        }
        catch (Exception ex) { TagArtworkInfo.Text = "Cannot preview artwork"; DebugLog.Write("Tag artwork", "Preview failed", ex); }
    }
    private void TagUploadArtwork_Click(object sender, RoutedEventArgs e)
    {
        if (!ContextActionsAvailable || TagSelection.Count == 0) return;
        var dialog = new OpenFileDialog { Title = "Upload artwork from your computer", Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" };
        if (dialog.ShowDialog(this) != true) return;
        try { _ = TagArtwork.Read(dialog.FileName); tagPendingArtwork = dialog.FileName; tagArtworkChanged = true; ShowTagArtwork(tagPendingArtwork); TagStatus.Text = "Artwork replacement ready. Apply to save it to the selected tracks."; UpdateTagControls(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not read artwork"); }
    }
    private void TagRemoveArtwork_Click(object sender, RoutedEventArgs e)
    {
        if (!ContextActionsAvailable || TagSelection.Count == 0) return;
        tagArtworkChanged = true; tagPendingArtwork = null; TagResizeArtwork.IsChecked = false; TagCropArtwork.IsChecked = false;
        ShowTagArtwork(null); TagStatus.Text = "Artwork removal ready. Apply to remove it from the selected tracks."; UpdateTagControls();
    }
    private void TagOptionChanged(object sender, RoutedEventArgs e) { if (!refreshingTagInspector) UpdateTagControls(); }
    private void TagSizeChanged(object sender, TextChangedEventArgs e) { if (!refreshingTagInspector && TagResizeArtwork is not null) UpdateTagControls(); }
    private void TagReset_Click(object sender, RoutedEventArgs e) { LoadTagInspector(); TagStatus.Text = "Draft reset. No changes saved."; }

    private void UpdateTagControls()
    {
        if (TagApplyButton is null || TagInspector is null || TagTracksGrid is null || tagFields.Count == 0) return;
        var available = ContextActionsAvailable && TagSelection.Count > 0;
        TagInspector.IsEnabled = TagWriteFiles.IsEnabled = available;
        TagApplyButton.IsEnabled = available && TagHasPendingChanges;
        TagAutoButton.IsEnabled = available;
        TagResetButton.IsEnabled = available;
    }

    private async void TagApply_Click(object sender, RoutedEventArgs e)
    {
        if (!ContextActionsAvailable || !TagHasPendingChanges) return;
        var selected = TagSelection;
        TagPatch patch;
        try
        {
            var resize = TagResizeArtwork.IsChecked == true;
            if (resize && (!int.TryParse(TagArtworkWidth.Text, out _) || !int.TryParse(TagArtworkHeight.Text, out _))) throw new ArgumentException("Enter whole-number artwork dimensions.");
            patch = new TagPatch(tagFields.Where(f => f.Value.Check.IsChecked == true).ToDictionary(f => f.Key, f => f.Value.Box.Text), tagArtworkChanged, tagPendingArtwork,
                resize, resize ? int.Parse(TagArtworkWidth.Text) : 600, resize ? int.Parse(TagArtworkHeight.Text) : 600, CropArtwork: TagCropArtwork.IsChecked == true);
            patch.Validate();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check tag values"); return; }
        var writeFiles = TagWriteFiles.IsChecked == true;
        if (writeFiles && player.Source is { IsFile: true } source && selected.Any(t => Same(t.FilePath, source.LocalPath))) { player.Stop(); player.Close(); }
        isTagSaving = true; UpdateBusyWorkspaces();
        TagStatus.Text = $"Saving tags for {selected.Count:N0} tracks…";
        try
        {
            var edit = await Task.Run(() => TagBatchEdit.Apply(selected, patch, writeFiles, Path.Combine(SettingsStore.DataDirectory, "artwork"), () => Dispatcher.Invoke(SaveLibrary)));
            var removed = DropMissingTrackEntries(edit.MissingFiles);
            if (removed is { } drop)
                RecordEdit("Tag selected tracks", () => { edit.Undo(); drop.Undo(); }, () => { edit.Redo(); drop.Redo(); });
            else
                RecordEdit("Tag selected tracks", edit.Undo, edit.Redo);
            LoadTagInspector(); RefreshBrowser(); RefreshTagLibrary();
            var saved = selected.Count - edit.MissingFiles.Count;
            TagStatus.Text = $"Saved {saved:N0} track{(saved == 1 ? "" : "s")} to the library{(writeFiles ? " and audio files" : " only")}." +
                (edit.MissingFiles.Count > 0 ? $" Removed {edit.MissingFiles.Count:N0} track{(edit.MissingFiles.Count == 1 ? "" : "s")} with missing files from the library." : "") +
                " Undo is available in Edit (Ctrl+Z).";
            DebugLog.Write("Tag editor", $"Saved tracks={selected.Count}; writeFiles={writeFiles}; artwork={patch.ChangeArtwork || patch.ResizeArtwork}");
        }
        catch (Exception ex)
        {
            TagStatus.Text = "Tag save failed. " + ex.Message;
            DebugLog.Write("Tag editor", "Batch failed", ex);
            MessageBox.Show(this, ex.ToString(), "Could not save tags", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { isTagSaving = false; if (initializeServices) RefreshDevice(); UpdateBusyWorkspaces(); }
    }

    // Tracks whose audio file has vanished from disk can't be tagged or synced; drop those
    // stale library entries (and their playlist references) instead of failing the whole
    // operation. Returns an undo/redo pair, or null when nothing was removed.
    private (Action Undo, Action Redo)? DropMissingTrackEntries(IReadOnlyList<Track> missing)
    {
        if (missing.Count == 0) return null;
        var ids = missing.Select(track => track.Id).ToHashSet();
        var tracksBefore = allTracks.ToList();
        var membershipsBefore = Playlists.ToDictionary(playlist => playlist, playlist => playlist.TrackIds.ToList());
        allTracks.RemoveAll(track => ids.Contains(track.Id));
        foreach (var playlist in Playlists) playlist.TrackIds.RemoveAll(ids.Contains);
        var tracksAfter = allTracks.ToList();
        var membershipsAfter = Playlists.ToDictionary(playlist => playlist, playlist => playlist.TrackIds.ToList());
        SaveLibrary();
        return (
            () => { allTracks = tracksBefore.ToList(); foreach (var (playlist, members) in membershipsBefore) playlist.TrackIds = members.ToList(); SaveLibrary(); },
            () => { allTracks = tracksAfter.ToList(); foreach (var (playlist, members) in membershipsAfter) playlist.TrackIds = members.ToList(); SaveLibrary(); });
    }

    // Shared entry point for auto-tag and sync: remove tracks whose audio file no longer
    // exists on disk from the library, record the removal on the undo stack, and refresh the
    // browser. Returns how many entries were dropped.
    private int RemoveMissingFilesFromLibrary(IReadOnlyList<Track> missing, string context)
    {
        var present = missing.Where(track => allTracks.Contains(track)).Distinct().ToList();
        if (DropMissingTrackEntries(present) is not { } drop) return 0;
        RecordEdit($"Remove {present.Count} missing file{(present.Count == 1 ? "" : "s")}", drop.Undo, drop.Redo);
        DebugLog.Write("Library", $"Removed {present.Count} track(s) with missing audio files from the library during {context}");
        RefreshBrowser();
        return present.Count;
    }

    private async void TagAuto_Click(object sender, RoutedEventArgs e)
    {
        if (!ContextActionsAvailable || TagSelection.Count == 0) return;
        var selected = TagSelection.ToList();
        var force = TagForceAuto.IsChecked == true;
        var writeFiles = TagWriteFiles.IsChecked == true;
        var edits = new List<TagBatchEdit>();
        var artistGenres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matched = 0; var skipped = 0; var failed = 0; var rateLimited = 0;
        string? lastError = null;
        // A track whose audio file has vanished can't be looked up - drop it from the library
        // rather than counting it as a silent failure.
        var removedMissing = RemoveMissingFilesFromLibrary(selected.Where(track => !File.Exists(track.FilePath)).ToList(), "auto-tag");
        selected = selected.Where(track => File.Exists(track.FilePath)).ToList();
        var vanished = new List<Track>();
        isTagSaving = true; UpdateBusyWorkspaces();
        try
        {
            for (var index = 0; index < selected.Count; index++)
            {
                var track = selected[index];
                TagStatus.Text = $"Looking up {index + 1:N0} of {selected.Count:N0}: {track.Title}";
                try
                {
                    var match = await MusicBrainzTagService.FindAsync(track, force, CancellationToken.None);
                    if (match is null) { skipped++; continue; }
                    var fields = match.Fields.ToDictionary(item => item.Key, item => item.Value);
                    var artistKey = fields.GetValueOrDefault("Artist", track.Artist);
                    if (fields.TryGetValue("Genre", out var foundGenre) && !string.IsNullOrWhiteSpace(artistKey)) artistGenres.TryAdd(artistKey, foundGenre);
                    if (!string.IsNullOrWhiteSpace(artistKey) && artistGenres.TryGetValue(artistKey, out var sharedGenre) &&
                        (force || track.Genre.Equals("Music", StringComparison.OrdinalIgnoreCase) || track.Genre.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))) fields["Genre"] = sharedGenre;
                    var patch = new TagPatch(fields, MarkAutoTagged: true);
                    var edit = await Task.Run(() => TagBatchEdit.Apply([track], patch, writeFiles, Path.Combine(SettingsStore.DataDirectory, "artwork"), () => Dispatcher.Invoke(SaveLibrary)));
                    if (edit.MissingFiles.Count > 0) { vanished.Add(track); continue; }
                    edits.Add(edit); matched++;
                }
                catch (MusicBrainzRateLimitException ex)
                {
                    rateLimited++; failed++; lastError = ex.Message;
                    DebugLog.Write("Auto tag", $"Rate limited on track={track.Id}", ex);
                    TagStatus.Text = $"Paused on “{track.Title}” — {ex.Message}";
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    failed++; lastError = ex.GetBaseException().Message;
                    DebugLog.Write("Auto tag", $"Failed track={track.Id}", ex);
                }
            }
            removedMissing += RemoveMissingFilesFromLibrary(vanished, "auto-tag");
            if (edits.Count > 0)
                RecordEdit("Auto-tag selected tracks", () => edits.AsEnumerable().Reverse().ToList().ForEach(edit => edit.Undo()), () => edits.ForEach(edit => edit.Redo()));
            RefreshBrowser(); RefreshTagLibrary();
            var report = $"Auto-tag complete: {matched} matched, {skipped} no confident match, {failed} failed";
            if (removedMissing > 0) report += $", {removedMissing} removed (missing file)";
            if (rateLimited > 0) report += $" ({rateLimited} of them MusicBrainz rate-limits — it allows ~1 request/second per IP; tag fewer tracks at a time or wait a minute)";
            else if (failed > 0 && lastError is not null) report += $". Last error: {lastError}";
            else report += ". MusicBrainz allows ~1 request/second, so large selections take a while.";
            TagStatus.Text = report;
            DebugLog.Write("Auto tag", report);
        }
        finally { isTagSaving = false; if (initializeServices) RefreshDevice(); UpdateBusyWorkspaces(); }
    }

    private void BuildTagMenu(ItemsControl menu)
    {
        AddMenuAction(menu, "Focus tag inspector", () => tagFields["Title"].Box.Focus(), TagSelection.Count > 0);
        // Invoke after the context-menu busy wrapper releases its guard.
        AddMenuAction(menu, "Apply inspector changes", () => Dispatcher.BeginInvoke(new Action(() => TagApply_Click(this, new RoutedEventArgs()))), TagSelection.Count > 0 && TagHasPendingChanges);
        AddMenuAction(menu, "Reset inspector", () => TagReset_Click(this, new RoutedEventArgs()), TagSelection.Count > 0);
        AddMenuAction(menu, "Auto-tag selection", () => Dispatcher.BeginInvoke(new Action(() => TagAuto_Click(this, new RoutedEventArgs()))), TagSelection.Count > 0);
        if (TagSelection.Count == 1) AddMenuAction(menu, "Show file in Explorer", () => RevealFile(TagSelection[0].FilePath), File.Exists(TagSelection[0].FilePath));
    }
}
