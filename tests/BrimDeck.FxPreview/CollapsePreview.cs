using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// The collapse textures (CollapseTexture, CollapseFx) on the panel's own closing: the open page in an island that morphs into
// the notch, the capsule or the line on the panel's closing tracks and curve (NoticeMotion.PanelCloseSurface, Soft.Drawer),
// with the texture on the page as it leaves, or the plain fade (PanelCloseContent, Soft.Out) for comparison. For every theme
// asked for, effect and collapsed form a frame strip, one frame every --step milliseconds (16 by default), at 100 and 200
// percent (<effect>-<form>-<theme>-<scale>.png); the time each frame takes to arrange and the time a texture takes to start
// (collapse-timing.csv, p50, p95 and max); and checks that stopping a texture ends it at once. Offscreen: the scene is hosted
// in an HwndSource that is never shown, on a clock of its own; BrimDeck is not started.
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --collapse <output folder> [--themes id,id,...] [--step 16]
internal static class CollapsePreview
{
    private const double Width = 800, Height = 340, PageWidth = 760, PageHeight = 300;
    private static int _failures;
    private static double _now;
    public static double FrameStep = .016;

    private enum Form { Notch, Capsule, Line }

    public static int Run(string folder, HashSet<string>? only)
    {
        Directory.CreateDirectory(folder);
        var timing = new List<string> { "effect,form,theme,scale,frames,p50_ms,p95_ms,max_ms,start_ms" };
        var themes = PanelThemes.BuiltIns.Where(t => only is null || only.Contains(t.Id)).ToList();
        foreach (var scale in new[] { 1.0, 2.0 })
            foreach (var theme in themes)
            {
                var palette = PanelThemes.Resolve(theme);
                var styles = new List<CollapseStyle> { CollapseStyle.Fade };
                if (palette.Motion.Collapse != CollapseStyle.Fade) styles.Add(palette.Motion.Collapse);
                else styles.AddRange([CollapseStyle.Glitch, CollapseStyle.Foam, CollapseStyle.Genie]);
                foreach (var style in styles.Distinct())
                    foreach (var form in Enum.GetValues<Form>())
                    {
                        var (shots, cost) = Strip(palette, style, form, scale);
                        string name = $"{style.ToString().ToLowerInvariant()}-{form.ToString().ToLowerInvariant()}-{theme.Id}-{scale * 100:0}";
                        Save(shots, $"{style} · {form} · {theme.Id} · {scale * 100:0}% · every {FrameStep * 1000:0} ms", Path.Combine(folder, name + ".png"));
                        if (style != CollapseStyle.Fade)
                            timing.Add(string.Join(",", style.ToString().ToLowerInvariant(), form.ToString().ToLowerInvariant(), theme.Id, (scale * 100).ToString("0", CultureInfo.InvariantCulture),
                                cost.Frames, F(cost.P50), F(cost.P95), F(cost.Max), F(cost.Start)));
                    }
            }
        File.WriteAllLines(Path.Combine(folder, "collapse-timing.csv"), timing);
        Interruptions();
        Console.WriteLine($"Wrote collapse strips to {Path.GetFullPath(folder)}");
        Console.WriteLine(_failures == 0 ? "PASS: all collapse checks" : $"{_failures} collapse checks failed");
        return _failures;
    }

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name);
        if (!ok) _failures++;
    }

    private sealed class Scene
    {
        public required HwndSource Source;
        public required Grid Stage;
        public required Border Island, Compact;
        public required Grid Page;
        public required CollapseTexture Texture;
    }

    // A dark desktop (the user's wallpaper anchors), the open panel hanging from the top, a page of rows and bars in it, and
    // the collapsed island's content waiting to land.
    private static Scene Build(PanelPalette palette, double scale)
    {
        var stage = new Grid { Width = Width, Height = Height, Background = new LinearGradientBrush(Color.FromRgb(0x10, 0x10, 0x30), Color.FromRgb(0x10, 0x30, 0x50), 90), UseLayoutRounding = true };
        Brush Surface() => palette.Effects.SurfaceGradient.Count >= 2
            ? new LinearGradientBrush(C(palette.Effects.SurfaceGradient[0]), C(palette.Effects.SurfaceGradient[^1]), 90)
            : new SolidColorBrush(C(palette.Surface));
        var island = new Border { Background = Surface(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
        var inside = new Grid();
        var page = new Grid { Width = PageWidth, Height = PageHeight, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        var rows = new StackPanel { Margin = new Thickness(24, 18, 24, 0) };
        rows.Children.Add(new TextBlock { Text = "用量 · 系统监控 · 音乐", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(C(palette["textPrimary"])) });
        foreach (var (name, share) in new[] { ("Claude Code", .42), ("Codex", .18), ("Gemini", .66), ("GPU 温度 54°C", .54), ("网络 ↓ 245K", .3) })
        {
            var row = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(new TextBlock { Text = name, FontSize = 13, Foreground = new SolidColorBrush(C(palette["textSecondary"])) });
            row.Children.Add(new TextBlock { Text = $"{share:P0}", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, Foreground = new SolidColorBrush(C(palette["textPrimary"])) });
            rows.Children.Add(row);
            rows.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(0, 6, (1 - share) * 600, 0), Background = new SolidColorBrush(C(palette["accent"])) });
        }
        page.Children.Add(rows);
        var compact = new Border { Child = new TextBlock { Text = "12:45  ·  42%", FontSize = 12, Foreground = new SolidColorBrush(C(palette["textPrimary"])), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, Opacity = 0 };
        var texture = new CollapseTexture { Clock = () => _now };
        inside.Children.Add(compact); inside.Children.Add(page); inside.Children.Add(texture);
        island.Child = inside;
        stage.Children.Add(island);
        var source = new HwndSource(new HwndSourceParameters("BrimDeck.CollapsePreview") { Width = (int)Width, Height = (int)Height, WindowStyle = unchecked((int)0x80000000) }) { RootVisual = stage, SizeToContent = SizeToContent.Manual };
        VisualTreeHelper.SetRootDpi(stage, new DpiScale(scale, scale));
        var scene = new Scene { Source = source, Stage = stage, Island = island, Compact = compact, Page = page, Texture = texture };
        Shape(scene, 0, Form.Notch);
        return scene;
    }

    private static Color C(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    // The closed island of each form.
    private static (Rect Box, CornerRadius Radius) Closed(Form form) => form switch
    {
        Form.Notch => (new Rect((Width - 200) / 2, 0, 200, 32), new CornerRadius(0, 0, 12, 12)),
        Form.Capsule => (new Rect((Width - 220) / 2, 8, 220, 34), new CornerRadius(17)),
        _ => (new Rect((Width - 120) / 2, 4, 120, 5), new CornerRadius(2.5))
    };

    // The island's outline at a share e of the closing's surface track (eased), from the open panel to the form.
    private static void Shape(Scene s, double e, Form form)
    {
        var open = new Rect((Width - PageWidth) / 2, 0, PageWidth, PageHeight);
        var (box, radius) = Closed(form);
        double L(double a, double b) => a + (b - a) * e;
        s.Island.Margin = new Thickness(0, L(open.Y, box.Y), 0, 0);
        s.Island.Width = L(open.Width, box.Width); s.Island.Height = L(open.Height, box.Height);
        s.Island.CornerRadius = new CornerRadius(L(0, radius.TopLeft), L(0, radius.TopRight), L(16, radius.BottomRight), L(16, radius.BottomLeft));
        s.Stage.UpdateLayout();
    }

    // One closing as MainWindow.SetExpanded plays it: the outline on PanelCloseSurface and Soft.Drawer over the whole move, the
    // page on PanelCloseContent and Soft.Out (fade) or as the theme's texture over its own track, the collapsed content landing
    // on PanelLandCompact.
    private static (List<(BitmapSource Image, string Label)> Shots, (int Frames, double P50, double P95, double Max, double Start) Cost) Strip(PanelPalette palette, CollapseStyle style, Form form, double scale)
    {
        PanelTheme.Show(palette);
        var s = Build(palette, scale);
        var motion = palette.Motion;
        double length = style == CollapseStyle.Fade ? NoticeMotion.PanelLength(AnimationLength.Default, closing: true, keyboard: false) / 1000.0
            : CollapseFx.Length(style, motion.Pace, motion.Collapse == style ? motion.CollapseMs : 0);
        var shots = new List<(BitmapSource, string)> { (Render(s, scale), "open") };
        _now = 0;
        double start = 0;
        if (style != CollapseStyle.Fade)
        {
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            var shot = CollapseTexture.Capture(s.Page)!;
            s.Page.Opacity = 0;
            var (from, to) = CollapseFx.Track(style);
            var (box, _) = Closed(form);
            s.Texture.Play(style, shot, length * (to - from), new Rect((PageWidth - box.Width) / 2, 0, box.Width, box.Height), jolt: true,
                PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, motion.Color)), PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, motion.Secondary)), null);
            start = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        var work = new List<double>();
        for (double t = 0; t <= length + FrameStep; t += FrameStep)
        {
            _now = t;
            double share = Math.Clamp(t / length, 0, 1);
            var surface = NoticeMotion.PanelCloseSurface;
            Shape(s, Soft.Drawer.Ease(Math.Clamp((share - surface.Start) / surface.Length, 0, 1)), form);
            if (style == CollapseStyle.Fade)
            {
                var content = NoticeMotion.PanelCloseContent;
                s.Page.Opacity = 1 - Soft.Out.Ease(Math.Clamp((share - content.Start) / content.Length, 0, 1));
            }
            else
            {
                s.Texture.Frame(t);
                if (s.Texture.Playing) work.Add(s.Texture.LastFrameMs);
            }
            var land = NoticeMotion.PanelLandCompact;
            s.Compact.Opacity = form == Form.Line ? 0 : Soft.Out.Ease(Math.Clamp((share - land.Start) / land.Length, 0, 1));
            s.Stage.UpdateLayout();
            shots.Add((Render(s, scale), $"{t * 1000:0} ms"));
        }
        s.Texture.Stop();
        s.Source.Dispose();
        work.Sort();
        double P(double q) => work.Count == 0 ? 0 : work[Math.Min(work.Count - 1, (int)Math.Floor(q * work.Count))];
        return (shots, (work.Count, P(.5), P(.95), work.Count == 0 ? 0 : work[^1], start));
    }

    // Opening again, or a new notice, stops a texture at once: nothing more is drawn and its end never runs.
    private static void Interruptions()
    {
        var palette = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!);
        PanelTheme.Show(palette);
        foreach (var style in new[] { CollapseStyle.Glitch, CollapseStyle.Foam, CollapseStyle.Genie })
        {
            var s = Build(palette, 1);
            _now = 0;
            int ended = 0;
            var shot = CollapseTexture.Capture(s.Page)!;
            s.Texture.Play(style, shot, .3, new Rect(280, 0, 200, 32), true, Colors.Cyan, Colors.Magenta, () => ended++);
            _now = .12; s.Texture.Frame(_now);
            bool drawing = s.Texture.Playing && !VisualTreeHelper.GetDescendantBounds(s.Texture).IsEmpty;
            s.Texture.Stop();
            _now = .2; s.Texture.Frame(_now);
            _now = .6; s.Texture.Frame(_now);
            Check($"{style}: stopped mid-way, it draws nothing more at once and never reports its end",
                drawing && !s.Texture.Playing && VisualTreeHelper.GetDescendantBounds(s.Texture).IsEmpty && ended == 0 && s.Texture.Visibility == Visibility.Collapsed);
            _now = 0;
            s.Texture.Play(style, shot, .3, new Rect(280, 0, 200, 32), true, Colors.Cyan, Colors.Magenta, () => ended++);
            _now = .31; s.Texture.Frame(_now);
            Check($"{style}: played out, it reports its end once and leaves nothing drawn",
                ended == 1 && !s.Texture.Playing && VisualTreeHelper.GetDescendantBounds(s.Texture).IsEmpty);
            s.Source.Dispose();
        }
    }

    private static BitmapSource Render(Scene s, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(s.Stage);
        bitmap.Freeze();
        return bitmap;
    }

    // Frames side by side at half size, six to a row.
    private static void Save(List<(BitmapSource Image, string Label)> shots, string title, string file)
    {
        const double gap = 6, label = 16, top = 26;
        const int columns = 6;
        double cellWidth = Width * .5, cellHeight = Height * .5;
        int rows = (shots.Count + columns - 1) / columns;
        double width = gap + columns * (cellWidth + gap), height = top + rows * (cellHeight + label + gap) + gap;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        var ink = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE));
        var typeface = new Typeface("Microsoft YaHei UI");
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2A)), null, new Rect(0, 0, width, height));
            dc.DrawText(new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 14, ink, 1), new Point(gap, 5));
            for (int i = 0; i < shots.Count; i++)
            {
                double x = gap + i % columns * (cellWidth + gap), y = top + i / columns * (cellHeight + label + gap);
                dc.DrawImage(shots[i].Image, new Rect(x, y, cellWidth, cellHeight));
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
