using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;

internal static class PanelOpacityChecks
{
    internal static void Run(Action<string, bool> check)
    {
        foreach (double scale in new[] { 1d, 1.5 })
        {
            var hidden = Render(0, 0, false, scale);
            var backdrop = Render(1, 0, false, scale);
            var rim = Render(0, 1, false, scale);
            var half = Render(.5, .5, false, scale);
            var composition = Render(1, 0, true, scale);
            check($"Panel backdrop opacity at {scale:P0} is independent of the rim",
                hidden.Center <= 1 && backdrop.Center == 255 && rim.Center == hidden.Center
                && rim.Edge == 255 && rim.Edge > hidden.Edge && half.Center is >= 127 and <= 129
                && half.Edge > half.Center && half.Edge < 255);
            check($"A composited backdrop at {scale:P0} leaves foreground to WPF, without an opaque WPF backing",
                composition.Center == hidden.Center && composition.Edge == hidden.Edge
                && composition.Content == 255 && hidden.Content == 255);
        }
    }

    private static (byte Center, byte Edge, byte Content) Render(double surface, double rim, bool composition, double scale)
    {
        const int width = 96, height = 48;
        var island = new IslandBorder
        {
            Width = width, Height = height, Background = Brushes.Black, RimBrush = Brushes.Red,
            RimWidth = 3, MaterialOpacity = 1, SurfaceOpacity = surface, RimOpacity = rim,
            CompositionBackdrop = composition, SettledClip = true,
            Child = new Border { Width = 12, Height = 12, Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        island.Measure(new Size(width, height));
        island.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(island);
        byte Alpha(int x, int y)
        {
            var bytes = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)(x * scale), (int)(y * scale), 1, 1), bytes, 4, 0);
            return bytes[3];
        }
        return (Alpha(20, 10), Alpha(1, height / 2), Alpha(width / 2, height / 2));
    }
}
