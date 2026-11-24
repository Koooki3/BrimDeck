using System.Runtime.InteropServices;
using BrimDeck.Core;
using Windows.UI.Composition;

namespace BrimDeck;

// The island's outline as a Direct2D path, for the glass layer's own clip (CompositionPathGeometry): the same parts as the WPF
// outline (GlassBoundaryMath.WriteOutline), so the backdrop is cut along the curve the edge is drawn on, antialiased by the
// compositor, with no window region. Composition takes a path from an IGeometrySource2D that answers IGeometrySource2DInterop
// with the Direct2D geometry; this one is written here (no Win2D): the geometry source is a WinRT object whose
// ICustomQueryInterface hands out a small native interop object holding the path.
internal static class GlassPath
{
    private static IntPtr _factory;

    // Test-only fault injection, as GlassEffect.Inject: called before each step that can fail ("CreateFactory",
    // "CreatePathGeometry", "Open", "Close", "CompositionPath"; "CompositionPathGeometry", "CompositionGeometricClip" and "Path"
    // in GlassSurface) and may throw what the step's HRESULT would.
    internal static Action<string>? Inject { get; set; }

    // A path that could not be made (a surface then cuts its glass by a rounded rectangle): tried again after 100, 300 and
    // 1000 ms, then no more; a refusal that will not pass is not tried again at all. Either way a new generation (Renew) starts
    // over. Used on the UI thread only; only Renew may come from another thread. The clock is GlassEffect's.
    private static readonly int[] Backoff = [100, 300, 1000];
    private const int StreakMs = 10_000;
    private static int _generation, _seenGeneration, _failures;
    private static bool _refused;
    private static long _retryAt;

    // The display, a resume or the capabilities changed: what was refused may work now.
    public static void Renew() => Interlocked.Increment(ref _generation);

    private static void Sync()
    {
        int generation = Volatile.Read(ref _generation);
        if (generation == _seenGeneration) return;
        _seenGeneration = generation;
        _failures = 0;
        _refused = false;
        _retryAt = 0;
    }

    // Whether a path may be made now. Cheap enough for every frame.
    public static bool CanTry
    {
        get { Sync(); return !_refused && GlassEffect.Clock() >= _retryAt; }
    }

    // How long until a path that failed may be tried again (ms); null where nothing failed or it will not be tried again.
    public static int? RetryIn
    {
        get { Sync(); return _refused || _failures == 0 ? null : (int)Math.Clamp(_retryAt - GlassEffect.Clock(), 0, 60_000); }
    }

    // A path made ends a streak of failures only once it has stayed away a while: a step that fails on every other path (new
    // objects accepted, the replacement refused) still runs out of tries.
    public static void Succeeded()
    {
        if (_failures > 0 && GlassEffect.Clock() - _retryAt >= StreakMs) _failures = 0;
    }

    // A step failed, however its HRESULT was mapped to an exception. The next try starts with a new factory (the paths made by
    // the old one hold it themselves).
    public static void Failed(Exception error)
    {
        var factory = _factory;
        _factory = IntPtr.Zero;
        if (factory != IntPtr.Zero) Marshal.Release(factory);
        _failures++;
        if (_failures > Backoff.Length || error is DllNotFoundException or EntryPointNotFoundException or InvalidCastException
                or NotImplementedException or NotSupportedException)
            _refused = true;
        else _retryAt = GlassEffect.Clock() + Backoff[_failures - 1];
    }

    // The outline of a layout (in the island's own units) as a composition path. Throws where a step fails (Failed takes it).
    public static CompositionPath Create(GlassBoundaryMath.Layout layout)
    {
        var source = GeometrySource(layout);
        Inject?.Invoke("CompositionPath");
        return new CompositionPath(source);
    }

    // The outline of a layout as the geometry source Composition is given.
    internal static Windows.Graphics.IGeometrySource2D GeometrySource(GlassBoundaryMath.Layout layout)
    {
        var geometry = Geometry(layout);
        try { return new Source(geometry); }
        finally { Marshal.Release(geometry); }
    }

    // The outline of a layout as a Direct2D path geometry (a reference the caller releases).
    internal static IntPtr Geometry(GlassBoundaryMath.Layout layout)
    {
        var factory = Factory();
        Inject?.Invoke("CreatePathGeometry");
        Check(Slot<CreatePath>(factory, 10)(factory, out var path));
        try
        {
            Inject?.Invoke("Open");
            Check(Slot<OpenPath>(path, 17)(path, out var sink));
            try
            {
                Slot<SetFill>(sink, 3)(sink, 1); // D2D1_FILL_MODE_WINDING
                var writer = new SinkWriter(sink);
                GlassBoundaryMath.WriteOutline(layout, placed: true, ref writer);
                writer.End();
                Inject?.Invoke("Close");
                Check(Slot<CloseSink>(sink, 9)(sink));
            }
            finally { Marshal.Release(sink); }
            var done = path;
            path = IntPtr.Zero;
            return done;
        }
        finally { if (path != IntPtr.Zero) Marshal.Release(path); }
    }

    // The area a Direct2D geometry fills (for the checks), at a hundredth of a unit.
    internal static double Area(IntPtr geometry)
    {
        Check(Slot<ComputeArea>(geometry, 13)(geometry, IntPtr.Zero, .01f, out float area));
        return area;
    }

    // Whether a Direct2D geometry fills a point (for the checks).
    internal static bool Fills(IntPtr geometry, double x, double y)
    {
        Check(Slot<FillContains>(geometry, 7)(geometry, new((float)x, (float)y), IntPtr.Zero, .01f, out int contains));
        return contains != 0;
    }

    private static IntPtr Factory()
    {
        if (_factory != IntPtr.Zero) return _factory;
        Inject?.Invoke("CreateFactory");
        // Multithreaded: the compositor reads the geometry on its own thread.
        var iid = new Guid("06152247-6f50-465a-9245-118bfd3b6007"); // ID2D1Factory
        Check(D2D1CreateFactory(1, in iid, IntPtr.Zero, out var factory));
        return _factory = factory;
    }

    // The factory a geometry was made by (ID2D1Resource::GetFactory); it lives as long as the geometry.
    private static IntPtr FactoryOf(IntPtr geometry)
    {
        Slot<GetFactory>(geometry, 3)(geometry, out var factory);
        if (factory != IntPtr.Zero) Marshal.Release(factory);
        return factory;
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    // A COM method through the object's vtable; delegates kept per function, as every path shares its type's vtable.
    private static readonly Dictionary<IntPtr, Delegate> Methods = [];
    private static T Slot<T>(IntPtr instance, int slot) where T : Delegate
    {
        var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
        lock (Methods)
        {
            if (Methods.TryGetValue(method, out var known)) return (T)known;
            var created = Marshal.GetDelegateForFunctionPointer<T>(method);
            Methods[method] = created;
            return created;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point2F(float X, float Y);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Bezier(Point2F A, Point2F B, Point2F C);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetFactory(IntPtr self, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreatePath(IntPtr self, out IntPtr path);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenPath(IntPtr self, out IntPtr sink);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetFill(IntPtr self, int mode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BeginFigure(IntPtr self, Point2F start, int begin);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void EndFigure(IntPtr self, int end);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CloseSink(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void AddLine(IntPtr self, Point2F point);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void AddBezier(IntPtr self, in Bezier bezier);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ComputeArea(IntPtr self, IntPtr transform, float tolerance, out float area);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FillContains(IntPtr self, Point2F point, IntPtr transform, float tolerance, out int contains);

    // ID2D1GeometrySink: BeginFigure 5, EndFigure 8, AddLine 10, AddBezier 11.
    private struct SinkWriter(IntPtr sink) : GlassBoundaryMath.IOutlineSink
    {
        private bool _open;
        public void Begin(double x, double y)
        {
            End();
            Slot<BeginFigure>(sink, 5)(sink, new((float)x, (float)y), 0); // D2D1_FIGURE_BEGIN_FILLED
            _open = true;
        }
        public readonly void Line(double x, double y) => Slot<AddLine>(sink, 10)(sink, new((float)x, (float)y));
        public readonly void Curve(double x1, double y1, double x2, double y2, double x3, double y3)
            => Slot<AddBezier>(sink, 11)(sink, new(new((float)x1, (float)y1), new((float)x2, (float)y2), new((float)x3, (float)y3)));
        public void End()
        {
            if (!_open) return;
            Slot<EndFigure>(sink, 8)(sink, 1); // D2D1_FIGURE_END_CLOSED
            _open = false;
        }
    }

    [DllImport("d2d1")] private static extern int D2D1CreateFactory(int type, in Guid iid, IntPtr options, out IntPtr factory);

    // The geometry source Composition is given. Its interop object (IGeometrySource2DInterop: GetGeometry, then
    // TryGetGeometryUsingFactory) is native memory laid out as { vtable, outer, geometry }: its references are the source's own
    // (AddRef and Release go to the source's wrapper, which keeps this object alive), so it lives exactly as long as the source.
    private sealed partial class Source : Windows.Graphics.IGeometrySource2D, ICustomQueryInterface
    {
        private static readonly Guid InteropId = new("0657AF73-53FD-47CF-84FF-C8492D2A80A3");
        private readonly IntPtr _interop;

        public Source(IntPtr geometry)
        {
            Marshal.AddRef(geometry);
            _interop = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(_interop, Table.Pointer);
            Marshal.WriteIntPtr(_interop, IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(_interop, 2 * IntPtr.Size, geometry);
        }

        ~Source()
        {
            var geometry = Marshal.ReadIntPtr(_interop, 2 * IntPtr.Size);
            if (geometry != IntPtr.Zero) Marshal.Release(geometry);
            Marshal.FreeHGlobal(_interop);
        }

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            if (iid != InteropId) return CustomQueryInterfaceResult.NotHandled;
            // The wrapper asking is this object's own: its identity is what the interop object hands IUnknown and references to.
            // The reference this returns is the one taken here, on the wrapper.
            var outer = WinRT.MarshalInspectable<object>.FromManaged(this);
            Marshal.WriteIntPtr(_interop, IntPtr.Size, outer);
            ppv = _interop;
            return CustomQueryInterfaceResult.Handled;
        }
    }

    // The interop object's vtable: IUnknown (to the outer wrapper), GetGeometry, TryGetGeometryUsingFactory.
    private static class Table
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Query(IntPtr self, ref Guid iid, out IntPtr ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Count(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Get(IntPtr self, out IntPtr geometry);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TryGet(IntPtr self, IntPtr factory, out IntPtr geometry);
        private static readonly Query QueryMethod = (IntPtr self, ref Guid iid, out IntPtr ppv) =>
        {
            if (iid == new Guid("0657AF73-53FD-47CF-84FF-C8492D2A80A3")) { ppv = self; AddRefMethod!(self); return 0; }
            return Marshal.QueryInterface(Marshal.ReadIntPtr(self, IntPtr.Size), in iid, out ppv);
        };
        private static readonly Count AddRefMethod = self => (uint)Marshal.AddRef(Marshal.ReadIntPtr(self, IntPtr.Size));
        private static readonly Count ReleaseMethod = self => (uint)Marshal.Release(Marshal.ReadIntPtr(self, IntPtr.Size));
        private static readonly Get GetMethod = (IntPtr self, out IntPtr geometry) =>
        {
            geometry = Marshal.ReadIntPtr(self, 2 * IntPtr.Size);
            if (geometry == IntPtr.Zero) return unchecked((int)0x80004005); // E_FAIL
            Marshal.AddRef(geometry);
            return 0;
        };
        // Only the geometry it has, from that geometry's own factory (a failure replaces the shared one); any other is refused outright, as Microsoft's sources do (HelloVectors,
        // XAML), rather than answered with a successful nothing. (Composition asks GetGeometry.)
        private static readonly TryGet TryGetMethod = (IntPtr self, IntPtr factory, out IntPtr geometry) =>
        {
            geometry = IntPtr.Zero;
            var held = Marshal.ReadIntPtr(self, 2 * IntPtr.Size);
            if (factory == IntPtr.Zero || held == IntPtr.Zero || factory != FactoryOf(held)) return unchecked((int)0x80004001); // E_NOTIMPL
            return GetMethod(self, out geometry);
        };
        public static readonly IntPtr Pointer = Build();
        private static IntPtr Build()
        {
            var table = Marshal.AllocHGlobal(5 * IntPtr.Size);
            Delegate[] methods = [QueryMethod, AddRefMethod, ReleaseMethod, GetMethod, TryGetMethod];
            for (int i = 0; i < methods.Length; i++) Marshal.WriteIntPtr(table, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(methods[i]));
            return table;
        }
    }
}
