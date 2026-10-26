using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;

// The liquid glass's answers to the pointer and its accessibility (the design reference's interaction table and its
// "reduce transparency, reduce motion"), on the real panel offscreen as in LiquidPages (BrimDeck's App never run, its
// MainWindow's stage in a source never shown, GlassBackdrop deciding through its stand-in host):
//   hover: the glass brightens on the monitor's hover time and its light comes from the pointer, in device pixels from the
//     glass's corner; it fades out after the pointer left, its light kept until then; none on the open panel.
//   press: the jelly (a small squash) and a stronger inner light eased on the press times; both let go.
//   the content row (GlassBackdrop.ContentArea): the collapsed row's parts, inside the island; none open.
//   expand and collapse: the glass's openness follows the island's opening motion and lands on 1, and 0 again.
//   the shadow after opening (R3 NEW-8): back from the first frame or two after the island comes to rest, kept as a picture
//     (its cache) from the fade's first frame, whole Soft.Quick later.
//   animations off: no jelly, no morph, hover and press and the openness (the frost) at their end at once.
//   the acrylic body (the default) makes no Direct3D device at all, and liquid glass then the dark theme leaves none alive,
//     the process's threads and commit back near where they were before the liquid theme (the memory session's finding: a
//     device kept, about 16 driver threads and 40 MB more, after liquid glass was used once);
//   transparency effects off (simulated: MotionEnvironment.SimulateTransparency, the setting is not touched): no glass, the
//     island's surface solid and whole, and a GlassBackdrop on a real (off-screen) window makes neither a window nor a capture.
// Animations on needs Windows' own "animation effects" on; with them off those checks are skipped (NOTE).
//   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --glass-access
internal static class GlassAccess
{
    private static int _failures;
    // MainWindow.ShadowSettleMs, the wait the shadow took after opening before R3 NEW-8.
    private const int ShadowSettleMs = 250;

    public static int Run()
    {
        var (app, deck, root, source) = LiquidPages.Offscreen(animations: true);
        using var _ = source;
        bool transparency = MotionEnvironment.Transparency;
        try
        {
            MotionEnvironment.SimulateTransparency(true);
            LiquidPages.UseLook(app, deck, root, PanelThemes.GlassLight);
            deck.OffscreenMedia(LiquidPages.Track(), LiquidPages.Cover());
            deck.SelectPage(DeckPage.Music, remember: false);
            deck.Preview(true, expanded: false);
            Frame(deck, root);
            var glass = deck.OffscreenGlassBackdrop!;
            var island = deck.Island;
            Report(deck.OffscreenGlassState.Shown, "liquid glass is shown (the stand-in host)");

            // Liquid glass is a base theme of its own (#55): a theme of the user's with glass is the blur, even one declaring the
            // liquid renderer and based on a liquid glass theme; the built-in liquid glass theme starts the renderer again.
            var custom = PanelThemes.Build("glassy", PanelThemes.Parse("""{ "id": "glassy", "base": "liquid-glass-light", "renderer": "liquid", "effects": { "glass": { "tint": "#E0F8FAFD" } } }"""));
            PanelTheme.Show(custom);
            Frame(deck, root);
            Report(PanelTheme.GlassShown && !LiquidTheme.Renders && !deck.OffscreenGlassState.Shown && !deck.OffscreenGlassState.Aside,
                "a custom theme with effects.glass shows glass (the blur), never the liquid renderer");
            LiquidPages.UseLook(app, deck, root, PanelThemes.GlassLight);
            Frame(deck, root);
            Report(LiquidTheme.Renders && deck.OffscreenGlassState.Shown, "the built-in liquid glass theme starts the liquid renderer again");

            // The content row: the collapsed row's parts, inside the island.
            var row = deck.GlassContentArea();
            var frame = glass.PreviewFrame();
            Report(row is { } r && r.Left >= 0 && r.Top >= 0 && r.Right <= island.ActualWidth + .01 && r.Bottom <= island.ActualHeight + .01 && r.Width > 20 && r.Height > 4,
                $"the collapsed content row lies inside the island ({row}, island {island.ActualWidth:0.#} x {island.ActualHeight:0.#} DIP)");
            Report(frame.ContentRight - frame.ContentLeft > 20 && frame.ContentBottom > frame.ContentTop && frame.ContentRight <= frame.Width + 1,
                $"the glass's frame carries it in device pixels ({frame.ContentLeft:0}..{frame.ContentRight:0} x {frame.ContentTop:0}..{frame.ContentBottom:0} of {frame.Width} x {frame.Height})");
            Report(frame.Hover == 0 && frame.Press == 0 && float.IsNaN(frame.PointerX) && frame.Morph == 0, "at rest: no hover, no press, no light, no morph");

            var at = new Point(island.ActualWidth * .75, island.ActualHeight / 2);
            if (Soft.Enabled) Motion(deck, root, glass, at);
            else Console.WriteLine("NOTE: Windows' animation effects are off (or high contrast): the checks with animations on are skipped");

            // ----- Animations off -----
            app.Settings.Animations = false;
            deck.SetExpanded(false, instant: true);
            Frame(deck, root);
            Report(!Soft.Enabled, "animations off: Soft.Enabled is false");
            deck.OffscreenPointer(at, false);
            frame = glass.PreviewFrame();
            Report(frame.Hover == 1 && Math.Abs(frame.PointerX - frame.Width * .75) <= 1, $"animations off: hover at once ({frame.Hover:0.00}), its light at the pointer ({frame.PointerX:0} of {frame.Width})");
            deck.OffscreenPointer(at, true);
            frame = glass.PreviewFrame();
            var jelly = deck.OffscreenJelly;
            Report(frame.Press == 1 && jelly == (1, 1) && frame.Morph == 0, $"animations off: the press's light at once ({frame.Press:0.00}), no jelly ({jelly.X:0.000} x {jelly.Y:0.000}), no morph");
            deck.OffscreenPointer(at, false);
            deck.OffscreenPointer(null, false);
            frame = glass.PreviewFrame();
            Report(frame.Hover == 0 && frame.Press == 0 && float.IsNaN(frame.PointerX), "animations off: let go and left, nothing at once");
            deck.SetExpanded(true);
            root.UpdateLayout();
            double open = deck.GlassOpenness();
            Frame(deck, root);
            frame = glass.PreviewFrame();
            Report(open == 1 && frame.Open == 1 && deck.GlassContentArea() is null, $"animations off: the panel is open at once, its frost whole (openness {open:0.00} on the first layout), no content row");
            deck.SetExpanded(false);
            root.UpdateLayout();
            open = deck.GlassOpenness();
            Report(open == 0, $"animations off: closed at once (openness {open:0.00})");
            app.Settings.Animations = true;

            Devices(app, deck, root);

            // ----- Transparency effects off -----
            MotionEnvironment.SimulateTransparency(false);
            LiquidPages.UseLook(app, deck, root, PanelThemes.GlassLight);
            Frame(deck, root);
            var state = deck.OffscreenGlassState;
            var surface = PanelTheme.SurfaceColor;
            Report(!PanelTheme.GlassShown && !GlassBackdrop.Available, "transparency off: glass is not shown, and Windows' blur is not available");
            Report(!state.Shown && !state.Aside && state.Surface >= .999 && surface.A == 255,
                $"transparency off: a solid surface (liquid glass shown {state.Shown}, surface aside {state.Aside}, its opacity {state.Surface:0.00}, its color {surface})");
            Report(!glass.HoldsWindowOrCapture, "transparency off: the island's glass holds no window or capture");
            RealWindow();
        }
        finally { MotionEnvironment.SimulateTransparency(transparency); }
        Console.WriteLine(_failures == 0 ? "PASS: all glass access checks" : $"{_failures} glass access checks failed");
        return _failures == 0 ? 0 : 1;
    }

    // With animations on: hover, press, expand and collapse, the shadow at rest.
    private static void Motion(MainWindow deck, System.Windows.Controls.Grid root, GlassBackdrop glass, Point at)
    {
        deck.OffscreenPointer(at, false);
        var first = glass.PreviewFrame();
        Wait(Soft.Hover + 80);
        var full = glass.PreviewFrame();
        Report(first.Hover < .6 && full.Hover == 1, $"hover eases in on the monitor's hover time ({first.Hover:0.00} at once, {full.Hover:0.00} after {Soft.Hover + 80} ms)");
        Report(Math.Abs(full.PointerX - full.Width * .75) <= 1 && Math.Abs(full.PointerY - full.Height / 2.0) <= 1,
            $"the light comes from the pointer, device pixels from the glass's corner ({full.PointerX:0}, {full.PointerY:0} of {full.Width} x {full.Height})");

        deck.OffscreenPointer(at, true);
        var down = glass.PreviewFrame();
        var jelly = deck.OffscreenJelly;
        Wait(Soft.Press + 80);
        var pressed = glass.PreviewFrame();
        Report(down.Press < .6 && pressed.Press == 1, $"the press's inner light eases in on Soft.Press ({down.Press:0.00} at once, {pressed.Press:0.00} after {Soft.Press + 80} ms)");
        Report(jelly.X > 1 && jelly.Y < 1 && jelly.X < 1.12, $"the press squashes the island a little (jelly to {jelly.X:0.000} x {jelly.Y:0.000})");
        deck.OffscreenPointer(at, false);
        var up = glass.PreviewFrame();
        Wait(Soft.Quick + 80);
        var released = glass.PreviewFrame();
        jelly = deck.OffscreenJelly;
        Report(up.Press > .4 && released.Press == 0 && jelly == (1, 1), $"let go, the light eases out on Soft.Quick ({up.Press:0.00} at once, {released.Press:0.00} after {Soft.Quick + 80} ms) and the jelly springs back to {jelly.X:0} x {jelly.Y:0}");

        deck.OffscreenPointer(null, false);
        var leaving = glass.PreviewFrame();
        Wait(Soft.Hover + 80);
        var left = glass.PreviewFrame();
        Report(leaving.Hover > .4 && !float.IsNaN(leaving.PointerX) && left.Hover == 0 && float.IsNaN(left.PointerX),
            $"the pointer gone, hover eases out with its light where it was ({leaving.Hover:0.00}, light at {leaving.PointerX:0}), then none ({left.Hover:0.00})");

        // Opening: the glass's openness follows the island's motion and lands on 1; the shadow comes back right at rest.
        var samples = new List<(long At, double Height, double Shadow, bool Cached)>();
        var clock = Stopwatch.StartNew();
        void Sample(object? sender, EventArgs e) => samples.Add((clock.ElapsedMilliseconds, deck.Island.ActualHeight, deck.ShadowHost.Opacity, deck.ShadowHost.CacheMode is BitmapCache));
        deck.OffscreenPointer(at, false);
        CompositionTarget.Rendering += Sample;
        deck.SetExpanded(true);
        root.UpdateLayout();
        double early = deck.GlassOpenness();
        Wait(1200);
        CompositionTarget.Rendering -= Sample;
        var frame = glass.PreviewFrame();
        Report(early < 1 && deck.GlassOpenness() == 1 && frame.Open == 1, $"opening: the glass's openness follows the motion ({early:0.00} on the first frame, {deck.GlassOpenness():0.00} at rest)");
        Report(frame.Hover == 0 && frame.Press == 0 && deck.GlassContentArea() is null, $"open: no hover light on the panel (the content layer; pointer still over it: hover {frame.Hover:0.00}), no content row");
        double end = samples.Count > 0 ? samples[^1].Height : 0;
        long rest = samples.LastOrDefault(s => Math.Abs(s.Height - end) > .01).At;
        // The offscreen source is ticked at its own pace (about 30 frames a second here): a frame's time is measured, and the
        // island's last change lies somewhere in the frame after the last sample that saw it moving.
        var gaps = samples.Zip(samples.Skip(1), (a, b) => b.At - a.At).Order().ToList();
        double tick = gaps.Count > 0 ? gaps[gaps.Count / 2] : 17;
        var back = samples.FirstOrDefault(s => s.At > rest && s.Shadow > 0);
        var whole = samples.FirstOrDefault(s => s.At > rest && s.Shadow >= .999);
        // The picture is kept once the frame the fade starts in is laid out (the island's last change of size may lie in it):
        // from the fade's second frame at the latest.
        int at0 = samples.IndexOf(back);
        bool kept = at0 >= 0 && at0 + 1 < samples.Count && samples[at0 + 1].Cached;
        Console.WriteLine($"  shadow after opening: island last moving at {rest} ms, shadow back at {back.At} ms ({back.Shadow:0.00}, kept {back.Cached}, a frame later {kept}), whole at {whole.At} ms; {samples.Count} frames, {tick:0} ms each");
        foreach (var s in samples.Where(s => s.At >= rest - 2 * tick && s.At <= whole.At + tick)) Console.WriteLine($"    {s.At,5} ms  island {s.Height:0.0} DIP  shadow {s.Shadow:0.00}  cached {s.Cached}");
        Report(back.At > 0 && back.At - rest <= 2 * tick + 17 && kept,
            $"the shadow comes back right at rest, kept as a picture from its second frame ({back.At - rest} ms after the last frame that moved, at {back.Shadow:0.00}; cached a frame later {kept}; before: {ShadowSettleMs} ms and a third of the move later)");
        Report(whole.At > 0 && whole.At - rest <= Soft.Quick + 17 + 2 * tick, $"the shadow is whole Soft.Quick after rest ({whole.At - rest} ms; wanted at most {Soft.Quick} ms, a frame and the sampling)");

        // Closing: back to the island, the openness to 0.
        deck.SetExpanded(false);
        root.UpdateLayout();
        early = deck.GlassOpenness();
        Wait(1000);
        Report(early > 0 && deck.GlassOpenness() == 0 && deck.GlassContentArea() is not null, $"closing: the glass returns to the island ({early:0.00} on the first frame, {deck.GlassOpenness():0.00} at rest), its content row back");
        deck.OffscreenPointer(null, false);
        Wait(Soft.Hover + 80);
    }

    // A GlassBackdrop on a real window, with transparency effects off: it makes no window of its own and no capture. The window
    // lies far off every monitor (nothing is shown on the screen), never takes focus, and is closed at once.
    private static void RealWindow()
    {
        var island = new IslandBorder { Width = 200, Height = 36, CornerRadius = new CornerRadius(18), Background = Brushes.Black };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = false,
            Left = -32000, Top = -32000, Width = 220, Height = 48, Content = island,
        };
        var glass = new GlassBackdrop(window, island) { LiquidTint = () => .5 };
        var clock = Stopwatch.StartNew();
        try
        {
            window.Show();
            glass.Update();
            glass.Follow(100);
            Wait(150);
            Report(!glass.HoldsWindowOrCapture && !glass.LiquidShown && !glass.SurfaceAside,
                $"transparency off, on a real window: no glass window, no capture, the surface not aside (window {new WindowInteropHelper(window).Handle != IntPtr.Zero})");
        }
        finally
        {
            glass.Dispose();
            window.Close();
            Console.WriteLine($"  the off-screen window lived {clock.ElapsedMilliseconds} ms");
        }
    }

    // Dark, then liquid glass (the panel opened and closed on it), then dark again, twice: the acrylic body makes no device,
    // none is alive on the dark theme, and the second round leaves the threads and the commit where the first one did (a kept
    // device: about 16 more driver threads and 40 MB after the first).
    private static void Devices(App app, MainWindow deck, System.Windows.Controls.Grid root)
    {
        var dark = new List<(int Threads, double Commit)>();
        void Look(string look) { LiquidPages.UseLook(app, deck, root, look); Frame(deck, root); deck.SetExpanded(true, instant: true); Frame(deck, root); deck.SetExpanded(false, instant: true); Frame(deck, root); Wait(600); }
        for (int round = 0; round < 3; round++)
        {
            Look(PanelThemes.Dark);
            dark.Add(Footprint());
            if (round == 1) Report(!AcrylicNoise.Is(deck.Island.Pattern), $"the dark theme: the island's pattern is the theme's again, not the acrylic's noise ({deck.Island.Pattern?.GetType().Name ?? "none"})");
            Look(PanelThemes.GlassLight);
            if (round == 1) Report(GlassBackdrop.Refraction || AcrylicNoise.Is(deck.Island.Pattern), "liquid glass: the acrylic's noise is the island's pattern (WPF draws it, no device)");
            Report(GlassBackdrop.Refraction || DesktopSource.DevicesMade == 0,
                $"round {round + 1}: the acrylic body makes no Direct3D device ({DesktopSource.DevicesMade} made; refraction renderer {GlassBackdrop.Refraction})");
        }
        Look(PanelThemes.Dark);
        dark.Add(Footprint());
        Console.WriteLine("  on the dark theme, between rounds of liquid glass: " + string.Join(" -> ", dark.Select(d => $"{d.Threads} threads {d.Commit:0} MB")));
        Report(DesktopSource.DevicesAlive == 0 && !LiquidTheme.Renders, $"liquid glass then the dark theme: no device alive ({DesktopSource.DevicesAlive})");
        // The first round warms WPF's own caches (the theme's brushes, the glass's composition objects): from it on, flat.
        var (threads, commit) = (dark[^1].Threads - dark[1].Threads, dark[^1].Commit - dark[1].Commit);
        Report(threads <= 2 && commit <= 8,
            $"rounds of liquid glass leave the dark theme's threads and commit where they were ({threads:+0;-0;0} threads, {commit:+0.0;-0.0;0} MB over two rounds; at most +2 and +8)");
    }

    // The process's threads and committed private bytes (MB), after a collection (WPF's and our own managed garbage aside).
    private static (int Threads, double Commit) Footprint()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        return (process.Threads.Count, process.PrivateMemorySize64 / 1048576.0);
    }

    private static void Frame(MainWindow deck, System.Windows.Controls.Grid root)
    {
        deck.OffscreenLiquidFrame();
        LiquidPages.Settle(root, deck);
        deck.OffscreenLiquidFrame();
    }

    private static void Report(bool ok, string text)
    {
        if (!ok) _failures++;
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + text);
    }

    private static void Wait(double milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
