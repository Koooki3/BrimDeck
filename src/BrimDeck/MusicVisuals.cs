using System.Globalization;
using System.Windows;
using BrimDeck.Core;
using System.Windows.Automation;
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
        var content = new Grid { Background = UI.Brush(PanelTheme.Hex("coverPlaceholder")) };
        if (image is not null) content.Children.Add(new Image { Source = image, Stretch = Stretch.UniformToFill });
        else content.Children.Add(Icon("music", size * .34));
        if (outlined) content.Children.Add(new Rectangle { Stroke = UI.Brush(PanelTheme.Hex("coverOutline")), StrokeThickness = 1, RadiusX = radius, RadiusY = radius, Margin = new Thickness(.5), IsHitTestVisible = false });
        content.Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Child = content };
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
    public bool Playing { get; set; }
    public float Peak { get; set; }
    public int Bars { get; init; } = 5;
    // Bar width in DIPs; the collapsed row scales it with its height.
    public double BarWidth { get; init; } = 3;
    private readonly (double Time, double Peak)[] _history = new (double, double)[64];
    private readonly double[] _levels = new double[5], _drawn = new double[5];
    private readonly DrawingGroup _drawing = new();
    private Size _drawnSize;
    private int _next, _count;
    private double _time;
    public void Tick(float peak, bool playing, double seconds = 1d / 30)
    {
        Peak = float.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0; Playing = playing;
        if (!playing)
        {
            Array.Clear(_levels); _next = _count = 0; _time = 0;
            Redraw(); return;
        }
        seconds = double.IsFinite(seconds) ? Math.Clamp(seconds, .001, .5) : 1d / 30;
        _time += seconds;
        _history[_next] = (_time, Peak); _next = (_next + 1) % _history.Length;
        _count = Math.Min(_count + 1, _history.Length);
        double low = Peak, high = Peak;
        for (int i = 0; i < _count; i++)
        {
            var sample = Previous(i);
            if (_time - sample.Time > 1.6) break;
            low = Math.Min(low, sample.Peak); high = Math.Max(high, sample.Peak);
        }
        // Expand the recent musical dynamics instead of compressing absolute
        // volume with a square root. A minimum range prevents noise amplification.
        double range = Math.Max(.0025, Math.Max(high * .25, high - low));
        for (int i = 0; i < _levels.Length; i++)
        {
            double sample = SampleAt(_time - i * .075);
            double contrast = Math.Clamp((sample - low) / range, 0, 1);
            double target = Math.Clamp(sample / .0015, 0, 1) * (.1 + .9 * Math.Pow(contrast, .8));
            double response = 1 - Math.Exp(-seconds / (target > _levels[i] ? .035 : .085));
            _levels[i] += (target - _levels[i]) * response;
        }
        Redraw();
    }
    // Bar values already computed from captured audio, leftmost first.
    public void Show(ReadOnlySpan<double> levels, bool playing)
    {
        Playing = playing;
        for (int i = 0; i < _levels.Length; i++) _levels[i] = playing && i < levels.Length ? Math.Clamp(levels[i], 0, 1) : 0;
        Redraw();
    }
    private (double Time, double Peak) Previous(int offset) => _history[(_next - 1 - offset + _history.Length) % _history.Length];
    private double SampleAt(double time)
    {
        var newer = Previous(0);
        for (int i = 0; i < _count; i++)
        {
            var older = Previous(i);
            if (older.Time <= time)
                return newer.Time == older.Time ? older.Peak : older.Peak + (newer.Peak - older.Peak) * (time - older.Time) / (newer.Time - older.Time);
            newer = older;
        }
        return 0;
    }
    protected override void OnRender(DrawingContext dc) { Redraw(force: true); dc.DrawDrawing(_drawing); }
    private void Redraw(bool force = false)
    {
        var size = RenderSize;
        // Paused bars stay at rest, so an unchanged frame is not drawn again.
        if (!force && size == _drawnSize && _levels.AsSpan().SequenceEqual(_drawn)) return;
        _levels.CopyTo(_drawn, 0); _drawnSize = size;
        using var dc = _drawing.Open();
        for (int i = 0; i < Math.Min(Bars, _levels.Length); i++)
        {
            double bar = BarWidth;
            double height = Math.Clamp(bar + _levels[i] * Math.Max(0, size.Height - bar), bar, Math.Max(bar, size.Height));
            dc.DrawRoundedRectangle(Color, null, new Rect(i * Math.Max(0, size.Width - bar) / Math.Max(1, Bars - 1), (size.Height - height) / 2, bar, height), bar / 2, bar / 2);
        }
    }
}

internal sealed class MusicMarquee : FrameworkElement
{
    private static readonly Typeface Typeface = new(UI.PanelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
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
    private ClockGroup? _scroll;
    private bool _scrolling, _running, _hovered;
    public MusicMarquee()
    {
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => { _hovered = false; ResetAnimation(); };
        IsVisibleChanged += (_, _) => UpdateAnimation();
        MouseEnter += (_, _) => { _hovered = true; UpdateAnimation(); };
        MouseLeave += (_, _) => { _hovered = false; UpdateAnimation(); };
    }
    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; ResetText(); AutomationProperties.SetName(this, value); }
    }
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
            _glyphs = new Glyphs(this); RenderOptions.SetClearTypeHint(_glyphs, ClearTypeHint.Enabled);
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
            _glyphs = new Glyphs(this); RenderOptions.SetClearTypeHint(_glyphs, ClearTypeHint.Enabled);
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
    private void ResetText() { _formatted = null; ResetAnimation(); Painter.InvalidateVisual(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); ResetText(); }
    private double EdgeStop => _fadeWidth > 0 ? Math.Min(.5, EdgeWidth / _fadeWidth) : 0;
    private void ResetAnimation()
    {
        _scroll?.Controller?.Remove(); _scroll = null; _running = false;
        _shift.ApplyAnimationClock(TranslateTransform.XProperty, null); _shift.X = 0;
        _unshift.ApplyAnimationClock(TranslateTransform.XProperty, null); _unshift.X = 0;
        _left.ApplyAnimationClock(GradientStop.OffsetProperty, null); _left.Offset = 0;
        _right.ApplyAnimationClock(GradientStop.OffsetProperty, null); _right.Offset = 1 - EdgeStop;
    }
    private void UpdateAnimation()
    {
        bool running = _scrolling && ScrollAllowed && IsLoaded && IsVisible && !_hovered && _overflow > 0 && _formatted is not null;
        if (_scroll is null && running)
        {
            double travel = _overflow / Speed, ramp = Math.Min(travel, EdgeWidth / Speed), edge = EdgeStop;
            static KeyTime At(double seconds) => KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
            var duration = TimeSpan.FromSeconds(3 + travel);
            DoubleAnimationUsingKeyFrames Track(params DoubleKeyFrame[] frames)
            { var track = new DoubleAnimationUsingKeyFrames { Duration = duration }; foreach (var frame in frames) track.KeyFrames.Add(frame); return track; }
            // Rest, glide to the end, rest, start again. Each fade grows or shrinks over the first
            // or last Edge of travel, so it follows how much of the text is hidden on that side.
            var cycle = new ParallelTimeline { Duration = duration, RepeatBehavior = RepeatBehavior.Forever };
            cycle.Children.Add(Track(new DiscreteDoubleKeyFrame(0, At(0)), new DiscreteDoubleKeyFrame(0, At(1.5)),
                new LinearDoubleKeyFrame(-_overflow, At(1.5 + travel)), new DiscreteDoubleKeyFrame(-_overflow, At(3 + travel))));
            cycle.Children.Add(Track(new DiscreteDoubleKeyFrame(0, At(0)), new DiscreteDoubleKeyFrame(0, At(1.5)),
                new LinearDoubleKeyFrame(edge, At(1.5 + ramp)), new DiscreteDoubleKeyFrame(edge, At(3 + travel))));
            cycle.Children.Add(Track(new DiscreteDoubleKeyFrame(1 - edge, At(0)), new DiscreteDoubleKeyFrame(1 - edge, At(1.5 + travel - ramp)),
                new LinearDoubleKeyFrame(1, At(1.5 + travel)), new DiscreteDoubleKeyFrame(1, At(3 + travel))));
            cycle.Children.Add(Track(new DiscreteDoubleKeyFrame(0, At(0)), new DiscreteDoubleKeyFrame(0, At(1.5)),
                new LinearDoubleKeyFrame(_overflow, At(1.5 + travel)), new DiscreteDoubleKeyFrame(_overflow, At(3 + travel))));
            // Animate the retained drawing at WPF's render cadence. A background
            // DispatcherTimer can be delayed by input and must not drive text motion.
            _scroll = (ClockGroup)cycle.CreateClock(true);
            _shift.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)_scroll.Children[0]);
            _left.ApplyAnimationClock(GradientStop.OffsetProperty, (AnimationClock)_scroll.Children[1]);
            _right.ApplyAnimationClock(GradientStop.OffsetProperty, (AnimationClock)_scroll.Children[2]);
            _unshift.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)_scroll.Children[3]);
            _running = true;
        }
        if (_scroll is not null && _running != running)
        {
            if (running) _scroll.Controller!.Resume(); else _scroll.Controller!.Pause();
            _running = running;
        }
        // Scrolling text takes the animated quality, which follows fractional offsets smoothly; otherwise WPF chooses, so resting
        // text gets the static quality and the island's scaling still counts as motion. The mode is a composition flag and does not
        // draw the text again.
        TextOptions.SetTextHintingMode(Painter, _running ? TextHintingMode.Animated : TextHintingMode.Auto);
    }
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
        if (_overflow != overflow) { _overflow = overflow; ResetAnimation(); }
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

// Overflowing rings scroll like the lyric marquee: rest at the first ring, glide until the
// last ring is in view, rest there, then fade back to the first ring and start again.
internal sealed class QuotaCarousel : Grid
{
    internal const double StartHold = 2.5, EndHold = 2, Speed = 12, Fade = .25, FadeEdge = 3;
    private readonly Canvas _strip;
    private readonly Storyboard? _cycle;
    private readonly bool _discrete;
    private bool _enabled, _paused, _begun;
    private TimeSpan _elapsed;
    public bool Rotates { get; }
    public double Travel { get; }
    // Position within the cycle; the window hands it to the next carousel so a rebuild continues where this one was.
    public TimeSpan Elapsed => _begun ? _cycle!.GetCurrentTime(this) ?? _elapsed : _elapsed;
    public QuotaCarousel(IReadOnlyList<(double Used, string Color, string Tip)> items, BrimDeck.Core.CompactMusicLayout layout, TimeSpan elapsed = default, bool animate = true)
    {
        // The region is the layout's ring width in whole device pixels; rings sit in fixed slots from its left edge and its
        // right edge cuts whichever ring it falls on. Rotation only moves the rings through the region, never resizes it.
        // Ring positions, the visible width and the travel all use whole device pixels. Layout rounding
        // would otherwise widen each 5 DIP gap at 150 % and carry the last ring into the edge fade.
        double scale = VisualTreeHelper.GetDpi(Application.Current?.MainWindow ?? (Visual)this).DpiScaleX;
        double Snap(double value) => Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
        double step = Snap(layout.RingSize + layout.RingGap);
        double span = Math.Floor(Math.Max(0, layout.RingWidth) * scale + .001) / scale, full = items.Count > 0 ? (items.Count - 1) * step + layout.RingSize : 0;
        // Short of one whole ring, the first ring is cut at the region's edge and rests.
        Rotates = layout.ScrollRings && items.Count > 1 && span >= layout.RingSize && span < full;
        double edge = Rotates ? Snap(layout.RingEdge) : 0;
        // The left fade lies outside, over the cover's gap, so the first ring is whole at rest; the right fade lies inside the
        // region, so nothing is drawn past its right edge toward the lyric. It spans up to one ring gap: a ring the edge cuts
        // fades out over what shows of it, so a sliver of a pixel or two is all but transparent instead of a hard line. It
        // never reaches the first ring, and like the region its width follows the row's width continuously.
        Width = span + edge; Height = layout.RingSize; ClipToBounds = true;
        _strip = new Canvas { Width = items.Count * step, Height = layout.RingSize, RenderTransform = new TranslateTransform() };
        for (int i = 0; i < items.Count; i++)
        {
            var ring = UI.Ring(items[i].Used, items[i].Color, layout.RingSize, layout.RingThickness);
            Canvas.SetLeft(ring, edge + i * step); _strip.Children.Add(ring);
        }
        // A Canvas measures the strip at its full width. Inside the Grid the strip would be
        // layout-clipped to the visible width, and that clip moves with the translation,
        // so rings scrolled in from the right would render blank.
        var host = new Canvas(); host.Children.Add(_strip); Children.Add(host);
        ToolTip = string.Join("\n", items.Select(i => i.Tip));
        AutomationProperties.SetName(this, Loc.T("配额环 ", "Quota rings ") + ToolTip);
        if (!Rotates) return;
        Margin = new Thickness(-edge, 0, 0, 0);
        double fade = Math.Clamp(span - layout.RingSize, 0, Snap(layout.RingGap));
        OpacityMask = new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Colors.White, edge / Width),
            new(Colors.White, (Width - fade) / Width), new(Colors.Transparent, 1) }, new Point(0, 0), new Point(Width, 0)) { MappingMode = BrushMappingMode.Absolute };
        // The masked content is composited from a layer the size of what is drawn. A transparent
        // background extends that layer to the box, so its resampled edge column falls on empty
        // space instead of repeating a ring's outermost pixels.
        Background = Brushes.Transparent;
        // The glide moves by whole ring steps, so at the end the rings stand in the same slots as at the start: the first is
        // whole at the left edge, and the last is whole before the right fade. Since the fade is at most span - ring, this
        // never moves the last ring out of the region.
        Travel = Math.Ceiling((full + fade - span) / step - 1e-6) * step;
        _discrete = !animate; _cycle = Cycle();
        _elapsed = TimeSpan.FromTicks(elapsed.Ticks % _cycle.Duration.TimeSpan.Ticks);
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Update();
        IsVisibleChanged += (_, _) => Update();
    }
    private Storyboard Cycle()
    {
        double move = Travel / Speed, end = StartHold + move, reset = end + EndHold;
        static KeyTime At(double seconds) => KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
        var shift = new DoubleAnimationUsingKeyFrames();
        shift.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(0)));
        shift.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(StartHold)));
        if (_discrete) shift.KeyFrames.Add(new DiscreteDoubleKeyFrame(-Travel, At(end)));
        else shift.KeyFrames.Add(new SplineDoubleKeyFrame(-Travel, At(end), new KeySpline(.4, 0, .6, 1)));
        shift.KeyFrames.Add(new DiscreteDoubleKeyFrame(-Travel, At(reset + (_discrete ? 0 : Fade))));
        shift.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(reset + (_discrete ? 0 : Fade))));
        var cycle = new Storyboard { Duration = TimeSpan.FromSeconds(reset + (_discrete ? 0 : 2 * Fade)), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(shift, _strip); Storyboard.SetTargetProperty(shift, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
        cycle.Children.Add(shift);
        if (!_discrete)
        {
            // The return to the first ring is a short fade, so the reset never reads as a backwards scroll.
            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, At(0)));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, At(reset)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, At(reset + Fade)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, At(reset + 2 * Fade)));
            Storyboard.SetTarget(fade, _strip); Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            cycle.Children.Add(fade);
        }
        return cycle;
    }
    public void SetRunning(bool enabled, bool paused) { _enabled = enabled; _paused = paused; Update(); }
    private void Update()
    {
        if (_cycle is null) return;
        if (!IsLoaded || !IsVisible)
        {
            // Release the clocks while detached; a later load resumes from the saved position.
            if (_begun) { _elapsed = Elapsed; _cycle.Remove(this); _begun = false; }
            return;
        }
        if (!_begun)
        {
            _cycle.Begin(this, true); _begun = true;
            if (_elapsed > TimeSpan.Zero) _cycle.Seek(this, _elapsed, TimeSeekOrigin.BeginTime);
            _cycle.Pause(this);
        }
        if (_enabled && !_paused) _cycle.Resume(this); else _cycle.Pause(this);
    }
}
