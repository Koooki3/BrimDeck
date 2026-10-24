using BrimDeck.Core;

// The answers to the pointer: where they are drawn (a control's visible shape, not its box), how a press goes down, holds,
// completes or backs out, the stepped curves of a mechanical theme, and the ration on anything that reads as a flash.
internal static class InteractionFxTests
{
    internal static void Run(Action<string, bool> check)
    {
        static DipRect R(double x, double y, double w, double h) => new(x, y, w, h);
        static bool Near(DipRect a, DipRect b, double e = .01) => Math.Abs(a.X - b.X) < e && Math.Abs(a.Y - b.Y) < e && Math.Abs(a.Width - b.Width) < e && Math.Abs(a.Height - b.Height) < e;

        // A transparent icon button 32 x 28 around an 18 DIP glyph: the mark goes on the glyph, not on the box.
        var glyphButton = new FxNode
        {
            Kind = FxNodeKind.Border, Box = R(0, 0, 32, 28), HasBackground = true, Children =
            {
                new FxNode { Kind = FxNodeKind.Container, Box = R(4, 2, 24, 24), Children =
                {
                    new FxNode { Kind = FxNodeKind.Ink, Box = R(7, 5, 18, 18), Ink = R(8.5, 6.5, 15, 15), Paints = true },
                    // The countdown ring, hidden while the sync is not counting.
                    new FxNode { Kind = FxNodeKind.Ink, Box = R(4, 2, 24, 24), Paints = true, Visible = false }
                } }
            }
        };
        var glyph = FxShapes.Resolve(glyphButton, R(0, 0, 32, 28));
        check("A transparent wide button is marked on its glyph, with a little room", glyph.Source == FxShapeSource.Ink && Near(glyph.Bounds, R(5.5, 3.5, 21, 21)) && glyph.Radius == FxShapes.InkRadius);
        var wideText = new FxNode
        {
            Kind = FxNodeKind.Container, Box = R(0, 0, 120, 24), Children = { new FxNode { Kind = FxNodeKind.Ink, Box = R(0, 0, 120, 24), Ink = R(40, 6, 40, 12), Paints = true } }
        };
        check("A stretched text button with no face is marked on its text", Near(FxShapes.Resolve(wideText, R(0, 0, 120, 24)).Bounds, R(37, 3, 46, 18)));
        check("Ink near the edge keeps its room inside the control", Near(FxShapes.Resolve(new FxNode { Kind = FxNodeKind.Ink, Box = R(0, 0, 20, 20), Ink = R(0, 1, 20, 18), Paints = true }, R(0, 0, 20, 20)).Bounds, R(0, 0, 20, 20)));

        // A panel icon button: the hover layer its template shows is where the mark goes, with its corner.
        var iconButton = new FxNode
        {
            Kind = FxNodeKind.Container, Box = R(0, 0, 28, 24), Children =
            {
                new FxNode { Name = "HoverLayer", Kind = FxNodeKind.Border, Box = R(0, 0, 28, 24), Radius = 5, HasBackground = true, Element = "hover" },
                new FxNode { Name = "Chrome", Kind = FxNodeKind.Border, Box = R(0, 0, 28, 24), Radius = 5, HasBackground = true,
                    Children = { new FxNode { Kind = FxNodeKind.Ink, Box = R(6, 4, 16, 16), Paints = true } } }
            }
        };
        var icon = FxShapes.Resolve(iconButton, R(0, 0, 28, 24));
        check("A button with a hover layer is marked on that layer, with its corner", icon is { Source: FxShapeSource.HoverLayer, Radius: 5, Element: "hover" } && Near(icon.Bounds, R(0, 0, 28, 24)));
        // A round media button: a circle in its box.
        var round = FxShapes.Resolve(new FxNode
        {
            Kind = FxNodeKind.Container, Box = R(0, 0, 40, 32), Children = { new FxNode { Name = "Hover", Kind = FxNodeKind.Ellipse, Box = R(0, 0, 40, 32) } }
        }, R(0, 0, 40, 32));
        check("A round button is marked on its circle", round is { Source: FxShapeSource.Ellipse, Ellipse: true, Radius: 16 } && Near(round.Bounds, R(4, 0, 32, 32)));
        // A notice button: a rounded face filling the button, transparent at rest, which the button fills on hover.
        var face = FxShapes.Resolve(new FxNode
        {
            Kind = FxNodeKind.Container, Box = R(0, 0, 80, 32), Children =
            {
                new FxNode { Kind = FxNodeKind.Border, Box = R(4, 0, 76, 32), Radius = 8, HasBackground = true,
                    Children = { new FxNode { Kind = FxNodeKind.Ink, Box = R(16, 9, 52, 14), Paints = true } } }
            }
        }, R(0, 0, 80, 32));
        check("A rounded face that fills the button is its shape", face is { Source: FxShapeSource.Face, Radius: 8 } && Near(face.Bounds, R(4, 0, 76, 32)));
        check("A small painted border inside is not the face", FxShapes.Resolve(new FxNode
        {
            Kind = FxNodeKind.Container, Box = R(0, 0, 80, 32), Children =
            {
                new FxNode { Kind = FxNodeKind.Border, Box = R(30, 8, 20, 16), Radius = 3, Paints = true, HasBackground = true }
            }
        }, R(0, 0, 80, 32)) is { Source: FxShapeSource.Ink } badge && Near(badge.Bounds, R(27, 5, 26, 22)));
        check("A hidden hover layer is passed over", FxShapes.Resolve(new FxNode
        {
            Kind = FxNodeKind.Container, Box = R(0, 0, 28, 24), Children =
            {
                new FxNode { Name = "HoverLayer", Kind = FxNodeKind.Border, Box = R(0, 0, 28, 24), Radius = 5, Visible = false },
                new FxNode { Kind = FxNodeKind.Ink, Box = R(6, 4, 16, 16), Paints = true }
            }
        }, R(0, 0, 28, 24)).Source == FxShapeSource.Ink);
        check("A control that draws nothing has no shape", FxShapes.Resolve(new FxNode { Kind = FxNodeKind.Container, Box = R(0, 0, 20, 20) }, R(0, 0, 20, 20)).IsEmpty);

        // Surfaces get no mark; small controls do.
        var panel = R(0, 0, 760, 240);
        check("Buttons and list rows are marked; cards and whole lists are not",
            FxShapes.Accepts(R(10, 6, 28, 24), panel) && FxShapes.Accepts(R(6, 60, 700, 30), panel)
            && !FxShapes.Accepts(R(10, 40, 360, 180), panel) && !FxShapes.Accepts(R(0, 0, 3, 20), panel) && !FxShapes.Accepts(R(6, 40, 200, 90), panel));

        // Device pixels.
        var snapped = FxShapes.Snap(R(10.2, 5.1, 20.3, 9.9), 1.5, 1.5);
        static bool OnPixel(double dip) => Math.Abs(dip * 1.5 - Math.Round(dip * 1.5)) < 1e-9;
        check("Edges land on device pixels at 150 percent, within half a pixel", OnPixel(snapped.X) && OnPixel(snapped.Y) && OnPixel(snapped.Right) && OnPixel(snapped.Bottom)
            && Math.Abs(snapped.X - 10.2) <= .34 && Math.Abs(snapped.Right - 30.5) <= .34 && Math.Abs(snapped.Bottom - 15) <= .34);
        check("A one pixel line sits on a pixel's middle, a two pixel line between pixels",
            Math.Abs(FxShapes.SnapLine(10.3, 1, 1) - 10.5) < 1e-9 && Math.Abs(FxShapes.SnapLine(10.3, 2, 1) - 10) < 1e-9 && Math.Abs(FxShapes.SnapLine(10.3, .5, 2) - 10.25) < 1e-9 && Math.Abs(FxShapes.SnapLine(10.3, 1, 2) - 10.5) < 1e-9);
        check("Clipped to the viewport; nothing when outside it", Near(FxShapes.Clip(R(0, 90, 50, 20), R(0, 0, 100, 100)), R(0, 90, 50, 10)) && FxShapes.Clip(R(0, 120, 50, 20), R(0, 0, 100, 100)).Width == 0);

        // Curves.
        check("Steps jump at the start of each step and land exactly", MotionCurve.Step(.01, 3) == 1 / 3.0 && MotionCurve.Step(.34, 3) == 2 / 3.0 && MotionCurve.Step(1, 3) == 1 && MotionCurve.Step(0, 3) == 0);
        check("Without steps the curve is the soft landing", MotionCurve.Ease(.5, 0) > .5 && MotionCurve.Ease(1, 0) == 1 && MotionCurve.Ease(0, 0) == 0);
        bool rising = true; double last = 0;
        for (int i = 0; i <= 100; i++) { double v = MotionCurve.Ease(i / 100.0, 3); rising &= v >= last; last = v; }
        check("A stepped arrival only ever rises: three changes, no flicker back", rising);

        // Timing.
        var normal = MotionTiming.From(360, 1);
        check("A press goes down within a tenth of a second and lets go within a fifth", normal.PressIn <= 100 && normal.PressOut <= 200 && normal.PressIn < normal.PressOut && normal.Glitch <= 90);
        var hard = MotionTiming.From(360, .8);
        check("The cyberpunk press phases are 60 to 140 ms", hard.PressIn is >= 40 and <= 80 && hard.PressOut is >= 100 and <= 160 && hard.HoverIn <= 140);
        check("Press timings follow the animation length, within limits", MotionTiming.From(1000, 1.6).PressIn == 200 && MotionTiming.From(100, .6).PressIn == 40);

        // The press.
        var t = normal; var press = new PressTrack();
        press.Press(10, 0, glitch: true);
        var first = press.At(10.001, t, 0);
        check("A press answers at once on the way down", first.Held > .02 && first.GlitchOn && first.Release < 0);
        check("Held, it stays down", press.At(12, t, 0).Held == 1 && press.Down);
        check("The glitch is a moment, not the whole hold", !press.At(10 + t.Glitch / 1000.0 + .001, t, 0).GlitchOn);
        press.Arm(12, inside: false, t, 0);
        check("Dragged off the control while held, it backs off", press.At(12 + t.PressOut / 1000.0, t, 0).Held < .01);
        press.Arm(13, inside: true, t, 0);
        check("Back on the control, it goes down again", press.At(13 + t.PressIn / 1000.0, t, 0).Held > .99);
        press.Release(14, inside: true, scan: true, t, 0);
        var released = press.At(14 + t.PressOut / 2000.0, t, 0);
        check("Released on the control it completes: lets go, plays its flourish and scan", press.Committed && released.Held is > 0 and < 1 && released.Flourish > 0 && released.ScanAt >= 0);
        check("Then it ends", !press.Alive(14 + t.Press / 1000.0 + .01, t) && press.At(14 + t.PressOut / 1000.0, t, 0).Held == 0);
        press.Press(20, 0, glitch: false);
        press.Release(20.05, inside: false, scan: false, t, 0);
        var cancelled = press.At(20.06, t, 0);
        check("Released elsewhere it only lets go: no flourish, no scan", !press.Committed && cancelled.Flourish < 0 && cancelled.ScanAt < 0 && !press.Alive(20.05 + t.PressOut / 1000.0 + .001, t));
        press.Press(30, 0, false); press.Arm(30.01, false, t, 0); press.Release(30.2, inside: true, scan: true, t, 0);
        check("Dragged off and released back on the edge, a press that was backed off does not complete", !press.Committed);
        press.Press(40, 0, false);
        double mid = press.At(40.03, t, 0).Held;
        press.Release(40.03, true, false, t, 0);
        press.Press(40.05, press.At(40.05, t, 0).Held, false);
        check("A quick second press goes down from where the first one was, never from nothing", press.At(40.0501, t, 0).Held >= mid * .5);
        var stepped = new PressTrack(); stepped.Press(0, 0, false);
        check("In steps, the press clicks most of the way down in its first frame", stepped.At(.001, hard, 3).Held > PressTrack.Click && stepped.At(hard.PressIn / 1000.0, hard, 3).Held == 1);
        var instant = new PressTrack(); instant.Press(0, 0, false);
        check("Even the very first pressed frame shows the press (it is drawn in the same frame as the button goes down)", instant.At(0, normal, 0).Held >= PressTrack.Click);

        // Flash safety: however fast the clicks, at most PressFlashes glitches and scans start in any second (WCAG 2.3 allows
        // three flashes a second), each one a single rise and fall.
        var budget = new FlashBudget();
        var starts = new List<double>();
        for (int i = 0; i < 200; i++)
        {
            double at = i * .07; // over 14 clicks a second
            if (budget.TryTake(at)) starts.Add(at);
            if (budget.TryTake(at + .03)) starts.Add(at + .03);
        }
        int peak = 0;
        for (int i = 0; i < starts.Count; i++) peak = Math.Max(peak, starts.Count(s => s >= starts[i] && s < starts[i] + 1));
        check("Rapid clicks never make more than two flashes in a second", peak <= MotionSafety.PressFlashes && MotionSafety.PressFlashes < MotionSafety.MaxFlashHz && starts.Count > 10);
        budget.Clear();
        check("The budget refills after a second", budget.TryTake(0) && budget.TryTake(.1) && !budget.TryTake(.5) && budget.TryTake(1.01));
        // One press, sampled at 240 Hz: the glitch shows once and the scan once, never blinking on and off.
        var one = new PressTrack(); one.Press(0, 0, true); one.Release(.2, true, true, t, 3);
        int glitchEdges = 0, scanEdges = 0; bool g = false, s = false;
        for (int i = 0; i < 240; i++)
        {
            var f = one.At(i / 240.0, t, 3);
            if (f.GlitchOn != g) { glitchEdges++; g = f.GlitchOn; }
            if (f.ScanAt >= 0 != s) { scanEdges++; s = f.ScanAt >= 0; }
        }
        check("A glitch and a scan each show once per press", glitchEdges == 2 && scanEdges == 2);
    }
}
