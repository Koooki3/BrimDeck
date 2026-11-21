namespace BrimDeck.Core;

// The acrylic recipe of liquid glass as numbers, apart from Composition, so it can be checked offscreen. Windows' Acrylic
// (microsoft-ui-xaml, MIT: AcrylicBrush) saturates the blur, blends the tint's luminosity in at the luminosity opacity (the
// backdrop's hue and saturation kept), lays the tint over at its opacity and adds a little noise; every step but the noise is
// linear, so here they are one color matrix (Matrix). The looks: the user's shell acrylic (the tint #232323 at 23 %, the
// saturation kept) for the dark look and its light counterpart (#F3F3F3), the tint layer thinned to .20 for both.
//   The luminosity opacity (the "veil") is what the backdrop's brightness asks for (LuminosityFor) and never under the base for
// the opening and the user's transparency (GlassMaterial.LuminosityOpacity). Both are continuous: the material no longer has
// to come in steps for a cache of compiled effects, because one persistent effect graph shows every value (GlassEffect.Graph).
//   Why one graph can: saturation about the luma keeps the luma, so the recipe is out = A + (1 - k) L (T - g), where A is the
// look at no veil ((1 - k) saturated + k tint), g the backdrop's luma, T the look's luminosity target, k the tint opacity and
// L the luminosity opacity. Both looks share A up to the tint (a constant), so any blend of the two looks at any two veils by
// a palette share p is out = A_dark + Slope g + Offset (Coefficients): two fixed colour matrices (A_dark and the luma) and an
// arithmetic composite whose two amounts are the graph's only moving numbers. Exact wherever A_dark stays inside 0..1 (it can
// leave it only for vivid colours under the 1.3 saturation, and only by the saturation's own overshoot).
public static class GlassRecipe
{
    public readonly record struct Look(double Saturation, double LuminosityOpacity, double Luminosity, double TintR, double TintG, double TintB, double TintOpacity);

    public const double LuminosityBase = GlassMaterial.DefaultExpandedVeil, LuminosityMost = GlassMaterial.MaximumVeil;
    public const double LuminosityCapsule = GlassMaterial.DefaultCollapsedVeil;
    public static readonly Look Dark = new(1.3, LuminosityBase, .1373, .1373, .1373, .1373, .20);
    public static readonly Look Light = new(1.3, LuminosityBase, .953, .953, .953, .953, .20);
    public static Look For(bool dark) => dark ? Dark : Light;

    // The arithmetic composite's amounts for each look's veil and the light look's share p (0 dark, 1 light):
    // out = A_dark + Slope g + Offset.
    public static (double Slope, double Offset) Coefficients(double darkVeil, double lightVeil, double palette)
    {
        double p = Math.Clamp(palette, 0, 1), k = Dark.TintOpacity;
        double dark = Math.Clamp(darkVeil, 0, 1), light = Math.Clamp(lightVeil, 0, 1);
        double slope = -(1 - k) * ((1 - p) * dark + p * light);
        double offset = (1 - k) * ((1 - p) * dark * Dark.Luminosity + p * light * Light.Luminosity) + p * k * (Light.TintR - Dark.TintR);
        return (slope, offset);
    }
    // The graph's two fixed matrices: the dark look at no veil, and the backdrop's luma (Rec. 709) in every channel.
    public static float[] BaseMatrix() => Matrix(Dark with { LuminosityOpacity = 0 });
    public static float[] LumaMatrix()
    {
        double[] w = [.2126, .7152, .0722];
        var m = new float[20];
        for (int i = 0; i < 3; i++)
            for (int o = 0; o < 3; o++)
                m[i * 4 + o] = (float)w[i];
        m[15] = 1;
        return m;
    }

    // The lightness the body may reach for the light look's dark text (sRGB luma; the dark look's limit is
    // GlassMaterial.DarkBackdropLimit, which follows the transparency).
    public const double LightLeast = .82;

    // The luminosity opacity the text needs over a backdrop whose brightest tail reaches bright (the dark look) or whose darkest
    // tail reaches dark (the light look), from the recipe's lightness ((1 - L) b + L t)(1 - k) + k t: never under zero, at most
    // LuminosityMost. Continuous (no steps).
    public static double LuminosityFor(bool darkLook, double bright, double dark, int transparency = GlassMaterial.DefaultTransparency)
    {
        if (!double.IsFinite(bright) || !double.IsFinite(dark)) return LuminosityMost;
        var look = For(darkLook);
        double t = look.TintOpacity, target = look.Luminosity, b = Math.Clamp(darkLook ? bright : dark, 0, 1);
        double need = darkLook
            ? (b - target) * (1 - t) <= 1e-4 ? 0 : ((1 - t) * b + t * target - GlassMaterial.DarkBackdropLimit(transparency)) / ((1 - t) * (b - target))
            : (target - b) * (1 - t) <= 1e-4 ? 0 : (LightLeast - (1 - t) * b - t * target) / ((1 - t) * (target - b));
        return Math.Clamp(need, 0, LuminosityMost);
    }

    // How much of the backdrop the veil and the tint cover together.
    public static double Coverage(Look look) => look.TintOpacity + (1 - look.TintOpacity) * look.LuminosityOpacity;
    // The glass's lightness over a grey backdrop of that lightness (the recipe's own estimate, sRGB luma).
    public static double Luma(Look look, double backdrop) => backdrop * (1 - Coverage(look)) + look.Luminosity * Coverage(look);

    // The recipe as one D2D1_MATRIX_5X4_F (rows by input channel, the fifth the offset): saturation about the luma (Rec. 709),
    // then the luminosity blend (each channel moved by opacity x (target - luma), which keeps the hue and the chroma: W3C's
    // SetLum without its clip), then the tint laid over at its opacity.
    public static float[] Matrix(Look look)
    {
        double[] w = [.2126, .7152, .0722];
        var a = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int o = 0; o < 3; o++)
                a[i, o] = (1 - look.Saturation) * w[i] + (i == o ? look.Saturation : 0);
        var b = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int o = 0; o < 3; o++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++) sum += a[i, k] * ((k == o ? 1 : 0) - look.LuminosityOpacity * w[k]);
                b[i, o] = sum;
            }
        double t = look.TintOpacity;
        double[] tint = [look.TintR, look.TintG, look.TintB];
        var m = new float[20];
        for (int i = 0; i < 3; i++)
            for (int o = 0; o < 3; o++)
                m[i * 4 + o] = (float)(b[i, o] * (1 - t));
        m[15] = 1;
        for (int o = 0; o < 3; o++) m[16 + o] = (float)(look.LuminosityOpacity * look.Luminosity * (1 - t) + t * tint[o]);
        return m;
    }

    // A colour (0..1 per channel, opaque) through a 5x4 matrix, clamped to 0..1 as the effect clamps its output.
    public static (double R, double G, double B) Apply(float[] m, (double R, double G, double B) c)
    {
        double R(int o) => Math.Clamp(c.R * m[o] + c.G * m[4 + o] + c.B * m[8 + o] + m[16 + o], 0, 1);
        return (R(0), R(1), R(2));
    }

    // What the persistent graph shows (GlassEffect.Graph), for the offscreen checks and the readability guard of every material
    // frame: A_dark (kept within 0..1, as an 8-bit intermediate would keep it), the luma, the composite, clamped once at the end.
    // A_dark is the base matrix, whose entries (as Composition gets them, in single precision) are constants: per channel
    // own x c + cross x (the other two channels) + tint, with no array built per call.
    public static (double R, double G, double B) Shown((double R, double G, double B) backdrop, double darkVeil, double lightVeil, double palette)
    {
        var (slope, offset) = Coefficients(darkVeil, lightVeil, palette);
        return Shown(backdrop, slope, offset);
    }

    // The same with the composite's amounts already worked out (one material frame over several backdrops).
    public static (double R, double G, double B) Shown((double R, double G, double B) backdrop, double slope, double offset)
    {
        var m = Base;
        double r = backdrop.R, gr = backdrop.G, b = backdrop.B;
        double ar = Math.Clamp(r * m.M00 + gr * m.M10 + b * m.M20 + m.T0, 0, 1);
        double ag = Math.Clamp(r * m.M01 + gr * m.M11 + b * m.M21 + m.T1, 0, 1);
        double ab = Math.Clamp(r * m.M02 + gr * m.M12 + b * m.M22 + m.T2, 0, 1);
        double shift = slope * Math.Clamp(.2126 * r + .7152 * gr + .0722 * b, 0, 1) + offset;
        return (Math.Clamp(ar + shift, 0, 1), Math.Clamp(ag + shift, 0, 1), Math.Clamp(ab + shift, 0, 1));
    }

    // BaseMatrix's entries, read once.
    private readonly record struct Rows(double M00, double M01, double M02, double M10, double M11, double M12, double M20, double M21, double M22,
        double T0, double T1, double T2);
    private static readonly Rows Base = BaseRows();
    private static Rows BaseRows()
    {
        var m = BaseMatrix();
        return new(m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10], m[16], m[17], m[18]);
    }
}
