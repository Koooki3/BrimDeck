using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.LiquidGlass;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Path = System.IO.Path;

// Liquid glass sheets (LiquidRenderer) drawn offscreen, with no window and no screen: a colorful desktop is drawn with WPF, the
// region under each island (padded as the capture side pads it) goes to a texture, the renderer draws into an offscreen target,
// and a staging copy is read back and laid over the desktop with a line of text on the glass. For each shape (capsule, notch,
// open panel), theme (light, dark glass) and scale (100, 150 percent) one sheet: the pointer at the left, the middle, the
// right and away, down the rows; press 0 and 1 across. Also checks the shader's outline against IslandBorder's, pixel by pixel,
// and times a frame with timestamp queries.
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --liquid <folder>
internal static class LiquidPreview
{
    private const double DesktopWidth = 760, DesktopHeight = 420, Margin = 18;

    private sealed record Shape(string Name, double Width, double Height, double Top, double Bottom, double Shoulder, double Y);

    private static readonly Shape[] Shapes =
    [
        new("capsule", 228, 40, 20, 20, 0, 10),
        new("notch", 210, 34, 0, 13, 8, 0),
        new("panel", 470, 250, 0, 26, 0, 0),
    ];

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        using var device = CreateDevice();
        using var renderer = new LiquidRenderer(device);
        int failures = 0;
        foreach (var scale in new[] { 1.0, 1.5 })
        {
            var desktop = Desktop(scale, out int dw, out int dh);
            foreach (var shape in Shapes)
                foreach (bool dark in new[] { false, true })
                {
                    string name = $"{shape.Name}-{(dark ? "dark" : "light")}-{scale * 100:0}";
                    var sheet = Sheet(device, renderer, desktop, dw, dh, shape, dark, scale);
                    Save(sheet, Path.Combine(folder, name + ".png"));
                    Console.WriteLine("Wrote " + name + ".png");
                }
            foreach (var shape in Shapes) failures += Seam(device, renderer, desktop, dw, dh, shape, scale);
        }
        Detail(device, renderer, folder);
        Time(device, renderer);
        failures += Composition(device, renderer);
        return failures == 0 ? 0 : 1;
    }

    private static ID3D11Device CreateDevice()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? device).Success && device is not null) return device;
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device).CheckError();
        return device!;
    }

    // ----- The desktop -----

    // A busy desktop at the scale, as BGRA rows: a sunset gradient, saturated discs, a grid of thin lines (which show the
    // bending), stripes, and a light document window with text, so the glass is seen over dark, light and colorful ground.
    private static byte[] Desktop(double scale, out int width, out int height)
    {
        width = (int)Math.Round(DesktopWidth * scale); height = (int)Math.Round(DesktopHeight * scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0x14, 0x1E, 0x4A), 0), new(Color.FromRgb(0x5B, 0x2A, 0x86), .35), new(Color.FromRgb(0xE8, 0x5D, 0x75), .7), new(Color.FromRgb(0xFF, 0xC3, 0x71), 1)
            }, new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(sky, null, new Rect(0, 0, DesktopWidth, DesktopHeight));
            (Color, double, double, double)[] discs =
            [
                (Color.FromRgb(0x00, 0xE0, 0xB0), 160, 40, 70), (Color.FromRgb(0xFF, 0x3D, 0x7F), 330, 20, 55), (Color.FromRgb(0x2E, 0x9B, 0xFF), 520, 60, 80),
                (Color.FromRgb(0xFF, 0xE0, 0x3D), 640, 10, 40), (Color.FromRgb(0x7C, 0xFF, 0x4F), 260, 200, 60), (Color.FromRgb(0xFF, 0x6A, 0x00), 90, 230, 50)
            ];
            foreach (var (color, x, y, r) in discs) dc.DrawEllipse(new SolidColorBrush(color), null, new Point(x, y), r, r);
            // A photograph instead, as the references are shown over (LIQUID_PHOTO: an image file); the document window stays.
            if (Environment.GetEnvironmentVariable("LIQUID_PHOTO") is { Length: > 0 } photo && File.Exists(photo))
                dc.DrawRectangle(new ImageBrush(new System.Windows.Media.Imaging.BitmapImage(new Uri(photo))) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, DesktopWidth, DesktopHeight));
            bool photoShown = Environment.GetEnvironmentVariable("LIQUID_PHOTO") is { Length: > 0 } shown && File.Exists(shown);
            if (!photoShown) {
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1);
            for (double x = 0; x <= DesktopWidth; x += 20) dc.DrawLine(grid, new Point(x + .5, 0), new Point(x + .5, DesktopHeight));
            for (double y = 0; y <= DesktopHeight; y += 20) dc.DrawLine(grid, new Point(0, y + .5), new Point(DesktopWidth, y + .5));
            var stripe = new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x10, 0x18));
            for (double x = -200; x < DesktopWidth; x += 26)
                dc.DrawGeometry(stripe, null, Geometry.Parse(FormattableString.Invariant($"M {x},120 L {x + 10},120 L {x + 70},180 L {x + 60},180 Z")));
            }
            // A document window across the middle and right.
            var window = new Rect(400, 90, 330, 300);
            dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD4)), 1), window, 8, 8);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)), null, new Rect(window.X, window.Y, window.Width, 26));
            var face = new Typeface("Segoe UI");
            for (int i = 0; i < 9; i++)
                dc.DrawText(new FormattedText("The quick brown fox jumps over the lazy dog 0123456789", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 13, Brushes.Black, scale),
                    new Point(window.X + 12, window.Y + 36 + i * 28));
            dc.DrawText(new FormattedText("BrimDeck", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Black"), 64, Brushes.White, scale), new Point(30, 300));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    // ----- One render -----

    private static (int X, int Y, int Width, int Height) Place(Shape shape, double scale)
    {
        int w = (int)Math.Round(shape.Width * scale), h = (int)Math.Round(shape.Height * scale);
        return ((int)Math.Round((DesktopWidth * scale - w) / 2), (int)Math.Round(shape.Y * scale), w, h);
    }

    // The renderer's output for the island at its place on the desktop: premultiplied BGRA rows, Width x Height.
    private static byte[] Render(ID3D11Device device, LiquidRenderer renderer, byte[] desktop, int dw, int dh, (int X, int Y, int Width, int Height) at, in LiquidFrame frame, int pad)
    {
        // The padded region, the desktop's edge rows repeated where it runs off the screen (as at the top for the notch).
        int bw = at.Width + 2 * pad, bh = at.Height + 2 * pad;
        var region = new byte[bw * bh * 4];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                int sx = Math.Clamp(at.X - pad + x, 0, dw - 1), sy = Math.Clamp(at.Y - pad + y, 0, dh - 1);
                Buffer.BlockCopy(desktop, (sy * dw + sx) * 4, region, (y * bw + x) * 4, 4);
            }
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(region, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var background = device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)bw, (uint)bh, 1, 1),
                [new SubresourceData(handle.AddrOfPinnedObject(), (uint)bw * 4)]);
            using var target = device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)at.Width, (uint)at.Height, 1, 1, BindFlags.RenderTarget));
            using var view = device.CreateRenderTargetView(target);
            renderer.Draw(background, pad, frame, view);
            return Read(device, target, at.Width, at.Height);
        }
        finally { handle.Free(); }
    }

    private static byte[] Read(ID3D11Device device, ID3D11Texture2D texture, int width, int height)
    {
        using var staging = device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        var context = device.ImmediateContext;
        context.CopyResource(staging, texture);
        var mapped = context.Map(staging, 0, MapMode.Read);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), pixels, y * width * 4, width * 4);
        context.Unmap(staging, 0);
        return pixels;
    }

    private static LiquidFrame Frame(Shape shape, bool dark, double scale, (int X, int Y, int Width, int Height) at, (double X, double Y)? pointer, double press)
    {
        var frame = LiquidFrame.Preset(dark, at.Width, at.Height, (float)(shape.Top * scale), (float)(shape.Bottom * scale), (float)(shape.Shoulder * scale), (float)scale) with { Open = shape.Name == "panel" ? 1 : 0 };
        return pointer is { } p
            ? frame with { PointerX = (float)(p.X * at.Width), PointerY = (float)(p.Y * at.Height), Hover = 1, Press = (float)press }
            : frame with { Press = (float)press };
    }

    // ----- Sheets -----

    private static readonly (string Name, (double X, double Y)? At)[] Pointers = [("left", (.12, .5)), ("middle", (.5, .45)), ("right", (.88, .5)), ("away", null)];

    private static BitmapSource Sheet(ID3D11Device device, LiquidRenderer renderer, byte[] desktop, int dw, int dh, Shape shape, bool dark, double scale)
    {
        var at = Place(shape, scale);
        int pad = (int)Math.Ceiling(48 * scale);
        int margin = (int)Math.Round(Margin * scale);
        int tx = Math.Max(0, at.X - margin), ty = Math.Max(0, at.Y - margin);
        int tw = Math.Min(dw, at.X + at.Width + margin) - tx, th = Math.Min(dh, at.Y + at.Height + margin) - ty;
        var visual = new DrawingVisual();
        const int gap = 10, label = 18;
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)), null, new Rect(0, 0, 2 * tw + 3 * gap, 4 * (th + label) + 5 * gap));
            for (int row = 0; row < Pointers.Length; row++)
                for (int column = 0; column < 2; column++)
                {
                    var frame = Frame(shape, dark, scale, at, Pointers[row].At, column);
                    var glass = Render(device, renderer, desktop, dw, dh, at, frame, pad);
                    var tile = Compose(desktop, dw, glass, at, tx, ty, tw, th, dark, scale);
                    double x = gap + column * (tw + gap), y = gap + row * (th + label + gap);
                    dc.DrawText(new FormattedText($"{shape.Name} {(dark ? "dark" : "light")} {scale * 100:0}%  pointer {Pointers[row].Name}  press {column}",
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, 1), new Point(x, y));
                    dc.DrawImage(tile, new Rect(x, y + label, tw, th));
                }
        }
        var sheet = new RenderTargetBitmap(2 * tw + 3 * gap, 4 * (th + label) + 5 * gap, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        return sheet;
    }

    // The desktop region with the glass laid over it (premultiplied over), and text on the glass as the panel would draw it.
    private static BitmapSource Compose(byte[] desktop, int dw, byte[] glass, (int X, int Y, int Width, int Height) at, int tx, int ty, int tw, int th, bool dark, double scale)
    {
        var pixels = new byte[tw * th * 4];
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                int dx = tx + x, dy = ty + y, o = (y * tw + x) * 4, d = (dy * dw + dx) * 4;
                int gx = dx - at.X, gy = dy - at.Y;
                if (gx >= 0 && gy >= 0 && gx < at.Width && gy < at.Height)
                {
                    int g = (gy * at.Width + gx) * 4;
                    double a = glass[g + 3] / 255d;
                    for (int c = 0; c < 3; c++) pixels[o + c] = (byte)Math.Round(glass[g + c] + (1 - a) * desktop[d + c]);
                }
                else for (int c = 0; c < 3; c++) pixels[o + c] = desktop[d + c];
                pixels[o + 3] = 255;
            }
        var image = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Pbgra32, null, pixels, tw * 4);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, tw, th));
            var ink = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A));
            var text = new FormattedText("12:34  Liquid glass  ▶ Now playing", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13 * scale, ink, 1);
            dc.DrawText(text, new Point(at.X - tx + (at.Width - text.Width) / 2, at.Y - ty + Math.Min(at.Height / 2, 20 * scale) - text.Height / 2));
        }
        var bitmap = new RenderTargetBitmap(tw, th, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    // Close-ups: a corner of each shape at 150 percent, four times enlarged, pixel for pixel, with the pointer near it.
    private static void Detail(ID3D11Device device, LiquidRenderer renderer, string folder)
    {
        const double scale = 1.5;
        var desktop = Desktop(scale, out int dw, out int dh);
        foreach (var shape in Shapes)
            foreach (bool dark in new[] { false, true })
            {
                var at = Place(shape, scale);
                var frame = Frame(shape, dark, scale, at, (.08, .7), 0);
                var glass = Render(device, renderer, desktop, dw, dh, at, frame, (int)Math.Ceiling(48 * scale));
                int size = (int)(90 * scale);
                int tx = Math.Max(0, at.X - 12), ty = Math.Max(0, at.Y + at.Height - size + 12);
                var tile = Compose(desktop, dw, glass, at, tx, ty, size, Math.Min(size, dh - ty), dark, scale);
                var nearest = Enlarge(tile, 4);
                string name = $"detail-{shape.Name}-{(dark ? "dark" : "light")}-150.png";
                Save(nearest, Path.Combine(folder, name));
                Console.WriteLine("Wrote " + name);
            }
    }

    // ----- Checks -----

    // The shader's coverage against IslandBorder's outline: exact coverage (the outline filled by WPF eight times larger,
    // without antialiasing, and averaged down), which it must follow closely, or a seam would show between the glass and the
    // island drawn over it; and, for information, WPF's own antialiased fill at the size, which itself strays from exact.
    private static int Seam(ID3D11Device device, LiquidRenderer renderer, byte[] desktop, int dw, int dh, Shape shape, double scale)
    {
        var at = Place(shape, scale);
        var frame = Frame(shape, false, scale, at, null, 0);
        var glass = Render(device, renderer, desktop, dw, dh, at, frame, (int)Math.Ceiling(48 * scale));
        var outline = IslandBorder.BuildOutline(at.Width, at.Height, shape.Shoulder * scale, new CornerRadius(shape.Top * scale, shape.Top * scale, shape.Bottom * scale, shape.Bottom * scale));
        var wpf = Coverage(outline, at.Width, at.Height, 1);
        var exact = Coverage(outline, at.Width, at.Height, 8);
        var (worst, worstAt, mean, signed) = Compare(glass, wpf, at.Width);
        var (exactWorst, _, exactMean, exactSigned) = Compare(glass, exact, at.Width);
        bool ok = exactWorst <= 48 && exactMean <= 4;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: outline {shape.Name} {scale * 100:0}%: against exact coverage worst {exactWorst}/255, mean over the edge {exactMean:0.0} (glass {exactSigned:+0.0;-0.0}); against WPF's own antialiasing worst {worst} at {worstAt % at.Width},{worstAt / at.Width}, mean {mean:0.0} (glass {signed:+0.0;-0.0})");
        return ok ? 0 : 1;
    }

    // The outline filled by WPF, its alpha per pixel; drawn `factor` times larger and averaged down for exact coverage.
    private static byte[] Coverage(Geometry outline, int width, int height, int factor)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(factor, factor));
            dc.DrawGeometry(Brushes.White, null, outline);
        }
        int w = width * factor, h = height * factor;
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        if (factor > 1) RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
        bitmap.Render(visual);
        var big = new byte[w * h * 4];
        bitmap.CopyPixels(big, w * 4, 0);
        var alpha = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int sum = 0;
                for (int j = 0; j < factor; j++)
                    for (int i = 0; i < factor; i++) sum += big[((y * factor + j) * w + x * factor + i) * 4 + 3];
                alpha[(y * width + x) * 4 + 3] = (byte)Math.Round(sum / (double)(factor * factor));
            }
        return alpha;
    }

    // The worst difference in alpha and where, and the mean and signed mean (glass minus reference) over the edge pixels.
    private static (int Worst, int At, double Mean, double Signed) Compare(byte[] glass, byte[] reference, int width)
    {
        int worst = 0, at = 0, edge = 0; long sum = 0, signed = 0;
        for (int i = 3; i < glass.Length; i += 4)
        {
            int diff = Math.Abs(reference[i] - glass[i]);
            if (reference[i] is > 0 and < 255 || glass[i] is > 0 and < 255) { sum += diff; signed += glass[i] - reference[i]; edge++; }
            if (diff > worst) { worst = diff; at = i / 4; }
        }
        return (worst, at, edge > 0 ? (double)sum / edge : 0, edge > 0 ? (double)signed / edge : 0);
    }

    // GPU time of a frame (the copy into the mips, their making and the draw) for the open panel at 150 percent, the largest
    // the island gets, and for the capsule, from timestamp queries over many frames.
    private static void Time(ID3D11Device device, LiquidRenderer renderer)
    {
        const double scale = 1.5;
        var desktop = Desktop(scale, out int dw, out int dh);
        var context = device.ImmediateContext;
        foreach (var shape in new[] { Shapes[2], Shapes[0] })
        {
            var at = Place(shape, scale);
            int pad = (int)Math.Ceiling(48 * scale);
            int bw = at.Width + 2 * pad, bh = at.Height + 2 * pad;
            var region = new byte[bw * bh * 4];
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    Buffer.BlockCopy(desktop, (Math.Clamp(at.Y - pad + y, 0, dh - 1) * dw + Math.Clamp(at.X - pad + x, 0, dw - 1)) * 4, region, (y * bw + x) * 4, 4);
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(region, System.Runtime.InteropServices.GCHandleType.Pinned);
            using var background = device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)bw, (uint)bh, 1, 1),
                [new SubresourceData(handle.AddrOfPinnedObject(), (uint)bw * 4)]);
            handle.Free();
            using var target = device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)at.Width, (uint)at.Height, 1, 1, BindFlags.RenderTarget));
            using var view = device.CreateRenderTargetView(target);
            using var disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            using var start = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            using var end = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            var frame = Frame(shape, false, scale, at, (.3, .5), .5);
            var times = new List<double>();
            for (int i = 0; i < 600; i++)
            {
                context.Begin(disjoint);
                context.End(start);
                renderer.Draw(background, pad, frame with { PointerX = frame.PointerX + i % 7 }, view);
                context.End(end);
                context.End(disjoint);
                QueryDataTimestampDisjoint data;
                while (!context.GetData(disjoint, out data)) Thread.Yield();
                ulong a, b;
                while (!context.GetData(start, out a)) Thread.Yield();
                while (!context.GetData(end, out b)) Thread.Yield();
                if (!data.Disjoint && i >= 300) times.Add((b - a) * 1000.0 / data.Frequency);
            }
            times.Sort();
            using var dxgi = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            Console.WriteLine($"GPU time on {adapter.Description.Description}, {shape.Name} {at.Width}x{at.Height} at 150%: median {times[times.Count / 2]:0.000} ms, 90th {times[(int)(times.Count * .9)]:0.000} ms");
        }
    }

    // ----- Top-of-screen backgrounds -----

    // The glass over still backgrounds of the top of a screen (a browser's tab strip, a wallpaper, a video, a page; made by the
    // comparison harness, pr-plan/liquid-glass/round-5/harness), as the references are rendered over the same ones: for each
    // background <name>-100.png / <name>-150.png in the folder (1200 x 300 DIP), each shape, theme and scale, the background with
    // the glass laid over it (<bg>-<shape>-<theme>-<scale>.png) and the glass's coverage (...-alpha.png). The harness adds the
    // island's shadow and text. Pointer away; for the capsule also with the pointer at its left, hovered (-hover).
    //   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --liquid-bg <backgrounds> <output folder>
    private static readonly Shape[] TopShapes =
    [
        new("wide", 470, 36, 18, 18, 0, 8),
        new("capsule", 228, 40, 20, 20, 0, 8),
        new("notch", 210, 34, 0, 13, 8, 0),
        new("panel", 470, 250, 0, 26, 0, 0),
    ];

    public static int TimeOnly()
    {
        using var device = CreateDevice();
        using var renderer = new LiquidRenderer(device);
        Time(device, renderer);
        Time(device, renderer);
        return 0;
    }

    public static int RunBackgrounds(string backgrounds, string folder)
    {
        Directory.CreateDirectory(folder);
        using var device = CreateDevice();
        using var renderer = new LiquidRenderer(device);
        foreach (var file in Directory.GetFiles(backgrounds, "*-100.png").Concat(Directory.GetFiles(backgrounds, "*-150.png")).Order())
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            double scale = stem.EndsWith("-150", StringComparison.Ordinal) ? 1.5 : 1;
            string bg = stem[..stem.LastIndexOf('-')];
            var decoded = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(Path.GetFullPath(file)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            int dw = decoded.PixelWidth, dh = decoded.PixelHeight;
            var desktop = new byte[dw * dh * 4];
            decoded.CopyPixels(desktop, dw * 4, 0);
            foreach (var shape in TopShapes)
                foreach (bool dark in new[] { false, true })
                    foreach (bool hover in new[] { false, true })
                    {
                        if (hover && shape.Name != "capsule") continue;
                        int w = (int)Math.Round(shape.Width * scale), h = (int)Math.Round(shape.Height * scale);
                        var at = ((int)Math.Round((dw - w) / 2.0), (int)Math.Round(shape.Y * scale), w, h);
                        var frame = Frame(shape, dark, scale, at, hover ? (.2, .45) : null, 0);
                        var glass = Render(device, renderer, desktop, dw, dh, at, frame, (int)Math.Ceiling(48 * scale));
                        var composed = (byte[])desktop.Clone();
                        var alpha = new byte[dw * dh];
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                int g = (y * w + x) * 4, d = ((at.Item2 + y) * dw + at.Item1 + x) * 4;
                                double a = glass[g + 3] / 255d;
                                for (int c = 0; c < 3; c++) composed[d + c] = (byte)Math.Clamp(Math.Round(glass[g + c] + (1 - a) * desktop[d + c]), 0, 255);
                                alpha[(at.Item2 + y) * dw + at.Item1 + x] = glass[g + 3];
                            }
                        string name = $"{bg}-{shape.Name}-{(dark ? "dark" : "light")}-{scale * 100:0}{(hover ? "-hover" : "")}";
                        Save(BitmapSource.Create(dw, dh, 96, 96, PixelFormats.Bgra32, null, composed, dw * 4), Path.Combine(folder, name + ".png"));
                        Save(BitmapSource.Create(dw, dh, 96, 96, PixelFormats.Gray8, null, alpha, dw), Path.Combine(folder, name + "-alpha.png"));
                    }
            Console.WriteLine("Rendered over " + stem);
        }
        Time(device, renderer);
        return 0;
    }

    // Each pixel as a block of factor x factor.
    private static BitmapSource Enlarge(BitmapSource source, int factor)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        var pixels = new int[w * h];
        source.CopyPixels(pixels, w * 4, 0);
        var big = new int[w * h * factor * factor];
        for (int y = 0; y < h * factor; y++)
            for (int x = 0; x < w * factor; x++) big[y * w * factor + x] = pixels[y / factor * w + x / factor];
        return BitmapSource.Create(w * factor, h * factor, 96, 96, PixelFormats.Pbgra32, null, big, w * factor * 4);
    }

    // The way BrimDeck shows the glass, short of a window: a compositor on this thread, the renderer's brush made from its swap
    // chain, and frames presented as the island grows (the buffers grow) and shrinks again (only the source size changes).
    private static int Composition(ID3D11Device device, LiquidRenderer renderer)
    {
        try
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
                Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), Type = 2, Apartment = 2 }, out _));
            using var compositor = new Windows.UI.Composition.Compositor();
            using var brush = renderer.CreateBrush(compositor);
            foreach (var shape in new[] { Shapes[0], Shapes[2], Shapes[1] })
            {
                var at = Place(shape, 1);
                int pad = 48, bw = at.Width + 2 * pad, bh = at.Height + 2 * pad;
                using var background = device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)bw, (uint)bh, 1, 1));
                renderer.Render(background, pad, Frame(shape, false, 1, at, (.5, .5), 0));
            }
            bool ok = brush is Windows.UI.Composition.CompositionSurfaceBrush { Surface: not null };
            Console.WriteLine((ok ? "PASS" : "FAIL") + ": composition brush from the swap chain, frames presented at three sizes");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: composition: " + ex);
            return 1;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DispatcherQueueOptions { public int Size, Type, Apartment; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
