using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

// The acrylic body's edge, as composition draws it (user report: the liquid glass themes' rounded edges are jagged where the
// other themes' are smooth). A GlassBackdrop on a real window far off every monitor (nothing on the screen; the window never
// takes focus and is closed at once), the liquid glass theme shown, its own glass window printed by DWM (PrintWindow, full
// content), for the capsule, the open panel (square top) and the notch (its shoulders), each at the sizes of 100 to 200 %.
// Writes each print and a 4x crop of a corner, and counts the edge's partly covered pixels along the curve: an antialiased
// edge has about one per row of the curve, an aliased one none.
//   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --edge-probe <folder>
internal static class EdgeProbe
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var (app, deck, root, source) = LiquidPages.Offscreen(animations: false);
        using var _ = source;
        if (!MotionEnvironment.Transparency) { Console.WriteLine("NOTE: transparency effects are off: no glass to probe"); return 0; }
        LiquidPages.UseLook(app, deck, root, PanelThemes.GlassDark);
        GlassBackdrop.ProbeColor = Windows.UI.Color.FromArgb(255, 230, 60, 60);
        int failures = 0;
        foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
            foreach (var (name, w, h, corners, shoulder) in new (string, double, double, CornerRadius, double)[]
            {
                ("capsule", 200, 36, new CornerRadius(18), 0),
                ("panel", 360, 240, new CornerRadius(0, 0, 24, 24), 0),
                ("notch", 220, 34, new CornerRadius(0, 0, 14, 14), 8),
            })
            {
                var (aa, total) = Probe(folder, $"{name}-{scale * 100:0}", w * scale, h * scale, new CornerRadius(corners.TopLeft * scale, corners.TopRight * scale, corners.BottomRight * scale, corners.BottomLeft * scale), shoulder * scale);
                bool ok = total > 0 && aa >= total * .5;
                if (!ok) failures++;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {name} at {scale * 100:0} % sizes: {aa} of {total} rows along the bottom-left curve carry a partly covered edge pixel");
            }
        failures += FirstFrame(app, deck, root);
        Console.WriteLine(failures == 0 ? "PASS: all edge checks" : $"{failures} edge checks failed");
        return failures == 0 ? 0 : 1;
    }

    // The first frame's luminosity (user 10-27: the light glass came up very dark at first): the glass shown for the first time,
    // and the look turned while it is shown, each settle the luminosity layer in that very frame, from the first reading (here
    // the screen off every monitor reads black: the light look wants its body lifted well past its floor, the dark one not).
    private static int FirstFrame(App app, MainWindow deck, System.Windows.Controls.Grid root)
    {
        int failures = 0;
        LiquidPages.UseLook(app, deck, root, PanelThemes.GlassLight);
        var island = new IslandBorder { Width = 200, Height = 36, CornerRadius = new CornerRadius(18), MaterialOpacity = 1, Background = Brushes.Transparent };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = false,
            AllowsTransparency = true, Background = Brushes.Transparent, Left = -32000, Top = -32000, Width = 240, Height = 76,
            Content = new System.Windows.Controls.Grid { Children = { island } },
        };
        var glass = new GlassBackdrop(window, island) { LiquidTint = () => .5 };
        try
        {
            window.Show();
            window.UpdateLayout();
            glass.Follow(0);
            double light = glass.LuminosityShown, wanted = GlassEffect.Shown(GlassEffect.LuminosityFor(false, 0, 0), 0, false);
            bool ok = Math.Abs(light - wanted) < 1e-6;
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: the light glass's first frame is settled: luminosity {light:0.00} (over black it wants {wanted:0.00}; the floor is {GlassEffect.LuminosityCapsule:0.00})");
            LiquidPages.UseLook(app, deck, root, PanelThemes.GlassDark);
            // A few frames (the glass is still following its first frames), well inside the probe's ProbeMs of 500.
            glass.Follow(0);
            Wait(120);
            double dark = glass.LuminosityShown, darkWanted = GlassEffect.Shown(GlassEffect.LuminosityFor(true, 0, 0), 0, true);
            ok = Math.Abs(dark - darkWanted) < 1e-6;
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: turned to the dark look, its first frame is settled too: luminosity {dark:0.00} (wants {darkWanted:0.00})");
        }
        finally { glass.Dispose(); window.Close(); }
        return failures;
    }

    private static (int Antialiased, int Rows) Probe(string folder, string name, double w, double h, CornerRadius corners, double shoulder)
    {
        var island = new IslandBorder { Width = w, Height = h, CornerRadius = corners, ShoulderRadius = shoulder, MaterialOpacity = 1, Background = Brushes.Transparent };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = false,
            AllowsTransparency = true, Background = Brushes.Transparent,
            Left = -32000, Top = -32000, Width = w + 40, Height = h + 40, Content = new System.Windows.Controls.Grid { Children = { island } },
        };
        var glass = new GlassBackdrop(window, island) { LiquidTint = () => .5 };
        try
        {
            window.Show();
            glass.Update();
            glass.Follow(200);
            Wait(400);
            var handle = glass.GlassWindow;
            if (handle == IntPtr.Zero) { Console.WriteLine($"NOTE: {name}: no glass window"); return (0, 0); }
            GetWindowRect(handle, out var rect);
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            var pixels = Print(handle, width, height);
            if (pixels is null) { Console.WriteLine($"NOTE: {name}: the print failed"); return (0, 0); }
            Save(Path.Combine(folder, name + ".png"), pixels, width, height, 1, 0, 0, width, height);
            // The island's place in the glass window: centred in the owner (the grid centres it).
            double s = VisualTreeHelper.GetDpi(island).DpiScaleX;
            var at = island.TransformToAncestor(window).Transform(new Point(0, 0));
            int x0 = (int)Math.Round(at.X * s), y0 = (int)Math.Round(at.Y * s), iw = (int)Math.Round(w * s), ih = (int)Math.Round(h * s);
            int r = (int)Math.Ceiling(Math.Max(corners.BottomLeft, corners.TopLeft) * s) + 4;
            // The bottom-left corner, 4x.
            Save(Path.Combine(folder, name + "-corner4x.png"), pixels, width, height, 4, Math.Max(0, x0 - 4), Math.Max(0, y0 + ih - r), r + 8, r + 4);
            if (shoulder > 0) Save(Path.Combine(folder, name + "-shoulder4x.png"), pixels, width, height, 4, Math.Max(0, x0 - 4), Math.Max(0, y0), (int)(shoulder * s) + 12, (int)(shoulder * s) + 8);
            // Along the bottom-left curve: in each row, is there a pixel neither empty nor whole (alpha between)?
            int rows = 0, aa = 0, radius = (int)Math.Round(corners.BottomLeft * s);
            for (int y = y0 + ih - radius + 2; y < y0 + ih - 1; y++)
            {
                if (y < 0 || y >= height) continue;
                rows++;
                for (int x = Math.Max(0, x0 - 2); x < Math.Min(width, x0 + radius); x++)
                {
                    // The print is opaque, black where nothing is drawn: a partly covered pixel is part of the probe's red.
                    byte red = pixels[(y * width + x) * 4 + 2];
                    if (red is > 24 and < 205) { aa++; break; }
                }
            }
            return (aa, rows);
        }
        finally
        {
            glass.Dispose();
            window.Close();
        }
    }

    private static byte[]? Print(IntPtr handle, int width, int height)
    {
        IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        IntPtr bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(dc, bitmap);
        try
        {
            if (!PrintWindow(handle, dc, 2)) return null; // PW_RENDERFULLCONTENT
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // A crop, each pixel drawn zoom x zoom, over a checkerboard where it is transparent (premultiplied as printed).
    private static void Save(string file, byte[] pixels, int width, int height, int zoom, int x0, int y0, int w, int h)
    {
        w = Math.Min(w, width - x0); h = Math.Min(h, height - y0);
        if (w <= 0 || h <= 0) return;
        var output = new byte[w * zoom * h * zoom * 4];
        for (int y = 0; y < h * zoom; y++)
            for (int x = 0; x < w * zoom; x++)
            {
                int i = ((y0 + y / zoom) * width + x0 + x / zoom) * 4, o = (y * w * zoom + x) * 4;
                double a = pixels[i + 3] / 255.0, back = ((x / 8 + y / 8) % 2 == 0) ? 200 : 140;
                for (int c = 0; c < 3; c++) output[o + c] = (byte)Math.Clamp(pixels[i + c] + back * (1 - a), 0, 255);
                output[o + 3] = 255;
            }
        var image = BitmapSource.Create(w * zoom, h * zoom, 96, 96, PixelFormats.Bgra32, null, output, w * zoom * 4);
        using var stream = File.Create(file);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }

    private static void Wait(double milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr gdiObject);
}
