using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BrimDeck.Native;

namespace BrimDeck;

// The word beside the pointer while files are dragged out of the file transfer: what letting go there does, "复制" or "移动"
// (MainWindow.DragOut). The pointer leaves the panel during the drag, so the word has a window of its own: above everything, never
// activated and transparent to the pointer, which keeps it from being taken for the place the files are dropped on. It looks as
// the label of a music gesture does.
internal sealed class DragHint : Window
{
    private readonly Border _frame;
    private readonly TextBlock _text;

    public DragHint(PanelLook look)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Focusable = false; IsHitTestVisible = false; Title = "BrimDeck";
        Content = _frame = Frame(look);
        _text = (TextBlock)_frame.Child;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, ExStyle, GetWindowLongPtr(handle, ExStyle) | Transparent | ToolWindow | NoActivate);
            Follow();
        };
        // The first layout and a longer or shorter word change the size, and with it the side of the pointer that has room.
        _frame.SizeChanged += (_, _) => Follow();
    }

    // The frame keeps to its word at the window's top-left corner. Windows gives a window a least size (136 x 39 at 100 %), far
    // more than a word of two characters needs; stretched over the window, the frame was a box that large with the word in a corner.
    internal static Border Frame(PanelLook look) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, SnapsToDevicePixels = true,
        Child = new TextBlock { FontFamily = UI.PanelFont, FontSize = 12, TextWrapping = TextWrapping.NoWrap, Foreground = UI.Brush(look.Primary) },
        Background = UI.Brush(look.Hex("popup")), BorderBrush = UI.Brush(look.Hex("popupBorder")),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 4)
    };

    // Shows the word at the pointer; nothing where letting go does nothing.
    public void Say(string? words)
    {
        if (words is not null) _text.Text = words;
        _frame.Visibility = words is null ? Visibility.Hidden : Visibility.Visible;
        if (!IsVisible) Show();
        Follow();
    }

    // Below the pointer and to its right, clear of the cursor and the mark Windows adds to it during a drag; on the other side
    // where the monitor ends.
    private void Follow()
    {
        if (new WindowInteropHelper(this).Handle == 0) return;
        var pointer = WindowsHost.Cursor();
        var (bounds, _, scale) = WindowsHost.CursorScreen();
        double width = _frame.ActualWidth * scale, height = _frame.ActualHeight * scale;
        double x = pointer.X + 20 * scale, y = pointer.Y + 24 * scale;
        if (x + width > bounds.Right) x = pointer.X - 12 * scale - width;
        if (y + height > bounds.Bottom) y = pointer.Y - 12 * scale - height;
        WindowsHost.MoveTo(this, (int)Math.Round(x), (int)Math.Round(y));
    }

    private const int ExStyle = -20;
    private const nint Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
}
