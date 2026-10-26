using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;
using Vortice.Direct3D11;
using Vortice.DXGI;

// The liquid glass's desktop capture: the region texture's size and steps, a region reaching past its monitor's edge (clamped to
// the monitor under its centre, the edge repeated), a region on another monitor, and what a frame costs; then, over a backdrop
// window of a known color (Backdrop: small, in the middle of the primary monitor, shown only for its test), that the first
// frame and the frame after a lost access are the desktop and not black, that an excluded window is not in the capture and a
// refused exclusion keeps the glass off, a device lost and replaced under the live path (LiquidHost) with nothing drawn on a
// disposed renderer, two glasses at once, the measure and decision of automatic liquid glass, and the allocations of a frame.
// The captured pixels are only compared in memory, never kept. Skipped where Desktop Duplication is not available (no hardware
// adapter with an output, a session without a desktop).
internal static class DesktopSourceTests
{
    // The backdrops' colors: a light gray, and a blue for the window over it.
    private static readonly (byte R, byte G, byte B) Light = (200, 200, 200), Blue = (30, 90, 160);

    public static void Run(Action<string, bool> check)
    {
        // Device pixels, as BrimDeck (per-monitor v2) sees them.
        var awareness = SetThreadDpiAwarenessContext(-4);
        try { Check(check); }
        finally { SetThreadDpiAwarenessContext(awareness); }
    }

    // A process duplicates an output once at a time (a second DuplicateOutput of it is refused, E_INVALIDARG): each test lets its
    // capture go before the next one starts.
    private static void Check(Action<string, bool> check)
    {
        if (!Basics(check)) return;
        Known(check);
        Exclusion(check);
        DeviceLoss(check);
        TwoGlasses(check);
        LiveLook(check);
        Allocations(check);
    }

    private static bool Basics(Action<string, bool> check)
    {
        using var source = DesktopSource.TryCreate();
        if (source is null) { Console.WriteLine("SKIP: desktop capture (no Direct3D 11 device on a desktop output)."); return false; }
        int deviceChanges = 0;
        source.DeviceChanged += () => deviceChanges++;

        var monitors = Monitors();
        var primary = monitors.First(m => m.Contains(0, 0));
        var region = new RECT(primary.Left + primary.Width / 2 - 350, primary.Top, primary.Left + primary.Width / 2 + 350, primary.Top + 260);
        if (!UpdateSoon(source, region, out bool changed)) { Console.WriteLine("SKIP: desktop capture (Desktop Duplication refused in this session)."); return false; }
        check("desktop capture: the region's size is captured, at the texture's top-left, in BGRA8", source.Size == (700, 260)
            && Texture(source) is var (tw, th) && tw >= 700 && th >= 260 && source.Texture!.Description.Format == Format.B8G8R8A8_UNorm);
        check("desktop capture: the texture grows in steps of 64 pixels", Texture(source) == (704, 320));
        check("desktop capture: the first frame counts as changed", changed);
        var texture = source.Texture;
        check("desktop capture: the same region again keeps its texture", source.Update(region, out _) && ReferenceEquals(texture, source.Texture));

        var smaller = region with { Right = region.Right - 100, Bottom = region.Bottom - 60 };
        check("desktop capture: a smaller region is captured at its size and keeps the texture", source.Update(smaller, out changed) && source.Size == (600, 200) && changed && ReferenceEquals(texture, source.Texture));
        var (pixels, stride) = Read(source);
        bool repeated = true;
        for (int y = 0; y < 200 && repeated; y += 7)
            for (int x = 600; x < stride && repeated; x++) repeated = Pixel(pixels, stride, x, y) == Pixel(pixels, stride, 599, y);
        for (int x = 0; x < 600 && repeated; x += 7)
            for (int y = 200; y < pixels.Length / 4 / stride && repeated; y++) repeated = Pixel(pixels, stride, x, y) == Pixel(pixels, stride, x, 199);
        check("desktop capture: past the region the texture repeats its last column and row", repeated);

        // A region over the primary monitor's left edge: whatever lies beyond (the other monitor, or nothing), the region is taken
        // from the monitor under its centre, and its first columns repeat that monitor's edge.
        var edge = new RECT(primary.Left - 100, primary.Top + 100, primary.Left + 500, primary.Top + 300);
        check("desktop capture: a region over a monitor's edge is captured at its full size", UpdateSoon(source, edge, out _) && source.Size == (600, 200));
        (pixels, stride) = Read(source);
        repeated = true;
        for (int y = 0; y < 200 && repeated; y++)
            for (int x = 0; x < 100 && repeated; x++)
                repeated = Pixel(pixels, stride, x, y) == Pixel(pixels, stride, 100, y);
        check("desktop capture: beyond the monitor's edge the edge repeats", repeated);
        check("desktop capture: the region is opaque", Opaque(pixels));

        // Another monitor (another output, maybe another format): the same device where it is on the same adapter.
        if (monitors.FirstOrDefault(m => !m.Contains(0, 0)) is { IsEmpty: false } other)
        {
            var there = new RECT(other.Left + 40, other.Top + 40, other.Left + 440, other.Top + 200);
            check("desktop capture: a region on another monitor is captured", UpdateSoon(source, there, out changed) && source.Size == (400, 160) && changed);
            check("desktop capture: the other monitor's region is opaque 8-bit", Opaque(Read(source).Pixels));
            check("desktop capture: back on the primary monitor", UpdateSoon(source, region, out _) && source.Size == (700, 260));
        }
        else Console.WriteLine("SKIP: desktop capture on a second monitor (one monitor).");
        Console.WriteLine($"desktop capture: device replaced {deviceChanges} times; primary {primary.Width}x{primary.Height}, {monitors.Count} monitors");

        Growth(check, source, primary);

        // What a frame costs, at the top of the primary monitor (where the island is) and lower down: Update for two seconds, and
        // how often the desktop under the region changed.
        Measure(source, region, "top");
        Measure(source, region.Offset(0, primary.Height / 2), "middle");
        return true;
    }

    // An opening panel: the padded region grows a little on every frame. The texture is made anew only when the region grows
    // past it (by a quarter at least each time), and not at all the second time.
    private static void Growth(Action<string, bool> check, DesktopSource source, RECT monitor)
    {
        const int pad = 72;
        int cx = monitor.Left + monitor.Width / 2;
        RECT At(int w, int h) => new(cx - w / 2 - pad, monitor.Top - pad, cx + w / 2 + pad, monitor.Top + h + pad);
        int Open(out double median)
        {
            int made = 0;
            var times = new List<double>();
            var last = source.Texture;
            for (int f = 0; f <= 30; f++)
            {
                int w = 342 + (1100 - 342) * f / 30, h = 60 + (600 - 60) * f / 30;
                long t0 = Stopwatch.GetTimestamp();
                if (!source.Update(At(w, h), out _)) continue;
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                if (!ReferenceEquals(last, source.Texture)) { made++; last = source.Texture; }
            }
            times.Sort();
            median = times.Count > 0 ? times[times.Count / 2] : 0;
            return made;
        }
        source.Update(At(342, 60), out _);
        int first = Open(out double firstMedian);
        source.Update(At(342, 60), out _);
        int second = Open(out double secondMedian);
        Console.WriteLine(FormattableString.Invariant($"desktop capture, an opening panel (31 frames): textures made {first} the first time, {second} the second; Update median {firstMedian:0.000} / {secondMedian:0.000} ms"));
        check($"desktop capture: an opening panel makes its texture anew only a few times ({first} of 31 frames)", first <= 8);
        check($"desktop capture: a second opening makes no texture ({second})", second == 0);
    }

    // A desktop of a known color: the first frame of a new duplication, and the one after access was lost (the lock screen, a UAC
    // prompt), are that color, never black (a frame nothing was presented for is not taken).
    private static void Known(Action<string, bool> check)
    {
        using var backdrop = new Backdrop(Backdrop.Corner(220, 140), Light.R, Light.G, Light.B);
        var region = Inside(backdrop.Bounds, 20);
        using var source = DesktopSource.TryCreate();
        if (source is null || !UpdateSoon(source, region, out _)) { Console.WriteLine("SKIP: desktop capture of a known backdrop (no capture)."); return; }
        check("desktop capture: the first frame of a new duplication is the desktop, not black", All(Read(source), source.Size, Light));
        typeof(DesktopSource).GetMethod("AccessLost", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(source, null);
        bool waits = !source.Update(region, out _);
        var clock = Stopwatch.StartNew();
        bool back = UpdateSoon(source, region, out bool changed);
        check(FormattableString.Invariant($"desktop capture: access lost, the capture waits and starts again ({clock.ElapsedMilliseconds} ms)"), waits && back && changed);
        check("desktop capture: after a lost access the region is the desktop again, whole, not black", All(Read(source), source.Size, Light));
    }

    // A window left out of the capture (DesktopSource.Exclude) is not in it, opaque as it is; a window Windows will not leave out
    // (here one already destroyed) keeps the glass off (DesktopSource.Open refuses, and GlassBackdrop shows the blur).
    private static void Exclusion(Action<string, bool> check)
    {
        using var backdrop = new Backdrop(Backdrop.Corner(220, 140), Light.R, Light.G, Light.B);
        using var cover = new Backdrop(Backdrop.Corner(110, 60), Blue.R, Blue.G, Blue.B);
        using var source = DesktopSource.TryCreate();
        if (source is null) { Console.WriteLine("SKIP: exclusion (no capture)."); return; }
        var region = Inside(cover.Bounds, 10);
        if (!UpdateSoon(source, region, out _)) { Console.WriteLine("SKIP: exclusion (no capture)."); return; }
        check("exclusion: an opaque window is in the capture until it is left out", All(Read(source), source.Size, Blue));
        check("exclusion: Windows takes and keeps the attribute", DesktopSource.Exclude(cover.Handle));
        Backdrop.Pump();
        bool gone = false;
        for (int i = 0; i < 40 && !gone; i++) { Thread.Sleep(25); gone = source.Update(region, out _) && All(Read(source), source.Size, Light); }
        check("exclusion: the window left out is not in the capture: the desktop behind it is", gone);

        var dead = new Backdrop(Backdrop.Corner(40, 40), 0, 0, 0);
        nint handle = dead.Handle;
        dead.Dispose();
        check("exclusion: a window that cannot be left out is refused", !DesktopSource.Exclude(handle) && !DesktopSource.Exclude(0));
        using (var refusedSource = DesktopSource.Open(handle, 0, out bool refused))
            check("exclusion: a refused exclusion gives no capture, the blur stands in", refusedSource is null && refused);
        using (var opened = DesktopSource.Open(cover.Handle, 0, out bool refused))
            check("exclusion: with the island's window left out the capture opens", opened is not null && !refused);
    }

    // A device lost (DXGI_ERROR_DEVICE_REMOVED as Take sees it: private Lose) and replaced, on the live path (LiquidHost, as
    // GlassBackdrop drives it): the renderer is made again for the new device after Update returned, never disposed under a
    // frame or a tick that still holds it, and drawing goes on; DeviceChanged is raised as Update returns, not inside it.
    private static void DeviceLoss(Action<string, bool> check)
    {
        using var backdrop = new Backdrop(Backdrop.Corner(220, 140), Light.R, Light.G, Light.B);
        var region = Inside(backdrop.Bounds, 20);
        var source = DesktopSource.TryCreate();
        if (source is null) { Console.WriteLine("SKIP: device loss (no capture)."); return; }
        using var host = new LiquidHost(source);
        bool inside = false;
        int raised = 0;
        source.DeviceChanged += () => { raised++; inside |= new StackTrace().ToString().Contains("DesktopSource.Capture", StringComparison.Ordinal); };
        var frame = LiquidFrame.Preset(false, region.Width - 48, region.Height - 48, 20, 20, 0, 1);
        long now = 0;
        // Frames 16 ms apart, only the hover changing (E18: drawn over the last capture until the next look, at most PollMs on):
        // the lost device is found at the next look and the renderer made again, within a few frames.
        bool Draw(bool tick, LiquidRenderer? before = null)
        {
            for (int i = 0; i < 40; i++)
            {
                bool drew = tick ? host.Tick(region, 24, frame, now += 100) : host.Frame(region, 24, frame with { Hover = i / 40f }, now += 16);
                if (drew && (before is null || !ReferenceEquals(before, host.Renderer))) return true;
                if (!drew) Thread.Sleep(25);
            }
            return false;
        }
        if (!Draw(false)) { Console.WriteLine("SKIP: device loss (no capture)."); return; }
        var lose = typeof(DesktopSource).GetMethod("Lose", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int swaps = 20, drawn = 0, errors = 0;
        for (int k = 0; k < swaps; k++)
        {
            var before = host.Renderer;
            lose.Invoke(source, null);
            try { if (Draw(k % 2 == 1, before) && !ReferenceEquals(before, host.Renderer)) drawn++; }
            catch (Exception ex) when (ex is ObjectDisposedException or NullReferenceException or SharpGen.Runtime.SharpGenException) { errors++; }
        }
        check($"device loss: every lost device is replaced and drawing goes on, frames and ticks alike ({drawn} of {swaps}, {errors} errors)", drawn == swaps && errors == 0);
        check($"device loss: the renderer is made again once a device ({host.Rebuilds} for {swaps})", host.Rebuilds == swaps);
        check($"device loss: DeviceChanged is raised as Update returns, never inside the capture ({raised} raised)", raised == swaps && !inside);
    }

    // Two glasses at once, as BrimDeck has a deck on each monitor: each its own capture and renderer, both drawing on every
    // frame. Windows duplicates an output once per process, so a second glass over the same output gets no capture (its
    // DuplicateOutput is refused): it shows the blur, retried, without an error and without disturbing the first.
    private static void TwoGlasses(Action<string, bool> check)
    {
        var monitors = Monitors();
        var second = monitors.FirstOrDefault(m => !m.Contains(0, 0));
        using var here = new Backdrop(Backdrop.Corner(220, 140), Light.R, Light.G, Light.B);
        var near = Inside(here.Bounds, 10);
        var one = DesktopSource.TryCreate();
        var two = DesktopSource.TryCreate();
        if (one is null || two is null) { one?.Dispose(); two?.Dispose(); Console.WriteLine("SKIP: two glasses (no capture)."); return; }
        using var first = new LiquidHost(one);
        using var other = new LiquidHost(two);
        var frame = LiquidFrame.Preset(false, near.Width - 48, near.Height - 48, 15, 15, 0, 1);
        bool Soon(LiquidHost host, RECT region, LiquidFrame f, long now)
        {
            for (int t = 0; t < 40; t++) { if (host.Frame(region, 24, f, now)) return true; Backdrop.Pump(); Thread.Sleep(25); }
            return false;
        }
        if (!Soon(first, near, frame, 0)) { Console.WriteLine("SKIP: two glasses (no capture)."); return; }
        bool refused = true;
        try { for (int t = 0; t < 4; t++) { refused &= !other.Frame(Inside(near, 20), 24, frame with { Width = frame.Width - 40, Height = frame.Height - 40 }, t * 16L); Thread.Sleep(25); } }
        catch (Exception) { refused = false; }
        check("two glasses: a second glass over the same output gets no capture, quietly, and the first goes on", refused && first.Frame(near, 24, frame with { Hover = .5f }, 100));
        if (second.IsEmpty) { Console.WriteLine("SKIP: two glasses on two monitors (one monitor)."); return; }
        using var there = new Backdrop(new RECT(second.Left, second.Top, second.Left + 220, second.Top + 140), Light.R, Light.G, Light.B);
        var far = Inside(there.Bounds, 10);
        int ok = 0, frames = 30;
        for (int i = 0; i < frames; i++)
        {
            var f = frame with { Hover = i / (float)frames };
            if (Soon(first, near, f, 200 + i * 16L) && Soon(other, far, f, 200 + i * 16L)) ok++;
        }
        check($"two glasses: one on each monitor, both capture and draw on every frame ({ok} of {frames})", ok == frames);
        check("two glasses: each sees the desktop under its own region", All(Read(one), one.Size, Light) && All(Read(two), two.Size, Light, 2));
    }

    // Automatic liquid glass on the live path, as GlassBackdrop runs it while the island holds still, over a light backdrop of a
    // known color (shown for about a second): the region captured and drawn through LiquidHost (the first frame followed, then
    // the poll's ticks), the renderer's measure read back (LiquidRenderer.TryBackdrop) and the decision stepped on every 100 ms
    // tick (BackdropLook, GlassAppearance). Checks that the measure is the backdrop's own brightness, that the first measure
    // decides at once (light glass), and that with the wrong look shown over a backdrop that holds still (no new frame drawn)
    // the look still turns within the dwell: the bug where a still backdrop, measured once, never
    // confirmed its look.
    private static void LiveLook(Action<string, bool> check)
    {
        using var backdrop = new Backdrop(Backdrop.Corner(220, 140), Light.R, Light.G, Light.B);
        const int pad = 24;
        var region = Inside(backdrop.Bounds, 10);
        int width = region.Width - 2 * pad, height = region.Height - 2 * pad;
        var source = DesktopSource.TryCreate();
        if (source is null) { Console.WriteLine("SKIP: liquid look (no capture)."); return; }
        using var host = new LiquidHost(source);
        var frame = LiquidFrame.Preset(false, width, height, height / 2f, height / 2f, 0, 1);
        bool followed = false;
        for (int i = 0; i < 40 && !followed; i++) { followed = host.Frame(region, pad, frame, 0); if (!followed) Thread.Sleep(25); }
        if (!followed) { Console.WriteLine("SKIP: liquid look (no capture)."); return; }
        var look = new BackdropLook();
        bool shown = true, firstInstant = false, decidedFirst = false;
        long turnedAt = -1, wrongFrom = -1;
        for (int tick = 1; tick <= 40; tick++)
        {
            long now = tick * 100L;
            host.Tick(region, pad, frame, now);
            Thread.Sleep(15);
            if (look.Step(host.Renderer!, now, shown, out bool instant))
            {
                if (!decidedFirst) firstInstant = instant;
                shown = !shown;
                if (wrongFrom >= 0 && turnedAt < 0) turnedAt = now;
            }
            if (!decidedFirst && look.Dark is not null)
            {
                decidedFirst = true;
                // The wrong look is shown from here (the theme turned the other way meanwhile).
                shown = !look.Dark.Value; wrongFrom = now;
            }
        }
        double expected = (.0722 * Light.B + .7152 * Light.G + .2126 * Light.R) / 255;
        check(FormattableString.Invariant($"liquid look: the measure is the backdrop under the island (measured {look.Luma:0.000}, backdrop {expected:0.000}, {look.Measures} measures)"),
            look.Measures > 0 && Math.Abs(look.Luma - expected) < .02);
        check("liquid look: the first measure decides at once, light glass over a light backdrop", decidedFirst && firstInstant && look.Dark == false);
        long latency = turnedAt - wrongFrom;
        check($"liquid look: over a still backdrop the wrong look turns within the dwell ({latency} ms)",
            turnedAt >= 0 && latency <= GlassAppearance.Dwell + 300);
    }

    // What a followed frame whose glass changes (the pointer's light easing) allocates on the live path: nothing of the host's
    // own (no closure per frame); what is left is the renderer's.
    private static void Allocations(Action<string, bool> check)
    {
        using var backdrop = new Backdrop(Backdrop.Corner(220, 140), Light.R, Light.G, Light.B);
        var region = Inside(backdrop.Bounds, 10);
        var source = DesktopSource.TryCreate();
        if (source is null) { Console.WriteLine("SKIP: allocations (no capture)."); return; }
        using var host = new LiquidHost(source);
        var frame = LiquidFrame.Preset(false, region.Width - 48, region.Height - 48, 20, 20, 0, 1);
        for (int i = 0; i < 40 && !host.Frame(region, 24, frame, 0); i++) Thread.Sleep(25);
        for (int i = 0; i < 20; i++) host.Frame(region, 24, frame with { Hover = i / 20f }, i * 16L);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        const int n = 300;
        for (int i = 0; i < n; i++) host.Frame(region, 24, frame with { Hover = i % 100 / 100f, Press = i % 50 / 50f }, 400 + i * 16L);
        double bytes = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)n;
        check(FormattableString.Invariant($"allocations: a followed frame allocates little ({bytes:0} bytes)"), bytes <= 128);
    }

    private static void Measure(DesktopSource source, RECT region, string name)
    {
        source.Update(region, out _);
        var times = new List<double>();
        int changes = 0, failures = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 2)
        {
            long start = Stopwatch.GetTimestamp();
            if (!source.Update(region, out bool changed)) failures++;
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (changed) changes++;
            Thread.Sleep(16);
        }
        times.Sort();
        Console.WriteLine($"desktop capture ({name}): Update median {times[times.Count / 2]:0.000} ms, 95th {times[times.Count * 95 / 100]:0.000} ms, max {times[^1]:0.000} ms over {times.Count} calls; changed {changes}, failed {failures}");
    }

    // Duplication may need a moment to hand out its first frame of an output.
    private static bool UpdateSoon(DesktopSource source, RECT region, out bool changed)
    {
        for (int i = 0; i < 40; i++)
        {
            if (source.Update(region, out changed)) return true;
            Backdrop.Pump();
            Thread.Sleep(25);
        }
        changed = false;
        return false;
    }

    private static RECT Inside(RECT bounds, int margin) => new(bounds.Left + margin, bounds.Top + margin, bounds.Right - margin, bounds.Bottom - margin);

    private static (int, int) Texture(DesktopSource source) => source.Texture is { } t ? ((int)t.Description.Width, (int)t.Description.Height) : (0, 0);

    private static int Pixel(byte[] pixels, int stride, int x, int y) => BitConverter.ToInt32(pixels, (y * stride + x) * 4);

    private static bool Opaque(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 255) return false;
        return true;
    }

    // Every pixel of the region (Size, at the texture's top-left) this color.
    // Within a step or two of it where the monitor composes in float (advanced color), which the fill shader turns to 8-bit.
    private static bool All((byte[] Pixels, int Stride) read, (int Width, int Height) size, (byte R, byte G, byte B) color, int within = 0)
    {
        for (int y = 0; y < size.Height; y++)
            for (int x = 0; x < size.Width; x++)
            {
                int o = (y * read.Stride + x) * 4;
                if (Math.Abs(read.Pixels[o] - color.B) > within || Math.Abs(read.Pixels[o + 1] - color.G) > within || Math.Abs(read.Pixels[o + 2] - color.R) > within) return false;
            }
        return true;
    }

    // The region texture's pixels (all of it; the region at its top-left), through a staging copy, in memory only.
    private static (byte[] Pixels, int Stride) Read(DesktopSource source)
    {
        var texture = source.Texture!;
        var description = texture.Description;
        // Vortice hands out one wrapper for the immediate context: it is not disposed here.
        var context = source.Device.ImmediateContext;
        using var staging = source.Device.CreateTexture2D(description with { Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read });
        context.CopyResource(staging, texture);
        var map = context.Map(staging, 0, MapMode.Read);
        int width = (int)description.Width, height = (int)description.Height;
        var pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++) Marshal.Copy(map.DataPointer + (nint)(row * map.RowPitch), pixels, row * width * 4, width * 4);
        context.Unmap(staging, 0);
        return (pixels, width);
    }

    private static List<RECT> Monitors()
    {
        var list = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) list.Add(new RECT(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private delegate bool MonitorProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc proc, IntPtr data);
    [DllImport("user32")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
