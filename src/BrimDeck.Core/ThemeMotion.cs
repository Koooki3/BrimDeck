namespace BrimDeck.Core;

// The motion a theme gives BrimDeck's own windows: slow ambient light on the island (a breathing rim, colors drifting
// along the rim, a scan line now and then, a rare soft sag of the neon), and how the pointer is answered: a mark on the control
// it hovers, drawn on the control's own visible shape; a press that answers on the button going down, holds while it is held
// and completes (or backs out) on release; and a short accent where keyboard focus lands. Nothing follows the pointer.
// The file's "motion" section is merged field by field along the base chain, like the effects, and every value is held to
// MotionSafety's limits, so no theme file can make anything flash. Nothing here runs by itself: BrimDeck only animates while
// MotionGate allows it.

// The mark on a hovered control. reveal: a faint fill and a 1 px edge lit from the top (Fluent); lift: a soft shadow outside
// the shape and a light top edge (a light theme's raised card); underline: a subtle fill and a 2 px bar along the bottom
// (Primer); caret: an editor's line highlight and a caret bar at the left; brackets: four HUD corner brackets that snap on in
// steps; sheen: a two-color tint and a fine gradient edge; glow: a warm inner glow and a glint at a corner.
public enum HoverFx { None, Reveal, Lift, Underline, Caret, Brackets, Sheen, Glow }
// The press. shade: the shape darkens or brightens a little and its edge firms; sink: the shadow collapses and the shape is
// shaded as if pushed in; indicator: the bottom bar turns the highlight color; select: a selection sweeps across from the
// caret; lock: the brackets clamp onto the shape with a chromatic ghost and a short glitch slice, and a scan line passes on
// release; sparkle: the tint deepens and a few stars twinkle on the corners on release; ember: a warm glow swells from the
// middle and fireflies rise from the top on release.
public enum PressFx { None, Shade, Sink, Indicator, Select, Lock, Sparkle, Ember }
// A short accent where keyboard focus lands, over the focus outline the control keeps: a ring, a soft glow, brackets or a caret.
public enum FocusFx { None, Ring, Glow, Brackets, Caret }
// What a completed press leaves for a moment, from the control's own edges (never where the pointer is): dust (soft motes),
// pixel (small squares, as a contribution graph), spark (short dashes), star (four-point twinkles on the corners), firefly
// (warm lights rising slowly from the top edge), neon (round motes in two neon colors).
public enum ParticleFx { None, Dust, Pixel, Spark, Star, Firefly, Neon }

// Style names in theme files, with the names of earlier versions read as the closest new style.
public static class MotionStyles
{
    private static readonly Dictionary<string, HoverFx> HoverAliases = new(StringComparer.OrdinalIgnoreCase) { ["spot"] = HoverFx.Reveal, ["edge"] = HoverFx.Underline, ["neon"] = HoverFx.Brackets };
    private static readonly Dictionary<string, PressFx> PressAliases = new(StringComparer.OrdinalIgnoreCase) { ["pulse"] = PressFx.Shade, ["ripple"] = PressFx.Shade, ["ring"] = PressFx.Lock };
    private static readonly Dictionary<string, FocusFx> FocusAliases = new(StringComparer.OrdinalIgnoreCase) { ["neon"] = FocusFx.Brackets };

    public static HoverFx Hover(string? value, HoverFx fallback) => Parse(value, HoverAliases, fallback);
    public static PressFx Press(string? value, PressFx fallback) => Parse(value, PressAliases, fallback);
    public static FocusFx Focus(string? value, FocusFx fallback) => Parse(value, FocusAliases, fallback);
    public static ParticleFx Particles(string? value, ParticleFx fallback) => Parse(value, null, fallback);

    // A name only: numbers ("7") are not styles, whatever the enum would make of them.
    private static T Parse<T>(string? value, Dictionary<string, T>? aliases, T fallback) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        value = value.Trim();
        if (!char.IsLetter(value[0])) return fallback;
        if (aliases is not null && aliases.TryGetValue(value, out var alias)) return alias;
        return Enum.TryParse<T>(value, true, out var style) && Enum.IsDefined(style) ? style : fallback;
    }
}

// Periods and lengths are seconds; 0 turns an effect off.
public sealed record AmbientMotion(double Breathe, double BreatheDepth, double Drift, double Sweep, double SweepOpacity, double Flicker, double FlickerDepth)
{
    public static readonly AmbientMotion None = new(0, 0, 0, 0, 0, 0, 0);
    public bool Any => Breathe > 0 && BreatheDepth > 0 || Drift > 0 || Sweep > 0 && SweepOpacity > 0 || Flicker > 0 && FlickerDepth > 0;

    // The ambient light at a time in seconds. At time 0 every value is at rest (Rim 1, no sweep), so starting the clock never
    // jumps. Allocation-free: it runs on every ambient frame.
    public AmbientFrame At(double t)
    {
        if (!double.IsFinite(t) || t < 0) t = 0;
        double rim = 1, drift = 0, sweep = -1, sweepOpacity = 0;
        // A cosine from the top, so the light only ever dims a little from its resting brightness and comes back.
        if (Breathe > 0 && BreatheDepth > 0) rim = 1 - BreatheDepth * (.5 - .5 * Math.Cos(2 * Math.PI * t / Breathe));
        if (Flicker > 0 && FlickerDepth > 0)
        {
            // One soft dip per interval, at a place in it that changes from one interval to the next.
            double length = MotionSafety.FlickerLength, k = Math.Floor(t / Flicker);
            double start = k * Flicker + Scatter(k) * Math.Max(0, Flicker - length);
            if (t >= start && t < start + length)
            {
                double s = Math.Sin(Math.PI * (t - start) / length), dip = FlickerDepth * s * s;
                rim *= 1 - dip;
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
        return new AmbientFrame(rim, drift, sweep, sweepOpacity);
    }

    // A fixed pseudo-random fraction in [0.1, 0.9] for an interval index.
    private static double Scatter(double k)
    {
        ulong h = (ulong)(long)k * 0x9E3779B97F4A7C15UL;
        h ^= h >> 29; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 32;
        return .1 + .8 * (h % 10_000) / 10_000.0;
    }
}

// Rim multiplies the theme's rim opacity; Drift moves the rim colors (0 to 2 widths); Sweep is where the scan line is, from 0
// (top) to 1 (bottom), or -1 while none passes, with SweepOpacity its strength. The glow (the island's shadow) never changes:
// any change to it has WPF blur it again (AmbientGlow).
public readonly record struct AmbientFrame(double Rim, double Drift, double Sweep, double SweepOpacity);

// Colors are theme color keys ("accent", "rim", "shadow", ...), "#RRGGBB" colors, or "system" for the Windows accent color.
// Color is the main mark (hover and focus); Secondary its second tone (a gradient's end, a shadow, a ghost, a neutral bar);
// Highlight the color of the moment of a press (a scan line, an indicator, a selection, sparkles, fireflies).
// ParticleStrength is how strongly the particles a completed press leaves show (0: none); the theme file calls it "trail".
// Pace scales every interaction duration. Steps quantizes the motion into that many hard steps (0: smooth), for a mechanical
// theme.
public sealed record ThemeMotion(AmbientMotion Ambient, HoverFx Hover, double HoverStrength, PressFx Press, double PressStrength,
    double ParticleStrength, FocusFx Focus, double Pace, string Color, string Secondary, ParticleFx Particles = ParticleFx.None, int Steps = 0, string Highlight = "accent")
{
    public const string SystemAccent = "system";
    public static readonly ThemeMotion Default = new(AmbientMotion.None, HoverFx.Reveal, .12, PressFx.Shade, .18, 0, FocusFx.Ring, 1, "accent", "accent");
    public bool AnyInteraction => Hover != HoverFx.None && HoverStrength > 0 || Press != PressFx.None && PressStrength > 0 || ParticleStrength > 0 && Particles != ParticleFx.None || Focus != FocusFx.None;

    // "system" follows Windows: its accent color and the quiet Fluent answers (a light theme's lift and sink stay), with no
    // ambient light, particles or steps. The base theme's strengths and pace stay.
    public ThemeMotion ForSystem() => this with
    {
        Ambient = AmbientMotion.None, Hover = Hover == HoverFx.Lift ? HoverFx.Lift : HoverFx.Reveal, Press = Press == PressFx.Sink ? PressFx.Sink : PressFx.Shade,
        ParticleStrength = 0, Particles = ParticleFx.None, Steps = 0, Focus = FocusFx.Ring, Color = SystemAccent, Secondary = Hover == HoverFx.Lift ? Secondary : SystemAccent, Highlight = SystemAccent
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
    // Stepped motion: 0 is smooth, otherwise 2 to 8 steps. A step moves a mark; brightness changes that read as a flash (a glitch
    // slice, a scan line) are rationed by FlashBudget to PressFlashes in any second, however fast the clicks come.
    public const int StepsMin = 2, StepsMax = 8, PressFlashes = 2;

    public static int Steps(int? value, int fallback) => value is not { } v ? fallback : v < StepsMin ? 0 : Math.Min(v, StepsMax);

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
    // brightness or the scan line's, sampled at 120 Hz.
    public static int PeakFlashes(AmbientMotion a, double seconds = 120, double threshold = .01)
    {
        int peak = 0;
        foreach (var channel in new Func<AmbientFrame, double>[] { f => f.Rim, f => f.SweepOpacity })
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

// Whether motion may run now. Animations, SystemAnimations and HighContrast make the app-wide rule (Soft.Enabled): BrimDeck's
// setting, Windows' "animation effects", and no motion in high contrast. Ambient light is a loop, so it also needs the island
// on screen, the session in front of the user with its display on, and power to spare; the answers to the pointer only need
// motion to be allowed.
public readonly record struct MotionConditions(bool Animations, bool SystemAnimations, bool HighContrast, bool Visible, bool Suppressed,
    bool PoweringOff, bool Locked, bool BatterySaver, bool HasMaterial, bool DisplayOff = false)
{
    public bool MotionAllowed => Allowed(Animations, SystemAnimations, HighContrast);
    // The rule itself, for Soft.Enabled and these conditions alike.
    public static bool Allowed(bool animations, bool systemAnimations, bool highContrast) => animations && systemAnimations && !highContrast;
}

public static class MotionGate
{
    public static bool Interaction(in MotionConditions c, ThemeMotion motion) => c.MotionAllowed && motion.AnyInteraction;
    public static bool Ambient(in MotionConditions c, ThemeMotion motion) => c.MotionAllowed
        && c.Visible && !c.Suppressed && !c.PoweringOff && !c.Locked && !c.DisplayOff && !c.BatterySaver && c.HasMaterial && motion.Ambient.Any;
}

// The interaction durations in milliseconds, from the animation length chosen in settings (100 to 1000 ms, 360 by default) and
// the theme's pace: at the default a hover arrives in about the monitor's hover time (Eased.Hover), a press goes down in under
// a tenth of a second and lets go in under a fifth, and what a completed press leaves lasts about half a second.
// PressIn: the button going down. PressOut: letting go. Press: what a completed press leaves (particles, a scan line).
// Glitch: the length of a glitch slice, a few frames.
public readonly record struct MotionTiming(int HoverIn, int HoverOut, int Glide, int Press, int Focus, int PressIn, int PressOut, int Glitch)
{
    public static MotionTiming From(int animationDuration, double pace)
    {
        double d = AnimationLength.Clamp(animationDuration) * Math.Clamp(double.IsFinite(pace) ? pace : 1, MotionSafety.PaceMin, MotionSafety.PaceMax);
        static int Ms(double value, double min, double max) => AnimationLength.Ms(value, min, max);
        return new(Ms(d * .45, 80, 450), Ms(d * .75, 120, 700), Ms(d * .6, 90, 600), Ms(d * 1.4, 240, 1400), Ms(d * 1.6, 300, 1600),
            Ms(d * .22, 40, 200), Ms(d * .5, 80, 450), Ms(d * .2, 50, 90));
    }

    // What the windows use (motion-review C5): the answers to the pointer and the keyboard keep their own pace, whatever the
    // panel's animation length, as the monitor page's hover does: the timings at the default length, with a smooth theme's
    // hover coming and going in the monitor's hover time (Eased.Hover) times its pace. A stepped theme keeps its quick arrival
    // and its slower, stepped way out (F14), so it still reads as mechanical.
    public static MotionTiming For(double pace, int steps)
    {
        var timing = From(AnimationLength.Default, pace);
        if (steps >= MotionSafety.StepsMin) return timing;
        int hover = AnimationLength.Ms(Eased.Hover * 1000 * Math.Clamp(double.IsFinite(pace) ? pace : 1, MotionSafety.PaceMin, MotionSafety.PaceMax), 80, 450);
        return timing with { HoverIn = hover, HoverOut = hover };
    }
}

// The animation length chosen in settings (DeckSettings.AnimationDuration), in milliseconds: its default and its range, and the
// rounding every duration derived from it takes. The one place these numbers live.
public static class AnimationLength
{
    public const int Default = 360, Min = 100, Max = 1000;
    public static int Clamp(int milliseconds) => Math.Clamp(milliseconds, Min, Max);
    // A derived duration held to its own range, in whole milliseconds.
    public static int Ms(double value, double min, double max) => (int)Math.Round(Math.Clamp(value, min, max));
}

// Progress along a timeline: smooth, or in hard steps that jump at the start of each step (so the first frame already moves).
public static class MotionCurve
{
    public static double Linear(double elapsed, double duration) => duration <= 0 ? 1 : Math.Clamp(elapsed / duration, 0, 1);
    public static double Step(double t, int steps) => steps < MotionSafety.StepsMin || t <= 0 || t >= 1 ? Math.Clamp(t, 0, 1) : Math.Ceiling(t * steps) / steps;
    // The theme's curve: steps (linear within them) or the soft landing the rest of BrimDeck uses (Soft.Out).
    public static double Ease(double t, int steps) => steps >= MotionSafety.StepsMin ? Step(t, steps) : Curves.Bezier(.22, .8, .28, 1, Math.Clamp(t, 0, 1));
    // For what fades out: the same soft landing, leaving at once (motion-review F13; it started slowly, as Soft.InOut), or steps.
    // Its flat end is taken as the end, so what fades is gone exactly when its time is up rather than a rounding error later.
    public static double EaseOut(double t, int steps) { double e = Ease(t, steps); return e > 1 - 1e-9 ? 1 : e; }
}

// Rations what reads as a flash (a glitch slice, a scan line): at most MotionSafety.PressFlashes start in any one second,
// however fast the clicks come; the others are left out and the press still answers with its steady marks.
public sealed class FlashBudget
{
    private readonly double[] _starts = new double[MotionSafety.PressFlashes];
    private int _next;
    public FlashBudget() => Array.Fill(_starts, double.NegativeInfinity);
    public bool TryTake(double now)
    {
        // The oldest of the last few starts must be over a second ago.
        if (now - _starts[_next] < 1) return false;
        _starts[_next] = now;
        _next = (_next + 1) % _starts.Length;
        return true;
    }
    public void Clear() { Array.Fill(_starts, double.NegativeInfinity); _next = 0; }
}

// A press on one control, from the button going down to the end of what it leaves. Times are seconds on one clock.
// Down: the press goes down at once and is held while the button is (Held, 0 to 1); if the pointer leaves the control while
// it is held the press backs off and comes back when it returns (Armed). Released on the control it completes (Committed):
// the held state lets go and the flourish plays; released elsewhere, or when the capture is lost, it only lets go.
public sealed class PressTrack
{
    public double DownAt { get; private set; } = double.NaN;
    public double UpAt { get; private set; } = double.NaN;
    public bool Down => !double.IsNaN(DownAt) && double.IsNaN(UpAt);
    public bool Committed { get; private set; }
    public bool Armed { get; private set; }
    public bool Glitch { get; private set; }
    public bool Scan { get; private set; }
    // The share of the way down the first pressed frame shows.
    public const double Click = .45;
    private double _hold, _holdFrom, _holdSince;

    public void Press(double now, double from, bool glitch)
    {
        DownAt = now; UpAt = double.NaN; Committed = false; Armed = true; Glitch = glitch; Scan = false;
        _holdFrom = Math.Clamp(from, 0, 1); _holdSince = now; _hold = _holdFrom;
    }

    // The pointer moved while held: off the control the press backs off; back on it, it goes down again.
    public void Arm(double now, bool inside, MotionTiming timing, int steps)
    {
        if (!Down || inside == Armed) return;
        _holdFrom = HeldAt(now, timing, steps); _holdSince = now; Armed = inside;
    }

    public void Release(double now, bool inside, bool scan, MotionTiming timing, int steps)
    {
        if (!Down) return;
        _hold = HeldAt(now, timing, steps);
        UpAt = now; Committed = inside && Armed; Scan = Committed && scan;
    }

    public void Clear() { DownAt = UpAt = double.NaN; Committed = Armed = Glitch = Scan = false; _hold = 0; }

    private double HeldAt(double now, MotionTiming timing, int steps)
    {
        double t = MotionCurve.Linear(now - _holdSince, (Armed ? timing.PressIn : timing.PressOut) / 1000.0);
        double target = Armed ? 1 : 0;
        // Going down, the first frame already takes a good part of the way (Click): the press is felt at once, as a key that
        // gives, and the rest settles within PressIn. Letting go has no such jump.
        double e = MotionCurve.Ease(t, steps);
        if (Armed) e = Click + (1 - Click) * e;
        return _holdFrom + (target - _holdFrom) * e;
    }

    // The press at a time. Held: how far down (0 to 1). Release: 0 to 1 through letting go (-1 while held or never released).
    // Flourish: 0 to 1 through what a completed press leaves (-1 when none). GlitchOn and ScanAt: the glitch slice shows, and
    // where the scan line is from 0 (top) to 1 (bottom), -1 when none.
    public PressFrame At(double now, MotionTiming timing, int steps)
    {
        if (double.IsNaN(DownAt)) return PressFrame.None;
        bool glitchOn = Glitch && now - DownAt < timing.Glitch / 1000.0;
        if (Down) return new PressFrame(HeldAt(now, timing, steps), -1, -1, glitchOn, -1);
        double release = MotionCurve.Linear(now - UpAt, timing.PressOut / 1000.0);
        double held = _hold * (1 - MotionCurve.EaseOut(release, steps));
        double flourish = Committed ? MotionCurve.Linear(now - UpAt, timing.Press / 1000.0) : -1;
        double scanLength = timing.PressOut / 1000.0 * .8;
        double scan = Scan && now - UpAt < scanLength ? MotionCurve.Step(Math.Max(.001, (now - UpAt) / scanLength), Math.Max(4, steps)) : -1;
        if (flourish >= 1) flourish = -1;
        return new PressFrame(held, release, flourish, glitchOn, scan);
    }

    // Whether anything of the press is still drawn.
    public bool Alive(double now, MotionTiming timing)
        => Down || !double.IsNaN(UpAt) && (now - UpAt < timing.PressOut / 1000.0 || Committed && now - UpAt < timing.Press / 1000.0);
}

public readonly record struct PressFrame(double Held, double Release, double Flourish, bool GlitchOn, double ScanAt)
{
    public static readonly PressFrame None = new(0, -1, -1, false, -1);
}

// The "motion" section of a theme file. Everything is optional and comes from the base theme when left out.
public sealed class PanelThemeMotion
{
    public PanelThemeAmbient? Ambient { get; set; }
    // none, reveal, lift, underline, caret, brackets, sheen, glow (HoverFx); earlier names: spot, edge, neon
    public string? Hover { get; set; }
    public double? HoverStrength { get; set; }
    // none, shade, sink, indicator, select, lock, sparkle, ember (PressFx); earlier names: pulse, ripple, ring
    public string? Press { get; set; }
    public double? PressStrength { get; set; }
    // 0 to 0.6: how strongly the particles a completed press leaves show (0: none)
    public double? Trail { get; set; }
    // none, dust, pixel, spark, star, firefly, neon
    public string? Particles { get; set; }
    // none, ring, glow, brackets, caret (FocusFx); earlier name: neon
    public string? Focus { get; set; }
    public double? Pace { get; set; }
    // 0 (smooth) or 2 to 8 hard steps
    public double? Steps { get; set; }
    public string? Color { get; set; }
    public string? Secondary { get; set; }
    public string? Highlight { get; set; }
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
                Hover = MotionStyles.Hover(motion.Hover, m.Hover),
                HoverStrength = MotionSafety.Amount(motion.HoverStrength, MotionSafety.HoverMax, m.HoverStrength),
                Press = MotionStyles.Press(motion.Press, m.Press),
                PressStrength = MotionSafety.Amount(motion.PressStrength, MotionSafety.PressMax, m.PressStrength),
                ParticleStrength = MotionSafety.Amount(motion.Trail, MotionSafety.TrailMax, m.ParticleStrength),
                Particles = MotionStyles.Particles(motion.Particles, m.Particles),
                Focus = MotionStyles.Focus(motion.Focus, m.Focus),
                Pace = motion.Pace is { } pace && double.IsFinite(pace) ? Math.Clamp(pace, MotionSafety.PaceMin, MotionSafety.PaceMax) : m.Pace,
                Steps = MotionSafety.Steps(motion.Steps is { } steps && double.IsFinite(steps) ? (int)Math.Round(Math.Clamp(steps, -1, 99)) : null, m.Steps),
                Color = motion.Color is { Length: > 0 } color ? color.Trim() : m.Color,
                Secondary = motion.Secondary is { Length: > 0 } secondary ? secondary.Trim() : m.Secondary,
                Highlight = motion.Highlight is { Length: > 0 } highlight ? highlight.Trim() : m.Highlight
            };
        }
        return m with { Ambient = a };
    }
}
