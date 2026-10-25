using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
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
        failures += SwitchContrast(device, renderer);
        failures += SurfaceOver(device, renderer);
        return failures == 0 ? 0 : 1;
    }

    // The island's own surface over liquid glass, composed as it is shown: the glass (the renderer's output over the desktop),
    // and over it the island as WPF draws it, an IslandBorder filled with the glass theme's surface color at the opacity
    // MainWindow's Held handler gives it (GlassBackdrop.HeldShare and SurfaceOpacity, the surface aside while liquid glass is
    // shown). For an island all of glass, in every state of the motion (moving 1 while it opens, a page resizes or the glass
    // arrives; 0 at rest; between) and whether or not the theme's blur is held, the glass must show through unchanged, in both
    // glass themes; for the island turned to the indicator's color (material 0) the surface must be whole again. The rule as it
    // was (1 - the held blur's share over liquid glass) is composed too, and must hide the glass, so the check can fail.
    private static int SurfaceOver(ID3D11Device device, LiquidRenderer renderer)
    {
        int failures = 0;
        var desktop = Desktop(1, out int dw, out int dh);
        var shape = Shapes[0];
        var at = Place(shape, 1);
        try
        {
            foreach (var id in new[] { PanelThemes.GlassLight, PanelThemes.GlassDark })
            {
                PanelTheme.Show(PanelThemes.Resolve(PanelThemes.BuiltIn(id)!));
                var tint = PanelTheme.ToColor(PanelTheme.Current.Effects.Glass!.Tint);
                var glass = Render(device, renderer, desktop, dw, dh, at, Frame(shape, id == PanelThemes.GlassDark, 1, at, null, 0), 48);
                var bare = Compose(desktop, dw, at, glass, shape, tint, 0);
                double alpha = tint.A / 255d;
                int worst = 0;
                foreach (bool holds in new[] { true, false })
                    foreach (double moving in new[] { 0, .5, 1 })
                        worst = Math.Max(worst, MaxDifference(bare, Compose(desktop, dw, at, glass, shape, tint, GlassBackdrop.SurfaceOpacity(true, GlassBackdrop.HeldShare(true, holds, 1, moving), alpha))));
                // The rule as it was, opening (moving 1): 1 - the held blur's share, which is then 0.
                int old = MaxDifference(bare, Compose(desktop, dw, at, glass, shape, tint, 1 - GlassBackdrop.HeldShare(false, true, 1, 1)));
                double indicator = GlassBackdrop.SurfaceOpacity(true, GlassBackdrop.HeldShare(true, true, 0, 0), alpha);
                bool ok = worst <= 1 && old > 40 && indicator == 1;
                if (!ok) failures++;
                Console.WriteLine(FormattableString.Invariant($"{(ok ? "PASS" : "FAIL")}: {id}: liquid glass shows through the island's surface while opening, resizing and at rest (largest change {worst}; the old rule changed it by {old}); turned to the indicator's color the surface is whole ({indicator:0.##})"));
            }
        }
        finally { PanelTheme.Show(PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!)); }
        return failures;
    }

    // The desktop under the island, the glass over it (premultiplied), and the island's surface over that at an opacity:
    // rendered by WPF, BGRA rows of the island's size.
    private static byte[] Compose(byte[] desktop, int dw, (int X, int Y, int Width, int Height) at, byte[] glass, Shape shape, Color tint, double opacity)
    {
        int w = at.Width, h = at.Height;
        var under = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(desktop, ((at.Y + y) * dw + at.X) * 4, under, y * w * 4, w * 4);
        var grid = new System.Windows.Controls.Grid { Width = w, Height = h };
        grid.Children.Add(new System.Windows.Controls.Image { Source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, under, w * 4), Stretch = Stretch.None });
        grid.Children.Add(new System.Windows.Controls.Image { Source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, glass, w * 4), Stretch = Stretch.None });
        grid.Children.Add(new IslandBorder
        {
            Background = new SolidColorBrush(tint) { Opacity = opacity },
            CornerRadius = new CornerRadius(shape.Top, shape.Top, shape.Bottom, shape.Bottom), ShoulderRadius = shape.Shoulder
        });
        grid.Measure(new Size(w, h));
        grid.Arrange(new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(grid);
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    private static int MaxDifference(byte[] a, byte[] b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i]));
        return max;
    }

    // Automatic liquid glass's turn, as GlassBackdrop and App run it: the glass eases from the old look to the new over 320 ms
    // (Soft.InOut), and the text (one glyph run) turns from the old look's ink to the new one's in one step once the glass
    // passes LiquidFrame.TextTurn over the measured backdrop. Ten points across the turn, over black, a dark gray, a light
    // gray (#D8D8D8) and white, to dark and to light: the text then shown must keep 3:1 over the glass under it (the worst
    // 5 percent of the pixels in the middle of the capsule).
    private static int SwitchContrast(ID3D11Device device, LiquidRenderer renderer)
    {
        const double scale = 1.5;
        int w = (int)(228 * scale), h = (int)(40 * scale), pad = (int)Math.Ceiling(48 * scale);
        int dw = w + 2 * pad, dh = h + 2 * pad;
        var light = LiquidFrame.Preset(false, w, h, (float)(20 * scale), (float)(20 * scale), 0, (float)scale);
        var deep = LiquidFrame.Preset(true, w, h, (float)(20 * scale), (float)(20 * scale), 0, (float)scale);
        (byte R, byte G, byte B) darkInk = (0x10, 0x12, 0x17), lightInk = (0xF6, 0xF7, 0xF9);
        double worst = double.MaxValue;
        string worstAt = "";
        foreach (var (name, level) in new[] { ("black", 10), ("dark gray", 64), ("#D8 gray", 0xD8), ("white", 255) })
            foreach (bool toDark in new[] { true, false })
            {
                var desktop = new byte[dw * dh * 4];
                for (int i = 0; i < desktop.Length; i += 4) { desktop[i] = desktop[i + 1] = desktop[i + 2] = (byte)level; desktop[i + 3] = 255; }
                double luma = level / 255.0;
                bool crossed = false;
                for (int point = 0; point < 10; point++)
                {
                    double t = point / 9.0;
                    double blend = Soft.InOut.Ease(t);
                    double dark = toDark ? blend : 1 - blend;
                    // The turn is looked for on every frame (60 a second over the 320 ms), as GlassBackdrop does.
                    for (int frame = 0; frame <= (int)Math.Round(t * 320 / 16.0); frame++)
                    {
                        double f = Math.Min(1, frame * 16 / 320.0), b = Soft.InOut.Ease(f);
                        crossed |= LiquidFrame.TextTurns(luma, toDark ? b : 1 - b, toDark, f >= 1);
                    }
                    var glass = Render(device, renderer, desktop, dw, dh, (pad, pad, w, h), LiquidFrame.Blend(light, deep, (float)dark), pad);
                    // The ink shown: the old look's until the turn, then the new one's.
                    bool inkLight = crossed ? toDark : !toDark;
                    var ink = inkLight ? lightInk : darkInk;
                    var under = new List<double>();
                    for (int y = h * 3 / 10; y < h * 7 / 10; y++)
                        for (int x = w * 3 / 10; x < w * 7 / 10; x++)
                        {
                            int o = (y * w + x) * 4;
                            under.Add(Luminance(glass[o + 2], glass[o + 1], glass[o]));
                        }
                    under.Sort();
                    double bg = inkLight ? under[(int)(under.Count * .95)] : under[(int)(under.Count * .05)];
                    double fg = Luminance(ink.R, ink.G, ink.B);
                    double ratio = (Math.Max(bg, fg) + .05) / (Math.Min(bg, fg) + .05);
                    if (ratio < worst) { worst = ratio; worstAt = FormattableString.Invariant($"over {name} to {(toDark ? "dark" : "light")} at {t * 320:0} ms"); }
                }
            }
        bool ok = worst >= 3;
        Console.WriteLine(FormattableString.Invariant($"{(ok ? "PASS" : "FAIL")}: automatic glass turn: the text shown keeps {worst:0.00}:1 at worst over the glass, ten points per turn ({worstAt})"));
        return ok ? 0 : 1;

        static double Luminance(byte r, byte g, byte b)
        {
            static double Channel(byte v) { double c = v / 255.0; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
            return .2126 * Channel(r) + .7152 * Channel(g) + .0722 * Channel(b);
        }
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
        var frame = LiquidFrame.Preset(dark, at.Width, at.Height, (float)(shape.Top * scale), (float)(shape.Bottom * scale), (float)(shape.Shoulder * scale), (float)scale) with { Open = shape.Name.StartsWith("panel", StringComparison.Ordinal) ? 1 : 0 };
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
        // The user's own cases: the open panel at 585 x 288, and the thin bar over a maximized window (680 x 20).
        new("panel585", 585, 288, 0, 26, 0, 0),
        new("bar", 680, 20, 10, 10, 0, 2),
    ];

    public static int TimeOnly()
    {
        using var device = CreateDevice();
        using var renderer = new LiquidRenderer(device);
        Time(device, renderer);
        Time(device, renderer);
        return 0;
    }

    // Automatic liquid glass, simulated at 60 frames a second as GlassBackdrop and App run it: the capsule (228 x 40, 150%) over a
    // background white on the left and black on the right glides from the white side to the black and back. Each frame is
    // drawn with the look blended as GlassBackdrop eases it, the backdrop measured by the renderer (TryBackdrop, read a frame
    // later), the look decided by GlassAppearance, and the text crossfading as MainWindow.ThemeFade does (the old text over
    // the new, fading over 320 ms). Writes auto-switch.png: one tile every 200 ms, with the time, the measure, the look and
    // the blend under each.
    public static int RunAuto(string folder)
    {
        Directory.CreateDirectory(folder);
        const double scale = 1.5;
        int dw = (int)(1200 * scale), dh = (int)(300 * scale);
        var desktop = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                byte v = x < dw / 2 ? (byte)255 : (byte)10;
                int o = (y * dw + x) * 4; desktop[o] = desktop[o + 1] = desktop[o + 2] = v; desktop[o + 3] = 255;
            }
        using var device = CreateDevice();
        using var renderer = new LiquidRenderer(device);
        var shape = TopShapes[1];
        int w = (int)Math.Round(shape.Width * scale), h = (int)Math.Round(shape.Height * scale), top = (int)Math.Round(shape.Y * scale);
        // Where the capsule's centre is at time t (ms): white side, glide, black side, glide back, white side.
        double CentreAt(double t) => t < 1000 ? 300 : t < 1600 ? 300 + 600 * Soft.InOut.Ease((t - 1000) / 600) : t < 3600 ? 900 : t < 4200 ? 900 - 600 * Soft.InOut.Ease((t - 3600) / 600) : 300;
        GlassAppearance? look = null;
        double dark = -1, from = 0, to = 0; long since = -10000;
        double lastLuma = double.NaN;
        var tiles = new List<(BitmapSource Tile, string Label)>();
        int pad = (int)Math.Ceiling(48 * scale);
        for (int frameIndex = 0; frameIndex <= 360; frameIndex++)
        {
            long now = (long)Math.Round(frameIndex * 1000 / 60.0);
            double centre = CentreAt(now) * scale;
            var at = ((int)Math.Round(centre - w / 2.0), top, w, h);
            double target = look?.Dark == true ? 1 : 0;
            if (dark < 0) dark = from = to = target;
            else if (target != to) { from = dark; to = target; since = now; }
            if (dark != to) { double p = Math.Clamp((now - since) / 320.0, 0, 1); dark = p >= 1 ? to : from + (to - from) * Soft.InOut.Ease(p); }
            var frame = LiquidFrame.Blend(LiquidFrame.Preset(false, w, h, (float)(shape.Top * scale), (float)(shape.Bottom * scale), 0, (float)scale),
                LiquidFrame.Preset(true, w, h, (float)(shape.Top * scale), (float)(shape.Bottom * scale), 0, (float)scale), (float)dark);
            var glass = Render(device, renderer, desktop, dw, dh, at, frame, pad);
            if (renderer.TryBackdrop(out double luma))
            {
                lastLuma = luma;
                if (look is null) { look = new GlassAppearance(false); look.Reset(luma < .5, now); }
                else look.Feed(luma, now);
            }
            if (frameIndex % 12 != 0) continue;
            // The text: the new look's under the old look's picture fading out (ThemeFade), from when the look changed.
            double textFade = 1 - Soft.InOut.Ease(Math.Clamp((now - since) / 320.0, 0, 1));
            int margin = (int)(26 * scale);
            int tx = Math.Clamp(at.Item1 - margin, 0, dw - 1), tw = Math.Min(dw - tx, w + 2 * margin), th = top + h + margin;
            var tile = ComposeAuto(desktop, dw, glass, at, tx, 0, tw, th, to > .5, textFade, scale);
            string measured = double.IsNaN(lastLuma) ? "-" : lastLuma.ToString("0.00", CultureInfo.InvariantCulture);
            tiles.Add((tile, FormattableString.Invariant($"{now / 1000.0:0.0} s  luma {measured}  {(look?.Dark == true ? "dark" : "light")}  blend {dark:0.00}")));
        }
        int cw = tiles.Max(t => t.Tile.PixelWidth), ch = tiles[0].Tile.PixelHeight, columns = 5, rows = (tiles.Count + columns - 1) / columns;
        const int gap = 10, label = 20;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), null, new Rect(0, 0, columns * (cw + gap) + gap, rows * (ch + label + gap) + gap));
            for (int i = 0; i < tiles.Count; i++)
            {
                double x = gap + i % columns * (cw + gap), y = gap + i / columns * (ch + label + gap);
                dc.DrawImage(tiles[i].Tile, new Rect(x, y, tiles[i].Tile.PixelWidth, ch));
                dc.DrawText(new FormattedText(tiles[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, 1), new Point(x, y + ch + 2));
            }
        }
        var sheet = new RenderTargetBitmap(columns * (cw + gap) + gap, rows * (ch + label + gap) + gap, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        Save(sheet, Path.Combine(folder, "auto-switch.png"));
        Console.WriteLine("Wrote auto-switch.png");
        return 0;
    }

    // The glass over the desktop with the text of the look now, and the old look's text over it fading out (oldText: its opacity).
    private static BitmapSource ComposeAuto(byte[] desktop, int dw, byte[] glass, (int X, int Y, int Width, int Height) at, int tx, int ty, int tw, int th, bool dark, double oldText, double scale)
    {
        var plain = Compose(desktop, dw, glass, at, tx, ty, tw, th, dark, scale);
        if (oldText <= .001) return plain;
        var old = Compose(desktop, dw, glass, at, tx, ty, tw, th, !dark, scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(plain, new Rect(0, 0, tw, th));
            dc.PushOpacity(oldText);
            dc.DrawImage(old, new Rect(0, 0, tw, th));
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(tw, th, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    // The port proof: the glass liquid-glass-web draws in its showcase (lgw.html in the harness lays out the same glasses),
    // drawn by the renderer in the source's own uv field (Faithful) over the same backgrounds, at the same places and sizes
    // (CSS pixels) and scales. For each <name>-100.png / -150.png in the folder: <name>-<scale>.png, the background with every
    // glass over it, and ...-alpha.png, the glasses' coverage (the harness adds the source's outer shadow under them).
    //   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --liquid-port <backgrounds> <output folder>
    private static readonly (string Name, int X, int Y, int W, int H, int R)[] PortGlass =
    [
        ("lens", 80, 60, 150, 150, 75), ("pill", 280, 80, 260, 96, 48), ("nav", 600, 40, 140, 44, 22), ("small", 610, 120, 110, 110, 55),
        ("card", 820, 40, 300, 200, 24), ("panel", 80, 290, 470, 250, 26), ("capsule", 620, 300, 470, 36, 18), ("bar", 620, 400, 500, 20, 10),
    ];

    public static int RunPort(string backgrounds, string folder, bool faithful = true)
    {
        Directory.CreateDirectory(folder);
        using var device = CreateDevice();
        using var renderer = new LiquidRenderer(device);
        foreach (var file in Directory.GetFiles(backgrounds, "*-100.png").Concat(Directory.GetFiles(backgrounds, "*-150.png")).Order())
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            float scale = stem.EndsWith("-150", StringComparison.Ordinal) ? 1.5f : 1;
            var decoded = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(Path.GetFullPath(file)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            int dw = decoded.PixelWidth, dh = decoded.PixelHeight;
            var desktop = new byte[dw * dh * 4];
            decoded.CopyPixels(desktop, dw * 4, 0);
            var composed = (byte[])desktop.Clone();
            var alpha = new byte[dw * dh];
            foreach (var g in PortGlass)
            {
                int x = (int)Math.Round(g.X * scale), y = (int)Math.Round(g.Y * scale), w = (int)Math.Round(g.W * scale), h = (int)Math.Round(g.H * scale);
                float r = g.R * scale;
                var frame = new LiquidFrame(w, h, r, r, 0, 1, 1, 1, .08f, float.NaN, float.NaN, 0, 0, 0, 0, Scale: scale, Faithful: faithful);
                var glass = Render(device, renderer, desktop, dw, dh, (x, y, w, h), frame, (int)Math.Ceiling(48 * scale));
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int o = (j * w + i) * 4, d = ((y + j) * dw + x + i) * 4;
                        double a = glass[o + 3] / 255d;
                        for (int c = 0; c < 3; c++) composed[d + c] = (byte)Math.Clamp(Math.Round(glass[o + c] + (1 - a) * composed[d + c]), 0, 255);
                        alpha[(y + j) * dw + x + i] = Math.Max(alpha[(y + j) * dw + x + i], glass[o + 3]);
                    }
            }
            Save(BitmapSource.Create(dw, dh, 96, 96, PixelFormats.Bgra32, null, composed, dw * 4), Path.Combine(folder, stem + ".png"));
            Save(BitmapSource.Create(dw, dh, 96, 96, PixelFormats.Gray8, null, alpha, dw), Path.Combine(folder, stem + "-alpha.png"));
            Console.WriteLine("Ported over " + stem);
        }
        return 0;
    }

    // Every state the glass takes, over a few backgrounds, both looks: the capsule, the wide capsule, the notch, the thin bar,
    // the open panel at its sizes (the pages: monitor 470 x 250, music 534 x 299, notices 481 x 300, the user's 585 x 288), the
    // opening's frames between the capsule and the panel (a quarter, half, three quarters), and the capsule pressed and hovered.
    // Writes states-<background>.png. The pages' own content is WPF's and is not drawn here.
    //   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --liquid-states <backgrounds> <output folder>
    public static int RunStates(string backgrounds, string folder)
    {
        Directory.CreateDirectory(folder);
        const double scale = 1.5;
        using var device = CreateDevice();
        using var renderer = new LiquidRenderer(device);
        Shape Mid(double t) => new($"opening {t * 100:0}%", 228 + (470 - 228) * t, 40 + (250 - 40) * t, t < .5 ? 20 * (1 - 2 * t) : 0, 20 + (26 - 20) * t, 0, 8 * (1 - t));
        var states = new List<(Shape Shape, double Press, (double, double)? Pointer)>
        {
            (TopShapes[1], 0, null), (TopShapes[1], 1, (.3, .5)), (TopShapes[1], 0, (.2, .45)), (TopShapes[0], 0, null), (TopShapes[2], 0, null),
            (new("bar", 680, 20, 10, 10, 0, 2), 0, null), (Mid(.25), 0, null), (Mid(.5), 0, null), (Mid(.75), 0, null),
            (new("monitor 470x250", 470, 250, 0, 26, 0, 0), 0, null), (new("music 534x299", 534, 299, 0, 26, 0, 0), 0, null),
            (new("notices 481x300", 481, 300, 0, 26, 0, 0), 0, null), (new("user 585x288", 585, 288, 0, 26, 0, 0), 0, null),
        };
        foreach (var file in Directory.GetFiles(backgrounds, "*-150.png").Order())
        {
            string bg = Path.GetFileNameWithoutExtension(file)[..^4];
            if (bg is not ("grayui" or "chrome-dark" or "video" or "chrome-light")) continue;
            var decoded = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(Path.GetFullPath(file)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            int dw = decoded.PixelWidth, dh = decoded.PixelHeight;
            var desktop = new byte[dw * dh * 4];
            decoded.CopyPixels(desktop, dw * 4, 0);
            var tiles = new List<(BitmapSource Tile, string Label)>();
            foreach (bool dark in new[] { false, true })
                foreach (var (shape, press, pointer) in states)
                {
                    int w = (int)Math.Round(shape.Width * scale), h = (int)Math.Round(shape.Height * scale);
                    var at = ((int)Math.Round((dw - w) / 2.0), (int)Math.Round(shape.Y * scale), w, h);
                    var frame = Frame(shape, dark, scale, at, pointer, press) with { Open = shape.Height > 100 ? 1 : 0 };
                    var glass = Render(device, renderer, desktop, dw, dh, at, frame, (int)Math.Ceiling(48 * scale));
                    int margin = (int)(16 * scale);
                    int tx = Math.Max(0, at.Item1 - margin), tw = Math.Min(dw - tx, w + 2 * margin), th = Math.Min(dh, at.Item2 + h + margin);
                    tiles.Add((Compose(desktop, dw, glass, at, tx, 0, tw, th, dark, scale),
                        $"{(dark ? "dark" : "light")} · {shape.Name}{(press > 0 ? " · pressed" : pointer is not null ? " · hovered" : "")}"));
                }
            const int gap = 10, label = 18, columns = 4;
            int cw = 360, rows = (tiles.Count + columns - 1) / columns;
            var heights = tiles.Select(t => t.Tile.PixelHeight * cw / (double)t.Tile.PixelWidth).ToList();
            double rowHeight = heights.Max();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), null, new Rect(0, 0, columns * (cw + gap) + gap, rows * (rowHeight + label + gap) + gap));
                for (int i = 0; i < tiles.Count; i++)
                {
                    double x = gap + i % columns * (cw + gap), y = gap + i / columns * (rowHeight + label + gap);
                    dc.DrawText(new FormattedText(tiles[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, 1), new Point(x, y));
                    dc.DrawImage(tiles[i].Tile, new Rect(x, y + label, cw, heights[i]));
                }
            }
            var sheet = new RenderTargetBitmap(columns * (cw + gap) + gap, (int)(rows * (rowHeight + label + gap) + gap), 96, 96, PixelFormats.Pbgra32);
            sheet.Render(visual);
            Save(sheet, Path.Combine(folder, $"states-{bg}.png"));
            Console.WriteLine("Wrote states-" + bg);
        }
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
                foreach (string look in new[] { "light", "dark", "lgw" })
                    foreach (bool hover in new[] { false, true })
                    {
                        if (hover && (shape.Name != "capsule" || look == "lgw")) continue;
                        bool dark = look == "dark";
                        int w = (int)Math.Round(shape.Width * scale), h = (int)Math.Round(shape.Height * scale);
                        var at = ((int)Math.Round((dw - w) / 2.0), (int)Math.Round(shape.Y * scale), w, h);
                        var frame = Frame(shape, dark, scale, at, hover ? (.2, .45) : null, 0);
                        // liquid-glass-web as it is (the proven port, its own uv field, no band): the reference column.
                        if (look == "lgw") frame = frame with { Faithful = true, BandLow = 0, BandHigh = 1 };
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
                        string name = $"{bg}-{shape.Name}-{look}-{scale * 100:0}{(hover ? "-hover" : "")}";
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
