using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BrimDeck;

// A press outside an open menu only closes the menu, as Windows' own menus do: that press and its release reach nothing in the
// window, so a press on a button or an entry while a menu is open does not also click it or paste the entry. A right press is the
// exception: it closes the menu and goes on, so a right click elsewhere opens the menu of the place clicked. It covers every
// ContextMenu, so a menu added later needs nothing of its own.
//
// A menu holding the mouse (WPF's capture) learns of a press outside it first (PreviewMouseDownOutsideCapturedElement) and closes
// there; the same press then goes on to the window, where the menu is already closed, so the press is known by its time. A menu
// not holding the mouse is closed by the window's own look at the press. Either way the press and its release go no further.
internal static class MenuDismissal
{
    private static readonly List<ContextMenu> Open = [];
    // The button whose press closed a menu, the press's time, and whether its release is still to be held back.
    private static MouseButton? _swallow;
    private static int _pressedAt;

    static MenuDismissal()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => { if (!Open.Contains((ContextMenu)sender)) Open.Add((ContextMenu)sender); }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent,
            new RoutedEventHandler((sender, _) => Open.Remove((ContextMenu)sender)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), Mouse.PreviewMouseDownOutsideCapturedElementEvent,
            new MouseButtonEventHandler((_, e) => Dismissing(e)), true);
    }

    public static void Attach(Window window)
    {
        window.PreviewMouseDown += (_, e) =>
        {
            // Only a press in the window itself, never one on a menu.
            if (e.OriginalSource is DependencyObject source && PresentationSource.FromDependencyObject(source) != PresentationSource.FromVisual(window)) return;
            // The press that has just closed a menu holding the mouse.
            if (_swallow == e.ChangedButton && _pressedAt == e.Timestamp) { e.Handled = true; return; }
            Open.RemoveAll(menu => !menu.IsOpen);
            // A press with no menu open: a release held back from an earlier one, which ended outside the window, is let go.
            if (Open.Count == 0) { _swallow = null; return; }
            foreach (var menu in Open.ToList()) menu.IsOpen = false;
            if (Dismissing(e)) e.Handled = true;
        };
        window.PreviewMouseUp += (_, e) =>
        {
            if (_swallow != e.ChangedButton) return;
            _swallow = null;
            e.Handled = true;
        };
    }

    // True when the press is to go no further.
    private static bool Dismissing(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right) return false;
        _swallow = e.ChangedButton; _pressedAt = e.Timestamp;
        return true;
    }
}
