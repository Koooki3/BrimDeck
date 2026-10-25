using System.Windows;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// For the offscreen page sheets only (tests/BrimDeck.FxPreview --liquid-pages): the real panel drawn with no window on screen.
// The preview moves the window's stage into a source of its own that is never shown and calls UseOffscreen; from then on the
// panel lays out for the monitor given here instead of the one it would find, never shows its window, and takes the data it
// shows from the calls below. Liquid glass needs a window on screen, so it is taken as shown wherever the theme's glass is:
// the island's surface and painted rim follow what GlassBackdrop reports for a frame showing it, and the shadow stays as it
// does over liquid glass. In the app _offscreen is always null and none of this changes anything.
public partial class MainWindow
{
    private sealed record OffscreenScreen(Rect Bounds, Rect Work, double Scale);
    private OffscreenScreen? _offscreen;

    // The monitor the panel lays out for (device pixels, and its scale).
    private (Rect Bounds, Rect Work, double Scale) Screen() => _offscreen is { } screen ? (screen.Bounds, screen.Work, screen.Scale)
        : WindowsHost.MonitorScreen(MonitorHandle) ?? WindowsHost.PrimaryScreen();

    // Whether liquid glass is shown under the island now.
    private bool LiquidGlassShown => _glass?.LiquidShown == true || _offscreen is not null && PanelTheme.GlassShown;

    internal void UseOffscreen(Rect bounds, Rect work, double scale) => _offscreen = new(bounds, work, scale);

    // The player's song and cover, as MediaSessions hands them over (with no notice of a new song).
    internal void OffscreenMedia(MediaTrack? track, BitmapSource? cover) => ApplyMediaUpdate(track, track is null ? [] : [track], cover, notice: false);

    // A reading of the system, as SystemSampler hands it over: the open monitor page and the collapsed row's readings.
    internal void OffscreenMonitor(MonitorSnapshot snapshot)
    {
        _compactStrip?.Update(snapshot);
        _monitorView?.Update(snapshot, motion: false);
    }

    // A frame showing liquid glass at rest, as GlassBackdrop reports it (Held with the surface aside): after every theme change
    // or relayout, as the next frame would in the app.
    internal void OffscreenLiquidFrame()
    {
        if (_offscreen is not null && PanelTheme.GlassShown) ApplyHeld(true, GlassBackdrop.HeldShare(true, false, Island.MaterialOpacity, 0));
    }
}
