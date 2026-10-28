using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// A self-check of the liquid motion's boundary, run by the tests (tests/BrimDeck.WindowsTests) and by the live measurement hook: the
// notice's surface is drawn off screen as the window draws it at each moment of the motion (the body with the necks and droplets
// that are joined to it in one outline, NoticeFxShape), without its text, and its inside is searched for lines. Joined parts share
// the surface's one boundary, so inside it there must be none: a line there is another outline drawn through it. Droplets that have
// parted have boundaries of their own and are not part of the surface, so they are not looked at.
internal static class NoticeFxCheck
{
    internal readonly record struct Result(int Frames, int Failed, int Worst, double WorstTime, bool Skipped)
    {
        public override string ToString() => Skipped ? "skipped (the theme has a pattern over its surface)" : $"frames={Frames} failed={Failed} worst={Worst} px at {WorstTime:0.00} s";
    }

    // The stray pixels of a joint grow with the picture, which is panelScale times larger.
    private const int Tolerance = 24;

    // The theme in use, one form of the motion (capsule or notch), every 40 ms of it. scale: how finely the picture is rendered; panelScale:
    // the scale the notice is drawn at (PanelScales), the motion being worked out at the designed size and drawn at it.
    internal static Result Sweep(LiquidForm form, double scale = 2, double panelScale = 1)
    {
        var plan = NoticeFxPlan.Standard;
        var look = NoticeLook.Current(1);
        if (look.Pattern is not null) return new Result(0, 0, 0, 0, true);
        double dw = 376, dh = form == LiquidForm.Notch ? 52 : 44;
        double w = dw * panelScale, h = dh * panelScale;
        double shoulder = (form == LiquidForm.Notch ? 16 : 0) * panelScale, bottom = form == LiquidForm.Notch ? 20 * panelScale : h / 2, top = form == LiquidForm.Notch ? 0 : h / 2;
        var fx = NoticeFxLook.Create(look, plan, panelScale);
        var liquid = new NoticeLiquid(new LiquidSpec(form, dw, dh, NoticeLiquid.DesignSeed, Intensity: plan.Intensity, Separation: plan.Separation,
            Shoulder: shoulder / panelScale, CornerRadius: bottom / panelScale, MaxInset: NoticeLiquid.MaxInset(dh, 16, dh / 2 - 6)));
        var frame = new LiquidFrame();
        double side = (form == LiquidForm.Notch ? 24 : 60) * panelScale, vMargin = 28 * panelScale, cw = w + 2 * side, ch = h + 2 * vMargin;
        // The backdrop is one flat color, so that nothing but the surface itself can put a line in the picture.
        var canvas = new Canvas { Width = cw, Height = ch, Background = new SolidColorBrush(Color.FromRgb(0x58, 0x62, 0x74)) };
        var surface = new NoticeSurface { Look = look, TopRadius = top, BottomRadius = bottom, Shoulder = shoulder, Width = w, Height = h, MarksHidden = true };
        Canvas.SetLeft(surface, side); Canvas.SetTop(surface, form == LiquidForm.Notch ? 0 : vMargin);
        canvas.Children.Add(surface);
        int failed = 0, worst = 0, frames = 0; double worstTime = 0;
        // What the surface's own rim and edge take at the outline is not inside: the rim, the step of a pixel theme and a little more.
        int erode = (int)Math.Ceiling(scale * (Math.Max(look.RimWidth * 2, fx.Step * panelScale) + 2.5)) + 2;
        for (double t = .04; t < liquid.Length; t += .04)
        {
            liquid.Sample(t, frame, dw, dh);
            surface.InsetLeft = frame.InsetLeft * panelScale; surface.InsetRight = frame.InsetRight * panelScale;
            surface.Attachments = NoticeFxShape.Build(frame, form, dw, dh, fx.Step, panelScale);
            canvas.Measure(new Size(cw, ch)); canvas.Arrange(new Rect(0, 0, cw, ch)); canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(cw * scale), (int)(ch * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            int bad = Lines(bitmap, Color.FromRgb(0x58, 0x62, 0x74), erode);
            frames++;
            // A stray pixel or two where a tiny droplet joins is not a line; an outline through the inside is hundreds.
            if (bad >= Tolerance * panelScale) failed++;
            if (bad > worst) { worst = bad; worstTime = t; }
        }
        return new Result(frames, failed, worst, worstTime, false);
    }

    // How many pixels deep inside the picture's shape (what is not the backdrop, eroded by `erode` pixels) lie on a line: where the
    // brightness changes by more than 32 (of 255) from one pixel to the next. A gradient or a flat fill does not; an outline does.
    internal static int Lines(BitmapSource image, Color backdrop, int erode)
    {
        var source = image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight;
        var pixels = new byte[w * h * 4];
        source.CopyPixels(pixels, w * 4, 0);
        var luminance = new int[w * h]; var shape = new int[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                luminance[y * w + x] = (pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8;
                bool inside = Math.Abs(pixels[i + 2] - backdrop.R) + Math.Abs(pixels[i + 1] - backdrop.G) + Math.Abs(pixels[i] - backdrop.B) > 12;
                shape[(y + 1) * (w + 1) + x + 1] = shape[y * (w + 1) + x + 1] + shape[(y + 1) * (w + 1) + x] - shape[y * (w + 1) + x] + (inside ? 1 : 0);
            }
        int side = 2 * erode + 1, bad = 0;
        for (int y = erode; y < h - erode - 1; y++)
            for (int x = erode; x < w - erode - 1; x++)
            {
                int total = shape[(y + erode + 1) * (w + 1) + x + erode + 1] - shape[(y - erode) * (w + 1) + x + erode + 1] - shape[(y + erode + 1) * (w + 1) + x - erode] + shape[(y - erode) * (w + 1) + x - erode];
                if (total != side * side) continue;
                int l = luminance[y * w + x];
                if (Math.Abs(luminance[y * w + x + 1] - l) > 32 || Math.Abs(luminance[(y + 1) * w + x] - l) > 32) bad++;
            }
        return bad;
    }
}
