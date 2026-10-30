using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using BrimDeck.Core;

namespace BrimDeck;

// How the island's window reaches the screen (WindowComposition). As a layered window (AllowsTransparency) WPF hands every frame
// to Windows through UpdateLayeredWindow; for a window as wide as the monitor that kept the opening at every other refresh at
// 144 Hz (live check 10-27: LayerProbe, a layered window of the monitor's width opened at half the rate, a window DWM composes at
// the full rate; BrimDeck in github-dark opened at 12.7 ms a frame layered, 7.7 composed). Composed by DWM, the window is an
// ordinary one whose frame is extended over all of it, so its transparent pixels show what lies below, and WPF presents to it
// as to any window.
// A layered window lets clicks through its fully transparent pixels; this one would take them all. So its region is cut to
// what it draws (the island, its shadow as far as it reaches, the resize grips and the hover strip of an open panel): at rest
// it follows the layout, and for a move it is set once to cover the move (HoldRegion).
// The choice is made when the window is made and cannot change after: the app makes the panels again when it should
// (App.Composition: a liquid glass theme chosen or left, software drawing, a remote session).
public partial class MainWindow
{
    // BRIMDECK_COMPOSITION=layered or dwm chooses regardless of the theme (for comparing, and to go back).
    internal static readonly string? ForcedComposition = Environment.GetEnvironmentVariable("BRIMDECK_COMPOSITION");
    // For the checks only: drawing taken as done in software, as after a lost graphics device or a remote connection.
    internal static bool SoftwareForTest { get; set; }
    internal static bool DrawsWithCard => !SoftwareForTest && RenderCapability.Tier >> 16 >= 2;
    internal static bool RemoteSession => SystemParameters.IsRemoteSession || GetSystemMetrics(0x1000) != 0; // SM_REMOTESESSION
    // What a panel window made now would be.
    internal static WindowSurface WantedSurface => WindowComposition.Choose(ForcedComposition, PanelThemes.IsLiquid(PanelTheme.Current), DrawsWithCard, RemoteSession);
    // What this window is.
    internal bool DwmComposed { get; private set; }
    private bool _regionQueued;

    // Before the window has a handle: from then on the choice cannot change.
    private void InitializeComposition()
    {
        // The offscreen page sheets draw the window's content only; their window is never shown.
        DwmComposed = !_app.Offscreen && WantedSurface == WindowSurface.Composed;
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
        // Whatever moves or changes size lays the window out again; the region follows once per frame at most. The shadow's
        // reach changes without a layout: as it fades in or out, and as its look follows the backdrop (MainWindow.ShadowLook).
        LayoutUpdated += (_, _) => QueueRegion();
        var opacity = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(OpacityProperty, typeof(System.Windows.Controls.Grid));
        void ShadowShown(object? sender, EventArgs e) => QueueRegion();
        opacity.AddValueChanged(ShadowHost, ShadowShown);
        foreach (var layer in new FrameworkElement[] { IslandShadow, IslandContact }) if (layer.Effect is { IsFrozen: false } effect) effect.Changed += ShadowShown;
        Closed += (_, _) => opacity.RemoveValueChanged(ShadowHost, ShadowShown);
    }

    private void QueueRegion()
    {
        if (_regionQueued || Environment.TickCount64 < _regionHeldUntil) return;
        _regionQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () => { _regionQueued = false; FitRegion(); });
    }

    // While the island opens, closes or changes size, the region is set once, to cover where it starts and where it ends, and
    // held until the move is over: set on every frame, it made DWM shape the window again on every frame of the move. Clicks
    // beside the island are taken for those few hundred milliseconds. Bounds in window DIPs; the move's own curve overshoots
    // its end by under 2 percent (Curves.BounceOvershoot is a sixth of that; the 2 percent below is kept as a wide margin).
    private long _regionHeldUntil;
    private Rect _regionHeld = Rect.Empty;
    private System.Windows.Threading.DispatcherTimer? _regionRelease;
    private void HoldRegion(Rect target, int duration)
    {
        if (!DwmComposed || PresentationSource.FromVisual(this) is null) return;
        if (duration <= 0) { _regionHeldUntil = 0; QueueRegion(); return; }
        var held = Drawn(Island.TranslatePoint(new Point(0, 0), this) is var at ? new Rect(at, Island.RenderSize) : Rect.Empty);
        var end = target; end.Inflate(target.Width * .02, target.Height * .02);
        held.Union(Drawn(end));
        // A move that takes over one still under way keeps what the first one covered: the island may still be anywhere in it.
        if (Environment.TickCount64 < _regionHeldUntil) held.Union(_regionHeld);
        _regionHeld = held;
        _regionHeldUntil = Environment.TickCount64 + duration + 50;
        SetRegion(held);
        _regionRelease ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render, Dispatcher);
        _regionRelease.Stop();
        _regionRelease.Interval = TimeSpan.FromMilliseconds(duration + 60);
        _regionRelease.Tick -= ReleaseRegion; _regionRelease.Tick += ReleaseRegion;
        _regionRelease.Start();
    }
    private void ReleaseRegion(object? sender, EventArgs e) { _regionRelease?.Stop(); _regionHeldUntil = 0; FitRegion(); }

    // What the window draws around an island at these bounds, in window DIPs: the island, its shadow as far as it is seen, the
    // resize grips and the open panel's hover strip. A drop shadow's alpha falls below 1/255 at about two thirds of its blur
    // radius: a layered window took clicks to 9 px around the collapsed island (blur 14) and to 19 px around the open liquid
    // glass panel (blur about 30), so the region ends there (ShadowSeen of the blur, around the island moved by the depth) and
    // what it leaves out was never drawn. The shadow's reserved bounds (20 to 40 DIPs) took clicks well beyond the visible
    // shadow under liquid glass (live check 10-27).
    private const double ShadowSeen = .7;
    private Rect Drawn(Rect island)
    {
        if (island.IsEmpty) return island;
        var drawn = Shadowed(island);
        foreach (var extra in Beside()) drawn.Union(extra);
        return drawn;
    }

    // The island with its shadow as far as it is seen.
    private Rect Shadowed(Rect island)
    {
        var drawn = island;
        if (ShadowHost.Visibility == Visibility.Visible && ShadowHost.Opacity > .01)
            foreach (var layer in new FrameworkElement[] { IslandShadow, IslandContact })
            {
                if (layer.Visibility != Visibility.Visible || layer.Effect is not System.Windows.Media.Effects.DropShadowEffect shadow) continue;
                double angle = shadow.Direction * Math.PI / 180;
                var cast = island;
                cast.Offset(shadow.ShadowDepth * Math.Cos(angle), -shadow.ShadowDepth * Math.Sin(angle));
                cast.Inflate(shadow.BlurRadius * ShadowSeen + 1, shadow.BlurRadius * ShadowSeen + 1);
                drawn.Union(cast);
            }
        return drawn;
    }

    // What takes clicks beside the island: the resize grips and the open panel's hover strip.
    private IEnumerable<Rect> Beside()
    {
        if (_grips.Visibility == Visibility.Visible && VisualTreeHelper.GetDescendantBounds(_grips) is { IsEmpty: false } grips)
            yield return new Rect(_grips.TranslatePoint(grips.TopLeft, this), grips.Size);
        if (HoverExtension.Visibility == Visibility.Visible && HoverExtension.ActualHeight > 0)
            yield return new Rect(HoverExtension.TranslatePoint(new Point(0, 0), this), HoverExtension.RenderSize);
    }

    // At rest the region keeps the island's rounded corners, widened by the shadow around them: a rectangle took clicks up to
    // 28 px out from a corner of the open liquid glass panel where the layered window took them to 15 (live check 10-27).
    private void FitRegion()
    {
        if (Environment.TickCount64 < _regionHeldUntil) return;
        if (!IsVisible) { SetRegion([]); return; }
        var island = new Rect(Island.TranslatePoint(new Point(0, 0), this), Island.RenderSize);
        var around = Shadowed(island);
        double side = Math.Max(0, island.Left - around.Left), corners = Island.CornerRadius.TopLeft, bottom = Island.CornerRadius.BottomLeft;
        var pieces = new List<(Rect Bounds, double Radius)> { (around, bottom + side) };
        // A flatter top than bottom (the notch, the open panel hanging from the screen's edge): its upper half rounds as the top.
        if (corners < bottom) pieces.Add((new Rect(around.Left, around.Top, around.Width, around.Height / 2), corners + side));
        foreach (var extra in Beside()) pieces.Add((extra, 0));
        SetRegion(pieces);
    }

    private void SetRegion(Rect bounds) => SetRegion(bounds.IsEmpty ? [] : [(bounds, 0)]);

    // The region in device pixels from rounded rectangles (radius in DIPs), whole pixels around each; nothing at all for none.
    private string _regionKey = "";
    private void SetRegion(List<(Rect Bounds, double Radius)> pieces)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || source.CompositionTarget is null) return;
        var device = source.CompositionTarget.TransformToDevice;
        var shapes = new List<(int Left, int Top, int Right, int Bottom, int Round)>();
        foreach (var (bounds, radius) in pieces)
        {
            if (bounds.IsEmpty) continue;
            var corner = device.Transform(bounds.TopLeft); var far = device.Transform(bounds.BottomRight);
            shapes.Add((Math.Max(0, (int)Math.Floor(corner.X)), Math.Max(0, (int)Math.Floor(corner.Y)), (int)Math.Ceiling(far.X), (int)Math.Ceiling(far.Y), (int)Math.Round(Math.Max(0, radius) * device.M11)));
        }
        string key = string.Join(";", shapes);
        if (key == _regionKey) return;
        _regionKey = key;
        var region = CreateRectRgn(0, 0, 0, 0);
        foreach (var (l, t, r, b, round) in shapes)
        {
            var piece = round > 0 ? CreateRoundRectRgn(l, t, r + 1, b + 1, 2 * round, 2 * round) : CreateRectRgn(l, t, r, b);
            CombineRgn(region, region, piece, 2); // RGN_OR
            DeleteObject(piece);
        }
        // The system owns the region once it is set; it is deleted only if setting it failed.
        if (SetWindowRgn(source.Handle, region, false) == 0) DeleteObject(region);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
}
