using System.Windows.Controls;
using System.Windows.Input;

namespace HTunes.App;

// Inline autocomplete for free-text metadata fields: as the user types, the best
// matching library value is appended as a selected suggestion. Tab commits it;
// leaving the field or continuing to type without committing discards it.
internal static class TextBoxAutoComplete
{
    public static void Attach(TextBox box, Func<IEnumerable<string>> suggestions)
    {
        var state = new State();
        box.PreviewKeyDown += (_, e) =>
        {
            state.Deleting = e.Key is Key.Back or Key.Delete;
            if (e.Key == Key.Tab && HasPendingSuggestion(box, state))
            {
                box.SelectionStart = box.Text.Length;
                box.SelectionLength = 0;
                state.Pending = false;
                e.Handled = true;
            }
        };
        box.TextChanged += (_, _) =>
        {
            if (state.Suppress) return;
            state.Pending = false;
            if (state.Deleting) { state.Deleting = false; return; }
            if (!box.IsKeyboardFocused) return; // ignore programmatic text changes
            var caret = box.SelectionStart;
            if (caret < box.Text.Length) return; // only complete when typing at the end
            var typed = box.Text;
            if (typed.Length == 0) return;
            var match = suggestions().FirstOrDefault(value =>
                value.Length > typed.Length && value.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase));
            if (match is null) return;
            state.Suppress = true;
            box.Text = typed + match[typed.Length..];
            box.SelectionStart = typed.Length;
            box.SelectionLength = match.Length - typed.Length;
            state.Suppress = false;
            state.Pending = true;
        };
        box.LostFocus += (_, _) =>
        {
            if (!HasPendingSuggestion(box, state)) { state.Pending = false; return; }
            state.Suppress = true;
            box.Text = box.Text[..box.SelectionStart];
            box.SelectionStart = box.Text.Length;
            box.SelectionLength = 0;
            state.Suppress = false;
            state.Pending = false;
        };
    }

    private static bool HasPendingSuggestion(TextBox box, State state) =>
        state.Pending && box.SelectionLength > 0 && box.SelectionStart + box.SelectionLength == box.Text.Length;

    private sealed class State
    {
        public bool Suppress;
        public bool Deleting;
        public bool Pending;
    }
}
