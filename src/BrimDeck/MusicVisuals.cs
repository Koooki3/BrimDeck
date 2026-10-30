using System.Globalization;
using System.Windows;
using BrimDeck.Core;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace BrimDeck;

internal static class MusicVisuals
{
    internal static FrameworkElement Icon(string name, double size, Brush? brush = null)
    {
        var data = name switch
        {
            "play" => "M 7,3.5 Q 5,2.5 5,5 V 19 Q 5,21.5 7,20.5 L 20,13 Q 22,12 20,11 Z",
            "pause" => "M 5,3 H 9 Q 10,3 10,4 V 20 Q 10,21 9,21 H 5 Q 4,21 4,20 V 4 Q 4,3 5,3 Z M 15,3 H 19 Q 20,3 20,4 V 20 Q 20,21 19,21 H 15 Q 14,21 14,20 V 4 Q 14,3 15,3 Z",
            "previous" => "M 3,4 H 5 V 20 H 3 Z M 20,4 Q 21,3.4 21,5 V 19 Q 21,20.6 20,20 L 7,13 Q 5.5,12 7,11 Z",
            "next" => "M 19,4 H 21 V 20 H 19 Z M 4,4 Q 3,3.4 3,5 V 19 Q 3,20.6 4,20 L 17,13 Q 18.5,12 17,11 Z",
            "shuffle" => "M 3,6 H 6 C 10,6 14,18 18,18 H 21 M 17,14 L 21,18 L 17,22 M 3,18 H 6 C 10,18 14,6 18,6 H 21 M 17,2 L 21,6 L 17,10",
            "repeat" => "M 4,10 V 8 Q 4,5 7,5 H 20 M 16,1 L 20,5 L 16,9 M 20,14 V 16 Q 20,19 17,19 H 4 M 8,15 L 4,19 L 8,23",
            "sequential" => "M 3,5.1 H 16 V 3 L 21,6 L 16,9 V 6.9 H 3 Z M 3,17.1 H 16 V 15 L 21,18 L 16,21 V 18.9 H 3 Z",
            "like" or "liked" => "M 12,20.5 C 12,20.5 3,15 3,8.8 C 3,6 5.2,3.8 7.9,3.8 C 9.6,3.8 11.1,4.7 12,6.1 C 12.9,4.7 14.4,3.8 16.1,3.8 C 18.8,3.8 21,6 21,8.8 C 21,15 12,20.5 12,20.5 Z",
            _ => "M 9,18 V 5.5 L 20,3.3 V 16 M 9,18 A 3,3 0 1 1 3,18 A 3,3 0 1 1 9,18 M 20,16 A 3,3 0 1 1 14,16 A 3,3 0 1 1 20,16"
        };
        bool filled = name is "play" or "pause" or "previous" or "next" or "sequential" or "liked";
        var path = new Path { Data = Geometry.Parse(data), Fill = filled ? brush ?? UI.Brush(UI.Primary) : null,
            Stroke = filled ? null : brush ?? UI.Brush(UI.Secondary), StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }
    internal static Border Cover(BitmapSource? image, double size, double radius, bool outlined = false)
    {
        double corner = PanelTheme.Corner(radius);
        var content = new Grid { Background = UI.Brush(PanelTheme.Hex("coverPlaceholder")) };
        if (image is not null) content.Children.Add(new Image { Source = image, Stretch = Stretch.UniformToFill });
        else content.Children.Add(Icon("music", size * .34));
        if (outlined) content.Children.Add(new Rectangle { Stroke = UI.Brush(PanelTheme.Hex("coverOutline")), StrokeThickness = 1, RadiusX = corner, RadiusY = corner, Margin = new Thickness(.5), IsHitTestVisible = false });
        content.Clip = new RectangleGeometry(new Rect(0, 0, size, size), corner, corner);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(corner), Child = content };
    }
    // The color taken from the cover, or the theme's equalizer color when there is no cover or it gives none.
    internal static Brush Accent(BitmapSource? source) => Accent(source, out _);
    internal static Brush Accent(BitmapSource? source, out Color? cover)
    {
        cover = null;
        if (source is null) return UI.Brush(PanelTheme.Hex("equalizer"));
        try
        {
            var scaled = new TransformedBitmap(source, new ScaleTransform(16d / source.PixelWidth, 16d / source.PixelHeight));
            var bitmap = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[16 * 16 * 4]; bitmap.CopyPixels(pixels, 64, 0);
            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                double min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
                double w = Math.Max(.12, (max - min) / 255d) * pixels[i + 3] / 255d;
                b += pixels[i] * w; g += pixels[i + 1] * w; r += pixels[i + 2] * w; weight += w;
            }
            if (weight == 0) return UI.Brush(PanelTheme.Hex("equalizer"));
            r /= weight * 255; g /= weight * 255; b /= weight * 255;
            double maxChannel = Math.Max(r, Math.Max(g, b)), minChannel = Math.Min(r, Math.Min(g, b));
            double light = (maxChannel + minChannel) / 2, delta = maxChannel - minChannel;
            double saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * light - 1));
            double hue = delta == 0 ? 0 : maxChannel == r ? ((g - b) / delta + (g < b ? 6 : 0)) / 6
                : maxChannel == g ? ((b - r) / delta + 2) / 6 : ((r - g) / delta + 4) / 6;
            bool dark = PanelTheme.Current.IsDark;
            saturation = Math.Max(.35, saturation); light = dark ? Math.Clamp(light, .62, .78) : Math.Clamp(light, .30, .42);
            double chroma = (1 - Math.Abs(2 * light - 1)) * saturation, x = chroma * (1 - Math.Abs(hue * 6 % 2 - 1)), m = light - chroma / 2;
            (r, g, b) = (hue * 6) switch
            { < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d), < 3 => (0d, chroma, x), < 4 => (0d, x, chroma), < 5 => (x, 0d, chroma), _ => (chroma, 0d, x) };
            r += m; g += m; b += m;
            var accent = PanelTheme.Readable(Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
            var brush = new SolidColorBrush(accent); brush.Freeze(); cover = accent; return brush;
        }
        catch (Exception) { return UI.Brush(PanelTheme.Hex("equalizer")); }
    }
    // Coordinates from the element's own width. A relative brush spans everything the element draws,
    // including rings scrolled out of view, so its fades would follow the content instead of the edges.
    internal static Brush Fade(double width, double edge)
        => new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Colors.White, Math.Min(.5, edge / Math.Max(1, width))), new(Colors.White, Math.Max(.5, 1 - edge / Math.Max(1, width))), new(Colors.Transparent, 1) }, new Point(0, 0), new Point(width, 0))
        { MappingMode = BrushMappingMode.Absolute };
}

// The bars change every frame. They are redrawn into a retained drawing, so a frame costs
// only rendering; InvalidateVisual would also run layout and re-hit-test the whole window.
internal sealed class MusicEqualizer : FrameworkElement
{
    public Brush Color { get; set; } = UI.Brush(UI.Secondary);
    public int Bars { get; init; } = 5;
    // Bar width in DIPs; the collapsed row scales it with its height.
    public double BarWidth { get; init; } = 3;
    // The heights. The panel hands the collapsed row's bars to each row it builds again, so a new row starts where the old one
    // was; built afresh, the bars would drop to rest for a frame on every step of a resize and their dynamics would start over.
    public EqualizerBars Levels { get; init; } = new();
    private readonly DrawingGroup _drawing = new();
    private Size _drawnSize;
    public void Tick(float peak, bool playing, double seconds = 1d / 30) { Levels.Tick(peak, playing, seconds); Redraw(); }
    // Bar values already computed from captured audio, leftmost first.
    public void Show(ReadOnlySpan<double> levels, bool playing) { Levels.Show(levels, playing); Redraw(); }
    protected override void OnRender(DrawingContext dc) { Redraw(force: true); dc.DrawDrawing(_drawing); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); _bars = null; InvalidateVisual(); }
    // The bars stand on half device pixels (EqualizerLayout): a level that moves none of them draws nothing new, and one that
    // does only moves its own rectangle in the retained drawing. The drawing is built again only for a new size, color or scale.
    private RectangleGeometry[]? _bars;
    private Brush? _barsColor;
    private void Redraw(bool force = false)
    {
        var size = RenderSize;
        var levels = Levels.Levels;
        int count = Math.Min(Bars, levels.Length);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip, radius = BarWidth / 2;
        if (force || _bars is null || _bars.Length != count || size != _drawnSize || !ReferenceEquals(_barsColor, Color))
        {
            _bars = new RectangleGeometry[count]; _barsColor = Color; _drawnSize = size;
            using var dc = _drawing.Open();
            for (int i = 0; i < count; i++)
            {
                var (x, y, w, h) = EqualizerLayout.Bar(i, Bars, levels[i], size.Width, size.Height, BarWidth, dpi);
                _bars[i] = new RectangleGeometry(new Rect(x, y, w, h), radius, radius);
                dc.DrawGeometry(Color, null, _bars[i]);
            }
            return;
        }
        for (int i = 0; i < count; i++)
        {
            var (x, y, w, h) = EqualizerLayout.Bar(i, Bars, levels[i], size.Width, size.Height, BarWidth, dpi);
            var rect = new Rect(x, y, w, h);
            if (_bars[i].Rect != rect) _bars[i].Rect = rect;
        }
    }
}

internal sealed class MusicMarquee : FrameworkElement
{
    private static Typeface Typeface => Fonts.Face(UiFontRole.Panel);
    // Width of the edge fade, and the scroll speed in DIP per second.
    internal const double Edge = 6, Speed = 24;
    private readonly TranslateTransform _shift = new();
    // The ink fade's brush moves against the text, so its fades stay at the element's edges while the text glides.
    private readonly TranslateTransform _unshift = new();
    // An edge fades only while text is hidden beyond it: none on the left before the text moves,
    // none on the right once its end is in view. The first and last characters rest unfaded.
    private readonly GradientStop _left = new(Colors.White, 0), _right = new(Colors.White, 1);
    private string _text = "";
    private double _fontSize = 11.5, _overflow, _fadeWidth = -1;
    private FormattedText? _formatted;
    private bool _scrolling, _running, _hovered, _hooked;
    // Wakes the line shortly before a rest ends; during the rest itself no frame is drawn.
    private PromptTimer? _rest;
    public MusicMarquee()
    {
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) =>
        {
            _hovered = false; UpdateAnimation();
            // A line of its own starts again when it comes back; the panel's line goes on in the row built in its place.
            if (!_sharedScroll) { _scroll.Restart(); Apply(); }
        };
        IsVisibleChanged += (_, _) => UpdateAnimation();
        MouseEnter += (_, _) => { _hovered = true; UpdateAnimation(); };
        MouseLeave += (_, _) => { _hovered = false; UpdateAnimation(); };
    }
    // Where the line stands in its scroll. The panel hands its own to the collapsed row it builds again, so the line carries on.
    public MarqueeScroll Scroll { get => _scroll; init { _scroll = value; _sharedScroll = true; } }
    private readonly MarqueeScroll _scroll = new();
    private readonly bool _sharedScroll;
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value; _scroll.Follow(value); ResetText(); AutomationProperties.SetName(this, value);
            // The collapsed row sizes the title's room to its text (TextWidth), so a new line places the row again.
            (VisualTreeHelper.GetParent(this) as UIElement)?.InvalidateArrange();
        }
    }
    // The width the text takes on one line, in DIPs.
    public double TextWidth => _text.Length == 0 ? 0 : Formatted.Width;
    // For screen readers: the whole line as text, however much of it shows.
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Text, () => _text);
    public double FontSize
    {
        get => _fontSize;
        set { if (_fontSize == value) return; _fontSize = value; ResetText(); }
    }
    public bool CenterWhenFits { get; init; } = true;
    // The edge fade's width; the collapsed row scales it with its height. The scroll speed does not change.
    public double EdgeWidth { get; init; } = Edge;
    // False when the region is too narrow for a meaningful stretch of text: the text then rests at its start, cut at the edge.
    public bool ScrollAllowed { get; init; } = true;
    public Brush Foreground { get; init; } = UI.Brush(UI.Primary);
    // With a backdrop, the text is drawn by a child that re-enables ClearType below this element's clip, and the edge fades are
    // drawn over the text in the backdrop color instead of an opacity mask. A clip or a mask on the drawing element forces grayscale.
    public Color? Backdrop
    {
        get => _backdrop;
        init
        {
            _backdrop = value;
            if (value is not { } color) return;
            _left.Color = _right.Color = Color.FromArgb(0, color.R, color.G, color.B);
            _glyphs = new Glyphs(this); RenderOptions.SetClearTypeHint(_glyphs, PanelTheme.ClearType);
            AddVisualChild(_glyphs); ClipToBounds = true;
        }
    }
    private readonly Color? _backdrop;
    // Over a surface with a gradient or scan lines no single color can paint the fades, so the text itself fades: its brush is
    // the foreground with transparent ends. The text is still drawn by the child, with no opacity mask and no intermediate
    // layer, which a mask would need on every frame of the scroll.
    private readonly bool _inkFade;
    public bool InkFade
    {
        get => _inkFade;
        init
        {
            if (!value || _glyphs is not null) return;
            _inkFade = true;
            // Text drawn with a gradient brush under a forced ClearType hint came out white whatever its color (seen on light
            // themes with a gradient or glass), so here WPF chooses: grayscale for the fading text.
            _glyphs = new Glyphs(this); RenderOptions.SetClearTypeHint(_glyphs, ClearTypeHint.Auto);
            AddVisualChild(_glyphs); ClipToBounds = true;
        }
    }
    private Brush? _ink;
    private readonly Glyphs? _glyphs;
    private Brush? _fade;
    private UIElement Painter => _glyphs ?? (UIElement)this;
    private sealed class Glyphs(MusicMarquee owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => owner.Draw(dc, RenderSize);
    }
    protected override int VisualChildrenCount => _glyphs is null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => _glyphs ?? throw new ArgumentOutOfRangeException(nameof(index));
    protected override Size MeasureOverride(Size availableSize) { _glyphs?.Measure(availableSize); return default; }
    protected override Size ArrangeOverride(Size finalSize) { _glyphs?.Arrange(new Rect(finalSize)); return finalSize; }
    private FormattedText Formatted => _formatted ??= new(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        Typeface, FontSize, _ink ?? Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    public void SetScrolling(bool enabled) { _scrolling = enabled; UpdateAnimation(); }
    // A new size or scale keeps the line's place (MarqueeScroll); only a new line starts it again.
    private void ResetText() { _formatted = null; Painter.InvalidateVisual(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); ResetText(); }
    private double EdgeStop => _fadeWidth > 0 ? Math.Min(.5, EdgeWidth / _fadeWidth) : 0;
    // Scrolls the laps again from the start once a Limited line has rested (MarqueeScroll.Replay).
    public void Replay() { if (_scroll.Replay()) { _applied = (double.NaN, 0, 0, 0); Apply(); UpdateAnimation(); } }
    // Whether the line is on the display's clock (Motion): while it is, WPF draws a frame every refresh anyway. Gliding is raised
    // when that changes, so the window can move the bars on those frames.
    public bool IsGliding => _hooked;
    public event Action<bool>? Gliding;
    private void SetHooked(bool hooked)
    {
        if (_hooked == hooked) return;
        _hooked = hooked; Gliding?.Invoke(hooked);
    }
    private void UpdateAnimation()
    {
        // A Limited line that has scrolled its laps rests at its start with no clock and no timer (MarqueeScroll.Rested).
        bool running = _scrolling && ScrollAllowed && IsLoaded && IsVisible && !_hovered && !_scroll.Rested && _overflow > 0 && _formatted is not null;
        if (running != _running)
        {
            _running = running;
            if (running) Hook();
            else
            {
                Unhook();
                // Held still (paused, hovered, hidden), the time does not count. A row taken out of the window is being replaced,
                // and the panel's line goes on in the new one without losing a frame.
                if (PresentationSource.FromVisual(this) is not null) _scroll.Pause();
            }
        }
        // Scrolling text takes the animated quality, which follows fractional offsets smoothly; otherwise WPF chooses, so resting
        // text gets the static quality and the island's scaling still counts as motion. The mode is a composition flag and does not
        // draw the text again. A row built in place of a scrolling one takes it from its first frame, before it has loaded.
        bool moving = _running || !IsLoaded && _scrolling && ScrollAllowed && !_hovered && !_scroll.Rested && _overflow > 0;
        TextOptions.SetTextHintingMode(Painter, moving ? TextHintingMode.Animated : TextHintingMode.Auto);
    }
    // The line glides with the display's frames on the panel's clock (Motion), timed by each frame's own time (Motion.FrameTime):
    // a keyframe clock is ticked at WPF's default animation rate, which does not divide a 144 Hz refresh, so a slow glide stepped
    // unevenly. Each frame only sets the transforms and fade stops, so nothing is measured or drawn again. The rests at either
    // end (MarqueeScroll.Hold) draw nothing, so they are waited out on a timer that wakes the line a few frames before the rest
    // ends, instead of holding the display's clock for frames that change nothing.
    private const double RestWake = .1;
    private void Hook()
    {
        _rest?.Stop();
        if (_hooked) return;
        Motion.Start(this, Frame);
        SetHooked(true);
    }
    private void Unhook() { _rest?.Stop(); SetHooked(false); }
    private bool Frame(double now)
    {
        if (!_hooked) return false;
        _scroll.Advance(Motion.FrameTime, _overflow, Speed);
        Apply();
        // Its laps done, the line lets go of the clock for good (UpdateAnimation no longer runs it) until it is replayed or changes.
        if (_scroll.Rested) { UpdateAnimation(); return false; }
        double rest = _scroll.RestLeft;
        if (rest <= RestWake + .15) return true;
        _scroll.Wait();
        _rest ??= new PromptTimer(Dispatcher, () => { _rest!.Stop(); if (_running) Hook(); });
        _rest.Interval = TimeSpan.FromSeconds(rest - RestWake); _rest.Start();
        SetHooked(false);
        return false;
    }
    // Places the text and the fades where the scroll stands; the frames of a rest set nothing.
    private void Apply()
    {
        double offset = _scroll.At(_overflow), edge = EdgeStop;
        var (left, right) = MarqueeScroll.Fades(offset, _overflow, EdgeWidth);
        if ((offset, left, right, edge) == _applied) return;
        _applied = (offset, left, right, edge);
        _shift.X = -offset; _unshift.X = offset;
        _left.Offset = edge * left; _right.Offset = 1 - edge * right;
    }
    private (double Offset, double Left, double Right, double Edge) _applied = (double.NaN, 0, 0, 0);
    protected override void OnRender(DrawingContext dc) { if (_glyphs is null) Draw(dc, RenderSize); }
    // The size comes from the element that draws: the child renders during this element's arrange, before its own size is set.
    private void Draw(DrawingContext dc, Size size)
    {
        if (size.Width <= 0) return;
        var text = Formatted;
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Snap(double value) => Math.Round(value * scale) / scale;
        // Whole device pixels for the scroll distance, the centred start and the baseline, so resting glyphs are not split across
        // pixels at 150 %. The small allowance keeps rounding noise from starting a one-pixel scroll.
        double overflow = Math.Max(0, Math.Ceiling((text.Width - size.Width) * scale - .01) / scale);
        double fadeWidth = overflow > 0 ? size.Width : 0;
        if (_fadeWidth != fadeWidth)
        {
            _fadeWidth = fadeWidth;
            if (_inkFade)
            {
                // The text's own brush fades at the ends (see InkFade).
                var ink = (Foreground as SolidColorBrush)?.Color ?? Colors.White;
                _left.Color = _right.Color = ink;
                var clear = Color.FromArgb(0, ink.R, ink.G, ink.B);
                _ink = fadeWidth > 0 ? new LinearGradientBrush(new GradientStopCollection { new(clear, 0), _left, _right, new(clear, 1) },
                    new Point(0, 0), new Point(fadeWidth, 0)) { MappingMode = BrushMappingMode.Absolute, Transform = _unshift } : null;
                _formatted = null; text = Formatted;
            }
            else
            {
                // Coordinates from this element's own width, like MusicVisuals.Fade.
                var edge = _backdrop ?? Colors.Transparent;
                var fade = fadeWidth > 0 ? new LinearGradientBrush(new GradientStopCollection { new(edge, 0), _left, _right, new(edge, 1) },
                    new Point(0, 0), new Point(fadeWidth, 0)) { MappingMode = BrushMappingMode.Absolute } : null;
                if (_glyphs is null) OpacityMask = fade; else _fade = fade;
            }
        }
        // A new width keeps the line's place: the glide goes on, and a line at its end keeps its end at the edge.
        _scroll.Follow(_text);
        _overflow = overflow; Apply();
        // Cover the whole box, as the carousel's background does, so the mask layer's edge is not the text's edge.
        if (overflow > 0) dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        if (_glyphs is null) dc.PushClip(new RectangleGeometry(new Rect(size)));
        dc.PushTransform(_shift);
        double x = overflow == 0 && CenterWhenFits ? Snap((size.Width - text.Width) / 2) : 0;
        dc.DrawText(text, new Point(x, Snap((size.Height - text.Height) / 2 + text.Baseline) - text.Baseline));
        dc.Pop();
        if (_glyphs is null) dc.Pop(); else if (_fade is not null) dc.DrawRectangle(_fade, null, new Rect(size));
        UpdateAnimation();
    }
}

// A window over a strip of items wider than it, which scrolls like the lyric marquee: rest with the first items whole in the
// window, glide until the last ones stand whole from its left edge (CompactSpacing.Window, Travel), rest there, then fade back
// to the start and begin again. Both rests show whole items only; the fades lie outside the window, over the gaps beside it,
// so nothing at rest is cut or faded. The quota rings and the system readings use it.
internal abstract class GlideCarousel : Grid
{
    internal const double StartHold = QuotaCycle.StartHold, EndHold = QuotaCycle.EndHold, Speed = QuotaCycle.Speed, Fade = QuotaCycle.Fade, FadeEdge = 3;
    private FrameworkElement _strip = null!;
    // The strip's shift is this carousel's own: the readings strip is handed from row to row, and a carousel that let go of it
    // (Unloaded, after the next row took the strip) must not stop or move the next carousel's glide, nor leave its own running
    // on a transform no longer drawn, which kept WPF's frame clock going for nothing.
    private TranslateTransform _shift = new();
    private bool _discrete;
    private bool _enabled, _paused;
    private TimeSpan _elapsed;
    // The rests are waited out on a timer (Turn) and an animation clock runs only while the rings move (win2 P1: a storyboard
    // repeating forever kept WPF's animation clock ticking at 60 frames a second through the rests too, as a storyboard of
    // discrete keyframes did with animations off). With animations off the rings only jump between the two rests. _since is
    // when the cycle last went on (Eased.Now), NaN while it is held; _elapsed is where it stood then.
    private PromptTimer? _turn;
    private double _since = double.NaN, _period;
    private QuotaCycle _cycle;
    // A move starts on the animation clock this long before its time, so a timer that wakes a little late never delays it.
    private const double Lead = .05;
    private static readonly IEasingFunction GlideEase = Frozen(new BezierEase(QuotaCycle.CurveX1, QuotaCycle.CurveY1, QuotaCycle.CurveX2, QuotaCycle.CurveY2));
    private static IEasingFunction Frozen(BezierEase ease) { ease.Freeze(); return ease; }
    public bool Rotates { get; private set; }
    public double Travel { get; private set; }
    // Position within the cycle; the window hands it to the next carousel so a rebuild continues where this one was.
    public TimeSpan Elapsed => Rotates ? TimeSpan.FromSeconds(At(Eased.Now)) : _elapsed;

    // Shows strip in a window this wide, its fades (edge wide) outside it, and scrolls it by travel; no travel leaves it at rest.
    // cut: while the surface is dragged (CompactSpacing.ShareFlowing) the window ends wherever the room does, in the middle of an
    // item, and nothing glides: the strip stands at its start, the item the window cuts fades out inside the window over this
    // many DIPs (CompactSpacing.CutFade), and the left fade lies over the gap before the window as for a rotating one.
    protected void Glide(FrameworkElement strip, double window, double travel, double edge, TimeSpan elapsed, bool animate, double cut = 0)
    {
        _strip = strip;
        window = Math.Max(0, window); travel = Math.Max(0, travel); cut = Math.Max(0, cut);
        Rotates = travel > 0;
        edge = Rotates || cut > 0 ? Math.Max(0, edge) : 0;
        Width = edge + window + edge; ClipToBounds = true;
        strip.RenderTransform = _shift;
        Canvas.SetLeft(strip, edge);
        // A Canvas measures the strip at its full width. Inside the Grid the strip would be layout-clipped to the visible width,
        // and that clip moves with the translation, so what scrolls in from the right would render blank.
        var host = new Canvas(); host.Children.Add(strip); Children.Add(host);
        if (!Rotates && cut > 0 && Width > 0)
        {
            Margin = new Thickness(-edge, 0, -edge, 0);
            double end = edge + window;
            OpacityMask = new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Colors.White, edge / Width),
                new(Colors.White, Math.Max(edge, end - cut) / Width), new(Colors.Transparent, end / Width), new(Colors.Transparent, 1) }, new Point(0, 0), new Point(Width, 0)) { MappingMode = BrushMappingMode.Absolute };
            Background = Brushes.Transparent;
            return;
        }
        if (!Rotates) return;
        Margin = new Thickness(-edge, 0, -edge, 0);
        OpacityMask = new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Colors.White, edge / Width),
            new(Colors.White, (Width - edge) / Width), new(Colors.Transparent, 1) }, new Point(0, 0), new Point(Width, 0)) { MappingMode = BrushMappingMode.Absolute };
        // The masked content is composited from a layer the size of what is drawn. A transparent background extends that layer
        // to the box, so its resampled edge column falls on empty space instead of repeating the strip's outermost pixels.
        Background = Brushes.Transparent;
        Travel = travel;
        _discrete = !animate;
        _cycle = new QuotaCycle(Travel);
        // Without animations there is no fade back, so the cycle ends with the last rest.
        _period = _discrete ? _cycle.GlideEnd + EndHold : _cycle.Period;
        _elapsed = TimeSpan.FromSeconds(elapsed.TotalSeconds % _period);
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Update();
        IsVisibleChanged += (_, _) => Update();
    }
    public void SetRunning(bool enabled, bool paused) { _enabled = enabled; _paused = paused; Update(); }

    // Where the cycle stands at a time, in seconds from its start.
    private double At(double now) => ((double.IsNaN(_since) ? _elapsed.TotalSeconds : _elapsed.TotalSeconds + now - _since) % _period + _period) % _period;

    // Shows where the cycle stands and, while it runs, waits for its next turn. Without animations: the first rest until the
    // glide's end, then the last until the cycle's end. With them: a rest shows its still values with no clock running, and
    // just before it ends the next move (the glide, or the fade back to the first ring) starts on a clock of its own, which
    // lets go at the next turn.
    private void Turn()
    {
        double now = Eased.Now, at = At(now);
        var shift = _shift;
        bool running = !double.IsNaN(_since);
        double next;
        if (_discrete)
        {
            shift.X = at < _cycle.GlideEnd ? 0 : -Travel;
            next = at < _cycle.GlideEnd ? _cycle.GlideEnd - at : _period - at;
        }
        else
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null); _strip.BeginAnimation(OpacityProperty, null);
            shift.X = _cycle.Shift(at); _strip.Opacity = _cycle.Opacity(at);
            var (moving, start, end) = _cycle.StretchAt(at);
            next = end - at;
            if (running && moving) Move(start, end, at - start);
            else if (running && next <= Lead)
            {
                // The rest is all but over: the move is handed to the clock now, to start exactly when the rest ends.
                (_, start, end) = _cycle.StretchAt(end);
                Move(start, end, at - start);
                next = end - at;
            }
            else if (running) next -= Lead;
        }
        if (!running) { _turn?.Stop(); return; }
        _turn ??= new PromptTimer(Dispatcher, Turn);
        // A hair past the turn; a timer that still lands short of it only waits out what is left.
        _turn.Interval = TimeSpan.FromSeconds(next + .005);
        _turn.Start();
    }

    // One move of the cycle, from start to end, of which `into` seconds have gone (negative: it starts that much later). The
    // glide eases from the first ring to the last; the reset fades out, jumps back to the first ring and fades in, so it never
    // reads as a backwards scroll. Each holds its last value until the next turn takes it off.
    private void Move(double start, double end, double into)
    {
        var shift = _shift;
        var begin = TimeSpan.FromSeconds(-into);
        if (start < _cycle.GlideEnd)
        {
            var glide = new DoubleAnimation(0, -Travel, TimeSpan.FromSeconds(end - start)) { BeginTime = begin, EasingFunction = GlideEase };
            Timeline.SetDesiredFrameRate(glide, GlideRate(VisualTreeHelper.GetDpi(this).PixelsPerDip));
            shift.BeginAnimation(TranslateTransform.XProperty, glide);
            return;
        }
        static KeyTime Key(double seconds) => KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
        var back = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = TimeSpan.FromSeconds(2 * Fade) };
        back.KeyFrames.Add(new DiscreteDoubleKeyFrame(-Travel, Key(0)));
        back.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, Key(Fade)));
        var fade = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = TimeSpan.FromSeconds(2 * Fade) };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, Key(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, Key(Fade)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, Key(2 * Fade)));
        shift.BeginAnimation(TranslateTransform.XProperty, back);
        _strip.BeginAnimation(OpacityProperty, fade);
    }

    // The glide moves at most PeakSlope times its average speed (the curve's steepest point); drawn half a device pixel a step at
    // that speed, which is 40 frames a second at 100 % and 60 at 150 %. At the display's own rate (144 a second) each frame
    // moved it a fifth of a pixel, and every one of them redrew the layered island: the collapsed row's readings and rings
    // cost about 120 ms of CPU a second while they glided (regress, bcc93c6).
    internal const double PeakSlope = 1.67;
    internal static int GlideRate(double pixelsPerDip)
        => (int)Math.Clamp(Math.Ceiling(2 * Speed * PeakSlope * (pixelsPerDip > 0 ? pixelsPerDip : 1)), 24, Motion.FrameRate);

    // Runs while it is loaded, visible, enabled and not paused; held, it still shows where the cycle stands (a carousel built
    // in place of one that was past its glide), with no clock or timer left behind.
    private void Update()
    {
        if (!Rotates) return;
        bool run = IsLoaded && IsVisible && _enabled && !_paused;
        double now = Eased.Now;
        if (run == !double.IsNaN(_since)) { if (!run) Turn(); return; }
        if (run) _since = now;
        else { _elapsed = TimeSpan.FromSeconds(At(now)); _since = double.NaN; }
        Turn();
    }
}

// Rings that do not all fit scroll through a window of whole rings (CompactSpacing.Window), a ring step at a time.
internal sealed class QuotaCarousel : GlideCarousel
{
    // For screen readers: its name, every ring's tooltip line, rotating or not.
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Text, () => AutomationProperties.GetName(this).Replace("\n", Loc.T("；", "; ")));
    // The first letter or digit of an application's name, upper case, for the middle of its ring; empty without one.
    internal static string Letter(string name)
    {
        foreach (char c in name ?? "") if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return "";
    }

    // count: the whole rings the window holds (CompactSpacing.Window); gap: the row's gap, which the rings keep between them too.
    // flowing: the window as wide as the room gives it, not whole rings (the surface is being dragged; CompactSpacing.ShareFlowing),
    // with the ring it cuts faded over cut DIPs; nothing glides then.
    public QuotaCarousel(IReadOnlyList<(double Used, string Color, string Tip, string Letter)> items, double size, double thickness, double gap, int count, double edge,
        double scale, TimeSpan elapsed = default, bool animate = true, double? flowing = null, double cut = 0)
    {
        // Ring positions, the window and the travel all use whole device pixels. Layout rounding would otherwise widen each gap
        // at 150 % and carry the last ring into the edge fade.
        double dpi = VisualTreeHelper.GetDpi(Application.Current?.MainWindow ?? (Visual)this).DpiScaleX;
        double Snap(double value) => Math.Round(value * dpi, MidpointRounding.AwayFromZero) / dpi;
        double step = Snap(size + gap);
        int shown = Math.Clamp(count, 0, items.Count);
        double window = flowing is { } flow ? Math.Min(Math.Max(0, flow), items.Count * step - Snap(gap)) : shown == 0 ? 0 : (shown - 1) * step + size;
        Height = size;
        var strip = new Canvas { Width = items.Count * step, Height = size };
        for (int i = 0; i < items.Count; i++)
        {
            var ring = UI.Ring(items[i].Used, items[i].Color, size, thickness);
            if (items[i].Letter.Length > 0)
            {
                // The letter inside, at the size the system readings' dials carry theirs for this height, in the secondary text color.
                var letter = new TextBlock { Text = items[i].Letter, FontFamily = UI.PanelFont, FontWeight = FontWeights.SemiBold, FontSize = Math.Max(1, BrimDeck.Core.CompactMusicLayout.Half(7 * scale)),
                    Foreground = UI.Brush(UI.Secondary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
                ring.Children.Add(letter);
            }
            Canvas.SetLeft(ring, i * step); strip.Children.Add(ring);
        }
        ToolTip = string.Join("\n", items.Select(i => i.Tip));
        AutomationProperties.SetName(this, Loc.T("配额环 ", "Quota rings ") + ToolTip);
        // The glide moves by whole ring steps, so at the end the last rings stand whole in the window, as the first did. The fades
        // are no wider than a gap, so they lie over the gaps beside the window.
        Glide(strip, window, flowing is null && shown >= 1 && shown < items.Count ? (items.Count - shown) * step : 0, Math.Min(Snap(edge), Snap(gap)), elapsed, animate, flowing is null ? 0 : Math.Min(Snap(cut), Snap(gap)));
    }
}

// System readings that do not all fit scroll through a window of whole readings (CompactSpacing.Window). The readings keep the
// width of their widest figures (MonitorStrip), so the window and the travel do not move with the figures.
internal sealed class ReadingsCarousel : GlideCarousel
{
    // cut: while the surface is dragged the window is not whole readings and does not glide (travel 0); the reading it cuts fades
    // over this many DIPs inside it (CompactSpacing.CutFade).
    public ReadingsCarousel(MonitorStrip strip, double window, double travel, double edge, TimeSpan elapsed, bool animate, double cut = 0)
    {
        Height = strip.Height;
        strip.Width = strip.PreferredWidth; strip.HorizontalAlignment = HorizontalAlignment.Left; strip.VerticalAlignment = VerticalAlignment.Top; strip.Margin = new Thickness(0);
        Glide(strip, window, travel, edge, elapsed, animate, cut);
    }
}
