namespace BrimDeck.Core.Notices;

// The liquid motion of an arriving notice: while it settles, its surface behaves like a viscous liquid. The capsule throws
// droplets off both ends and takes them back as the ends flow out to refill, the notch swells at its two lower corners with
// bulges that run round its outline; an edge glow lights twice. (The indicator does not use it: its motion is the surface's own,
// see Curves.LineArrive and Curves.LineReturn.)
//
// The capsule's and the notch's motion follows the design "胶囊通知动效" that the maintainer MoFeng2223 provided for BrimDeck
// (https://claude.ai/artifact/JU9bvJp5PPNY85vpGgUX6C): a body of max(width - 2 zones, min(width, keep)), a glow, then a
// separation of droplets with necks that thin and break, a refill, and an end where the droplets shrink away and the ends flow
// back. Its parameters and defaults are that design's. This is our own implementation of the described model for WPF (no code
// of the design is used); the adaptation to every notice and theme is ours.
//
// Everything is worked out here from the notice's size and a seed, with no UI types, and sampled by time: the droplets' births,
// sizes, directions and lives are decided once, and a frame only evaluates positions. Positions are in DIPs. Capsule
// droplets are given relative to the end they leave (x outward from the surface's edge at rest, y from its middle),
// so a surface that changes size meanwhile (a burst making the toast wider) keeps its droplets at its ends; the notch's bulges
// and beads are given in the surface's own coordinates from its top-left corner.
public enum LiquidForm { Capsule, Notch }

public readonly record struct LiquidPoint(double X, double Y)
{
    public static LiquidPoint operator +(LiquidPoint a, LiquidPoint b) => new(a.X + b.X, a.Y + b.Y);
}

// A circle: a droplet, a bead of the notch's edge, or the end cap a neck starts from. Side: -1 left end, 1 right end, 0 absolute.
// Attached: the droplet is still joined to the surface (its neck to the end has not broken, or it hangs on one that has not), so it
// is part of the surface's own outline; once it has parted it has an outline of its own.
public readonly record struct LiquidDisc(int Side, double X, double Y, double R, bool Attached = false);

// The neck between two circles pulling apart, as two cubic curves: P1 to P3 along one side (handles H1, H3), P4 back to P2
// along the other (handles H4, H2). Side as for LiquidDisc. Mouth: the neck starts on the end itself (not on another droplet),
// so its first two points lie on the end's outline. First and Second: the droplets (indices in LiquidFrame.Discs) it joins, -1 for the
// end. Attached: it joins the surface, directly or through attached droplets, so it too is part of the surface's outline.
public readonly record struct LiquidNeck(int Side, LiquidPoint P1, LiquidPoint H1, LiquidPoint H3, LiquidPoint P3, LiquidPoint P4, LiquidPoint H4, LiquidPoint H2, LiquidPoint P2,
    bool Mouth = false, int First = -1, int Second = -1, bool Attached = false);

public readonly record struct LiquidSpec(
    LiquidForm Form, double Width, double Height, int Seed,
    // The design's parameters and defaults: the zone each end may throw droplets into, the narrowest body that is kept, the
    // intensity (1 to 10), the separation's length in seconds, the widest angle of a droplet off the horizontal, and the pull
    // that brings a droplet back to the middle line.
    double Zone = 16, double Keep = 300, double Intensity = 5, double Separation = .6, double MaxAngle = 80, double Pull = 3,
    // The notch's outline: its shoulder and the radius of its bottom corners.
    double Shoulder = 16, double CornerRadius = 12,
    // How far an end may draw in at most: what the surface's content leaves it (its padding, less what the end's own curve already
    // takes at the content's height; see NoticeLiquid.MaxInset), so that the content is not cut by the outline as it gives way.
    double MaxInset = double.PositiveInfinity,
    // The surface's cut corners (the theme's effects.cut): above 0 the notch's shoulders are straight slants and its bottom corners
    // 45-degree cuts of at most this many DIP (and its corner radius), so the bulges and the glow run along the outline it draws.
    double Cut = 0)
{
    // The design's own height, at which its sizes are given; a taller surface keeps the droplets' sizes.
    public const double DesignHeight = 32;
}

public sealed class LiquidFrame
{
    // The edge glow's brightness, 0 to 1 (the capsule's and the notch's flash).
    public double Glow;
    // How far each end of the capsule has drawn in (the droplets took some of it), 0 at rest.
    public double InsetLeft, InsetRight;
    public readonly List<LiquidDisc> Discs = [];
    public readonly List<LiquidNeck> Necks = [];
    // The notch's bulges: runs of points, each run its outer edge in order followed by its base back (a closed polygon).
    public readonly List<LiquidPoint> BumpPoints = [];
    public readonly List<int> BumpRuns = [];
    // How far each point of Outline stands out from it (the notch's bulges); the surface's outline is Outline displaced by it.
    public double[] Offsets = [];
    // The notch's whole outline as it is for this size, with the outward normal of each point and how far along it is: the glow
    // is drawn on it, and its taper follows the distance.
    public LiquidPoint[] Outline = [];
    public LiquidPoint[] Normals = [];
    public double[] Along = [];
    public double OutlineLength;
    public bool Alive;

    internal void Clear()
    {
        Glow = 0; InsetLeft = InsetRight = 0; Offsets = []; Discs.Clear(); Necks.Clear(); BumpPoints.Clear(); BumpRuns.Clear(); Alive = false;
    }
}

public sealed class NoticeLiquid
{
    // The end of the motion after the separation (the ends flowing back, the droplets gone) and a last moment of rest.
    public const double Tail = 1.0, Rest = .15;
    // How long a droplet takes of the end as it leaves, and how long the end then takes to flow out and fill up again.
    private const double Pinch = .1, Refill = .2;
    // The notch's bulges and beads stay within this far of its outline.
    public const double Band = 5;
    private const double Kappa = .5522847498307936;

    private readonly LiquidSpec _spec;
    private readonly double _width, _height, _radius, _body, _budget, _reach, _over;
    private readonly bool _short;
    private readonly List<Drop> _drops = [];
    private readonly List<Ripple> _ripples = [];
    private double _abortAt = double.PositiveInfinity;
    // The notch's outline for the size it was last sampled at.
    private double _outlineWidth = -1, _outlineHeight = -1;
    private LiquidPoint[] _points = [];
    private LiquidPoint[] _normals = [];
    private double[] _along = [];
    private double _total, _cornerRight, _cornerLeft, _shoulderEnd;

    private struct Drop
    {
        public int Side;
        public double Born, R, Take, Life, Theta, K, V0, Drift, Omega, Damp, Wobble, Frequency;
    }
    private struct Ripple
    {
        public int Side;
        public double Born, Amplitude, Sigma, Speed, Life, Bead;
        public double Direction;
    }

    public NoticeLiquid(in LiquidSpec spec)
    {
        _spec = spec;
        _width = Math.Max(1, spec.Width); _height = Math.Max(1, spec.Height);
        _radius = _height / 2;
        double zone = Math.Max(0, spec.Zone);
        // The body: what is kept of the capsule while its ends give way, the design's max(width - 2 zones, min(width, keep)). A
        // surface that is already narrower than keep would give nothing, so keep is lowered to leave the ends a little room.
        double keep = Math.Min(spec.Keep, _width - Math.Min(16, _width / 4));
        _body = Math.Max(_width - 2 * zone, Math.Min(_width, keep));
        // _reach is the room an end's droplets have (the zone); _budget how far the end itself may draw in, within what the content leaves.
        _reach = Math.Max(0, (_width - _body) / 2);
        _budget = Math.Min(_reach, Math.Max(0, spec.MaxInset));
        _over = zone / 2;
        _short = _reach < _radius * .5;
        Separation = Math.Clamp(spec.Separation, .2, 2);
        Seed(spec);
    }

    // How far the end of a pill may draw in before the outline reaches content that stands `padding` from its end and reaches
    // `contentHalf` up and down from its middle: the outline's own curve takes R - sqrt(R^2 - contentHalf^2) of the padding at that
    // height already, and a little is left over.
    public static double MaxInset(double height, double padding, double contentHalf)
    {
        double r = Math.Max(1, height / 2), half = Math.Min(Math.Max(0, contentHalf), r);
        double curve = r - Math.Sqrt(Math.Max(0, r * r - half * half));
        return Math.Max(0, padding - curve - 3);
    }

    public LiquidForm Form => _spec.Form;
    public double Separation { get; }
    // The whole motion's length in seconds.
    public double Length => Separation + Tail + Rest;
    public double Width => _width;
    public double Height => _height;

    // The maintainer chose the fourth group of the design's seeds for BrimDeck: the seed after four steps from 11.
    public static int DesignSeed
    {
        get { long s = 11; for (int i = 0; i < 4; i++) s = (s * 48271 + 11) % 2147483647; return (int)s; }
    }

    // The motion ends early at this time: droplets shrink away, the ends flow back and the glow goes out within AbortLength.
    public const double AbortLength = .3;
    public void Abort(double time) { if (time < _abortAt) _abortAt = Math.Max(0, time); }
    public bool Aborted => !double.IsPositiveInfinity(_abortAt);
    // Whether it has nothing more to show at this time.
    public bool Finished(double time) => time >= Length || time >= _abortAt + AbortLength;

    // ----- Randomness -----

    // A small deterministic generator (SplitMix32), so a seed always gives the same droplets.
    private sealed class Random32(uint state)
    {
        public double Next()
        {
            state += 0x9E3779B9u;
            uint z = state;
            z = (z ^ (z >> 16)) * 0x85EBCA6Bu; z = (z ^ (z >> 13)) * 0xC2B2AE35u; z ^= z >> 16;
            return z / 4294967296d;
        }
    }

    private void Seed(in LiquidSpec spec)
    {
        var random = new Random32((uint)spec.Seed);
        double intensity = Math.Clamp(spec.Intensity, 1, 10);
        switch (spec.Form)
        {
            case LiquidForm.Notch: SeedRipples(random, intensity); break;
            default: SeedDrops(random, intensity); break;
        }
    }

    // The sizes at the design's own height; a droplet is a fraction of the end cap's radius there: a few small, mostly middling,
    // now and then one nearly as big as the cap.
    private double CapRadius => Math.Min(_radius, LiquidSpec.DesignHeight / 2 * 1.25);
    private double DropSize(Random32 random)
    {
        double u = random.Next(), cap = CapRadius;
        return cap * (u < .3 ? .25 + random.Next() * .17 : u < .8 ? .42 + random.Next() * .23 : .65 + random.Next() * .25);
    }
    // How far a droplet may go past the end: the zone, but never less than about two of its own widths.
    private double Room(double r) => Math.Max(_reach, r * 2.4);

    private void SeedDrops(Random32 random, double intensity)
    {
        double separation = Separation;
        for (int side = -1; side <= 1; side += 2)
        {
            // The same pace from the first droplet to the last, thinning only near the end, so the start looks like the middle.
            double s = _short ? .16 : .03;
            int made = 0;
            while (s < separation && made < 14)
            {
                double twinChance = (.15 + intensity * .05) * Math.Min(1, (_reach * 2) / 72);
                int twins = random.Next() < twinChance ? 2 : 1;
                for (int i = 0; i < twins; i++)
                {
                    double r = DropSize(random);
                    _drops.Add(NewDrop(random, side, s + i * (.03 + random.Next() * .05), r));
                    made++;
                }
                double progress = s / separation;
                // Fewer at a time in a narrow zone.
                double sparse = Math.Max(1, 36 / Math.Max(1, _reach));
                double mean = (.17 - intensity * .025) * sparse * (progress < .8 ? 1 : 1 + 4 * (progress - .8));
                s += .03 + -Math.Log(1 - random.Next()) * mean;
            }
        }
        _drops.Sort((a, b) => a.Born.CompareTo(b.Born));
    }

    private Drop NewDrop(Random32 random, int side, double born, double r)
    {
        double angle = _spec.MaxAngle * Math.PI / 180, theta = (random.Next() * 2 - 1) * angle;
        // It shoots out at its angle, fast at first and slowed by drag (k); a soft spring pulls it back to the middle line (it
        // swings across it and settles) and it keeps drifting slowly outward. It always goes far enough to break its neck (the
        // end springs back as it leaves, so about 1.6 of its radii is enough), then a little further into its room.
        double k = 8 + random.Next() * 5, reach = Room(r), least = r * 1.6;
        return new Drop
        {
            Side = side, Born = born, R = r, Take = r * (.9 + random.Next() * .5), Life = .65 + random.Next() * .6, Theta = theta, K = k,
            V0 = k * (least + random.Next() * Math.Max(0, reach - least) * .6), Drift = reach * (.03 + random.Next() * .07),
            Omega = 9 + random.Next() * 6, Damp = 1.2 + _spec.Pull * .9 + random.Next(), Wobble = random.Next() * 6.28, Frequency = 9 + random.Next() * 8
        };
    }

    private void SeedRipples(Random32 random, double intensity)
    {
        double separation = Separation;
        for (int side = -1; side <= 1; side += 2)
        {
            // The corner swells first.
            _ripples.Add(new Ripple { Side = side, Born = .02, Amplitude = Band * .85, Sigma = 9, Speed = 0, Life = .45, Direction = 0 });
            double s = .06 + random.Next() * .04;
            while (s < separation && _ripples.Count < 60)
            {
                int n = random.Next() < .2 + intensity * .06 ? 2 : 1;
                for (int i = 0; i < n; i++)
                {
                    // Mostly along the bottom, towards the middle; sometimes up the side wall.
                    double toward = random.Next() < .68 ? 1 : -1;
                    _ripples.Add(new Ripple
                    {
                        Side = side, Direction = toward, Born = s + i * (.02 + random.Next() * .05), Amplitude = 1.6 + random.Next() * 2.8,
                        Sigma = 4.5 + random.Next() * 5, Speed = 220 + random.Next() * 200, Life = .6 + random.Next() * .45,
                        Bead = random.Next() < .4 ? 1.3 + random.Next() * 1.1 : 0
                    });
                }
                double progress = s / separation;
                s += .03 + -Math.Log(1 - random.Next()) * (.2 - intensity * .03) * (progress < .8 ? 1 : 1 + 4 * (progress - .8));
            }
        }
    }

    // ----- Shapes -----

    private static double Clamp(double v, double a = 0, double b = 1) => Math.Min(b, Math.Max(a, v));
    private static double Smooth(double p) => p * p * (3 - 2 * p);
    private static double Lerp(double a, double b, double p) => a + (b - a) * p;

    // How much of the end a droplet born earlier takes at time s: it goes at once as the droplet leaves (Pinch) and the end then
    // flows back out to fill up (Refill).
    private double Taken(double s, int side)
    {
        double taken = 0;
        foreach (ref readonly var d in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_drops))
        {
            if (d.Side != side) continue;
            double a = s - d.Born;
            if (a <= 0) continue;
            taken += d.Take * (a < Pinch ? Smooth(a / Pinch) : Math.Exp(-(a - Pinch) / Refill));
        }
        return taken;
    }

    // How far the end has drawn in at time s: never past the body.
    private double InsetAt(double s, int side, double fade) => Math.Min(_budget, Taken(s, side)) * fade;

    // The factor that takes everything away when the motion is aborted.
    private double AbortFade(double time) => time <= _abortAt ? 1 : 1 - Smooth(Clamp((time - _abortAt) / AbortLength));

    // ----- Sampling -----

    // Fills the frame with the motion at this time (seconds from its start). The notch's frame needs the surface's present size
    // (it may have changed since the motion began); the others take theirs from the ends it is drawn on.
    public void Sample(double time, LiquidFrame frame, double width = 0, double height = 0)
    {
        frame.Clear();
        if (time < 0 || Finished(time)) return;
        frame.Alive = true;
        double fade = AbortFade(time);
        switch (_spec.Form)
        {
            case LiquidForm.Notch: SampleNotch(time, frame, width > 0 ? width : _width, height > 0 ? height : _height, fade); break;
            default: SampleDrops(time, frame, fade); frame.Glow = GlowAt(time) * fade; break;
        }
    }

    // The glow's brightness: it lights twice, at 0.12 s and 0.42 s, and is gone by 0.75 s.
    private static readonly (double T, double V)[] GlowKeys = [(0, 0), (.12, 1), (.3, .12), (.42, 1), (.75, 0)];
    public static double GlowAt(double s)
    {
        if (s <= 0 || s >= GlowKeys[^1].T) return 0;
        for (int i = 1; i < GlowKeys.Length; i++)
        {
            if (s > GlowKeys[i].T) continue;
            var (t0, v0) = GlowKeys[i - 1]; var (t1, v1) = GlowKeys[i];
            return Lerp(v0, v1, .5 - .5 * Math.Cos(Math.PI * (s - t0) / (t1 - t0)));
        }
        return 0;
    }

    private void SampleDrops(double s, LiquidFrame frame, double fade)
    {
        double capR = _radius;
        frame.InsetLeft = InsetAt(s, -1, fade); frame.InsetRight = InsetAt(s, 1, fade);
        for (int side = -1; side <= 1; side += 2)
        {
            double inset = side < 0 ? frame.InsetLeft : frame.InsetRight;
            double capX = -inset - capR;
            int firstDisc = frame.Discs.Count, discs = 0;
            foreach (ref readonly var d in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_drops))
            {
                if (d.Side != side) continue;
                double a = s - d.Born;
                if (a < 0 || a > d.Life) continue;
                double p = a / d.Life;
                // It starts as the tip of the end (inside it, so unseen) and shoots out at its angle while the end springs back
                // and the neck between them stretches and breaks.
                double bornCap = -Math.Min(_budget, Taken(d.Born, side)) * fade - capR;
                double inside = capR - d.R;
                double x0 = bornCap + Math.Cos(d.Theta) * inside, y0 = Math.Sin(d.Theta) * inside * .6;
                double dist = d.V0 * (1 - Math.Exp(-d.K * a)) / d.K + d.Drift * a;
                double x = x0 + dist, vy = d.V0 * Math.Sin(d.Theta) * .6;
                double y = Math.Exp(-d.Damp * a) * (y0 * Math.Cos(d.Omega * a) + vy / d.Omega * Math.Sin(d.Omega * a));
                double r = d.R;
                // Past its room (and the extra room) it shrinks away and goes no further.
                double room = Room(d.R), past = x - room * .8;
                if (past > 0) r *= 1 - Smooth(Clamp(past / Math.Max(1, _over)));
                if (p > .72) r *= 1 - Smooth((p - .72) / .28);
                r *= 1 + .045 * Math.Sin(a * d.Frequency + d.Wobble);
                y += .5 * Math.Sin(a * d.Frequency * .8 + d.Wobble * 2);
                r *= fade;
                if (r <= .4) continue;
                double reach = Math.Max(0, _radius - r);
                y = Math.Clamp(y, -reach, reach);
                x = Math.Min(x, room + _over - r);
                frame.Discs.Add(new LiquidDisc(side, x, y, r)); discs++;
                // A droplet that is fading no longer hangs on the end (the refilling end would sprout a small knob).
                int before = frame.Necks.Count;
                if (p < .6) AddNeck(frame, side, capX, 0, capR, x, y, r, r * .7, .65, true, -1, frame.Discs.Count - 1);
                // Joined by its neck, or still overlapping the end (the neck is not drawn while the droplet is mostly inside it): part of the surface.
                if (frame.Necks.Count > before || Math.Sqrt((x - capX) * (x - capX) + y * y) < capR + r + Touch) frame.Discs[^1] = frame.Discs[^1] with { Attached = true };
            }
            // Neighbouring droplets of one end join while they are close.
            for (int i = 0; i < discs; i++)
                for (int j = i + 1; j < discs; j++)
                {
                    var a = frame.Discs[firstDisc + i]; var b = frame.Discs[firstDisc + j];
                    AddNeck(frame, side, a.X, a.Y, a.R, b.X, b.Y, b.R, Math.Min(a.R, b.R) * .45, .55, false, firstDisc + i, firstDisc + j);
                }
        }
        MarkAttached(frame);
    }

    // What is still joined to the surface: the droplets whose neck to the end has not broken, and the droplets hanging on those, with
    // the necks between. Everything else has parted and is drawn on its own.
    private const double Touch = 1.5;
    private static void MarkAttached(LiquidFrame frame)
    {
        var attached = new bool[frame.Discs.Count];
        for (int i = 0; i < attached.Length; i++) attached[i] = frame.Discs[i].Attached;
        foreach (var neck in frame.Necks) if (neck.Mouth && neck.Second >= 0) attached[neck.Second] = true;
        for (bool grew = true; grew;)
        {
            grew = false;
            // A droplet touching one that is joined is joined too: its outline would run through the other's.
            for (int i = 0; i < attached.Length; i++)
                for (int j = 0; j < attached.Length; j++)
                {
                    if (!attached[i] || attached[j] || frame.Discs[i].Side != frame.Discs[j].Side) continue;
                    double dx = frame.Discs[i].X - frame.Discs[j].X, dy = frame.Discs[i].Y - frame.Discs[j].Y;
                    if (Math.Sqrt(dx * dx + dy * dy) < frame.Discs[i].R + frame.Discs[j].R + Touch) { attached[j] = true; grew = true; }
                }
            foreach (var neck in frame.Necks)
            {
                if (neck.Mouth || neck.First < 0 || neck.Second < 0 || attached[neck.First] == attached[neck.Second]) continue;
                attached[neck.First] = attached[neck.Second] = true; grew = true;
            }
        }
        for (int i = 0; i < frame.Discs.Count; i++) if (attached[i]) frame.Discs[i] = frame.Discs[i] with { Attached = true };
        for (int i = 0; i < frame.Necks.Count; i++)
        {
            var neck = frame.Necks[i];
            if (neck.Mouth || (neck.First >= 0 && neck.Second >= 0 && attached[neck.First] && attached[neck.Second])) frame.Necks[i] = neck with { Attached = true };
        }
    }

    // The neck between two circles pulling apart, as two cubic curves. `soft` widens the neck and lengthens its handles; it
    // narrows smoothly as the gap grows and is gone at `snap`. Nothing while the smaller circle is still mostly inside the
    // larger one (the curves would fold over).
    private static void AddNeck(LiquidFrame frame, int side, double x1, double y1, double r1, double x2, double y2, double r2, double snap, double soft, bool mouth, int first, int second)
    {
        double dx = x2 - x1, dy = y2 - y1, d = Math.Sqrt(dx * dx + dy * dy);
        if (r1 <= .5 || r2 <= .5 || d <= Math.Max(r1, r2) - .2 * Math.Min(r1, r2) || d >= r1 + r2 + snap) return;
        double gap = d - (r1 + r2);
        double v = soft * (gap <= 0 ? 1 : 1 - Smooth(gap / snap));
        if (v <= .03) return;
        double u1 = d < r1 + r2 ? Math.Acos(Clamp((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d), -1, 1)) : 0;
        double u2 = d < r1 + r2 ? Math.Acos(Clamp((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d), -1, 1)) : 0;
        double bearing = Math.Atan2(dy, dx), spread = Math.Acos(Clamp((r1 - r2) / d, -1, 1));
        double a1 = bearing + u1 + (spread - u1) * v, a2 = bearing - u1 - (spread - u1) * v;
        double a3 = bearing + Math.PI - u2 - (Math.PI - u2 - spread) * v, a4 = bearing - Math.PI + u2 + (Math.PI - u2 - spread) * v;
        static LiquidPoint At(double x, double y, double r, double a) => new(x + r * Math.Cos(a), y + r * Math.Sin(a));
        var p1 = At(x1, y1, r1, a1); var p2 = At(x1, y1, r1, a2); var p3 = At(x2, y2, r2, a3); var p4 = At(x2, y2, r2, a4);
        double reach = Math.Sqrt((p3.X - p1.X) * (p3.X - p1.X) + (p3.Y - p1.Y) * (p3.Y - p1.Y));
        double handle = Math.Min(v * 2.6, reach / (r1 + r2)) * Math.Min(1, d * 2 / (r1 + r2));
        var h1 = At(p1.X, p1.Y, r1 * handle, a1 - Math.PI / 2); var h2 = At(p2.X, p2.Y, r1 * handle, a2 + Math.PI / 2);
        var h3 = At(p3.X, p3.Y, r2 * handle, a3 + Math.PI / 2); var h4 = At(p4.X, p4.Y, r2 * handle, a4 - Math.PI / 2);
        frame.Necks.Add(new LiquidNeck(side, p1, h1, h3, p3, p4, h4, h2, p2, mouth, first, second));
    }

    // ----- The notch -----

    // The notch's outline below the screen's edge, sampled every Step: from where the right shoulder leaves the screen, round the
    // shoulder, down the right wall, round the right corner, along the bottom, round the left corner, up the left wall and round
    // the left shoulder back to the screen, with each point's outward normal and its distance along. The curves are the island's
    // own (IslandBorder.BuildOutline), so the bulges sit on the outline the surface draws.
    private void BuildOutline(double w, double h)
    {
        if (w == _outlineWidth && h == _outlineHeight) return;
        _outlineWidth = w; _outlineHeight = h;
        const double step = 2;
        double shoulder = Math.Clamp(_spec.Shoulder, 0, Math.Min(w / 4, h / 2)), depth = shoulder * 7d / 16;
        double left = shoulder, right = w - shoulder;
        double limit = Math.Max(0, Math.Min(h - depth, (right - left) / 2));
        double corner = Math.Clamp(_spec.CornerRadius, 0, limit);
        double cut = Math.Max(0, _spec.Cut), leg = cut > 0 ? Math.Min(cut, corner) : 0;
        var points = new List<LiquidPoint>(); var normals = new List<LiquidPoint>(); var along = new List<double>();
        double s = 0;
        void Push(double x, double y, double nx, double ny)
        {
            if (points.Count > 0) { var last = points[^1]; s += Math.Sqrt((x - last.X) * (x - last.X) + (y - last.Y) * (y - last.Y)); }
            points.Add(new LiquidPoint(x, y)); normals.Add(new LiquidPoint(nx, ny)); along.Add(s);
        }
        void Line(double x1, double y1, double x2, double y2, double nx, double ny)
        {
            int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) / step));
            for (int i = points.Count > 0 ? 1 : 0; i <= n; i++) Push(Lerp(x1, x2, (double)i / n), Lerp(y1, y2, (double)i / n), nx, ny);
        }
        void Arc(double cx, double cy, double a1, double a2)
        {
            int n = Math.Max(2, (int)Math.Ceiling(corner * Math.Abs(a2 - a1) / step));
            for (int i = 1; i <= n; i++) { double a = Lerp(a1, a2, (double)i / n); Push(cx + corner * Math.Cos(a), cy + corner * Math.Sin(a), Math.Cos(a), Math.Sin(a)); }
        }
        // A cubic, its outward normal turned from its tangent (the outline runs clockwise on screen, so outward is the tangent
        // turned a quarter to the left).
        void Cubic(LiquidPoint p0, LiquidPoint p1, LiquidPoint p2, LiquidPoint p3)
        {
            for (int i = points.Count > 0 ? 1 : 0; i <= 10; i++)
            {
                double t = i / 10d, u = 1 - t;
                double x = u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X;
                double y = u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y;
                double tx = 3 * u * u * (p1.X - p0.X) + 6 * u * t * (p2.X - p1.X) + 3 * t * t * (p3.X - p2.X);
                double ty = 3 * u * u * (p1.Y - p0.Y) + 6 * u * t * (p2.Y - p1.Y) + 3 * t * t * (p3.Y - p2.Y);
                double len = Math.Sqrt(tx * tx + ty * ty); if (len < 1e-9) len = 1;
                Push(x, y, ty / len, -tx / len);
            }
        }
        LiquidPoint P(double x, double y) => new(x, y);
        // The edge's reach into the corner: its leg when it is cut, its radius when it is round.
        double reach = leg > 0 ? leg : corner;
        const double Diagonal = .7071067811865476;
        if (shoulder > 0)
        {
            if (cut > 0)
            {
                // A slant from the screen's edge to the wall, its outward normal the slant's own, turned a quarter.
                double slant = Math.Sqrt(shoulder * shoulder + depth * depth);
                Line(w, 0, right, depth, depth / slant, shoulder / slant);
            }
            else
            {
                Cubic(P(w, 0), P(w - shoulder * .28, 0), P(w - shoulder * .64, 0), P(w - shoulder * .82, depth * .18));
                Cubic(P(w - shoulder * .82, depth * .18), P(right, depth * .36), P(right, depth * .72), P(right, depth));
            }
            _shoulderEnd = s;
            Line(right, depth, right, h - reach, 1, 0);
        }
        else { _shoulderEnd = 0; Line(w, 0, w, h - reach, 1, 0); }
        double cornerRight0 = s;
        if (leg > 0) Line(right, h - leg, right - leg, h, Diagonal, Diagonal); else Arc(right - corner, h - corner, 0, Math.PI / 2);
        double cornerRight = (cornerRight0 + s) / 2;
        Line(right - reach, h, left + reach, h, 0, 1);
        double cornerLeft0 = s;
        if (leg > 0) Line(left + leg, h, left, h - leg, -Diagonal, Diagonal); else Arc(left + corner, h - corner, Math.PI / 2, Math.PI);
        double cornerLeft = (cornerLeft0 + s) / 2;
        if (shoulder > 0)
        {
            Line(left, h - reach, left, depth, -1, 0);
            if (cut > 0)
            {
                double slant = Math.Sqrt(shoulder * shoulder + depth * depth);
                Line(left, depth, 0, 0, -depth / slant, shoulder / slant);
            }
            else
            {
                Cubic(P(left, depth), P(left, depth * .72), P(left, depth * .36), P(shoulder * .82, depth * .18));
                Cubic(P(shoulder * .82, depth * .18), P(shoulder * .64, 0), P(shoulder * .28, 0), P(0, 0));
            }
        }
        else Line(left, h - reach, left, 0, -1, 0);
        _points = [.. points]; _normals = [.. normals]; _along = [.. along]; _total = s; _cornerRight = cornerRight; _cornerLeft = cornerLeft;
    }

    // A bulge keeps its size for most of its run, so it still shows when it reaches the middle of the bottom.
    private static double RippleEnvelope(in Ripple w, double a) => Smooth(Clamp(a / .08)) * (1 - Smooth(Clamp((a - w.Life * .6) / (w.Life * .4))));
    private double RipplePosition(in Ripple w, double a)
    {
        double corner = w.Side > 0 ? _cornerRight : _cornerLeft;
        // Towards the middle of the bottom is +s from the right corner and -s from the left one.
        double toward = w.Side > 0 ? 1 : -1;
        return corner + w.Direction * toward * w.Speed * (1 - Math.Exp(-.6 * a)) / .6;
    }

    // How far the edge stands out at distance s along the outline.
    private double EdgeOut(double s, double time, double fade)
    {
        double sum = 0;
        foreach (ref readonly var w in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_ripples))
        {
            double a = time - w.Born;
            if (a <= 0 || a >= w.Life) continue;
            double ds = s - RipplePosition(w, a);
            if (Math.Abs(ds) > w.Sigma * 3.5) continue;
            sum += w.Amplitude * RippleEnvelope(w, a) * Math.Exp(-ds * ds / (2 * w.Sigma * w.Sigma));
        }
        // Soft cap at the band, and gone over the whole shoulder, where the outline turns too fast for a bulge to follow it.
        double fromTop = Math.Min(s, _total - s);
        return Band * Math.Tanh(sum / Band) * Smooth(Clamp((fromTop - 2) / Math.Max(6, _shoulderEnd))) * fade;
    }

    private int NearestPoint(double s) => Math.Clamp((int)Math.Round(s / _total * (_points.Length - 1)), 0, _points.Length - 1);

    private void SampleNotch(double time, LiquidFrame frame, double w, double h, double fade)
    {
        BuildOutline(w, h);
        frame.Outline = _points; frame.Normals = _normals; frame.Along = _along; frame.OutlineLength = _total;
        frame.Glow = GlowAt(time) * fade;
        // The outward offset of every point, then the runs where it is noticeable, each closed back along the base (a little
        // inside the surface, so the polygon overlaps the surface's rim and hides it where the bulge stands).
        int n = _points.Length;
        double[] offsets = Offsets(n);
        for (int i = 0; i < n; i++) offsets[i] = EdgeOut(_along[i], time, fade);
        frame.Offsets = offsets;
        int start = -1;
        for (int i = 0; i <= n; i++)
        {
            bool out_ = i < n && offsets[i] > .12;
            if (out_ && start < 0) start = i;
            if (out_ || start < 0) continue;
            int end = i - 1, count = end - start + 1;
            if (count >= 2)
            {
                frame.BumpRuns.Add(count * 2);
                for (int k = start; k <= end; k++) frame.BumpPoints.Add(new LiquidPoint(_points[k].X + _normals[k].X * offsets[k], _points[k].Y + _normals[k].Y * offsets[k]));
                for (int k = end; k >= start; k--) frame.BumpPoints.Add(new LiquidPoint(_points[k].X - _normals[k].X * 1.5, _points[k].Y - _normals[k].Y * 1.5));
            }
            start = -1;
        }
        // Beads trail a little behind their bulge, just clear of the edge and inside the band.
        foreach (ref readonly var r in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_ripples))
        {
            if (r.Bead <= 0) continue;
            double a = time - r.Born;
            if (a <= .06 || a >= r.Life) continue;
            double s = RipplePosition(r, a) - r.Direction * (r.Side > 0 ? 1 : -1) * r.Sigma * 1.7;
            if (s < 0 || s > _total) continue;
            double radius = r.Bead * RippleEnvelope(r, a) * fade;
            if (radius < .4) continue;
            int p = NearestPoint(s);
            double off = Math.Min(Band - radius, EdgeOut(s, time, fade) + radius + .7);
            frame.Discs.Add(new LiquidDisc(0, _points[p].X + _normals[p].X * off, _points[p].Y + _normals[p].Y * off, radius));
        }
    }

    private double[]? _offsets;
    private double[] Offsets(int n) => _offsets is { } o && o.Length == n ? o : _offsets = new double[n];
}
