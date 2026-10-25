using System.Windows;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// For the offscreen page sheets only (tests/BrimDeck.FxPreview --liquid-pages): the real panel drawn with no window on screen.
// The preview moves the window's stage into a source of its own that is never shown and calls UseOffscreen; from then on the
// panel lays out for the monitor given here instead of the one it would find, never shows its window, and takes the data it
// shows from the calls below. Liquid glass needs a window on screen, so GlassBackdrop decides through a stand-in host that
// shows it wherever the theme's glass is (GlassBackdrop.StandIn): the island's surface, painted rim and shadow follow that
// decision as in the app. In the app _offscreen is always null and none of this changes anything.
public partial class MainWindow
{
    private sealed record OffscreenScreen(Rect Bounds, Rect Work, double Scale);
    private OffscreenScreen? _offscreen;

    // The monitor the panel lays out for (device pixels, and its scale).
    private (Rect Bounds, Rect Work, double Scale) Screen() => _offscreen is { } screen ? (screen.Bounds, screen.Work, screen.Scale)
        : WindowsHost.MonitorScreen(MonitorHandle) ?? WindowsHost.PrimaryScreen();

    // Whether liquid glass is shown under the island now.
    private bool LiquidGlassShown => _glass?.LiquidShown == true;

    internal void UseOffscreen(Rect bounds, Rect work, double scale)
    {
        _offscreen = new(bounds, work, scale);
        OffscreenGlass();
    }

    // Offscreen the glass decides through a stand-in host that shows liquid glass wherever it would be shown (GlassBackdrop.StandIn):
    // the island's surface, painted rim and shadow then follow GlassBackdrop's own decision (SurfaceAside, Held, ApplyHeld).
    private void OffscreenGlass()
    {
        if (_offscreen is not null && _glass is not null) _glass.StandIn ??= () => true;
    }

    // The player's song and cover, as MediaSessions hands them over (with no notice of a new song).
    internal void OffscreenMedia(MediaTrack? track, BitmapSource? cover) => ApplyMediaUpdate(track, track is null ? [] : [track], cover, notice: false);

    // A reading of the system, as SystemSampler hands it over: the open monitor page and the collapsed row's readings.
    internal void OffscreenMonitor(MonitorSnapshot snapshot)
    {
        _compactStrip?.Update(snapshot);
        _monitorView?.Update(snapshot, motion: false);
    }

    // What the glass decided for the last frame, and what the island made of it: liquid glass shown, the surface aside, the
    // surface's opacity, and whether the shadow's caster is opaque (MainWindow.LiquidShadow).
    internal (bool Shown, bool Aside, double Surface, bool Caster) OffscreenGlassState =>
        (_glass?.LiquidShown == true, _glass?.SurfaceAside == true, _surface.Opacity,
            IslandShadow.Children.OfType<IslandBorder>().FirstOrDefault() is { } caster && ReferenceEquals(caster.Background, System.Windows.Media.Brushes.Black));

    // A frame of the glass, decided by GlassBackdrop through the stand-in: after every theme change or relayout, as the next
    // frame would in the app.
    internal void OffscreenLiquidFrame()
    {
        OffscreenGlass();
        _glass?.Update();
    }
}
