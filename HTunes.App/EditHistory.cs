namespace HTunes.App;

/// <summary>Session-only, bounded history. Tag/rename entries also restore local files; device operations are not recorded.</summary>
internal sealed class EditHistory(int capacity = 100)
{
    private sealed record Edit(string Description, Action Undo, Action Redo);
    private readonly List<Edit> undo = [];
    private readonly List<Edit> redo = [];
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string? UndoDescription => CanUndo ? undo[^1].Description : null;
    public string? RedoDescription => CanRedo ? redo[^1].Description : null;

    public void Record(string description, Action undoAction, Action redoAction)
    {
        undo.Add(new Edit(description, undoAction, redoAction));
        redo.Clear();
        if (undo.Count > Math.Max(1, capacity)) undo.RemoveAt(0);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var edit = undo[^1];
        edit.Undo();
        undo.RemoveAt(undo.Count - 1);
        redo.Add(edit);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var edit = redo[^1];
        edit.Redo();
        redo.RemoveAt(redo.Count - 1);
        undo.Add(edit);
    }
}
