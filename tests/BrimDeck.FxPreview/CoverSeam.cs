using System;

// Round 15: the cover seam. The light look's glass over an open panel, rendered twice: with its text cover (T) and without
// (N, the same frame with no text regions). The cover is D = T - N. Where it turns on (the band where D is between 15 % and
// 85 % of its strong value), the low-frequency gradient it adds, |grad D|, is set against the glass's own natural variation,
// the mean |grad N| over the panel, both in CIELAB L* and C* and both at the feather's scale (boxes of 3 DIPs, central
// differences across 6). A seam no stronger than the glass's own variation scores at most 1; over a backdrop with nothing in
// it (a plain colour) that ratio means little, so the peak |grad D| is given too, in L* per DIP. The cover's strength is
// |D| (a cover may darken as well as lighten). Next to it, the covered zone (|D| above 70 %) against the uncovered glass
// within a 24 DIP box of it: their mean colours' Delta E 2000 and chroma ratio, for T and for N (N's are the backdrop's own
// difference there; the cover's share is T's less N's). A note, not a check.
internal static class CoverSeam
{
    internal static void Measure(string name, byte[] covered, byte[] free, byte[] alpha, int stride, int gx, int gy, int w, int h, double scale)
    {
        int inset = (int)Math.Round(20 * scale), r = (int)Math.Max(1, Math.Round(1.5 * scale)), s = (int)Math.Max(1, Math.Round(3 * scale));
        var tl = new double[w * h]; var tc = new double[w * h]; var nl = new double[w * h]; var nc = new double[w * h];
        var ta = new double[w * h]; var tb = new double[w * h]; var na = new double[w * h]; var nb = new double[w * h];
        var inside = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, d = ((gy + y) * stride + gx + x) * 4;
                inside[i] = alpha[i * 4 + 3] >= 250 && x >= inset && y >= inset && x < w - inset && y < h - inset;
                (tl[i], ta[i], tb[i]) = Lab(covered, d); tc[i] = Math.Sqrt(ta[i] * ta[i] + tb[i] * tb[i]);
                (nl[i], na[i], nb[i]) = Lab(free, d); nc[i] = Math.Sqrt(na[i] * na[i] + nb[i] * nb[i]);
            }
        var dl = new double[w * h]; var dc = new double[w * h];
        for (int i = 0; i < w * h; i++) { dl[i] = tl[i] - nl[i]; dc[i] = tc[i] - nc[i]; }
        var sdl = Smooth(dl, w, h, r); var sdc = Smooth(dc, w, h, r); var snl = Smooth(nl, w, h, r); var snc = Smooth(nc, w, h, r);
        // The cover's strength: |D| in L* smoothed wider (4 DIPs), against its 95th percentile inside.
        var magnitude = new double[w * h];
        for (int i = 0; i < w * h; i++) magnitude[i] = Math.Abs(dl[i]);
        var wide = Smooth(magnitude, w, h, (int)Math.Round(4 * scale));
        var values = new System.Collections.Generic.List<double>();
        for (int i = 0; i < w * h; i++) if (inside[i]) values.Add(wide[i]);
        if (values.Count < 100) return;
        values.Sort();
        double strong = values[(int)(values.Count * .95)];
        if (strong < 1)
        {
            Console.WriteLine(FormattableString.Invariant($"NOTE: {name}: cover seam: the cover adds under 1 L* here ({strong:0.00})"));
            return;
        }
        double Grad(double[] f, int x, int y)
        {
            double gxv = (f[y * w + Math.Min(w - 1, x + s)] - f[y * w + Math.Max(0, x - s)]) / (2.0 * s / scale);
            double gyv = (f[Math.Min(h - 1, y + s) * w + x] - f[Math.Max(0, y - s) * w + x]) / (2.0 * s / scale);
            return Math.Sqrt(gxv * gxv + gyv * gyv);
        }
        double bandL = 0, bandC = 0, natL = 0, natC = 0, peak = 0; int band = 0, all = 0;
        var zone = new sbyte[w * h];   // 1 covered, -1 uncovered
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!inside[i]) continue;
                double k = wide[i] / strong;
                natL += Grad(snl, x, y); natC += Grad(snc, x, y); all++;
                if (k > .15 && k < .85) { double g = Grad(sdl, x, y); bandL += g; bandC += Grad(sdc, x, y); band++; peak = Math.Max(peak, g); }
                zone[i] = (sbyte)(k > .7 ? 1 : k < .15 ? -1 : 0);
            }
        // The uncovered glass next to the covered: within 24 DIPs of it.
        int reach = (int)Math.Round(24 * scale);
        var cov = new int[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) cov[(y + 1) * (w + 1) + x + 1] = (zone[y * w + x] == 1 ? 1 : 0) + cov[y * (w + 1) + x + 1] + cov[(y + 1) * (w + 1) + x] - cov[y * (w + 1) + x];
        double[] sumT = new double[6], sumN = new double[6]; int nCov = 0, nUnc = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (zone[i] == 0 || !inside[i]) continue;
                if (zone[i] == -1)
                {
                    int x0 = Math.Max(0, x - reach), x1 = Math.Min(w, x + reach + 1), y0 = Math.Max(0, y - reach), y1 = Math.Min(h, y + reach + 1);
                    if (cov[y1 * (w + 1) + x1] - cov[y0 * (w + 1) + x1] - cov[y1 * (w + 1) + x0] + cov[y0 * (w + 1) + x0] == 0) continue;
                }
                int o = zone[i] == 1 ? 0 : 3;
                sumT[o] += tl[i]; sumT[o + 1] += ta[i]; sumT[o + 2] += tb[i];
                sumN[o] += nl[i]; sumN[o + 1] += na[i]; sumN[o + 2] += nb[i];
                if (o == 0) nCov++; else nUnc++;
            }
        string zones = "no uncovered glass next to it";
        if (nCov > 50 && nUnc > 50)
        {
            (double, double, double) M(double[] v, int o, int n) => (v[o] / n, v[o + 1] / n, v[o + 2] / n);
            var tCov = M(sumT, 0, nCov); var tUnc = M(sumT, 3, nUnc); var nCovM = M(sumN, 0, nCov); var nUncM = M(sumN, 3, nUnc);
            double C((double L, double A, double B) v) => Math.Sqrt(v.A * v.A + v.B * v.B);
            zones = FormattableString.Invariant($"covered vs next to it: dE00 {DeltaE00(tCov, tUnc):0.0} (without cover {DeltaE00(nCovM, nUncM):0.0}), chroma ratio {C(tCov) / Math.Max(C(tUnc), .5):0.00} (without {C(nCovM) / Math.Max(C(nUncM), .5):0.00}), L* {tCov.Item1 - tUnc.Item1:+0.0;-0.0} (without {nCovM.Item1 - nUncM.Item1:+0.0;-0.0})");
        }
        double seamL = band == 0 ? 0 : bandL / band / Math.Max(natL / all, 1e-3), seamC = band == 0 ? 0 : bandC / band / Math.Max(natC / all, 1e-3);
        Console.WriteLine(FormattableString.Invariant($"NOTE: {name}: cover seam: L* {seamL:0.00}, C* {seamC:0.00} of the glass's own variation, peak {peak:0.00} L*/DIP (cover up to {strong:0.0} L*; natural {natL / all:0.00} L*/DIP, {natC / all:0.00} C*/DIP); {zones}"));
    }

    private static double[] Smooth(double[] f, int w, int h, int r)
    {
        var integral = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) integral[(y + 1) * (w + 1) + x + 1] = f[y * w + x] + integral[y * (w + 1) + x + 1] + integral[(y + 1) * (w + 1) + x] - integral[y * (w + 1) + x];
        var o = new double[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1), y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
                o[y * w + x] = (integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1] - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0]) / ((x1 - x0) * (y1 - y0));
            }
        return o;
    }

    // sRGB (BGRA bytes) to CIELAB, D65.
    internal static (double L, double A, double B) Lab(byte[] p, int at)
    {
        static double Lin(int v) { double c = v / 255.0; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        double r = Lin(p[at + 2]), g = Lin(p[at + 1]), b = Lin(p[at]);
        double X = (.4124 * r + .3576 * g + .1805 * b) / .95047, Y = .2126 * r + .7152 * g + .0722 * b, Z = (.0193 * r + .1192 * g + .9505 * b) / 1.08883;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        double fx = F(X), fy = F(Y), fz = F(Z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    // CIEDE2000 (Sharma, Wu, Dalal 2005).
    internal static double DeltaE00((double L, double A, double B) c1, (double L, double A, double B) c2)
    {
        double C1 = Math.Sqrt(c1.A * c1.A + c1.B * c1.B), C2 = Math.Sqrt(c2.A * c2.A + c2.B * c2.B), Cm = (C1 + C2) / 2;
        double G = .5 * (1 - Math.Sqrt(Math.Pow(Cm, 7) / (Math.Pow(Cm, 7) + Math.Pow(25, 7))));
        double a1 = (1 + G) * c1.A, a2 = (1 + G) * c2.A;
        double C1p = Math.Sqrt(a1 * a1 + c1.B * c1.B), C2p = Math.Sqrt(a2 * a2 + c2.B * c2.B);
        double H(double b, double a) { double v = Math.Atan2(b, a) * 180 / Math.PI; return v < 0 ? v + 360 : v; }
        double h1 = C1p == 0 ? 0 : H(c1.B, a1), h2 = C2p == 0 ? 0 : H(c2.B, a2);
        double dL = c2.L - c1.L, dC = C2p - C1p, dh = 0;
        if (C1p * C2p != 0) { dh = h2 - h1; if (dh > 180) dh -= 360; else if (dh < -180) dh += 360; }
        double dH = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(dh / 2 * Math.PI / 180);
        double Lm = (c1.L + c2.L) / 2, Cpm = (C1p + C2p) / 2, hm = h1 + h2;
        if (C1p * C2p != 0) { hm = Math.Abs(h1 - h2) <= 180 ? (h1 + h2) / 2 : (h1 + h2 < 360 ? (h1 + h2 + 360) / 2 : (h1 + h2 - 360) / 2); }
        double T = 1 - .17 * Math.Cos((hm - 30) * Math.PI / 180) + .24 * Math.Cos(2 * hm * Math.PI / 180) + .32 * Math.Cos((3 * hm + 6) * Math.PI / 180) - .2 * Math.Cos((4 * hm - 63) * Math.PI / 180);
        double dTheta = 30 * Math.Exp(-Math.Pow((hm - 275) / 25, 2));
        double Rc = 2 * Math.Sqrt(Math.Pow(Cpm, 7) / (Math.Pow(Cpm, 7) + Math.Pow(25, 7)));
        double Sl = 1 + .015 * Math.Pow(Lm - 50, 2) / Math.Sqrt(20 + Math.Pow(Lm - 50, 2)), Sc = 1 + .045 * Cpm, Sh = 1 + .015 * Cpm * T;
        double Rt = -Math.Sin(2 * dTheta * Math.PI / 180) * Rc;
        return Math.Sqrt(Math.Pow(dL / Sl, 2) + Math.Pow(dC / Sc, 2) + Math.Pow(dH / Sh, 2) + Rt * (dC / Sc) * (dH / Sh));
    }
}
