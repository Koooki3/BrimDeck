namespace BrimDeck.Core;

// The motion a theme gives BrimDeck's own windows: slow ambient light on the island (a breathing rim and glow, colors drifting
// along the rim, a scan line now and then, a rare soft sag of the neon), and how the pointer is answered (a light under the
// pointer on what it hovers, a ripple where it presses, a faint light that trails it, a bloom where keyboard focus lands).
// The file's "motion" section is merged field by field along the base chain, like the effects, and every value is held to
// MotionSafety's limits, so no theme file can make anything flash. Nothing here runs by itself: BrimDeck only animates while
// MotionGate allows it.
public enum HoverFx { None, Spot, Edge, Lift, Neon }
public enum PressFx { None, Pulse, Ripple, Ring }
public enum FocusFx { None, Ring, Glow, Neon }
// The particles a moving pointer leaves over the open panel: dust (soft motes), pixel (small squares, as a contribution graph),
// spark (short streaks along the movement), star (four-point twinkles), firefly (warm lights drifting up and glowing slowly),
// glitch (thin neon dashes that jitter).
public enum ParticleFx { None, Dust, Pixel, Spark, Star, Firefly, Glitch }

// Periods and lengths are seconds; 0 turns an effect off.
public sealed record AmbientMotion(double Breathe, double BreatheDepth, double Drift, double Sweep, double SweepOpacity, double Flicker, double FlickerDepth)
{
    public static readonly AmbientMotion None = new(0, 0, 0, 0, 0, 0, 0);
    public bool Any => Breathe > 0 && BreatheDepth > 0 || Drift > 0 || Sweep > 0 && SweepOpacity > 0 || Flicker > 0 && FlickerDepth > 0;

    // The ambient light at a time in seconds. At time 0 every value is at rest (Rim and Glow 1, no sweep), so starting the clock
    // never jumps. Allocation-free: it runs on every ambient frame.
    public AmbientFrame At(double t)
    {
        if (!double.IsFinite(t) || t < 0) t = 0;
        double rim = 1, glow = 1, drift = 0, sweep = -1, sweepOpacity = 0;
        if (Breathe > 0 && BreatheDepth > 0)
        {
            // A cosine from the top, so the light only ever dims a little from its resting brightness and comes back. The glow
            // trails the rim by an eighth of a breath, which reads as the light spilling outward rather than as both blinking.
            double rimPhase = t / Breathe, glowPhase = Math.Max(0, rimPhase - .12);
            rim = 1 - BreatheDepth * (.5 - .5 * Math.Cos(2 * Math.PI * rimPhase));
            glow = 1 - Math.Min(.45, BreatheDepth * 1.2) * (.5 - .5 * Math.Cos(2 * Math.PI * glowPhase));
        }
        if (Flicker > 0 && FlickerDepth > 0)
        {
            // One soft dip per interval, at a place in it that changes from one interval to the next.
            double length = MotionSafety.FlickerLength, k = Math.Floor(t / Flicker);
            double start = k * Flicker + Scatter(k) * Math.Max(0, Flicker - length);
            if (t >= start && t < start + length)
            {
                double s = Math.Sin(Math.PI * (t - start) / length), dip = FlickerDepth * s * s;
                rim *= 1 - dip; glow *= 1 - dip * .6;
            }
        }
        // The rim's colors travel one width per Drift seconds; the brush reflects, so a loop of two widths is seamless.
        if (Drift > 0) drift = t / Drift % 2;
        if (Sweep > 0 && SweepOpacity > 0)
        {
            // At the end of each interval, so the first pass comes a while after the light starts.
            double x = (t % Sweep - (Sweep - MotionSafety.SweepLength)) / MotionSafety.SweepLength;
            if (x is >= 0 and < 1)
            {
                sweep = Curves.Bezier(.45, 0, .25, 1, x);
                double s = Math.Sin(Math.PI * x);
                sweepOpacity = SweepOpacity * s * s;
            }
        }
        return new AmbientFrame(rim, glow, drift, sweep, sweepOpacity);
    }

    // A fixed pseudo-random fraction in [0.1, 0.9] for an interval index.
    private static double Scatter(double k)
    {
        ulong h = (ulong)(long)k * 0x9E3779B97F4A7C15UL;
        h ^= h >> 29; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 32;
        return .1 + .8 * (h % 10_000) / 10_000.0;
    }
}

// Rim and Glow multiply the theme's rim and glow opacity; Drift moves the rim colors (0 to 2 widths); Sweep is where the scan
// line is, from 0 (top) to 1 (bottom), or -1 while none passes, with SweepOpacity its strength.
public readonly record struct AmbientFrame(double Rim, double Glow, double Drift, double Sweep, double SweepOpacity);

// Color is a theme color key ("accent", "rim", "shadow", ...), a "#RRGGBB" color, or "system" for the Windows accent color.
// Secondary is the second color of the neon styles. Pace scales every interaction duration.
public sealed record ThemeMotion(AmbientMotion Ambient, HoverFx Hover, double HoverStrength, PressFx Press, double PressStrength,
    double Trail, FocusFx Focus, double Pace, string Color, string Secondary, ParticleFx Particles = ParticleFx.Dust)
{
    public const string SystemAccent = "system";
    public static readonly ThemeMotion Default = new(AmbientMotion.None, HoverFx.Spot, .1, PressFx.Pulse, .16, 0, FocusFx.Glow, 1, "accent", "accent");
    public bool AnyInteraction => Hover != HoverFx.None && HoverStrength > 0 || Press != PressFx.None && PressStrength > 0 || Trail > 0 || Focus != FocusFx.None;

    // "system" follows Windows: its accent color and the quietest answers, with no ambient light. The base theme's pace stays.
    public ThemeMotion ForSystem() => this with
    {
        Ambient = AmbientMotion.None, Hover = HoverFx.Spot, HoverStrength = .08, Press = PressFx.Pulse, PressStrength = .12, Trail = .26, Particles = ParticleFx.Dust,
        Focus = FocusFx.Ring, Color = SystemAccent, Secondary = SystemAccent
    };

    // A motion color as the palette draws it: a key of the palette, a color, or the Windows accent (systemAccent; the palette's
    // accent when unknown). Kept readable on the surface like any color chosen outside the theme.
    public static ThemeColor ResolveColor(PanelPalette palette, string value, ThemeColor? systemAccent = null)
    {
        ThemeColor color;
        if (string.Equals(value, SystemAccent, StringComparison.OrdinalIgnoreCase)) color = systemAccent ?? palette["accent"];
        else if (palette.Colors.Keys.FirstOrDefault(key => string.Equals(key, value, StringComparison.OrdinalIgnoreCase)) is { } key) color = palette[key];
        else if (!ThemeColor.TryParse(value, out color)) color = palette["accent"];
        return palette.Readable(color.Over(palette.Surface), PanelThemes.GraphicContrast);
    }
}

// The limits every theme is held to. Brightness never changes faster than FastestHz allows (WCAG 2.3: no more than three
// flashes in any second); the built-in themes stay below one change a second.
public static class MotionSafety
{
    public const double MaxFlashHz = 3;
    public const double BreatheMin = 3, BreatheMax = 30, BreatheDepthMax = .35, DriftMin = 4, DriftMax = 60;
    public const double SweepMin = 6, SweepMax = 120, SweepLength = 2.4, SweepOpacityMax = .12;
    public const double FlickerMin = 20, FlickerMax = 300, FlickerLength = 1.2, FlickerDepthMax = .15;
    public const double HoverMax = .3, PressMax = .35, TrailMax = .6, PaceMin = .6, PaceMax = 1.6;

    // A period: 0 (or less) turns the effect off; anything else lies within its range.
    public static double Period(double? value, double min, double max, double fallback)
        => value is not { } v || !double.IsFinite(v) ? fallback : v <= 0 ? 0 : Math.Clamp(v, min, max);
    public static double Amount(double? value, double max, double fallback) => value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0, max) : fallback;

    // The quickest cycle of brightness the ambient light can make, in cycles per second.
    public static double FastestHz(AmbientMotion a) => Math.Max(Math.Max(
        a.Breathe > 0 && a.BreatheDepth > 0 ? 1 / a.Breathe : 0,
        a.Flicker > 0 && a.FlickerDepth > 0 ? 1 / FlickerLength : 0),
        a.Sweep > 0 && a.SweepOpacity > 0 ? 1 / SweepLength : 0);

    // The most flashes (a rise and a fall of at least `threshold`) in any one-second window over the given span, in the rim's
    // brightness, the glow's or the scan line's, sampled at 120 Hz.
    public static int PeakFlashes(AmbientMotion a, double seconds = 120, double threshold = .01)
    {
        int peak = 0;
        foreach (var channel in new Func<AmbientFrame, double>[] { f => f.Rim, f => f.Glow, f => f.SweepOpacity })
            peak = Math.Max(peak, PeakFlashes(a, channel, seconds, threshold));
        return peak;
    }

    private static int PeakFlashes(AmbientMotion a, Func<AmbientFrame, double> channel, double seconds, double threshold)
    {
        const double rate = 120;
        var turns = new List<double>();
        {
            double last = channel(a.At(0)), extreme = last; int direction = 0;
            for (int i = 1; i <= seconds * rate; i++)
            {
                double t = i / rate, v = channel(a.At(t));
                int now = Math.Sign(v - last);
                if (now != 0 && now != direction)
                {
                    if (direction != 0 && Math.Abs(last - extreme) >= threshold) { turns.Add(t); extreme = last; }
                    else if (direction == 0) extreme = last;
                    direction = now;
                }
                last = v;
            }
        }
        turns.Sort();
        int peak = 0;
        for (int i = 0, j = 0; i < turns.Count; i++)
        {
            while (turns[i] - turns[j] > 1) j++;
            peak = Math.Max(peak, i - j + 1);
        }
        // Two turns make one flash.
        return (peak + 1) / 2;
    }
}

// Whether motion may run now. Ambient light is a loop, so it needs the island on screen, the session in front of the user and
// power to spare; the answers to the pointer only need motion to be allowed.
public readonly record struct MotionConditions(bool Animations, bool SystemAnimations, bool HighContrast, bool Visible, bool Suppressed,
    bool PoweringOff, bool Locked, bool BatterySaver, bool HasMaterial);

public static class MotionGate
{
    public static bool Interaction(in MotionConditions c, ThemeMotion motion) => c.Animations && c.SystemAnimations && !c.HighContrast && motion.AnyInteraction;
    public static bool Ambient(in MotionConditions c, ThemeMotion motion) => c.Animations && c.SystemAnimations && !c.HighContrast
        && c.Visible && !c.Suppressed && !c.PoweringOff && !c.Locked && !c.BatterySaver && c.HasMaterial && motion.Ambient.Any;
}

// The interaction durations in milliseconds, from the animation length chosen in settings (100 to 1000 ms, 360 by default) and
// the theme's pace: at the default a hover arrives in about the monitor's hover time (Eased.Hover) and a ripple lasts half a second.
public readonly record struct MotionTiming(int HoverIn, int HoverOut, int Glide, int Press, int Focus)
{
    public static MotionTiming From(int animationDuration, double pace)
    {
        double d = Math.Clamp(animationDuration, 100, 1000) * Math.Clamp(double.IsFinite(pace) ? pace : 1, MotionSafety.PaceMin, MotionSafety.PaceMax);
        static int Ms(double value, double min, double max) => (int)Math.Round(Math.Clamp(value, min, max));
        return new(Ms(d * .45, 80, 450), Ms(d * .75, 120, 700), Ms(d * .6, 90, 600), Ms(d * 1.4, 240, 1400), Ms(d * 1.6, 300, 1600));
    }
}

// The "motion" section of a theme file. Everything is optional and comes from the base theme when left out.
public sealed class PanelThemeMotion
{
    public PanelThemeAmbient? Ambient { get; set; }
    // none, spot (a light under the pointer), edge (a thin outline), lift (a soft shadow ring and sheen), neon (light and outline)
    public string? Hover { get; set; }
    public double? HoverStrength { get; set; }
    // none, pulse (the control glows once), ripple (a disc spreading from the pointer), ring (a spreading ring)
    public string? Press { get; set; }
    public double? PressStrength { get; set; }
    // 0 to 0.6: how strongly the particles show (0: none)
    public double? Trail { get; set; }
    // none, dust, pixel, spark, star, firefly, glitch
    public string? Particles { get; set; }
    // none, ring, glow, neon: a bloom around the control keyboard focus moves to
    public string? Focus { get; set; }
    public double? Pace { get; set; }
    public string? Color { get; set; }
    public string? Secondary { get; set; }
}

public sealed class PanelThemeAmbient
{
    public double? Breathe { get; set; }
    public double? BreatheDepth { get; set; }
    public double? Drift { get; set; }
    public double? Sweep { get; set; }
    public double? SweepOpacity { get; set; }
    public double? Flicker { get; set; }
    public double? FlickerDepth { get; set; }
}

public static partial class PanelThemes
{
    // The base chain's motion sections, merged from the dark theme up and held to MotionSafety.
    private static ThemeMotion MotionValues(IEnumerable<PanelThemeFile> chain)
    {
        var m = ThemeMotion.Default;
        var a = m.Ambient;
        foreach (var layer in chain)
        {
            if (layer.Motion is not { } motion) continue;
            if (motion.Ambient is { } ambient)
                a = new AmbientMotion(
                    MotionSafety.Period(ambient.Breathe, MotionSafety.BreatheMin, MotionSafety.BreatheMax, a.Breathe),
                    MotionSafety.Amount(ambient.BreatheDepth, MotionSafety.BreatheDepthMax, a.BreatheDepth),
                    MotionSafety.Period(ambient.Drift, MotionSafety.DriftMin, MotionSafety.DriftMax, a.Drift),
                    MotionSafety.Period(ambient.Sweep, MotionSafety.SweepMin, MotionSafety.SweepMax, a.Sweep),
                    MotionSafety.Amount(ambient.SweepOpacity, MotionSafety.SweepOpacityMax, a.SweepOpacity),
                    MotionSafety.Period(ambient.Flicker, MotionSafety.FlickerMin, MotionSafety.FlickerMax, a.Flicker),
                    MotionSafety.Amount(ambient.FlickerDepth, MotionSafety.FlickerDepthMax, a.FlickerDepth));
            m = m with
            {
                Hover = Enum.TryParse<HoverFx>(motion.Hover, true, out var hover) && Enum.IsDefined(hover) ? hover : m.Hover,
                HoverStrength = MotionSafety.Amount(motion.HoverStrength, MotionSafety.HoverMax, m.HoverStrength),
                Press = Enum.TryParse<PressFx>(motion.Press, true, out var press) && Enum.IsDefined(press) ? press : m.Press,
                PressStrength = MotionSafety.Amount(motion.PressStrength, MotionSafety.PressMax, m.PressStrength),
                Trail = MotionSafety.Amount(motion.Trail, MotionSafety.TrailMax, m.Trail),
                Particles = Enum.TryParse<ParticleFx>(motion.Particles, true, out var particles) && Enum.IsDefined(particles) ? particles : m.Particles,
                Focus = Enum.TryParse<FocusFx>(motion.Focus, true, out var focus) && Enum.IsDefined(focus) ? focus : m.Focus,
                Pace = motion.Pace is { } pace && double.IsFinite(pace) ? Math.Clamp(pace, MotionSafety.PaceMin, MotionSafety.PaceMax) : m.Pace,
                Color = motion.Color is { Length: > 0 } color ? color.Trim() : m.Color,
                Secondary = motion.Secondary is { Length: > 0 } secondary ? secondary.Trim() : m.Secondary
            };
        }
        return m with { Ambient = a };
    }
}
