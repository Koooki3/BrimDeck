using System.Runtime.InteropServices;
using BrimDeck.Core;
using Windows.Graphics.Effects;
using Windows.UI.Composition;
using WinRT;

namespace BrimDeck;

// The graph backend's state (GlassEffect.GraphState).
internal enum GlassBackendState { Uninitialized, Loading, Ready, Recovering, Degraded }

// The glass's Direct2D effects over the host backdrop (DWM's blur of what lies behind). Composition takes an effect as an
// IGraphicsEffect that also answers IGraphicsEffectD2D1Interop; Win2D, which normally provides those, is not available to a WPF
// app, so this is that object, built in native memory from delegates: one identity with two interface pointers, the effect
// (also IUnknown, IInspectable and IGraphicsEffectSource) and the interop.
//   Graph: one persistent effect graph for every value liquid glass shows (GlassRecipe, "Why one graph can"): the backdrop
// through two fixed colour matrices (the dark look at no veil, and the luma), an arithmetic composite of the two whose amounts
// (Slope, Offset) are named, animatable scalar properties, and a last matrix that only sets the alpha to one (the composite
// works on every channel, alpha too). The material moves by setting the two amounts on the brush: the factory is compiled
// once for the run and no frame compiles anything (Stats.Compiled stays at one). Where Composition refuses the graph, the
// recipe falls back to one fixed matrix per look in steps (Acrylic), with a bounded least-recently-used cache.
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
    public const string Through = "linear.Source1Amount", Slope = "linear.Source2Amount", Offset = "linear.Offset";

    // Counters for the resource checks (Windows tests, diagnostics): factories compiled, brushes made and native effect
    // objects alive now; effect brushes not yet released and factories not yet disposed.
    internal static (int Compiled, int Brushes, int Live) Stats => (_compiled, _brushes, States.Count);
    internal static (int Brushes, int Factories) Held => (Owners.Count, _liveFactories);
    // Whether a brush made here still has its factory (not disposed under it).
    internal static bool FactoryLive(CompositionBrush? brush) => brush is not null && Owners.TryGetValue(brush, out var factory) && !factory.Disposed;
    private static int _compiled, _brushes, _liveFactories;

    // Test-only fault injection (G12). Inject is called before each step that can fail ("CreateFactory", "CreateBrush",
    // "SetSource", "Present", "CreateFallback") and may throw; InjectStatus replaces a factory's load status as it is read
    // (told whether it is the graph's); Clock gives the milliseconds the backoff and the loading limit count in.
    internal static Action<string>? Inject { get; set; }
    internal static Func<bool, CompositionEffectFactoryLoadStatus, CompositionEffectFactoryLoadStatus>? InjectStatus { get; set; }
    internal static Func<long> Clock = static () => Environment.TickCount64;

    // A compiled factory and the brushes made from it that are not yet released. A factory leaves the cache (retired) when it
    // failed, its compositor went or the fallback cache evicted it; it is disposed only once no brush of it is left, so no
    // surface loses the brush it shows to another surface's failure.
    private sealed class Factory(CompositionEffectFactory value, bool graph)
    {
        public readonly CompositionEffectFactory Value = value;
        public readonly bool Graph = graph;
        public int Brushes;
        public long Used;
        public bool Retired, Disposed, Failed;
    }
    private static readonly Dictionary<CompositionBrush, Factory> Owners = new(ReferenceEqualityComparer.Instance);

    // The graph's state for the current compositor and capability generation (spec 7.1): Loading while its factory is
    // Pending, Recovering while a failure that may pass backs off (100, 300, 1000 ms), Degraded once Composition refused it or
    // the retries ran out. Used on the UI thread only, as the compositor is; only Renew may come from another thread.
    private static Factory? _graph;
    private static Compositor? _compositor;
    private static int _generation, _seenGeneration = -1;
    private static bool _graphRefused, _fallbackRefused;
    private static int _graphFailures;
    private static long _retryAt;
    private static readonly int[] Backoff = [100, 300, 1000];
    // The first try and one retry after each backoff.
    public const int GraphTries = 4;
    internal static GlassBackendState GraphState { get; private set; }
    // Whether the persistent graph can be used now (false once Composition refused it for this generation).
    public static bool GraphAvailable => !_graphRefused;
    // When the graph may be tried again after a transient failure (a surface showing a fallback wakes for it).
    internal static long RetryAt => _retryAt;

    // The display, a resume or the capabilities changed: what was refused may work now, and the failure streak starts again.
    // Safe from any thread; the UI thread takes it up at its next request.
    public static void Renew() => Interlocked.Increment(ref _generation);
    // Diagnostics and tests only: makes Graph refuse for this generation, so the stepped fallback is used.
    internal static void RefuseGraph() { _graphRefused = true; GraphState = GlassBackendState.Degraded; }

    private static void Sync(Compositor compositor)
    {
        int generation = Volatile.Read(ref _generation);
        bool sameCompositor = ReferenceEquals(compositor, _compositor);
        if (generation == _seenGeneration && sameCompositor) return;
        if (!sameCompositor)
        {
            // Another compositor: nothing compiled for the old one is used again (brushes still out keep their factory alive).
            if (_graph is { } old) Retire(old);
            _graph = null;
            foreach (var factory in AcrylicFactories.Values) Retire(factory);
            AcrylicFactories.Clear();
            _compositor = compositor;
        }
        _seenGeneration = generation;
        _graphRefused = _fallbackRefused = false;
        _fallbackRetryAt = 0;
        _graphFailures = 0;
        _retryAt = 0;
        GraphState = _graph is null ? GlassBackendState.Uninitialized : GlassBackendState.Ready;
    }

    // Whether Graph would try now: not refused for this generation and not backing off. Cheap enough for every frame.
    public static bool CanTryGraph(Compositor compositor)
    {
        Sync(compositor);
        return !_graphRefused && Clock() >= _retryAt;
    }

    // A brush of the persistent graph over a new host backdrop, or null where Composition refuses the graph or a failure is
    // backing off (the caller then shows the fallback). The brush may still be loading: StatusOf tells, and only Success may
    // be shown. The weights start at the dark look at its base veil.
    public static CompositionEffectBrush? Graph(Compositor compositor)
    {
        if (!CanTryGraph(compositor)) return null;
        CompositionEffectBrush? brush = null;
        CompositionBrush? backdrop = null;
        try
        {
            EnsureFactory(compositor);
            var factory = _graph!;
            Inject?.Invoke("CreateBrush");
            brush = factory.Value.CreateBrush();
            Inject?.Invoke("SetSource");
            backdrop = compositor.CreateHostBackdropBrush();
            brush.SetSourceParameter("backdrop", backdrop);
            Own(brush, factory);
            var status = Status(factory);
            // A factory that loaded is no success yet: the streak ends only once a brush of it has presented (Succeeded).
            if (status == CompositionEffectFactoryLoadStatus.Pending) GraphState = GlassBackendState.Loading;
            else if (status != CompositionEffectFactoryLoadStatus.Success) throw new NotSupportedException($"the glass graph did not load: {status}");
            return brush;
        }
        catch (Exception error) when (Expected(error))
        {
            Discard(brush, backdrop);
            Fail(_graph, Permanent(error));
            return null;
        }
    }

    // Compiles the persistent graph ahead of the first panel that shows it: a new factory loads asynchronously (it stays Pending
    // until the UI thread has pumped messages, about 70-100 ms), and a panel shown meanwhile would show its opaque base first.
    // Called when liquid glass is chosen; harmless when the graph exists, is refused or is backing off.
    public static void Prewarm(Compositor compositor)
    {
        if (_graph is not null || !CanTryGraph(compositor)) return;
        try { EnsureFactory(compositor); }
        catch (Exception error) when (Expected(error)) { Fail(_graph, Permanent(error)); }
    }

    private static void EnsureFactory(Compositor compositor)
    {
        if (_graph is not null) return;
        Inject?.Invoke("CreateFactory");
        var source = new CompositionEffectSourceParameter("backdrop");
        // The matrices leave their output unclamped, so a compositor with float intermediates keeps a vivid colour's
        // saturation overshoot; the composite clamps once.
        // Review v3 V312: the backdrop itself and its luma, so the tint opacity and the saturation move with the veils (three
        // amounts, GlassRecipe.Coefficients) on the same compiled factory.
        var luma = Create("luma", ColorMatrixEffect, [source], GlassRecipe.LumaMatrix(), clamp: false);
        var linear = Create("linear", ArithmeticEffect, [source, luma], coefficients: [0, 1, -.28f, .1f]);
        var opaque = Create("opaque", ColorMatrixEffect, [linear], OpaqueMatrix, straight: true);
        _graph = Compile(compositor, (IGraphicsEffect)opaque, [Through, Slope, Offset]);
        GraphState = GlassBackendState.Loading;
        // A graph the compositor took but cannot draw would show as clear or black glass: the fallback instead.
        if (Status(_graph) is not (CompositionEffectFactoryLoadStatus.Success or CompositionEffectFactoryLoadStatus.Pending))
            throw new NotSupportedException($"the glass graph did not load: {Status(_graph)}");
    }

    // A brush's factory's load status as it is now (Other for a brush not made here or a factory that cannot be read).
    public static CompositionEffectFactoryLoadStatus StatusOf(CompositionBrush brush)
    {
        if (!Owners.TryGetValue(brush, out var factory)) return CompositionEffectFactoryLoadStatus.Other;
        try { return Status(factory); }
        catch (Exception error) when (Expected(error)) { return CompositionEffectFactoryLoadStatus.Other; }
    }

    // A graph brush presented (its factory loaded and its first weights were taken): the failure streak starts again. A brush of
    // a factory let go since still draws, but says nothing of the factory in use or of the backoff before the next one.
    public static void Succeeded(CompositionBrush brush)
    {
        if (!Owners.TryGetValue(brush, out var factory) || !ReferenceEquals(factory, _graph)) return;
        _graphFailures = 0;
        _retryAt = 0;
        if (!_graphRefused) GraphState = GlassBackendState.Ready;
    }

    // A graph brush failed after it was made (its factory never loaded, or its weights were refused): counted once per factory,
    // which leaves the cache. The caller releases the brush.
    public static void Failed(CompositionBrush brush, bool permanent)
    {
        if (Owners.TryGetValue(brush, out var factory)) Fail(factory, permanent);
        else Fail(null, permanent);
    }

    private static void Fail(Factory? factory, bool permanent)
    {
        if (factory is not null)
        {
            if (factory.Failed && !permanent) return;
            factory.Failed = true;
            if (ReferenceEquals(factory, _graph)) _graph = null;
            Retire(factory);
        }
        if (_graphRefused) return;
        _graphFailures++;
        if (permanent || _graphFailures >= GraphTries)
        {
            _graphRefused = true;
            GraphState = GlassBackendState.Degraded;
        }
        else
        {
            _retryAt = Clock() + Backoff[Math.Min(_graphFailures, Backoff.Length) - 1];
            GraphState = GlassBackendState.Recovering;
        }
    }

    // Sets the graph brush's amounts for each look's luminosity opacity and the light look's share (0 dark, 1 light).
    public static void Present(CompositionEffectBrush brush, double darkVeil, double lightVeil, double palette,
        double tint = GlassRecipe.DefaultTint, double saturation = GlassRecipe.DefaultSaturation)
    {
        Inject?.Invoke("Present");
        var amounts = GlassRecipe.Coefficients(darkVeil, lightVeil, palette, tint, saturation);
        brush.Properties.InsertScalar(Through, (float)amounts.Source);
        brush.Properties.InsertScalar(Slope, (float)amounts.Slope);
        brush.Properties.InsertScalar(Offset, (float)amounts.Offset);
    }

    // The colour as it is, the alpha set to one: applied to the stored values (straight mode), after the composite has
    // added its offset to the alpha as well.
    private static readonly float[] OpaqueMatrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1];

    // The fallback: the recipe for one look, compiled once per look. The looks it is asked for come in steps (StepLook), and
    // the cache keeps the most recently used ones: past the limit the least recently used look no brush shows is let go (one
    // at a time, so the looks in use stay compiled). Null where the look is still loading or Composition refuses it (the
    // caller then shows an opaque ground).
    private static readonly Dictionary<GlassRecipe.Look, Factory> AcrylicFactories = [];
    internal static int FallbackLimit = 96;
    internal static int FallbackCached => AcrylicFactories.Count;
    private static long _used, _fallbackRetryAt;
    public static bool FallbackAvailable(Compositor compositor)
    {
        Sync(compositor);
        return !_fallbackRefused;
    }

    public static CompositionBrush? Acrylic(Compositor compositor, GlassRecipe.Look look)
    {
        Sync(compositor);
        if (_fallbackRefused || Clock() < _fallbackRetryAt) return null;
        CompositionEffectBrush? brush = null;
        CompositionBrush? backdrop = null;
        Factory? factory = null;
        try
        {
            if (!AcrylicFactories.TryGetValue(look, out factory))
            {
                Evict();
                Inject?.Invoke("CreateFallback");
                factory = Compile(compositor,
                    (IGraphicsEffect)Create("glass", ColorMatrixEffect, [new CompositionEffectSourceParameter("backdrop")], GlassRecipe.Matrix(look)), null);
                AcrylicFactories[look] = factory;
            }
            factory.Used = ++_used;
            var status = Status(factory);
            if (status == CompositionEffectFactoryLoadStatus.Pending) return null;
            if (status != CompositionEffectFactoryLoadStatus.Success) throw new NotSupportedException($"the glass fallback did not load: {status}");
            brush = factory.Value.CreateBrush();
            backdrop = compositor.CreateHostBackdropBrush();
            brush.SetSourceParameter("backdrop", backdrop);
            Own(brush, factory);
            return brush;
        }
        catch (Exception error) when (Expected(error))
        {
            Discard(brush, backdrop);
            if (factory is not null && AcrylicFactories.Remove(look)) Retire(factory);
            // Refused for this generation, or (a device that is changing) not asked again for a second.
            if (Permanent(error)) _fallbackRefused = true;
            else _fallbackRetryAt = Clock() + Backoff[^1];
            return null;
        }
    }

    private static void Evict()
    {
        while (AcrylicFactories.Count >= FallbackLimit)
        {
            GlassRecipe.Look oldest = default;
            Factory? least = null;
            foreach (var (look, factory) in AcrylicFactories)
                if (factory.Brushes == 0 && (least is null || factory.Used < least.Used)) { oldest = look; least = factory; }
            // Every cached look is on screen: the cache stays over its limit by at most the surfaces shown.
            if (least is null) return;
            AcrylicFactories.Remove(oldest);
            Retire(least);
        }
    }

    // The fallback's look for the graph's weights, in .05 opacity steps and twelfths of a turn, with the clarity's tint opacity
    // and saturation (V4 F03: the same five numbers the graph takes, so the slider keeps its meaning when the glass degrades) in
    // .01 and .05 steps. The cache holds the looks by all five (AcrylicFactories, bounded by FallbackLimit).
    public static GlassRecipe.Look StepLook(double darkVeil, double lightVeil, double palette,
        double tint = GlassRecipe.DefaultTint, double saturation = GlassRecipe.DefaultSaturation)
    {
        var (darkStep, lightStep, share, tintStep, saturationStep) = StepMaterial(darkVeil, lightVeil, palette, tint, saturation);
        var dark = GlassRecipe.For(true, tintStep, saturationStep) with { LuminosityOpacity = darkStep };
        var light = GlassRecipe.For(false, tintStep, saturationStep) with { LuminosityOpacity = lightStep };
        if (share <= 0) return dark;
        if (share >= 1) return light;
        double veil = dark.LuminosityOpacity + (light.LuminosityOpacity - dark.LuminosityOpacity) * share;
        double oldCover = GlassRecipe.Coverage(dark), newCover = GlassRecipe.Coverage(light);
        double neutral = (oldCover * dark.Luminosity * (1 - share) + newCover * light.Luminosity * share) / (oldCover * (1 - share) + newCover * share);
        return dark with { LuminosityOpacity = veil, Luminosity = neutral, TintR = neutral, TintG = neutral, TintB = neutral };
    }

    // The weights the fallback's stepped look stands for: each veil up to the next .05, the share to the nearest twelfth, the
    // tint opacity to the nearest .01 and the saturation to the nearest .05 (the defaults fall on their steps).
    public static (double DarkVeil, double LightVeil, double Palette, double Tint, double Saturation) StepMaterial(double darkVeil, double lightVeil,
        double palette, double tint = GlassRecipe.DefaultTint, double saturation = GlassRecipe.DefaultSaturation)
    {
        static double Step(double veil) => Math.Ceiling(Math.Clamp(veil, 0, GlassRecipe.LuminosityMost) * 20 - 1e-9) / 20;
        double k = double.IsFinite(tint) ? Math.Clamp(tint, 0, 1) : GlassRecipe.DefaultTint;
        double s = double.IsFinite(saturation) ? Math.Clamp(saturation, 0, 2) : GlassRecipe.DefaultSaturation;
        return (Step(darkVeil), Step(lightVeil), Math.Round(Math.Clamp(palette, 0, 1) * 12) / 12, Math.Round(k * 100) / 100, Math.Round(s * 20) / 20);
    }

    // Lets a glass window's brush go, with the backdrop brush inside it; its factory goes too once retired and unused.
    public static void Release(CompositionBrush? brush)
    {
        if (brush is null) return;
        if (brush is CompositionEffectBrush held)
        {
            try { held.GetSourceParameter("backdrop")?.Dispose(); }
            catch (Exception error) when (Expected(error)) { }
        }
        brush.Dispose();
        if (Owners.Remove(brush, out var factory))
        {
            factory.Brushes--;
            if (factory.Retired) Retire(factory);
        }
    }

    private static Factory Compile(Compositor compositor, IGraphicsEffect effect, string[]? animatable)
    {
        var compiled = animatable is null ? compositor.CreateEffectFactory(effect) : compositor.CreateEffectFactory(effect, animatable);
        _compiled++;
        _liveFactories++;
        return new Factory(compiled, animatable is not null);
    }

    private static CompositionEffectFactoryLoadStatus Status(Factory factory)
    {
        var status = factory.Value.LoadStatus;
        return InjectStatus is { } inject ? inject(factory.Graph, status) : status;
    }

    private static void Own(CompositionEffectBrush brush, Factory factory)
    {
        Owners.Add(brush, factory);
        factory.Brushes++;
        _brushes++;
    }

    // A brush (and its backdrop) that never reached a caller.
    private static void Discard(CompositionEffectBrush? brush, CompositionBrush? backdrop)
    {
        if (brush is not null && Owners.ContainsKey(brush)) { Release(brush); return; }
        try { backdrop?.Dispose(); brush?.Dispose(); }
        catch (Exception error) when (Expected(error)) { }
    }

    // Out of the cache; disposed once no brush of it is left.
    private static void Retire(Factory factory)
    {
        factory.Retired = true;
        if (factory.Brushes > 0 || factory.Disposed) return;
        factory.Disposed = true;
        _liveFactories--;
        try { factory.Value.Dispose(); }
        catch (Exception error) when (Expected(error)) { }
    }

    // What Composition throws for an effect it cannot take (permanent for this device generation: the effect, its arguments
    // or the interface are refused) or for a device that is changing (COMException, InvalidOperationException: may pass).
    private static bool Expected(Exception error) => error is COMException or ArgumentException or InvalidOperationException or InvalidCastException
        or NotSupportedException or NotImplementedException;
    private static bool Permanent(Exception error) => error is ArgumentException or InvalidCastException or NotSupportedException or NotImplementedException;

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
