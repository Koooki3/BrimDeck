using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.LiquidGlass;

// The frost's temporal stability, offscreen: a busy page (text of several sizes, thin lines, coloured blocks, a gradient) is
// moved under the island in steps of 1 px and of 1/4 px, and the glass drawn over each step (the legibility band off, so the
// frosted interior is the frost through the fixed filters and tint). Measured in the frosted interior (the open panel at
// Open 1 and .1, the collapsed capsule's content row), luma in 8-bit steps:
//   R1  the 1 px steps, motion compensated: out(t+1, x) against out(t, x - 1). A shift-invariant frost gives 0; a pyramid whose
//       grid the page moves across gives its aliasing (the shimmer).
//   O/S the uncompensated change of the glass over that of the backdrop itself (how much of the motion shows through).
//   D2  the 1/4 px steps: the second difference in time, out(t+1) - 2 out(t) + out(t-1); smooth motion gives about 0.
//   Detail  the interior's RMS about its mean on the still page (how strong the frost is, to compare like with like).
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --frost-check <output folder>
internal static class FrostCheck
{
    private const double Scale = 1.5, PageW = 760, PageH = 420;

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        using var device = LiquidPreview.CreateDevice();
        using var renderer = new LiquidRenderer(device);
        var lines = new List<string>();
        var cases = new (string Name, double W, double H, double Top, double Bottom, double Y, float Open)[]
        {
            ("panel open 1", 470, 250, 0, 26, 0, 1),
            ("panel open .1", 470, 250, 0, 26, 0, .1f),
            ("capsule row", 228, 40, 20, 20, 10, 0),
        };
        foreach (bool grain in new[] { false, true })
        foreach (var c in cases)
        {
            renderer.GrainOn = grain;
            int w = (int)Math.Round(c.W * Scale), h = (int)Math.Round(c.H * Scale);
            var at = ((int)Math.Round((PageW * Scale - w) / 2), (int)Math.Round(c.Y * Scale), w, h);
            var frame = LiquidFrame.Preset(false, w, h, (float)(c.Top * Scale), (float)(c.Bottom * Scale), 0, (float)Scale)
                with { Open = c.Open, BandLow = 0, BandHigh = 1 };
            (int X0, int Y0, int X1, int Y1) mask;
            if (c.Open > 0) mask = ((int)(24 * Scale), (int)(24 * Scale), w - (int)(24 * Scale), h - (int)(24 * Scale));
            else
            {
                frame = frame with { ContentLeft = h * .5f, ContentTop = 0, ContentRight = w - h * .5f, ContentBottom = h };
                mask = (h, (int)(h * .4), w - h, (int)Math.Ceiling(h * .6));
            }
            int pad = (int)Math.Ceiling(48 * Scale);
            double r1 = 0, o = 0, s = 0, d2 = 0, detail = 0; int n1 = 0, nd = 0;
            // The stats' steadiness over the 1 px steps (unsmoothed): per tile over the island, the change of each channel
            // from step to step over its mean.
            var statsChange = new double[3]; var statsSum = new double[3]; float[]? lastStats = null; int statsCount = 0;
            float[]? prev = null, prevBg = null, prev2 = null;
            // 1 px steps.
            for (int t = 0; t <= 10; t++)
            {
                var page = Page(t, out int dw, out int dh);
                var glass = Luma(LiquidPreview.Render(device, renderer, page, dw, dh, at, frame, pad), w, h);
                var bg = LumaAt(page, dw, at.Item1, at.Item2, w, h);
                if (t == 0)
                {
                    double sum = 0, sq = 0; int k = 0;
                    for (int y = mask.Y0; y < mask.Y1; y++) for (int x = mask.X0; x < mask.X1; x++) { sum += glass[y * w + x]; k++; }
                    double mean = sum / k;
                    for (int y = mask.Y0; y < mask.Y1; y++) for (int x = mask.X0; x < mask.X1; x++) sq += (glass[y * w + x] - mean) * (glass[y * w + x] - mean);
                    detail = Math.Sqrt(sq / k);
                    if (c.Open == 1) SaveGray(glass, w, h, Path.Combine(folder, grain ? "frost-panel.png" : "frost-panel-nograin.png"));
                }
                if (prev is not null)
                    for (int y = mask.Y0; y < mask.Y1; y++)
                        for (int x = mask.X0 + 1; x < mask.X1; x++)
                        {
                            double e = glass[y * w + x] - prev[y * w + x - 1];
                            r1 += e * e; n1++;
                            double u = glass[y * w + x] - prev[y * w + x], v = bg[y * w + x] - prevBg![y * w + x];
                            o += u * u; s += v * v;
                        }
                prev = glass; prevBg = bg;
                var (sd, cols, rows, ox, oy, tile) = renderer.ReadStats();
                if (lastStats is not null)
                    for (int ty = 0; ty < rows; ty++)
                        for (int tx = 0; tx < cols; tx++)
                        {
                            double cx = ox + (tx + .5) * tile, cy = oy + (ty + .5) * tile;
                            if (cx < mask.X0 || cx > mask.X1 || cy < mask.Y0 || cy > mask.Y1) continue;
                            int i = (ty * cols + tx) * 4;
                            for (int ch = 0; ch < 3; ch++) { statsChange[ch] += Math.Abs(sd[i + ch] - lastStats[i + ch]); statsSum[ch] += sd[i + ch]; }
                            statsCount++;
                        }
                lastStats = sd;
            }
            // 1/4 px steps.
            prev = null;
            for (int t = 0; t <= 12; t++)
            {
                var page = Page(t * .25, out int dw, out int dh);
                var glass = Luma(LiquidPreview.Render(device, renderer, page, dw, dh, at, frame, pad), w, h);
                if (prev is not null && prev2 is not null)
                    for (int y = mask.Y0; y < mask.Y1; y++)
                        for (int x = mask.X0; x < mask.X1; x++)
                        {
                            double e = glass[y * w + x] - 2 * prev[y * w + x] + prev2[y * w + x];
                            d2 += e * e; nd++;
                        }
                prev2 = prev; prev = glass;
            }
            string line = (grain ? "grain " : "plain ") + string.Format(CultureInfo.InvariantCulture, "{0,-14} R1 {1:0.000}  O/S {2:0.0000}  D2 {3:0.000}  detail {4:0.00}",
                c.Name, Math.Sqrt(r1 / n1), Math.Sqrt(o / Math.Max(s, 1e-9)), Math.Sqrt(d2 / nd), detail)
                + string.Format(CultureInfo.InvariantCulture, "  stats change/step: mean {0:0.0000} sigma {1:0.000} raw {2:0.000} (over {3} tiles; mean sigma {4:0.0000}, raw {5:0.0000})",
                    statsChange[0] / Math.Max(statsSum[0], 1e-9), statsChange[1] / Math.Max(statsSum[1], 1e-9), statsChange[2] / Math.Max(statsSum[2], 1e-9), statsCount / 10, statsSum[1] / Math.Max(1, statsCount), statsSum[2] / Math.Max(1, statsCount));
            Console.WriteLine(line);
            lines.Add(line);
        }
        // The opening: the panel's frost radius swept from 0 to its full size in 64 even steps over the still page (no grain).
        // Steps: the RMS change of the frosted interior from one step to the next; a level boundary would show as a step much
        // larger than its neighbours (largest over median, and largest over its neighbours' mean).
        {
            renderer.GrainOn = false;
            int w = (int)Math.Round(470 * Scale), h = (int)Math.Round(250 * Scale), pad = (int)Math.Ceiling(48 * Scale);
            var at = ((int)Math.Round((PageW * Scale - w) / 2), 0, w, h);
            var page = Page(0, out int dw, out int dh);
            var steps = new List<double>();
            float[]? last = null;
            for (int i = 0; i <= 64; i++)
            {
                float r = i / 64f;
                var frame = LiquidFrame.Preset(false, w, h, 0, (float)(26 * Scale), 0, (float)Scale) with { Open = r * r, BandLow = 0, BandHigh = 1 };
                var glass = Luma(LiquidPreview.Render(device, renderer, page, dw, dh, at, frame, pad), w, h);
                if (last is not null)
                {
                    double sq = 0; int k = 0;
                    for (int y = (int)(24 * Scale); y < h - (int)(24 * Scale); y++)
                        for (int x = (int)(24 * Scale); x < w - (int)(24 * Scale); x++) { double e = glass[y * w + x] - last[y * w + x]; sq += e * e; k++; }
                    steps.Add(Math.Sqrt(sq / k));
                }
                last = glass;
            }
            var sorted = steps.Order().ToList();
            double bump = 0;
            for (int i = 1; i + 1 < steps.Count; i++) bump = Math.Max(bump, steps[i] / Math.Max(1e-6, (steps[i - 1] + steps[i + 1]) / 2));
            string line = string.Format(CultureInfo.InvariantCulture, "opening sweep  steps: median {0:0.000}, largest {1:0.000} (step {2}), largest over its neighbours {3:0.00}; all: {4}",
                sorted[sorted.Count / 2], sorted[^1], steps.IndexOf(sorted[^1]) + 1, bump, string.Join(" ", steps.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture))));
            Console.WriteLine(line);
            lines.Add(line);
            renderer.GrainOn = true;
        }
        File.WriteAllLines(Path.Combine(folder, "frost-check.txt"), lines);
        return 0;
    }

    // Each level's Gaussian deviation (LiquidRenderer.LevelSigma), from a step edge through it: half the distance between where
    // its profile crosses 15.87 % and 84.13 %, across a vertical edge and a horizontal one.
    //   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --frost-calibrate
    public static int Calibrate()
    {
        using var device = LiquidPreview.CreateDevice();
        using var renderer = new LiquidRenderer(device);
        const int size = 1536;
        foreach (bool vertical in new[] { true, false })
        {
            var pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    byte v = (vertical ? x : y) < size / 2 ? (byte)0 : (byte)255;
                    int i = (y * size + x) * 4;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = v; pixels[i + 3] = 255;
                }
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            using var background = device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, size, size, 1, 1),
                [new Vortice.Direct3D11.SubresourceData(handle.AddrOfPinnedObject(), size * 4)]);
            handle.Free();
            using var target = device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, 64, 64, 1, 1, Vortice.Direct3D11.BindFlags.RenderTarget));
            using var view = device.CreateRenderTargetView(target);
            renderer.Draw(background, 0, LiquidFrame.Preset(false, 64, 64, 0, 10, 0, 4) with { Open = 1 }, view);
            var line = new List<string>();
            for (int k = 1; k <= renderer.Levels; k++)
            {
                var (data, w, h, texel) = renderer.ReadLevel(k);
                int n = vertical ? w : h;
                var profile = new double[n];
                for (int i = 0; i < n; i++) profile[i] = vertical ? data[((h / 2) * w + i) * 4 + 1] : data[(i * w + w / 2) * 4 + 1];
                double Cross(double level)
                {
                    for (int i = 1; i < n; i++)
                        if (profile[i - 1] < level && profile[i] >= level) return (i - 1 + (level - profile[i - 1]) / (profile[i] - profile[i - 1]) + .5) * texel;
                    return double.NaN;
                }
                line.Add(((Cross(.8413) - Cross(.1587)) / 2).ToString("0.00", CultureInfo.InvariantCulture));
            }
            Console.WriteLine((vertical ? "vertical edge:   " : "horizontal edge: ") + string.Join(", ", line));
        }
        Console.WriteLine("table: " + string.Join(", ", LiquidRenderer.LevelSigma.Select(s => s.ToString(CultureInfo.InvariantCulture))));
        return 0;
    }

    // The backdrop's stats (Liquid.hlsl, Stats; Frost.hlsl) over each background <name>-150.png in the folder (1200 x 300 DIP at
    // 150 %), for the open panel and the collapsed capsule (with its content row): <name>-<shape>-stats.png, the background
    // under the island and, at tile resolution, the mean luma (gray), the frosted band RMS and the unfrosted band RMS (white at
    // SigmaWhite, linear Y); and <name>-<shape>-stats.csv, the tiles over the island (island pixels of their centres).
    //   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --frost-stats <backgrounds> <output folder>
    public static int Stats(string backgrounds, string folder)
    {
        const double SigmaWhite = .1;
        Directory.CreateDirectory(folder);
        using var device = LiquidPreview.CreateDevice();
        using var renderer = new LiquidRenderer(device);
        foreach (var file in Directory.GetFiles(backgrounds, "*-150.png").Order())
        {
            var decoded = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(Path.GetFullPath(file)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            int dw = decoded.PixelWidth, dh = decoded.PixelHeight;
            var desktop = new byte[dw * dh * 4];
            decoded.CopyPixels(desktop, dw * 4, 0);
            string name = Path.GetFileNameWithoutExtension(file)[..^4];
            foreach (var (shape, sw, sh, top, bottom, y, open) in new[] { ("panel", 470.0, 250.0, 0.0, 26.0, 0.0, 1f), ("capsule", 228.0, 40.0, 20.0, 20.0, 10.0, 0f) })
            {
                int w = (int)Math.Round(sw * Scale), h = (int)Math.Round(sh * Scale);
                var at = ((int)Math.Round((dw - w) / 2.0), (int)Math.Round(y * Scale), w, h);
                var frame = LiquidFrame.Preset(false, w, h, (float)(top * Scale), (float)(bottom * Scale), 0, (float)Scale) with { Open = open };
                if (open == 0) frame = frame with { ContentLeft = h * .5f, ContentTop = 0, ContentRight = w - h * .5f, ContentBottom = h };
                LiquidPreview.Render(device, renderer, desktop, dw, dh, at, frame, (int)Math.Ceiling(48 * Scale));
                var (data, cols, rows, ox, oy, tile) = renderer.ReadStats();
                // Four panels down: the backdrop under the island, the mean, the frosted band, the unfrosted band.
                var sheet = new byte[w * h * 4 * 4];
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int s = (((at.Item2 + j) * dw) + at.Item1 + i) * 4;
                        Buffer.BlockCopy(desktop, s, sheet, (j * w + i) * 4, 4);
                        int tx = (int)Math.Floor((i - ox) / tile), ty = (int)Math.Floor((j - oy) / tile);
                        int t = (Math.Clamp(ty, 0, rows - 1) * cols + Math.Clamp(tx, 0, cols - 1)) * 4;
                        byte Gray(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
                        var values = new[] { Gray(data[t]), Gray(data[t + 1] / SigmaWhite), Gray(data[t + 2] / SigmaWhite) };
                        for (int p = 0; p < 3; p++)
                        {
                            int o = (((p + 1) * h + j) * w + i) * 4;
                            sheet[o] = sheet[o + 1] = sheet[o + 2] = values[p]; sheet[o + 3] = 255;
                        }
                    }
                var bitmap = BitmapSource.Create(w, h * 4, 96, 96, PixelFormats.Bgra32, null, sheet, w * 4);
                using (var png = File.Create(Path.Combine(folder, $"{name}-{shape}-stats.png")))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(png);
                }
                var csv = new List<string> { "x,y,mean,sigma_frosted,sigma_raw" };
                double most = 0, mostRaw = 0;
                for (int ty = 0; ty < rows; ty++)
                    for (int tx = 0; tx < cols; tx++)
                    {
                        double cx = ox + (tx + .5) * tile, cy = oy + (ty + .5) * tile;
                        if (cx < 0 || cx > w || cy < 0 || cy > h) continue;
                        int t = (ty * cols + tx) * 4;
                        csv.Add(string.Create(CultureInfo.InvariantCulture, $"{cx:0.#},{cy:0.#},{data[t]:0.0000},{data[t + 1]:0.00000},{data[t + 2]:0.00000}"));
                        most = Math.Max(most, data[t + 1]); mostRaw = Math.Max(mostRaw, data[t + 2]);
                    }
                File.WriteAllLines(Path.Combine(folder, $"{name}-{shape}-stats.csv"), csv);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-12} {shape,-8} tiles {csv.Count - 1}, tile {tile} px; largest sigma frosted {most:0.0000}, unfrosted {mostRaw:0.0000}"));
            }
        }
        return 0;
    }

    private static float[] Luma(byte[] bgra, int w, int h)
    {
        var l = new float[w * h];
        for (int i = 0; i < w * h; i++) l[i] = (float)(.0722 * bgra[i * 4] + .7152 * bgra[i * 4 + 1] + .2126 * bgra[i * 4 + 2]);
        return l;
    }

    private static float[] LumaAt(byte[] page, int dw, int x0, int y0, int w, int h)
    {
        var l = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = ((y0 + y) * dw + x0 + x) * 4;
                l[y * w + x] = (float)(.0722 * page[i] + .7152 * page[i + 1] + .2126 * page[i + 2]);
            }
        return l;
    }

    private static void SaveGray(float[] l, int w, int h, string path)
    {
        var bytes = new byte[w * h];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Math.Clamp(Math.Round(l[i]), 0, 255);
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, bytes, w);
        using var file = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }

    // The busy page, moved right by shift device pixels.
    private static byte[] Page(double shift, out int width, out int height)
    {
        width = (int)Math.Round(PageW * Scale); height = (int)Math.Round(PageH * Scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageW, PageH));
            dc.PushTransform(new TranslateTransform(shift / Scale, 0));
            var sky = new LinearGradientBrush(Color.FromRgb(0x20, 0x40, 0x90), Color.FromRgb(0xF0, 0xA0, 0x40), 0);
            dc.DrawRectangle(sky, null, new Rect(-40, 0, 300, PageH));
            var rng = new Random(7);
            for (int i = 0; i < 40; i++)
            {
                var color = Color.FromRgb((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(rng.Next(-40, 760), rng.Next(0, 420), rng.Next(6, 60), rng.Next(4, 40)));
            }
            var pen = new Pen(Brushes.Black, .7);
            for (int i = 0; i < 30; i++) dc.DrawLine(pen, new Point(rng.Next(-40, 760), rng.Next(0, 420)), new Point(rng.Next(-40, 760), rng.Next(0, 420)));
            var face = new Typeface("Segoe UI");
            for (int i = 0; i < 26; i++)
            {
                double size = 9 + (i % 5) * 3;
                dc.DrawText(new FormattedText("The quick brown fox jumps over the lazy dog 0123456789 BrimDeck liquid glass", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, size, i % 3 == 0 ? Brushes.DarkBlue : Brushes.Black, Scale), new Point(-30 + (i % 4) * 9, 4 + i * 16));
            }
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
