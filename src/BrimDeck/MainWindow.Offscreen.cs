using System.Windows;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// For the offscreen page sheets only (tests/BrimDeck.FxPreview, tests/BrimDeck.IslandChecks): the real panel drawn with no window
// on screen. The preview moves the window's stage into a source of its own that is never shown and calls UseOffscreen; from then
// on the panel lays out for the monitor given here instead of the one it would find, never shows its window, and takes the data
// it shows from the calls below. In the app _offscreen is always null and none of this changes anything.
public partial class MainWindow
{
    private sealed record OffscreenScreen(Rect Bounds, Rect Work, double Scale);
    private OffscreenScreen? _offscreen;

    // The monitor the panel lays out for (device pixels, and its scale).
    private (Rect Bounds, Rect Work, double Scale) Screen() => _offscreen is { } screen ? (screen.Bounds, screen.Work, screen.Scale)
        : WindowsHost.MonitorScreen(MonitorHandle) ?? WindowsHost.PrimaryScreen();

    internal void UseOffscreen(Rect bounds, Rect work, double scale) => _offscreen = new(bounds, work, scale);

    // The player's song and cover, as MediaSessions hands them over (with no notice of a new song).
    internal void OffscreenMedia(MediaTrack? track, BitmapSource? cover) => ApplyMediaUpdate(track, track is null ? [] : [track], cover, notice: false);

    // A reading of the system, as SystemSampler hands it over: the open monitor page and the collapsed row's readings.
    internal void OffscreenMonitor(MonitorSnapshot snapshot)
    {
        // The readings that join the collapsed row with their first figure (the drives, the machine's power and battery) are admitted.
        AdmitStrip(snapshot);
        UpdateCompactStrips(snapshot, motion: false);
        _monitorView?.Update(snapshot, motion: false);
    }
}
