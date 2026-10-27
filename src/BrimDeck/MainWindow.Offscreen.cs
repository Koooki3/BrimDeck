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
    private bool LiquidGlassShown => LiquidTheme.Shown(_glass);

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
        // The readings that join the collapsed row with their first figure (the drives, the machine's power and battery) are admitted.
        AdmitStrip(snapshot);
        _compactStrip?.Update(snapshot);
        _monitorView?.Update(snapshot, motion: false);
    }

    // What the glass decided for the last frame, and what the island made of it: liquid glass shown, the surface aside, the
    // surface's opacity, and whether the shadow's caster is opaque (MainWindow.LiquidShadow).
    internal (bool Shown, bool Aside, double Surface, bool Caster) OffscreenGlassState =>
        (_glass?.BodyShown == true, _glass?.SurfaceAside == true, _surface.Opacity,
            IslandShadow.Children.OfType<IslandBorder>().FirstOrDefault() is { } caster && ReferenceEquals(caster.Background, System.Windows.Media.Brushes.Black));

    // The pointer over the island (DIP, island coordinates; null: it left) and its button, as the island's mouse handlers pass
    // them on (GlassPress): the glass's hover and press and the jelly. The scale the jelly is going to (its animations only
    // cover the way), 1 and 1 at rest.
    internal void OffscreenPointer(Point? at, bool down) => GlassPress(at, down);
    internal (double X, double Y) OffscreenJelly => _jelly is null ? (1, 1)
        : ((double)_jelly.GetAnimationBaseValue(System.Windows.Media.ScaleTransform.ScaleXProperty), (double)_jelly.GetAnimationBaseValue(System.Windows.Media.ScaleTransform.ScaleYProperty));
    internal GlassBackdrop? OffscreenGlassBackdrop => _glass;
    // What lies behind the island, as the glass's measure would report it (GlassBackdrop.Measured: the 2nd and 98th percentiles
    // of its luma), taken at once; the shadow's tone and its two effects (ambient, contact: blur / depth / opacity) as now.
    internal double OffscreenBackdrop(float low, float high)
    {
        _shadowTone = _shadowToneTarget = ShadowTone(low, high);
        ApplyShadowLook();
        return _shadowTone;
    }
    internal string OffscreenShadowLook => IslandShadow.Effect is System.Windows.Media.Effects.DropShadowEffect a && IslandContact.Effect is System.Windows.Media.Effects.DropShadowEffect c
        ? FormattableString.Invariant($"ambient {a.BlurRadius:0.#}/{a.ShadowDepth:0.#}/{a.Opacity:0.000}, contact {(IslandContact.Visibility == System.Windows.Visibility.Visible ? FormattableString.Invariant($"{c.BlurRadius:0.#}/{c.ShadowDepth:0.#}/{c.Opacity:0.000}") : "off")}")
        : "-";

    // A frame of the glass, decided by GlassBackdrop through the stand-in: after every theme change or relayout, as the next
    // frame would in the app.
    internal void OffscreenLiquidFrame()
    {
        OffscreenGlass();
        _glass?.Update();
    }

    // A quota alert coming or ending as the coordinator hands it over (PresentAlert, EndAlert), without the coordinator: the
    // swap of the collapsed row that follows (MainWindow.RowSwap.cs). Whether the alert shows now.
    internal bool OffscreenAlert(AppEntry entry, bool shown)
    {
        if (shown) PresentAlert(new CompactAlertInfo(entry, entry.Name, "5 小时 ", 83, DateTimeOffset.Now.AddHours(2)));
        else if (_alert is { } alert) EndAlert(alert);
        return _alert is not null;
    }
}
