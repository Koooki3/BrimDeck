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

        Faults(check, compositor);
    }

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
