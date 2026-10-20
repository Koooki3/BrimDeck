using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// A miniature of the expanded panel in a theme, for the theme cards in settings: the toolbar, two quota columns (one in its
// warning color) and the totals, on the theme's surface with its gradient, scan lines and rim. Every color comes from the
// palette given, not from the theme in use, so each card shows its own theme.
internal static class PanelThemePreview
{
    private const double Width = 240, Height = 136;
    // The desktop behind the panel follows the panel's own tone, as the user would most likely pair them.
    private static readonly Brush DarkDesk = Frozen(new LinearGradientBrush(Color.FromRgb(0x2B, 0x33, 0x45), Color.FromRgb(0x15, 0x18, 0x22), 90));
    private static readonly Brush LightDesk = Frozen(new LinearGradientBrush(Color.FromRgb(0xDD, 0xE4, 0xEE), Color.FromRgb(0xC4, 0xCF, 0xDC), 90));
    private static readonly Geometry ScreenClip = Frozen(new RectangleGeometry(new Rect(0, 0, Width, Height), 9, 9));
    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    public static FrameworkElement Create(PanelPalette palette)
    {
        Brush Paint(string key) => UI.Brush(palette[key].ToString());
        string App(ProviderId id) => palette.AppColor(id).ToString();
        string warning = palette.WarningColor.ToString();
        TextBlock Text(string text, double size, string key, FontWeight? weight = null) => new()
        {
            Text = text, FontFamily = UI.PanelFont, FontSize = size, Foreground = Paint(key), FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center
        };
        var screen = new Grid { Width = Width, Height = Height, Background = palette.IsDark ? DarkDesk : LightDesk, IsHitTestVisible = false, Clip = ScreenClip };
        // The panel and the shadow under it share one shape.
        IslandBorder Shape() => new()
        {
            Width = 212, Height = 118, CornerRadius = new CornerRadius(0, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Background = Paint("surface")
        };
        var island = Shape(); island.MaterialOpacity = 1; island.ClipToBounds = true;
        PanelTheme.Dress(island, palette);
        var body = new Grid { Margin = new Thickness(10, 4, 10, 8) };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The toolbar: the page switch at the left, the clock in the middle, the settings button at the right.
        var toolbar = new Grid();
        var segment = new Border { Background = Paint("switchFill"), CornerRadius = new CornerRadius(4), Padding = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var pair = new StackPanel { Orientation = Orientation.Horizontal };
        pair.Children.Add(new Border { Width = 14, Height = 10, CornerRadius = new CornerRadius(3), Background = Paint("fillSelected"), Child = new Border { Width = 6, Height = 1.4, Background = Paint("textPrimary") } });
        pair.Children.Add(new Border { Width = 14, Height = 10, Child = new Border { Width = 6, Height = 1.4, Background = Paint("textSecondary") } });
        segment.Child = pair; toolbar.Children.Add(segment);
        var clock = Text("14:32", 6.5, "textTertiary"); clock.HorizontalAlignment = HorizontalAlignment.Center; toolbar.Children.Add(clock);
        toolbar.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Stroke = Paint("textSecondary"), StrokeThickness = 1.2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(toolbar);

        var columns = new UniformGrid { Columns = 2, Rows = 1 };
        FrameworkElement Column(string name, string plan, string color, double used, bool warn, bool separator)
        {
            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(separator ? 8 : 0, 0, separator ? 0 : 8, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Text(name, 8.5, "textPrimary", FontWeights.SemiBold));
            head.Children.Add(new Border { BorderBrush = Paint("outline"), BorderThickness = new Thickness(.75), CornerRadius = new CornerRadius(2), Padding = new Thickness(2.5, 0, 2.5, 0),
                Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = Text(plan, 5.5, "textTertiary") });
            column.Children.Add(head);
            var label = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var reset = Text(Loc.T("2 时后", "in 2h"), 5.5, "textTertiary"); DockPanel.SetDock(reset, Dock.Right); label.Children.Add(reset);
            label.Children.Add(Text(Loc.T("5 小时", "5-hour"), 6, "textSecondary"));
            column.Children.Add(label);
            var number = Text($"{used:0}%", 15, "textPrimary", FontWeights.SemiBold);
            if (warn) number.Foreground = UI.Brush(color);
            number.Margin = new Thickness(0, 1, 0, 3); column.Children.Add(number);
            var track = new Grid { Height = 3 };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - used, GridUnitType.Star) });
            var rail = new Border { Background = Paint("track"), CornerRadius = new CornerRadius(1.5) }; Grid.SetColumnSpan(rail, 2); track.Children.Add(rail);
            track.Children.Add(new Border { Background = UI.Brush(color), CornerRadius = new CornerRadius(1.5) });
            column.Children.Add(track);
            return separator ? new Border { BorderBrush = Paint("hairline"), BorderThickness = new Thickness(.75, 0, 0, 0), Child = column } : column;
        }
        columns.Children.Add(Column("Claude", "Max", App(ProviderId.Claude), 42, warn: false, separator: false));
        columns.Children.Add(Column("Codex", "Pro", warning, 76, warn: true, separator: true));
        Grid.SetRow(columns, 1); body.Children.Add(columns);

        var totals = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        totals.Children.Add(new Border { Height = .75, Background = Paint("hairline"), Margin = new Thickness(0, 0, 0, 4) });
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(Text(Loc.T("今日", "Today"), 6, "textTertiary"));
        var tokens = Text("1.24M", 6, "textSecondary"); tokens.Margin = new Thickness(6, 0, 6, 0); line.Children.Add(tokens);
        line.Children.Add(Text("$3.40", 6, "textPrimary", FontWeights.SemiBold));
        var ring = UI.Ring(64, App(ProviderId.Antigravity), 8, 1.4, palette["ringTrack"].ToString());
        ring.Margin = new Thickness(10, 0, 0, 0); line.Children.Add(ring);
        totals.Children.Add(line);
        Grid.SetRow(totals, 2); body.Children.Add(totals);
        island.Child = body;
        // The collapsed shadow or glow, under the panel as the notch would cast it. A card never changes, so the blur is kept as a
        // bitmap instead of being drawn again whenever the settings page scrolls or a card is hovered.
        var glow = new System.Windows.Media.Effects.DropShadowEffect { Direction = 270 };
        PanelTheme.Dress(glow, palette); glow.Freeze();
        var shadow = Shape(); shadow.Effect = glow; shadow.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        screen.Children.Add(shadow); screen.Children.Add(island);
        return new Viewbox { Child = screen, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
    }
}
