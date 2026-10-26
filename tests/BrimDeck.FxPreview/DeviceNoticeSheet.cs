using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using Path = System.IO.Path;

// A device's low battery notice in every built-in theme: the arriving toast for each kind of device, with the device's own
// glyph on the plate and the low battery badge, next to the same notice without its glyph (the application's initial, as it
// was). The toast is laid out as NoticeWindow.BuildToast lays it out (52 DIP high, a 32 DIP plate, 12.5/11.5 DIP lines, the
// dot in the application's color), and the plate is drawn by the app's own NoticeBadges. Not a test: a picture to look at.
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --device-notice <folder>
internal static class DeviceNoticeSheet
{
    private const double Width = 760, Height = 420;

    private static readonly (DeviceKind Kind, string Name, int Percent)[] Devices =
    [
        (DeviceKind.Mouse, "MCHOSE A7 V2 Ultra+", 12), (DeviceKind.Keyboard, "MX Keys S", 9), (DeviceKind.Headphones, "WH-1000XM4", 15),
        (DeviceKind.Earbuds, "AirPods Pro", 10), (DeviceKind.Controller, "Xbox Wireless Controller", 8), (DeviceKind.Pen, "Surface Pen", 5),
        (DeviceKind.Speaker, "JBL Flip 6", 14), (DeviceKind.Other, "Wireless device", 11),
    ];

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var theme in PanelThemes.BuiltIns)
        {
            Draw(PanelThemes.Resolve(theme), Path.Combine(folder, theme.Id + "-device-notice.png"), theme.DisplayName);
            if (theme.File.Night is not null) Draw(PanelThemes.Resolve(theme, true), Path.Combine(folder, theme.Id + "-night-device-notice.png"), theme.DisplayName + " · 夜间");
        }
        Loc.Use(Loc.English);
        foreach (var id in new[] { PanelThemes.Dark, PanelThemes.Light })
            Draw(PanelThemes.Resolve(PanelThemes.BuiltIn(id)!), Path.Combine(folder, id + "-device-notice-en.png"), id + " · English");
        Loc.Use(Loc.Chinese);
        Console.WriteLine("Wrote device notice sheets to " + Path.GetFullPath(folder));
        return 0;
    }

    private static void Draw(PanelPalette palette, string file, string title)
    {
        PanelTheme.Show(palette);
        var root = new Canvas { Width = Width, Height = Height, Background = Backdrop() };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        Place(root, new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }, 12, Height - 26);
        for (int i = 0; i < Devices.Length; i++)
            Place(root, Toast(palette, Notice(Devices[i].Kind, Devices[i].Name, Devices[i].Percent, glyph: true)), 20 + i % 2 * 370, 12 + i / 2 * 72);
        Place(root, new TextBlock { Text = Loc.T("此前（字母）", "Before (initial)"), FontSize = 11, Foreground = Brushes.Gray }, 400, Height - 70);
        Place(root, Toast(palette, Notice(DeviceKind.Mouse, Devices[0].Name, Devices[0].Percent, glyph: false)), 390, Height - 54 - 4);
        root.UseLayoutRounding = true;
        root.Measure(new Size(Width, Height)); root.Arrange(new Rect(0, 0, Width, Height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var big = new RenderTargetBitmap((int)Width * 2, (int)Height * 2, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor); dc.DrawImage(bitmap, new Rect(0, 0, Width * 2, Height * 2)); }
        big.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(big));
        using var stream = File.Create(file); encoder.Save(stream);
    }

    // The notice NoticeCenter.SendDeviceLow sends, as the hub keeps it; without the glyph reference when glyph is false.
    private static Notice Notice(DeviceKind kind, string name, int percent, bool glyph) => new()
    {
        Origin = NoticeOrigin.BrimDeck, AppKey = NoticeCenter.DevicesAppKey, AppName = Loc.T("设备", "Devices"),
        IconRef = glyph ? NoticeGlyphs.GlyphRef(NoticeGlyphs.Device(kind)) : null, Category = NoticeCategory.Warning,
        Title = Loc.T($"{name} 电量低", $"{name} battery low"), Body = Loc.T($"剩余 {percent}%，请及时充电。", $"{percent}% left. Charge it soon.")
    };

    private static void Place(Canvas canvas, UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element); }

    // The toast as NoticeWindow.BuildToast builds it, on the theme's island.
    private static UIElement Toast(PanelPalette palette, Notice notice)
    {
        const double width = 350, height = 52, plateSize = 32, padding = 16;
        var look = NoticeLook.Current();
        var accent = look.AppColor(NoticeIcons.Fallback(notice.AppKey));
        var row = new Grid { Margin = new Thickness(padding, 0, padding, 0), Height = height };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var plate = new Grid { Width = plateSize, Height = plateSize, VerticalAlignment = VerticalAlignment.Center };
        plate.Children.Add(new Border { CornerRadius = new CornerRadius(PanelTheme.Corner(plateSize * .28)), Background = UI.Brush(look.Fill) });
        if (NoticeBadges.AppGlyph(notice, plateSize, accent) is { } glyph) plate.Children.Add(glyph);
        else plate.Children.Add(new TextBlock { Text = notice.AppName[..1], FontFamily = UI.DisplayFont, FontSize = Math.Round(plateSize * .5), FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        NoticeBadges.Decorate(plate, notice, plateSize, look.Glow, NoticeGlyphs.Plate(notice));
        row.Children.Add(plate);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        texts.Children.Add(Line(notice.Title, 12.5, look.Primary, FontWeights.SemiBold));
        texts.Children.Add(Line(notice.Body, 11.5, look.Secondary, FontWeights.Normal));
        Grid.SetColumn(texts, 1); row.Children.Add(texts);
        var dot = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(dot, 2); row.Children.Add(dot);
        return ThemeSheet.Island(palette, width, height, new CornerRadius(0, 0, 20, 20), 16, row);
    }

    private static TextBlock Line(string text, double size, string color, FontWeight weight) => new()
    {
        Text = text, FontFamily = UI.PanelFont, FontSize = size, FontWeight = weight, Foreground = UI.Brush(color), TextTrimming = TextTrimming.CharacterEllipsis
    };

    // Dark at the left, light at the right, as the devices sheet.
    private static Brush Backdrop()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(0x1B, 0x1F, 0x3A), Color.FromRgb(0x6A, 0x2A, 0x86), 30), null, new RectangleGeometry(new Rect(0, 0, 380, Height))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF2)), null, new RectangleGeometry(new Rect(380, 0, Width - 380, Height))));
        return new DrawingBrush(group) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, Viewbox = new Rect(0, 0, Width, Height), ViewboxUnits = BrushMappingMode.Absolute };
    }
}
