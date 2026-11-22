using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using BrimDeck.Core;

namespace BrimDeck;

// The windows that send and receive files with WeType, laid out like a share sheet: a small title with a light close button,
// then the subject in the middle (the file's own large picture, or the other device as a round avatar) with its name and size
// under it, then what is happening now. A finished or failed transfer puts a green or red badge on the subject. Buttons span
// the width, side by side, and appear only when the next step is the user's. Icons come from Windows' own icon font.
internal abstract class FileTransferDialog : Window
{
    internal enum Badge { None, Done, Failed }

    protected const string PhoneGlyph = "", LaptopGlyph = "", DesktopGlyph = "", QrGlyph = "",
        DownloadGlyph = "", FolderGlyph = "";
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    protected readonly SettingsPalette Palette;
    // The part that changes with the state, under the subject.
    protected readonly StackPanel Stage = new();
    private readonly ContentControl _hero = new() { Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch };

    protected FileTransferDialog(SettingsTheme theme, string title)
    {
        Palette = new SettingsPalette(SettingsPalette.IsDark(theme));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/SettingsResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/NeteaseControlResources.xaml", UriKind.Relative) });
        Palette.Apply(Resources); Style = (Style)FindResource(typeof(Window));
        Title = title; Width = 360; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true;
        FontFamily = UI.PanelFont; Background = UI.Brush(Palette.Surface); Foreground = UI.Brush(Palette.Primary);
        Native.SettingsChrome.Apply(this); ResizeMode = ResizeMode.NoResize;
        SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(this, Palette.Dark);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Content = Frame(title);
    }

    private FrameworkElement Frame(string title)
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = UI.Line(title, 13, Palette.Secondary); heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(20, 16, 52, 0); heading.TextTrimming = TextTrimming.CharacterEllipsis;
        layout.Children.Add(heading);
        layout.Children.Add(CloseButton());
        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 20) };
        body.Children.Add(_hero);
        Stage.Margin = new Thickness(0, 18, 0, 0);
        body.Children.Add(Stage);
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var frame = new Border { BorderThickness = new Thickness(1), Child = layout };
        frame.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
        return frame;
    }

    // A light close button: the cross alone, a soft fill under the pointer; Esc closes too.
    private Button CloseButton()
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var glyph = new FrameworkElementFactory(typeof(TextBlock), "Glyph");
        glyph.SetValue(TextBlock.TextProperty, "");
        glyph.SetValue(TextBlock.FontFamilyProperty, Icons);
        glyph.SetValue(TextBlock.FontSizeProperty, 10.0);
        glyph.SetValue(TextBlock.ForegroundProperty, UI.Brush(Palette.Secondary));
        glyph.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(glyph);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SettingsFillHover"), "Chrome"));
        hover.Setters.Add(new Setter(TextBlock.ForegroundProperty, UI.Brush(Palette.Primary), "Glyph"));
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SettingsFillPressed"), "Chrome"));
        template.Triggers.Add(hover); template.Triggers.Add(pressed);
        var close = new Button { Template = template, Width = 28, Height = 28, Margin = new Thickness(0, 10, 10, 0), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsCancel = true };
        close.Click += (_, _) => Close();
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        AutomationProperties.SetName(close, Loc.T("关闭", "Close"));
        return close;
    }

    protected static TextBlock Glyph(string glyph, double size, string color) => new()
    {
        Text = glyph, FontFamily = Icons, FontSize = size, Foreground = UI.Brush(color),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    protected TextBlock Centered(string text, double size, string color, bool semibold = false, int lines = 2)
    {
        var line = UI.Line(text, size, color);
        line.TextAlignment = TextAlignment.Center; line.TextWrapping = TextWrapping.Wrap; line.TextTrimming = TextTrimming.CharacterEllipsis;
        line.LineHeight = Math.Round(size * 1.45); line.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        line.MaxHeight = line.LineHeight * lines + 1;
        if (semibold) line.FontWeight = FontWeights.SemiBold;
        return line;
    }

    // ---- The subject ----

    // The middle of the window: a picture with its name and a line under it.
    protected void Hero(FrameworkElement picture, string name, string? meta, Badge badge = Badge.None)
    {
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var holder = new Grid { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center };
        holder.Children.Add(picture);
        if (badge != Badge.None)
        {
            // A disc on the picture's lower right corner, ringed in the surface color so it stands off the picture.
            var disc = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2.5),
                Background = UI.Brush(badge == Badge.Done ? Palette.Positive : Palette.Negative), BorderBrush = UI.Brush(Palette.Surface),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -4),
                Child = Glyph(badge == Badge.Done ? "" : "", badge == Badge.Done ? 11 : 8.5, Palette.Dark ? "#141416" : "#FFFFFF") };
            holder.Children.Add(disc);
        }
        hero.Children.Add(holder);
        var title = Centered(name, 14, Palette.Primary, true); title.Margin = new Thickness(0, 12, 0, 0); title.ToolTip = name;
        hero.Children.Add(title);
        if (meta != null)
        {
            var under = Centered(meta, 12, Palette.Tertiary, lines: 1); under.Margin = new Thickness(0, 3, 0, 0);
            hero.Children.Add(under);
        }
        _hero.Content = hero;
    }

    // The file's own large picture (a thumbnail for pictures), at 64 units; with several files, a count on its corner.
    protected FrameworkElement FilePicture(string path, int count = 1)
    {
        var grid = new Grid { Width = 72, Height = 72 };
        var image = new Image { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        void Load()
        {
            if (ShelfFiles.Large(path, 128) is { } large) image.Source = large;
            else if (image.Source == null && ShelfFiles.Icon(path) is { } small) image.Source = small;
        }
        Load();
        void Arrived() => Dispatcher.InvokeAsync(Load);
        ShelfFiles.Changed += Arrived;
        Closed += (_, _) => ShelfFiles.Changed -= Arrived;
        grid.Children.Add(image);
        if (count > 1)
        {
            var pill = new Border { MinWidth = 24, Height = 22, CornerRadius = new CornerRadius(11), Padding = new Thickness(6, 0, 6, 0),
                BorderThickness = new Thickness(2), BorderBrush = UI.Brush(Palette.Surface), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -6, 0) };
            pill.SetResourceReference(Border.BackgroundProperty, "SettingsAccentFill");
            var number = UI.Line(count.ToString(System.Globalization.CultureInfo.InvariantCulture), 11, "#FFFFFF"); number.FontWeight = FontWeights.SemiBold;
            number.HorizontalAlignment = HorizontalAlignment.Center; number.VerticalAlignment = VerticalAlignment.Center;
            pill.Child = number;
            grid.Children.Add(pill);
        }
        return grid;
    }

    // Another device, or this one: its glyph in a round avatar.
    protected FrameworkElement Avatar(string glyph, double size = 64)
    {
        var disc = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        disc.SetResourceReference(Border.BackgroundProperty, "SettingsAccentTint");
        disc.Child = Glyph(glyph, Math.Round(size * 0.42), Palette.Accent);
        return disc;
    }

    // ---- The state ----

    protected void Clear() => Stage.Children.Clear();

    protected TextBlock Say(string text, string? color = null, double size = 12.5)
    {
        var line = Centered(text, size, color ?? Palette.Secondary, lines: 3);
        if (Stage.Children.Count > 0) line.Margin = new Thickness(0, 6, 0, 0);
        Stage.Children.Add(line);
        return line;
    }

    // A thin bar with what is happening on the left and how far on the right under it.
    protected void Progress(string label, double share)
    {
        share = Math.Clamp(share, 0, 1);
        var track = new Grid { Height = 6 };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = UI.Brush(Palette.Inset) });
        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, "SettingsAccent");
        track.SizeChanged += (_, e) => fill.Width = Math.Max(share > 0 ? 6 : 0, e.NewSize.Width * share);
        track.Children.Add(fill);
        Stage.Children.Add(track);
        var under = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        under.ColumnDefinitions.Add(new ColumnDefinition());
        under.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var what = UI.Line(label, 12, Palette.Secondary); what.TextTrimming = TextTrimming.CharacterEllipsis; what.Margin = new Thickness(0, 0, 12, 0);
        var much = UI.Line(Math.Round(share * 100) + "%", 12, Palette.Primary); much.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(much, 1);
        under.Children.Add(what); under.Children.Add(much);
        Stage.Children.Add(under);
        AutomationProperties.SetName(track, label + " " + much.Text);
    }

    // Where the files go: a folder glyph and the folder's name, the full path on hover.
    protected void Where(string folder)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0),
            ToolTip = folder, Background = Brushes.Transparent };
        row.Children.Add(new Border { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), Child = Glyph(FolderGlyph, 13, Palette.Tertiary) });
        string name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
        var text = UI.Line(name.Length > 0 ? name : folder, 12, Palette.Tertiary); text.TextTrimming = TextTrimming.CharacterEllipsis; text.MaxWidth = 280;
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        Stage.Children.Add(row);
    }

    // The buttons of a state, sharing the width; the primary one last.
    protected Button[] Buttons(params (string Label, bool Primary, Action Click)[] buttons)
    {
        var row = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        var made = new Button[buttons.Length];
        for (int i = 0; i < buttons.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var (label, primary, click) = buttons[i];
            var button = new Button { Content = label, Style = (Style)FindResource(primary ? "ControlDialogPrimaryButton" : "ControlDialogButton"),
                MinWidth = 0, Margin = new Thickness(i == 0 ? 0 : 5, 0, i == buttons.Length - 1 ? 0 : 5, 0) };
            button.Click += (_, _) => click();
            Grid.SetColumn(button, i); row.Children.Add(button);
            made[i] = button;
        }
        Stage.Children.Add(row);
        return made;
    }

    // Choices as tiles: a round avatar with the name under it, three to a row.
    protected void Tiles(string heading, IEnumerable<(string Glyph, string Label, Action Click)> tiles)
    {
        var caption = Centered(heading, 12, Palette.Tertiary, lines: 1); caption.Margin = new Thickness(0, 0, 0, 8);
        Stage.Children.Add(caption);
        var list = tiles.ToList();
        // Three to a row; fewer stay centred at the same width.
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = Math.Clamp(list.Count, 1, 3), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (glyph, label, click) in list)
        {
            var content = new StackPanel();
            var disc = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), HorizontalAlignment = HorizontalAlignment.Center,
                Child = Glyph(glyph, 22, Palette.Primary) };
            disc.SetResourceReference(Border.BackgroundProperty, "SettingsInset");
            content.Children.Add(disc);
            // Two lines kept for every name, so the discs line up whatever the names' lengths.
            var name = Centered(label, 12, Palette.Primary); name.Margin = new Thickness(0, 7, 0, 0); name.Height = name.MaxHeight;
            content.Children.Add(name);
            var tile = new Button { Content = content, Style = (Style)FindResource("SettingsNav"), Height = double.NaN, Padding = new Thickness(4, 10, 4, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(2), Width = 102 };
            AutomationProperties.SetName(tile, label);
            tile.Click += (_, _) => click();
            grid.Children.Add(tile);
        }
        Stage.Children.Add(grid);
    }

    // The QR code on its own white card, so any phone reads it in the dark theme too.
    protected void QrCard(ImageSource code)
    {
        var image = new Image { Source = code, Width = 168, Height = 168 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Stage.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center, Child = image });
    }
}
