namespace BrimDeck.Core.Notices;

public enum IslandForm { Capsule, Notch, Line }

public readonly record struct LiquidPoint(double X, double Y);

// What the island does when a notice arrives: it stays its own size and moves once like a viscous liquid, then rests again.
// The notch swells at its two lower corners and the capsule at its two round ends, and the bulges run round the outline within
// 5 DIPs of it, adding up, until some reach the middle; the indicator beads along its length and drips small droplets from its
// ends. The capsule and the notch flash a backlight twice behind them; the indicator breathes twice instead.
//
// The notch's and the indicator's model, their constants and the order in which they draw random numbers are those of the
// maintainer's prototype (https://claude.ai/artifact/JU9bvJp5PPNY85vpGgUX6C), so the same seed plays the prototype's motion; the
// capsule takes the notch's (the user's call, 2026-10-30, in place of the prototype's bubbles), round its whole outline. Everything
// is decided when the notice arrives; a frame only evaluates positions at its time. Lengths are DIPs at the design's size (a 32 DIP
// high capsule or notch, a 4 DIP thick indicator); the caller scales the result. Times are seconds.
//
// Coordinates: the capsule and the notch from their top-left corner (the notch's top edge against the screen at y = 0); the
// indicator from its centre (x right, y down).
public sealed class IslandLiquid
{
    public readonly record struct Circle(double X, double Y, double R);

    // The neck between two circles pulling apart: P1 to P3 along one side (handles H1, H3), P4 back to P2 along the other
    // (handles H4, H2).
    public readonly record struct Neck(LiquidPoint P1, LiquidPoint H1, LiquidPoint H3, LiquidPoint P3, LiquidPoint P4, LiquidPoint H4, LiquidPoint H2, LiquidPoint P2);

    // A point of the outline (the notch's below the screen's edge, the capsule's all round), with its outward normal and how far
    // along the outline it lies.
    public readonly record struct EdgePoint(double X, double Y, double Nx, double Ny, double S);

    // The prototype's chosen random group: the fourth step from 11 of the sequence s -> (s * 48271 + 11) mod (2^31 - 1).
    public const int DesignSeed = 769243480;
    public const double DesignHeight = 32, Tail = 1.0, Breath = .9, Band = 5, LineReach = 12;
    private const double Pinch = .1, Refill = .2;

    public IslandForm Form { get; }
    public double Width { get; }
    public double Height { get; }
    public double Separation { get; }
    public double Total { get; private set; }
    public bool Liquid { get; }

    private readonly double _fury, _angle, _pull;
    private readonly double _corner, _shoulder;
    private readonly List<Ripple> _ripples = [];
    private readonly List<Drop> _drops = [];
    private (double L1, double L2, double V1, double V2, double P1, double P2) _beads;
    private readonly List<EdgePoint> _outline = [];
    private double _cornerRight, _cornerLeft;
    // The capsule's outline runs all round: distances along it wrap.
    private bool _closed;

    private sealed record Ripple(double Corner, int Dir, double Born, double Amp, double Sigma, double Speed, double Life, double Bead);
    private sealed record Drop(int Side, double Born, double R, double Take, double Life, double K, double V0, double Theta, double Omega, double Damp);

    // width, height: the island's size at the design's scale (for the indicator, its length and thickness). liquid: false keeps the
    // shape still (the capsule and the notch only flash, the indicator only breathes). corner, shoulder: the notch's.
    public IslandLiquid(IslandForm form, double width, double height, int seed = DesignSeed, bool liquid = true,
        double fury = 5, double separation = .6, double angle = 80, double pull = 3, double corner = 12, double shoulder = 16)
    {
        Form = form; Liquid = liquid;
        Width = Math.Max(1, Finite(width, 1)); Height = Math.Max(1, Finite(height, 1));
        _fury = fury; _angle = angle * Math.PI / 180; _pull = pull; Separation = separation;
        _corner = Math.Min(corner, Height / 2); _shoulder = shoulder;
        Total = form == IslandForm.Line ? Math.Max(separation + Tail, 2 * Breath) + .15 : separation + Tail + .15;
        var rand = Random(seed);
        switch (form)
        {
            case IslandForm.Capsule: BuildCapsuleOutline(); SpawnRipples(rand); if (!liquid) _ripples.Clear(); break;
            case IslandForm.Notch: BuildOutline(); SpawnRipples(rand); if (!liquid) _ripples.Clear(); break;
            default: SpawnDrops(rand); if (!liquid) _drops.Clear(); break;
        }
    }

    public bool Finished(double t) => t >= Total;

    // ----- Shared curves -----

    public static double Smooth(double p) { p = Math.Clamp(p, 0, 1); return p * p * (3 - 2 * p); }

    // The backlight's brightness: two flashes within about three quarters of a second.
    public static double GlowAt(double t)
    {
        ReadOnlySpan<(double T, double V)> keys = [(0, 0), (.12, 1), (.3, .12), (.42, 1), (.75, 0)];
        if (t <= 0 || t >= keys[^1].T) return 0;
        for (int i = 1; i < keys.Length; i++)
            if (t <= keys[i].T)
            {
                var (t0, v0) = keys[i - 1]; var (t1, v1) = keys[i];
                return v0 + (v1 - v0) * (.5 - .5 * Math.Cos(Math.PI * (t - t0) / (t1 - t0)));
            }
        return 0;
    }

    // The indicator's two breaths.
    public static double BreathAt(double t) => t <= 0 || t >= 2 * Breath ? 0 : Math.Pow(Math.Sin(Math.PI * t / Breath), 2);

    // mulberry32, as the prototype's rng().
    public static Func<double> Random(int seed)
    {
        uint state = (uint)seed;
        return () =>
        {
            state += 0x6D2B79F5;
            uint x = (state ^ (state >> 15)) * (1 | state);
            x = (x + ((x ^ (x >> 7)) * (61 | x))) ^ x;
            return (x ^ (x >> 14)) / 4294967296.0;
        };
    }

    private static double Exp(Func<double> rand, double mean) => -Math.Log(1 - rand()) * mean;

    // The neck between two circles pulling apart (a metaball join), gone once their gap reaches snap. Null when nothing is drawn.
    public static Neck? Join(double x1, double y1, double r1, double x2, double y2, double r2, double snap, double soft)
    {
        double dx = x2 - x1, dy = y2 - y1, d = Math.Sqrt(dx * dx + dy * dy);
        if (r1 <= .5 || r2 <= .5 || d <= Math.Max(r1, r2) - .2 * Math.Min(r1, r2) || d >= r1 + r2 + snap) return null;
        double gap = d - (r1 + r2);
        double v = soft * (gap <= 0 ? 1 : 1 - Smooth(gap / snap));
        if (v <= .03) return null;
        double u1 = d < r1 + r2 ? Math.Acos(Math.Clamp((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d), -1, 1)) : 0;
        double u2 = d < r1 + r2 ? Math.Acos(Math.Clamp((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d), -1, 1)) : 0;
        double angle = Math.Atan2(dy, dx), spread = Math.Acos(Math.Clamp((r1 - r2) / d, -1, 1));
        double a1 = angle + u1 + (spread - u1) * v, a2 = angle - u1 - (spread - u1) * v;
        double a3 = angle + Math.PI - u2 - (Math.PI - u2 - spread) * v, a4 = angle - Math.PI + u2 + (Math.PI - u2 - spread) * v;
        static LiquidPoint At(double x, double y, double r, double a) => new(x + r * Math.Cos(a), y + r * Math.Sin(a));
        LiquidPoint p1 = At(x1, y1, r1, a1), p2 = At(x1, y1, r1, a2), p3 = At(x2, y2, r2, a3), p4 = At(x2, y2, r2, a4);
        double hs = Math.Min(v * 2.6, Math.Sqrt((p3.X - p1.X) * (p3.X - p1.X) + (p3.Y - p1.Y) * (p3.Y - p1.Y)) / (r1 + r2)) * Math.Min(1, d * 2 / (r1 + r2));
        return new Neck(p1, At(p1.X, p1.Y, r1 * hs, a1 - Math.PI / 2), At(p3.X, p3.Y, r2 * hs, a3 + Math.PI / 2), p3,
            p4, At(p4.X, p4.Y, r2 * hs, a4 - Math.PI / 2), At(p2.X, p2.Y, r1 * hs, a2 + Math.PI / 2), p2);
    }

    // ----- The capsule's backlight -----

    private double R => Height / 2;

    // A closed ring round the capsule's outline between left and right (from its centre), width wide over the middle and 45 %
    // of that round the ends. Two loops, the outer and the inner, to fill even-odd.
    public (List<LiquidPoint> Outer, List<LiquidPoint> Inner) CapsuleRing(double left, double right, double width)
    {
        double cx = (left + right) / 2, half = Math.Max(1e-6, (right - left) / 2);
        double l = left + R, r = Math.Max(l, right - R);
        var pts = new List<(double X, double Y, double Nx, double Ny)>();
        const double Step = 2;
        for (double x = l; x <= r; x += Step) pts.Add((x, -R, 0, -1));
        for (double a = -Math.PI / 2; a <= Math.PI / 2; a += Step / R) pts.Add((r + R * Math.Cos(a), R * Math.Sin(a), Math.Cos(a), Math.Sin(a)));
        for (double x = r; x >= l; x -= Step) pts.Add((x, R, 0, 1));
        for (double a = Math.PI / 2; a <= Math.PI * 1.5; a += Step / R) pts.Add((l + R * Math.Cos(a), R * Math.Sin(a), Math.Cos(a), Math.Sin(a)));
        List<LiquidPoint> Loop(int sign)
        {
            var loop = new List<LiquidPoint>(pts.Count);
            foreach (var (x, y, nx, ny) in pts)
            {
                double mid = 1 - Math.Abs(x - cx) / half;
                double w = width / 2 * (.45 + .55 * Smooth(mid / .6));
                loop.Add(new(x + sign * nx * w, y + sign * ny * w));
            }
            return loop;
        }
        return (Loop(1), Loop(-1));
    }

    // ----- The outline and its ripples (the notch and the capsule) -----

    public IReadOnlyList<EdgePoint> Outline => _outline;
    public double OutlineLength { get; private set; }

    // The whole outline below the screen's edge, from where the right shoulder leaves the screen round to where the left one meets
    // it, sampled every 1.5 DIPs (each shoulder curve in 12 steps) with its outward normal. The curves are IslandBorder's.
    private void BuildOutline()
    {
        const double Step = 1.5;
        double T = 0, B = Height, l = 0, r = Width, sh = _shoulder, sd = sh * 7 / 16, br = _corner, wl = l + sh, wr = r - sh;
        double s = 0;
        void Push(double x, double y, double nx, double ny)
        {
            if (_outline.Count > 0) { var last = _outline[^1]; s += Math.Sqrt((x - last.X) * (x - last.X) + (y - last.Y) * (y - last.Y)); }
            _outline.Add(new EdgePoint(x, y, nx, ny, s));
        }
        void Line(double x1, double y1, double x2, double y2, double nx, double ny)
        {
            int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) / Step));
            for (int i = _outline.Count > 0 ? 1 : 0; i <= n; i++) Push(x1 + (x2 - x1) * i / n, y1 + (y2 - y1) * i / n, nx, ny);
        }
        void Arc(double cx, double cy, double a1, double a2)
        {
            int n = Math.Max(1, (int)Math.Ceiling(br * Math.Abs(a2 - a1) / Step));
            for (int i = 1; i <= n; i++) { double a = a1 + (a2 - a1) * i / n; Push(cx + br * Math.Cos(a), cy + br * Math.Sin(a), Math.Cos(a), Math.Sin(a)); }
        }
        // Outward is the tangent turned a quarter to the left, (ty, -tx): the outline runs clockwise on screen.
        void Cubic((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3)
        {
            const int N = 12;
            for (int i = _outline.Count > 0 ? 1 : 0; i <= N; i++)
            {
                double t = (double)i / N, u = 1 - t;
                double x = u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X;
                double y = u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y;
                double tx = 3 * u * u * (p1.X - p0.X) + 6 * u * t * (p2.X - p1.X) + 3 * t * t * (p3.X - p2.X);
                double ty = 3 * u * u * (p1.Y - p0.Y) + 6 * u * t * (p2.Y - p1.Y) + 3 * t * t * (p3.Y - p2.Y);
                double len = Math.Sqrt(tx * tx + ty * ty); if (len == 0) len = 1;
                Push(x, y, ty / len, -tx / len);
            }
        }
        Cubic((r, T), (r - sh * .28, T), (r - sh * .64, T), (r - sh * .82, T + sd * .18));
        Cubic((r - sh * .82, T + sd * .18), (wr, T + sd * .36), (wr, T + sd * .72), (wr, T + sd));
        Line(wr, T + sd, wr, B - br, 1, 0);
        double s0 = s; Arc(wr - br, B - br, 0, Math.PI / 2); _cornerRight = (s0 + s) / 2;
        Line(wr - br, B, wl + br, B, 0, 1);
        s0 = s; Arc(wl + br, B - br, Math.PI / 2, Math.PI); _cornerLeft = (s0 + s) / 2;
        Line(wl, B - br, wl, T + sd, -1, 0);
        Cubic((wl, T + sd), (wl, T + sd * .72), (wl, T + sd * .36), (l + sh * .82, T + sd * .18));
        Cubic((l + sh * .82, T + sd * .18), (l + sh * .64, T), (l + sh * .28, T), (l, T));
        OutlineLength = s;
    }

    // The capsule's outline all round, from the middle of its top edge to the right, round the right end, back along the bottom and
    // round the left end, every 1.5 DIPs with its outward normal. The middles of the two ends are where the ripples start.
    private void BuildCapsuleOutline()
    {
        const double Step = 1.5;
        double r = R, w = Width, s = 0;
        double right = Math.Max(w / 2, w - r), left = Math.Min(w / 2, r);
        _closed = true;
        void Push(double x, double y, double nx, double ny)
        {
            if (_outline.Count > 0) { var last = _outline[^1]; s += Math.Sqrt((x - last.X) * (x - last.X) + (y - last.Y) * (y - last.Y)); }
            _outline.Add(new EdgePoint(x, y, nx, ny, s));
        }
        void Line(double x1, double y, double x2, double ny)
        {
            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(x2 - x1) / Step));
            for (int i = _outline.Count > 0 ? 1 : 0; i <= n; i++) Push(x1 + (x2 - x1) * i / n, y, 0, ny);
        }
        double Arc(double cx, double a1, double a2)
        {
            double s0 = s;
            int n = Math.Max(1, (int)Math.Ceiling(r * Math.Abs(a2 - a1) / Step));
            for (int i = 1; i <= n; i++) { double a = a1 + (a2 - a1) * i / n; Push(cx + r * Math.Cos(a), r + r * Math.Sin(a), Math.Cos(a), Math.Sin(a)); }
            return (s0 + s) / 2;
        }
        Line(w / 2, 0, right, -1);
        _cornerRight = Arc(right, -Math.PI / 2, Math.PI / 2);
        Line(right, Height, left, 1);
        _cornerLeft = Arc(left, Math.PI / 2, Math.PI * 1.5);
        Line(left, 0, w / 2, -1);
        OutlineLength = s;
    }

    private void SpawnRipples(Func<double> rand)
    {
        foreach (int side in (int[])[-1, 1])
        {
            double corner = side > 0 ? _cornerRight : _cornerLeft;
            // Towards the middle of the bottom is +s from the right corner and -s from the left one.
            int inward = side > 0 ? 1 : -1;
            // The corner swells first.
            _ripples.Add(new Ripple(corner, 0, .02, Band * .85, 9, 0, .45, 0));
            double s = .06 + rand() * .04;
            while (s < Separation)
            {
                int n = rand() < .2 + _fury * .06 ? 2 : 1;
                for (int i = 0; i < n; i++)
                {
                    int dir = rand() < .68 ? inward : -inward;
                    double born = s + i * (.02 + rand() * .05), amp = 1.6 + rand() * 2.8, sigma = 4.5 + rand() * 5, speed = 220 + rand() * 200, life = .6 + rand() * .45;
                    double bead = rand() < .4 ? 1.3 + rand() * 1.1 : 0;
                    _ripples.Add(new Ripple(corner, dir, born, amp, sigma, speed, life, bead));
                    // The capsule moves the same along its top and its bottom: each ripple has its mirror on the other edge.
                    if (_closed) _ripples.Add(new Ripple(corner, -dir, born, amp, sigma, speed, life, bead));
                }
                double p = s / Separation;
                s += .03 + Exp(rand, Math.Max(.005, .2 - _fury * .03) * (p < .8 ? 1 : 1 + 4 * (p - .8)));
            }
        }
        Relay();
    }

    // The prototype's ripples fade before they reach the middle of a wider island. Each that runs towards a middle and fades short
    // of it hands on to an after-ripple where it is, a little weaker, which runs on at the same pace, until one reaches the middle.
    // The prototype's own ripples are left as they are, so near the corners the motion is exactly the prototype's.
    private void Relay()
    {
        const double Hand = .75, Rise = .08, Weaker = .8;
        int count = _ripples.Count;
        for (int i = 0; i < count; i++)
        {
            var w = _ripples[i];
            if (w.Dir == 0) continue;
            int inward = w.Corner == _cornerRight ? 1 : -1;
            if (!_closed && w.Dir != inward) continue;
            // The notch's run to the middle of its bottom; the capsule's, from the middle of an end, to the middle of the top or bottom.
            double left = _closed ? OutlineLength / 4 : Math.Abs(OutlineLength / 2 - w.Corner);
            for (int hop = 0; hop < 12; hop++)
            {
                double a = Hand * w.Life, run = w.Speed * (1 - Math.Exp(-.6 * a)) / .6;
                left -= run;
                if (left <= 0) break;
                w = w with { Corner = w.Corner + w.Dir * run, Born = w.Born + a - Rise, Amp = w.Amp * Weaker, Bead = 0 };
                _ripples.Add(w);
            }
        }
        // The motion lasts until the last after-ripple has faded.
        foreach (var w in _ripples) Total = Math.Max(Total, w.Born + w.Life + .15);
    }

    private static double RippleEnvelope(Ripple w, double a) => Smooth(a / .08) * (1 - Smooth((a - w.Life * .6) / (w.Life * .4)));
    private static double RipplePlace(Ripple w, double a) => w.Corner + w.Dir * w.Speed * (1 - Math.Exp(-.6 * a)) / .6;

    // The distance from b to a along the outline; on the closed capsule the shorter way round.
    private double Along(double a, double b)
    {
        double d = a - b;
        if (!_closed || OutlineLength <= 0) return d;
        d %= OutlineLength;
        if (d > OutlineLength / 2) d -= OutlineLength; else if (d < -OutlineLength / 2) d += OutlineLength;
        return d;
    }

    // How far the edge stands out at distance s along its outline, at time t: the bulges add up, softly capped at Band.
    public double EdgeOut(double s, double t)
    {
        if (_ripples.Count == 0) return 0;
        double sum = 0;
        foreach (var w in _ripples)
        {
            double a = t - w.Born;
            if (a <= 0 || a >= w.Life) continue;
            double ds = Along(s, RipplePlace(w, a));
            if (Math.Abs(ds) > w.Sigma * 3.5) continue;
            sum += w.Amp * RippleEnvelope(w, a) * Math.Exp(-ds * ds / (2 * w.Sigma * w.Sigma));
        }
        // The notch keeps still where it meets the screen.
        double fromTop = _closed ? 6 : Math.Min(s, OutlineLength - s);
        return Band * Math.Tanh(sum / Band) * Smooth(fromTop / 6);
    }

    // The beads that trail some bulges, just clear of the edge and within the band.
    public void Beads(double t, List<Circle> circles)
    {
        circles.Clear();
        if (_outline.Count == 0) return;
        foreach (var w in _ripples)
        {
            if (w.Bead <= 0) continue;
            double a = t - w.Born;
            if (a <= .06 || a >= w.Life) continue;
            double s = RipplePlace(w, a) - w.Dir * w.Sigma * 1.7;
            if (_closed) s = ((s % OutlineLength) + OutlineLength) % OutlineLength;
            else if (s < 0 || s > OutlineLength) continue;
            double rb = w.Bead * RippleEnvelope(w, a);
            if (rb < .4) continue;
            var p = _outline[Math.Clamp((int)Math.Round(s / OutlineLength * (_outline.Count - 1)), 0, _outline.Count - 1)];
            double off = Math.Min(Band - rb, EdgeOut(s, t) + rb + .7);
            circles.Add(new Circle(p.X + p.Nx * off, p.Y + p.Ny * off, rb));
        }
    }

    // The notch's backlight: a band along its outline, width wide where it is full, narrowing up the walls to about a third and
    // closing only in the last few DIPs where the shoulders meet the screen. One closed polygon (outer edge, inner edge back).
    public List<LiquidPoint> NotchBand(double width)
    {
        var outer = new List<LiquidPoint>(_outline.Count * 2);
        var inner = new List<LiquidPoint>(_outline.Count);
        foreach (var p in _outline)
        {
            double end = Math.Min(p.S, OutlineLength - p.S);
            double half = width / 2 * (.35 + .65 * Smooth((end - 10) / 36)) * Smooth(end / 5);
            outer.Add(new(p.X + p.Nx * half, p.Y + p.Ny * half));
            inner.Add(new(p.X - p.Nx * half, p.Y - p.Ny * half));
        }
        inner.Reverse();
        outer.AddRange(inner);
        return outer;
    }

    // ----- The indicator -----

    private double LineHalf0 => Height / 2;

    private void SpawnDrops(Func<double> rand)
    {
        double thick = Height;
        foreach (int side in (int[])[-1, 1])
        {
            double s = .08 + rand() * .06;
            while (s < Separation)
            {
                double r = thick / 2 * (.55 + rand() * .45), k = 9 + rand() * 4;
                double life = .5 + rand() * .4, v0 = k * (r * 2.6 + rand() * (LineReach - r * 2.6)), theta = (rand() * 2 - 1) * _angle, omega = 10 + rand() * 6;
                _drops.Add(new Drop(side, s, r, r * 1.3, life, k, v0, theta, omega, 1.5 + _pull * .9));
                s += .06 + Exp(rand, Math.Max(.005, .22 - _fury * .03));
            }
        }
        _drops.Sort((a, b) => a.Born.CompareTo(b.Born));
        // Beading: two waves of different length drifting in opposite directions.
        _beads = (9 + rand() * 3, 14 + rand() * 4, 18 + rand() * 10, -(10 + rand() * 8), rand() * 6.28, rand() * 6.28);
    }

    // Half the indicator's length on one side: its ends spring back as droplets leave and refill, never by more than twice its thickness.
    public double LineEnd(double t, int side)
    {
        double end = Width / 2;
        foreach (var b in _drops)
        {
            if (b.Side != side) continue;
            double a = t - b.Born;
            if (a > 0) end -= b.Take * (a < Pinch ? Smooth(a / Pinch) : Math.Exp(-(a - Pinch) / Refill));
        }
        return Math.Max(Width / 2 - Height * 2, end);
    }

    // Half the indicator's thickness at x (from its centre): it beads while droplets separate and settles after.
    public double LineHalf(double x, double t)
    {
        double amp = Liquid ? .5 * Smooth(t / .12) * (1 - Smooth((t - Separation) / .45)) : 0;
        if (amp <= 0) return LineHalf0;
        var b = _beads;
        double wave = .6 * Math.Cos(2 * Math.PI * (x - b.V1 * t) / b.L1 + b.P1) + .4 * Math.Cos(2 * Math.PI * (x - b.V2 * t) / b.L2 + b.P2);
        return LineHalf0 * Math.Max(.45, 1 + amp * wave);
    }

    // The droplets leaving the indicator's ends and their necks, from its centre.
    public void LineDrops(double t, List<Circle> circles, List<Neck> necks)
    {
        circles.Clear(); necks.Clear();
        double h0 = LineHalf0;
        foreach (int side in (int[])[-1, 1])
        {
            double capX = side < 0 ? -LineEnd(t, -1) + h0 : LineEnd(t, 1) - h0, capH = LineHalf(capX, t);
            foreach (var b in _drops)
            {
                if (b.Side != side) continue;
                double a = t - b.Born;
                if (a < 0 || a > b.Life) continue;
                double p = a / b.Life, from = side * (Width / 2 - h0);
                double x = from + side * ((h0 - b.R) + b.V0 * (1 - Math.Exp(-b.K * a)) / b.K);
                double y = Math.Exp(-b.Damp * a) * (b.V0 * Math.Sin(b.Theta) * .5 / b.Omega) * Math.Sin(b.Omega * a);
                double r = b.R * (p > .7 ? 1 - Smooth((p - .7) / .3) : 1);
                if (r <= .25) continue;
                y = Math.Clamp(y, -Height, Height);
                circles.Add(new Circle(x, y, r));
                if (p < .6 && Join(capX, 0, capH, x, y, r, r * .9, .65) is { } neck) necks.Add(neck);
            }
        }
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
