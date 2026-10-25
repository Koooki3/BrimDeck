using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static BrimDeck.Tracks;

namespace BrimDeck;

// What a theme's material asks of the island beyond its colors (PanelTheme.Dress): glass lets the desktop through, blurred where
// Windows can (GlassBackdrop), so its text cannot use ClearType, and the copy that casts the shadow, lying under the island,
// would show through it; pixel art draws its text unsmoothed and casts a stepped shadow.
public partial class MainWindow
{
    private (double X, double Y, Size Size, double Shoulder, CornerRadius Corners)? _shadowCutKey;
    private bool _shadowCut;
    // The blur under glass (GlassBackdrop), made with the window; it stays out of the way for other themes.
    private GlassBackdrop? _glass;

    private void ApplyMaterial()
    {
        if (_glass is null)
        {
            _glass = new GlassBackdrop(this, Island);
            // Over a held blur the tint thins with it, frame by frame. Liquid glass draws its own tint: the island's surface
            // steps aside under it altogether (GlassBackdrop.SurfaceAside), whatever the blur's tint would be.
            _glass.Held += held =>
            {
                _surface.Opacity = GlassBackdrop.SurfaceOpacity(_glass.SurfaceAside, held, _surface.Color.A / 255d);
                LiquidEdge(_glass.SurfaceAside);
            };
            // The panel opening takes the island out of a press at once: a squash or spring left on the open panel would hold
            // it off the screen's top edge.
            Island.SizeChanged += (_, _) => { if (_expanded) ResetJelly(); };
            _glass.LiquidTint = () => _surface.Color.A / 255d;
            // Automatic liquid glass takes its look from what lies behind the island (App.BackdropMeasured).
            _glass.AutoLook = () => _app.AutoLookFrom(this);
            _glass.Look += (dark, instant) => _app.TurnGlass(this, dark, instant);
            _glass.Crossed += () => _app.GlassCrossed(this);
            // Liquid glass lights its rim from the pointer and presses with it: where the pointer is over the island, and
            // whether the button is down (seen even when a button under it handles the click).
            Island.MouseMove += (_, e) => _glass.Pointer(e.GetPosition(Island), e.LeftButton == MouseButtonState.Pressed);
            Island.MouseLeave += (_, _) => { _glass.Pointer(null, false); Jelly(false); };
            Island.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) => { _glass.Pointer(e.GetPosition(Island), true); Jelly(true); }), true);
            Island.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) => { _glass.Pointer(e.GetPosition(Island), false); Jelly(false); }), true);
        }
        _glass.Update();
        bool glass = PanelTheme.GlassShown;
        var palette = PanelTheme.Current;
        // ClearType needs an opaque background under the text; over glass it washes the text out (PanelTheme.ClearType).
        if (Island.Child is Grid content) RenderOptions.SetClearTypeHint(content, PanelTheme.ClearType);
        if (palette.Effects.Pixel > 0) TextOptions.SetTextRenderingMode(Island, TextRenderingMode.Aliased); else Island.ClearValue(TextOptions.TextRenderingModeProperty);
        if (IslandShadow.Children.OfType<IslandBorder>().FirstOrDefault() is { } caster) caster.Pixel = palette.Effects.Pixel;
        if (glass == _shadowCut) return;
        _shadowCut = glass;
        if (glass) { Island.LayoutUpdated += CutShadow; CutShadow(null, EventArgs.Empty); }
        else { Island.LayoutUpdated -= CutShadow; ShadowHost.Clip = null; _shadowCutKey = null; }
    }

    // The press of liquid-glass-web's glass (xiaofangstudio, MIT): pressed, it squashes to scale(1 + k, 1 - .45 k) about its
    // middle, going down as every press in BrimDeck does (Soft.Pressed: Soft.Press on the monitor's curve); released, it springs
    // back over JellyBackMs on the source's 1 - e^(-5t) cos 9t (SpringEase, which lands exactly on 1). k is the source's .1 for
    // a glass as wide as its buttons (JellyWidth), less on a wider island. Only the collapsed island (a press in the open panel
    // is a press of one of its controls), only over liquid glass, and not with animations off. The glass follows the island's
    // shape through its transform (GlassBackdrop.Measure), as it follows the boot's dot. Each animation only covers the way and
    // ends without holding the scale (Soft.Glide's pattern), so the scale left behind is exactly the one set: 1 once let go.
    private ScaleTransform? _jelly;
    private bool _jellyDown;
    private const double JellyK = .1, JellyWidth = 140, JellyBackMs = 500;
    private static readonly SpringEase JellySpring = Frozen(new SpringEase());
    private static SpringEase Frozen(SpringEase ease) { ease.Freeze(); return ease; }

    // Out of any press at once, no spring: the panel opening.
    private void ResetJelly()
    {
        _jellyDown = false;
        if (_jelly is null || _jelly.ScaleX == 1 && _jelly.ScaleY == 1 && !_jelly.HasAnimatedProperties) return;
        SetImmediate(_jelly, ScaleTransform.ScaleXProperty, 1d);
        SetImmediate(_jelly, ScaleTransform.ScaleYProperty, 1d);
    }

    private void Jelly(bool down)
    {
        if (down == _jellyDown) return;
        if (down && (_expanded || _glass?.LiquidShown != true || !Soft.Enabled)) return;
        if (!down && _expanded) { ResetJelly(); return; }
        _jellyDown = down;
        if (_jelly is null)
        {
            if (_dotSquash is null || _dotLift is null)
            {
                _dotSquash = new ScaleTransform(); _dotLift = new TranslateTransform();
                Island.RenderTransformOrigin = new Point(.5, 1);
                Island.RenderTransform = new TransformGroup { Children = { _dotSquash, _dotLift } };
            }
            if (Island.RenderTransform is not TransformGroup group) return;
            _jelly = new ScaleTransform();
            group.Children.Insert(0, _jelly);
        }
        // About the middle: the group's origin is the bottom middle (the dot's floor).
        _jelly.CenterY = -Island.ActualHeight / 2;
        if (down)
        {
            double k = JellyK * Math.Min(1, JellyWidth / Math.Max(1, Island.ActualWidth));
            Soft.Glide(_jelly, ScaleTransform.ScaleXProperty, 1 + k, true, Soft.Press);
            Soft.Glide(_jelly, ScaleTransform.ScaleYProperty, 1 - .45 * k, true, Soft.Press);
            _glass?.Follow(Soft.Press + 50);
            return;
        }
        Back(_jelly, ScaleTransform.ScaleXProperty);
        Back(_jelly, ScaleTransform.ScaleYProperty);
        _glass?.Follow(JellyBackMs + 50);

        // From where the press has it now (perhaps still going down) back to 1, the value set before the spring starts.
        static void Back(ScaleTransform jelly, DependencyProperty property)
        {
            double from = (double)jelly.GetValue(property);
            jelly.BeginAnimation(property, null);
            jelly.SetValue(property, 1d);
            if (from != 1)
                jelly.BeginAnimation(property, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(JellyBackMs)) { EasingFunction = JellySpring, FillBehavior = FillBehavior.Stop });
        }
    }

    // Automatic liquid glass turning: the glass eases toward the new look ahead of the theme (GlassBackdrop.Lead).
    internal void LeadGlass(bool? dark) => _glass?.Lead(dark);
    // Whether this island's liquid glass can be shown (GlassBackdrop.LiquidUsable); true before the glass is made.
    internal bool LiquidUsable => _glass?.LiquidUsable != false;

    // Liquid glass draws its own rim light and edge, following the pointer; the island's painted rim and edge light would lie
    // over it as a stroke (a white ring on dark glass, a 3D button's edge), so they step aside while it is shown and come back
    // with the blur. PanelTheme.Dress sets them again on a theme change, and the next frame takes them away again.
    private bool _liquidEdge;
    private void LiquidEdge(bool shown)
    {
        if (shown)
        {
            if (Island.RimWidth == 0 && Island.EdgeBrush is null) return;
            Island.RimWidth = 0; Island.EdgeBrush = null; _liquidEdge = true;
            _ambient?.Update();
        }
        else if (_liquidEdge)
        {
            _liquidEdge = false;
            var palette = PanelTheme.Current;
            Island.RimWidth = palette.Effects.RimWidth;
            Island.EdgeBrush = PanelTheme.GlassShown ? PanelTheme.EdgeBrush(palette) : null;
            _ambient?.Update();
        }
    }

    // Glass: the shadow is drawn only outside the island. Layout runs on every frame the island changes size or place, and
    // only then; the clip is made again only when the outline has moved.
    private void CutShadow(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(Island) is null || !Island.IsVisible) return;
        var at = Island.TranslatePoint(new Point(), ShadowHost);
        var key = (at.X, at.Y, Island.RenderSize, Island.ShoulderRadius, Island.CornerRadius);
        if (_shadowCutKey == key) return;
        _shadowCutKey = key;
        var outline = IslandBorder.BuildOutline(Island.RenderSize.Width, Island.RenderSize.Height, Island.ShoulderRadius, Island.CornerRadius, 0,
            IslandBorder.PixelStep(Island.Pixel, Island)).Clone();
        outline.Transform = new TranslateTransform(at.X, at.Y);
        // Everything the shadow can reach, less the island.
        var reach = new RectangleGeometry(new Rect(-4096, -4096, 8192, 8192));
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude, reach, outline);
        clip.Freeze();
        ShadowHost.Clip = clip;
    }
}
