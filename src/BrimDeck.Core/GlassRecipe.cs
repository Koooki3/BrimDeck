namespace BrimDeck.Core;

// The acrylic recipe of liquid glass as numbers, apart from Composition, so it can be checked offscreen. Windows' Acrylic
// (microsoft-ui-xaml, MIT: AcrylicBrush) saturates the blur, blends the tint's luminosity in at the luminosity opacity (the
// backdrop's hue and saturation kept), lays the tint over at its opacity and adds a little noise; every step but the noise is
// linear, so here they are one color matrix (Matrix). The looks: the user's shell acrylic (the tint #232323) for the dark look and
// its light counterpart (#F3F3F3); their tint opacity and saturation follow the clarity (GlassClarityPolicy), the same for both.
//   The luminosity opacity (the "veil") weighs what the backdrop's brightness asks for the text (LuminosityFor) against the
// clarity's own veil (GlassClarityPolicy.Veil). All of it is continuous: one persistent effect graph shows every value
// (GlassEffect.Graph), nothing comes in steps for a cache of compiled effects.
//   Why one graph can (review v3 §4.6): saturation s about the luma g keeps the luma, and both looks' tints are neutral, so with
// the tint opacity k, each look's veil V and target T (the tint's own grey), and the light look's share p, the recipe is
//   out = (1 - k) s c + (1 - k) (1 - s - V) g + O,  V = (1 - p) V_dark + p V_light,
//   O = (1 - k) ((1 - p) V_dark T_dark + p V_light T_light) + k ((1 - p) T_dark + p T_light):
// the backdrop and its luma (one fixed matrix) in an arithmetic composite whose three amounts (Source, Slope, Offset:
// Coefficients) are the graph's only moving numbers. Exact for every colour (the composite clamps once, as the matrix does).
public static class GlassRecipe
{
    public readonly record struct Look(double Saturation, double LuminosityOpacity, double Luminosity, double TintR, double TintG, double TintB, double TintOpacity);

    public const double LuminosityBase = GlassMaterial.DefaultExpandedVeil, LuminosityMost = GlassMaterial.MaximumVeil;
    public const double LuminosityCapsule = GlassMaterial.DefaultCollapsedVeil;
    public const double DefaultTint = GlassClarityPolicy.DefaultTint, DefaultSaturation = GlassClarityPolicy.DefaultSaturation;
    public static readonly Look Dark = new(DefaultSaturation, LuminosityBase, .1373, .1373, .1373, .1373, DefaultTint);
    public static readonly Look Light = new(DefaultSaturation, LuminosityBase, .953, .953, .953, .953, DefaultTint);
    public static Look For(bool dark) => dark ? Dark : Light;
    public static Look For(bool dark, double tint, double saturation) => For(dark) with { TintOpacity = tint, Saturation = saturation };

    // The composite's three amounts: out = Source c + Slope g + Offset.
    public readonly record struct Amounts(double Source, double Slope, double Offset);

    // The arithmetic composite's amounts for each look's veil, the light look's share p (0 dark, 1 light), the tint opacity and the
    // saturation (each kept in its range).
    public static Amounts Coefficients(double darkVeil, double lightVeil, double palette, double tint = DefaultTint, double saturation = DefaultSaturation)
    {
        double p = Clamp01(palette), k = Clamp01(tint), s = double.IsFinite(saturation) ? Math.Clamp(saturation, 0, 2) : DefaultSaturation;
        double dark = Clamp01(darkVeil), light = Clamp01(lightVeil), veil = (1 - p) * dark + p * light;
        double offset = (1 - k) * ((1 - p) * dark * Dark.Luminosity + p * light * Light.Luminosity) + k * ((1 - p) * Dark.TintR + p * Light.TintR);
        return new((1 - k) * s, (1 - k) * (1 - s - veil), offset);
    }
    private static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
    // The graph's one fixed matrix: the backdrop's luma (Rec. 709) in every channel.
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

    // The lightness the body may reach for each look's text (sRGB luma): at most DarkMost under the dark look's light text, at
    // least LightLeast under the light look's dark text. The text's goal is fixed; the clarity only weighs it (GlassClarityPolicy).
    public const double DarkMost = .26, LightLeast = .82;

    // The luminosity opacity the text needs over a backdrop whose brightest tail reaches bright (the dark look) or whose darkest
    // tail reaches dark (the light look), from the recipe's lightness ((1 - L) b + L t)(1 - k) + k t at the clarity's tint opacity k:
    // never under zero, at most LuminosityMost. Continuous (no steps). Not finite: nothing known, the thickest.
    public static double LuminosityFor(bool darkLook, double bright, double dark, int transparency = GlassMaterial.DefaultTransparency)
        => LuminosityForTint(darkLook, bright, dark, GlassClarityPolicy.Tint(transparency));

    public static double LuminosityForTint(bool darkLook, double bright, double dark, double tint)
    {
        if (!double.IsFinite(bright) || !double.IsFinite(dark)) return LuminosityMost;
        double t = Clamp01(tint), target = For(darkLook).Luminosity, b = Math.Clamp(darkLook ? bright : dark, 0, 1);
        double need = darkLook
            ? (b - target) * (1 - t) <= 1e-4 ? 0 : ((1 - t) * b + t * target - DarkMost) / ((1 - t) * (b - target))
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
    // frame: the composite of the backdrop and its luma, clamped once at the end. Allocates nothing.
    public static (double R, double G, double B) Shown((double R, double G, double B) backdrop, double darkVeil, double lightVeil, double palette,
        double tint = DefaultTint, double saturation = DefaultSaturation)
        => Shown(backdrop, Coefficients(darkVeil, lightVeil, palette, tint, saturation));

    // The same with the composite's amounts already worked out (one material frame over several backdrops).
    public static (double R, double G, double B) Shown((double R, double G, double B) backdrop, Amounts amounts)
    {
        double r = backdrop.R, gr = backdrop.G, b = backdrop.B;
        double shift = amounts.Slope * Math.Clamp(.2126 * r + .7152 * gr + .0722 * b, 0, 1) + amounts.Offset, a = amounts.Source;
        return (Math.Clamp(a * r + shift, 0, 1), Math.Clamp(a * gr + shift, 0, 1), Math.Clamp(a * b + shift, 0, 1));
    }
}
