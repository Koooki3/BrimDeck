using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

// The collapse pictures (CollapseLayer, CollapseFx) on a notice over its island: for every effect and built-in theme a frame
// strip at 100 and 200 percent (<effect>-<theme>-<scale>.png), the time each frame takes to work out (collapse-timing.csv),
// and checks that stopping a picture ends it at once. Offscreen: the scene is hosted in an HwndSource that is never shown,
// on a clock of its own; BrimDeck is not started.
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --collapse <output folder> [--themes id,id,...]
internal static class CollapsePreview
{
    private const double Width = 420, Height = 150;
    private static int _failures;
    // How long starting the last measured picture took: the snapshot, its pixels and the first frame.
    private static double _startMs;
    private static double _now;

    public static int Run(string folder, HashSet<string>? only)
    {
        Directory.CreateDirectory(folder);
        var timing = new List<string> { "effect,theme,scale,frames,mean_ms,max_ms,render_mean_ms,start_ms" };
        var styles = new[] { CollapseStyle.Glitch, CollapseStyle.Foam, CollapseStyle.Genie };
        foreach (var scale in new[] { 1.0, 2.0 })
            foreach (var theme in PanelThemes.BuiltIns.Where(t => only is null || only.Contains(t.Id)))
            {
                var palette = PanelThemes.Resolve(theme);
                foreach (var style in styles)
                {
                    var (shots, cost) = Strip(palette, style, scale);
                    Save(shots, palette, $"{style} · {theme.Id} · {scale * 100:0}%", Path.Combine(folder, $"{style.ToString().ToLowerInvariant()}-{theme.Id}-{scale * 100:0}.png"));
                    timing.Add(Row(style, theme.Id, scale, cost));
                }
            }
        // An open panel's size (760 x 300 DIP) at 200 percent: the heaviest picture a collapse takes.
        foreach (var style in styles)
        {
            var palette = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!);
            var (_, cost) = Strip(palette, style, 2, panel: true);
            timing.Add(Row(style, "panel-760x300", 2, cost));
        }
        File.WriteAllLines(Path.Combine(folder, "collapse-timing.csv"), timing);
        Interruptions();
        Console.WriteLine($"Wrote collapse strips to {Path.GetFullPath(folder)}");
        Console.WriteLine(_failures == 0 ? "PASS: all collapse checks" : $"{_failures} collapse checks failed");
        return _failures;
    }

    private static string Row(CollapseStyle style, string id, double scale, (int Frames, double Mean, double Max, double Render) cost)
        => string.Join(",", style.ToString().ToLowerInvariant(), id, (scale * 100).ToString("0", CultureInfo.InvariantCulture), cost.Frames,
            cost.Mean.ToString("0.00", CultureInfo.InvariantCulture), cost.Max.ToString("0.00", CultureInfo.InvariantCulture),
            cost.Render.ToString("0.00", CultureInfo.InvariantCulture), _startMs.ToString("0.00", CultureInfo.InvariantCulture));

    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name);
        if (!ok) _failures++;
    }

    private sealed class Scene
    {
        public required HwndSource Source;
        public required Grid Stage;
        public required Border Island, Notice;
        public required CollapseLayer Layer;
    }

    // A dark desktop (the user's wallpaper anchors), the collapsed island at the top and a notice grown out of it.
    private static Scene Build(PanelPalette palette, double scale, bool panel = false)
    {
        double Width = panel ? 800 : CollapsePreview.Width, Height = panel ? 340 : CollapsePreview.Height;
        var stage = new Grid { Width = Width, Height = Height, Background = new LinearGradientBrush(Color.FromRgb(0x10, 0x10, 0x30), Color.FromRgb(0x10, 0x30, 0x50), 90), UseLayoutRounding = true };
        Brush Surface() => palette.Effects.SurfaceGradient.Count >= 2
            ? new LinearGradientBrush(C(palette.Effects.SurfaceGradient[0]), C(palette.Effects.SurfaceGradient[^1]), 90)
            : new SolidColorBrush(C(palette.Surface));
        var island = new Border { Width = 190, Height = 32, CornerRadius = new CornerRadius(0, 0, 12, 12), Background = Surface(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        island.Child = new TextBlock { Text = "12:45  ·  42%", Foreground = new SolidColorBrush(C(palette["textPrimary"])), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var notice = new Border { Width = panel ? 760 : 340, Height = panel ? 300 : 92, CornerRadius = new CornerRadius(0, 0, 18, 18), Background = Surface(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
        var text = new StackPanel { Margin = new Thickness(16, 14, 16, 0) };
        text.Children.Add(new TextBlock { Text = "Claude Code", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(C(palette["textPrimary"])) });
        text.Children.Add(new TextBlock { Text = "任务完成：已提交 3 个文件，测试全部通过。", FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Foreground = new SolidColorBrush(C(palette["textSecondary"])) });
        var bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 10, 0, 0), Background = new SolidColorBrush(C(palette["accent"])) };
        text.Children.Add(bar);
        notice.Child = text;
        var layer = new CollapseLayer { Clock = () => _now };
        stage.Children.Add(island); stage.Children.Add(notice); stage.Children.Add(layer);
        var source = new HwndSource(new HwndSourceParameters("BrimDeck.CollapsePreview") { Width = (int)Width, Height = (int)Height, WindowStyle = unchecked((int)0x80000000) }) { RootVisual = stage, SizeToContent = SizeToContent.Manual };
        VisualTreeHelper.SetRootDpi(stage, new DpiScale(scale, scale));
        stage.InvalidateMeasure();
        stage.Measure(new Size(Width, Height)); stage.Arrange(new Rect(0, 0, Width, Height)); stage.UpdateLayout();
        return new Scene { Source = source, Stage = stage, Island = island, Notice = notice, Layer = layer };
    }

    private static Color C(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
    private static Rect Bounds(Scene s, FrameworkElement e) => e.TransformToAncestor(s.Stage).TransformBounds(new Rect(e.RenderSize));

    // Starts the picture as NoticeWindow.End does: snapshot, the real notice gone at once, the picture over the island.
    private static double Start(Scene s, CollapseStyle style, Action? ended = null, double aberration = 0)
    {
        var snapshot = s.Layer.Capture(s.Notice)!;
        var from = Bounds(s, s.Notice);
        s.Notice.Visibility = Visibility.Hidden;
        s.Stage.UpdateLayout();
        s.Layer.Play(style, snapshot, from, Bounds(s, s.Island), 1, Colors.White, aberration, ended);
        return CollapseFx.Length(style, 1);
    }

    private static (List<(BitmapSource Image, string Label)> Shots, (int Frames, double Mean, double Max, double Render) Cost) Strip(PanelPalette palette, CollapseStyle style, double scale, bool panel = false)
    {
        PanelTheme.Show(palette);
        var s = Build(palette, scale, panel);
        var shots = new List<(BitmapSource, string)>();
        _now = 0;
        shots.Add((Render(s, scale), "before"));
        double length = Start(s, style);
        // Ten frames across the picture and the frame after its end.
        for (int i = 0; i <= 10; i++)
        {
            _now = length * i / 10.0 - (i == 10 ? 1e-4 : 0);
            s.Layer.Frame(_now);
            shots.Add((Render(s, scale), $"{_now * 1000:0} ms"));
        }
        _now = length + .001;
        s.Layer.Frame(_now);
        shots.Add((Render(s, scale), "after"));
        // The cost of a frame at 60 Hz over a second picture: the work on the UI thread (LastFrameMs) and a full render.
        s.Notice.Visibility = Visibility.Visible; s.Stage.UpdateLayout();
        _now = 0;
        long setup = System.Diagnostics.Stopwatch.GetTimestamp();
        length = Start(s, style);
        _startMs = (System.Diagnostics.Stopwatch.GetTimestamp() - setup) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var work = new List<double>(); var render = new List<double>();
        for (double t = 0; t < length; t += 1 / 60.0)
        {
            _now = t;
            s.Layer.Frame(t);
            work.Add(s.Layer.LastFrameMs);
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            Render(s, scale);
            render.Add((System.Diagnostics.Stopwatch.GetTimestamp() - begin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        s.Layer.Stop();
        s.Source.Dispose();
        return (shots, (work.Count, work.Average(), work.Max(), render.Average()));
    }

    // A new notice stops a picture at once: nothing more is drawn and its end never runs (NoticeWindow.Present).
    private static void Interruptions()
    {
        var palette = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!);
        PanelTheme.Show(palette);
        foreach (var style in new[] { CollapseStyle.Glitch, CollapseStyle.Foam, CollapseStyle.Genie })
        {
            var s = Build(palette, 1);
            _now = 0;
            int ended = 0;
            double length = Start(s, style, () => ended++);
            _now = length * .4; s.Layer.Frame(_now);
            bool drawing = s.Layer.Playing && !VisualTreeHelper.GetDescendantBounds(s.Layer).IsEmpty;
            s.Layer.Stop();
            _now = length * .6; s.Layer.Frame(_now);
            _now = length * 2; s.Layer.Frame(_now);
            check($"{style}: stopped mid-way, it draws nothing more at once and never reports its end",
                drawing && !s.Layer.Playing && VisualTreeHelper.GetDescendantBounds(s.Layer).IsEmpty && ended == 0);
            s.Notice.Visibility = Visibility.Visible; s.Stage.UpdateLayout();
            _now = 0;
            length = Start(s, style, () => ended++);
            _now = length + .01; s.Layer.Frame(_now);
            check($"{style}: played out, it reports its end once and leaves nothing drawn",
                ended == 1 && !s.Layer.Playing && VisualTreeHelper.GetDescendantBounds(s.Layer).IsEmpty);
            s.Source.Dispose();
        }
        static void check(string name, bool ok) => Check(name, ok);
    }

    private static BitmapSource Render(Scene s, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(s.Stage.Width * scale), (int)Math.Ceiling(s.Stage.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(s.Stage);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(List<(BitmapSource Image, string Label)> shots, PanelPalette palette, string title, string file)
    {
        const double gap = 6, label = 16, top = 26;
        const int columns = 4;
        double cellWidth = shots.Max(s => s.Image.PixelWidth), cellHeight = shots.Max(s => s.Image.PixelHeight);
        int rows = (shots.Count + columns - 1) / columns;
        double width = gap + columns * (cellWidth + gap), height = top + rows * (cellHeight + label + gap) + gap;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        var ink = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE));
        var typeface = new Typeface("Microsoft YaHei UI");
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2A)), null, new Rect(0, 0, width, height));
            dc.DrawText(new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 14, ink, 1), new Point(gap, 5));
            for (int i = 0; i < shots.Count; i++)
            {
                double x = gap + i % columns * (cellWidth + gap), y = top + i / columns * (cellHeight + label + gap);
                dc.DrawImage(shots[i].Image, new Rect(x, y, shots[i].Image.PixelWidth, shots[i].Image.PixelHeight));
                dc.DrawText(new FormattedText(shots[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, ink, 1), new Point(x + 2, y + cellHeight + 1));
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
