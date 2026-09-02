using System.Globalization;
using System.IO;
using System.Text.Json;

namespace HTunes.App;

internal enum RenameMode { FileName, MetadataFileName, FileNameToTitle }

internal sealed record RenameOptions(RenameMode Mode = RenameMode.FileName, bool Replace = false, string Find = "", string Replacement = "",
    bool Remove = false, string RemoveText = "", bool CutBefore = false, string CutBeforeText = "", bool CutAfter = false, string CutAfterText = "",
    bool TrimFront = false, int FrontCount = 0, bool TrimEnd = false, int EndCount = 0,
    bool Prepend = false, string Prefix = "", bool Append = false, string Suffix = "", bool IgnoreCase = false)
{
    public void Validate()
    {
        if (Mode == RenameMode.FileNameToTitle) return;
        if (Replace && Find.Length == 0) throw new ArgumentException("Enter the text to replace.");
        if (Remove && RemoveText.Length == 0) throw new ArgumentException("Enter the text to remove.");
        if (CutBefore && CutBeforeText.Length == 0) throw new ArgumentException("Enter the text to cut before.");
        if (CutAfter && CutAfterText.Length == 0) throw new ArgumentException("Enter the text to cut after.");
        if ((TrimFront && FrontCount is < 0 or > 100000) || (TrimEnd && EndCount is < 0 or > 100000)) throw new ArgumentException("Trim counts must be whole numbers from 0 to 100000.");
    }

    public string Transform(Track track)
    {
        Validate();
        var stem = Path.GetFileNameWithoutExtension(track.FilePath);
        if (Mode == RenameMode.FileNameToTitle) return stem;
        if (Mode == RenameMode.MetadataFileName) stem = string.Join(" - ", new[] { track.Artist, track.Album, track.Title }.Select(SafeMetadata));
        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Replace) stem = stem.Replace(Find, Replacement, comparison);
        if (Remove) stem = stem.Replace(RemoveText, "", comparison);
        if (CutBefore && CutBeforeText.Length > 0 && stem.IndexOf(CutBeforeText, comparison) is var cutBeforeAt and >= 0)
            stem = stem[(cutBeforeAt + CutBeforeText.Length)..];
        if (CutAfter && CutAfterText.Length > 0 && stem.IndexOf(CutAfterText, comparison) is var cutAfterAt and >= 0)
            stem = stem[..cutAfterAt];
        if (TrimFront) stem = Trim(stem, FrontCount, true);
        if (TrimEnd) stem = Trim(stem, EndCount, false);
        if (Prepend) stem = Prefix + stem;
        if (Append) stem += Suffix;
        return stem;
    }

    private static string SafeMetadata(string text) => string.Concat(text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string Trim(string text, int count, bool front)
    {
        var indices = StringInfo.ParseCombiningCharacters(text);
        if (count >= indices.Length) return "";
        return front ? text[indices[count]..] : count == 0 ? text : text[..indices[indices.Length - count]];
    }
}

internal sealed class RenamePreview
{
    public required Track Track { get; init; }
    public string CurrentName => Path.GetFileName(Track.FilePath);
    public string Extension => Path.GetExtension(Track.FilePath);
    public string DirectoryName => Path.GetDirectoryName(Track.FilePath) ?? "";
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string Album => Track.Album;
    public string Proposed { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public bool Changed { get; init; }
    public string? Error { get; set; }
    public string Status => Error is not null ? Error : Changed ? "Ready" : "Unchanged";
}

internal static class LibraryRenameService
{
    internal static StringComparer Paths => StringComparer.OrdinalIgnoreCase;
    public static List<RenamePreview> Preview(IReadOnlyList<Track> targets, IReadOnlyList<Track> library, RenameOptions options, bool writeTitles)
    {
        options.Validate();
        var rows = new List<RenamePreview>();
        foreach (var track in targets.Distinct())
        {
            try
            {
                var proposed = options.Transform(track);
                var titleMode = options.Mode == RenameMode.FileNameToTitle;
                var oldPath = Path.GetFullPath(track.FilePath);
                var filename = proposed + Path.GetExtension(oldPath);
                var newPath = titleMode ? oldPath : Path.Combine(Path.GetDirectoryName(oldPath)!, filename);
                var error = titleMode ? (proposed.Length == 0 ? "Empty title" : null) : ValidateFileName(filename, proposed);
                if ((!titleMode || writeTitles) && !File.Exists(oldPath)) error = "File missing";
                if (File.Exists(oldPath) && (File.GetAttributes(oldPath) & FileAttributes.ReparsePoint) != 0) error = "Linked file; rename its source instead";
                if (titleMode && writeTitles && File.Exists(oldPath) && (File.GetAttributes(oldPath) & FileAttributes.ReadOnly) != 0) error = "File is read-only";
                rows.Add(new() { Track = track, Proposed = titleMode ? proposed : filename, TargetPath = newPath, Changed = titleMode ? proposed != track.Title : oldPath != newPath, Error = error });
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            { rows.Add(new() { Track = track, Error = ex.Message }); }
        }
        if (options.Mode == RenameMode.FileNameToTitle) return rows;
        foreach (var group in rows.GroupBy(r => r.Track.FilePath, Paths).Where(g => g.Select(r => r.TargetPath).Distinct(Paths).Count() > 1))
            foreach (var row in group) row.Error = "Same source has conflicting names";
        foreach (var group in rows.Where(r => r.Error is null).GroupBy(r => r.TargetPath, Paths).Where(g => g.Select(r => r.Track.FilePath).Distinct(Paths).Count() > 1))
            foreach (var row in group) row.Error = "Duplicate destination name";
        var moving = rows.Where(r => r.Changed && r.Error is null).Select(r => Path.GetFullPath(r.Track.FilePath)).ToHashSet(Paths);
        var knownPaths = library.Select(t => t.FilePath).ToHashSet(Paths);
        foreach (var row in rows.Where(r => r.Changed && r.Error is null))
        {
            if (Paths.Equals(row.TargetPath, row.Track.FilePath)) continue; // Case-only rename uses staging too.
            if (Directory.Exists(row.TargetPath) || ((File.Exists(row.TargetPath) || knownPaths.Contains(row.TargetPath)) && !moving.Contains(row.TargetPath)))
                row.Error = "Destination already exists";
        }
        return rows;
    }

    internal static string? ValidateFileName(string filename, string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return "Filename would be empty";
        if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Invalid filename character";
        if (filename.EndsWith(' ') || filename.EndsWith('.')) return "Filename ends with a space or dot";
        if (filename.Length > 255) return "Filename exceeds 255 characters";
        var baseName = filename.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (baseName is "CON" or "PRN" or "AUX" or "NUL" ||
            (baseName.Length == 4 && (baseName.StartsWith("COM") || baseName.StartsWith("LPT")) && "123456789¹²³".Contains(baseName[3]))) return "Reserved Windows filename";
        return null;
    }

    public static RenameBatchEdit Apply(IReadOnlyList<Track> targets, IReadOnlyList<Track> library, RenameOptions options, bool writeTitles, Action persist)
    {
        var rows = Preview(targets, library, options, writeTitles);
        if (rows.Any(r => r.Error is not null)) throw new IOException("Fix preview conflicts before applying: " + string.Join("; ", rows.Where(r => r.Error is not null).Take(6).Select(r => r.CurrentName + ": " + r.Error)));
        var changed = rows.Where(r => r.Changed).ToList();
        if (changed.Count == 0) throw new InvalidOperationException("There are no changes to apply.");
        return options.Mode == RenameMode.FileNameToTitle ? RenameBatchEdit.ApplyTitles(changed, writeTitles, persist) : RenameBatchEdit.ApplyFiles(changed, library, persist);
    }
}

internal sealed class RenameBatchEdit
{
    private sealed record PathChange(string Before, string After);
    private sealed record Reference(Track Track, string BeforePath, string BeforeImport, string AfterPath, string AfterImport);
    private readonly List<PathChange> paths;
    private readonly List<Reference> references;
    private readonly Action persist;
    private readonly List<TagBatchEdit> titles = [];
    private RenameBatchEdit(List<PathChange> paths, List<Reference> references, Action persist) { this.paths = paths; this.references = references; this.persist = persist; }

    public static RenameBatchEdit ApplyFiles(List<RenamePreview> rows, IReadOnlyList<Track> library, Action persist)
    {
        var paths = rows.GroupBy(r => r.Track.FilePath, LibraryRenameService.Paths).Select(g => new PathChange(Path.GetFullPath(g.Key), g.First().TargetPath)).ToList();
        var mapping = paths.ToDictionary(p => p.Before, p => p.After, LibraryRenameService.Paths);
        var references = library.Where(t => mapping.ContainsKey(t.FilePath) || mapping.ContainsKey(t.OriginalImportPath)).Select(t => new Reference(t, t.FilePath, t.OriginalImportPath,
            mapping.GetValueOrDefault(t.FilePath, t.FilePath), mapping.GetValueOrDefault(t.OriginalImportPath, t.OriginalImportPath))).ToList();
        var edit = new RenameBatchEdit(paths, references, persist); edit.Redo(); return edit;
    }

    public static RenameBatchEdit ApplyTitles(List<RenamePreview> rows, bool writeFiles, Action persist)
    {
        var edit = new RenameBatchEdit([], [], persist);
        try
        {
            foreach (var row in rows)
                edit.titles.Add(TagBatchEdit.Apply([row.Track], new TagPatch(new Dictionary<string, string> { ["Title"] = row.Proposed }), writeFiles, "", () => { }));
            persist(); return edit;
        }
        catch (Exception ex)
        {
            var errors = new List<Exception> { ex };
            foreach (var title in edit.titles.AsEnumerable().Reverse()) try { title.Undo(); } catch (Exception recovery) { errors.Add(recovery); }
            try { persist(); } catch (Exception recovery) { errors.Add(recovery); }
            throw new AggregateException("Title update failed; changes were rolled back where possible.", errors);
        }
    }

    public void Undo() { if (titles.Count > 0) SetTitles(false); else SetFiles(false); }
    public void Redo() { if (titles.Count > 0) SetTitles(true); else SetFiles(true); }
    private void SetTitles(bool after)
    {
        var applied = new List<TagBatchEdit>();
        try
        {
            foreach (var title in after ? titles : titles.AsEnumerable().Reverse()) { if (after) title.Redo(); else title.Undo(); applied.Add(title); }
            persist();
        }
        catch (Exception ex)
        {
            var errors = new List<Exception> { ex };
            foreach (var title in applied.AsEnumerable().Reverse()) try { if (after) title.Undo(); else title.Redo(); } catch (Exception recovery) { errors.Add(recovery); }
            try { persist(); } catch (Exception recovery) { errors.Add(recovery); }
            throw new AggregateException("Could not change title history; recovery errors are included below.", errors);
        }
    }

    private void SetFiles(bool after)
    {
        var moves = paths.Select(p => (Source: after ? p.Before : p.After, Target: after ? p.After : p.Before)).ToList();
        var sourceSet = moves.Select(p => p.Source).ToHashSet(LibraryRenameService.Paths);
        foreach (var move in moves)
        {
            if (!Path.IsPathFullyQualified(move.Source) || !Path.IsPathFullyQualified(move.Target) ||
                !LibraryRenameService.Paths.Equals(Path.GetDirectoryName(move.Source), Path.GetDirectoryName(move.Target)) || Path.GetExtension(move.Source) != Path.GetExtension(move.Target))
                throw new IOException("A rename cannot change the folder or extension.");
            if (!File.Exists(move.Source)) throw new FileNotFoundException("Rename source is missing.", move.Source);
            if (Directory.Exists(move.Target) || (File.Exists(move.Target) && !sourceSet.Contains(move.Target))) throw new IOException("Rename destination is occupied: " + move.Target);
        }
        foreach (var reference in references)
            if (!LibraryRenameService.Paths.Equals(reference.Track.FilePath, after ? reference.BeforePath : reference.AfterPath)) throw new IOException("A library path changed since this operation was recorded.");
        var executed = new List<(string Source, string Target)>();
        var locations = moves.ToDictionary(p => p.Source, p => p.Source, LibraryRenameService.Paths);
        var owners = moves.ToDictionary(p => p.Source, p => p.Source, LibraryRenameService.Paths);
        var staging = moves.ToDictionary(p => p.Source, p => Path.Combine(Path.GetDirectoryName(p.Source)!, ".htunes-rename-" + Guid.NewGuid().ToString("N") + Path.GetExtension(p.Source)), LibraryRenameService.Paths);
        var recoveryFile = Path.Combine(Path.GetTempPath(), "htunes-rename-recovery-" + Guid.NewGuid().ToString("N") + ".json");
        void SaveRecovery(string phase)
        {
            var json = JsonSerializer.Serialize(new { Phase = phase, Moves = moves.Select(p => new { p.Source, p.Target, Temporary = staging[p.Source], LastKnownPath = locations[p.Source] }),
                References = references.Select(r => new { r.Track.Id, r.BeforePath, r.AfterPath, r.BeforeImport, r.AfterImport }), ApplyingForward = after }, new JsonSerializerOptions { WriteIndented = true });
            var temporary = recoveryFile + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                writer.Write(json); writer.Flush(); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, recoveryFile, true);
        }
        void ForgetRecovery()
        {
            try { File.Delete(recoveryFile); File.Delete(recoveryFile + ".tmp"); }
            catch (Exception ex) { DebugLog.Write("Rename", "Completed operation left a recovery journal: " + recoveryFile, ex); }
        }
        void Move(string source, string destination)
        {
            File.Move(source, destination); // Never overwrite, even if another process races the preview.
            executed.Add((source, destination));
            var key = owners[source]; owners.Remove(source); owners[destination] = key; locations[key] = destination;
        }
        try
        {
            // Stage within each source folder, allowing case changes, chains and swaps without overwriting any file.
            // Save the complete mapping before the first move, so interrupted runs are recoverable.
            SaveRecovery("Staging");
            foreach (var move in moves) Move(move.Source, staging[move.Source]);
            SaveRecovery("Finalizing");
            foreach (var move in moves) Move(locations[move.Source], move.Target);
            foreach (var reference in references) { reference.Track.FilePath = after ? reference.AfterPath : reference.BeforePath; reference.Track.OriginalImportPath = after ? reference.AfterImport : reference.BeforeImport; }
            persist();
            ForgetRecovery();
        }
        catch (Exception ex)
        {
            var errors = new List<Exception> { ex };
            foreach (var move in executed.AsEnumerable().Reverse())
            {
                try
                {
                    File.Move(move.Target, move.Source);
                    var key = owners[move.Target]; owners.Remove(move.Target); owners[move.Source] = key; locations[key] = move.Source;
                }
                catch (Exception recovery) { errors.Add(new IOException($"Recovery could not move '{move.Target}' back to '{move.Source}'. No file was overwritten.", recovery)); }
            }
            foreach (var reference in references)
            {
                var source = after ? reference.BeforePath : reference.AfterPath;
                reference.Track.FilePath = locations.GetValueOrDefault(source, source);
                var import = after ? reference.BeforeImport : reference.AfterImport;
                reference.Track.OriginalImportPath = locations.GetValueOrDefault(import, import);
            }
            try { persist(); } catch (Exception recovery) { errors.Add(recovery); }
            if (errors.Count == 1) ForgetRecovery();
            else { try { SaveRecovery("Recovery required"); } catch (Exception recovery) { errors.Add(recovery); } }
            throw new AggregateException("Rename failed. Recovery was attempted; any remaining file locations are retained in the library when it can be saved." +
                (errors.Count > 1 ? " Recovery mapping: " + recoveryFile : ""), errors);
        }
    }
}
