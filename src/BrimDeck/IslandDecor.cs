using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;

namespace BrimDeck;

// A theme's motifs drawn into the island's surface, under its content (PanelDecor): a sea below a horizon, the caustics a sea
// throws, a palm frond's shadow in the open panel's corner, HUD cuts with ticks in the corners, and the two marks along the open
// panel's bottom edge (a bar of the signal color at the left, a strip of hatching at the right). Nothing here moves: the picture is
// still, so it costs nothing while the island is idle.
// The island animates its size every frame while it opens and closes, so the picture is made once, the first time it is
// drawn (the caustics' tile is shared by every island of a color), and a new size only moves its parts: the rectangles, the
// transforms and the fade are changed in place, nothing is allocated.
internal sealed class IslandDecor
{
    // The open panel shows the sea and the palm; on the small collapsed island the horizon would cross the clock like a
    // strikethrough, so it keeps the plain surface. Between the two heights they fade in, so an opening island grows into
    // them rather than having them appear on one frame.
    private const double SeaMinHeight = 90, SeaFullHeight = 140, HudMinHeight = 40, HudFullHeight = 60;
    // The bottom marks: both are MarkBand tall and lie on the bottom edge, clear of the corners' cuts. Like the sea they belong to the
    // open panel and fade in between the two heights, so the collapsed island's clock is never crossed.
    internal const double MarkBand = 5, HatchWidth = 72, TabWidth = 28, MarksMinHeight = 56, MarksFullHeight = 80;
    private static readonly Dictionary<Color, Drawing> HatchStrips = [];
    // The caustics' tile, in DIP: lines a few DIP wide, far enough apart to read as light, not as a pattern.
    private const double Tile = 150;
    // Tiles kept for the colors last used; a theme file being edited through many colors does not keep them all.
    private const int TilesKept = 8;
    private static readonly Dictionary<Color, Brush> CausticTiles = [];
    private readonly PanelDecor _decor;
    private readonly Brush? _tab;
    // The frame: its outline (the cut outline's, or the curved one, a few DIP in), its pen and the clip that leaves the top edge out.
    internal const double FrameInset = 4;
    private readonly double _cut;
    private readonly Pen? _framePen;
    private (Size Size, CornerRadius Corners, double Shoulder)? _frameFor;
    private Geometry? _frame;
    private Geometry? _frameClip;
    private Size? _for;
    // The picture and the parts a size moves.
    private DrawingGroup? _scene;
    private RectangleGeometry? _water, _glint, _light, _shadeClip;
    private GuidelineSet? _guides;
    private TranslateTransform? _lightShift;
    private MatrixTransform? _shadePlace;
    // The HUD marks follow the corners' shapes, so they are made again when the size or the corners change (only a theme with them).
    private Drawing? _hud;
    private (Size Size, CornerRadius Corners, double Shoulder)? _hudFor;

    public IslandDecor(PanelDecor decor, double cut = 0)
    {
        _decor = decor; _cut = cut;
        if (decor.Tab is { } tab) _tab = Frozen(new SolidColorBrush(ToColor(tab)));
        if (decor.Frame is { } frame) _framePen = Frozen(new Pen(Frozen(new SolidColorBrush(ToColor(frame))), 1) { LineJoin = PenLineJoin.Miter });
    }

    public static IslandDecor? For(PanelPalette palette) => palette.Effects.Decor is { } decor ? new IslandDecor(decor, palette.Effects.Cut) : null;

    public void Draw(DrawingContext dc, Size size, CornerRadius corners, double shoulder, Geometry outline)
    {
        if (size.Width < 2) return;
        bool scene = size.Height > SeaMinHeight && (_decor.Horizon is not null || _decor.Caustics is not null || _decor.Palm is not null);
        bool hud = _decor.Hud is not null && size.Height >= HudMinHeight;
        bool plate = (_decor.Hatch is not null || _decor.Tab is not null || _decor.Frame is not null) && size.Height > MarksMinHeight;
        if (!scene && !hud && !plate) return;
        dc.PushClip(outline);
        if (scene)
        {
            _scene ??= Build();
            if (_for != size) { Place(size); _for = size; }
            if (_scene.Children.Count > 0) dc.DrawDrawing(_scene);
        }
        if (hud && _decor.Hud is { } marks)
        {
            if (_hudFor != (size, corners, shoulder) || _hud is null)
            {
                // The marks fade in too, so they do not appear on one frame as the island grows past the collapsed height.
                var color = ToColor(marks.Color);
                color.A = (byte)Math.Round(color.A * Math.Min(1, (size.Height - HudMinHeight) / (HudFullHeight - HudMinHeight)));
                _hud = Hud(color, marks.Chamfer, size.Width, size.Height, corners, Math.Clamp(shoulder, 0, Math.Min(size.Width / 4, size.Height / 2)));
                _hudFor = (size, corners, shoulder);
            }
            dc.DrawDrawing(_hud);
        }
        if (plate) Marks(dc, size, corners, shoulder);
        dc.Pop();
    }

    // The tab at the bottom edge's left end and the hatching at its right end, each starting beyond its corner's radius.
    private void Marks(DrawingContext dc, Size size, CornerRadius corners, double shoulder)
    {
        double fade = Math.Min(1, (size.Height - MarksMinHeight) / (MarksFullHeight - MarksMinHeight));
        if (_framePen is not null)
        {
            if (_frameFor != (size, corners, shoulder))
            {
                _frame = _cut > 0 ? IslandBorder.BuildCut(size.Width, size.Height, shoulder, corners, FrameInset, _cut) : IslandBorder.BuildOutline(size.Width, size.Height, shoulder, corners, FrameInset);
                _frameClip = Frozen(new RectangleGeometry(new Rect(0, FrameInset + 1, size.Width, Math.Max(0, size.Height - FrameInset - 1))));
                _frameFor = (size, corners, shoulder);
            }
            if (fade < 1) dc.PushOpacity(fade);
            dc.PushClip(_frameClip);
            dc.DrawGeometry(null, _framePen, _frame);
            dc.Pop();
            if (fade < 1) dc.Pop();
        }
        double y = size.Height - MarkBand, tabX = shoulder + corners.BottomLeft + 4, hatchX = size.Width - shoulder - corners.BottomRight - 4 - HatchWidth;
        bool tab = _tab is not null && tabX + TabWidth < size.Width, hatch = _decor.Hatch is not null && hatchX > (tab ? tabX + TabWidth + 8 : 0);
        if (!tab && !hatch) return;
        if (fade < 1) dc.PushOpacity(fade);
        if (tab) dc.DrawRectangle(_tab, null, new Rect(tabX, y, TabWidth, MarkBand));
        if (hatch)
        {
            dc.PushTransform(new TranslateTransform(hatchX, y));
            dc.DrawDrawing(HatchStrip(ToColor(_decor.Hatch!.Value)));
            dc.Pop();
        }
        if (fade < 1) dc.Pop();
    }

    // A strip of hatching: 45-degree stripes 2 DIP wide every 5, clipped to the strip. It is the same for every island of a color.
    internal static Drawing HatchStrip(Color color)
    {
        if (HatchStrips.TryGetValue(color, out var known)) return known;
        if (HatchStrips.Count >= TilesKept) HatchStrips.Clear();
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
            for (double x = -MarkBand; x < HatchWidth; x += 5)
            {
                g.BeginFigure(new Point(x, MarkBand), true, true);
                g.LineTo(new Point(x + 2, MarkBand), true, false); g.LineTo(new Point(x + 2 + MarkBand, 0), true, false); g.LineTo(new Point(x + MarkBand, 0), true, false);
            }
        geometry.Freeze();
        var strip = new DrawingGroup { ClipGeometry = Frozen(new RectangleGeometry(new Rect(0, 0, HatchWidth, MarkBand))), Children = { new GeometryDrawing(Frozen(new SolidColorBrush(color)), null, geometry) } };
        strip.Freeze();
        return HatchStrips[color] = strip;
    }

    private DrawingGroup Build()
    {
        var scene = new DrawingGroup();
        if (_decor.Horizon is { } horizon)
        {
            // The sea is lightest at the horizon, where the sky's light lies on it, and deepens toward the bottom. Its top fades
            // in, so where the horizon falls between a row's text and its bar it reads as a haze, not a cut through the row.
            Color shallow = ToColor(horizon.Sea), deep = ToColor(horizon.Deep);
            var water = Frozen(new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, shallow.R, shallow.G, shallow.B), 0), new(shallow, .08), new(deep, 1)
            }, new Point(0, 0), new Point(0, 1)));
            scene.Children.Add(new GeometryDrawing(water, null, _water = new RectangleGeometry()));
            if (horizon.Line is { } line)
            {
                // A line of light along the horizon, brightest in the middle, as the sun lays on the water far off; on whole
                // device pixels, so it stays one sharp line at any scale.
                var light = ToColor(line);
                var glint = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromArgb(0, light.R, light.G, light.B), 0), new(light, .5), new(Color.FromArgb(0, light.R, light.G, light.B), 1)
                }, new Point(0, 0), new Point(1, 0)));
                scene.GuidelineSet = _guides = new GuidelineSet(null, [0, 0]);
                scene.Children.Add(new GeometryDrawing(glint, null, _glint = new RectangleGeometry()));
            }
        }
        if (_decor.Caustics is { } caustics)
        {
            // Without a horizon the caustics cover the whole surface. The tile is anchored at the island's centre, which stays
            // put while the island widens around it, so the light does not slide as the island opens.
            scene.Children.Add(new DrawingGroup
            {
                Transform = _lightShift = new TranslateTransform(),
                Children = { new GeometryDrawing(Caustics(ToColor(caustics)), null, _light = new RectangleGeometry()) }
            });
        }
        if (_decor.Palm is { } palm)
        {
            // It is shade on the sand, so it stops at the horizon (and its contrast is checked over the sky alone).
            var shade = new DrawingGroup { Transform = _shadePlace = new MatrixTransform(), Children = { Palm(ToColor(palm)) } };
            scene.Children.Add(_decor.Horizon is null ? shade : new DrawingGroup { ClipGeometry = _shadeClip = new RectangleGeometry(), Children = { shade } });
        }
        return scene;
    }

    private void Place(Size size)
    {
        double w = size.Width, h = size.Height;
        _scene!.Opacity = Math.Min(1, (h - SeaMinHeight) / (SeaFullHeight - SeaMinHeight));
        double seaTop = _decor.Horizon is { } horizon ? Math.Round(h * horizon.At) : 0;
        if (_water is not null) _water.Rect = new Rect(0, seaTop, w, h - seaTop);
        if (_glint is not null)
        {
            _glint.Rect = new Rect(0, seaTop - .5, w, 1);
            _guides!.GuidelinesY[0] = seaTop - .5; _guides.GuidelinesY[1] = seaTop + .5;
        }
        if (_light is not null)
        {
            double dx = w / 2 % Tile;
            _lightShift!.X = dx;
            _light.Rect = new Rect(-dx, seaTop, w, h - seaTop);
        }
        if (_shadePlace is not null)
        {
            // At most 120 DIP, set well beyond the top-right corner, so it stays in the corner and clear of the clock column
            // below it; the settings button in the corner sits over its faint tips.
            double frond = Math.Min(120, h * .5);
            _shadePlace.Matrix = new Matrix(frond, 0, 0, frond, w - frond * .72, -frond * .2);
            if (_shadeClip is not null) _shadeClip.Rect = new Rect(0, 0, w, seaTop);
        }
    }

    // A tile of caustic light: two sine fields, each warped by the other, light where either crosses zero, so the lines meet
    // in the loose net sunlight draws on a sandy bottom. The tile wraps (whole periods), so it repeats without a seam.
    private static Brush Caustics(Color color)
    {
        if (CausticTiles.TryGetValue(color, out var cached)) return cached;
        if (CausticTiles.Count >= TilesKept) CausticTiles.Clear();
        // 256 pixels for 150 DIP: sharp up to about 170 % scale.
        const int size = 256;
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
