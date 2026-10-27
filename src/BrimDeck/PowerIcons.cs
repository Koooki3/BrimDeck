using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The marks of the power and battery rows, in place of words: what a figure comes from, whether it is an estimate, the mains, the time,
// the power mode, the battery's health, the peak and the mean. One drawing of each on a 16 unit grid, set in the theme's graphic language
// (PowerLook): plain line art, rounder and heavier for the island, a hairline for glass, straight edges for the HUD look and the industrial
// plate (IconStyle), and 8 by 8 squares for pixel art. A mark that stands for an estimate is dashed.
internal enum PowerIcon { Chip, Meter, BatteryOut, Plug, Clock, Heart, Leaf, GaugeLow, GaugeMid, GaugeHigh, Peak, Mean, Bolt }

internal static class PowerIcons
{
    // Stroked line art on a 16 unit grid.
    private static readonly Dictionary<PowerIcon, Geometry> Lines = new()
    {
        // A chip with its pins: the sum of the processor's and the graphics' power.
        [PowerIcon.Chip] = Frozen("M4.5,4.5 H11.5 V11.5 H4.5 Z M6.5,2 V4.5 M9.5,2 V4.5 M6.5,11.5 V14 M9.5,11.5 V14 M2,6.5 H4.5 M2,9.5 H4.5 M11.5,6.5 H14 M11.5,9.5 H14"),
        // An instrument: a frame with a trace in it (a meter of the whole machine).
        [PowerIcon.Meter] = Frozen("M2.5,3 H13.5 V13 H2.5 Z M4.5,9.5 L6.5,6 L8.5,10.5 L10,7.5 L11.5,8.5"),
        // A battery giving power out (the discharge): the body with a minus in it.
        [PowerIcon.BatteryOut] = Frozen("M2.5,4.5 H12 A1,1 0 0 1 13,5.5 V10.5 A1,1 0 0 1 12,11.5 H2.5 A1,1 0 0 1 1.5,10.5 V5.5 A1,1 0 0 1 2.5,4.5 Z M14.5,7 V9 M4.5,8 H9.5"),
        [PowerIcon.Plug] = Frozen("M5.5,1.8 V5 M10.5,1.8 V5 M4,5 H12 V7.8 A4,4 0 0 1 4,7.8 Z M8,11.8 V14.5"),
        [PowerIcon.Clock] = Frozen("M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 Z M8,4.8 V8 L10.3,9.7"),
        [PowerIcon.Heart] = Frozen("M8,13.6 L2.9,8.3 A3.2,3.2 0 0 1 8,4.1 A3.2,3.2 0 0 1 13.1,8.3 Z"),
        [PowerIcon.Leaf] = Frozen("M3,13 C3,6.5 7.5,3 13,3 C13,9 9.5,13 3,13 Z M3,13 L9,7"),
        // The power mode: a dial with its needle low (efficiency), in the middle (balanced) or high (performance).
        [PowerIcon.GaugeLow] = Frozen("M2.2,11.8 A5.8,5.8 0 0 1 13.8,11.8 M2.2,11.8 H13.8 M8,11.8 L4.8,8.4"),
        [PowerIcon.GaugeMid] = Frozen("M2.2,11.8 A5.8,5.8 0 0 1 13.8,11.8 M2.2,11.8 H13.8 M8,11.8 V6.4"),
        [PowerIcon.GaugeHigh] = Frozen("M2.2,11.8 A5.8,5.8 0 0 1 13.8,11.8 M2.2,11.8 H13.8 M8,11.8 L11.2,8.4"),
        // The peak: a head over a base. The mean: a bar over an x.
        [PowerIcon.Peak] = Frozen("M8,3.2 L13,11.2 H3 Z"),
        [PowerIcon.Mean] = Frozen("M3.8,3.6 H12.2 M4.8,6.8 L11.2,13 M11.2,6.8 L4.8,13"),
        [PowerIcon.Bolt] = Frozen("M9.2,1.5 L3.8,9 H7.6 L7,14.5 L12.2,7 H8.4 Z"),
    };
    // Marks drawn filled rather than stroked.
    private static bool Solid(PowerIcon icon) => icon is PowerIcon.Bolt;

    // The 8 by 8 squares of pixel art, one string a row.
    private static readonly Dictionary<PowerIcon, string[]> Pixels = new()
    {
        [PowerIcon.Chip] = ["..#..#..", ".######.", "##....##", ".#....#.", "##....##", ".#....#.", ".######.", "..#..#.."],
        [PowerIcon.Meter] = ["########", "#......#", "#...#..#", "#.#.#.##", "#.#.#.##", "#..#...#", "#......#", "########"],
        [PowerIcon.BatteryOut] = ["........", "#######.", "#.....##", "#.###.##", "#.....##", "#######.", "........", "........"],
        [PowerIcon.Plug] = ["..#..#..", "..#..#..", ".######.", ".#....#.", ".#....#.", "..####..", "...##...", "...##..."],
        [PowerIcon.Clock] = ["..####..", ".#....#.", "#..#...#", "#..#...#", "#..##..#", ".#....#.", "..####..", "........"],
        [PowerIcon.Heart] = [".##..##.", "########", "########", "########", ".######.", "..####..", "...##...", "........"],
        [PowerIcon.Leaf] = ["......##", "....####", "..######", ".#####.#", "#####.#.", "####.#..", "##.#....", "#......."],
        [PowerIcon.GaugeLow] = ["..####..", ".#....#.", "#.#....#", "#..#...#", "#...#..#", "#......#", "########", "........"],
        [PowerIcon.GaugeMid] = ["..####..", ".#.##.#.", "#..##..#", "#..##..#", "#..##..#", "#......#", "########", "........"],
        [PowerIcon.GaugeHigh] = ["..####..", ".#....#.", "#....#.#", "#...#..#", "#..#...#", "#......#", "########", "........"],
        [PowerIcon.Peak] = ["...##...", "..####..", "..####..", ".######.", ".######.", "########", "........", "........"],
        [PowerIcon.Mean] = ["########", "........", "#......#", ".#....#.", "..#..#..", "..#..#..", ".#....#.", "#......#"],
        [PowerIcon.Bolt] = ["...###..", "..###...", ".###....", "..#####.", "....###.", "...###..", "..##....", "..#....."],
    };

    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    // How the theme draws a mark: Plain, Round (the island), Fine (glass), Angular (the HUD look), Industrial (a plate: IconStyle) or Pixel.
    private enum Treatment { Plain, Round, Fine, Angular, Industrial, Pixel }
    private static Treatment TreatmentOf(PowerStyle style) => style.Look.Gauge switch
    {
        PowerGauge.Round => Treatment.Round, PowerGauge.Glass => Treatment.Fine, PowerGauge.Blocks => Treatment.Pixel,
        PowerGauge.Chamfer => PanelTheme.Plated ? Treatment.Industrial : Treatment.Angular, _ => Treatment.Plain
    };

    private static readonly Dictionary<PowerIcon, Geometry> Angles = [];
    private static Geometry Angular(PowerIcon icon) => Angles.TryGetValue(icon, out var known) ? known : Angles[icon] = IconStyle.Angular(Lines[icon], 16);

    // Pens by color, thickness (on the 16 unit grid), treatment and dash.
    private static readonly Dictionary<(string Color, double Thickness, Treatment Kind, bool Dashed), Pen> Pens = [];
    private static Pen Stroke(string color, double thickness, Treatment kind, bool dashed)
    {
        if (Pens.TryGetValue((color, thickness, kind, dashed), out var known)) return known;
        bool square = kind is Treatment.Angular or Treatment.Industrial;
        var pen = new Pen(UI.Brush(color), thickness)
        {
            StartLineCap = square ? PenLineCap.Square : PenLineCap.Round, EndLineCap = square ? PenLineCap.Square : PenLineCap.Round,
            LineJoin = square ? PenLineJoin.Miter : PenLineJoin.Round, MiterLimit = 3
        };
        if (dashed) pen.DashStyle = new DashStyle([1.7, 1.5], 0);
        pen.Freeze();
        if (Pens.Count > 96) Pens.Clear();
        return Pens[(color, thickness, kind, dashed)] = pen;
    }

    // The mark in the box (its shorter side is its size), in the color; dashed for an estimate. ppd: device pixels per DIP.
    public static void Draw(DrawingContext drawing, PowerIcon icon, Rect box, string color, PowerStyle style, double ppd, bool dashed = false)
    {
        double size = Math.Min(box.Width, box.Height);
        if (size < 3) return;
        var kind = TreatmentOf(style);
        double x = box.X + (box.Width - size) / 2, y = box.Y + (box.Height - size) / 2;
        if (kind == Treatment.Pixel) { Pixel(drawing, icon, x, y, size, color, ppd, dashed); return; }
        double scale = size / 16;
        // The stroke in DIP: a little heavier for the island and the plate, a hairline for glass.
        double dip = kind switch { Treatment.Round => 1.55, Treatment.Fine => 1, Treatment.Industrial => 1.5, Treatment.Angular => 1.25, _ => 1.35 };
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
        var geometry = kind is Treatment.Angular or Treatment.Industrial ? Angular(icon) : Lines[icon];
        if (Solid(icon)) drawing.DrawGeometry(UI.Brush(color), null, geometry);
        else drawing.DrawGeometry(null, Stroke(color, dip / scale, kind, dashed), geometry);
        drawing.Pop();
    }

    // Squares on whole device pixels (as the island's steps are): a dashed mark leaves every other square out.
    private static void Pixel(DrawingContext drawing, PowerIcon icon, double x, double y, double size, string color, double ppd, bool dashed)
    {
        double cell = Math.Max(1, Math.Round(size / 8 * ppd)) / ppd;
        double left = Math.Round(x * ppd) / ppd, top = Math.Round(y * ppd) / ppd;
        var brush = UI.Brush(color);
        var rows = Pixels[icon];
        for (int row = 0; row < rows.Length; row++)
            for (int column = 0; column < rows[row].Length; column++)
            {
                if (rows[row][column] != '#') continue;
                if (dashed && (row + column) % 2 == 1) continue;
                drawing.DrawRectangle(brush, null, new Rect(left + column * cell, top + row * cell, cell, cell));
            }
    }
}

// The first time the power rows are shown, the UI thread built the icons' geometry and compiled the rows' code on the spot (about 25 ms, with
// the page's own first layout). Once the app has started and has nothing else to do, a pool thread does both: the geometry (frozen, so any
// thread may use it) and the code of the rows, the tooltip's words and the figures. One pass, a few milliseconds of one pool thread, and
// nothing runs again while the panel is idle.
internal static class PowerWarm
{
    public static void Start(Dispatcher dispatcher) => dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ThreadPool.QueueUserWorkItem(_ => Run()));

    private static void Run()
    {
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(PowerIcons).TypeHandle);
            const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var type in new[] { typeof(PowerBlock), typeof(PowerGlyphs), typeof(PowerIcons), typeof(PowerStyle), typeof(PowerText), typeof(PowerResolver), typeof(BatteryModel), typeof(PowerFormat) })
                foreach (var method in type.GetMethods(All))
                    if (!method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() is not null) RuntimeHelpers.PrepareMethod(method.MethodHandle);
            // The words and the figures once, with made-up readings.
            var sample = new PowerSample(20, PowerOrigin.Estimated, PowerSource.Components, [new PowerPart("cpu", "CPU", 12, "RAPL")]);
            var battery = BatteryModel.Build(new BatteryInputs([new BatteryCell("warm", 30_000, 60_000, 66_000, -10_000, false, true)], false, false, PowerMode.Balanced));
            PowerText.Power(Reading<PowerSample>.Of(sample), new PowerSummary(30, 20, 3));
            PowerText.Battery(battery);
            PowerText.Spoken(Reading<PowerSample>.Of(sample), battery, true);
            PowerFormat.Clock(TimeSpan.FromMinutes(90)); PowerFormat.Duration(TimeSpan.FromMinutes(90)); PowerFormat.Watts(12.3);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
