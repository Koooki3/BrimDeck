using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BrimDeck;
using BrimDeck.Core;
using Windows.UI.Composition;

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
        var source = GlassPath.GeometrySource(layout);
        Contract(check, source);
        Replacement(check, layout);
        Outlives(check, layout);
        Concurrent(check, source);
        GC.KeepAlive(source);
        Faults(check);
        Replaced(check);
    }

    // TryGetGeometryUsingFactory hands out the geometry to its own factory only; for another (or none) it answers E_NOTIMPL and
    // no geometry, as Microsoft's own sources do (HelloVectors, XAML's CGeometrySource), and takes no reference on anything.
    private static void Contract(Action<string, bool> check, object source)
    {
        var interop = InteropOf(source, out int found);
        if (!Got(check, "the interop object of a source", found, interop)) return;
        IntPtr geometry = IntPtr.Zero, own = IntPtr.Zero, other = IntPtr.Zero;
        try
        {
            int got = Get(interop, out geometry);
            if (!Got(check, "the geometry of a source", got, geometry)) return;
            int references = References(geometry);
            int again = Get(interop, out var second);
            bool counted = second == geometry && References(geometry) == references + 1;
            if (second != IntPtr.Zero) Marshal.Release(second);
            check($"GetGeometry hands out the geometry with a reference of its own (0x{got:X8}, 0x{again:X8})",
                got == 0 && again == 0 && geometry != IntPtr.Zero && counted && References(geometry) == references);

            int asked = Marshal.QueryInterface(interop, in Unknown, out var inside);
            var outer = WinRT.MarshalInspectable<object>.FromManaged(source);
            int outerAsked = Marshal.QueryInterface(outer, in Unknown, out var outside);
            check($"The interop object and the geometry source are one COM object (the same IUnknown; 0x{asked:X8}, 0x{outerAsked:X8})",
                asked == 0 && outerAsked == 0 && inside != IntPtr.Zero && inside == outside);
            foreach (var held in new[] { inside, outside, outer }) if (held != IntPtr.Zero) Marshal.Release(held);

            GetFactory(geometry, out own);
            if (!Got(check, "the geometry's factory", 0, own)) return;
            int mine = TryGet(interop, own, out var same);
            check($"TryGetGeometryUsingFactory answers its own factory with the geometry (0x{mine:X8})", mine == 0 && same == geometry);
            if (mine == 0 && same != IntPtr.Zero) Marshal.Release(same);

            int created = D2D1CreateFactory(1, new Guid("06152247-6f50-465a-9245-118bfd3b6007"), IntPtr.Zero, out other);
            if (!Got(check, "another factory", created, other)) return;
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

    // A failure replaces the shared factory: a source made before it belongs to the factory its geometry was made by, not to the
    // one in use now.
    private static void Replacement(Action<string, bool> check, GlassBoundaryMath.Layout layout)
    {
        var old = GlassPath.GeometrySource(layout);
        var interop = InteropOf(old, out int found);
        if (!Got(check, "the interop object of a source whose factory is replaced", found, interop)) return;
        IntPtr geometry = IntPtr.Zero, first = IntPtr.Zero, next = IntPtr.Zero, second = IntPtr.Zero;
        try
        {
            int got = Get(interop, out geometry);
            if (!Got(check, "the geometry of a source whose factory is replaced", got, geometry)) return;
            GetFactory(geometry, out first);
            if (!Got(check, "the factory that is replaced", 0, first)) return;
            GlassPath.Failed(new COMException("test"));
            next = GlassPath.Geometry(layout);
            GetFactory(next, out second);
            if (!Got(check, "the factory that replaces it", 0, second)) return;
            int mine = TryGet(interop, first, out var kept), foreign = TryGet(interop, second, out var other);
            check($"A source made before its factory was replaced answers its own factory only (0x{mine:X8}, 0x{foreign:X8})",
                first != second && mine == 0 && kept == geometry && foreign == NotImplemented && other == IntPtr.Zero);
            if (mine == 0 && kept != IntPtr.Zero) Marshal.Release(kept);
            if (foreign == 0 && other != IntPtr.Zero) Marshal.Release(other);
        }
        finally
        {
            GlassPath.Renew();
            foreach (var held in new[] { geometry, first, next, second, interop }) if (held != IntPtr.Zero) Marshal.Release(held);
            GC.KeepAlive(old);
        }
    }

    // A reference on the interop object alone keeps all of it alive; the last one gone, the source lets its geometry go.
    private static void Outlives(Action<string, bool> check, GlassBoundaryMath.Layout layout)
    {
        var interop = Alone(layout, out int found);
        if (!Got(check, "the interop object of a source nothing else holds", found, interop)) return;
        Collect();
        int got = Get(interop, out var geometry);
        if (!Got(check, "the geometry of a source held by its interop object alone", got, geometry)) { Marshal.Release(interop); return; }
        double area = GlassPath.Area(geometry);
        check($"The interop object outlives its managed source while a reference is held (area {area:0.0})", area > 3000);
        int held = References(geometry);
        Marshal.Release(interop);
        Collect();
        int left = References(geometry);
        check($"The last reference gone, the source lets its geometry go ({held} references before, {left} after)", held == 2 && left == 1);
        Marshal.Release(geometry);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IntPtr Alone(GlassBoundaryMath.Layout layout, out int result) => InteropOf(GlassPath.GeometrySource(layout), out result);
    }

    // The compositor may ask from its own threads: queries, requests and releases at once leave the counts as they were.
    private static void Concurrent(Action<string, bool> check, object source)
    {
        var interop = InteropOf(source, out int found);
        if (!Got(check, "the interop object of a source asked from several threads", found, interop)) return;
        int first = Get(interop, out var geometry);
        if (!Got(check, "the geometry of a source asked from several threads", first, geometry)) { Marshal.Release(interop); return; }
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

    // Each step of making the path refused (V5 N02): nothing reaches the caller, the glass stays shown, cut by the rounded
    // rectangle inside the outline, clicks still pass, and the path is tried again a bounded number of times until it is back.
    private static void Faults(Action<string, bool> check)
    {
        long clock = 5_000_000;
        var clockBefore = GlassEffect.Clock;
        GlassEffect.Clock = () => clock;
        var owner = new HwndSource(new HwndSourceParameters("BrimDeck glass path checks")
        {
            WindowStyle = unchecked((int)0x80000000), PositionX = -20000, PositionY = -20000, Width = 400, Height = 200
        });
        var surfaces = new List<GlassSurface>();
        var bounds = new Int32Rect(-20000, -20000, 240, 40);
        var place = new System.Numerics.Matrix3x2(1.25f, 0, 0, 1.25f, 0, 0);
        static GlassBoundaryMath.Layout Notch(double width) => GlassBoundaryMath.Measure(width, 30, 14, 0, 0, 12, 12, 0);
        var material = new GlassSurface.Material(.4, .3, .5);
        GlassSurface New()
        {
            var surface = new GlassSurface();
            surfaces.Add(surface);
            return surface;
        }
        // What showing the notch does: shown, or what it threw.
        string Show(GlassSurface surface, double width, out bool shown)
        {
            shown = false;
            try
            {
                shown = surface.Show(owner.Handle, bounds, null, new GlassSurface.Outline(Notch(width), place), 1, material, out _);
                return shown ? "shown" : "hidden";
            }
            catch (Exception error) { return "threw " + error.GetType().Name; }
        }
        static bool Path(GlassSurface surface) => surface.Visible && surface.Clip is CompositionGeometricClip { Geometry: CompositionPathGeometry { Path: not null } };
        static bool Rounded(GlassSurface surface) => surface.Visible && surface.Clip is CompositionGeometricClip { Geometry: CompositionRoundedRectangleGeometry };
        // Brings the maker back to a fresh start: the refused steps let through, the generation renewed, a path made.
        void Fresh()
        {
            GlassPath.Inject = null;
            GlassPath.Renew();
            Show(New(), 150, out _);
        }
        (string Step, Exception Error, bool Unsupported)[] faults =
        [
            ("CreateFactory", new COMException("device removed", unchecked((int)0x887A0005)), false),
            ("CreateFactory", new OutOfMemoryException(), false),
            ("CreateFactory", new DllNotFoundException("d2d1"), true),
            ("CreateFactory", new InvalidCastException("no such interface"), true),
            ("CreatePathGeometry", new COMException("device removed", unchecked((int)0x887A0005)), false),
            ("CreatePathGeometry", new OutOfMemoryException(), false),
            ("Open", new COMException("wrong state", unchecked((int)0x88990001)), false),
            ("Close", new COMException("bad number", unchecked((int)0x88990011)), false),
            ("Close", new OutOfMemoryException(), false),
            ("Interop", new OutOfMemoryException(), false),
            ("CompositionPath", new COMException("refused", unchecked((int)0x80004005)), false),
            ("CompositionPath", new NotSupportedException("refused"), true),
            ("CompositionPathGeometry", new OutOfMemoryException(), false),
            ("CompositionGeometricClip", new COMException("refused", unchecked((int)0x80004005)), false),
            ("CompositionGeometricClip", new UnauthorizedAccessException("refused"), false),
            ("Path", new COMException("device removed", unchecked((int)0x887A0005)), false),
            ("Path", new OutOfMemoryException(), false),
        ];
        try
        {
            foreach (var (step, error, unsupported) in faults)
            {
                Fresh();
                var surface = New();
                // The setter only runs on a surface that had a path; the first path of a surface runs the other steps.
                if (step == "Path") Show(surface, 160, out _);
                int tries = 0;
                bool refuse = true;
                // The factory is made anew only after a failure: one at Open lets go of the one held.
                if (step == "CreateFactory")
                {
                    GlassPath.Inject = at => { if (at == "Open") throw new COMException("once", unchecked((int)0x88990001)); };
                    Show(New(), 158, out _);
                    GlassPath.Renew();
                }
                GlassPath.Inject = at => { if (at == step) { tries++; if (refuse) throw error; } };
                string first = Show(surface, 170, out bool shown);
                bool safe = shown && surface.Visible && Rounded(surface) && surface.Brush is not null && tries == 1;
                // Frame after frame of an opening: no further try before the backoff has passed.
                for (int i = 0; i < 20; i++) Show(surface, 171 + i, out _);
                bool waited = tries == 1 && Rounded(surface);
                // 100, 300, 1000 ms, then no more.
                var counts = new List<int>();
                int width = 200;
                foreach (int wait in new[] { 100, 300, 1000, 5000, 60_000 })
                {
                    clock += wait;
                    Show(surface, width++, out _);
                    // A refused replacement starts over with new objects, which are accepted: the next one is refused again.
                    if (step == "Path") Show(surface, width++, out _);
                    counts.Add(tries);
                }
                bool bounded = unsupported ? counts.All(c => c == 1) : counts.SequenceEqual([2, 3, 4, 4, 4]);
                // It works again: after a new generation the next frame is cut by the path.
                refuse = false;
                GlassPath.Renew();
                string back = Show(surface, 180, out bool again);
                bool recovered = again && Path(surface);
                check($"{step} refused ({error.GetType().Name}): the glass stays, cut by the rounded approximation ({first}; tries {tries}: {string.Join("/", counts)}), then is back on its path ({back})",
                    safe && waited && bounded && recovered);
            }

            // Back after a failure that passes, without a frame asking (an opening ended): by the surface's own timer.
            Fresh();
            var resting = New();
            bool once = true;
            GlassPath.Inject = at => { if (at == "Close" && once) { once = false; throw new COMException("once", unchecked((int)0x88990011)); } };
            Show(resting, 165, out _);
            bool waiting = Rounded(resting) && resting.Waking;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long from = clock;
            while (!Path(resting) && watch.ElapsedMilliseconds < 3000) { Pump(); clock = from + watch.ElapsedMilliseconds; }
            check($"A path that failed once is back without another frame ({watch.ElapsedMilliseconds} ms), and the timer stops", waiting && Path(resting) && !resting.Waking);

            // A path that came back and stayed (nothing made meanwhile) ends the streak: failures minutes apart never add up to
            // a refusal. The same failures with less than ten seconds between them, each path accepted in between, do.
            (int Failed, bool Ready) Streak(int stayed, bool others)
            {
                Fresh();
                var surface = New();
                int failed = 0, width = 300;
                for (int i = 0; i < 8; i++)
                {
                    bool fail = true;
                    GlassPath.Inject = at => { if (at == "Close" && fail) { fail = false; failed++; throw new COMException("once", unchecked((int)0x88990011)); } };
                    Show(surface, ++width, out _);
                    clock += 1000;
                    Show(surface, width, out _);
                    clock += stayed;
                    // Another panel's path made just then neither starts the stay over nor ends the streak early.
                    if (others) { clock--; Show(New(), 150, out _); clock++; }
                }
                return (failed, Path(surface));
            }
            var far = Streak(120_000, others: false);
            check($"8 failures minutes apart, the path back each time, never refuse the path ({far})", far == (8, true));
            var near = Streak(10_000, others: true);
            check($"8 failures, the path back for ten seconds each time, never refuse the path ({near})", near == (8, true));
            var soon = Streak(9_999, others: true);
            check($"8 failures, the path back for less each time, run out of tries ({soon})", soon == (4, false));
            var often = Streak(0, others: false);
            check($"Failures right after each other, the path back in between, run out of tries ({often})", often == (4, false));
            GlassPath.Inject = null;

            // Refused for good: no timer, no try.
            Fresh();
            var refused = New();
            int asked = 0;
            GlassPath.Inject = at => { if (at == "CompositionPath") { asked++; throw new NotSupportedException("refused"); } };
            Show(refused, 166, out _);
            watch.Restart();
            while (watch.ElapsedMilliseconds < 400) Pump();
            check($"A path refused for good leaves no timer and no further try ({asked})", asked == 1 && !refused.Waking && refused.Visible);

            // A hidden surface does not wait.
            Fresh();
            var hidden = New();
            GlassPath.Inject = at => { if (at == "Open") throw new COMException("refused", unchecked((int)0x88990001)); };
            Show(hidden, 167, out _);
            hidden.Hide();
            check("A hidden surface does not wait for its path", !hidden.Waking);
            GlassPath.Inject = null;

            // Nothing is left behind where a step fails: each path holds its factory, so the factory's references tell.
            Fresh();
            var sample = GlassPath.Geometry(Notch(160));
            GetFactory(sample, out var factory);
            if (!Got(check, "the factory of the paths that fail", 0, factory)) { Marshal.Release(sample); return; }
            Pump(); Collect(); Pump(); Collect();
            int alone = References(factory);
            var second = GlassPath.Geometry(Notch(161));
            int withOne = References(factory);
            Marshal.Release(second);
            int after = References(factory);
            foreach (string step in new[] { "Open", "Close", "Interop" })
            {
                GlassPath.Inject = at => { if (at == step) throw new COMException("refused", unchecked((int)0x88990001)); };
                for (int i = 0; i < 1000; i++) try { GlassPath.GeometrySource(Notch(160 + i % 40)); } catch (COMException) { }
            }
            // The sources that could not be made are finalized too (one reading memory it never got would end the process here).
            Collect();
            int left = References(factory);
            check($"1000 paths failed at Open, 1000 at Close and 1000 at their source's memory leave nothing holding the factory ({alone} references, {withOne} with a path more, {after} without, {left} after)",
                withOne > alone && after == alone && left == alone);
            Marshal.Release(sample);
            Marshal.Release(factory);
        }
        finally
        {
            GlassPath.Inject = null;
            GlassPath.Renew();
            foreach (var surface in surfaces) surface.Dispose();
            owner.Dispose();
            GlassEffect.Clock = clockBefore;
        }
    }

    // V5 P01: a move makes no path; a thousand replaced ones are let go (each holds its factory, so the factory's references
    // tell: the surface's own path at most while it shows, none once it is gone).
    private static void Replaced(Action<string, bool> check)
    {
        var owner = new HwndSource(new HwndSourceParameters("BrimDeck glass path checks")
        {
            WindowStyle = unchecked((int)0x80000000), PositionX = -20000, PositionY = -20000, Width = 400, Height = 200
        });
        var surface = new GlassSurface();
        int made = 0;
        GlassPath.Inject = step => { if (step == "CreatePathGeometry") made++; };
        try
        {
            var sample = GlassPath.Geometry(GlassBoundaryMath.Measure(150, 30, 14, 0, 0, 12, 12, 0));
            GetFactory(sample, out var factory);
            if (!Got(check, "the factory of the paths that are replaced", 0, factory)) { Marshal.Release(sample); return; }
            Collect();
            int alone = References(factory);
            bool shown = true;
            void Show(double width, float at)
                => shown &= surface.Show(owner.Handle, new Int32Rect(-20000 + (int)at, -20000, 240, 40), null, new GlassSurface.Outline(
                    GlassBoundaryMath.Measure(width, 30, 14, 0, 0, 12, 12, 0), new System.Numerics.Matrix3x2(1.25f, 0, 0, 1.25f, at % 1, 0)), 1, new GlassSurface.Material(.4, .3, .5), out _);
            Show(160, 0);
            made = 0;
            for (int i = 0; i < 1000; i++) Show(160, i * .37f % 100);
            check($"A notch moved 1000 times makes no path ({made})", shown && made == 0);
            for (int i = 0; i < 1000; i++) { Show(160 + i % 2, 0); if (i % 100 == 0) Pump(); }
            Pump(); Collect(); Pump(); Collect();
            int showing = References(factory);
            check($"1000 replaced paths are let go ({made} made, {alone} references before, {showing} after)", shown && made == 1000 - 1 && showing <= alone + 1);
            surface.Dispose();
            Pump(); Collect(); Pump(); Collect();
            check($"A surface gone leaves no path behind ({References(factory)} references)", References(factory) == alone);
            Marshal.Release(sample);
            Marshal.Release(factory);
        }
        finally
        {
            GlassPath.Inject = null;
            surface.Dispose();
            owner.Dispose();
        }
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Thread.Sleep(5);
    }

    private static void Collect()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }

    // The source's IGeometrySource2DInterop, with a reference the caller releases.
    private static IntPtr InteropOf(object source, out int result)
    {
        var outer = WinRT.MarshalInspectable<object>.FromManaged(source);
        result = Marshal.QueryInterface(outer, in Interop, out var interop);
        Marshal.Release(outer);
        return interop;
    }

    // What a check goes on from: where a call failed or left nothing, that is a failed check with its HRESULT, and what depends
    // on it is left out (nothing is called through it).
    private static bool Got(Action<string, bool> check, string what, int result, IntPtr value)
    {
        if (result == 0 && value != IntPtr.Zero) return true;
        check($"Asking for {what} answers (0x{result:X8}, {(value == IntPtr.Zero ? "nothing" : "a pointer")})", false);
        return false;
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
