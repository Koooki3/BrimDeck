using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

// Frame strips of the AI usage page's date range picker (ModelDatePicker) on a fixed clock: the pop-up opening (Soft.Enter's
// curves), the active field and a marker gliding, a month change, the validation message opening and closing, and the theme's
// answers to the pointer on its buttons, for a few themes. The picker is hosted in an HwndSource that is never shown; BrimDeck
// is not started and no settings or theme files are read. Run by hand:
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --date <output folder>
internal static class DatePreview
{
    private const double Scale = 2, Width = 246, Pad = 14;
    private static double _now = 1000;

    private sealed record Shot(string Label, BitmapSource Image);

    private sealed class Scene
    {
        public required HwndSource Source;
        public required Grid Root;
        public required ModelDatePicker Picker;
        public required FxSurface Fx;
        public required double Height;
    }

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        int failures = 0;
        foreach (var id in new[] { PanelThemes.Demo, PanelThemes.Light, "github-dark" })
            failures += Strips(PanelThemes.Resolve(PanelThemes.BuiltIn(id)!), id, folder);
        Console.WriteLine("Wrote date picker previews to " + Path.GetFullPath(folder));
        return failures;
    }

    private static DateTime Day(int day) { var today = DateTime.Today; return new DateTime(today.Year, today.Month, Math.Min(day, today.Day)); }

    private static Scene Build(PanelPalette palette)
    {
        var picker = new ModelDatePicker(new UsageDateRange(Day(5), Day(12)), palette)
        {
            Preview = new() { Clock = () => _now, Moves = () => true, AnimationDuration = 360 }, VerticalAlignment = VerticalAlignment.Top, Width = Width
        };
        double height = picker.FullHeight(Width);
        var stage = new Grid { Width = Width, Height = height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Pad) };
        stage.Children.Add(picker);
        var fx = new FxSurface(stage, stage, () => true)
        {
            Preview = new()
            {
                Clock = () => _now, Palette = () => palette, SystemTheme = false, AnimationDuration = 360,
                Conditions = () => new MotionConditions(true, true, false, true, false, false, false, false, true)
            }
        };
        stage.Children.Add(fx);
        // A desktop behind the pop-up: dark for a dark theme, light for a light one.
        var root = new Grid { Background = new SolidColorBrush(palette.IsDark ? Color.FromRgb(0x2B, 0x30, 0x3A) : Color.FromRgb(0xC9, 0xD2, 0xDC)) };
        root.Children.Add(stage);
        double w = Width + 2 * Pad, h = height + 2 * Pad;
        var source = new HwndSource(new HwndSourceParameters("BrimDeck.DatePreview") { Width = (int)w, Height = (int)h, WindowStyle = unchecked((int)0x80000000) }) { RootVisual = root, SizeToContent = SizeToContent.Manual };
        root.Measure(new Size(w, h)); root.Arrange(new Rect(0, 0, w, h)); root.UpdateLayout();
        picker.Frame(_now);
        // The effects stay inside the picker's rounded face, as the window's layer does (InteractionFx.ClipTo).
        fx.Clip = new RectangleGeometry(new Rect(0, 0, Width, picker.ActualHeight), 10, 10);
        picker.SizeChanged += (_, _) => fx.Clip = new RectangleGeometry(new Rect(0, 0, Width, picker.ActualHeight), 10, 10);
        return new Scene { Source = source, Root = root, Picker = picker, Fx = fx, Height = h };
    }

    private static int Strips(PanelPalette palette, string id, string folder)
    {
        int failures = 0;
        void Check(string name, bool ok) { if (!ok) failures++; Console.WriteLine((ok ? "PASS: " : "FAIL: ") + id + " · " + name); }
        var scene = Build(palette);
        var t = DatePickerTiming.From(360);
        double glide = t.Glide / 1000.0, month = t.MonthLength / 1000.0, leave = t.Leave / 1000.0, delay = t.ArriveDelay / 1000.0, message = t.Message / 1000.0;
        var rows = new List<List<Shot>>();

        // Opening: Soft.Enter's fade on Soft.Out and its 6 DIP drop, as ModelDetails starts it after the first frame.
        var translate = new TranslateTransform();
        scene.Picker.RenderTransform = translate;
        var opening = new List<Shot>();
        foreach (double ms in new double[] { 0, 40, 80, 140, 200, 300 })
        {
            scene.Picker.Opacity = Soft.Out.Ease(Math.Min(1, ms / Soft.Short));
            translate.Y = -6 + 6 * Soft.Out.Ease(Math.Min(1, ms / (Soft.Short + 60)));
            opening.Add(new Shot(ms == 0 ? "打开 0ms" : ms + "ms", Render(scene)));
        }
        scene.Picker.Opacity = 1; translate.Y = 0;
        rows.Add(opening);

        // The end field becomes active (its outline glides), then a later end day: the marker glides and the band grows.
        rows.Add(Run(scene, [
            (0, "选结束栏", s => Click(Named(s, "选择结束日期", "Choose the end date"))), (glide / 3, "1/3", null), (glide, "到位", null),
            (glide + .1, "选 20 日", s => Click(DayButton(s, Day(20)))), (glide + .1 + glide / 4, "1/4", null), (glide + .1 + glide / 2, "1/2", null),
            (glide + .1 + glide * 3 / 4, "3/4", null), (glide * 2 + .15, "到位", null)]));
        // The start field, then an earlier start day: the start marker glides back.
        rows.Add(Run(scene, [
            (0, "选开始栏", s => Click(Named(s, "选择开始日期", "Choose the start date"))), (glide / 2, "1/2", null), (glide + .05, "选 2 日", s => Click(DayButton(s, Day(2)))),
            (glide + .05 + glide / 3, "1/3", null), (glide + .05 + glide * 2 / 3, "2/3", null), (glide * 2 + .1, "到位", null)]));

        // A month change back and forward again.
        rows.Add(Run(scene, [
            (0, "上个月", s => Click(Named(s, "上个月", "Previous month"))), (.001, "首帧", null), (leave / 2, "旧月离开", null), (delay, "交接", null),
            (delay + t.Arrive / 3000.0, "新月 1/3", null), (delay + t.Arrive * 2 / 3000.0, "2/3", null), (month + .02, "到位", null)]));
        var before = Probe(scene);
        rows.Add(Run(scene, [
            (0, "下个月", s => Click(Named(s, "下个月", "Next month"))), (.001, "首帧", null), (delay, "交接", null), (delay + t.Arrive / 2000.0, "1/2", null), (month + .02, "到位", null)]));

        // An end before the start: the message opens, the buttons below move down, Apply eases to unavailable; then a valid end.
        rows.Add(Run(scene, [
            (0, "选结束栏", s => Click(Named(s, "选择结束日期", "Choose the end date"))), (glide + .05, "结束早于开始", s => Click(DayButton(s, Day(1)))), (glide + .05 + message / 3, "1/3", null),
            (glide + .05 + message * 2 / 3, "2/3", null), (glide + .05 + message + .05, "展开", null),
            (glide + message + .3, "改为有效", s => Click(DayButton(s, Day(15)))), (glide + message + .3 + message / 2, "1/2", null), (glide + message * 2 + .4, "收起", null)]));

        // The theme's answers: a day hovered, gliding to its neighbour, and Apply pressed and released.
        rows.Add(Run(scene, [
            (0, "悬停日期", s => Hover(s, DayButton(s, Day(8)))), (.08, "+80ms", null), (.2, "滑到相邻", s => Hover(s, DayButton(s, Day(9)))), (.3, "到位", null),
            (.5, "按下应用", s => { Hover(s, Named(s, "应用日期筛选", "Apply the date filter")); s.Fx.PressOn(Named(s, "应用日期筛选", "Apply the date filter")); }), (.6, "按住", null), (.7, "松开", s => s.Fx.LiftOff(true)), (.85, "+150ms", null)]));

        Check("the month change slides and returns to the same place", (before - Probe(scene)).Length < .5);
        Save(rows, palette, id + " · 日期范围选择", Path.Combine(folder, id + "-date.png"));
        scene.Source.Dispose();
        return failures;
    }

    // Where the grid lies, to see it come back to the same place after a month change.
    private static Point Probe(Scene scene) => First(scene, name => DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        .TransformToVisual(scene.Root).Transform(new Point());

    // The picker's controls, found as a screen reader finds them: by their names (Chinese or English, as the UI language is).
    private static Button Named(Scene scene, params string[] names) => First(scene, names.Contains);
    private static Button DayButton(Scene scene, DateTime day) => Named(scene, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static Button First(Scene scene, Func<string, bool> match)
    {
        Button? Walk(DependencyObject node)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is Button b && match(System.Windows.Automation.AutomationProperties.GetName(b))) return b;
                if (Walk(child) is { } found) return found;
            }
            return null;
        }
        return Walk(scene.Picker) ?? throw new InvalidOperationException("No such control in the picker");
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static Button? _hovered;
    private static void Hover(Scene scene, Button? target)
    {
        if (_hovered?.Template?.FindName("HoverLayer", _hovered) is UIElement old) old.Opacity = 0;
        _hovered = target;
        if (target?.Template?.FindName("HoverLayer", target) is UIElement layer) layer.Opacity = 1;
        scene.Fx.HoverOn(target);
    }

    private static List<Shot> Run(Scene scene, List<(double At, string Label, Action<Scene>? Step)> steps)
    {
        scene.Fx.Reset(); Hover(scene, null);
        double start = _now += 10;
        // Whatever was moving has settled.
        scene.Picker.Frame(_now); scene.Root.UpdateLayout();
        var shots = new List<Shot>();
        foreach (var (at, label, step) in steps)
        {
            while (_now + 1 / 60.0 < start + at) { _now += 1 / 60.0; scene.Picker.Frame(_now); scene.Fx.Frame(_now); }
            _now = start + at;
            if (step is not null) { step(scene); scene.Root.UpdateLayout(); }
            scene.Picker.Frame(_now); scene.Root.UpdateLayout();
            scene.Fx.Frame(_now);
            shots.Add(new Shot(label, Render(scene)));
        }
        scene.Fx.Reset(); Hover(scene, null);
        return shots;
    }

    private static BitmapSource Render(Scene scene)
    {
        scene.Root.UpdateLayout();
        double w = Width + 2 * Pad;
        var bitmap = new RenderTargetBitmap((int)(w * Scale), (int)(scene.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(scene.Root);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(List<List<Shot>> scenarios, PanelPalette palette, string title, string file)
    {
        var rows = new List<List<Shot>>();
        foreach (var scenario in scenarios)
            for (int i = 0; i < scenario.Count; i += 6) rows.Add(scenario.Skip(i).Take(6).ToList());
        const double gap = 8, label = 18, top = 30;
        double cellWidth = rows.SelectMany(r => r).Max(s => s.Image.PixelWidth);
        int columns = rows.Max(r => r.Count);
        double width = gap + columns * (cellWidth + gap), height = top + rows.Sum(r => r.Max(s => s.Image.PixelHeight) + label + gap) + gap;
        var visual = new DrawingVisual();
        var ink = new SolidColorBrush(palette.IsDark ? Color.FromRgb(0xE8, 0xE8, 0xEE) : Color.FromRgb(0x20, 0x20, 0x28));
        var typeface = new Typeface("Microsoft YaHei UI");
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(palette.IsDark ? Color.FromRgb(0x30, 0x30, 0x36) : Color.FromRgb(0xE4, 0xE4, 0xEA)), null, new Rect(0, 0, width, height));
            dc.DrawText(new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 16, ink, 1), new Point(gap, 6));
            double y = top;
            foreach (var row in rows)
            {
                double x = gap, rowHeight = row.Max(s => s.Image.PixelHeight);
                foreach (var shot in row)
                {
                    dc.DrawImage(shot.Image, new Rect(x, y, shot.Image.PixelWidth, shot.Image.PixelHeight));
                    dc.DrawText(new FormattedText(shot.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, ink, 1), new Point(x + 2, y + rowHeight + 1));
                    x += cellWidth + gap;
                }
                y += rowHeight + label + gap;
            }
        }
        var output = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        output.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
