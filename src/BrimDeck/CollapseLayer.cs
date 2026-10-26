using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;

namespace BrimDeck;

// Plays a theme's collapse picture (CollapseFx) over a window: the notice or the open panel is photographed as it is, the
// caller puts the real element in its final state at once (the island), and the picture leaves on top of it. glitch and foam
// are worked out pixel by pixel into one WriteableBitmap per frame; genie draws the snapshot as one affine piece per mesh cell
// inside the rounded outline. It takes no input. Stop ends it at once and draws nothing more: a new notice, the panel opening
// again, the window hiding. Frames come from the panel's shared clock (Motion), only while a picture plays.
internal sealed class CollapseLayer : FrameworkElement
{
    private readonly DrawingVisual _pixels = new();
    // The genie: the outline's clip on the container (antialiased), the mesh's pieces aliased inside it so that neighbouring
    // cells meet without seams, as the original's aliased quads under a per-primitive mask.
    private readonly ContainerVisual _outline = new();
    private readonly DrawingVisual _mesh = new();
    private readonly CollapsePlayback _playback = new();
    private Action? _ended;
    private BitmapSource? _snapshot;
    private WriteableBitmap? _canvas;
    private uint[] _frame = [];
    private GlitchCollapse? _glitch;
    private FoamCollapse? _foam;
    private Rect _source, _island;
    private double _scale = 1, _aberration;
    private bool _hooked;
    private readonly (double X, double Y)[] _points = new (double, double)[(GenieCollapse.Tiles + 1) * (GenieCollapse.Tiles + 1)];

    // The clock frames are drawn at; a preview steps its own.
    internal Func<double> Clock { get; set; } = () => Eased.Now;
    // How long the last frame took to work out, in milliseconds (FxPreview reports it).
    internal double LastFrameMs { get; private set; }

    public CollapseLayer()
    {
        IsHitTestVisible = false; Focusable = false;
        AddVisualChild(_pixels);
        AddVisualChild(_outline);
        _outline.Children.Add(_mesh);
        RenderOptions.SetEdgeMode(_mesh, EdgeMode.Aliased);
        RenderOptions.SetBitmapScalingMode(_mesh, BitmapScalingMode.Linear);
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _pixels : _outline;

    public bool Playing => _playback.Active;
    public CollapseStyle PlayingStyle => _playback.Style;

    // The element as it shows now, in device pixels at the layer's scale; null when it has no size or is not on screen.
    public BitmapSource? Capture(FrameworkElement element)
    {
        if (PresentationSource.FromVisual(element) is null || PresentationSource.FromVisual(this) is null) return null;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
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

    // The theme's collapse, as a window starts one: Prepare photographs the element only when a picture could play now (the
    // theme asks for one, motion is allowed, the second's flashes are not spent; null means the default fade), and Begin
    // spends the flash and plays it, returning false when the default fade has to run after all. The caller puts the real
    // element in its final state once Begin has returned true.
    public BitmapSource? Prepare(FrameworkElement element) => CanPlay ? Capture(element) : null;
    public static bool CanPlay => CollapseFx.Possible(PanelTheme.Motion.Collapse, MotionEnvironment.Conditions(), FlashBudget.Shared, Eased.Now);

    public bool Begin(BitmapSource? snapshot, Rect source, Rect island, Action? ended)
    {
        if (snapshot is null) return false;
        var motion = PanelTheme.Motion;
        var style = CollapseFx.Choose(motion.Collapse, MotionEnvironment.Conditions(), FlashBudget.Shared, Eased.Now);
        if (style == CollapseStyle.Fade) return false;
        Play(style, snapshot, source, island, CollapseFx.Length(style, motion.Pace, motion.CollapseMs), FoamColor(motion), motion.Aberration, ended);
        return true;
    }

    // Sea foam is white on every surface: the highlight a light theme gave it read as a dark red outline (live check IS-02).
    internal static Color FoamColor(ThemeMotion motion) => Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);

    // Plays `style` on the snapshot lying at `source` (layer DIP) into the island at `island`. foam: the foam's color.
    // aberration: the theme's red and blue split in DIP (0: none). ended runs once the picture is over, never after Stop.
    public void Play(CollapseStyle style, BitmapSource snapshot, Rect source, Rect island, double length, Color foam, double aberration, Action? ended)
    {
        Stop();
        if (style == CollapseStyle.Fade || source.IsEmpty || source.Width < 1 || source.Height < 1) { ended?.Invoke(); return; }
        _scale = Math.Max(.5, VisualTreeHelper.GetDpi(this).DpiScaleX);
        _snapshot = snapshot; _source = source; _island = island; _ended = ended; _aberration = aberration;
        if (style is CollapseStyle.Glitch or CollapseStyle.Foam)
        {
            int w = snapshot.PixelWidth, h = snapshot.PixelHeight;
            var pixels = new uint[w * h];
            snapshot.CopyPixels(pixels, w * 4, 0);
            if (style == CollapseStyle.Glitch)
            {
                _glitch = new GlitchCollapse(pixels, w, h, CollapseFx.GlitchBands(source.Height));
                w = _glitch.CanvasWidth;
            }
            else _foam = new FoamCollapse(pixels, w, h, _scale, (uint)(foam.A << 24 | foam.R << 16 | foam.G << 8 | foam.B));
            _frame = new uint[w * h];
            _canvas = new WriteableBitmap(w, h, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32, null);
        }
        _playback.Start(style, Clock(), length);
        Frame(Clock());
        if (_hooked) return;
        _hooked = true;
        Motion.Start(this, now => { if (!_hooked) return false; Frame(Clock()); return _hooked; });
    }

    // Ends the picture at once: nothing more is drawn, and ended does not run.
    public void Stop()
    {
        _ended = null;
        _playback.Stop();
        _hooked = false;
        _snapshot = null; _canvas = null; _glitch = null; _foam = null; _frame = [];
        using (_pixels.RenderOpen()) { }
        using (_mesh.RenderOpen()) { }
        _outline.Clip = null;
    }

    // One frame at a time on the playback's clock; the last one clears the layer and lets the caller finish.
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
        double aberration = CollapseFx.AberrationAt(_playback.Elapsed(now), _aberration, _scale);
        switch (_playback.Style)
        {
            case CollapseStyle.Glitch when _glitch is not null && _canvas is not null:
                _glitch.Render(p, _frame, aberration);
                ShowPixels(new Rect(_source.X - _glitch.Margin / _scale, _source.Y, _glitch.CanvasWidth / _scale, _glitch.Height / _scale));
                break;
            case CollapseStyle.Foam when _foam is not null && _canvas is not null:
                _foam.Render(p, _frame, aberration);
                ShowPixels(new Rect(_source.X, _source.Y, _foam.Width / _scale, _foam.Height / _scale));
                break;
            case CollapseStyle.Genie when _snapshot is not null:
                DrawGenie(p);
                break;
        }
        LastFrameMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void ShowPixels(Rect at)
    {
        var canvas = _canvas!;
        canvas.WritePixels(new Int32Rect(0, 0, canvas.PixelWidth, canvas.PixelHeight), _frame, canvas.PixelWidth * 4, 0);
        using var dc = _pixels.RenderOpen();
        dc.DrawImage(canvas, at);
    }

    // The mesh at this progress: each cell is the snapshot's own cell mapped by the affine transform its three corners give,
    // clipped to the cell's four warped corners pushed a fraction of a pixel apart (so neighbours overlap), all inside the
    // outline's rounded box (12 DIP corners, Bezier K 0.5523 as the original's; WPF's rounded rectangle is the same curve).
    private void DrawGenie(double progress)
    {
        var snapshot = _snapshot!;
        var bounds = GenieCollapse.Mesh(progress, new GenieRect(_source.X, _source.Y, _source.Width, _source.Height),
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
        int n = GenieCollapse.Tiles + 1;
        double cellW = _source.Width / GenieCollapse.Tiles, cellH = _source.Height / GenieCollapse.Tiles;
        var whole = new Rect(_source.X, _source.Y, _source.Width, _source.Height);
        using var dc = _mesh.RenderOpen();
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
                // The snapshot's cell (sx0, sy0, cellW, cellH) onto p1, p2, p3.
                double sx0 = _source.X + gx * cellW, sy0 = _source.Y + gy * cellH;
                double m11 = (p2.X - p1.X) / cellW, m12 = (p2.Y - p1.Y) / cellW, m21 = (p3.X - p1.X) / cellH, m22 = (p3.Y - p1.Y) / cellH;
                var matrix = new Matrix(m11, m12, m21, m22, p1.X - sx0 * m11 - sy0 * m21, p1.Y - sx0 * m12 - sy0 * m22);
                if (!matrix.HasInverse) continue;
                dc.PushClip(quad);
                dc.PushTransform(new MatrixTransform(matrix));
                dc.DrawImage(snapshot, whole);
                dc.Pop();
                dc.Pop();
            }
    }

    private static double Sq(double v) => v * v;
    // The original pushes a corner away from the cell's centre along the line to it; a cell far wider than tall then barely
    // grows in height, and WPF's aliased clip left one-pixel seams between rows. Pushed along each axis instead.
    private static Point Bloat((double X, double Y) p, double cx, double cy, double amount)
        => new(p.X + Math.Sign(p.X - cx) * amount, p.Y + Math.Sign(p.Y - cy) * amount);
}
