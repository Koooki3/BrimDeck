namespace BrimDeck.Core;

// How a theme's notice or open panel goes back into the island (ThemeMotion.Collapse). fade is the panel's own closing and
// stays the default; the others are short pictures played on a snapshot of what leaves, while the real element is already
// in its final state underneath (ux #119: cancellable, the final state set at once):
//   glitch (cyberpunk): the snapshot tears into horizontal bands that jump sideways in three hard steps and breaks up pixel by
//     pixel as it fades (windows-animations' Cyber Glitch, scaled to the island).
//   foam (island holiday): the snapshot dissolves along a value-noise field, leaving a thin line of foam at the dissolving
//     edge (windows-animations' Perlin Dissolve).
//   genie (liquid glass): the snapshot is drawn into the island along the macOS lamp curve, on a 12 x 12 mesh inside a
//     rounded outline (windows-animations' macOS genie).
// The pixel effects write premultiplied BGRA (WPF's Pbgra32) into a buffer the caller shows; the genie gives the mesh, which
// the caller draws as one affine piece per cell. Everything here is plain arithmetic on the progress, so a frame depends on
// its time alone and the same collapse looks the same in a preview. Each collapse counts as one flash (FlashBudget).
//
// The glitch, the dissolve and the lamp curve are ported from windows-animations by ReDrag and, for the macOS genie, by
// Abdullah Masood and Potassiumuncher (MIT; ThirdPartyNotices.txt). Changes: band and noise scales follow the island's size,
// the glitch's seeds step three times (the cyberpunk theme's steps), the dissolve leaves a foam edge, the genie's target is
// the island's own outline.
public enum CollapseStyle { Fade, Glitch, Foam, Genie }

public static class CollapseFx
{
    // The lengths in seconds at pace 1, and the range the theme's pace may move each within.
    public const double GlitchLength = .27, GlitchMin = .24, GlitchMax = .30;
    public const double FoamLength = .33, FoamMin = .30, FoamMax = .36;
    // The user's genie: 360 ms times the genie's 1.25.
    public const double GenieLength = .45, GenieMin = .42, GenieMax = .48;
    // The chromatic pulse a theme may add (ThemeMotion.Aberration): this long at most, red and blue this many DIP apart at the
    // edges and none at the centre.
    public const double AberrationLength = .12, AberrationMin = 1, AberrationMax = 1.5;

    public static double Length(CollapseStyle style, double pace)
    {
        double p = double.IsFinite(pace) ? Math.Clamp(pace, MotionSafety.PaceMin, MotionSafety.PaceMax) : 1;
        return style switch
        {
            CollapseStyle.Glitch => Math.Clamp(GlitchLength * p, GlitchMin, GlitchMax),
            CollapseStyle.Foam => Math.Clamp(FoamLength * p, FoamMin, FoamMax),
            CollapseStyle.Genie => Math.Clamp(GenieLength * p, GenieMin, GenieMax),
            _ => 0
        };
    }

    // What a collapse plays: the style the theme asks for while motion is allowed and the second's flashes are not spent,
    // otherwise the default fade. Taking a picture spends one of the shared flashes.
    public static CollapseStyle Choose(CollapseStyle wanted, in MotionConditions conditions, FlashBudget budget, double now)
        => wanted == CollapseStyle.Fade || !conditions.MotionAllowed || !budget.TryTake(now) ? CollapseStyle.Fade : wanted;

    // Whether a picture could be taken now, without spending anything: the caller snapshots only then.
    public static bool Possible(CollapseStyle wanted, in MotionConditions conditions, FlashBudget budget, double now)
        => wanted != CollapseStyle.Fade && conditions.MotionAllowed && budget.Available(now);

    public static double SmoothStep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / Math.Max(1e-5, e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    // The red and blue channels' offset in device pixels at a time into the collapse: one swell and back within
    // AberrationLength, nothing after it. dip is the theme's amount (0: none).
    public static double AberrationAt(double seconds, double dip, double scale)
        => dip <= 0 || seconds < 0 || seconds >= AberrationLength ? 0 : dip * scale * Math.Sin(Math.PI * seconds / AberrationLength);

    // Red moved right and blue left by up to `offset` device pixels, none at the row's centre and all of it at its ends, as the
    // wallpaper's center falloff; green and the coverage stay. `row` is one row of premultiplied pixels, `scratch` as long.
    public static void Aberrate(Span<uint> row, Span<uint> scratch, double offset)
    {
        int w = row.Length;
        if (offset < .5 || w < 3) return;
        row.CopyTo(scratch);
        double half = (w - 1) / 2.0;
        for (int x = 0; x < w; x++)
        {
            int d = (int)Math.Round(offset * Math.Abs(x - half) / half);
            if (d == 0) continue;
            uint c = scratch[x];
            uint red = x - d >= 0 ? scratch[x - d] : 0, blue = x + d < w ? scratch[x + d] : 0;
            uint a = Math.Max(c >> 24, Math.Max(red >> 24, blue >> 24));
            row[x] = a << 24 | red & 0xFF0000 | c & 0xFF00 | blue & 0xFF;
        }
    }

    // ----- Glitch -----

    // windows-animations' hash (RenderGlitch's ihash), to the bit.
    public static double GlitchHash(int x, int y)
    {
        uint h = unchecked((uint)(x * 374761393) ^ (uint)(y * 668265263));
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return (h & 0xFFFFu) / 65535.0;
    }

    // Bands for a picture this many DIP tall: one per four DIP, 24 to 32 (the original's 70 suit a whole window).
    public static int GlitchBands(double heightDip) => Math.Clamp((int)Math.Round(heightDip / 4), 24, 32);

    // The alpha the original gives its progress (after smoothstep): (1 - ss(0,1,p))(1 - ss(.55,1,p)).
    public static double GlitchAlpha(double progress)
    {
        double p = SmoothStep(0, 1, progress);
        return (1 - SmoothStep(0, 1, p)) * (1 - SmoothStep(.55, 1, p));
    }
}

// The glitch on one snapshot. The canvas is wider than the snapshot by Margin on either side, room for the tears.
public sealed class GlitchCollapse
{
    public const double Threshold = .55, Tear = .10, Jitter = .025, Breakup = .92;
    // The seeds step this many times over the collapse, as the cyberpunk theme's own steps:3 (the original's 18 to 64 steps).
    public const int SeedSteps = 3;
    private readonly uint[] _source;
    private readonly uint[] _scratch;
    public int Width { get; }
    public int Height { get; }
    public int Bands { get; }
    public int Margin { get; }
    public int CanvasWidth => Width + 2 * Margin;

    public GlitchCollapse(uint[] source, int width, int height, int bands)
    {
        if (width < 1 || height < 1 || source.Length < width * height) throw new ArgumentException("The snapshot does not match its size.");
        _source = source; Width = width; Height = height; Bands = Math.Clamp(bands, 1, 256);
        // The furthest a row moves: half the tear and half the jitter of the width.
        Margin = (int)Math.Ceiling((Tear + Jitter) / 2 * width) + 1;
        _scratch = new uint[CanvasWidth];
    }

    // How far a row moves sideways at a progress, in pixels (the original's tear plus micro-jitter).
    public int Shift(int y, double progress)
    {
        double p = CollapseFx.SmoothStep(0, 1, progress);
        int seed = (int)Math.Floor(p * SeedSteps);
        // The jitter lines are as much finer than the bands as the original's 220 are than its 70.
        int microLines = (int)Math.Round(Bands * 220 / 70.0);
        int lineId = (int)((double)y / Height * Bands);
        double band = CollapseFx.GlitchHash(lineId, seed) >= Threshold ? 1 : 0;
        double tear = (CollapseFx.GlitchHash((int)(lineId * 1.7f), seed) - .5) * Tear * band;
        double micro = (CollapseFx.GlitchHash((int)((double)y / Height * microLines), seed) - .5) * Jitter;
        return (int)((tear + micro) * Width);
    }

    // One frame into `canvas` (CanvasWidth x Height, cleared here). aberration: the red and blue offset in device pixels.
    public void Render(double progress, uint[] canvas, double aberration = 0)
    {
        Array.Clear(canvas);
        double p = CollapseFx.SmoothStep(0, 1, Math.Clamp(progress, 0, 1));
        double alpha = (1 - CollapseFx.SmoothStep(0, 1, p)) * (1 - CollapseFx.SmoothStep(.55, 1, p));
        if (alpha <= .01) return;
        int seed = (int)Math.Floor(p * SeedSteps);
        double cut = 1 - p * Breakup;
        // Until about a tenth of the way nothing breaks up, as in the original.
        bool whole = cut >= Breakup;
        bool opaque = alpha >= .99;
        uint scale = (uint)Math.Round(alpha * 256);
        int stride = CanvasWidth;
        for (int y = 0; y < Height; y++)
        {
            int shift = Shift(y, progress);
            var row = canvas.AsSpan(y * stride, stride);
            int src = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (!whole && CollapseFx.GlitchHash(x, y + seed * 17) > cut) continue;
                int dx = Margin + x + shift;
                if ((uint)dx >= (uint)stride) continue;
                uint c = _source[src + x];
                row[dx] = opaque ? c : Scale(c, scale);
            }
            if (aberration >= .5) CollapseFx.Aberrate(row, _scratch, aberration);
        }
    }

    // Every channel of a premultiplied pixel times scale / 256.
    internal static uint Scale(uint c, uint scale)
    {
        uint rb = (c & 0x00FF00FF) * scale >> 8 & 0x00FF00FF;
        uint ag = (c >> 8 & 0x00FF00FF) * scale & 0xFF00FF00;
        return rb | ag;
    }
}

// The foam dissolve on one snapshot, drawn in place (the canvas is the snapshot's size).
public sealed class FoamCollapse
{
    // windows-animations' Perlin Dissolve: every pixel starts dissolving at 0.9 times three octaves of value noise and is gone
    // a LifeSpan of the progress later.
    public const double LifeSpan = .15, StartScale = .9;
    private readonly uint[] _source;
    private readonly float[] _start;
    private readonly byte[] _gone, _near;
    private readonly uint[] _scratch;
    private readonly uint _foam;
    public int Width { get; }
    public int Height { get; }
    // The foam's width in device pixels (1 DIP).
    public int Edge { get; }
    public double NoiseScale { get; }

    // scale: device pixels per DIP. foam: the foam's color, straight (not premultiplied) BGRA with its alpha.
    public FoamCollapse(uint[] source, int width, int height, double scale, uint foam = 0xE6FFFFFF)
    {
        if (width < 1 || height < 1 || source.Length < width * height) throw new ArgumentException("The snapshot does not match its size.");
        _source = source; Width = width; Height = height; _foam = foam;
        Edge = Math.Max(1, (int)Math.Round(scale));
        // The original's 150 px cells suit a whole window; here a cell is half the snapshot's shorter side, so a notice and a
        // panel both dissolve in a few large islands rather than in dust.
        NoiseScale = Math.Max(4, Math.Min(width, height) * .5);
        _start = new float[width * height];
        _gone = new byte[width * height];
        _near = new byte[width * height];
        _scratch = new uint[width];
        double amplitude = .5;
        for (int octave = 0; octave < 3; octave++)
        {
            double cell = NoiseScale / (1 << octave);
            for (int y = 0; y < height; y++)
            {
                double ty = y / cell;
                int iy = (int)Math.Floor(ty);
                double fy = ty - iy, uy = fy * fy * (3 - 2 * fy);
                int i = y * width;
                for (int x = 0; x < width; x++, i++)
                {
                    double tx = x / cell;
                    int ix = (int)Math.Floor(tx);
                    double fx = tx - ix, ux = fx * fx * (3 - 2 * fx);
                    double top = Hash(ix, iy) * (1 - ux) + Hash(ix + 1, iy) * ux;
                    double bottom = Hash(ix, iy + 1) * (1 - ux) + Hash(ix + 1, iy + 1) * ux;
                    _start[i] += (float)(amplitude * (top * (1 - uy) + bottom * uy));
                }
            }
            amplitude *= .5;
        }
        for (int i = 0; i < _start.Length; i++) _start[i] *= (float)StartScale;
    }

    // windows-animations' pseudo_hash, to the bit.
    public static double Hash(int x, int y) => unchecked((uint)x * 73856093u ^ (uint)y * 19349663u) % 10000 / 10000.0;

    public float StartOf(int x, int y) => _start[y * Width + x];

    // One frame into `canvas` (Width x Height). A pixel fades over its LifeSpan; one still there within Edge of a pixel that
    // has gone turns to foam. The foam itself fades in the last tenth.
    public void Render(double progress, uint[] canvas, double aberration = 0)
    {
        double p = Math.Clamp(progress, 0, 1);
        int w = Width, h = Height;
        // Gone wherever the noise has passed, inside the shape or not: the shape's own transparent surroundings foam only once
        // the dissolve reaches them, so its outline does not foam from the first frame.
        for (int i = 0; i < _start.Length; i++) _gone[i] = (byte)((p - _start[i]) / LifeSpan >= 1 ? 1 : 0);
        Dilate(w, h);
        double foamFade = 1 - CollapseFx.SmoothStep(.9, 1, p);
        uint foamAlpha = (uint)Math.Round((_foam >> 24) * foamFade);
        uint foam = foamAlpha == 0 ? 0 : Premultiply(_foam & 0xFFFFFF | foamAlpha << 24);
        for (int i = 0; i < _start.Length; i++)
        {
            uint c = _source[i];
            if (_gone[i] != 0 || c >> 24 == 0) { canvas[i] = 0; continue; }
            if (_near[i] != 0 && foam != 0) { canvas[i] = foam; continue; }
            double local = (p - _start[i]) / LifeSpan;
            canvas[i] = local <= 0 ? c : GlitchCollapse.Scale(c, (uint)Math.Round((1 - local) * 256));
        }
        if (aberration >= .5)
            for (int y = 0; y < h; y++) CollapseFx.Aberrate(canvas.AsSpan(y * w, w), _scratch, aberration);
    }

    // _near: within Edge pixels of a gone one (a square neighbourhood), in two passes.
    private void Dilate(int w, int h)
    {
        int r = Edge;
        // Across: _near holds whether a gone pixel lies within r in the row.
        for (int y = 0; y < h; y++)
        {
            int row = y * w, last = int.MinValue / 2;
            for (int x = 0; x < w; x++) { if (_gone[row + x] != 0) last = x; _near[row + x] = (byte)(x - last <= r ? 1 : 0); }
            last = int.MaxValue / 2;
            for (int x = w - 1; x >= 0; x--) { if (_gone[row + x] != 0) last = x; if (last - x <= r) _near[row + x] = 1; }
        }
        // Down: a column pass over that, into _gone's spare bit.
        for (int x = 0; x < w; x++)
        {
            int last = int.MinValue / 2;
            for (int y = 0; y < h; y++) { int i = y * w + x; if ((_near[i] & 1) != 0) last = y; if (y - last <= r) _gone[i] |= 2; }
            last = int.MaxValue / 2;
            for (int y = h - 1; y >= 0; y--) { int i = y * w + x; if ((_near[i] & 1) != 0) last = y; if (last - y <= r) _gone[i] |= 2; }
        }
        for (int i = 0; i < _gone.Length; i++) { _near[i] = (byte)(_gone[i] >> 1); _gone[i] &= 1; }
    }

    private static uint Premultiply(uint c)
    {
        uint a = c >> 24;
        uint r = (c >> 16 & 0xFF) * a / 255, g = (c >> 8 & 0xFF) * a / 255, b = (c & 0xFF) * a / 255;
        return a << 24 | r << 16 | g << 8 | b;
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
