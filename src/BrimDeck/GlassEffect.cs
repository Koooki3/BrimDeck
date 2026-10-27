using System.Runtime.InteropServices;
using Windows.Graphics.Effects;
using Windows.UI.Composition;
using WinRT;

namespace BrimDeck;

// The held blur under glass (PanelGlass.Held): Direct2D's color matrix over the host backdrop, saturating the blur's colors and
// bringing its brightness into the theme's band, so a thinner tint reads over it. Composition takes an effect as an
// IGraphicsEffect that also answers IGraphicsEffectD2D1Interop; Win2D, which normally provides those, is not available to a WPF
// app, so this is that object, built in native memory from delegates: one identity with two interface pointers, the effect
// (also IUnknown, IInspectable and IGraphicsEffectSource) and the interop. One is made for each band a theme asks for, with
// the effect factory compiled from it, and both are kept for the run: every blur window (the island, each notice) only makes
// a brush from that factory, and lets the brush and its backdrop go again (Release).
internal static class GlassEffect
{
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid IidEffect = typeof(IGraphicsEffect).GUID;
    private static readonly Guid IidSource = typeof(IGraphicsEffectSource).GUID;
    private static readonly Guid IidInterop = new("2FC57384-A068-44D7-A331-30982FCF7177");
    private static readonly Guid IidPropertyValue = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
    private static readonly Guid ColorMatrix = new("921F03D6-641C-47DF-852D-B4BB6153AE11");
    private const int NotImplemented = unchecked((int)0x80004001), NoInterface = unchecked((int)0x80004002);

    // Used on the UI thread only, as the compositor is.
    private static readonly Dictionary<(double Low, double High, double Saturation), CompositionEffectFactory> Factories = [];

    // The brush: the host backdrop through the matrix that saturates by `saturation` and maps brightness 0..1 to low..high.
    public static CompositionBrush Brush(Compositor compositor, double low, double high, double saturation)
    {
        var key = (low, high, saturation);
        if (!Factories.TryGetValue(key, out var factory))
            Factories[key] = factory = compositor.CreateEffectFactory(Create(Matrix(low, high, saturation), new CompositionEffectSourceParameter("backdrop")));
        var brush = factory.CreateBrush();
        brush.SetSourceParameter("backdrop", compositor.CreateHostBackdropBrush());
        return brush;
    }

    // Round 16: the system acrylic's recipe over the same host backdrop (DWM's blur of what lies behind), the body of liquid glass
    // without any capture. Windows' Acrylic (microsoft-ui-xaml, MIT: AcrylicBrush) saturates the blur, blends the tint's
    // luminosity in at the luminosity opacity (the backdrop's hue and saturation kept), lays the tint over at its opacity and
    // adds a little noise; every step but the noise is linear, so here they are one color matrix (Acrylic, below). The looks:
    // the user's shell acrylic (ZenDesktop's numbers, user-setup-reference 1.4: the tint #232323 at 23 %, the saturation kept)
    // for the dark look and its light counterpart (#F3F3F3). Its luminosity layer of .3 suits Explorer over the user's dark
    // wallpaper only: over a white page it leaves the dark glass mid-grey and its light text unreadable (the page checks: 97
    // failures). The luminosity layer moves only the lightness - the backdrop's hue and saturation stay, so its colours still
    // glow through. Its base is .55, halfway between the Explorer's .3 and the first prototype's .8 (the user: "介于现在这版和资源
    // 管理器的透明度是合适的"); a brighter backdrop (a darker one for the light look) raises it just as far as the text needs
    // (LuminosityFor, from what lies around the island, AcrylicProbe).
    public readonly record struct AcrylicLook(double Saturation, double LuminosityOpacity, double Luminosity, double TintR, double TintG, double TintB, double TintOpacity);
    // User 10-27 ("各个液态玻璃主题都能再透一点"): a notch clearer, the tint and the saturation as they were, the probe raising
    // it where the text needs it as before. The collapsed island .45 to .40 in both looks;
    // both looks' open panel .55 to .45. The user chose the dark look as clear as the light one ("液态玻璃深色也和浅色一样透吧"),
    // knowing that over a moving wallpaper a bright streak under the panel, where the probe's ring cannot see it, can take the
    // dark look's secondary or tertiary text a little short for that frame (offscreen, all backdrops at 100/150/200 %: one
    // moving frame at 150 %, tertiary text 2.93:1 for 3:1; the pages report it as a warning, LiquidPages.Known); the primary text
    // keeps 4.5:1, and the probe still thickens the glass wherever its ring sees light.
    // Then once more ("浅色/深色液态玻璃都再透一些（0.05）"): the open panel .45 to .40, the collapsed island .40 to .35, both looks.
    // And again ("液态玻璃再调透0.05，磨砂质感做淡一点"): the open panel .40 to .35, the collapsed island .35 to .30; the frost
    // lighter by a thinner tint layer (the even veil over the blur: .23 dark and .25 light to .20 both) and half the noise
    // (AcrylicNoise). The blur itself is DWM's host backdrop, its radius not ours to set.
    public const double LuminosityBase = .35, LuminosityBaseLight = .35, LuminosityMost = .95;
    // Size makes thickness (step 4 of round 16): the collapsed island, the control layer, thinner and clearer (.35); the open
    // panel, the content layer, its full base - in between with the opening.
    public const double LuminosityCapsule = .30;
    public static double PanelBase(bool dark) => dark ? LuminosityBase : LuminosityBaseLight;
    public static double Thickness(double openness, bool dark) => LuminosityCapsule + (PanelBase(dark) - LuminosityCapsule) * Math.Clamp(openness, 0, 1);
    // The luminosity opacity shown: what the text needs (LuminosityFor) and never under the thickness for the opening; in steps.
    public static double Shown(double needed, double openness, bool dark) => Math.Ceiling(Math.Max(needed, Thickness(openness, dark)) * 20 - 1e-9) / 20;
    public static readonly AcrylicLook AcrylicDark = new(1.3, LuminosityBase, .1373, .1373, .1373, .1373, .20);
    public static readonly AcrylicLook AcrylicLight = new(1.3, LuminosityBase, .953, .953, .953, .953, .20);
    // The lightness the body may reach for the looks' text: the dark look's light text over at most DarkMost, the light look's
    // dark text over at least LightLeast (sRGB luma; 4.5:1 and more with the text's own colours, #F6F7F9 and #101217).
    // (DarkMost .27 to .26 with the lower floors of 10-27: a moving wallpaper's frame at 100 % left the dark look's open panel at
    // 89.9 % of the opaque theme's masked contrast.)
    // (LightLeast stays .82, 10-27, "浅色模式好像不透了": over a dark, moving wallpaper the light look's dark text needs its body
    // at .82, which the recipe reaches only at a luminosity of about .85 - the light look over a dark desktop is near solid by
    // its nature. .78 left the light look's masked contrast at 72 % of the opaque theme's (Lc 73.6) over a plain mid-grey or
    // blue backdrop, short of 75 % and Lc 75. Over a dark desktop the dark look, or automatic, is the clear one.)
    public const double DarkMost = .26, LightLeast = .82;

    // The luminosity opacity the text needs over a backdrop whose brightest tenth reaches bright (the dark look) or whose
    // darkest tenth reaches dark (the light look), from the recipe's lightness ((1 - L) b + L t)(1 - k) + k t: never under
    // LuminosityCapsule, at most LuminosityMost. In steps of .05, so a brush is made again only for a real change.
    public static double LuminosityFor(bool darkLook, double bright, double dark)
    {
        var look = darkLook ? AcrylicDark : AcrylicLight;
        double t = look.TintOpacity, target = look.Luminosity, b = darkLook ? bright : dark;
        double need = darkLook
            ? (b - target) * (1 - t) <= 1e-4 ? 0 : ((1 - t) * b + t * target - DarkMost) / ((1 - t) * (b - target))
            : (target - b) * (1 - t) <= 1e-4 ? 0 : (LightLeast - (1 - t) * b - t * target) / ((1 - t) * (target - b));
        return Math.Ceiling(Math.Clamp(need, LuminosityCapsule, LuminosityMost) * 20 - 1e-9) / 20;
    }
    private static readonly Dictionary<AcrylicLook, CompositionEffectFactory> AcrylicFactories = [];

    public static CompositionBrush Acrylic(Compositor compositor, AcrylicLook look)
    {
        if (!AcrylicFactories.TryGetValue(look, out var factory))
            AcrylicFactories[look] = factory = compositor.CreateEffectFactory(Create(AcrylicMatrix(look), new CompositionEffectSourceParameter("backdrop")));
        var brush = factory.CreateBrush();
        brush.SetSourceParameter("backdrop", compositor.CreateHostBackdropBrush());
        return brush;
    }

    // The recipe as one D2D1_MATRIX_5X4_F (rows by input channel, the fifth the offset): saturation about the luma (Rec. 709),
    // then the luminosity blend (each channel moved by opacity x (target - luma), which keeps the hue and the chroma: W3C's
    // SetLum without its clip), then the tint laid over at its opacity.
    internal static float[] AcrylicMatrix(AcrylicLook look)
    {
        double[] w = [.2126, .7152, .0722];
        var a = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int o = 0; o < 3; o++)
                a[i, o] = (1 - look.Saturation) * w[i] + (i == o ? look.Saturation : 0);
        // Luminosity: out_o = in_o - L (sum_k w_k in_k) + L target.
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

    // Lets a blur window's brush go, with the backdrop brush inside a held one.
    public static void Release(CompositionBrush? brush)
    {
        if (brush is CompositionEffectBrush held) held.GetSourceParameter("backdrop")?.Dispose();
        brush?.Dispose();
    }

    // D2D1_MATRIX_5X4_F, row by input channel: a saturation about the luma (Rec. 709 weights) scaled into the band, with the
    // band's low end as the offset row; alpha passes through.
    internal static float[] Matrix(double low, double high, double saturation)
    {
        double[] luma = [.2126, .7152, .0722];
        double scale = high - low;
        var m = new float[20];
        for (int input = 0; input < 3; input++)
            for (int output = 0; output < 3; output++)
                m[input * 4 + output] = (float)(scale * ((1 - saturation) * luma[input] + (input == output ? saturation : 0)));
        m[15] = 1;
        m[16] = m[17] = m[18] = (float)low;
        return m;
    }

    private sealed class State { public required float[] Matrix; public required IntPtr Source; public int Refs = 1; }
    // Composition and the runtime's finalizer may add and release references from other threads.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, State> States = new();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryFn(IntPtr self, ref Guid iid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint RefFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IidsFn(IntPtr self, out uint count, out IntPtr iids);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutPtrFn(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutIntFn(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InPtrFn(IntPtr self, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutGuidFn(IntPtr self, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MappingFn(IntPtr self, IntPtr name, out uint index, out int mapping);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CountFn(IntPtr self, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IndexFn(IntPtr self, uint index, out IntPtr value);

    // The delegates behind the tables must outlive every object made from them.
    private static readonly List<Delegate> Keep = [];
    private static IntPtr _effectTable, _interopTable;

    private static IGraphicsEffect Create(float[] matrix, IGraphicsEffectSource source)
    {
        Tables();
        var unknown = MarshalInspectable<object>.FromManaged(source);
        int hr = Marshal.QueryInterface(unknown, in IidSource, out var sourcePointer);
        Marshal.Release(unknown);
        Marshal.ThrowExceptionForHR(hr);
        var block = Marshal.AllocHGlobal(2 * IntPtr.Size);
        Marshal.WriteIntPtr(block, _effectTable);
        Marshal.WriteIntPtr(block, IntPtr.Size, _interopTable);
        States[block] = new State { Matrix = matrix, Source = sourcePointer };
        return MarshalInterface<IGraphicsEffect>.FromAbi(block);
    }

    private static IntPtr Identity(IntPtr self) => States.ContainsKey(self) ? self : self - IntPtr.Size;
    private static State Of(IntPtr self) => States[Identity(self)];

    private static void Tables()
    {
        if (_effectTable != IntPtr.Zero) return;
        QueryFn query = Query;
        RefFn addRef = self => (uint)Interlocked.Increment(ref Of(self).Refs);
        // Never below one: the object is the brush's for good.
        RefFn release = self => (uint)Math.Max(1, Interlocked.Decrement(ref Of(self).Refs));
        Delegate[] effect =
        [
            query, addRef, release,
            new IidsFn((IntPtr _, out uint count, out IntPtr iids) => { count = 0; iids = IntPtr.Zero; return 0; }),
            new OutPtrFn((IntPtr _, out IntPtr name) => { name = IntPtr.Zero; return 0; }),
            new OutIntFn((IntPtr _, out int level) => { level = 0; return 0; }),
            new OutPtrFn((IntPtr _, out IntPtr name) => WindowsCreateString("glass", 5, out name)),
            new InPtrFn((_, _) => 0)
        ];
        Delegate[] interop =
        [
            query, addRef, release,
            new OutGuidFn((IntPtr _, out Guid id) => { id = ColorMatrix; return 0; }),
            new MappingFn((IntPtr _, IntPtr _, out uint index, out int mapping) => { index = 0; mapping = 0; return NotImplemented; }),
            new CountFn((IntPtr _, out uint count) => { count = 3; return 0; }),
            new IndexFn(Property),
            new IndexFn((IntPtr self, uint _, out IntPtr source) => { source = Of(self).Source; Marshal.AddRef(source); return 0; }),
            new CountFn((IntPtr _, out uint count) => { count = 1; return 0; })
        ];
        _effectTable = Table(effect); _interopTable = Table(interop);
    }

    private static IntPtr Table(Delegate[] methods)
    {
        Keep.AddRange(methods);
        var table = Marshal.AllocHGlobal(methods.Length * IntPtr.Size);
        for (int i = 0; i < methods.Length; i++) Marshal.WriteIntPtr(table, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(methods[i]));
        return table;
    }

    private static int Query(IntPtr self, ref Guid iid, out IntPtr ppv)
    {
        var identity = Identity(self);
        if (iid == IidUnknown || iid == IidInspectable || iid == IidEffect || iid == IidSource) ppv = identity;
        else if (iid == IidInterop) ppv = identity + IntPtr.Size;
        else { ppv = IntPtr.Zero; return NoInterface; }
        Interlocked.Increment(ref States[identity].Refs);
        return 0;
    }

    // The color matrix's properties: the matrix, the alpha mode (premultiplied) and clamping the output to 0..1.
    private static int Property(IntPtr self, uint index, out IntPtr value)
    {
        object boxed = index switch
        {
            0 => Windows.Foundation.PropertyValue.CreateSingleArray(Of(self).Matrix),
            1 => Windows.Foundation.PropertyValue.CreateUInt32(1),
            _ => Windows.Foundation.PropertyValue.CreateBoolean(true)
        };
        var unknown = MarshalInspectable<object>.FromManaged(boxed);
        try { return Marshal.QueryInterface(unknown, in IidPropertyValue, out value); }
        finally { Marshal.Release(unknown); }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out IntPtr hstring);
}
