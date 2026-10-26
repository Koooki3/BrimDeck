using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.IO.Path;
using BrimDeck;
using BrimDeck.Core;

// A design sheet for each theme: the collapsed notch, capsule and indicator on a desktop, the open panel with its toolbar,
// usage rows, quota rings, clock and a selected segment, a pop-up menu with a hovered row and a chosen date, and a notice, all
// drawn with the app's own island (IslandBorder dressed by PanelTheme), clock (ClockFace) and notice surface (NoticeSurface).
// The desktop behind is half dark wallpaper, half a light document, so glass themes show over both. Drawn at 100 percent and
// enlarged two times, pixel for pixel, so stepped corners and lines can be checked. Not a test: a picture to look at.
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --sheet <folder> [--themes id,id,...] [--dpi 1.5]
internal static class ThemeSheet
{
    private const double Width = 1280, Height = 520;

    private static double _dpi = 1;

    public static int Run(string folder, HashSet<string>? only, double dpi = 1)
    {
        _dpi = dpi;
        string suffix = dpi == 1 ? "" : $"-{dpi * 100:0}";
        Directory.CreateDirectory(folder);
        foreach (var theme in PanelThemes.BuiltIns.Where(t => only is null || only.Contains(t.Id)))
        {
            Draw(PanelThemes.Resolve(theme), Path.Combine(folder, theme.Id + suffix + "-sheet.png"), theme.DisplayName);
            if (theme.File.Night is not null) Draw(PanelThemes.Resolve(theme, true), Path.Combine(folder, theme.Id + suffix + "-night-sheet.png"), theme.DisplayName + " · 夜间");
            if (theme.File.Dusk is not null) Draw(PanelThemes.Resolve(theme, DaylightLook.Dusk), Path.Combine(folder, theme.Id + suffix + "-dusk-sheet.png"), theme.DisplayName + " · 黄昏");
        }
        Console.WriteLine("Wrote theme sheets to " + Path.GetFullPath(folder));
        return 0;
    }

    // A quota's color as the panel picks it: the app's own, then the warning and critical colors.
    private static string Quota(ProviderId app, double percent) => percent >= AppPresets.CriticalPercent ? PanelTheme.Readable(PanelTheme.CriticalDefault)
        : percent >= AppPresets.WarningPercent ? PanelTheme.Readable(PanelTheme.WarningDefault) : PanelTheme.DefaultAppColor(app);
    private static Color C(ThemeColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    private static SolidColorBrush B(PanelPalette p, string key) => new(C(p[key]));

    private static void Draw(PanelPalette palette, string file, string title)
    {
        PanelTheme.Show(palette);
        var root = new Canvas { Width = Width, Height = Height, Background = Desktop() };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        root.Children.Add(Label(title, 12, 492, Colors.White));

        // Collapsed: notch, capsule and indicator, each at the top edge of its own stretch of desktop.
        Place(root, Collapsed(palette, CompactStyle.Notch), 70, 0);
        Place(root, Collapsed(palette, CompactStyle.Capsule), 520, 6);
        var line = new Border { Width = 120, Height = 4, CornerRadius = new CornerRadius(2), Background = B(palette, "indicator") };
        var progress = new Border { Width = 46, Height = 4, CornerRadius = new CornerRadius(2), Background = B(palette, "indicatorProgress"), HorizontalAlignment = HorizontalAlignment.Left };
        Place(root, new Grid { Children = { line, progress } }, 1010, 4);

        // Open: the panel hangs from the top edge, square at the top as on the real panel.
        Place(root, Panel(palette), 340, 60);
        Place(root, Popup(palette), 40, 120);
        Place(root, Notice(palette), 920, 120);

        // Laid out and drawn at the scale under test, as on a display at that scale; then enlarged two times, pixel for pixel.
        root.UseLayoutRounding = true;
        VisualTreeHelper.SetRootDpi(root, new DpiScale(_dpi, _dpi));
        root.Measure(new Size(Width, Height)); root.Arrange(new Rect(0, 0, Width, Height)); root.UpdateLayout();
        int w = (int)Math.Ceiling(Width * _dpi), h = (int)Math.Ceiling(Height * _dpi);
        var bitmap = new RenderTargetBitmap(w, h, 96 * _dpi, 96 * _dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var nearest = new RenderTargetBitmap(w * 2, h * 2, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            dc.DrawImage(bitmap, new Rect(0, 0, w * 2, h * 2));
        }
        nearest.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(nearest));
        using var stream = File.Create(file); encoder.Save(stream);
    }

    private static void Place(Canvas canvas, UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element); }
    private static TextBlock Label(string text, double x, double y, Color color)
    {
        var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color) };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y);
        return label;
    }

    // Dark wallpaper with bright shapes on the left, a light document with text on the right.
    private static Brush Desktop()
    {
        var group = new DrawingGroup();
        var dark = new LinearGradientBrush(Color.FromRgb(0x1B, 0x1F, 0x3A), Color.FromRgb(0x6A, 0x2A, 0x86), 30);
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 640, Height))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3D)), null, new EllipseGeometry(new Point(160, 40), 70, 70)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0xB6)), null, new EllipseGeometry(new Point(470, 300), 120, 90)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF2)), null, new RectangleGeometry(new Rect(640, 0, 640, Height))));
        var ink = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x36));
        for (int i = 0; i < 26; i++)
        {
            double width = 420 + (i * 37 % 160);
            group.Children.Add(new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(680, 14 + i * 19, width, 7))));
        }
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)), null, new RectangleGeometry(new Rect(680, 200, 220, 120))));
        return new DrawingBrush(group) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, Viewbox = new Rect(0, 0, Width, Height), ViewboxUnits = BrushMappingMode.Absolute };
    }

    // The island as the panel dresses it, with its shadow or glow drawn from a copy outside the island only (as MainWindow does
    // for glass, where the copy would show through).
    private static Grid Island(PanelPalette palette, double width, double height, CornerRadius corners, double shoulder, UIElement content)
    {
        IslandBorder Shape(double inset) => new()
        {
            Width = width, Height = height, CornerRadius = corners, ShoulderRadius = shoulder, Inset = inset, MaterialOpacity = 1,
            Background = new SolidColorBrush(PanelTheme.SurfaceColor), Pixel = palette.Effects.Pixel, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        var shadow = new DropShadowEffect { Direction = 270 };
        PanelTheme.Dress(shadow, palette);
        var caster = new Grid { Effect = shadow, Margin = new Thickness(0) };
        caster.Children.Add(Shape(1));
        var holder = new Grid();
        var shadowHost = new Grid { Children = { caster } };
        if (PanelTheme.GlassShown)
        {
            var outline = IslandBorder.BuildOutline(width, height, shoulder, corners, 0, palette.Effects.Pixel);
            shadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-200, -200, width + 400, height + 400)), outline);
        }
        holder.Children.Add(shadowHost);
        var island = Shape(0);
        PanelTheme.Dress(island, palette);
        if (palette.Effects.Pixel > 0) TextOptions.SetTextRenderingMode(island, TextRenderingMode.Aliased);
        island.Child = content;
        holder.Children.Add(island);
        return holder;
    }

    private static UIElement Collapsed(PanelPalette palette, CompactStyle style)
    {
        bool notch = style == CompactStyle.Notch;
        var row = new DockPanel { Margin = new Thickness(notch ? 26 : 14, 0, notch ? 26 : 14, 0), VerticalAlignment = VerticalAlignment.Center };
        var clock = new ClockFace(Enum.TryParse<ClockStyle>(palette.Clock?.Style, true, out var s) ? s : ClockStyle.Minimal, true, [PanelTheme.ColorOf("clock")], new DateTime(2026, 10, 24, 14, 32, 0))
            { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(clock, Dock.Right); row.Children.Add(clock);
        var rings = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (app, percent) in new[] { (ProviderId.Claude, 47d), (ProviderId.Codex, 23), (ProviderId.Antigravity, 76), (ProviderId.Kimi, 91) })
        {
            var ring = UI.Ring(percent, Quota(app, percent), 12, 2); ring.Margin = new Thickness(0, 0, 8, 0); rings.Children.Add(ring);
        }
        row.Children.Add(rings);
        return Island(palette, notch ? 260 : 230, 32, notch ? new CornerRadius(0, 0, 12, 12) : new CornerRadius(16), notch ? 16 : 0, row);
    }

    private static UIElement Panel(PanelPalette palette)
    {
        var stack = new StackPanel { Margin = new Thickness(14, 8, 14, 10), Width = 552 };
        // Toolbar: the page switch with the selected page, the title, the settings glyph.
        var toolbar = new DockPanel { Height = 30 };
        var pages = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Background = B(palette, "switchFill") };
        var pageRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (glyph, on) in new[] { ("", true), ("", false), ("", false), ("", false) })
            pageRow.Children.Add(new Border { Width = 28, Height = 24, CornerRadius = new CornerRadius(palette.Effects.Pixel > 0 ? 0 : 6), Background = on ? B(palette, "fillSelected") : Brushes.Transparent,
                Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = on ? B(palette, "textPrimary") : B(palette, "textSecondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        pages.Child = pageRow;
        DockPanel.SetDock(pages, Dock.Left); toolbar.Children.Add(pages);
        var gear = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, Foreground = B(palette, "textSecondary"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(gear, Dock.Right); toolbar.Children.Add(gear);
        toolbar.Children.Add(new TextBlock { Text = "AI 用量 · 更新于 14:32", FontSize = 12, Foreground = B(palette, "textTertiary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        stack.Children.Add(toolbar);
        stack.Children.Add(new Border { Height = 1, Background = B(palette, "hairline"), Margin = new Thickness(0, 6, 0, 6) });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var rows = new StackPanel();
        foreach (var (app, name, percent, reset) in new[] { (ProviderId.Claude, "Claude Code", 47d, "5 小时窗口 · 2 小时后重置"), (ProviderId.Codex, "Codex", 23, "每周 · 周一重置"),
            (ProviderId.Antigravity, "Antigravity", 76, "每日 · 今晚重置"), (ProviderId.Kimi, "Kimi", 91, "额度即将用完") })
        {
            var row = new Grid { Margin = new Thickness(0, 0, 12, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = B(palette, "textPrimary") });
            text.Children.Add(new TextBlock { Text = reset, FontSize = 11, Foreground = B(palette, percent >= 90 ? "danger" : "textTertiary") });
            row.Children.Add(text);
            var value = new TextBlock { Text = $"{percent:0}%", FontSize = 13, Foreground = B(palette, "textSecondary"), VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(value, 1); row.Children.Add(value);
            var track = new Grid { Height = 4, Margin = new Thickness(0, 34, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumnSpan(track, 2);
            double radius = palette.Effects.Pixel > 0 ? 0 : 2;
            track.Children.Add(new Border { CornerRadius = new CornerRadius(radius), Background = B(palette, "track") });
            track.Children.Add(new Border { CornerRadius = new CornerRadius(radius), Background = UI.Brush(Quota(app, percent)), HorizontalAlignment = HorizontalAlignment.Left, Width = 360 * percent / 100 });
            row.Children.Add(track);
            row.Height = 44;
            rows.Children.Add(row);
        }
        body.Children.Add(rows);
        var side = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var clock = new ClockFace(Enum.TryParse<ClockStyle>(palette.Clock?.Style, true, out var s) ? s : ClockStyle.Minimal, true, [PanelTheme.ColorOf("clock")], new DateTime(2026, 10, 24, 14, 32, 0), 2)
            { HorizontalAlignment = HorizontalAlignment.Center };
        side.Children.Add(clock);
        var ringRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 12) };
        foreach (var (app, percent) in new[] { (ProviderId.Claude, 47d), (ProviderId.Codex, 23), (ProviderId.Kimi, 91) })
        { var ring = UI.Ring(percent, Quota(app, percent), 30, 3); ring.Margin = new Thickness(5, 0, 5, 0); ringRow.Children.Add(ring); }
        side.Children.Add(ringRow);
        // A segment control with the chosen segment, and the accent: a focused button.
        var segments = new Border { CornerRadius = new CornerRadius(palette.Effects.Pixel > 0 ? 0 : 7), Background = B(palette, "fill"), Padding = new Thickness(2) };
        var segRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, on) in new[] { ("今日", false), ("本周", true), ("本月", false) })
            segRow.Children.Add(new Border { Padding = new Thickness(9, 3, 9, 3), CornerRadius = new CornerRadius(palette.Effects.Pixel > 0 ? 0 : 5), Background = on ? B(palette, "fillSelected") : Brushes.Transparent,
                Child = new TextBlock { Text = label, FontSize = 12, Foreground = B(palette, on ? "textPrimary" : "textSecondary") } });
        segments.Child = segRow;
        side.Children.Add(segments);
        Grid.SetColumn(side, 1); body.Children.Add(side);
        stack.Children.Add(body);
        return Island(palette, 580, 300, new CornerRadius(0, 0, 18, 18), 0, stack);
    }

    // A menu and a date picker's chosen day: an opaque pop-up in the theme's pop-up colors.
    private static UIElement Popup(PanelPalette palette)
    {
        bool pixel = palette.Effects.Pixel > 0;
        var menu = new Border { Width = 230, CornerRadius = new CornerRadius(pixel ? 0 : 8), Background = B(palette, "popup"), BorderBrush = B(palette, "popupBorder"), BorderThickness = new Thickness(pixel ? 2 : 1), Padding = new Thickness(4) };
        var stack = new StackPanel();
        foreach (var (text, detail, state) in new[] { ("网易云音乐", "晴天 · 周杰伦", 0), ("QQ 音乐", "正在播放", 1), ("Spotify", "已暂停", 0) })
        {
            var row = new Border { CornerRadius = new CornerRadius(pixel ? 0 : 5), Padding = new Thickness(8, 4, 8, 4), Background = state == 1 ? B(palette, "hover") : Brushes.Transparent };
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = B(palette, "textPrimary") });
            inner.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = B(palette, "menuDetail") });
            row.Child = inner; stack.Children.Add(row);
        }
        stack.Children.Add(new Border { Height = 1, Background = B(palette, "hairline"), Margin = new Thickness(4, 4, 4, 6) });
        var days = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 4) };
        for (int d = 21; d <= 26; d++)
        {
            bool chosen = d == 24, range = d is 22 or 23;
            days.Children.Add(new Border { Width = 30, Height = 26, CornerRadius = new CornerRadius(pixel ? 0 : 13),
                Background = chosen ? B(palette, "accent") : range ? B(palette, "rangeFill") : Brushes.Transparent,
                Child = new TextBlock { Text = d.ToString(), FontSize = 12, Foreground = B(palette, chosen ? "onAccent" : "textPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        }
        stack.Children.Add(days);
        stack.Children.Add(new TextBlock { Text = "无法读取用量：网络超时", FontSize = 11, Foreground = B(palette, "danger"), Margin = new Thickness(8, 2, 0, 4) });
        menu.Child = stack;
        if (pixel) TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Aliased);
        return new Border { Child = menu, Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = .3 } };
    }

    // A notice as the notice window draws it: the island's outline in the theme's notice look, an app mark, a title and body,
    // a main button on the accent and a second one on the fill.
    private static UIElement Notice(PanelPalette palette)
    {
        var look = NoticeLook.Current();
        var content = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Ellipse { Width = 18, Height = 18, Fill = UI.Brush(PanelTheme.DefaultAppColor(ProviderId.Claude)), Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(new TextBlock { Text = "Claude Code · 任务完成", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(look.Primary) });
        content.Children.Add(head);
        content.Children.Add(new TextBlock { Text = "已为四个新主题生成预览图，等待确认。", FontSize = 12, Foreground = UI.Brush(look.Secondary), Margin = new Thickness(26, 4, 0, 10) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 0, 0, 0) };
        bool pixel = palette.Effects.Pixel > 0;
        buttons.Children.Add(new Border { CornerRadius = new CornerRadius(pixel ? 0 : 8), Padding = new Thickness(12, 4, 12, 4), Background = UI.Brush(look.Accent), Child = new TextBlock { Text = "打开", FontSize = 12, Foreground = UI.Brush(look.OnAccent) } });
        buttons.Children.Add(new Border { CornerRadius = new CornerRadius(pixel ? 0 : 8), Padding = new Thickness(12, 4, 12, 4), Background = UI.Brush(look.Fill), Margin = new Thickness(8, 0, 0, 0), Child = new TextBlock { Text = "稍后", FontSize = 12, Foreground = UI.Brush(look.Primary) } });
        content.Children.Add(buttons);
        // The notice window's own surface and its glow copy, dressed as NoticeWindow.Dress dresses them.
        var surface = new NoticeSurface { Width = 320, Height = 104, TopRadius = 22, BottomRadius = 22, Look = look, Child = content };
        bool translucent = look.Surface.A < 255;
        var glow = new NoticeSurface { Width = 320, Height = 104, TopRadius = 22, BottomRadius = 22, FillOverride = translucent ? Brushes.Transparent : null, MaterialHidden = !translucent, Look = look,
            Effect = look.ShadowOpacity <= 0 ? null : new DropShadowEffect { Color = look.Shadow, BlurRadius = look.ShadowBlur, ShadowDepth = look.ShadowDepth, Direction = 270, Opacity = look.ShadowOpacity } };
        if (look.Pixel > 0) TextOptions.SetTextRenderingMode(surface, TextRenderingMode.Aliased);
        return new Grid { Children = { glow, surface } };
    }
}
