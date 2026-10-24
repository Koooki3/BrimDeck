using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BrimDeck;

// Drop-downs, lists and pickers open and close as the panel's cards do (Soft.Enter, Soft.Leave). WPF closes a pop-up at
// once, also when it closes itself on a click outside, so its closing is shown by a picture of its last frame, which leaves
// while the real one is already gone and the click has done what it was meant for. The picture is shown in a pop-up of its
// own that is opened as soon as the real one has been laid out: a pop-up created at the moment of closing came up some 50 ms
// later, after a blank. It lies right beneath the real one and is kept up to date while that is open, so it already shows
// when WPF takes the real one away: given its picture only on closing, it was drawn a frame late and the list blinked out
// and back before it left.
internal static class PopupMotion
{
    private static readonly ConditionalWeakTable<object, Placed> Places = new();
    // The waiting picture's pop-up for the opening under way, null between openings.
    private sealed class Placed { public Popup? Ghost; public Image? Picture; public Window? Window; public DispatcherTimer? Refresh; }

    // How often the picture beneath an open pop-up is taken again: a hover or a choice changes the list.
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMilliseconds(50);

    public static void Attach(Popup popup)
    {
        if (Places.TryGetValue(popup, out _)) return;
        var placed = new Placed();
        Places.Add(popup, placed);
        popup.Opened += (_, _) =>
        {
            Soft.Enter(popup.Child);
            Prepare(placed, () => popup.IsOpen ? popup.Child : null, () => Anchor(popup));
        };
        popup.Closed += (_, _) => Leave(popup.Child as FrameworkElement, placed);
    }

    // What a pop-up is placed against: its target, or, as a template's pop-up has none (a combo box's list), the element it lies in.
    private static UIElement? Anchor(Popup popup) => popup.PlacementTarget ?? popup.Parent as UIElement ?? popup.TemplatedParent as UIElement;

    // A combo box's list: its pop-up is in its template.
    public static void Attach(ComboBox combo)
    {
        void Hook() { if (combo.Template?.FindName("PART_Popup", combo) is Popup popup) Attach(popup); }
        if (combo.IsLoaded) Hook(); else combo.Loaded += (_, _) => Hook();
    }

    // A context menu enters by its surface (chrome), so its own bounds stay put for the pointer checks of its owner. Windows
    // fades a menu out itself as it closes, so it needs no picture leaving.
    public static void Attach(ContextMenu menu, Func<UIElement?> chrome) => menu.Opened += (_, _) => Soft.Enter(chrome());

    // Once the content is laid out, a pop-up for its picture is opened beneath it, placed against the content's window rather
    // than its anchor: choosing an option often rebuilds the page and takes the anchor away in the same click. The place is the
    // content's own offset in the real pop-up's root, which does not move while the content drops into place (Soft.Enter); the
    // root's corner alone put the picture a margin too high. The picture is first taken once the content has entered, so
    // nothing shows beneath it while it fades in.
    private static void Prepare(Placed placed, Func<UIElement?> content, Func<UIElement?> anchor)
    {
        Close(placed);
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!Soft.Enabled || placed.Ghost is not null || content() is not FrameworkElement element || anchor() is not { } source || Window.GetWindow(source) is not { } window) return;
            var root = VisualTreeHelper.GetParent(element) as Visual ?? element;
            if (PresentationSource.FromVisual(root) is not HwndSource real || PresentationSource.FromVisual(window) is null) return;
            var offset = root == element ? new Vector() : VisualTreeHelper.GetOffset(element);
            var at = window.PointFromScreen(root.PointToScreen(new Point(offset.X, offset.Y)));
            var picture = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            placed.Picture = picture; placed.Window = window;
            var ghost = placed.Ghost = new Popup { PlacementTarget = window, Placement = PlacementMode.Relative, HorizontalOffset = at.X, VerticalOffset = at.Y,
                AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false, Focusable = false, Child = picture, IsOpen = true };
            if (PresentationSource.FromVisual(picture) is HwndSource own) Native.WindowsHost.PlaceBelow(own.Handle, real.Handle);
            var refresh = placed.Refresh = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(Soft.Short + 80) };
            refresh.Tick += (_, _) =>
            {
                refresh.Interval = RefreshEvery;
                if (placed.Ghost != ghost || !element.IsVisible) { refresh.Stop(); return; }
                Take(element, picture);
            };
            refresh.Start();
        });
    }

    // The content as it is drawn now, one to one (PageHandover.Exactly), so neither its offset nor its entrance transform is
    // part of the picture.
    private static void Take(FrameworkElement content, Image image)
    {
        double width = content.ActualWidth, height = content.ActualHeight;
        if (width < 1 || height < 1 || PresentationSource.FromVisual(image) is null) return;
        var dpi = VisualTreeHelper.GetDpi(image);
        var picture = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(PageHandover.Exactly(content, width, height), null, new Rect(0, 0, width, height));
        picture.Render(drawing);
        picture.Freeze();
        image.Width = width; image.Height = height; image.Source = picture;
    }

    // Applies a choice made in a pop-up once the pop-up's leaving has been drawn: a choice that rebuilds the page holds the
    // window for a moment, and the closed pop-up then left a blank where its picture should already be.
    public static void AfterClose(Action change) => Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, change);

    private static void Close(Placed placed)
    {
        placed.Refresh?.Stop();
        if (placed.Ghost is { } ghost) ghost.IsOpen = false;
        placed.Ghost = null; placed.Picture = null; placed.Window = null; placed.Refresh = null;
    }

    private static void Leave(FrameworkElement? content, Placed placed)
    {
        var ghost = placed.Ghost; var image = placed.Picture; var window = placed.Window;
        placed.Refresh?.Stop();
        placed.Ghost = null; placed.Picture = null; placed.Window = null; placed.Refresh = null;
        if (ghost is null || image is null) return;
        // A pop-up closed with its window (the settings window closed or hidden) goes with it, without a picture left behind.
        if (!Soft.Enabled || content is null || window is not { IsVisible: true } || PresentationSource.FromVisual(image) is null) { ghost.IsOpen = false; return; }
        // The picture taken while it was open is the one beneath it already: taken now, the hovered option has lost its mark.
        // Closed before it had entered, it has none yet and is taken now.
        if (image.Source is null) Take(content, image);
        if (image.Source is null) { ghost.IsOpen = false; return; }
        DependencyPropertyChangedEventHandler hidden = (_, _) => ghost.IsOpen = false;
        window.IsVisibleChanged += hidden;
        Soft.Leave(image, () => { window.IsVisibleChanged -= hidden; ghost.IsOpen = false; });
    }
}
