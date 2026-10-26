using System.Windows;
using System.Windows.Input;

namespace BrimDeck;

// The system monitor page's controls draw themselves (FrameworkElement, not Button), so Tab passed them by and only the header
// took the keyboard (live check X-03). One that takes clicks takes the keyboard focus too, shows the panel's focus mark and
// answers Enter and Space as a click.
internal static class KeyboardPress
{
    public static void Take(FrameworkElement element, bool on)
    {
        element.Focusable = on;
        KeyboardNavigation.SetIsTabStop(element, on);
        if (on && Application.Current?.TryFindResource("PanelKeyboardFocus") is Style style) element.FocusVisualStyle = style;
    }

    // Enter or Space without modifiers: handled, and true for the caller to act as on a click.
    public static bool Pressed(KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || e.Key is not (Key.Enter or Key.Space)) return false;
        e.Handled = true;
        return true;
    }
}
