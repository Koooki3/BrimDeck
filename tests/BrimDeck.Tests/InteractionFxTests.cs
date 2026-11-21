using BrimDeck.Core;

// Where the hover mark is drawn: on a control's visible shape, not on its box.
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
        var half = FxShapes.Snap(R(6, 0, 36, 36), 1.75, 1.75);
        check("A shape on half pixels keeps its width in device pixels (175 percent: 63, not 64)", Math.Abs(half.Width * 1.75 - 63) < 1e-9 && Math.Abs(half.Height * 1.75 - 63) < 1e-9);
        check("A one pixel line sits on a pixel's middle, a two pixel line between pixels",
            Math.Abs(FxShapes.SnapLine(10.3, 1, 1) - 10.5) < 1e-9 && Math.Abs(FxShapes.SnapLine(10.3, 2, 1) - 10) < 1e-9 && Math.Abs(FxShapes.SnapLine(10.3, .5, 2) - 10.25) < 1e-9 && Math.Abs(FxShapes.SnapLine(10.3, 1, 2) - 10.5) < 1e-9);
        check("Clipped to the viewport; nothing when outside it", Near(FxShapes.Clip(R(0, 90, 50, 20), R(0, 0, 100, 100)), R(0, 90, 50, 10)) && FxShapes.Clip(R(0, 120, 50, 20), R(0, 0, 100, 100)).Width == 0);
    }
}
