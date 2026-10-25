using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
            // Over a held blur the tint thins with it, frame by frame.
            // Liquid glass draws the tint itself, so the surface steps aside under it as the blur's tint thins.
            _glass.Held += held =>
            {
                _surface.Opacity = _glass.LiquidShown ? 1 - held : 1 + held * (PanelTheme.HeldOpacity(_surface.Color.A / 255d) - 1);
                LiquidEdge(_glass.LiquidShown);
            };
            _glass.LiquidTint = () => _surface.Color.A / 255d;
            // Automatic liquid glass takes its look from what lies behind the island (App.BackdropMeasured).
            _glass.AutoLook = () => _app.AutoLookFrom(this);
            _glass.Look += (dark, instant) => _app.TurnGlass(this, dark, instant);
            _glass.Crossed += () => _app.GlassCrossed(this);
            // Liquid glass lights its rim from the pointer and presses with it: where the pointer is over the island, and
            // whether the button is down (seen even when a button under it handles the click).
            Island.MouseMove += (_, e) => _glass.Pointer(e.GetPosition(Island), e.LeftButton == MouseButtonState.Pressed);
            Island.MouseLeave += (_, _) => _glass.Pointer(null, false);
            Island.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) => _glass.Pointer(e.GetPosition(Island), true)), true);
            Island.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) => _glass.Pointer(e.GetPosition(Island), false)), true);
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

    // Automatic liquid glass turning: the glass eases toward the new look ahead of the theme (GlassBackdrop.Lead).
    internal void LeadGlass(bool dark) => _glass?.Lead(dark);

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
        }
        else if (_liquidEdge)
        {
            _liquidEdge = false;
            var palette = PanelTheme.Current;
            Island.RimWidth = palette.Effects.RimWidth;
            Island.EdgeBrush = PanelTheme.GlassShown ? PanelTheme.EdgeBrush(palette) : null;
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
