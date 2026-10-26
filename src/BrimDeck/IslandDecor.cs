using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;

namespace BrimDeck;

// A theme's motifs drawn into the island's surface, under its content (PanelDecor): a sea below a horizon, the caustics a sea
// throws, a palm frond's shadow in the open panel's corner, HUD cuts and ticks in the corners. Nothing here moves: a theme's
// motion (AmbientGlow) may drift them later, but the picture itself is still.
// The island animates its size every frame while it opens and closes, so what does not depend on the size is made once: the
// sea's and the line's brushes, the palm's shape in unit coordinates and the caustics' tile (shared by every island of a
// color, and made the first time a sea is drawn). A size only places them, in a small group kept until the size changes.
internal sealed class IslandDecor
{
    // The open panel shows the sea and the palm; on the small collapsed island the horizon would cross the clock like a
    // strikethrough, so it keeps the plain surface. Between the two heights they fade in, so an opening island grows into
    // them rather than having them appear on one frame.
    private const double SeaMinHeight = 90, SeaFullHeight = 140, HudMinHeight = 40, HudFullHeight = 60;
    // The caustics' tile, in DIP: lines a few DIP wide, far enough apart to read as light, not as a pattern.
    private const double Tile = 150;
    private static readonly Dictionary<Color, Brush> CausticTiles = [];
    private readonly PanelDecor _decor;
    private readonly Brush? _water, _glint;
    private readonly Drawing? _palm;
    private (Size Size, CornerRadius Corners, double Shoulder)? _for;
    private DrawingGroup? _drawing;

    public IslandDecor(PanelDecor decor)
    {
        _decor = decor;
        if (decor.Horizon is { } horizon)
        {
            // The sea is lightest at the horizon, where the sky's light lies on it, and deepens toward the bottom. Its top fades
            // in, so where the horizon falls between a row's text and its bar it reads as a haze, not a cut through the row.
            Color shallow = ToColor(horizon.Sea), deep = ToColor(horizon.Deep);
            _water = Frozen(new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, shallow.R, shallow.G, shallow.B), 0), new(shallow, .08), new(deep, 1)
            }, new Point(0, 0), new Point(0, 1)));
            if (horizon.Line is { } line)
            {
                // A line of light along the horizon, brightest in the middle, as the sun lays on the water far off.
                var light = ToColor(line);
                _glint = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromArgb(0, light.R, light.G, light.B), 0), new(light, .5), new(Color.FromArgb(0, light.R, light.G, light.B), 1)
                }, new Point(0, 0), new Point(1, 0)));
            }
        }
        if (decor.Palm is { } palm) _palm = Palm(ToColor(palm));
    }

    public static IslandDecor? For(PanelPalette palette) => palette.Effects.Decor is { } decor ? new IslandDecor(decor) : null;

    public void Draw(DrawingContext dc, Size size, CornerRadius corners, double shoulder, Geometry outline)
    {
        if (size.Width < 2 || size.Height < 2) return;
        if (_for != (size, corners, shoulder) || _drawing is null) { _drawing = Build(size, corners, shoulder); _for = (size, corners, shoulder); }
        if (_drawing.Children.Count == 0) return;
        dc.PushClip(outline);
        dc.DrawDrawing(_drawing);
        dc.Pop();
    }

    private DrawingGroup Build(Size size, CornerRadius corners, double shoulder)
    {
        var group = new DrawingGroup();
        double w = size.Width, h = size.Height;
        if (h >= SeaMinHeight)
        {
            var scene = new DrawingGroup { Opacity = Math.Min(1, (h - SeaMinHeight) / (SeaFullHeight - SeaMinHeight)) };
            double seaTop = 0;
            if (_decor.Horizon is not null)
            {
                seaTop = Math.Round(h * _decor.Horizon.At);
                scene.Children.Add(new GeometryDrawing(_water, null, new RectangleGeometry(new Rect(0, seaTop, w, h - seaTop))));
                if (_glint is not null)
                {
                    // On whole device pixels, so the line stays one sharp line at any scale.
                    scene.GuidelineSet = new GuidelineSet(null, [seaTop - .5, seaTop + .5]);
                    scene.Children.Add(new GeometryDrawing(_glint, null, new RectangleGeometry(new Rect(0, seaTop - .5, w, 1))));
                }
            }
            if (_decor.Caustics is { } caustics)
            {
                // Without a horizon the caustics cover the whole surface. The tile is anchored at the island's centre, which
                // stays put while the island widens around it, so the light does not slide as the island opens.
                double dx = w / 2 % Tile;
                scene.Children.Add(new DrawingGroup
                {
                    Transform = new TranslateTransform(dx, 0),
                    Children = { new GeometryDrawing(Caustics(ToColor(caustics)), null, new RectangleGeometry(new Rect(-dx, seaTop, w, h - seaTop))) }
                });
            }
            if (_palm is not null)
            {
                // At most 120 DIP, set well beyond the top-right corner, so it stays in the corner, clear of the clock column
                // below it and of the settings button in the corner itself.
                // It is shade on the sand, so it stops at the horizon (and its contrast is checked over the sky alone).
                double frond = Math.Min(120, h * .5);
                var shade = new DrawingGroup { Transform = new MatrixTransform(frond, 0, 0, frond, w - frond * .72, -frond * .2), Children = { _palm } };
                scene.Children.Add(_decor.Horizon is null ? shade : new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, w, seaTop)), Children = { shade } });
            }
            if (scene.Children.Count > 0) group.Children.Add(scene);
        }
        if (_decor.Hud is { } hud && h >= HudMinHeight)
        {
            // The marks fade in too, so they do not appear on one frame as the island grows past the collapsed height.
            var color = ToColor(hud.Color);
            color.A = (byte)Math.Round(color.A * Math.Min(1, (h - HudMinHeight) / (HudFullHeight - HudMinHeight)));
            group.Children.Add(Hud(color, hud.Chamfer, w, h, corners, Math.Clamp(shoulder, 0, Math.Min(w / 4, h / 2))));
        }
        group.Freeze();
        return group;
    }

    // A tile of caustic light: two sine fields, each warped by the other, light where either crosses zero, so the lines meet
    // in the loose net sunlight draws on a sandy bottom. The tile wraps (whole periods), so it repeats without a seam.
    private static Brush Caustics(Color color)
    {
        if (CausticTiles.TryGetValue(color, out var cached)) return cached;
        const int size = 192;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double u = 2 * Math.PI * x / size, v = 2 * Math.PI * y / size;
                double a = Math.Sin(2 * u + 1.6 * Math.Sin(v + .7 * Math.Sin(2 * u)));
                double b = Math.Sin(3 * v + 1.3 * Math.Sin(2 * u - .9));
                double c = Math.Sin(u + 2 * v + 1.1 * Math.Sin(3 * u));
                // A thin bright ridge where each field is near zero, softened, then the brightest of the three.
                static double Ridge(double s) { double d = 1 - Math.Min(1, Math.Abs(s) / .3); return d * d * (3 - 2 * d); }
                double light = Math.Max(Ridge(a), Math.Max(Ridge(b) * .85, Ridge(c) * .7));
                byte alpha = (byte)Math.Round(color.A * light);
                int i = (y * size + x) * 4;
                // Premultiplied BGRA.
                pixels[i] = (byte)(color.B * alpha / 255); pixels[i + 1] = (byte)(color.G * alpha / 255); pixels[i + 2] = (byte)(color.R * alpha / 255); pixels[i + 3] = alpha;
            }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        // About 150 DIP across: lines a few DIP wide, wide apart enough to read as light, not as a pattern.
        var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, Tile, Tile), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        brush.Freeze();
        return CausticTiles[color] = brush;
    }

    // A palm frond's shadow: a curved stem from beyond the top-right corner, with long narrow leaflets falling from it on both
    // sides, as the shade of a palm overhead falls on the sand. In unit coordinates; Build sizes and places it.
    private static Drawing Palm(Color color)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            // The stem: a quadratic curve from beyond the corner down and to the left, in units of the frond's size.
            Point Stem(double t) => new(1.05 - .95 * t, -.05 + .5 * t * t + .12 * t);
            Point Tangent(double t) { var a = Stem(Math.Max(0, t - .01)); var b = Stem(Math.Min(1, t + .01)); var d = b - a; d.Normalize(); return new Point(d.X, d.Y); }
            for (int i = 0; i < 15; i++)
            {
                double t = .08 + i * .062;
                var at = Stem(t); var dir = Tangent(t);
                // Leaflets lengthen toward the middle of the frond and shorten at its tip; they fall forward along the stem.
                double length = .2 + .26 * Math.Sin(Math.PI * Math.Min(1, t * 1.05));
                foreach (int side in new[] { -1, 1 })
                {
                    var normal = new Vector(-dir.Y * side, dir.X * side);
                    var along = new Vector(dir.X, dir.Y);
                    var tip = at + (normal * .85 + along * .55) * length;
                    var width = .028 * (1 - .5 * t);
                    var bend = at + (normal * .45 + along * .1) * length + new Vector(0, .03);
                    g.BeginFigure(at + along * width, true, true);
                    g.QuadraticBezierTo(bend + along * width, tip, true, true);
                    g.QuadraticBezierTo(bend - along * width, at - along * width, true, true);
                }
            }
            // The stem itself, a thin taper.
            g.BeginFigure(Stem(0) + new Vector(0, -.012), true, true);
            for (int i = 1; i <= 10; i++) g.LineTo(Stem(i / 10.0) + new Vector(0, -.012 * (1 - i / 10.0)), true, true);
            for (int i = 10; i >= 0; i--) g.LineTo(Stem(i / 10.0) + new Vector(0, .012 * (1 - i / 10.0)), true, true);
        }
        geometry.Freeze();
        var drawing = new GeometryDrawing(Frozen(new SolidColorBrush(color)), null, geometry);
        drawing.Freeze();
        return drawing;
    }

    // HUD marks: a 45-degree cut across the top-right (or bottom-right) and bottom-left corners, each led in by a short straight
    // run along the edge, and a small hexagon beside each cut, as on the heads-up displays the theme borrows from. One DIP
    // lines. A notch's body stands in from its shoulders, so the bottom corners do too.
    private static Drawing Hud(Color color, double chamfer, double w, double h, CornerRadius corners, double shoulder)
    {
        var geometry = new StreamGeometry();
        double inset = 3.5, lead = 14;
        using (var g = geometry.Open())
        {
            void Corner(Point corner, Vector across, Vector down, double radius)
            {
                // Inside a rounded corner the cut moves in with the curve (a 45-degree line touches a radius r arc r(1 - 1/sqrt 2)
                // from the corner), so it is never clipped away.
                double c = Math.Max(chamfer, 2) + radius * .3;
                // The cut runs from c along the top edge to c down the side, inset from the edge; the leads extend it.
                var start = corner - across * (c + lead) + down * inset;
                var cutTop = corner - across * (c + inset) + down * inset;
                var cutSide = corner - across * inset + down * (c + inset);
                var end = corner - across * inset + down * (c + inset + lead);
                g.BeginFigure(start, false, false);
                g.LineTo(cutTop, true, true); g.LineTo(cutSide, true, true); g.LineTo(end, true, true);
                // One hexagonal tick just inside the cut, large enough to read as a hexagon.
                Hexagon(g, corner - across * (c + inset + 14) + down * (inset + 9), 6);
            }
            // Only corners that are drawn round; a square corner (the notch's top) is the screen's edge, so an island hanging from
            // it takes the second cut at its bottom-right instead.
            if (corners.TopRight > 2) Corner(new Point(w, 0), new Vector(1, 0), new Vector(0, 1), corners.TopRight);
            else if (corners.BottomRight > 2) Corner(new Point(w - shoulder, h), new Vector(1, 0), new Vector(0, -1), corners.BottomRight);
            if (corners.BottomLeft > 2) Corner(new Point(shoulder, h), new Vector(-1, 0), new Vector(0, -1), corners.BottomLeft);
        }
        geometry.Freeze();
        var pen = Frozen(new Pen(new SolidColorBrush(color), 1) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Miter });
        return new GeometryDrawing(null, pen, geometry);
    }

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    private static void Hexagon(StreamGeometryContext g, Point centre, double radius)
    {
        g.BeginFigure(centre + new Vector(radius, 0), false, true);
        for (int i = 1; i < 6; i++)
        {
            double a = Math.PI / 3 * i;
            g.LineTo(centre + new Vector(radius * Math.Cos(a), radius * Math.Sin(a)), true, true);
        }
    }
}
