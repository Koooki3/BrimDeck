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
