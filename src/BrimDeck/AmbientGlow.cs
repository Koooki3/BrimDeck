using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The theme's ambient light on one island (ThemeMotion.Ambient): the rim breathes, its colors drift along it, a faint scan line
// passes behind the content now and then, and the neon sags softly once in a long while. Only cheap properties change: the
// opacity and transform of an unfrozen copy of the rim brush and the opacity and transform of one rectangle. The glow (the
// island's shadow effect) never changes: it is drawn from a cached picture (MainWindow.FitShadowCache), which any change to
// it would have to blur again. It runs on MotionTicker only while MotionGate.Ambient allows it and stops by itself otherwise;
// stopping puts everything back at rest.
internal sealed class AmbientGlow : IMotionClient
{
    // The collapsed island ticks slowest: the light is slow, and every changed frame redraws the layered window.
    private const int CollapsedMs = 100, OpenMs = 40, SweepMs = 33;
    // A change is drawn only once it shows: the drift and the scan line once they have moved this many device pixels, the
    // breathing once the rim's opacity has changed by one step of eight bits.
    private const double MinPixels = .75, MinOpacity = 1 / 255d;
    private readonly IslandBorder _island;
    private readonly Func<MotionConditions> _conditions;
    private readonly Func<bool> _open;
    // Always in the layout and only ever faded, so a scan line coming or going does not lay out the island's content again.
    private readonly Rectangle _sweep = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly TranslateTransform _drift = new(), _sweepShift = new();
    private ThemeMotion _motion = ThemeMotion.Default;
    private Brush? _rim;
    private double _origin, _rimShown = 1, _driftShown, _sweepShown = -1, _sweepOpacityShown;
    private bool _running;
    // When the light was paused (Eased time), so it goes on from the same moment of its cycle when it runs again.
    private double? _pausedAt;

    // liquid: whether liquid glass is shown under the island, which draws its own rim light (following the pointer): the
    // island's painted rim steps aside then (MainWindow.LiquidEdge), and its drift would only redraw the window for nothing.
    private readonly Func<bool> _liquid;
    public AmbientGlow(IslandBorder island, Panel content, Func<MotionConditions> conditions, Func<bool> open, Func<bool>? liquid = null)
    {
        _island = island; _conditions = conditions; _open = open; _liquid = liquid ?? (() => false);
        // Behind everything on the surface, above its own layers.
        content.Children.Insert(0, _sweep);
    }

    // Takes the current theme; call after PanelTheme.Dress has given the island its new rim.
    public void Apply()
    {
        // The old light comes to rest first: its drift and opacity live on the rim brush being replaced and on _drift, which
        // the new rim shares, so a light halted mid-drift would otherwise leave the new rim offset.
        Stop();
        var palette = PanelTheme.Current;
        _motion = palette.Motion;
        _rim = null; _sweep.Fill = null;
        if (_motion.Ambient.Any)
        {
            // An unfrozen copy that reflects past its ends, so the drift can move it any distance without a seam.
            var rim = PanelTheme.RimBrush(palette).Clone();
            if (rim is LinearGradientBrush gradient) { gradient.SpreadMethod = GradientSpreadMethod.Reflect; gradient.RelativeTransform = _drift; }
            _island.RimBrush = _rim = rim;
            var color = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, _motion.Color));
            var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), RelativeTransform = _sweepShift };
            band.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), .38));
            band.GradientStops.Add(new GradientStop(color, .5));
            band.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), .62));
            _sweep.Fill = band;
        }
        Update();
    }

    // Starts or stops the light for the current conditions.
    public void Update()
    {
        var conditions = _conditions();
        bool run = _rim is not null && MotionGate.Ambient(conditions, _motion) && !HeldForBlur;
        if (run == _running)
        {
            // Paused, and now motion is not allowed at all (animations turned off, high contrast): at rest at once.
            if (!run && _pausedAt is not null && !conditions.MotionAllowed) Stop();
            return;
        }
        if (run)
        {
            _running = true;
            _origin = _pausedAt is { } paused ? _origin + (Eased.Now - paused) : Eased.Now;
            _pausedAt = null;
            _sweep.BeginAnimation(UIElement.OpacityProperty, null); _sweep.Opacity = 0;
            MotionTicker.Add(this, Interval(-1));
        }
        else Halt(conditions);
    }

    // Motion still allowed (the panel opening over glass, the indicator line, a locked session, battery saver, the display off):
    // the light holds where it is rather than jumping back to rest, and a passing scan line fades out. With motion not allowed
    // (animations off, high contrast) it is put at rest at once.
    private void Halt(in MotionConditions conditions)
    {
        if (!conditions.MotionAllowed || _rim is null) { Stop(); return; }
        if (_running) MotionTicker.Remove(this);
        _running = false;
        _pausedAt = Eased.Now;
        if (_sweep.Opacity > 0) _sweep.BeginAnimation(UIElement.OpacityProperty, Soft.Fade(_sweep.Opacity, 0, LeaveMs, Soft.Out));
        _sweepShown = -1; _sweepOpacityShown = 0;
    }
    private const int LeaveMs = 200;

    public void Stop()
    {
        if (_running) MotionTicker.Remove(this);
        _running = false;
        _pausedAt = null;
        _sweep.BeginAnimation(UIElement.OpacityProperty, null); _sweep.Opacity = 0;
        if (_rim is not null) _rim.Opacity = 1;
        _drift.X = 0;
        _rimShown = 1; _driftShown = 0; _sweepShown = -1; _sweepOpacityShown = 0;
    }

    public int Tick(double now)
    {
        if (!_running) return 0;
        var conditions = _conditions();
        if (_rim is null || !MotionGate.Ambient(conditions, _motion) || HeldForBlur) { Halt(conditions); return 0; }
        var frame = _motion.Ambient.At(now - _origin);
        // Changes the screen cannot show yet (under a device pixel of movement, under a step of opacity) are left for a later
        // tick, so a tick that would change nothing visible redraws nothing: the drift is in widths of the rim, the scan line
        // in 1.3 heights of the island.
        double scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
        double width = Math.Max(1, _island.ActualWidth * scale), height = Math.Max(1, _island.ActualHeight * scale);
        if (Math.Abs(frame.Rim - _rimShown) >= MinOpacity) _rim.Opacity = _rimShown = frame.Rim;
        if (Math.Abs(frame.Drift - _driftShown) * width >= MinPixels) _drift.X = _driftShown = frame.Drift;
        if (frame.Sweep >= 0)
        {
            if (Math.Abs(frame.Sweep - _sweepShown) * 1.3 * height >= MinPixels) { _sweepShown = frame.Sweep; _sweepShift.Y = -.65 + 1.3 * frame.Sweep; }
            if (Math.Abs(frame.SweepOpacity - _sweepOpacityShown) >= MinOpacity) _sweep.Opacity = _sweepOpacityShown = frame.SweepOpacity;
        }
        else if (_sweepShown >= 0) { _sweepShown = -1; _sweepOpacityShown = 0; _sweep.Opacity = 0; }
        return Interval(frame.Sweep);
    }

    // Over a live blur (GlassBackdrop) each frame of the open panel's large layered window costs more, and the drifting light
    // alone kept the open panel at 7.5 % of a core against 2.7 % with it at rest (measured). There the light rests while the
    // panel is open and moves on the small collapsed island only. Over liquid glass it rests always: the glass draws its own
    // rim light, and the painted rim it would move is not drawn.
    private bool HeldForBlur => _open() && GlassBackdrop.Available || _liquid();

    // Slow light needs few frames: fewer still on the small collapsed island, a few more while a scan line moves.
    private int Interval(double sweep) => sweep >= 0 ? SweepMs : _open() ? OpenMs : CollapsedMs;
}
