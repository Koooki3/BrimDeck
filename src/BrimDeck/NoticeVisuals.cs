using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// A notice in the panel theme the user chose: it is the island itself, dressed as PanelTheme dresses the island (surface color,
// gradient, scan lines, rim, shadow or glow), with the theme's text, fill, hover and accent colors for its content. Each theme
// therefore gets its own notices without a setting of their own: a glowing theme's notice arrives with one flash of its glow,
// a theme with depth casts its shadow, and colors chosen outside the theme (an application's color, the status marks) are
// moved just far enough to read on its surface. Glass is the island's tint and edge light (with the blur of GlassBackdrop under
// the notice window), pixel art its steps, bevel and grain. High contrast replaces everything with system colors and drops glow.
internal sealed record NoticeLook(
    Color Surface, Brush? Gradient, Brush? Pattern, Brush Rim, double RimWidth,
    Color Shadow, double ShadowBlur, double ShadowDepth, double ShadowOpacity,
    string Primary, string Secondary, string Tertiary, string Fill, string Hover, string Pressed, string Accent, string OnAccent, string Danger,
    bool Glow, bool HighContrast)
{
    // The island's material beyond its colors: a pixel theme's step (PanelThemeEffectValues.Pixel) and bevel, a glass's edge light.
    public double Pixel { get; init; }
    // Cut corners (PanelThemeEffectValues.Cut) and the notice's own marks (a hatching strip and the signal bar), from the theme's decor.
    public double Cut { get; init; }
    public PanelDecor? Decor { get; init; }
    public Brush? Bevel { get; init; }
    public Brush? Edge { get; init; }
    // Glass shown (PanelTheme.GlassShown): the surface is a tint over the desktop, so the text cannot use ClearType.
    public bool Glass { get; init; }
    private static (int Generation, bool Glass, double Dpi, NoticeLook Look)? _cached;

    public static NoticeLook Current(double dpiScale = 1)
    {
        if (SystemParameters.HighContrast) return HighContrastLook();
        bool glass = PanelTheme.GlassShown;
        if (_cached is { } cached && cached.Generation == PanelTheme.Generation && cached.Glass == glass && cached.Dpi == dpiScale) return cached.Look;
        var palette = PanelTheme.Current;
        var effects = palette.Effects;
        var look = new NoticeLook(
            PanelTheme.SurfaceColor, PanelTheme.SurfaceGradient(palette), PanelTheme.Texture(palette, dpiScale) ?? PanelTheme.Scanlines(palette, dpiScale), PanelTheme.RimBrush(palette), effects.RimWidth,
            PanelTheme.ColorOf("shadow"), effects.ShadowBlur, effects.ShadowDepth, effects.ShadowOpacity,
            PanelTheme.Hex("textPrimary"), PanelTheme.Hex("textSecondary"), PanelTheme.Hex("textTertiary"),
            PanelTheme.Hex("fill"), PanelTheme.Hex("hover"), PanelTheme.Hex("pressed"), PanelTheme.Hex("accent"), PanelTheme.Hex("onAccent"), PanelTheme.Hex("danger"),
            // A theme without depth whose shadow is drawn anyway gives its surface a glow rather than a shadow.
            Glow: effects.ShadowDepth <= 0 && effects.ShadowOpacity > 0, HighContrast: false)
        {
            Pixel = effects.Pixel, Cut = effects.Cut, Decor = effects.Decor is { Hatch: not null } or { Tab: not null } or { Frame: not null } ? effects.Decor : null, Bevel = PanelTheme.BevelBrush(palette), Edge = glass ? PanelTheme.EdgeBrush(palette) : null, Glass = glass
        };
        _cached = (PanelTheme.Generation, glass, dpiScale, look);
        return look;
    }

    private static NoticeLook HighContrastLook()
    {
        static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        string text = Hex(SystemColors.WindowTextColor);
        return new(SystemColors.WindowColor, null, null, SystemColors.WindowTextBrush, 2, Colors.Transparent, 0, 0, 0,
            text, text, Hex(SystemColors.GrayTextColor), Hex(SystemColors.ControlColor), Hex(SystemColors.HighlightColor), Hex(SystemColors.HighlightColor),
            Hex(SystemColors.HighlightColor), Hex(SystemColors.HighlightTextColor), text, Glow: false, HighContrast: true);
    }

    // An application's color as drawn on this theme.
    public Color AppColor(Color color) => HighContrast ? SystemColors.HighlightColor : PanelTheme.Readable(color);
}

// The notice's shape: the island's outline (shoulders and corners as IslandBorder draws them), filled and finished as the theme
// finishes the island. On top of that it can draw the indicator's flat color, which fades out as a notice grows from the
// indicator and back in as it returns, so the change of material happens on the island's own outline. Its child is clipped to
// the outline.
internal sealed class NoticeSurface : System.Windows.Controls.Decorator
{
    public static readonly DependencyProperty TopRadiusProperty = Register(nameof(TopRadius), 0d);
    public static readonly DependencyProperty BottomRadiusProperty = Register(nameof(BottomRadius), 0d);
    public static readonly DependencyProperty ShoulderProperty = Register(nameof(Shoulder), 0d);
    public static readonly DependencyProperty IslandOpacityProperty = Register(nameof(IslandOpacity), 0d);
    // The surface's opacity: below 1 while a held blur lies under glass (GlassBackdrop.Held).
    public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(nameof(FillOpacity), typeof(double), typeof(NoticeSurface),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    // How far each end of the outline is drawn in from the surface's bounds, while the capsule's ends give way to the droplets
    // leaving them (NoticeLiquid). The surface keeps its size and place; only its outline, and so its clip, draws in.
    public static readonly DependencyProperty InsetLeftProperty = Register(nameof(InsetLeft), 0d);
    public static readonly DependencyProperty InsetRightProperty = Register(nameof(InsetRight), 0d);
    public static readonly DependencyProperty IslandColorProperty = DependencyProperty.Register(nameof(IslandColor), typeof(Color), typeof(NoticeSurface),
        new FrameworkPropertyMetadata(Colors.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(name, typeof(double), typeof(NoticeSurface),
        new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double TopRadius { get => (double)GetValue(TopRadiusProperty); set => SetValue(TopRadiusProperty, value); }
    public double BottomRadius { get => (double)GetValue(BottomRadiusProperty); set => SetValue(BottomRadiusProperty, value); }
    public double Shoulder { get => (double)GetValue(ShoulderProperty); set => SetValue(ShoulderProperty, value); }
    public double IslandOpacity { get => (double)GetValue(IslandOpacityProperty); set => SetValue(IslandOpacityProperty, value); }
    public double FillOpacity { get => (double)GetValue(FillOpacityProperty); set => SetValue(FillOpacityProperty, value); }
    public double InsetLeft { get => (double)GetValue(InsetLeftProperty); set => SetValue(InsetLeftProperty, value); }
    public double InsetRight { get => (double)GetValue(InsetRightProperty); set => SetValue(InsetRightProperty, value); }
    public Color IslandColor { get => (Color)GetValue(IslandColorProperty); set => SetValue(IslandColorProperty, value); }
    // Drawn with the look's material, or only its fill (Shadow: the black copy that casts a shadow behind the notice).
    public NoticeLook? Look { get => _look; set { _look = value; _fill = null; _rim = null; _edge = null; _tabBrush = null; _framePen = null; _frame = null; _key = null; InvalidateArrange(); InvalidateVisual(); } }
    public Brush? FillOverride { get => _fillOverride; set { _fillOverride = value; _fill = null; InvalidateVisual(); } }
    public bool MaterialHidden { get; set; }

    private NoticeLook? _look;
    private Brush? _fill, _fillOverride;
    private Pen? _rim;
    private Pen[]? _edge;
    private SolidColorBrush? _islandBrush, _tabBrush;
    private Pen? _framePen;
    private Geometry? _frame;
    private (Size, double, double, double, double, double, double, double)? _key;
    private Geometry _outline = Geometry.Empty;
    private (Geometry Light, Geometry Shade)? _bevel;

    // The island's outline, stepped for a pixel theme as the island's is.
    public Geometry Outline()
    {
        double step = IslandBorder.PixelStep(_look?.Pixel ?? 0, this);
        double left = Math.Max(0, InsetLeft), right = Math.Max(0, InsetRight);
        // A pixel theme's outline stays on its grid while the ends give way.
        if (step > 0) { left = Math.Round(left / step) * step; right = Math.Round(right / step) * step; }
        var key = (RenderSize, TopRadius, BottomRadius, Shoulder, step, step > 0 ? 0 : _look?.Cut ?? 0, left, right);
        if (_key == key) return _outline;
        _key = key; _frame = null;
        // Drawn in at its ends: the outline (and bevel) of the narrower body, moved to where the left end now is.
        (_outline, _bevel) = IslandBorder.Shape(Math.Max(1, RenderSize.Width - left - right), RenderSize.Height, Shoulder, new CornerRadius(TopRadius, TopRadius, BottomRadius, BottomRadius), 0, step,
            _look?.RimWidth ?? 0, VisualTreeHelper.GetDpi(this).PixelsPerDip, _look?.Cut ?? 0);
        if (left > 0 || right > 0)
        {
            _outline = Moved(_outline, left);
            if (_bevel is { } bevel) _bevel = (Moved(bevel.Light, left), Moved(bevel.Shade, left));
        }
        return _outline;
    }

    private static Geometry Moved(Geometry geometry, double x)
    {
        var moved = geometry.Clone();
        moved.Transform = new TranslateTransform(x, 0);
        moved.Freeze();
        return moved;
    }

    // A pixel theme's steps are whole device pixels, so a new scale needs a new outline.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _key = null; InvalidateArrange(); InvalidateVisual();
    }

    protected override Geometry GetLayoutClip(Size layoutSlotSize) => Outline();

    // Screen readers and UI Automation see the notice as one named group holding its text and buttons.
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new NoticeSurfacePeer(this);

    private sealed class NoticeSurfacePeer(NoticeSurface owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(NoticeSurface);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var outline = Outline();
        if (outline.IsEmpty() || _look is not { } look) return;
        _fill ??= Freeze(_fillOverride ?? new SolidColorBrush(look.Surface));
        double fillOpacity = Math.Clamp(FillOpacity, 0, 1);
        if (fillOpacity < 1) drawing.PushOpacity(fillOpacity);
        drawing.DrawGeometry(_fill, null, outline);
        if (fillOpacity < 1) drawing.Pop();
        if (!MaterialHidden)
        {
            if (look.Gradient is { } gradient) drawing.DrawGeometry(gradient, null, outline);
            if (look.Pattern is { } pattern) drawing.DrawGeometry(pattern, null, outline);
            if (look.Edge is { } edge) IslandBorder.DrawEdge(drawing, _edge ??= IslandBorder.EdgePens(edge), outline);
            if (look.RimWidth > 0 && _bevel is { } bevel && look.Bevel is { } shade)
            {
                drawing.DrawGeometry(look.Rim, null, bevel.Light);
                drawing.DrawGeometry(shade, null, bevel.Shade);
            }
            else if (look.RimWidth > 0)
            {
                // The clip removes the outer half of the stroke, so the pen is twice the rim's width (as IslandBorder's).
                _rim ??= Freeze(new Pen(look.Rim, look.RimWidth * 2));
                drawing.DrawGeometry(null, _rim, outline);
            }
            if (look.Decor is { } marks) Marks(drawing, outline, marks);
        }
        if (IslandOpacity > 0)
        {
            if (_islandBrush is null || _islandBrush.Color != IslandColor) _islandBrush = Freeze(new SolidColorBrush(IslandColor));
            drawing.PushOpacity(Math.Clamp(IslandOpacity, 0, 1));
            drawing.DrawGeometry(_islandBrush, null, outline);
            drawing.Pop();
        }
    }

    // The notice's own marks, on its outline: the signal color as a bar down the left edge (a notice is what is waiting to be read) and
    // the strip of hatching at the right end of the bottom edge, as the island's open panel has them.
    private void Marks(DrawingContext drawing, Geometry outline, PanelDecor marks)
    {
        drawing.PushClip(outline);
        if (marks.Tab is { } tab)
        {
            _tabBrush ??= Freeze(new SolidColorBrush(Color.FromArgb(tab.A, tab.R, tab.G, tab.B)));
            drawing.DrawRectangle(_tabBrush, null, new Rect(0, 0, 4, RenderSize.Height));
        }
        if (marks.Frame is { } frame && RenderSize.Height > IslandDecor.MarksMinHeight)
        {
            _framePen ??= Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(frame.A, frame.R, frame.G, frame.B))), 1) { LineJoin = PenLineJoin.Miter });
            var corners = new CornerRadius(TopRadius, TopRadius, BottomRadius, BottomRadius);
            _frame ??= Freeze((_look?.Cut ?? 0) > 0 ? IslandBorder.BuildCut(RenderSize.Width, RenderSize.Height, Shoulder, corners, IslandDecor.FrameInset, _look!.Cut)
                : IslandBorder.BuildOutline(RenderSize.Width, RenderSize.Height, Shoulder, corners, IslandDecor.FrameInset));
            drawing.PushClip(new RectangleGeometry(new Rect(0, IslandDecor.FrameInset + 1, RenderSize.Width, Math.Max(0, RenderSize.Height - IslandDecor.FrameInset - 1))));
            drawing.DrawGeometry(null, _framePen, _frame);
            drawing.Pop();
        }
        double x = RenderSize.Width - Shoulder - BottomRadius - 4 - IslandDecor.HatchWidth;
        if (marks.Hatch is { } hatch && x > 12 && RenderSize.Height > IslandDecor.MarksMinHeight)
        {
            drawing.PushTransform(new TranslateTransform(x, RenderSize.Height - IslandDecor.MarkBand));
            drawing.DrawDrawing(IslandDecor.HatchStrip(Color.FromArgb(hatch.A, hatch.R, hatch.G, hatch.B)));
            drawing.Pop();
        }
        drawing.Pop();
    }

    private static T Freeze<T>(T freezable) where T : Freezable { if (freezable.CanFreeze) freezable.Freeze(); return freezable; }
}
