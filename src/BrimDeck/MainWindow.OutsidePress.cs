using System.Windows;
using BrimDeck.Native;

namespace BrimDeck;

// A press outside the open panel closes it at once. The panel does not take the foreground, so a press in another app reaches only
// that app; the panel learns of it from a low-level hook, kept only while the panel is open and the pointer has left it. The press
// skips the waits meant for a pointer that is only passing by: the collapse delay, the slack past the resize grips and the path back
// from a popup (CheckPointerExit). What still holds the panel open (a menu, a drag, a resize) holds it as before.
public partial class MainWindow
{
    private PointerPressObserver? _outsidePress;
    private bool _pressedOutside;

    private void WatchOutsidePress(bool watch)
    {
        if (watch) { _outsidePress ??= new PointerPressObserver(Dispatcher, OutsidePressed); return; }
        _outsidePress?.Dispose(); _outsidePress = null; _pressedOutside = false;
    }

    private void OutsidePressed(Point point)
    {
        if (!_expanded || _preview || _app.Exiting) return;
        // The panel, the collapsed surface's area and this app's own windows (menus, popups, the settings) are not outside.
        if (InsideOpenPanel(point) || WindowsHost.OwnWindowAt(point)) return;
        _pressedOutside = true;
        // A panel with the foreground (typing in a box, or given the keyboard by a hotkey) closes from Deactivated, once the press
        // has moved the foreground to where it landed: closing first would give the keyboard back to the window the typing came
        // from (EndTyping), over the one just pressed.
        if (IsActive) return;
        PointerChanged(false);
    }
}
