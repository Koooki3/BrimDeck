using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrimDeck.LiquidGlass;

// Round 16, step 1: the acrylic body's noise, as Windows' Acrylic lays it over (microsoft-ui-xaml, MIT: AcrylicBrush's noise
// texture at 2 %): a fine grey grain that keeps a wide, smooth blur from banding and gives it the frosted surface's texture.
// One tile of Tile x Tile device pixels, repeated, drawn by the island itself (its pattern layer, IslandBorder.Pattern) over the
// glass in its exact outline. WPF draws it: no device of ours (an earlier cut kept a Direct3D 11 device and a composition swap
// chain alive for it, with their driver threads, for as long as the app ran). Seeded, so the offscreen sheets draw the same
// grain (Pixels).
internal static class AcrylicNoise
{
    public const int Tile = 256;
    // 2 % as Windows' Acrylic has it, halved for a lighter frost (user 10-27, "磨砂质感做淡一点").
    public const double Opacity = .01;
    private static readonly Dictionary<double, Brush> Brushes = [];

    // The tile's premultiplied BGRA pixels: a grey of random lightness at Opacity, from the middle half of the range (the
    // whole range at 200 % weighed on the page checks' reading comfort as detail behind the text, 1.10 to 1.12).
    public static byte[] Pixels()
    {
        var random = new Random(1983);
        var pixels = new byte[Tile * Tile * 4];
        byte alpha = (byte)Math.Round(Opacity * 255);
        for (int i = 0; i < Tile * Tile; i++)
        {
            byte v = (byte)Math.Round((64 + random.Next(129)) * Opacity);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = v;
            pixels[i * 4 + 3] = alpha;
        }
        return pixels;
    }

    // The tiled brush for a display scale: one texel to a device pixel, from the island's corner. Made once per scale, frozen.
    public static Brush Brush(double dpiScale)
    {
        double scale = dpiScale > 0 ? dpiScale : 1;
        if (Brushes.TryGetValue(scale, out var made)) return made;
        var bitmap = BitmapSource.Create(Tile, Tile, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, Pixels(), Tile * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, Tile / scale, Tile / scale),
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        Brushes[scale] = brush;
        return brush;
    }

    // Whether a brush is one of these (the island's pattern to hand back to the theme's).
    public static bool Is(Brush? brush) => brush is not null && Brushes.ContainsValue(brush);
}
