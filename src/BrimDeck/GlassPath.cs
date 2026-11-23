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
    private static bool _failed;

    // The outline of a layout (in the island's own units) as a composition path; null where Direct2D is not to be had.
    public static CompositionPath? Create(GlassBoundaryMath.Layout layout)
    {
        if (Geometry(layout) is not { } geometry || geometry == IntPtr.Zero) return null;
        try { return new CompositionPath(new Source(geometry)); }
        finally { Marshal.Release(geometry); }
    }

    // The outline of a layout as a Direct2D path geometry (a reference the caller releases); null where Direct2D is not to be had.
    internal static IntPtr? Geometry(GlassBoundaryMath.Layout layout)
    {
        if (Factory() is not { } factory) return null;
        Check(Slot<CreatePath>(factory, 10)(factory, out var path));
        try
        {
            Check(Slot<OpenPath>(path, 17)(path, out var sink));
            try
            {
                Slot<SetFill>(sink, 3)(sink, 1); // D2D1_FILL_MODE_WINDING
                var writer = new SinkWriter(sink);
                GlassBoundaryMath.WriteOutline(layout, placed: true, ref writer);
                writer.End();
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

    private static IntPtr? Factory()
    {
        if (_factory != IntPtr.Zero) return _factory;
        if (_failed) return null;
        try
        {
            // Multithreaded: the compositor reads the geometry on its own thread.
            var iid = new Guid("06152247-6f50-465a-9245-118bfd3b6007"); // ID2D1Factory
            Check(D2D1CreateFactory(1, in iid, IntPtr.Zero, out _factory));
            return _factory;
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            _failed = true;
            _factory = IntPtr.Zero;
            return null;
        }
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
        // Only the geometry it has, from its own factory; Composition then asks GetGeometry.
        private static readonly TryGet TryGetMethod = (IntPtr self, IntPtr factory, out IntPtr geometry) =>
        {
            geometry = IntPtr.Zero;
            if (factory != _factory) return 0;
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
