using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BrimDeck;
using BrimDeck.Core;
using Windows.UI.Composition;

// Liquid glass on the real compositor: the persistent graph compiles once, takes its two amounts on any number of brushes and
// frames without compiling again, and lets every native effect go. (What it shows on screen is compared with GlassRecipe by a
// capture-based oracle outside the test suite: GDI cannot read the composed glass back.) Then the backend under injected
// faults (U09, U10, G12): a graph that stays loading or fails to load, a brush or its source refused, a frame refused, a
// failure that passes; the fallback cache; what each surface reports it shows.
internal static class GlassChecks
{
    public static void Run(Action<string, bool> check)
    {
        if (GlassComposition.Compositor is not { } compositor)
        {
            Console.WriteLine("SKIPPED: Composition is not available here, so the glass graph cannot be checked.");
            return;
        }
        var start = GlassEffect.Stats;
        var brushes = new List<CompositionEffectBrush>();
        for (int i = 0; i < 20; i++)
            if (GlassEffect.Graph(compositor) is { } brush) brushes.Add(brush);
        check("Composition accepts the persistent glass graph with its three animatable amounts", brushes.Count == 20 && GlassEffect.GraphAvailable);
        var rng = new Random(11);
        bool set = true;
        try
        {
            for (int frame = 0; frame < 2000; frame++)
                GlassEffect.Present(brushes[frame % brushes.Count], rng.NextDouble(), rng.NextDouble(), rng.NextDouble(),
                    // A clarity drag 0 -> 100 -> 0: the tint opacity and saturation move on the same factory.
                    GlassClarityPolicy.Tint(Math.Abs(frame % 200 - 100) / 100d), GlassClarityPolicy.Saturation(Math.Abs(frame % 200 - 100) / 100d));
        }
        catch (Exception) { set = false; }
        var after = GlassEffect.Stats;
        check($"2000 material frames on 20 brushes, the clarity dragged 0-100-0, compile nothing after the first graph ({after.Compiled - start.Compiled} compiled)",
            set && after.Compiled - start.Compiled <= 1 && after.Brushes - start.Brushes == 20);
        foreach (var brush in brushes) GlassEffect.Release(brush);
        brushes.Clear();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        var released = GlassEffect.Stats;
        check($"Releasing the brushes leaves only the graph's own native effects ({released.Live} alive)", released.Live <= 4);

        PassesClicks(check, notch: false);
        PassesClicks(check, notch: true);
        ShoulderClip(check);
        GlassPathChecks.Run(check);
        Faults(check, compositor);
    }

    // The collapsed notch (shoulders) is cut by the glass layer's own antialiased path, the WPF outline's curve, at the panel's
    // fraction of a pixel, and the glass window has no region: until V4-2 the shoulders were a polygon region of whole pixels,
    // stepped against the smooth edge drawn on it and set again on almost every frame of a closing.
    private static void ShoulderClip(Action<string, bool> check)
    {
        var owner = new HwndSource(new HwndSourceParameters("BrimDeck glass shoulder checks")
        {
            WindowStyle = unchecked((int)0x80000000), PositionX = -20000, PositionY = -20000, Width = 300, Height = 80
        });
        var surface = new GlassSurface();
        try
        {
            // A notch at 125 % placed a third of a pixel in.
            var layout = GlassBoundaryMath.Measure(160, 30, 14, 0, 0, 12, 12, 0);
            var place = new System.Numerics.Matrix3x2(1.25f, 0, 0, 1.25f, 20.33f, 0);
            var bounds = new Int32Rect(-20000, -20000, 240, 40);
            bool shown = surface.Show(owner.Handle, bounds, null, new GlassSurface.Outline(layout, place), 1, new GlassSurface.Material(.4, .3, .5), out _);
            Pump();
            var glass = GlassWindow();
            int region = glass == IntPtr.Zero ? -1 : GetWindowRgnBox(glass, out _);
            check($"A notch's glass window has no window region (region box type {region}; 0: none)", shown && glass != IntPtr.Zero && region == 0);
            bool path = surface.Clip is CompositionGeometricClip { Geometry: CompositionPathGeometry { Path: not null } } clip && clip.TransformMatrix == place;
            check("A notch's glass is cut by its own path clip, placed at the panel's fraction of a pixel", path);

            // The path is the WPF outline: the same area and the same inside at points on a fine grid away from the line.
            var wpf = IslandBorder.BuildOutline(160, 30, 14, new CornerRadius(0, 0, 12, 12), 0);
            var geometry = GlassPath.Geometry(layout);
            try
            {
                double area = GlassPath.Area(geometry), wpfArea = wpf.GetArea(.001, System.Windows.Media.ToleranceType.Absolute);
                int differ = 0, tried = 0;
                var flat = wpf.GetFlattenedPathGeometry(.001, System.Windows.Media.ToleranceType.Absolute);
                var near = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, .02);
                for (double y = -1; y <= 31; y += .37)
                    for (double x = -1; x <= 161; x += .41)
                    {
                        var at = new Point(x, y);
                        if (flat.StrokeContains(near, at)) continue;
                        tried++;
                        if (GlassPath.Fills(geometry, x, y) != flat.FillContains(at)) differ++;
                    }
                check($"The notch's clip path is the WPF outline (area {area:0.00} against {wpfArea:0.00}; {differ} of {tried} points differ)",
                    Math.Abs(area - wpfArea) < .001 * wpfArea && differ == 0 && tried > 20_000);
            }
            finally { Marshal.Release(geometry); }
        }
        finally
        {
            surface.Dispose();
            owner.Dispose();
        }
    }

    private static IntPtr GlassWindow()
    {
        IntPtr glass = IntPtr.Zero;
        EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            var title = new System.Text.StringBuilder(64);
            GetWindowText(window, title, 64);
            if (IsWindowVisible(window) && title.ToString() == "BrimDeck glass") glass = window;
            return glass == IntPtr.Zero;
        }, IntPtr.Zero);
        return glass;
    }

    [DllImport("user32")] private static extern int GetWindowRgnBox(IntPtr window, out Rect32 rect);

    // The glass window covers the whole panel window behind it, which lets clicks through where it draws nothing: another
    // process's hit test must pass over the glass window everywhere, beside the outline and on it, as it passes over the panel's
    // clear pixels. Until 11-23 the whole top of the panel's display took no clicks while the glass showed. WindowFromPoint is
    // asked from another thread: on its own thread a window's HTTRANSPARENT is honoured, which the apps beneath never see.
    // With a notch: its path refused, so the glass is cut by the rounded approximation (V5 N02).
    private static void PassesClicks(Action<string, bool> check, bool notch)
    {
        var area = SystemParameters.WorkArea;
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(new System.Windows.Controls.Border()).DpiScaleX;
        int right = (int)(area.Right * dpi), bottom = (int)(area.Bottom * dpi);
        // A small panel window in the corner of the work area, topmost and passing clicks (layered, transparent and empty), as
        // the panel's window is where it draws nothing; the glass is nearly invisible (opacity 0.01) and shown for a moment.
        var owner = new HwndSource(new HwndSourceParameters("BrimDeck glass click checks")
        {
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08 | 0x80 | 0x08000000 | 0x20, UsesPerPixelOpacity = true,
            PositionX = right - 160, PositionY = bottom - 80, Width = 160, Height = 80
        });
        var surface = new GlassSurface();
        try
        {
            var bounds = new Int32Rect(right - 120, bottom - 60, 80, 40);
            if (notch) GlassPath.Inject = step => { if (step == "CreatePathGeometry") throw new COMException("refused", unchecked((int)0x887A0005)); };
            bool shown = notch
                ? surface.Show(owner.Handle, bounds, null, new GlassSurface.Outline(GlassBoundaryMath.Measure(80, 40, 14, 0, 0, 8, 8, 0), System.Numerics.Matrix3x2.Identity), .01,
                    new GlassSurface.Material(.4, .3, .5), out _)
                : surface.Show(owner.Handle, bounds, 8, .01, new GlassSurface.Material(.4, .3, .5), out _);
            Pump();
            IntPtr glass = IntPtr.Zero;
            EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
            {
                var title = new System.Text.StringBuilder(64);
                GetWindowText(window, title, 64);
                if (IsWindowVisible(window) && title.ToString() == "BrimDeck glass") glass = window;
                return glass == IntPtr.Zero;
            }, IntPtr.Zero);
            GetWindowRect(glass, out var frame);
            var points = new[] { (X: right - 150, Y: bottom - 70, Where: "beside the outline"), (X: right - 80, Y: bottom - 40, Where: "on the outline") };
            var found = new List<string>();
            bool covered = true, passed = true;
            foreach (var (x, y, where) in points)
            {
                covered &= x >= frame.Left && x < frame.Right && y >= frame.Top && y < frame.Bottom;
                IntPtr hit = IntPtr.Zero;
                var asking = new Thread(() => hit = GetAncestor(WindowFromPoint(new Point32 { X = x, Y = y }), 2));
                asking.Start(); asking.Join();
                passed &= hit != glass && hit != owner.Handle;
                found.Add($"{where}: {(hit == glass ? "the glass window" : hit == owner.Handle ? "the panel window" : "beneath")}");
            }
            check($"The glass window{(notch ? " of a notch without its path" : "")} lets clicks through to what lies beneath, beside the outline and on it ({string.Join("; ", found)})",
                shown && glass != IntPtr.Zero && covered && passed);
        }
        finally
        {
            GlassPath.Inject = null;
            GlassPath.Renew();
            surface.Dispose();
            owner.Dispose();
        }
    }

    private delegate bool ThreadWindows(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Point32 { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] private static extern bool EnumThreadWindows(uint thread, ThreadWindows callback, IntPtr data);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int length);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);
    [DllImport("user32")] private static extern IntPtr WindowFromPoint(Point32 point);
    [DllImport("user32")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Thread.Sleep(5);
    }

    // A fallback look, once it has loaded.
    private static CompositionBrush? Loaded(Compositor compositor, GlassRecipe.Look look)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        CompositionBrush? brush;
        while ((brush = GlassEffect.Acrylic(compositor, look)) is null && watch.ElapsedMilliseconds < 2000) Pump();
        return brush;
    }

    private static void Faults(Action<string, bool> check, Compositor compositor)
    {
        long clock = 1_000_000;
        var clockBefore = GlassEffect.Clock;
        GlassEffect.Clock = () => clock;
        int attempts = 0;
        var owner = new HwndSource(new HwndSourceParameters("BrimDeck glass checks")
        {
            WindowStyle = unchecked((int)0x80000000), PositionX = -20000, PositionY = -20000, Width = 200, Height = 100
        });
        var bounds = new Int32Rect(-20000, -20000, 200, 100);
        var surfaces = new List<GlassSurface>();
        var held = GlassEffect.Held;
        GlassSurface Open(GlassSurface.Material material, out GlassSurface.AppliedMaterial applied)
        {
            var surface = new GlassSurface();
            surfaces.Add(surface);
            if (!surface.Show(owner.Handle, bounds, 8, 1, material, out applied)) applied = default;
            return surface;
        }
        GlassSurface.AppliedMaterial Present(GlassSurface surface, GlassSurface.Material material)
            => surface.Present(material, out var applied) ? applied : default;
        static bool Drawn(GlassSurface surface) => surface.Brush is not null;
        // A factory loads while the UI thread pumps (Pending until then): pump, looking again as the surface's timer would,
        // until the surface shows what is wanted (at most two seconds of real time; the injected clock does not move).
        static bool Settle(GlassSurface surface, GlassSurface.Backend want)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (surface.Applied.Backend != want && watch.ElapsedMilliseconds < 2000)
            {
                Pump();
                surface.Poll();
            }
            return surface.Applied.Backend == want;
        }
        var asked = new GlassSurface.Material(.33, .41, .27);
        var stepped = GlassEffect.StepMaterial(asked.DarkVeil, asked.LightVeil, asked.Palette);
        var steppedMaterial = new GlassSurface.Material(stepped.DarkVeil, stepped.LightVeil, stepped.Palette);
        try
        {
            // The graph: Composition reports a new factory Pending until the UI thread pumps, and Pending is not success; once
            // loaded, what it shows is what was asked for.
            GlassEffect.Renew();
            // Retire the graph compiled above, so the surface below gets a fresh factory.
            if (GlassEffect.Graph(compositor) is { } stale) { GlassEffect.Failed(stale, permanent: false); GlassEffect.Release(stale); }
            GlassEffect.Renew();
            var a = Open(asked, out var shown);
            bool loadingFirst = shown.Backend == GlassSurface.Backend.Opaque && Drawn(a);
            var next = new GlassSurface.Material(.5, .2, .6);
            check($"A new graph shows an opaque ground while Composition loads it ({shown.Backend}), then the graph and the material asked for",
                loadingFirst && Settle(a, GlassSurface.Backend.Graph) && a.Applied.Material == asked
                && Present(a, next) is { Backend: GlassSurface.Backend.Graph } p && p.Material == next);
            // Prewarmed when liquid glass is chosen: by the time a panel shows, the graph has loaded and no opaque ground flashes.
            if (GlassEffect.Graph(compositor) is { } loaded) { GlassEffect.Failed(loaded, permanent: false); GlassEffect.Release(loaded); }
            GlassEffect.Renew();
            GlassEffect.Prewarm(compositor);
            var warm = System.Diagnostics.Stopwatch.StartNew();
            while (warm.ElapsedMilliseconds < 400) Pump();
            Open(asked, out var warmShown);
            check($"A prewarmed graph is shown at once, without the opaque loading ground ({warmShown.Backend})", warmShown.Backend == GlassSurface.Backend.Graph);
            // The fallback step the faults below fall back to, loaded beforehand (its first load is Pending as well).
            GlassEffect.Release(Loaded(compositor, GlassEffect.StepLook(asked.DarkVeil, asked.LightVeil, asked.Palette)));

            // Pending, then Error (U09): loading shows an opaque ground, not the brush; the error degrades to the stepped
            // fallback, reported as its steps; nothing is retried until the generation changes.
            GlassEffect.Renew();
            bool error = false;
            GlassEffect.InjectStatus = (graph, status) => graph ? error ? CompositionEffectFactoryLoadStatus.Other : CompositionEffectFactoryLoadStatus.Pending : status;
            GlassEffect.Inject = step => { if (step == "CreateBrush") attempts++; };
            var b = Open(asked, out var loading);
            check($"A loading graph is not taken for success: the surface shows an opaque ground meanwhile ({loading.Backend}, {GlassEffect.GraphState})",
                loading.Backend == GlassSurface.Backend.Opaque && loading.Material == new GlassSurface.Material(1, 1, asked.Palette) && Drawn(b)
                && GlassEffect.GraphState == GlassBackendState.Loading);
            error = true;
            clock += 100;
            b.Poll();
            var degraded = b.Applied;
            bool noRetry = attempts == 1;
            check($"Pending then Error degrades to the fallback, reported as its quantised steps ({degraded.Material})",
                degraded.Backend == GlassSurface.Backend.Fallback && degraded.Material == steppedMaterial && degraded.Material != asked && Drawn(b)
                && GlassEffect.GraphState == GlassBackendState.Degraded && !GlassEffect.GraphAvailable && noRetry);
            check("The failed factory is not disposed under the surface still drawing with it",
                GlassEffect.FactoryLive(a.Brush) && Present(a, asked).Backend == GlassSurface.Backend.Graph);
            attempts = 0;
            for (int i = 0; i < 50; i++) { clock += 500; Present(b, new(i / 60d, .3, i / 50d)); }
            check($"A degraded graph is not retried frame after frame ({attempts} attempts in 50 frames)", attempts == 0);
            GlassEffect.InjectStatus = null;
            GlassEffect.Renew();
            var retried = Present(b, asked);
            check($"A new display or capability generation tries the graph again on the same surface (the step kept while it loads: {retried.Backend})",
                retried.Backend == GlassSurface.Backend.Fallback && attempts == 1 && Settle(b, GlassSurface.Backend.Graph) && b.Applied.Material == asked);

            // A brush refused on a device that is changing (COMException): backoff 100/300/1000 ms, bounded, then recovery.
            GlassEffect.Renew();
            attempts = 0;
            bool lost = true;
            GlassEffect.Inject = step =>
            {
                if (step != "CreateBrush") return;
                attempts++;
                if (lost) throw new COMException("device removed", unchecked((int)0x887A0005));
            };
            var c = Open(asked, out var lostShown);
            for (int i = 0; i < 20; i++) Present(c, asked);
            bool first = attempts == 1 && lostShown.Backend == GlassSurface.Backend.Fallback && GlassEffect.GraphState == GlassBackendState.Recovering;
            clock += 99; Present(c, asked);
            bool waited = attempts == 1;
            clock += 1; Present(c, asked);
            clock += 299; Present(c, asked);
            bool second = attempts == 2;
            clock += 1; Present(c, asked);
            clock += 1000; Present(c, asked);
            bool bounded = attempts == 4 && GlassEffect.GraphState == GlassBackendState.Degraded;
            for (int i = 0; i < 20; i++) { clock += 5000; Present(c, asked); }
            check($"A transient brush failure backs off 100/300/1000 ms and gives up after {GlassEffect.GraphTries} tries ({attempts} attempts)",
                first && waited && second && bounded && attempts == 4 && Drawn(c) && c.Applied.Backend == GlassSurface.Backend.Fallback);
            lost = false;
            GlassEffect.Renew();
            Present(c, asked);
            check("Once the device is back the surface returns to the graph and the failure streak is cleared",
                Settle(c, GlassSurface.Backend.Graph) && c.Applied.Material == asked && GlassEffect.GraphState == GlassBackendState.Ready);

            // The source refused (an argument the device will never take): permanent for this generation, nothing leaked.
            GlassEffect.Renew();
            attempts = 0;
            var before = GlassEffect.Held.Brushes;
            GlassEffect.Inject = step =>
            {
                if (step == "SetSource") { attempts++; throw new ArgumentException("refused"); }
            };
            var d = Open(asked, out var refused);
            for (int i = 0; i < 20; i++) { clock += 2000; Present(d, asked); }
            check($"A refused source degrades at once, tried once, and leaks no brush ({attempts} tries, {GlassEffect.Held.Brushes - before} brushes held)",
                refused.Backend == GlassSurface.Backend.Fallback && attempts == 1 && GlassEffect.GraphState == GlassBackendState.Degraded
                && GlassEffect.Held.Brushes - before == 1);

            // A frame refused: the same frame falls back (never a surface without a brush), the other surfaces keep the graph,
            // and the graph comes back after the backoff.
            GlassEffect.Inject = null;
            GlassEffect.Renew();
            var e = Open(asked, out _);
            bool eReady = Settle(e, GlassSurface.Backend.Graph);
            bool armed = true;
            GlassEffect.Inject = step =>
            {
                if (step == "Present" && armed) { armed = false; throw new COMException("frame refused", unchecked((int)0x8899000C)); }
            };
            // (Another material in the same fallback step as asked, which is loaded.)
            var refusedFrame = new GlassSurface.Material(.34, .44, .26);
            bool presented = e.Present(refusedFrame, out var fellBack);
            check($"A refused frame still presents: the same frame shows the fallback ({fellBack.Backend})",
                eReady && presented && fellBack.Backend == GlassSurface.Backend.Fallback && fellBack.Material == steppedMaterial && Drawn(e)
                && GlassEffect.GraphState == GlassBackendState.Recovering);
            check("The other surfaces keep drawing with the graph", Present(a, asked).Backend == GlassSurface.Backend.Graph && GlassEffect.FactoryLive(a.Brush));
            clock += 100;
            Present(e, asked);
            check("After the backoff the refused surface is back on the graph", Settle(e, GlassSurface.Backend.Graph));

            // Two surfaces loading the same factory: the first one's first frame is refused, so the factory is let go and the
            // backoff starts; the second presents its brush of it all the same, and ends neither the backoff nor the streak.
            void Two(out GlassSurface one, out GlassSurface two, out bool drawn)
            {
                GlassEffect.Inject = null;
                GlassEffect.Renew();
                if (GlassEffect.Graph(compositor) is { } made) { GlassEffect.Failed(made, permanent: false); GlassEffect.Release(made); }
                GlassEffect.Renew();
                bool held = true;
                var actual = CompositionEffectFactoryLoadStatus.Pending;
                GlassEffect.InjectStatus = (graph, status) =>
                {
                    if (!graph) return status;
                    actual = status;
                    return held ? CompositionEffectFactoryLoadStatus.Pending : status;
                };
                one = Open(asked, out _);
                two = Open(asked, out _);
                var loads = System.Diagnostics.Stopwatch.StartNew();
                while (actual != CompositionEffectFactoryLoadStatus.Success && loads.ElapsedMilliseconds < 2000) { Pump(); one.Poll(); }
                held = false;
                bool refuse = true;
                GlassEffect.Inject = step =>
                {
                    if (step == "Present" && refuse) { refuse = false; throw new COMException("frame refused", unchecked((int)0x8899000C)); }
                };
                one.Poll();
                two.Poll();
                GlassEffect.InjectStatus = null;
                drawn = !refuse && two.Applied.Backend == GlassSurface.Backend.Graph && one.Applied.Backend == GlassSurface.Backend.Fallback;
            }
            Two(out var one, out var two, out bool twoDrawn);
            bool backing = GlassEffect.GraphState == GlassBackendState.Recovering && GlassEffect.RetryAt == clock + 100 && !GlassEffect.CanTryGraph(compositor);
            clock += 99;
            bool waits = !GlassEffect.CanTryGraph(compositor);
            check($"A brush of a factory let go presents, but ends no backoff of the one after it ({two.Applied.Backend}, {GlassEffect.GraphState})",
                twoDrawn && backing && waits && GlassEffect.FactoryLive(two.Brush));
            clock += 1;
            one.Poll();
            check($"The factory made after the backoff ends it once it presents ({GlassEffect.GraphState})",
                Settle(one, GlassSurface.Backend.Graph) && GlassEffect.GraphState == GlassBackendState.Ready && GlassEffect.RetryAt == 0);
            one.Hide();
            two.Hide();
            GlassEffect.Inject = null;

            // Pending for good: the loading limit turns it into a transient failure (no endless wait).
            GlassEffect.Inject = null;
            GlassEffect.Renew();
            GlassEffect.InjectStatus = (graph, status) => graph ? CompositionEffectFactoryLoadStatus.Pending : status;
            var f = Open(asked, out _);
            clock += 1000; f.Poll();
            bool stillLoading = f.Applied.Backend == GlassSurface.Backend.Opaque;
            clock += 1000; f.Poll();
            check($"A graph that never loads is given up after the loading limit ({f.Applied.Backend}, {GlassEffect.GraphState})",
                stillLoading && f.Applied.Backend == GlassSurface.Backend.Fallback && GlassEffect.GraphState == GlassBackendState.Recovering && Drawn(f));
            GlassEffect.InjectStatus = null;

            // No compositor means no glass: a hidden or disposed surface does not report a backend.
            f.Hide();
            check("A hidden surface presents nothing and reports no backend", !f.Present(asked, out var none) && none.Backend == GlassSurface.Backend.None);

            // Every new factory loads, but each first frame on it is refused (a device that keeps changing): a load is no
            // success, so the streak still runs out after GraphTries and the graph degrades instead of recompiling for ever.
            GlassEffect.Renew();
            int frames = 0;
            GlassEffect.Inject = step =>
            {
                if (step == "Present") { frames++; throw new COMException("frame refused", unchecked((int)0x8899000C)); }
            };
            var g = Open(asked, out _);
            for (int cycle = 0; cycle < 8; cycle++)
            {
                // The factory made for this try loads (real time; the injected clock waits), then the backoff passes.
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (GlassEffect.GraphState == GlassBackendState.Loading && watch.ElapsedMilliseconds < 2000) { Pump(); g.Poll(); }
                clock += 2000;
                Present(g, asked);
            }
            check($"A graph whose every first frame is refused degrades after {GlassEffect.GraphTries} tries although each factory loads ({frames} frames tried, {GlassEffect.GraphState})",
                frames == GlassEffect.GraphTries && GlassEffect.GraphState == GlassBackendState.Degraded && g.Applied.Backend == GlassSurface.Backend.Fallback && Drawn(g));
            g.Hide();
            GlassEffect.Inject = null;

            // No frame asks any more (an animation ended): what the surface still waits for shows by its own timer, and the
            // timer stops once it has. The injected clock follows real time meanwhile.
            bool Idle(Func<bool> done)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                long from = clock;
                while (!done() && watch.ElapsedMilliseconds < 3000) { Pump(); clock = from + watch.ElapsedMilliseconds; }
                return done();
            }
            // A fallback step asked for by the last frame, still loading: the step shown stays meanwhile, then the one asked.
            GlassEffect.Renew();
            GlassEffect.CanTryGraph(compositor);
            GlassEffect.RefuseGraph();
            var h = Open(asked, out var hShown);
            var unloaded = new GlassSurface.Material(.62, .07, .58);
            var unloadedStep = GlassEffect.StepMaterial(unloaded.DarkVeil, unloaded.LightVeil, unloaded.Palette);
            bool keptStep = h.Present(unloaded, out var kept) && kept.Backend == GlassSurface.Backend.Fallback && kept.Material == steppedMaterial;
            check($"A fallback step that loads after the last frame is shown without another frame ({h.Applied.Material})",
                hShown.Backend == GlassSurface.Backend.Fallback && keptStep
                && Idle(() => h.Applied.Material == new GlassSurface.Material(unloadedStep.DarkVeil, unloadedStep.LightVeil, unloadedStep.Palette))
                && !h.Waking);
            h.Hide();
            // The graph backing off under a fallback: tried again when the backoff has passed, without another frame.
            GlassEffect.Renew();
            var k = Open(asked, out _);
            bool kReady = Settle(k, GlassSurface.Backend.Graph);
            bool kArmed = true;
            GlassEffect.Inject = step =>
            {
                if (step == "Present" && kArmed) { kArmed = false; throw new COMException("frame refused", unchecked((int)0x8899000C)); }
            };
            bool kFellBack = k.Present(refusedFrame, out var kShown) && kShown.Backend == GlassSurface.Backend.Fallback;
            check($"A graph backing off under a fallback comes back after the backoff without another frame ({k.Applied.Backend}, {GlassEffect.GraphState})",
                kReady && kFellBack && Idle(() => k.Applied.Backend == GlassSurface.Backend.Graph) && k.Applied.Material == refusedFrame && !k.Waking);
            k.Hide();
            GlassEffect.Inject = null;

            // Hidden before its timer ticked, again and again: shown for good it still looks again (a loading graph, a loading
            // fallback step, a graph backing off), a bounded number of times, and shows what then loads.
            int Ticks(GlassSurface surface)
            {
                int ticks = 0;
                for (; surface.Waking && ticks < 50; ticks++) surface.Tick();
                return ticks;
            }
            bool Again(GlassSurface surface, GlassSurface.Material material)
            {
                for (int i = 0; i < 12; i++) { surface.Hide(); if (surface.Waking || !surface.Show(owner.Handle, bounds, 8, 1, material, out _)) return false; }
                return surface.Waking;
            }
            GlassEffect.Renew();
            GlassEffect.InjectStatus = (graph, status) => graph ? CompositionEffectFactoryLoadStatus.Pending : status;
            var m = Open(asked, out _);
            bool graphLooks = Again(m, asked);
            int graphTicks = Ticks(m);
            bool graphAgain = Again(m, asked);
            GlassEffect.InjectStatus = null;
            check($"A surface hidden before it looked again at its loading graph, again and again, still looks ({graphTicks} times), and shows the graph without another frame",
                graphLooks && graphTicks == 4 && graphAgain && Idle(() => m.Applied.Backend == GlassSurface.Backend.Graph) && !m.Waking);
            m.Hide();
            GlassEffect.Renew();
            GlassEffect.CanTryGraph(compositor);
            GlassEffect.RefuseGraph();
            GlassEffect.InjectStatus = (graph, status) => graph ? status : CompositionEffectFactoryLoadStatus.Pending;
            var late = new GlassSurface.Material(.71, .12, .42);
            var n = Open(late, out _);
            bool stepLooks = Again(n, late);
            int stepTicks = Ticks(n);
            bool stepAgain = Again(n, late);
            GlassEffect.InjectStatus = null;
            check($"A surface hidden before it looked again at its fallback step, again and again, still looks ({stepTicks} times), and shows the step without another frame",
                stepLooks && stepTicks == 4 && stepAgain && Idle(() => n.Applied.Backend == GlassSurface.Backend.Fallback) && !n.Waking);
            n.Hide();
            GlassEffect.Renew();
            GlassEffect.Inject = step => { if (step == "CreateBrush") throw new COMException("device removed", unchecked((int)0x887A0005)); };
            var o = Open(asked, out _);
            bool retryLooks = Again(o, asked) && GlassEffect.GraphState == GlassBackendState.Recovering;
            int retryTicks = Ticks(o);
            bool retryAgain = Again(o, asked);
            GlassEffect.Inject = null;
            check($"A surface hidden before its graph's backoff passed, again and again, still looks ({retryTicks} times), and shows the graph without another frame",
                retryLooks && retryTicks == 3 && retryAgain && Idle(() => o.Applied.Backend == GlassSurface.Backend.Graph) && !o.Waking);
            o.Hide();

            // The backoff ends while the frame is worked out (the graph refused it, then the clock passed the deadline): the last
            // frame's fallback is looked at again all the same; a graph refused for good is not.
            GlassEffect.Renew();
            GlassEffect.Inject = step => { if (step == "CreateBrush") throw new COMException("device removed", unchecked((int)0x887A0005)); };
            var q = Open(asked, out _);
            bool waitsBackoff = q.Waking && GlassEffect.GraphState == GlassBackendState.Recovering;
            clock = GlassEffect.RetryAt - 1;
            int reads = 0;
            GlassEffect.Clock = () => reads++ == 0 ? clock : clock + 1;
            q.Tick();
            bool passed = reads > 1 && q.Waking && q.Applied.Backend == GlassSurface.Backend.Fallback;
            GlassEffect.Clock = () => clock;
            clock++;
            GlassEffect.Inject = null;
            check($"A backoff that ends while a frame is worked out is still looked at again ({reads} reads of the clock), and shows the graph without another frame",
                waitsBackoff && passed && Idle(() => q.Applied.Backend == GlassSurface.Backend.Graph) && !q.Waking);
            q.Hide();
            GlassEffect.Renew();
            int refusals = 0;
            GlassEffect.Inject = step => { if (step == "CreateBrush") { refusals++; throw new COMException("device removed", unchecked((int)0x887A0005)); } };
            var s = Open(asked, out _);
            // Every look past the backoff: the tries are the same four, then no timer.
            int looks = 0;
            for (; s.Waking && looks < 50; looks++) { clock += 1000; s.Tick(); }
            check($"The looks end with the tries ({refusals} tries, {looks} looks)", refusals == GlassEffect.GraphTries && looks == GlassEffect.GraphTries - 1 && !s.Waking);
            GlassEffect.Renew();
            GlassEffect.Inject = step => { if (step == "CreateBrush") throw new NotSupportedException("refused"); };
            var t = Open(asked, out _);
            check($"A graph refused for good sets no timer ({GlassEffect.GraphState})", t.Applied.Backend == GlassSurface.Backend.Fallback && !t.Waking);
            s.Hide();
            t.Hide();
            GlassEffect.Inject = null;

            // A surface still on a brush of a factory let go, whose backoff another surface already ended: losing that brush, it
            // looks again too, though nothing backs off.
            Two(out one, out two, out twoDrawn);
            clock += 100;
            one.Poll();
            bool ended = Settle(one, GlassSurface.Backend.Graph) && GlassEffect.RetryAt == 0;
            bool twoArmed = true;
            GlassEffect.Inject = step => { if (step == "Present" && twoArmed) { twoArmed = false; throw new COMException("frame refused", unchecked((int)0x8899000C)); } };
            bool twoFell = two.Present(refusedFrame, out var twoShown) && twoShown.Backend == GlassSurface.Backend.Fallback;
            check("A surface that loses a brush of a factory let go, nothing backing off, shows the new graph without another frame",
                twoDrawn && ended && twoFell && Idle(() => two.Applied.Backend == GlassSurface.Backend.Graph) && !two.Waking);
            one.Hide();
            two.Hide();
            GlassEffect.Inject = null;

            // V4 F03: the graph refused, the stepped fallback still follows the clarity: at 0, 50 and 100 it shows (and reports)
            // the clarity's tint opacity and saturation, not the defaults, so the slider's ends keep their meaning after the
            // glass degrades; each weight within its step of what was asked.
            GlassEffect.Renew();
            GlassEffect.CanTryGraph(compositor);
            GlassEffect.RefuseGraph();
            var faithful = new List<string>();
            bool follows = true;
            double stepWorst = 0, graphWorst = 0;
            (double R, double G, double B)[] backdrops = [(1, 1, 1), (0, 0, 0), (.5, .5, .5), (.78, .27, .04), (.1, .4, .9), (.95, .9, .2)];
            foreach (int clarity in new[] { 0, 50, 100 })
            {
                double u = clarity / 100d;
                foreach (double palette in new[] { 0d, 1d, .5 })
                {
                    var wanted = new GlassSurface.Material(.41, .23, palette, GlassClarityPolicy.Tint(u), GlassClarityPolicy.Saturation(u));
                    var surface = Open(wanted, out _);
                    bool degradedTo = Settle(surface, GlassSurface.Backend.Fallback);
                    var got = surface.Applied.Material;
                    bool near = degradedTo && Math.Abs(got.Tint - wanted.Tint) <= .005 + 1e-9 && Math.Abs(got.Saturation - wanted.Saturation) <= .025 + 1e-9
                        && got.DarkVeil >= wanted.DarkVeil && got.DarkVeil - wanted.DarkVeil < .05 && got.LightVeil >= wanted.LightVeil
                        && got.LightVeil - wanted.LightVeil < .05 && Math.Abs(got.Palette - wanted.Palette) <= 1 / 24d + 1e-9;
                    // What the fallback draws (its stepped look's matrix) against the graph's recipe: the same for the stepped
                    // weights it reports (the turn's middle mixes the two looks' greys, close), and within the steps of the asked.
                    var matrix = GlassRecipe.Matrix(GlassEffect.StepLook(wanted.DarkVeil, wanted.LightVeil, wanted.Palette, wanted.Tint, wanted.Saturation));
                    foreach (var backdrop in backdrops)
                    {
                        var drawn = GlassRecipe.Apply(matrix, backdrop);
                        var reported = GlassRecipe.Shown(backdrop, got.DarkVeil, got.LightVeil, got.Palette, got.Tint, got.Saturation);
                        var graph = GlassRecipe.Shown(backdrop, wanted.DarkVeil, wanted.LightVeil, wanted.Palette, wanted.Tint, wanted.Saturation);
                        static double Gap((double R, double G, double B) a, (double R, double G, double B) b)
                            => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                        double step = Gap(drawn, reported);
                        if (palette is 0 or 1 ? step > 1e-4 : step > .03) near = false;
                        stepWorst = Math.Max(stepWorst, palette is 0 or 1 ? step : 0);
                        graphWorst = Math.Max(graphWorst, Gap(drawn, graph));
                    }
                    follows &= near;
                    faithful.Add(FormattableString.Invariant($"{clarity}/{palette:0.0}: tint {got.Tint:0.00} sat {got.Saturation:0.00}"));
                    surface.Hide();
                }
            }
            check(FormattableString.Invariant($"The graph refused, the stepped fallback keeps the clarity's tint and saturation at 0, 50 and 100 and draws the graph's recipe for its steps ({string.Join("; ", faithful)}; step gap {stepWorst:0.00000}, to the asked {graphWorst:0.000})"),
                follows && graphWorst <= .06);
        }
        finally
        {
            GlassEffect.Inject = null;
            GlassEffect.InjectStatus = null;
            foreach (var surface in surfaces) surface.Dispose();
            owner.Dispose();
            GlassEffect.Clock = clockBefore;
            GlassEffect.Renew();
        }
        check($"Every brush of the surfaces is released ({GlassEffect.Held.Brushes - held.Brushes} left over)", GlassEffect.Held.Brushes == held.Brushes);

        // The fallback cache (U10): least recently used out one at a time, never a look a surface shows.
        int limit = GlassEffect.FallbackLimit;
        try
        {
            GlassEffect.FallbackLimit = 4;
            var looks = Enumerable.Range(0, 9).Select(i => GlassRecipe.Dark with { LuminosityOpacity = .5 + i / 1000d }).ToArray();
            for (int i = 0; i < 4; i++) GlassEffect.Release(Loaded(compositor, looks[i]));
            int compiled = GlassEffect.Stats.Compiled;
            GlassEffect.Release(Loaded(compositor, looks[4]));
            for (int i = 1; i <= 3; i++) GlassEffect.Release(Loaded(compositor, looks[i]));
            int recompiled = GlassEffect.Stats.Compiled - compiled;
            check($"One look past the cache's limit lets only the least recently used go ({recompiled} compiled for 4 requests)", recompiled == 1);
            var shown = Loaded(compositor, looks[1]);
            for (int i = 5; i < 9; i++) GlassEffect.Release(Loaded(compositor, looks[i]));
            check($"A look still shown is never evicted or disposed ({GlassEffect.FallbackCached} cached)",
                GlassEffect.FactoryLive(shown) && GlassEffect.FallbackCached <= 5);
            GlassEffect.Release(shown);
        }
        finally { GlassEffect.FallbackLimit = limit; }

        // Every failed, evicted or replaced factory was disposed, and their native effects are gone.
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        var (heldBrushes, factories) = GlassEffect.Held;
        check($"After the faults only the cached factories are alive ({factories} for {GlassEffect.FallbackCached} cached looks, {GlassEffect.Stats.Live} native effects)",
            heldBrushes == held.Brushes && factories <= 1 + GlassEffect.FallbackCached && GlassEffect.Stats.Live <= 4 + GlassEffect.FallbackCached);
    }
}
