using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace BrimDeck;

// How the island's window reaches the screen (prototype, memory-opt). As a layered window (AllowsTransparency) WPF hands every
// frame to Windows through UpdateLayeredWindow; for a window as wide as the monitor that kept the opening at every other refresh
// at 144 Hz (live check 10-27, LayerProbe: a layered window of the monitor's width opened at half the rate, a window DWM composes
// at the full rate). Composed by DWM instead, the window is an ordinary one whose frame is extended over all of it, so its
// transparent pixels show what lies below, and WPF presents to it as to any window.
// A layered window lets clicks through its fully transparent pixels; this one would take them all. So its region is cut to
// what it draws (the island, its shadow, the resize grips and the hover strip of an open panel) and follows it as it changes.
// Without a graphics card (software rendering, a remote session) DWM would show the transparent pixels black, so the window
// stays layered there. Chosen once per process: BRIMDECK_COMPOSITION=dwm while this is a prototype.
public partial class MainWindow
{
    internal static readonly bool DwmComposed = Environment.GetEnvironmentVariable("BRIMDECK_COMPOSITION") == "dwm"
        && RenderCapability.Tier >> 16 >= 2 && !SystemParameters.IsRemoteSession;
    private Int32Rect _regionShown = Int32Rect.Empty;
    private bool _regionQueued;

    // Before the window has a handle: from then on the choice cannot change.
    private void InitializeComposition()
    {
        if (!DwmComposed) return;
        AllowsTransparency = false;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        { GlassFrameThickness = new Thickness(-1), CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(handle) is { } source) source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);
            int none = unchecked((int)0xFFFFFFFE), square = 1, noBackdrop = 1, noTransitions = 1;
            DwmSetWindowAttribute(handle, 34, ref none, 4);           // DWMWA_BORDER_COLOR: none
            DwmSetWindowAttribute(handle, 33, ref square, 4);         // DWMWA_WINDOW_CORNER_PREFERENCE: do not round
            DwmSetWindowAttribute(handle, 38, ref noBackdrop, 4);     // DWMWA_SYSTEMBACKDROP_TYPE: none
            DwmSetWindowAttribute(handle, 3, ref noTransitions, 4);   // DWMWA_TRANSITIONS_FORCEDISABLED: shown and hidden at once, as a layered window is
            FitRegion();
        };
        // Whatever moves or changes size lays the window out again; the region follows once per frame at most.
        LayoutUpdated += (_, _) => QueueRegion();
    }

    private void QueueRegion()
    {
        if (_regionQueued) return;
        _regionQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () => { _regionQueued = false; FitRegion(); });
    }

    // The window's region: the bounds of everything drawn in it, in device pixels, a little larger. What lies outside gets the
    // clicks, as the transparent pixels of the layered window did; the island's rounded corners and the faint edge of its shadow
    // take them as they did there too.
    private void FitRegion()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || source.CompositionTarget is null) return;
        var bounds = VisualTreeHelper.GetDescendantBounds(Stage);
        Int32Rect wanted;
        if (bounds.IsEmpty || !IsVisible) wanted = new Int32Rect(0, 0, 0, 0);
        else
        {
            var device = source.CompositionTarget.TransformToDevice;
            var corner = device.Transform(Stage.TranslatePoint(bounds.TopLeft, this));
            var far = device.Transform(Stage.TranslatePoint(bounds.BottomRight, this));
            int left = (int)Math.Floor(corner.X) - 2, top = (int)Math.Floor(corner.Y) - 2;
            wanted = new Int32Rect(Math.Max(0, left), Math.Max(0, top), (int)Math.Ceiling(far.X) + 2 - Math.Max(0, left), (int)Math.Ceiling(far.Y) + 2 - Math.Max(0, top));
        }
        if (wanted == _regionShown) return;
        _regionShown = wanted;
        var region = CreateRectRgn(wanted.X, wanted.Y, wanted.X + Math.Max(0, wanted.Width), wanted.Y + Math.Max(0, wanted.Height));
        // The system owns the region once it is set; it is deleted only if setting it failed.
        if (SetWindowRgn(source.Handle, region, false) == 0) DeleteObject(region);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("gdi32")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
}
