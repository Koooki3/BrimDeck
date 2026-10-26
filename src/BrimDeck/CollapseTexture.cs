using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;

namespace BrimDeck;

// A theme's collapse texture (CollapseFx) on the content that leaves as the panel or a notice closes. It lies in the content's
// own place inside the island (or the notice's surface), so the outline's clip shrinks it as the outline morphs into the
// notch, capsule or line on the panel's own move; the real content is hidden as it starts and this draws a snapshot of it
// instead. Every frame only rearranges that snapshot: bands clipped and moved (glitch), one of a few small masks made as it
// starts (foam), one affine transform per mesh cell (genie); the composition engine does the drawing. It takes no input.
// Stop ends it at once: the panel opening again, a new notice, the window hiding. Frames come from the panel's shared clock
// (Motion, on the display's frames), only while it plays.
internal sealed class CollapseTexture : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    // The genie: the outline's clip on the container (antialiased), the mesh's pieces aliased inside it, so that neighbouring
    // cells meet without seams, as the original's aliased quads under a per-primitive mask.
    private readonly ContainerVisual _outline = new();
    private readonly DrawingVisual _mesh = new();
    private readonly CollapsePlayback _playback = new();
    private Action? _ended;
    private BitmapSource? _snapshot;
    private Rect _whole, _island;
    private double _scale = 1;
    private bool _hooked, _jolt;
    private ImageBrush[] _masks = [];
    private BitmapSource[] _foam = [];
    private Pen _neonA = new(), _neonB = new();
    private readonly (double X, double Y)[] _points = new (double, double)[(GenieCollapse.Tiles + 1) * (GenieCollapse.Tiles + 1)];

    // The clock frames are drawn at; a preview steps its own.
    internal Func<double> Clock { get; set; } = () => Eased.Now;
    // How long the last frame took to arrange, and how long starting took, in milliseconds (FxPreview reports them).
    internal double LastFrameMs { get; private set; }
    internal double StartMs { get; private set; }

    public CollapseTexture()
    {
        IsHitTestVisible = false; Focusable = false; Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Top;
        AddVisualChild(_visual);
        AddVisualChild(_outline);
        _outline.Children.Add(_mesh);
        RenderOptions.SetEdgeMode(_mesh, EdgeMode.Aliased);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _visual : _outline;

    public bool Playing => _playback.Active;
    public CollapseStyle PlayingStyle => _playback.Style;

    // Whether the theme's texture plays now: it asks for one and motion is allowed.
    public static bool CanPlay => CollapseFx.Plays(PanelTheme.Motion.Collapse, MotionEnvironment.Conditions());

    // The element as it shows now, in device pixels at its own scale; null when it has no size or is not on screen.
    public static BitmapSource? Capture(FrameworkElement element)
    {
        if (PresentationSource.FromVisual(element) is null) return null;
        double scale = VisualTreeHelper.GetDpi(element).DpiScaleX;
        var size = element.RenderSize;
        int w = (int)Math.Ceiling(size.Width * scale), h = (int)Math.Ceiling(size.Height * scale);
        if (w < 2 || h < 2 || w > 8192 || h > 8192) return null;
        // Through a brush, so that the element's own place in its parent does not move it within the picture.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // The theme's texture as a closing starts: Width and Height become the snapshot's, so the caller places this where the
    // content was. seconds: how long the texture plays. island: where the closed island will be, in this element's DIP (the
    // genie's target). The glitch's jolt is taken from the shared flashes here. ended runs once it is over, never after Stop.
    public void Begin(BitmapSource snapshot, double seconds, Rect island, Action? ended = null)
    {
        var motion = PanelTheme.Motion;
        var style = motion.Collapse;
        var palette = PanelTheme.Current;
        Play(style, snapshot, seconds, island, CollapseFx.Jolt(style, FlashBudget.Shared, Clock()),
            PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, motion.Color)), PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, motion.Secondary)), ended);
    }

    public void Play(CollapseStyle style, BitmapSource snapshot, double seconds, Rect island, bool jolt, Color neonA, Color neonB, Action? ended)
    {
        Stop();
        if (style == CollapseStyle.Fade || !(seconds > 0)) { ended?.Invoke(); return; }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        _scale = Math.Max(.5, snapshot.DpiX / 96);
        _snapshot = snapshot; _ended = ended; _island = island; _jolt = jolt;
        _whole = new Rect(0, 0, snapshot.PixelWidth / _scale, snapshot.PixelHeight / _scale);
        Width = _whole.Width; Height = _whole.Height;
        Visibility = Visibility.Visible;
        if (style == CollapseStyle.Glitch)
        {
            _neonA = Line(neonA); _neonB = Line(neonB);
        }
        else if (style == CollapseStyle.Foam) MakeMasks();
        _playback.Start(style, Clock(), seconds);
        Frame(Clock());
        StartMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (_hooked) return;
        _hooked = true;
        Motion.Start(this, _ => { if (!_hooked) return false; Frame(Clock()); return _hooked; });
    }

    private Pen Line(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1 / _scale);
        pen.Freeze();
        return pen;
    }

    // The foam's masks and foam lines, one per level, each a small bitmap over the whole content that the composition engine
    // stretches (smoothly) to its size.
    private void MakeMasks()
    {
        var field = new FoamField(_whole.Width, _whole.Height);
        int count = field.Columns * field.Rows;
        var shown = new byte[count]; var foam = new byte[count];
        var shownPixels = new uint[count]; var foamPixels = new uint[count];
        _masks = new ImageBrush[FoamField.Levels]; _foam = new BitmapSource[FoamField.Levels];
        for (int level = 0; level < FoamField.Levels; level++)
        {
            field.Mask(level, shown, foam);
            for (int i = 0; i < count; i++)
            {
                uint a = shown[i], f = (uint)(foam[i] * .9);
                shownPixels[i] = a << 24 | a << 16 | a << 8 | a;
                foamPixels[i] = f << 24 | f << 16 | f << 8 | f;
            }
            var mask = BitmapSource.Create(field.Columns, field.Rows, 96, 96, PixelFormats.Pbgra32, null, shownPixels, field.Columns * 4);
            mask.Freeze();
            var brush = new ImageBrush(mask) { Stretch = Stretch.Fill };
            brush.Freeze();
            _masks[level] = brush;
            var line = BitmapSource.Create(field.Columns, field.Rows, 96, 96, PixelFormats.Pbgra32, null, foamPixels, field.Columns * 4);
            line.Freeze();
            _foam[level] = line;
        }
    }

    // Ends the texture at once: nothing more is drawn, and ended does not run.
    public void Stop()
    {
        _ended = null;
        _playback.Stop();
        _hooked = false;
        _snapshot = null; _masks = []; _foam = [];
        using (_visual.RenderOpen()) { }
        using (_mesh.RenderOpen()) { }
        _outline.Clip = null;
        Visibility = Visibility.Collapsed;
    }

    // One frame at a time on the playback's clock; past the end it clears and lets the caller know.
    internal void Frame(double now)
    {
        if (!_playback.Active) return;
        if (_playback.Done(now))
        {
            var ended = _ended;
            Stop();
            ended?.Invoke();
            return;
        }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        double p = _playback.Progress(now);
        switch (_playback.Style)
        {
            case CollapseStyle.Glitch: DrawGlitch(p); break;
            case CollapseStyle.Foam: DrawFoam(p); break;
            case CollapseStyle.Genie: DrawGenie(p); break;
        }
        LastFrameMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    // Bands of the snapshot, each clipped to its own rows and moved sideways; on whole device pixels, so no seam shows.
    private void DrawGlitch(double p)
    {
        var snapshot = _snapshot!;
        double w = _whole.Width, h = _whole.Height;
        int bands = CollapseFx.GlitchBands(h);
        bool jolt = _jolt && GlitchTexture.JoltOn(p);
        using var dc = _visual.RenderOpen();
        for (int i = 0; i < bands; i++)
        {
            double alpha = GlitchTexture.Alpha(i, p);
            if (alpha <= .002) continue;
            double top = Px((double)i / bands * h), bottom = Px((double)(i + 1) / bands * h);
            double dx = Px(GlitchTexture.Offset(i, p, jolt) * w);
            dc.PushClip(new RectangleGeometry(new Rect(dx, top, w, bottom - top)));
            dc.PushOpacity(alpha);
            dc.DrawImage(snapshot, new Rect(dx, 0, w, h));
            if (jolt && CollapseFx.GlitchHash(i, 0) >= GlitchTexture.Threshold)
            {
                dc.DrawLine(_neonA, new Point(dx, top + .5 / _scale), new Point(dx + w, top + .5 / _scale));
                dc.DrawLine(_neonB, new Point(dx, bottom - .5 / _scale), new Point(dx + w, bottom - .5 / _scale));
            }
            dc.Pop(); dc.Pop();
        }
    }

    private double Px(double dip) => Math.Round(dip * _scale) / _scale;

    // The snapshot under the level's mask, and the level's foam over it.
    private void DrawFoam(double p)
    {
        int level = FoamField.LevelAt(p);
        using var dc = _visual.RenderOpen();
        dc.PushOpacityMask(_masks[level]);
        dc.DrawImage(_snapshot!, _whole);
        dc.Pop();
        dc.DrawImage(_foam[level], _whole);
    }

    // The mesh at this progress: each cell is the snapshot's own cell mapped by the affine transform its three corners give,
    // clipped to the cell's four warped corners pushed a fraction of a pixel apart (so neighbours overlap), all inside the
    // outline's rounded box (12 DIP corners, Bezier K 0.5523 as the original's; WPF's rounded rectangle is the same curve).
    private void DrawGenie(double progress)
    {
        var snapshot = _snapshot!;
        var source = _whole;
        var bounds = GenieCollapse.Mesh(progress, new GenieRect(source.X, source.Y, source.Width, source.Height),
            new GenieRect(_island.X, _island.Y, _island.Width, _island.Height), _points);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            using (_mesh.RenderOpen()) { }
            return;
        }
        double r = Math.Min(GenieCollapse.Corner, Math.Min(bounds.Width, bounds.Height) / 2);
        var clip = new RectangleGeometry(new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height), r, r);
        clip.Freeze();
        _outline.Clip = clip;
        // The texture fades out over the last fifth, so the content has gone as the outline lands.
        double fade = 1 - CollapseFx.SmoothStep(.8, 1, progress);
        int n = GenieCollapse.Tiles + 1;
        double cellW = source.Width / GenieCollapse.Tiles, cellH = source.Height / GenieCollapse.Tiles;
        using var dc = _mesh.RenderOpen();
        dc.PushOpacity(fade);
        for (int gy = 0; gy < GenieCollapse.Tiles; gy++)
            for (int gx = 0; gx < GenieCollapse.Tiles; gx++)
            {
                var p1 = _points[gy * n + gx]; var p2 = _points[gy * n + gx + 1];
                var p3 = _points[(gy + 1) * n + gx]; var p4 = _points[(gy + 1) * n + gx + 1];
                double cx = (p1.X + p2.X + p3.X + p4.X) / 4, cy = (p1.Y + p2.Y + p3.Y + p4.Y) / 4;
                double quadW = Math.Max(Math.Sqrt(Sq(p2.X - p1.X) + Sq(p2.Y - p1.Y)), Math.Sqrt(Sq(p4.X - p3.X) + Sq(p4.Y - p3.Y)));
                double quadH = Math.Max(Math.Sqrt(Sq(p3.X - p1.X) + Sq(p3.Y - p1.Y)), Math.Sqrt(Sq(p4.X - p2.X) + Sq(p4.Y - p2.Y)));
                // The original pushes 0.2 to 0.4 px; half a pixel to three quarters here (see Bloat), in DIP.
                double bloat = Math.Max(.5, Math.Min(.75, Math.Min(quadW, quadH) * _scale * .05)) / _scale;
                var quad = new StreamGeometry();
                using (var path = quad.Open())
                {
                    path.BeginFigure(Bloat(p1, cx, cy, bloat), true, true);
                    path.LineTo(Bloat(p2, cx, cy, bloat), false, false);
                    path.LineTo(Bloat(p4, cx, cy, bloat), false, false);
                    path.LineTo(Bloat(p3, cx, cy, bloat), false, false);
                }
                quad.Freeze();
                double sx0 = source.X + gx * cellW, sy0 = source.Y + gy * cellH;
                double m11 = (p2.X - p1.X) / cellW, m12 = (p2.Y - p1.Y) / cellW, m21 = (p3.X - p1.X) / cellH, m22 = (p3.Y - p1.Y) / cellH;
                var matrix = new Matrix(m11, m12, m21, m22, p1.X - sx0 * m11 - sy0 * m21, p1.Y - sx0 * m12 - sy0 * m22);
                if (!matrix.HasInverse) continue;
                dc.PushClip(quad);
                dc.PushTransform(new MatrixTransform(matrix));
                dc.DrawImage(snapshot, source);
                dc.Pop();
                dc.Pop();
            }
        dc.Pop();
    }

    private static double Sq(double v) => v * v;
    // The original pushes a corner away from the cell's centre along the line to it; a cell far wider than tall then barely
    // grows in height, and WPF's aliased clip left one-pixel seams between rows. Pushed along each axis instead.
    private static Point Bloat((double X, double Y) p, double cx, double cy, double amount)
        => new(p.X + Math.Sign(p.X - cx) * amount, p.Y + Math.Sign(p.Y - cy) * amount);
}
