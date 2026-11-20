using System.Runtime.InteropServices;
using BrimDeck.Core;
using Windows.Graphics.Effects;
using Windows.UI.Composition;
using WinRT;

namespace BrimDeck;

// The glass's Direct2D effects over the host backdrop (DWM's blur of what lies behind). Composition takes an effect as an
// IGraphicsEffect that also answers IGraphicsEffectD2D1Interop; Win2D, which normally provides those, is not available to a WPF
// app, so this is that object, built in native memory from delegates: one identity with two interface pointers, the effect
// (also IUnknown, IInspectable and IGraphicsEffectSource) and the interop.
//   Graph: one persistent effect graph for every value liquid glass shows (GlassRecipe, "Why one graph can"): the backdrop
// through two fixed colour matrices (the dark look at no veil, and the luma), an arithmetic composite of the two whose amounts
// (Slope, Offset) are named, animatable scalar properties, and a last matrix that only sets the alpha to one (the composite
// works on every channel, alpha too). The material moves by setting the two amounts on the brush: the factory is compiled
// once for the run and no frame compiles anything (Stats.Compiled stays at one). Where Composition refuses the graph, the
// recipe falls back to one fixed matrix per look in steps (Acrylic), with a bounded cache.
internal static class GlassEffect
{
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid IidEffect = typeof(IGraphicsEffect).GUID;
    private static readonly Guid IidSource = typeof(IGraphicsEffectSource).GUID;
    private static readonly Guid IidInterop = new("2FC57384-A068-44D7-A331-30982FCF7177");
    private static readonly Guid IidPropertyValue = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
    private static readonly Guid ColorMatrixEffect = new("921F03D6-641C-47DF-852D-B4BB6153AE11");
    private static readonly Guid ArithmeticEffect = new("FC151437-049A-4784-A24A-F1C4DAF20987");
    private const int NotImplemented = unchecked((int)0x80004001), NoInterface = unchecked((int)0x80004002), InvalidArgument = unchecked((int)0x80070057);
    // GRAPHICS_EFFECT_PROPERTY_MAPPING_VECTORX..W: the composite's coefficients C1..C4 as four named scalars.
    private const int MappingVectorX = 2;
    private static readonly string[] ArithmeticNames = ["MultiplyAmount", "Source1Amount", "Source2Amount", "Offset"];

    // The animatable properties of the graph's brush.
    public const string Slope = "linear.Source2Amount", Offset = "linear.Offset";

    // Counters for the resource checks (Windows tests, diagnostics): factories compiled, brushes made and native effect
    // objects alive now.
    internal static (int Compiled, int Brushes, int Live) Stats => (_compiled, _brushes, States.Count);
    private static int _compiled, _brushes;

    // Used on the UI thread only, as the compositor is.
    private static CompositionEffectFactory? _graph;
    private static bool _graphRefused;
    // Failures that may pass (the device lost while the display changed) are retried a few times before the graph is given up.
    private const int GraphTries = 3;
    private static int _graphFailures;
    // Whether the persistent graph can be used on this system (false once Composition refused it).
    public static bool GraphAvailable => !_graphRefused;
    // Diagnostics only: makes Graph refuse, so the stepped fallback is used.
    internal static void RefuseGraph() => _graphRefused = true;

    // A brush of the persistent graph over a new host backdrop, or null where Composition refuses the graph (the caller then
    // uses Acrylic). The weights start at the dark look at its base veil.
    public static CompositionEffectBrush? Graph(Compositor compositor)
    {
        if (_graphRefused) return null;
        try
        {
            if (_graph is null)
            {
                var backdrop = new CompositionEffectSourceParameter("backdrop");
                // The matrices leave their output unclamped, so a compositor with float intermediates keeps a vivid colour's
                // saturation overshoot; the composite clamps once.
                var bare = Create("base", ColorMatrixEffect, [backdrop], GlassRecipe.BaseMatrix(), clamp: false);
                var luma = Create("luma", ColorMatrixEffect, [backdrop], GlassRecipe.LumaMatrix(), clamp: false);
                var linear = Create("linear", ArithmeticEffect, [bare, luma], coefficients: [0, 1, -.28f, .1f]);
                var opaque = Create("opaque", ColorMatrixEffect, [linear], OpaqueMatrix, straight: true);
                _graph = compositor.CreateEffectFactory((IGraphicsEffect)opaque, [Slope, Offset]);
                _compiled++;
                // A graph the compositor took but cannot draw would show as clear or black glass: the fallback instead.
                if (_graph.LoadStatus is not (CompositionEffectFactoryLoadStatus.Success or CompositionEffectFactoryLoadStatus.Pending))
                    throw new NotSupportedException($"the glass graph did not load: {_graph.LoadStatus}");
            }
            var brush = _graph.CreateBrush();
            brush.SetSourceParameter("backdrop", compositor.CreateHostBackdropBrush());
            _brushes++;
            return brush;
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or InvalidCastException or NotSupportedException)
        {
            if (error is ArgumentException or InvalidCastException or NotSupportedException || ++_graphFailures >= GraphTries) _graphRefused = true;
            _graph?.Dispose();
            _graph = null;
            return null;
        }
    }

    // Sets the graph brush's amounts for each look's luminosity opacity and the light look's share (0 dark, 1 light).
    public static void Present(CompositionEffectBrush brush, double darkVeil, double lightVeil, double palette)
    {
        var (slope, offset) = GlassRecipe.Coefficients(darkVeil, lightVeil, palette);
        brush.Properties.InsertScalar(Slope, (float)slope);
        brush.Properties.InsertScalar(Offset, (float)offset);
    }

    // The colour as it is, the alpha set to one: applied to the stored values (straight mode), after the composite has
    // added its offset to the alpha as well.
    private static readonly float[] OpaqueMatrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1];

    // The fallback: the recipe for one look, compiled once per look and kept for the run. The looks it is asked for come in
    // steps (StepLook), so the cache is bounded.
    private static readonly Dictionary<GlassRecipe.Look, CompositionEffectFactory> AcrylicFactories = [];
    public const int FallbackLimit = 96;
    public static CompositionBrush Acrylic(Compositor compositor, GlassRecipe.Look look)
    {
        if (!AcrylicFactories.TryGetValue(look, out var factory))
        {
            if (AcrylicFactories.Count >= FallbackLimit)
            {
                foreach (var old in AcrylicFactories.Values) old.Dispose();
                AcrylicFactories.Clear();
            }
            AcrylicFactories[look] = factory = compositor.CreateEffectFactory(
                (IGraphicsEffect)Create("glass", ColorMatrixEffect, [new CompositionEffectSourceParameter("backdrop")], GlassRecipe.Matrix(look)));
            _compiled++;
        }
        var brush = factory.CreateBrush();
        brush.SetSourceParameter("backdrop", compositor.CreateHostBackdropBrush());
        _brushes++;
        return brush;
    }

    // The fallback's look for the graph's weights, in .05 opacity steps and twelfths of a turn.
    public static GlassRecipe.Look StepLook(double darkVeil, double lightVeil, double palette)
    {
        double share = Math.Round(Math.Clamp(palette, 0, 1) * 12) / 12;
        static double Step(double veil) => Math.Ceiling(Math.Clamp(veil, 0, GlassRecipe.LuminosityMost) * 20 - 1e-9) / 20;
        var dark = GlassRecipe.Dark with { LuminosityOpacity = Step(darkVeil) };
        var light = GlassRecipe.Light with { LuminosityOpacity = Step(lightVeil) };
        if (share <= 0) return dark;
        if (share >= 1) return light;
        double veil = dark.LuminosityOpacity + (light.LuminosityOpacity - dark.LuminosityOpacity) * share;
        double oldCover = GlassRecipe.Coverage(dark), newCover = GlassRecipe.Coverage(light);
        double neutral = (oldCover * dark.Luminosity * (1 - share) + newCover * light.Luminosity * share) / (oldCover * (1 - share) + newCover * share);
        return dark with { LuminosityOpacity = veil, Luminosity = neutral, TintR = neutral, TintG = neutral, TintB = neutral };
    }

    // Lets a glass window's brush go, with the backdrop brush inside it.
    public static void Release(CompositionBrush? brush)
    {
        if (brush is CompositionEffectBrush held) held.GetSourceParameter("backdrop")?.Dispose();
        brush?.Dispose();
    }

    // One native effect: its Direct2D effect id, its name (the prefix of its animatable properties), its sources (owned
    // references) and, for a color matrix, its matrix.
    private sealed class State
    {
        public required Guid Clsid; public required string Name; public required IntPtr[] Sources; public float[]? Matrix; public float[]? Coefficients;
        public bool Clamp = true, Straight;
        public int Refs = 1;
    }
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

    // A native effect over sources (effect parameters or other effects made here). The returned projection holds the only
    // reference; Composition takes its own while it compiles.
    private static IGraphicsEffectSource Create(string name, Guid clsid, IGraphicsEffectSource[] sources, float[]? matrix = null, bool clamp = true,
        bool straight = false, float[]? coefficients = null)
    {
        Tables();
        var pointers = new IntPtr[sources.Length];
        IntPtr block = IntPtr.Zero;
        bool registered = false;
        try
        {
            for (int i = 0; i < sources.Length; i++)
            {
                var unknown = MarshalInspectable<object>.FromManaged(sources[i]);
                try { Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IidSource, out pointers[i])); }
                finally { if (unknown != IntPtr.Zero) Marshal.Release(unknown); }
            }
            block = Marshal.AllocHGlobal(2 * IntPtr.Size);
            Marshal.WriteIntPtr(block, _effectTable);
            Marshal.WriteIntPtr(block, IntPtr.Size, _interopTable);
            if (!States.TryAdd(block, new State { Clsid = clsid, Name = name, Sources = pointers, Matrix = matrix, Clamp = clamp, Straight = straight, Coefficients = coefficients }))
                throw new InvalidOperationException("A newly allocated glass effect identity was already registered.");
            registered = true; // The state now owns the sources' references.
            // FromAbi takes its own reference; the initial one is dropped below.
            return MarshalInterface<IGraphicsEffect>.FromAbi(block);
        }
        finally
        {
            if (registered) Marshal.Release(block);
            else
            {
                foreach (var pointer in pointers) if (pointer != IntPtr.Zero) Marshal.Release(pointer);
                if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
            }
        }
    }

    private static IntPtr Identity(IntPtr self) => States.ContainsKey(self) ? self : self - IntPtr.Size;
    private static State Of(IntPtr self) => States[Identity(self)];

    private static void Tables()
    {
        if (_effectTable != IntPtr.Zero) return;
        QueryFn query = Query;
        RefFn addRef = self => (uint)Interlocked.Increment(ref Of(self).Refs);
        RefFn release = Release;
        Delegate[] effect =
        [
            query, addRef, release,
            new IidsFn((IntPtr _, out uint count, out IntPtr iids) => { count = 0; iids = IntPtr.Zero; return 0; }),
            new OutPtrFn((IntPtr _, out IntPtr name) => { name = IntPtr.Zero; return 0; }),
            new OutIntFn((IntPtr _, out int level) => { level = 0; return 0; }),
            new OutPtrFn(Name),
            new InPtrFn((_, _) => 0)
        ];
        Delegate[] interop =
        [
            query, addRef, release,
            new OutGuidFn((IntPtr self, out Guid id) => { id = Of(self).Clsid; return 0; }),
            new MappingFn(Mapping),
            new CountFn((IntPtr self, out uint count) => { count = Of(self).Clsid == ColorMatrixEffect ? 3u : 2u; return 0; }),
            new IndexFn(Property),
            new IndexFn(Source),
            new CountFn((IntPtr self, out uint count) => { count = (uint)Of(self).Sources.Length; return 0; })
        ];
        _effectTable = Table(effect); _interopTable = Table(interop);
    }

    private static int Name(IntPtr self, out IntPtr name)
    {
        string text = Of(self).Name;
        return WindowsCreateString(text, text.Length, out name);
    }

    // The composite's coefficients are its animatable properties, by Win2D's names (each a component of property 0); a colour
    // matrix has none.
    private static int Mapping(IntPtr self, IntPtr name, out uint index, out int mapping)
    {
        index = 0; mapping = 0;
        if (Of(self).Clsid != ArithmeticEffect) return NotImplemented;
        string? text = Marshal.PtrToStringUni(name); // LPCWSTR
        int at = Array.FindIndex(ArithmeticNames, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return InvalidArgument;
        mapping = MappingVectorX + at;
        return 0;
    }

    private static int Source(IntPtr self, uint index, out IntPtr source)
    {
        var sources = Of(self).Sources;
        if (index >= sources.Length) { source = IntPtr.Zero; return InvalidArgument; }
        source = sources[index];
        Marshal.AddRef(source);
        return 0;
    }

    private static uint Release(IntPtr self)
    {
        var identity = Identity(self);
        var state = States[identity];
        int remaining = Interlocked.Decrement(ref state.Refs);
        if (remaining == 0 && States.TryRemove(identity, out _))
        {
            try { foreach (var source in state.Sources) Marshal.Release(source); }
            finally { Marshal.FreeHGlobal(identity); }
        }
        return (uint)Math.Max(remaining, 0);
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

    // A colour matrix: the matrix, the alpha mode (premultiplied, or straight: the stored values as they are) and whether the
    // output is clamped to 0..1. The composite: its coefficients (C1 A B + C2 A + C3 B + C4) and clamping its output.
    private static int Property(IntPtr self, uint index, out IntPtr value)
    {
        var state = Of(self);
        object boxed = state.Clsid == ArithmeticEffect
            ? index == 0 ? Windows.Foundation.PropertyValue.CreateSingleArray(state.Coefficients!) : Windows.Foundation.PropertyValue.CreateBoolean(true)
            : index switch
            {
                0 => Windows.Foundation.PropertyValue.CreateSingleArray(state.Matrix!),
                1 => Windows.Foundation.PropertyValue.CreateUInt32(state.Straight ? 2u : 1u),
                _ => Windows.Foundation.PropertyValue.CreateBoolean(state.Clamp)
            };
        var unknown = MarshalInspectable<object>.FromManaged(boxed);
        try { return Marshal.QueryInterface(unknown, in IidPropertyValue, out value); }
        finally { Marshal.Release(unknown); }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out IntPtr hstring);
}
