namespace BrimDeck.Core;

// Conversions between RGB and HSV (hue in degrees 0-360, saturation and value 0-1) for the settings color picker.
public static class ColorMath
{
    public static (byte R, byte G, byte B) FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360; s = Math.Clamp(s, 0, 1); v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x)
        };
        static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return (Byte(r + m), Byte(g + m), Byte(b + m));
    }

    // Grays have no hue and report 0; callers that track a hue keep their own for them.
    public static (double H, double S, double V) ToHsv(byte red, byte green, byte blue)
    {
        double r = red / 255d, g = green / 255d, b = blue / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, max == 0 ? 0 : d / max, max);
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}
