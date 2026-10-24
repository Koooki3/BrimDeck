using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrimDeck;

// A change of theme while the island is on screen dissolves from the old look into the new one instead of snapping: a picture
// of the island as it was lies over it and fades out while the new colors are already drawn underneath. A theme's day and night
// looks trade places slowly (DaylightFadeMs), as dusk does; any other change takes about a hover's time. Nothing moves with
// animations off; the picture is taken once, so the fade costs one bitmap and an opacity animation.
public partial class MainWindow
{
    private const int DaylightFadeMs = 1600, ThemeFadeMs = 320;
    private Image? _themeFade;

    // PanelTheme.Changing: the old look is still on screen. The new one is drawn on the frames that follow.
    private void BeginThemeFade(bool daylight)
    {
        EndThemeFade();
        if (!IsVisible || !Soft.Enabled || _preview || Island.ActualWidth < 1 || Island.ActualHeight < 1 || PresentationSource.FromVisual(Island) is null) return;
        var dpi = VisualTreeHelper.GetDpi(Island);
        double width = Island.ActualWidth, height = Island.ActualHeight;
        var picture = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Drawn through a brush so the island's own place in the window does not offset the picture.
        var copy = new DrawingVisual();
        using (var dc = copy.RenderOpen()) dc.DrawRectangle(new VisualBrush(Island) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        picture.Render(copy);
        picture.Freeze();
        var at = Island.TranslatePoint(new Point(), HoverZone);
        _themeFade = new Image
        {
            Source = picture, Width = width, Height = height, Stretch = Stretch.Fill, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(at.X, at.Y, 0, 0)
        };
        RenderOptions.SetBitmapScalingMode(_themeFade, BitmapScalingMode.NearestNeighbor);
        Panel.SetZIndex(_themeFade, int.MaxValue);
        HoverZone.Children.Add(_themeFade);
        var fade = Soft.Fade(1, 0, daylight ? DaylightFadeMs : ThemeFadeMs, Soft.InOut);
        var image = _themeFade;
        fade.Completed += (_, _) => { if (ReferenceEquals(_themeFade, image)) EndThemeFade(); };
        image.BeginAnimation(OpacityProperty, fade);
    }

    // Also when the island changes shape or hides: the picture would no longer lie on it.
    private void EndThemeFade()
    {
        if (_themeFade is not { } image) return;
        _themeFade = null;
        image.BeginAnimation(OpacityProperty, null);
        HoverZone.Children.Remove(image);
    }
}
