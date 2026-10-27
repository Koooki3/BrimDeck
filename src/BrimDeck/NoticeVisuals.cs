using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// A notice in the panel theme the user chose: it is the island itself, dressed as PanelTheme dresses the island (surface color,
// gradient, scan lines, rim, shadow or glow), with the theme's text, fill, hover and accent colors for its content. Each theme
// therefore gets its own notices without a setting of their own: a glowing theme's notice arrives with one flash of its glow,
// a theme with depth casts its shadow, and colors chosen outside the theme (an application's color, the status marks) are
// moved just far enough to read on its surface. High contrast replaces everything with system colors and drops glow.
internal sealed record NoticeLook(
    Color Surface, Brush? Gradient, Brush? Pattern, Brush Rim, double RimWidth,
    Color Shadow, double ShadowBlur, double ShadowDepth, double ShadowOpacity,
    string Primary, string Secondary, string Tertiary, string Fill, string Hover, string Pressed, string Accent, string OnAccent, string Danger,
    bool Glow, bool HighContrast)
{
    private static (int Generation, double Dpi, NoticeLook Look)? _cached;

    public static NoticeLook Current(double dpiScale = 1)
    {
        if (SystemParameters.HighContrast) return HighContrastLook();
        if (_cached is { } cached && cached.Generation == PanelTheme.Generation && cached.Dpi == dpiScale) return cached.Look;
        var palette = PanelTheme.Current;
        var effects = palette.Effects;
        var look = new NoticeLook(
            PanelTheme.ColorOf("surface"), PanelTheme.SurfaceGradient(palette), PanelTheme.Scanlines(palette, dpiScale), PanelTheme.RimBrush(palette), effects.RimWidth,
            PanelTheme.ColorOf("shadow"), effects.ShadowBlur, effects.ShadowDepth, effects.ShadowOpacity,
            PanelTheme.Hex("textPrimary"), PanelTheme.Hex("textSecondary"), PanelTheme.Hex("textTertiary"),
            PanelTheme.Hex("fill"), PanelTheme.Hex("hover"), PanelTheme.Hex("pressed"), PanelTheme.Hex("accent"), PanelTheme.Hex("onAccent"), PanelTheme.Hex("danger"),
            // A theme without depth whose shadow is drawn anyway gives its surface a glow rather than a shadow.
            Glow: effects.ShadowDepth <= 0 && effects.ShadowOpacity > 0, HighContrast: false);
        _cached = (PanelTheme.Generation, dpiScale, look);
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
    public static readonly DependencyProperty IslandColorProperty = DependencyProperty.Register(nameof(IslandColor), typeof(Color), typeof(NoticeSurface),
        new FrameworkPropertyMetadata(Colors.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(name, typeof(double), typeof(NoticeSurface),
        new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double TopRadius { get => (double)GetValue(TopRadiusProperty); set => SetValue(TopRadiusProperty, value); }
    public double BottomRadius { get => (double)GetValue(BottomRadiusProperty); set => SetValue(BottomRadiusProperty, value); }
    public double Shoulder { get => (double)GetValue(ShoulderProperty); set => SetValue(ShoulderProperty, value); }
    public double IslandOpacity { get => (double)GetValue(IslandOpacityProperty); set => SetValue(IslandOpacityProperty, value); }
    public Color IslandColor { get => (Color)GetValue(IslandColorProperty); set => SetValue(IslandColorProperty, value); }
    // Drawn with the look's material, or only its fill (Shadow: the black copy that casts a shadow behind the notice).
    public NoticeLook? Look { get => _look; set { _look = value; _fill = null; _rim = null; InvalidateVisual(); } }
    public Brush? FillOverride { get => _fillOverride; set { _fillOverride = value; _fill = null; InvalidateVisual(); } }
    public bool MaterialHidden { get; set; }

    private NoticeLook? _look;
    private Brush? _fill, _fillOverride;
    private Pen? _rim;
    private SolidColorBrush? _islandBrush;
    private (Size, double, double, double)? _key;
    private Geometry _outline = Geometry.Empty;

    public Geometry Outline()
    {
        var key = (RenderSize, TopRadius, BottomRadius, Shoulder);
        if (_key == key) return _outline;
        _key = key;
        return _outline = IslandBorder.BuildOutline(RenderSize.Width, RenderSize.Height, Shoulder, new CornerRadius(TopRadius, TopRadius, BottomRadius, BottomRadius));
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
        drawing.DrawGeometry(_fill, null, outline);
        if (!MaterialHidden)
        {
            if (look.Gradient is { } gradient) drawing.DrawGeometry(gradient, null, outline);
            if (look.Pattern is { } pattern) drawing.DrawGeometry(pattern, null, outline);
            if (look.RimWidth > 0)
            {
                // The clip removes the outer half of the stroke, so the pen is twice the rim's width (as IslandBorder's).
                _rim ??= Freeze(new Pen(look.Rim, look.RimWidth * 2));
                drawing.DrawGeometry(null, _rim, outline);
            }
        }
        if (IslandOpacity > 0)
        {
            if (_islandBrush is null || _islandBrush.Color != IslandColor) _islandBrush = Freeze(new SolidColorBrush(IslandColor));
            drawing.PushOpacity(Math.Clamp(IslandOpacity, 0, 1));
            drawing.DrawGeometry(_islandBrush, null, outline);
            drawing.Pop();
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable { if (freezable.CanFreeze) freezable.Freeze(); return freezable; }
}
