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
    private const int CollapsedMs = 66, OpenMs = 40, SweepMs = 33;
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

    public AmbientGlow(IslandBorder island, Panel content, Func<MotionConditions> conditions, Func<bool> open)
    {
        _island = island; _conditions = conditions; _open = open;
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
        bool run = _rim is not null && MotionGate.Ambient(_conditions(), _motion) && !HeldForBlur;
        if (run == _running) return;
        if (run) { _running = true; _origin = Eased.Now; MotionTicker.Add(this, Interval(-1)); }
        else Stop();
    }

    public void Stop()
    {
        if (_running) MotionTicker.Remove(this);
        _running = false;
        _sweep.Opacity = 0;
        if (_rim is not null) _rim.Opacity = 1;
        _drift.X = 0;
        _rimShown = 1; _driftShown = 0; _sweepShown = -1; _sweepOpacityShown = 0;
    }

    public int Tick(double now)
    {
        if (!_running) return 0;
        if (_rim is null || !MotionGate.Ambient(_conditions(), _motion) || HeldForBlur) { Stop(); return 0; }
        var frame = _motion.Ambient.At(now - _origin);
        // Changes below what eight bits of opacity can show are left for a later frame. The drift always moves on by more than
        // its threshold between ticks (cyberpunk: .006 of a width per collapsed tick against .0015), so a slower tick would
        // show as coarser steps; the interval stays the theme's.
        if (Math.Abs(frame.Rim - _rimShown) >= .002) _rim.Opacity = _rimShown = frame.Rim;
        if (Math.Abs(frame.Drift - _driftShown) >= .0015) _drift.X = _driftShown = frame.Drift;
        if (frame.Sweep >= 0)
        {
            if (Math.Abs(frame.Sweep - _sweepShown) >= .002) { _sweepShown = frame.Sweep; _sweepShift.Y = -.65 + 1.3 * frame.Sweep; }
            if (Math.Abs(frame.SweepOpacity - _sweepOpacityShown) >= .001) _sweep.Opacity = _sweepOpacityShown = frame.SweepOpacity;
        }
        else if (_sweepShown >= 0) { _sweepShown = -1; _sweepOpacityShown = 0; _sweep.Opacity = 0; }
        return Interval(frame.Sweep);
    }

    // Over a live blur (GlassBackdrop) every frame of the open panel's large layered window costs several times what it does over
    // clear glass (measured: 22 % of a core against 7 %), so there the light rests while the panel is open and moves on the
    // small collapsed island only.
    private bool HeldForBlur => _open() && GlassBackdrop.Available;

    // Slow light needs few frames: fewer still on the small collapsed island, a few more while a scan line moves.
    private int Interval(double sweep) => sweep >= 0 ? SweepMs : _open() ? OpenMs : CollapsedMs;
}
