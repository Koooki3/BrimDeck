using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.LiquidGlass;
using Vortice.Direct3D11;
using Vortice.DXGI;

// The liquid glass's desktop capture, without showing a window: the region texture's size, a region reaching past its monitor's
// edge (clamped to the monitor under its centre, the edge repeated), a region on another monitor, and what a frame costs. The
// captured pixels are only compared in memory, never kept. Skipped where Desktop Duplication is not available (no hardware
// adapter with an output, a session without a desktop).
internal static class DesktopSourceTests
{
    public static void Run(Action<string, bool> check)
    {
        // Device pixels, as BrimDeck (per-monitor v2) sees them.
        var awareness = SetThreadDpiAwarenessContext(-4);
        try { Check(check); }
        finally { SetThreadDpiAwarenessContext(awareness); }
    }

    private static void Check(Action<string, bool> check)
    {
        using var source = DesktopSource.TryCreate();
        if (source is null) { Console.WriteLine("SKIP: desktop capture (no Direct3D 11 device on a desktop output)."); return; }
        int deviceChanges = 0;
        source.DeviceChanged += () => deviceChanges++;

        var monitors = Monitors();
        var primary = monitors.First(m => m.Contains(0, 0));
        var region = new RECT(primary.Left + primary.Width / 2 - 350, primary.Top, primary.Left + primary.Width / 2 + 350, primary.Top + 260);
        if (!UpdateSoon(source, region, out bool changed)) { Console.WriteLine("SKIP: desktop capture (Desktop Duplication refused in this session)."); return; }
        check("desktop capture: texture is the region's size in BGRA8", Size(source) == (700, 260) && source.Texture!.Description.Format == Format.B8G8R8A8_UNorm);
        check("desktop capture: the first frame counts as changed", changed);
        var texture = source.Texture;
        check("desktop capture: the same region again keeps its texture", source.Update(region, out _) && ReferenceEquals(texture, source.Texture));

        var smaller = region with { Right = region.Right - 100, Bottom = region.Bottom - 60 };
        check("desktop capture: a resized region gets a texture of its new size", source.Update(smaller, out changed) && Size(source) == (600, 200) && changed);

        // A region over the primary monitor's left edge: whatever lies beyond (the other monitor, or nothing), the region is taken
        // from the monitor under its centre, and its first columns repeat that monitor's edge.
        var edge = new RECT(primary.Left - 100, primary.Top + 100, primary.Left + 500, primary.Top + 300);
        check("desktop capture: a region over a monitor's edge is captured at its full size", UpdateSoon(source, edge, out _) && Size(source) == (600, 200));
        var pixels = Read(source);
        bool repeated = true;
        for (int y = 0; y < 200 && repeated; y++)
            for (int x = 0; x < 100 && repeated; x++)
                repeated = Pixel(pixels, 600, x, y) == Pixel(pixels, 600, 100, y);
        check("desktop capture: beyond the monitor's edge the edge repeats", repeated);
        check("desktop capture: the region is opaque", Opaque(pixels));

        // Another monitor (another output, maybe another format): the same device where it is on the same adapter.
        if (monitors.FirstOrDefault(m => !m.Contains(0, 0)) is { IsEmpty: false } other)
        {
            var there = new RECT(other.Left + 40, other.Top + 40, other.Left + 440, other.Top + 200);
            check("desktop capture: a region on another monitor is captured", UpdateSoon(source, there, out changed) && Size(source) == (400, 160) && changed);
            check("desktop capture: the other monitor's region is opaque 8-bit", Opaque(Read(source)));
            check("desktop capture: back on the primary monitor", UpdateSoon(source, region, out _) && Size(source) == (700, 260));
        }
        else Console.WriteLine("SKIP: desktop capture on a second monitor (one monitor).");
        Console.WriteLine($"desktop capture: device replaced {deviceChanges} times; primary {primary.Width}x{primary.Height}, {monitors.Count} monitors");

        // What a frame costs, at the top of the primary monitor (where the island is) and lower down: Update for two seconds, and
        // how often the desktop under the region changed.
        Measure(source, region, "top");
        Measure(source, region.Offset(0, primary.Height / 2), "middle");
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
            Thread.Sleep(25);
        }
        changed = false;
        return false;
    }

    private static (int, int) Size(DesktopSource source) => source.Texture is { } t ? ((int)t.Description.Width, (int)t.Description.Height) : (0, 0);

    private static int Pixel(byte[] pixels, int width, int x, int y) => BitConverter.ToInt32(pixels, (y * width + x) * 4);

    private static bool Opaque(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 255) return false;
        return true;
    }

    // The region texture's pixels, through a staging copy, in memory only.
    private static byte[] Read(DesktopSource source)
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
        return pixels;
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
