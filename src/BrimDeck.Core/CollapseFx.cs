namespace BrimDeck.Core;

// How a theme's notice or open panel goes back into the island (ThemeMotion.Collapse). fade is the panel's own closing and
// stays the default. The others are a texture on that same closing, never a picture of their own: the island's outline
// morphs into the notch, capsule or line exactly as it always does (MainWindow.SetExpanded, the monitor page's curves), on a
// move this long (Length) instead of the plain one, and only the content that leaves is drawn differently while it goes,
// over the first part of the move (Track), inside the island's own clip:
//   glitch (cyberpunk): the content tears into horizontal bands that slide sideways in smooth waves and drop out one by one;
//     once per collapse, if the second's flashes allow it, a few frames jolt harder with neon edges.
//   foam (island holiday): the content dissolves along a value-noise field with a soft line of foam at the dissolving edge.
//   genie (liquid glass): the content is drawn into the island along the macOS lamp curve, on a 12 x 12 mesh inside a
//     rounded outline.
// All of it is drawn by the composition engine from one snapshot taken as the closing starts: bands are the snapshot clipped
// and moved, the dissolve is the snapshot under one of a set of small masks made at that moment, the genie is the snapshot
// under one affine transform per mesh cell. No frame works through pixels. Everything here is arithmetic on the progress, so
// a frame depends on its time alone and looks the same in a preview.
//
// The glitch's bands and hash, the dissolve's value noise and the lamp curve come from windows-animations by ReDrag and, for
// the macOS genie, Abdullah Masood and Potassiumuncher (MIT; ThirdPartyNotices.txt). Changes: band and noise scales follow
// the island's size, the bands slide smoothly instead of jumping, the dissolve is spread evenly over the move and leaves a foam
// edge, the genie's target is the island's own outline.
public enum CollapseStyle { Fade, Glitch, Foam, Genie }

public static class CollapseFx
{
    // The closing's length in seconds at pace 1 when a texture plays, and the range the theme's pace may move it within: the
    // panel's own closing family (the 360 ms setting's 270 ms close, a little longer so the texture can be seen).
    public const double GlitchLength = .42, GlitchMin = .40, GlitchMax = .45;
    public const double FoamLength = .48, FoamMin = .46, FoamMax = .52;
    // The user's genie: 360 ms times the genie's 1.25.
    public const double GenieLength = .45, GenieMin = .42, GenieMax = .48;
    // The chromatic pulse a theme may add to a completed press (ThemeMotion.Aberration): this long at most, red and blue this
    // many DIP apart at the edges.
    public const double AberrationLength = .12, AberrationMin = 1, AberrationMax = 1.5;
    // A theme's own length for its closing (ThemeMotion.CollapseMs), in milliseconds.
    public const int CollapseMsMin = 200, CollapseMsMax = 630;

    // The closing's length in seconds: the theme's own (milliseconds, 0 for the style's) or the style's at the theme's pace.
    public static double Length(CollapseStyle style, double pace, int milliseconds = 0)
    {
        if (style == CollapseStyle.Fade) return 0;
        if (milliseconds > 0) return Math.Clamp(milliseconds, CollapseMsMin, CollapseMsMax) / 1000.0;
        double p = double.IsFinite(pace) ? Math.Clamp(pace, MotionSafety.PaceMin, MotionSafety.PaceMax) : 1;
        return style switch
        {
            CollapseStyle.Glitch => Math.Clamp(GlitchLength * p, GlitchMin, GlitchMax),
            CollapseStyle.Foam => Math.Clamp(FoamLength * p, FoamMin, FoamMax),
            CollapseStyle.Genie => Math.Clamp(GenieLength * p, GenieMin, GenieMax),
            _ => 0
        };
    }

    // The share of the closing over which the texture plays, from its start: the content's own track (PanelCloseContent)
    // stretched so the texture can be read, while the outline morphs on the panel's own surface track. The genie draws the
    // content into the island and needs most of the move.
    public static (double Start, double End) Track(CollapseStyle style) => style == CollapseStyle.Genie ? (0, .85) : (0, .75);

    // Whether a texture plays: the theme asks for one and motion is allowed (BrimDeck's and Windows' animation settings, high
    // contrast). It spends no flash; only the glitch's jolt does (Jolt).
    public static bool Plays(CollapseStyle wanted, in MotionConditions conditions) => wanted != CollapseStyle.Fade && conditions.MotionAllowed;

    // The glitch's jolt is a flash: it plays only while the second's shared flashes allow it, and spends one.
    public static bool Jolt(CollapseStyle style, FlashBudget budget, double now) => style == CollapseStyle.Glitch && budget.TryTake(now);

    public static double SmoothStep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / Math.Max(1e-5, e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    // The red and blue offset in device pixels at a time into a press's pulse: one swell and back within AberrationLength.
    public static double AberrationAt(double seconds, double dip, double scale)
        => dip <= 0 || seconds < 0 || seconds >= AberrationLength ? 0 : dip * scale * Math.Sin(Math.PI * seconds / AberrationLength);

    // windows-animations' hash (RenderGlitch's ihash), to the bit.
    public static double GlitchHash(int x, int y)
    {
        uint h = unchecked((uint)(x * 374761393) ^ (uint)(y * 668265263));
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return (h & 0xFFFFu) / 65535.0;
    }

    // Bands for content this many DIP tall: one per four DIP, 24 to 32 (the original's 70 suit a whole window).
    public static int GlitchBands(double heightDip) => Math.Clamp((int)Math.Round(heightDip / 4), 24, 32);
}

// The glitch texture: each band of the content slides sideways and drops out on its own. A band that the original's hash
// marks (threshold 0.55, about 45 percent) tears up to half the original's ±5 percent of the width; every band also jitters
// by up to the original's ±1.25 percent. Both are slow waves (at most one swing over the texture), swelling in
// and dying away, so nothing jumps; bands drop out between 40 and 100 percent of the way. The jolt, a flash, holds a tenth of
// the way from 30 percent: the tears double and the band edges take the theme's neon colors.
public static class GlitchTexture
{
    public const double Threshold = .55, Tear = .10, Jitter = .025, JoltFrom = .30, JoltTo = .40;

    // How far a band moves sideways at a progress, as a share of the content's width. jolt: the jolt plays in this collapse.
    public static double Offset(int band, double progress, bool jolt = false)
    {
        double p = Math.Clamp(progress, 0, 1);
        double swell = CollapseFx.SmoothStep(0, .3, p) * (1 - CollapseFx.SmoothStep(.7, 1, p));
        bool tears = CollapseFx.GlitchHash(band, 0) >= Threshold;
        double f = .4 + .6 * CollapseFx.GlitchHash(band, 1), phase = CollapseFx.GlitchHash(band, 2);
        double g = .4 + .6 * CollapseFx.GlitchHash(band, 3), phase2 = CollapseFx.GlitchHash(band, 4);
        double tear = tears ? Tear / 2 * Math.Sin(2 * Math.PI * (f * p + phase)) : 0;
        double jitter = Jitter / 2 * Math.Sin(2 * Math.PI * (g * p + phase2));
        double kick = jolt && JoltOn(p) ? 2 : 1;
        return swell * (tear * kick + jitter);
    }

    public static bool JoltOn(double progress) => progress >= JoltFrom && progress < JoltTo;

    // How much of a band shows at a progress: whole until its own moment between 40 and 80 percent, then gone over a fifth.
    public static double Alpha(int band, double progress)
    {
        double from = .4 + .4 * CollapseFx.GlitchHash(band, 5);
        return 1 - CollapseFx.SmoothStep(from, from + .2, Math.Clamp(progress, 0, 1));
    }
}

// The foam texture's field and masks. The field is the original's three octaves of value noise, worked out per block (Block
// DIP square, so a panel has at most about 16 000 blocks) and spread evenly over the move by rank, so no corner lingers and the
// same share dissolves at every moment. Levels masks are made at once from it, each a soft edge at one moment of the dissolve
// and a soft line of foam just ahead of it; a frame only picks the one for its progress.
public sealed class FoamField
{
    public const int Levels = 40;
    // How soft the dissolving edge is, and how wide the foam, as shares of the move.
    public const double Edge = .035, FoamWidth = .07;
    private readonly float[] _start;
    public int Columns { get; }
    public int Rows { get; }
    public double Block { get; }

    public FoamField(double widthDip, double heightDip)
    {
        Block = Math.Max(3, Math.Ceiling(Math.Sqrt(Math.Max(1, widthDip * heightDip) / 16000)));
        Columns = Math.Max(1, (int)Math.Ceiling(widthDip / Block)); Rows = Math.Max(1, (int)Math.Ceiling(heightDip / Block));
        _start = new float[Columns * Rows];
        // A cell is half the content's shorter side, so a notice and a panel both dissolve in a few large islands.
        double scale = Math.Max(4, Math.Min(widthDip, heightDip) * .5) / Block;
        double amplitude = .5;
        for (int octave = 0; octave < 3; octave++)
        {
            double cell = scale / (1 << octave);
            for (int y = 0; y < Rows; y++)
            {
                double ty = y / cell; int iy = (int)Math.Floor(ty); double fy = ty - iy, uy = fy * fy * (3 - 2 * fy);
                for (int x = 0; x < Columns; x++)
                {
                    double tx = x / cell; int ix = (int)Math.Floor(tx); double fx = tx - ix, ux = fx * fx * (3 - 2 * fx);
                    double top = Hash(ix, iy) * (1 - ux) + Hash(ix + 1, iy) * ux, bottom = Hash(ix, iy + 1) * (1 - ux) + Hash(ix + 1, iy + 1) * ux;
                    _start[y * Columns + x] += (float)(amplitude * (top * (1 - uy) + bottom * uy));
                }
            }
            amplitude *= .5;
        }
        // Spread over the run by rank: a histogram of 1024 steps, each block keeping its place within its step.
        float low = float.MaxValue, high = float.MinValue;
        foreach (var v in _start) { low = Math.Min(low, v); high = Math.Max(high, v); }
        const int bins = 1024;
        var below = new int[bins + 1];
        float span = Math.Max(1e-6f, high - low);
        int Bin(float v) => Math.Min(bins - 1, (int)((v - low) / span * bins));
        foreach (var v in _start) below[Bin(v) + 1]++;
        for (int i = 1; i <= bins; i++) below[i] += below[i - 1];
        double total = Math.Max(1, _start.Length - 1);
        for (int i = 0; i < _start.Length; i++)
        {
            int bin = Bin(_start[i]);
            double within = (_start[i] - low) / span * bins - bin, rank = below[bin] + within * (below[bin + 1] - below[bin] - 1);
            _start[i] = (float)Math.Clamp(rank / total, 0, 1);
        }
    }

    // windows-animations' pseudo_hash, to the bit.
    public static double Hash(int x, int y) => unchecked((uint)x * 73856093u ^ (uint)y * 19349663u) % 10000 / 10000.0;

    // When a block dissolves, 0 to 1.
    public float StartOf(int column, int row) => _start[row * Columns + column];

    // The level a progress shows.
    public static int LevelAt(double progress) => Math.Clamp((int)Math.Round(Math.Clamp(progress, 0, 1) * (Levels - 1)), 0, Levels - 1);

    // The dissolve's front at a level: everything has gone at the last one, nothing at the first.
    public static double Front(int level) => (double)level / (Levels - 1) * (1 + Edge + FoamWidth) - Edge - FoamWidth;

    // A level's mask (how much of each block shows) and foam (how much foam lies on it), 0 to 255, block by block.
    public void Mask(int level, byte[] shown, byte[] foam)
    {
        double q = Front(level);
        for (int i = 0; i < _start.Length; i++)
        {
            double s = _start[i];
            double a = CollapseFx.SmoothStep(q, q + Edge, s);
            double f = CollapseFx.SmoothStep(q, q + Edge, s) * (1 - CollapseFx.SmoothStep(q + Edge, q + Edge + FoamWidth, s));
            shown[i] = (byte)Math.Round(255 * a);
            foam[i] = (byte)Math.Round(255 * f);
        }
    }
}

public readonly record struct GenieRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

// The genie's mesh: the snapshot's corners moved along the macOS lamp curve into the island. The curve runs in a canonical
// space where the dock lies below the window; an island above the snapshot (a notch, a capsule at the top) mirrors it, as the
// original does for a taskbar at the top.
public static class GenieCollapse
{
    public const int Tiles = 12;
    public const double Corner = 12;

    // The lamp curve (MacGenieLampVertex), to the expression: a mesh vertex (tx, ty in 0 to 1) at progress p (after smoothstep),
    // for the window w and the dock i, both canonical.
    public static (double X, double Y) LampVertex(double tx, double ty, double p, GenieRect w, GenieRect i)
    {
        const double split = .3;
        double k = p <= split ? p / split : 1;
        double j = p > split ? (p - split) / (1 - split) : 0;
        double expandHeight = i.Y - w.Y - w.Height;
        double fullHeight = i.Y - w.Y - expandHeight * (1 - k);
        double height = fullHeight - j * fullHeight;
        double y = ty * height;
        double x = tx * i.Width + tx * (w.Width - i.Width) * (1 - j) * (1 - ty) + tx * (w.Width - i.Width) * (1 - k) * ty;
        double safeFullHeight = Math.Abs(fullHeight) < .001 ? .001 : fullHeight;
        double offsetX = (i.X - w.X) * (y / (fullHeight + .1)) * k + (i.X - w.X) * j;
        double offsetY = i.Y - w.Y - height - expandHeight * (1 - k);
        double effectX = Math.Sin((height - y) / safeFullHeight * 2 * Math.PI + Math.PI) * (w.X + w.Width * tx - (i.X + i.Width * tx)) / 7 * k;
        return (w.X + x + offsetX + effectX, w.Y + y + offsetY);
    }

    // The mesh at a progress (0 to 1, smoothstep applied here) for the snapshot and the island in the same coordinates, into
    // `points` ((Tiles + 1)^2, row by row from the snapshot's top). Returns the mesh's bounds, empty once it has gone flat.
    public static GenieRect Mesh(double progress, GenieRect window, GenieRect island, Span<(double X, double Y)> points)
    {
        int n = Tiles + 1;
        if (points.Length < n * n) throw new ArgumentException("Too few points for the mesh.");
        double raw = Math.Clamp(progress, 0, 1), t = raw * raw * (3 - 2 * raw);
        // The island above the snapshot's middle: the original's top taskbar (y mirrored); otherwise its bottom one.
        bool above = island.Y + island.Height / 2 < window.Y + window.Height / 2;
        var w = above ? new GenieRect(window.X, -(window.Y + window.Height), window.Width, window.Height) : window;
        var i = above ? new GenieRect(island.X, -island.Bottom, island.Width, island.Height) : island;
        double left = double.MaxValue, right = double.MinValue, top = double.MaxValue, bottom = double.MinValue;
        for (int gy = 0; gy < n; gy++)
            for (int gx = 0; gx < n; gx++)
            {
                double u = (double)gx / Tiles, v = (double)gy / Tiles;
                var (cx, cy) = LampVertex(u, above ? 1 - v : v, t, w, i);
                var point = (cx, above ? -cy : cy);
                points[gy * n + gx] = point;
                left = Math.Min(left, point.Item1); right = Math.Max(right, point.Item1);
                top = Math.Min(top, point.Item2); bottom = Math.Max(bottom, point.Item2);
            }
        return right - left < .5 || bottom - top < .5 ? default : new GenieRect(left, top, right - left, bottom - top);
    }
}

// One collapse picture in time: which style plays, from when and for how long, and whether it has been stopped. A new notice,
// the panel opening again or anything else that needs the island stops it at once; nothing of it is drawn after that.
public sealed class CollapsePlayback
{
    public CollapseStyle Style { get; private set; }
    public double StartedAt { get; private set; } = double.NaN;
    public double Length { get; private set; }
    public bool Active => Style != CollapseStyle.Fade && !double.IsNaN(StartedAt);

    public void Start(CollapseStyle style, double now, double length)
    {
        if (style == CollapseStyle.Fade || !(length > 0)) { Stop(); return; }
        Style = style; StartedAt = now; Length = length;
    }

    public void Stop() { Style = CollapseStyle.Fade; StartedAt = double.NaN; Length = 0; }

    // The progress at a time, 0 to 1; -1 when nothing plays.
    public double Progress(double now) => !Active ? -1 : Math.Clamp((now - StartedAt) / Length, 0, 1);
    public double Elapsed(double now) => !Active ? 0 : Math.Max(0, now - StartedAt);
    public bool Done(double now) => !Active || now - StartedAt >= Length;
}
