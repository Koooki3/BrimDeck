using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrimDeck;

// What a theme's material asks of the island beyond its colors (PanelTheme.Dress): glass lets the desktop through, so its text
// cannot use ClearType, and the copy that casts the shadow, lying under the island, would show through it; pixel art draws
// its text unsmoothed and casts a stepped shadow.
public partial class MainWindow
{
    private (double X, double Y, Size Size, double Shoulder, CornerRadius Corners)? _shadowCutKey;
    private bool _shadowCut;

    private void ApplyMaterial()
    {
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
