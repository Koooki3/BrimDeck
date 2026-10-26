using BrimDeck;

// Round 16: the acrylic body offscreen. DWM draws the app's (the host backdrop: its blur of what lies behind the window), which
// no offscreen render can capture, so the page sheets draw its stand-in: the desktop behind the island blurred as Windows'
// acrylic blurs it (a Gaussian of AcrylicBlurDip, three box passes), then GlassEffect.AcrylicMatrix exactly as the app's
// effect applies it, inside the island's outline (the coverage of the glass the renderer drew), and the rim as GlassBackdrop
// draws it (a hairline just inside the outline and a light along the top fading by the middle). Premultiplied BGRA, the
// island's size, like LiquidPreview.Render.
internal static class AcrylicEmulation
{
    // Windows' acrylic blurs with a Gaussian of 30 (AcrylicBrush's blur amount): taken here as 30 DIPs of deviation.
    internal const double AcrylicBlurDip = 30;
    // The luminosity opacity of the last drawing (for the page notes).
    internal static double Luminosity { get; private set; }

    internal static byte[] Draw(byte[] desktop, int dw, int dh, int gx, int gy, int w, int h, byte[] coverage, bool dark, double scale, double openness)
    {
        // The luminosity layer as the app's probe sets it (AcrylicProbe): from the ring left, right and below the island, 24 to
        // 64 DIPs outside it.
        int gap = (int)Math.Round(24 * scale), band = (int)Math.Round(40 * scale);
        var ring = new List<double>();
        void Ring(int l, int t, int r, int b)
        {
            for (int y = Math.Max(0, t); y < Math.Min(dh, b); y += 2)
                for (int x = Math.Max(0, l); x < Math.Min(dw, r); x += 2)
                {
                    int o = (y * dw + x) * 4;
                    ring.Add((.2126 * desktop[o + 2] + .7152 * desktop[o + 1] + .0722 * desktop[o]) / 255);
                }
        }
        Ring(gx - gap - band, gy, gx - gap, gy + h + gap);
        Ring(gx + w + gap, gy, gx + w + gap + band, gy + h + gap);
        Ring(gx - gap - band, gy + h + gap, gx + w + gap + band, gy + h + gap + band);
        ring.Sort();
        double bright = ring.Count > 0 ? ring[(int)(ring.Count * .9)] : .5, darkest = ring.Count > 0 ? ring[(int)(ring.Count * .1)] : .5;
        Luminosity = GlassEffect.Shown(GlassEffect.LuminosityFor(dark, bright, darkest), openness);
        var look = (dark ? GlassEffect.AcrylicDark : GlassEffect.AcrylicLight) with { LuminosityOpacity = Luminosity };
        var m = GlassEffect.AcrylicMatrix(look);
        // The blurred desktop over the island and a margin as wide as the blur reaches.
        int reach = (int)Math.Ceiling(AcrylicBlurDip * scale * 3);
        int x0 = Math.Max(0, gx - reach), y0 = Math.Max(0, gy - reach), x1 = Math.Min(dw, gx + w + reach), y1 = Math.Min(dh, gy + h + reach);
        int bw = x1 - x0, bh = y1 - y0;
        var channels = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            var plane = new float[bw * bh];
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++) plane[y * bw + x] = desktop[((y0 + y) * dw + x0 + x) * 4 + c] / 255f;
            // Three box passes of width sqrt(12 sigma^2 / 3 + 1): a Gaussian of that deviation, near enough.
            double sigma = AcrylicBlurDip * scale;
            int radius = (int)Math.Max(1, Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) / 2));
            var spare = new float[bw * bh];
            for (int pass = 0; pass < 3; pass++) { Box(plane, spare, bw, bh, radius, true); Box(spare, plane, bw, bh, radius, false); }
            channels[c] = plane;
        }
        // Inside distance from the outline (device pixels) for the rim: a two-pass chamfer over the coverage.
        var inside = Distance(coverage, w, h);
        float hair = (float)Math.Max(1, .75 * scale), light = (float)(1.25 * scale);
        var output = new byte[w * h * 4];
        // The noise as the app lays it over (AcrylicNoise: one tile repeated from the window's corner).
        var noise = BrimDeck.LiquidGlass.AcrylicNoise.Pixels();
        int tile = BrimDeck.LiquidGlass.AcrylicNoise.Tile;
        double noiseAlpha = noise[3] / 255.0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                double a = coverage[i * 4 + 3] / 255.0;
                if (a <= 0) continue;
                int b = (gy + y - y0) * bw + gx + x - x0;
                // BGRA planes: 0 blue, 1 green, 2 red; the matrix is by RGB.
                double r = channels[2][b], g = channels[1][b], bl = channels[0][b];
                double or = r * m[0] + g * m[4] + bl * m[8] + m[16];
                double og = r * m[1] + g * m[5] + bl * m[9] + m[17];
                double ob = r * m[2] + g * m[6] + bl * m[10] + m[18];
                double d = inside[i];
                // The hairline (the rim's own colors and strengths as GlassBackdrop.Rim sets them).
                if (d < hair) { double k = (dark ? 26 : 20) / 255.0; double v = dark ? 1 : 0; or += (v - or) * k; og += (v - og) * k; ob += (v - ob) * k; }
                else if (d < hair + light)
                {
                    double fade = Math.Clamp(1 - y / (h * .5), 0, 1), k = (dark ? 64 : 150) / 255.0 * fade;
                    or += (1 - or) * k; og += (1 - og) * k; ob += (1 - ob) * k;
                }
                int n = (((gy + y) % tile) * tile + (gx + x) % tile) * 4;
                double grain = noise[n] / 255.0;
                or = or * (1 - noiseAlpha) + grain; og = og * (1 - noiseAlpha) + grain; ob = ob * (1 - noiseAlpha) + grain;
                output[i * 4 + 2] = (byte)Math.Clamp(Math.Round(Math.Clamp(or, 0, 1) * 255 * a), 0, 255);
                output[i * 4 + 1] = (byte)Math.Clamp(Math.Round(Math.Clamp(og, 0, 1) * 255 * a), 0, 255);
                output[i * 4] = (byte)Math.Clamp(Math.Round(Math.Clamp(ob, 0, 1) * 255 * a), 0, 255);
                output[i * 4 + 3] = coverage[i * 4 + 3];
            }
        return output;
    }

    private static void Box(float[] from, float[] to, int w, int h, int r, bool horizontal)
    {
        int n = horizontal ? w : h, lines = horizontal ? h : w;
        for (int line = 0; line < lines; line++)
        {
            int At(int k) => horizontal ? line * w + Math.Clamp(k, 0, n - 1) : Math.Clamp(k, 0, n - 1) * w + line;
            float sum = 0;
            for (int k = -r; k <= r; k++) sum += from[At(k)];
            for (int k = 0; k < n; k++)
            {
                to[At(k)] = sum / (2 * r + 1);
                sum += from[At(k + r + 1)] - from[At(k - r)];
            }
        }
    }

    // Distance (device pixels, chamfer 1 / 1.414) from each covered pixel to the nearest uncovered one or the image's edge.
    private static float[] Distance(byte[] coverage, int w, int h)
    {
        var d = new float[w * h];
        for (int i = 0; i < w * h; i++) d[i] = coverage[i * 4 + 3] >= 128 ? 1e6f : 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; if (d[i] == 0) continue;
                float v = d[i];
                v = Math.Min(v, x > 0 ? d[i - 1] + 1 : 1);
                v = Math.Min(v, y > 0 ? d[i - w] + 1 : 1);
                if (x > 0 && y > 0) v = Math.Min(v, d[i - w - 1] + 1.414f);
                if (x < w - 1 && y > 0) v = Math.Min(v, d[i - w + 1] + 1.414f);
                d[i] = v;
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x; if (d[i] == 0) continue;
                float v = d[i];
                v = Math.Min(v, x < w - 1 ? d[i + 1] + 1 : 1);
                v = Math.Min(v, y < h - 1 ? d[i + w] + 1 : 1);
                if (x < w - 1 && y < h - 1) v = Math.Min(v, d[i + w + 1] + 1.414f);
                if (x > 0 && y < h - 1) v = Math.Min(v, d[i + w - 1] + 1.414f);
                d[i] = v;
            }
        return d;
    }
}
