using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BrimDeck;

// Where the music page's source menu opens against its button (MainWindow.Music.cs): below it with the two right edges in line,
// or above it where the screen has no room below. The places are given to WPF as they are, so nothing depends on how it turns
// a placement round: when Windows opens menus right-aligned (SystemParameters.MenuDropAlignment, the right-handed pen and touch
// setting), a bottom placement already hangs from the button's right edge and takes its horizontal offset the other way, and a
// menu given a negative offset to line up its right edge lay to the left of the button instead (issue #401). At the bottom of the
// screen a bottom placement flipped above kept its downward offset and covered the button's lower edge.
internal static class SourceMenuPlacement
{
    // The space between the button and the menu, in the panel's own DIPs.
    public const double Gap = 6;

    public static void Attach(ContextMenu menu, FrameworkElement button, double scale)
    {
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Custom;
        // The callback measures in screen DIPs, the scaled sizes of both; WPF adds the offsets after scaling them, so they stay 0.
        double gap = Gap * scale;
        menu.CustomPopupPlacementCallback = (list, anchor, _) =>
        [
            new(new Point(anchor.Width - list.Width, anchor.Height + gap), PopupPrimaryAxis.Horizontal),
            new(new Point(anchor.Width - list.Width, -list.Height - gap), PopupPrimaryAxis.Horizontal),
        ];
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
    }
}
