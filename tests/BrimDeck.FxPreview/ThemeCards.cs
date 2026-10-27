using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

// The theme cards of the settings page: each theme's collapsed capsule as CompactStylePreview draws it, with the stage badge of
// a theme that names one (PanelThemeFile.Stage, or "alpha" at the end of its name) and its name under it, on the settings window's
// light and dark inset colors, enlarged two times. Not a test: a picture to look at.
//   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --cards <output folder> [--themes id,id,...] [--scale 150]
internal static class ThemeCards
{
    public static int Run(string folder, HashSet<string>? only, double scale)
    {
        Directory.CreateDirectory(folder);
        var themes = PanelThemes.BuiltIns.Where(theme => only is null || only.Contains(theme.Id)).ToList();
        foreach (bool dark in new[] { false, true })
        {
            var row = new WrapPanel { Width = 3 * 232 + 24, Background = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)) };
            foreach (var theme in themes)
            foreach (var look in new[] { DaylightLook.Day, DaylightLook.Night }.Where(look => look == DaylightLook.Day || theme.File.Night is not null && theme.Id == PanelThemes.Endfield))
            {
                var palette = PanelThemes.Resolve(theme, look);
                PanelTheme.Show(palette);
                var preview = CompactStylePreview.Theme(palette);
                string name = theme.DisplayName;
                string? stage = theme.File.Stage is { Length: > 0 } written ? written : name.EndsWith("alpha", StringComparison.OrdinalIgnoreCase) ? "alpha" : null;
                if (stage is not null && name.EndsWith(stage, StringComparison.OrdinalIgnoreCase)) name = name[..^stage.Length].TrimEnd();
                if (look == DaylightLook.Night) name += " · night";
                FrameworkElement card = preview;
                if (stage is not null)
                {
                    var badge = new Border
                    {
                        CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 2), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                        Background = new SolidColorBrush(dark ? Color.FromRgb(0x2D, 0x2D, 0x2D) : Colors.White),
                        Child = new TextBlock { Text = stage, FontSize = 11, Foreground = new SolidColorBrush(dark ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x5A, 0x5A, 0x5A)) }
                    };
                    card = new Grid { Children = { preview, badge } };
                }
                var cell = new StackPanel { Width = 232, Margin = new Thickness(4) };
                cell.Children.Add(card);
                cell.Children.Add(new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(4, 4, 0, 6), Foreground = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x20, 0x20, 0x20)) });
                row.Children.Add(cell);
            }
            row.Measure(new Size(row.Width, double.PositiveInfinity));
            row.Arrange(new Rect(row.DesiredSize));
            row.UpdateLayout();
            int w = (int)Math.Ceiling(row.ActualWidth * scale), h = (int)Math.Ceiling(row.ActualHeight * scale);
            var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(row);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(folder, dark ? "cards-dark.png" : "cards-light.png"));
            encoder.Save(file);
        }
        PanelTheme.Show(PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!));
        Console.WriteLine("Wrote the theme cards to " + Path.GetFullPath(folder));
        return 0;
    }
}
