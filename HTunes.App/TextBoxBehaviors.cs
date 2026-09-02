using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HTunes.App;

// App-wide text box conveniences. Tabbing into a field that already has text selects the
// whole value so the next keystroke overwrites it (Windows address-bar behaviour); a mouse
// click still drops the caret where you clicked.
internal static class TextBoxBehaviors
{
    private static bool installed;

    public static void InstallTabSelectAll()
    {
        if (installed) return;
        installed = true;
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus));
    }

    // Single-line, editable field that received focus via Tab/Shift+Tab from another
    // control and holds text worth replacing.
    internal static bool ShouldSelectAllOnFocus(TextBox box, bool tabHeld, bool hadPreviousFocus) =>
        tabHeld && hadPreviousFocus && box.IsEnabled && !box.IsReadOnly && !box.AcceptsReturn
        && !string.IsNullOrEmpty(box.Text);

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (!ShouldSelectAllOnFocus(box, Keyboard.IsKeyDown(Key.Tab), e.OldFocus is not null)) return;
        // Let WPF finish placing the caret, then take the whole value - unless the user
        // already moved the caret or another handler (inline autocomplete) claimed focus.
        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (box.IsKeyboardFocused && box.SelectionLength == 0) box.SelectAll();
        }));
    }
}
