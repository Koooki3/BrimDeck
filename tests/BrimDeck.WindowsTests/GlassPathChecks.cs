using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BrimDeck;
using BrimDeck.Core;

// The path the glass layer is cut by (GlassPath), as Composition and Direct2D see it: the geometry source is asked through its
// real COM interface (IGeometrySource2DInterop), and references are counted on the objects themselves.
internal static class GlassPathChecks
{
    private static readonly Guid Interop = new("0657AF73-53FD-47CF-84FF-C8492D2A80A3");
    private static readonly Guid Unknown = new("00000000-0000-0000-C000-000000000046");
    private const int NotImplemented = unchecked((int)0x80004001);

    public static void Run(Action<string, bool> check)
    {
        var layout = GlassBoundaryMath.Measure(160, 30, 14, 0, 0, 12, 12, 0);
        if (GlassPath.GeometrySource(layout) is not { } source) { check("Direct2D builds the notch's geometry source", false); return; }
        Contract(check, source);
        Outlives(check, layout);
        Concurrent(check, source);
        GC.KeepAlive(source);
    }

    // TryGetGeometryUsingFactory hands out the geometry to its own factory only; for another (or none) it answers E_NOTIMPL and
    // no geometry, as Microsoft's own sources do (HelloVectors, XAML's CGeometrySource), and takes no reference on anything.
    private static void Contract(Action<string, bool> check, object source)
    {
        var interop = InteropOf(source);
        IntPtr geometry = IntPtr.Zero, own = IntPtr.Zero, other = IntPtr.Zero;
        try
        {
            int got = Get(interop, out geometry);
            int references = References(geometry);
            int again = Get(interop, out var second);
            bool counted = second == geometry && References(geometry) == references + 1;
            if (second != IntPtr.Zero) Marshal.Release(second);
            check($"GetGeometry hands out the geometry with a reference of its own (0x{got:X8}, 0x{again:X8})",
                got == 0 && again == 0 && geometry != IntPtr.Zero && counted && References(geometry) == references);

            Marshal.QueryInterface(interop, in Unknown, out var inside);
            var outer = WinRT.MarshalInspectable<object>.FromManaged(source);
            Marshal.QueryInterface(outer, in Unknown, out var outside);
            check("The interop object and the geometry source are one COM object (the same IUnknown)", inside != IntPtr.Zero && inside == outside);
            Marshal.Release(inside); Marshal.Release(outside); Marshal.Release(outer);

            GetFactory(geometry, out own);
            int mine = TryGet(interop, own, out var same);
            check($"TryGetGeometryUsingFactory answers its own factory with the geometry (0x{mine:X8})", mine == 0 && same == geometry);
            if (same != IntPtr.Zero) Marshal.Release(same);

            D2D1CreateFactory(1, new Guid("06152247-6f50-465a-9245-118bfd3b6007"), IntPtr.Zero, out other);
            int before = References(geometry), factoryBefore = References(other), selfBefore = References(interop);
            int refused = TryGet(interop, other, out var foreign), none = TryGet(interop, IntPtr.Zero, out var nothing);
            check($"TryGetGeometryUsingFactory refuses another factory with E_NOTIMPL and no geometry (0x{refused:X8}, {(foreign == IntPtr.Zero ? "null" : "a geometry")})",
                other != IntPtr.Zero && refused == NotImplemented && foreign == IntPtr.Zero);
            check($"TryGetGeometryUsingFactory refuses no factory the same way (0x{none:X8}, {(nothing == IntPtr.Zero ? "null" : "a geometry")})",
                none == NotImplemented && nothing == IntPtr.Zero);
            check("A refused request takes no reference on the geometry, the factory or the source",
                References(geometry) == before && References(other) == factoryBefore && References(interop) == selfBefore);
            int fallback = Get(interop, out var after);
            check($"GetGeometry still answers after a refusal (0x{fallback:X8})", fallback == 0 && after == geometry);
            if (after != IntPtr.Zero) Marshal.Release(after);
        }
        finally
        {
            foreach (var held in new[] { geometry, own, other, interop }) if (held != IntPtr.Zero) Marshal.Release(held);
        }
    }

    // A reference on the interop object alone keeps all of it alive; the last one gone, the source lets its geometry go.
    private static void Outlives(Action<string, bool> check, GlassBoundaryMath.Layout layout)
    {
        var interop = Alone(layout);
        Collect();
        int got = Get(interop, out var geometry);
        double area = got == 0 ? GlassPath.Area(geometry) : 0;
        check($"The interop object outlives its managed source while a reference is held (0x{got:X8}, area {area:0.0})", got == 0 && area > 3000);
        int held = References(geometry);
        Marshal.Release(interop);
        Collect();
        int left = References(geometry);
        check($"The last reference gone, the source lets its geometry go ({held} references before, {left} after)", held == 2 && left == 1);
        if (geometry != IntPtr.Zero) Marshal.Release(geometry);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IntPtr Alone(GlassBoundaryMath.Layout layout) => InteropOf(GlassPath.GeometrySource(layout)!);
    }

    // The compositor may ask from its own threads: queries, requests and releases at once leave the counts as they were.
    private static void Concurrent(Action<string, bool> check, object source)
    {
        var interop = InteropOf(source);
        Get(interop, out var geometry);
        int before = References(interop), geometryBefore = References(geometry), wrong = 0;
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < 5000; i++)
            {
                if (Marshal.QueryInterface(interop, in Interop, out var one) != 0 || one != interop) { Interlocked.Increment(ref wrong); continue; }
                if (Get(one, out var got) != 0 || got != geometry) Interlocked.Increment(ref wrong);
                else Marshal.Release(got);
                TryGet(one, IntPtr.Zero, out var refused);
                if (refused != IntPtr.Zero) Interlocked.Increment(ref wrong);
                Marshal.Release(one);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        int after = References(interop), geometryAfter = References(geometry);
        check($"8 threads querying, asking and releasing 5000 times each leave the references as they were ({wrong} wrong; {before} and {after}; {geometryBefore} and {geometryAfter})",
            wrong == 0 && after == before && geometryAfter == geometryBefore);
        Marshal.Release(geometry);
        Marshal.Release(interop);
    }

    private static void Collect()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }

    // The source's IGeometrySource2DInterop, with a reference the caller releases.
    private static IntPtr InteropOf(object source)
    {
        var outer = WinRT.MarshalInspectable<object>.FromManaged(source);
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(outer, in Interop, out var interop));
        Marshal.Release(outer);
        return interop;
    }

    private static int References(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return 0;
        Marshal.AddRef(instance);
        return Marshal.Release(instance);
    }

    // Called through the vtable's own function pointers, as native callers do.
    private static unsafe void* Slot(IntPtr instance, int slot) => (*(void***)instance)[slot];
    private static unsafe int Get(IntPtr interop, out IntPtr geometry)
    {
        fixed (IntPtr* result = &geometry) return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(interop, 3))(interop, result);
    }
    private static unsafe int TryGet(IntPtr interop, IntPtr factory, out IntPtr geometry)
    {
        // Not null beforehand: an answer that leaves the result alone shows.
        IntPtr result = 1;
        int answer = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(interop, 4))(interop, factory, &result);
        geometry = result;
        return answer;
    }
    private static unsafe void GetFactory(IntPtr resource, out IntPtr factory)
    {
        fixed (IntPtr* result = &factory) ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Slot(resource, 3))(resource, result);
    }
    [DllImport("d2d1")] private static extern int D2D1CreateFactory(int type, in Guid iid, IntPtr options, out IntPtr factory);
}
