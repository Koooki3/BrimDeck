using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Native;
using Path = System.Windows.Shapes.Path;

namespace BrimDeck;

// The clipboard page's full-content view, its header (back, position, previous and next; or the search box), typing (the search,
// phrases, category names) and its menus. Typing needs the keyboard, so the panel takes the focus while the user types and gives it
// back to the window it came from (ShelfService.TypingTarget), where a paste then goes.
public partial class MainWindow
{
    private bool _editCommitting;

    // ---- Header ----
    private void RenderShelfToolbar(double width)
    {
        ShelfToolbar.Children.Clear();
        ShelfToolbar.Visibility = Visibility.Visible;
        double right = _header.RightMargin + PanelHeader.SettingsWidth + 6;
        if (_shelfDetail is { } detail)
        {
            ClockLabel.Visibility = Visibility.Collapsed; ShelfSearchButton.Visibility = Visibility.Collapsed;
            var list = DetailList(detail.Side);
            int at = list.IndexOf(detail.Id);
            var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(_header.Side - 6, 0, 0, 0) };
            var back = ToolbarGlyph("M 5,0 L 0,5 L 5,10", Loc.T("返回", "Back"), CloseDetail);
            left.Children.Add(back);
            left.Children.Add(Label(DetailKind(detail.Side, detail.Id), 13, UI.Primary, HorizontalAlignment.Left, left: 4));
            if (at >= 0) left.Children.Add(Label($"{at + 1} / {list.Count}", 11.5, UI.Tertiary, HorizontalAlignment.Left, left: 8));
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, right, 0) };
            var up = ToolbarGlyph("M 0,5 L 5,0 L 10,5", Loc.T("上一条", "Previous"), () => StepDetail(-1));
            var down = ToolbarGlyph("M 0,0 L 5,5 L 10,0", Loc.T("下一条", "Next"), () => StepDetail(1));
            up.IsEnabled = at > 0; down.IsEnabled = at >= 0 && at < list.Count - 1;
            arrows.Children.Add(up); arrows.Children.Add(down);
            ShelfToolbar.Children.Add(left); ShelfToolbar.Children.Add(arrows);
            return;
        }
        if (_shelfSearching)
        {
            ShelfSearchButton.Visibility = Visibility.Collapsed;
            double boxWidth = ShelfLayout.SearchWidth(width), height = _header.SwitchHeight;
            var box = _searchBox ??= NewSearchBox();
            (box.Parent as Panel)?.Children.Remove(box);
            var host = new Grid { Width = boxWidth, Height = height, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, right, 0) };
            box.FontSize = 12.5; box.Padding = new Thickness(26, 0, 8, 0); box.VerticalContentAlignment = VerticalAlignment.Center;
            host.Children.Add(box);
            host.Children.Add(new Path { Data = Geometry.Parse("M 10.5,3.5 A 7,7 0 1 1 10.5,17.5 A 7,7 0 1 1 10.5,3.5 Z M 15.6,15.6 L 21,21"), Stroke = UI.Brush(UI.Tertiary), StrokeThickness = 2,
                Stretch = Stretch.Uniform, Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0), IsHitTestVisible = false });
            var hint = Label(Loc.T("搜索剪贴板与文件", "Search clipboard and files"), 12.5, UI.Tertiary, HorizontalAlignment.Left, left: 27);
            hint.IsHitTestVisible = false; hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; hint.Tag = "hint";
            host.Children.Add(hint);
            ShelfToolbar.Children.Add(host);
            // The clock is hidden when the box reaches it.
            bool overlap = width - right - boxWidth < width / 2 + 34;
            ClockLabel.Visibility = overlap ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        ShelfSearchButton.Visibility = Visibility.Visible;
        ClockLabel.Visibility = Visibility.Visible;
    }
    private Button ToolbarGlyph(string data, string name, Action click)
    {
        var path = new Path { Data = Geometry.Parse(data), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var button = new Button { Style = (Style)FindResource("PanelIconButton"), Width = 28, Height = _header.ButtonHeight, Content = path, ToolTip = name, Margin = new Thickness(1, 0, 0, 0) };
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : .35;
        AutomationProperties.SetName(button, name);
        button.Click += (_, e) => { e.Handled = true; click(); };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        return button;
    }
    private TextBox NewSearchBox()
    {
        var box = new TextBox { Style = (Style)FindResource("ShelfTextBox") };
        AutomationProperties.SetName(box, Loc.T("搜索剪贴板与文件", "Search clipboard and files"));
        box.TextChanged += (_, _) =>
        {
            _shelfQuery = box.Text;
            if (box.Parent is Grid host) foreach (var hint in host.Children.OfType<TextBlock>().Where(t => Equals(t.Tag, "hint"))) hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_shelfSearching && IsShelfPage) RenderShelf();
        };
        return box;
    }
    private void ShelfSearch_Click(object sender, RoutedEventArgs e)
    {
        _shelfSearching = true; _shelfDetail = null; _shelfQuery = _searchBox?.Text ?? "";
        RenderShelf();
        BeginTyping(_searchBox);
    }
    private void CloseSearch()
    {
        _shelfSearching = false; _shelfQuery = "";
        if (_searchBox is not null) _searchBox.Text = "";
        EndTyping();
        RenderShelf();
    }

    // Esc leaves the menu, the typing, the search or the details, in that order, before it closes the panel.
    internal bool ShelfEscape()
    {
        if (!_expanded || !IsShelfPage) return false;
        if (_shelfMenu?.IsOpen == true) { _shelfMenu.IsOpen = false; return true; }
        if (_shelfEdit is not null) { _shelfEdit = null; EndTyping(); RenderShelf(); return true; }
        if (_shelfSearching) { CloseSearch(); return true; }
        if (_shelfDetail is not null) { CloseDetail(); return true; }
        return false;
    }
    // Closing the panel ends the search, the typing and the details; the chosen categories stay.
    private void ResetShelf()
    {
        if (_shelfEdit is not null || _shelfSearching) EndTyping();
        _shelfEdit = null; _shelfDetail = null; _shelfSearching = false; _shelfQuery = ""; _picked.Clear();
        if (_searchBox is not null) _searchBox.Text = "";
        _shelfMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        if (IsShelfPage) _shelfStale = true;
    }

    // ---- Typing ----
    private void BeginTyping(Control? box)
    {
        if (box is null) return;
        var foreground = PasteInput.Foreground;
        if (PasteInput.IsOther(foreground)) _app.Shelf.TypingTarget = foreground;
        Activate();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { box.Focus(); Keyboard.Focus(box); if (box is TextBox text) text.CaretIndex = text.Text.Length; });
    }
    private void EndTyping()
    {
        if (IsActive && PasteInput.IsOther(_app.Shelf.TypingTarget)) PasteInput.Activate(_app.Shelf.TypingTarget);
    }
    private void RefocusTyping()
    {
        if (!IsActive) return;
        Control? box = _shelfEdit is { Kind: ShelfEditKind.NewPhrase or ShelfEditKind.EditText } ? _editBox : _shelfEdit is not null ? _nameBox : _shelfSearching ? _searchBox : null;
        if (box is not null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { if (box.IsVisible) { box.Focus(); Keyboard.Focus(box); } });
    }
    private void BeginEdit(ShelfEdit edit)
    {
        _shelfEdit = edit; _shelfDetail = null;
        if (edit.Kind is ShelfEditKind.NewPhrase or ShelfEditKind.EditText)
        {
            _editBox = NewEditBox();
            _editBox.Text = edit.Kind == ShelfEditKind.EditText ? ShelfData.Clips.FirstOrDefault(c => c.Id == edit.Id)?.Text ?? "" : "";
        }
        else
        {
            _nameBox = NewNameBox();
            _nameBox.Text = edit.Kind == ShelfEditKind.RenameCategory ? ShelfData.Categories(edit.Side).FirstOrDefault(c => c.Id == edit.Id)?.Name ?? "" : "";
            _nameBox.SelectAll();
        }
        RenderShelf();
        BeginTyping(edit.Kind is ShelfEditKind.NewPhrase or ShelfEditKind.EditText ? _editBox : _nameBox);
    }
    private TextBox NewNameBox()
    {
        var box = new TextBox { Style = (Style)FindResource("ShelfTextBox"), Width = 104, Height = 22, FontSize = 12, Padding = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 24 };
        AutomationProperties.SetName(box, Loc.T("分类名称", "Category name"));
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitName(); } };
        return box;
    }
    private FrameworkElement NameBox(ShelfEdit edit, string text, ShelfLayout layout)
    {
        var box = _nameBox ??= NewNameBox();
        (box.Parent as Panel)?.Children.Remove(box);
        box.Margin = new Thickness(2, 0, 2, 0);
        return box;
    }
    private void CommitName()
    {
        if (_shelfEdit is not { Kind: ShelfEditKind.NewCategory or ShelfEditKind.RenameCategory } edit || _nameBox is null) return;
        string name = _nameBox.Text.Trim();
        _editCommitting = true;
        _shelfEdit = null;
        if (name.Length > 0)
        {
            var categories = ShelfData.Categories(edit.Side);
            if (edit.Kind == ShelfEditKind.RenameCategory) { if (categories.FirstOrDefault(c => c.Id == edit.Id) is { } category) category.Name = name; }
            else
            {
                var swatches = PanelTheme.Current.Swatches.Take(8).Select(color => color.WithAlpha(255).ToString()).ToList();
                var category = new ShelfCategory { Name = name, Color = swatches.Count > 0 ? swatches[categories.Count % swatches.Count] : "#9CB9FF" };
                categories.Add(category);
                if (edit.ForItems is { Count: > 0 } items) Assign(edit.Side, items, category.Id);
            }
        }
        EndTyping();
        _editCommitting = false;
        _app.Shelf.Edited();
        RenderShelf();
    }
    private TextBox NewEditBox()
    {
        var box = new TextBox { Style = (Style)FindResource("ShelfTextBox"), AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6), VerticalContentAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(box, Loc.T("常用语内容", "Phrase text"));
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; CommitPhrase(); } };
        return box;
    }
    // A phrase is written here and kept until it is deleted; any text entry can be edited the same way. Ctrl+Enter saves, Esc cancels.
    private FrameworkElement PhraseEditor(ShelfEdit edit, ShelfLayout layout)
    {
        var box = _editBox ??= NewEditBox();
        (box.Parent as Panel)?.Children.Remove(box);
        box.FontSize = layout.Body;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(box);
        var bar = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string what = edit.Kind == ShelfEditKind.EditText ? Loc.T("编辑", "Edit") : Loc.T("新建常用语", "New phrase");
        bar.Children.Add(Label(what + Loc.T(" · Ctrl+Enter 保存 · Esc 取消", " · Ctrl+Enter saves · Esc cancels"), layout.Aux, UI.Tertiary, HorizontalAlignment.Left));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = PillButton(Loc.T("取消", "Cancel"), false, () => { _shelfEdit = null; EndTyping(); RenderShelf(); });
        var save = PillButton(Loc.T("保存", "Save"), true, CommitPhrase); save.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        Grid.SetColumn(buttons, 1); bar.Children.Add(buttons);
        Grid.SetRow(bar, 1); grid.Children.Add(bar);
        return grid;
    }
    private void CommitPhrase()
    {
        if (_shelfEdit is not { Kind: ShelfEditKind.NewPhrase or ShelfEditKind.EditText } edit || _editBox is null) return;
        string text = _editBox.Text;
        _editCommitting = true;
        _shelfEdit = null;
        if (text.Trim().Length > 0)
        {
            if (edit.Kind == ShelfEditKind.NewPhrase)
            {
                ShelfData.Clips.Insert(0, new ClipEntry { Kind = Shelf.Classify(text), Text = text, Manual = true, Copied = DateTimeOffset.Now, Category = edit.Category });
                if (edit.Category is not null) _clipTab = edit.Category;
                _clipOffset = 0;
            }
            else if (ShelfData.Clips.FirstOrDefault(c => c.Id == edit.Id) is { } clip) { clip.Text = text; clip.Kind = Shelf.Classify(text); clip.Html = ""; clip.Rtf = ""; }
        }
        EndTyping();
        _editCommitting = false;
        _app.Shelf.Edited();
        RenderShelf();
    }

    // ---- The full-content view ----
    private List<Guid> DetailList(ShelfSide side) => side == ShelfSide.Clipboard ? VisibleClips().Select(c => c.Id).ToList() : VisibleFiles().Select(f => f.Id).ToList();
    private void OpenDetail(ShelfSide side, Guid id) { CloseMenu(); _shelfDetail = (side, id); RenderShelf(); }
    private void CloseDetail() { _shelfDetail = null; RenderShelf(); }
    private void StepDetail(int step)
    {
        if (_shelfDetail is not { } detail) return;
        var list = DetailList(detail.Side);
        int at = list.IndexOf(detail.Id) + step;
        if (at < 0 || at >= list.Count) return;
        _shelfDetail = (detail.Side, list[at]); RenderShelf();
    }
    private string DetailKind(ShelfSide side, Guid id)
    {
        if (side == ShelfSide.Files) return Loc.T("文件详情", "File details");
        return (Find(side, id) as ClipEntry)?.Kind switch
        {
            ClipKind.Link => Loc.T("链接", "Link"), ClipKind.Image => Loc.T("图片", "Picture"), ClipKind.Code => Loc.T("代码", "Code"), ClipKind.Color => Loc.T("颜色", "Color"), _ => Loc.T("文本", "Text")
        };
    }

    private FrameworkElement DetailView(ShelfSide side, Guid id, double width, ShelfLayout layout)
    {
        bool wide = ShelfLayout.DetailSide(ContentCanvas.Width);
        var grid = new Grid();
        FrameworkElement content = side == ShelfSide.Clipboard ? ClipDetail((ClipEntry)Find(side, id)!, layout) : FileDetail((TransferFile)Find(side, id)!, layout);
        var facts = side == ShelfSide.Clipboard ? ClipFacts((ClipEntry)Find(side, id)!) : FileFacts((TransferFile)Find(side, id)!);
        var actions = side == ShelfSide.Clipboard ? ClipActions((ClipEntry)Find(side, id)!) : FileActions((TransferFile)Find(side, id)!);
        if (wide)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Gap / 2) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.Children.Add(content);
            var info = new DockPanel { LastChildFill = false };
            actions.Margin = new Thickness(0, 8, 0, 0);
            DockPanel.SetDock(actions, System.Windows.Controls.Dock.Bottom); info.Children.Add(actions);
            var list = new StackPanel();
            foreach (var (label, value, change) in facts)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, layout.Tier == ShelfTier.Compact ? 2 : 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(Label(label, layout.Aux, UI.Tertiary, HorizontalAlignment.Left));
                FrameworkElement shown = Label(value, layout.Aux, UI.Secondary, HorizontalAlignment.Left);
                if (change is not null) shown = LinkText(value, layout.Aux, change);
                Grid.SetColumn(shown, 1); row.Children.Add(shown);
                list.Children.Add(row);
            }
            DockPanel.SetDock(list, System.Windows.Controls.Dock.Top); info.Children.Add(list);
            Grid.SetColumn(info, 2); grid.Children.Add(info);
            return grid;
        }
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(content);
        var bottom = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition()); bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bool first = true;
        foreach (var (_, value, change) in facts)
        {
            if (value.Length == 0) continue;
            if (!first) line.Children.Add(Label("  ·  ", layout.Aux, UI.Tertiary, HorizontalAlignment.Left));
            line.Children.Add(change is not null ? LinkText(value, layout.Aux, change) : Label(value, layout.Aux, UI.Tertiary, HorizontalAlignment.Left));
            first = false;
        }
        bottom.Children.Add(line);
        Grid.SetColumn(actions, 1); bottom.Children.Add(actions);
        Grid.SetRow(bottom, 1); grid.Children.Add(bottom);
        return grid;
    }
    private TextBlock LinkText(string text, double size, Action<FrameworkElement> click)
    {
        var block = Label(text + " ⌄", size, UI.Secondary, HorizontalAlignment.Left);
        block.Cursor = Cursors.Hand;
        block.MouseEnter += (_, _) => block.Foreground = UI.Brush(UI.Primary);
        block.MouseLeave += (_, _) => block.Foreground = UI.Brush(UI.Secondary);
        block.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(block); };
        return block;
    }
    // A read-only text box, so the text can be selected; the whole entry is pasted with the button.
    private static TextBox ReadOnlyText(string text, double size, FontFamily font) => new()
    {
        Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = UI.Brush(UI.Primary), CaretBrush = Brushes.Transparent,
        FontFamily = font, FontSize = size, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), SelectionBrush = UI.Brush(UI.Accent), FocusVisualStyle = null,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private FrameworkElement ClipDetail(ClipEntry clip, ShelfLayout layout)
    {
        double size = layout.DetailBody;
        switch (clip.Kind)
        {
            case ClipKind.Image:
            {
                var image = new Image { Source = _app.Shelf.Picture(clip.Id), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                return new Border { Child = image, Margin = new Thickness(0, 2, 0, 2) };
            }
            case ClipKind.Color when Shelf.TryColor(clip.Text, out var c):
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                double swatch = Math.Min(96, Math.Max(48, ContentCanvas.Height - _header.Row - 60));
                panel.Children.Add(new Border { Width = swatch, Height = swatch, CornerRadius = new CornerRadius(PanelTheme.Corner(10)), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                    BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(1) });
                var forms = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var (hex, rgb, hsl) = Shelf.ColorForms(c);
                foreach (var (name, value) in new[] { ("HEX", hex), ("RGB", rgb), ("HSL", hsl) })
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = Loc.T("单击粘贴这种写法", "Click to paste this form") };
                    row.Children.Add(new TextBlock { Text = name, Width = 40, FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(UI.Tertiary), VerticalAlignment = VerticalAlignment.Center });
                    var text = new TextBlock { Text = value, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = size - 1, Foreground = UI.Brush(UI.Primary), VerticalAlignment = VerticalAlignment.Center };
                    row.Children.Add(text);
                    row.MouseEnter += (_, _) => text.Foreground = UI.Brush(UI.Accent);
                    row.MouseLeave += (_, _) => text.Foreground = UI.Brush(UI.Primary);
                    row.MouseLeftButtonUp += (_, e) => { e.Handled = true; _app.Shelf.Paste(ShelfService.TextData(value), done => text.Text = value + (done ? Loc.T("  已粘贴", "  pasted") : Loc.T("  已复制", "  copied"))); };
                    forms.Children.Add(row);
                }
                panel.Children.Add(forms);
                return panel;
            }
            case ClipKind.Link:
            {
                var (host, _) = Shelf.SplitLink(clip.Text);
                var panel = new StackPanel();
                panel.Children.Add(Label(host, size + 2, UI.Primary, HorizontalAlignment.Left));
                var url = ReadOnlyText(clip.Text.Trim(), size - 1, UI.PanelFont); url.Foreground = UI.Brush(UI.Secondary); url.Margin = new Thickness(0, 6, 0, 10);
                panel.Children.Add(url);
                var open = PillButton(Loc.T("在浏览器中打开", "Open in browser"), false, () => { if (Shelf.IsLink(clip.Text.Trim())) OpenFile(clip.Text.Trim()); });
                open.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(open);
                return new ScrollViewer { Content = panel, Focusable = false };
            }
            case ClipKind.Code:
            {
                var lines = clip.Text.Replace("\r\n", "\n").Split('\n');
                var font = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
                var numbers = new TextBlock { Text = string.Join("\n", Enumerable.Range(1, lines.Length)), FontFamily = font, FontSize = size - 1.5, Foreground = UI.Brush(UI.Tertiary),
                    TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 12, 0) };
                var code = ReadOnlyText(string.Join("\n", lines), size - 1.5, font); code.TextWrapping = TextWrapping.NoWrap;
                Grid.SetColumn(code, 1); grid.Children.Add(numbers); grid.Children.Add(code);
                return new ScrollViewer { Content = grid, Focusable = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            }
            default:
                return new ScrollViewer { Content = ReadOnlyText(clip.Text, size, UI.PanelFont), Focusable = false };
        }
    }
    private FrameworkElement FileDetail(TransferFile file, ShelfLayout layout)
    {
        bool exists = ShelfFiles.Exists(file.Path);
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Top };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        double icon = layout.Tier == ShelfTier.Compact ? 32 : 48;
        var image = new Image { Source = ShelfFiles.Icon(file.Path, large: true), Width = icon, Height = icon, VerticalAlignment = VerticalAlignment.Top, Opacity = exists ? 1 : .45, Margin = new Thickness(0, 2, 14, 0) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        grid.Children.Add(image);
        var text = new StackPanel();
        var name = ReadOnlyText(file.Name, layout.DetailBody + 1, UI.PanelFont); name.FontWeight = FontWeights.SemiBold; name.Opacity = exists ? 1 : .55;
        text.Children.Add(name);
        string type = ShelfFiles.TypeName(file.Path);
        text.Children.Add(Label(exists ? type : Loc.T("原文件已不存在（被移动或删除）", "The original is gone (moved or deleted)"), layout.Aux, exists ? UI.Secondary : "#E5C890", HorizontalAlignment.Left, top: 4));
        var folder = ReadOnlyText(System.IO.Path.GetDirectoryName(file.Path) ?? "", layout.Aux, UI.PanelFont); folder.Foreground = UI.Brush(UI.Tertiary); folder.Margin = new Thickness(0, 4, 0, 0);
        text.Children.Add(folder);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        return new ScrollViewer { Content = grid, Focusable = false };
    }

    private List<(string Label, string Value, Action<FrameworkElement>? Change)> ClipFacts(ClipEntry clip)
    {
        var facts = new List<(string, string, Action<FrameworkElement>?)>();
        facts.Add((Loc.T("来源", "Source"), clip.Manual ? Loc.T("常用语", "Phrase") : clip.Source.Length > 0 ? clip.Source : Loc.T("未知", "Unknown"), null));
        facts.Add((Loc.T("时间", "Time"), clip.Copied.ToLocalTime().ToString(Loc.T("M月d日 HH:mm", "MMM d, HH:mm"), CultureInfo.CurrentCulture), null));
        facts.Add(clip.Kind == ClipKind.Image ? (Loc.T("尺寸", "Size"), $"{clip.PixelWidth} × {clip.PixelHeight}", null)
            : (Loc.T("长度", "Length"), Loc.T($"{clip.Text.Length} 字 · {Shelf.LineCount(clip.Text)} 行", $"{clip.Text.Length} chars · {Shelf.LineCount(clip.Text)} lines"), null));
        facts.Add((Loc.T("分类", "Category"), CategoryName(ShelfSide.Clipboard, clip.Category), target => OpenMenu(CategoryChoices(ShelfSide.Clipboard, [clip.Id], clip.Category), target)));
        return facts;
    }
    private List<(string Label, string Value, Action<FrameworkElement>? Change)> FileFacts(TransferFile file)
    {
        bool exists = ShelfFiles.Exists(file.Path);
        string size = !exists ? Loc.T("已不存在", "Missing") : ShelfFiles.IsFolder(file.Path) ? FolderFacts(file.Path) : ShelfFiles.FileSize(file.Path) is { } bytes ? Shelf.Size(bytes) : "";
        var facts = new List<(string, string, Action<FrameworkElement>?)> { (Loc.T("大小", "Size"), size, null) };
        if (exists && ShelfFiles.Modified(file.Path) is { } modified) facts.Add((Loc.T("修改", "Modified"), modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), null));
        facts.Add((Loc.T("加入", "Added"), Shelf.When(file.Added, DateTimeOffset.Now), null));
        facts.Add((Loc.T("分类", "Category"), CategoryName(ShelfSide.Files, file.Category), target => OpenMenu(CategoryChoices(ShelfSide.Files, [file.Id], file.Category), target)));
        return facts;
    }
    private string CategoryName(ShelfSide side, Guid? id) => id is { } known && ShelfData.Categories(side).FirstOrDefault(c => c.Id == known) is { } category ? category.Name : Loc.T("无", "None");

    private StackPanel ClipActions(ClipEntry clip)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        panel.Children.Add(PillButton(Loc.T("粘贴", "Paste"), true, () => PasteClip(clip)));
        Button more = null!;
        more = PillButton("⋯", false, () =>
        {
            var menu = NewMenu();
            menu.Items.Add(Item(Loc.T("只复制，不粘贴", "Copy only"), () => CopyClip(clip)));
            if (clip.Kind != ClipKind.Image && (clip.Html.Length > 0 || clip.Rtf.Length > 0)) menu.Items.Add(Item(Loc.T("粘贴为纯文本", "Paste as plain text"), () => PasteClip(clip, plain: true)));
            if (clip.Kind != ClipKind.Image) menu.Items.Add(Item(Loc.T("编辑…", "Edit…"), () => BeginEdit(new ShelfEdit(ShelfEditKind.EditText, ShelfSide.Clipboard, clip.Id))));
            menu.Items.Add(Submenu(Loc.T("加入分类", "Add to category"), CategoryItems(ShelfSide.Clipboard, [clip.Id], clip.Category)));
            menu.Items.Add(Separator());
            menu.Items.Add(Item(Loc.T("删除", "Delete"), () => DeleteClips([clip]), danger: true));
            OpenMenu(menu, more);
        });
        more.Margin = new Thickness(6, 0, 0, 0);
        panel.Children.Add(more);
        return panel;
    }
    private StackPanel FileActions(TransferFile file)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        if (!ShelfFiles.Exists(file.Path)) { panel.Children.Add(PillButton(Loc.T("从文件中转移除", "Remove"), false, () => RemoveFiles([file]))); return panel; }
        panel.Children.Add(PillButton(Loc.T("打开", "Open"), true, () => OpenFile(file.Path)));
        var show = PillButton(Loc.T("在文件夹中显示", "Show in folder"), false, () => ShowInFolder(file.Path)); show.Margin = new Thickness(6, 0, 0, 0);
        panel.Children.Add(show);
        Button more = null!;
        more = PillButton("⋯", false, () =>
        {
            var menu = NewMenu();
            menu.Items.Add(Item(Loc.T("粘贴到当前窗口", "Paste into the current window"), () => PasteFiles([file])));
            menu.Items.Add(Item(Loc.T("复制路径", "Copy path"), () => { if (_app.Shelf.Put(ShelfService.TextData(file.Path))) Flash(file.Id, Loc.T("已复制路径", "Path copied")); }));
            menu.Items.Add(Submenu(Loc.T("加入分类", "Add to category"), CategoryItems(ShelfSide.Files, [file.Id], file.Category)));
            menu.Items.Add(Separator());
            menu.Items.Add(Item(Loc.T("从文件中转移除", "Remove"), () => RemoveFiles([file]), hint: Loc.T("原文件保留", "Original kept"), danger: true));
            OpenMenu(menu, more);
        });
        more.Margin = new Thickness(6, 0, 0, 0);
        panel.Children.Add(more);
        return panel;
    }

    private void DeleteClips(IReadOnlyList<ClipEntry> clips)
    {
        if (_shelfDetail is { Side: ShelfSide.Clipboard } detail && clips.Any(c => c.Id == detail.Id)) _shelfDetail = null;
        _app.Shelf.Remove(clips);
    }
    private void RemoveFiles(IReadOnlyList<TransferFile> files)
    {
        if (_shelfDetail is { Side: ShelfSide.Files } detail && files.Any(f => f.Id == detail.Id)) _shelfDetail = null;
        foreach (var file in files) _picked.Remove(file.Id);
        _app.Shelf.RemoveFiles(files);
    }

    // ---- Menus ----
    private ContextMenu NewMenu() => new() { Style = (Style)FindResource("ShelfMenu") };
    private MenuItem Item(string label, Action act, string? hint = null, bool danger = false, bool check = false, bool enabled = true, string? dot = null)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot is not null) header.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = UI.Brush(dot), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = label, Foreground = UI.Brush(danger ? PanelTheme.Hex("danger") : UI.Primary), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var item = new MenuItem { Style = (Style)FindResource("ShelfMenuItem"), Header = header, InputGestureText = hint ?? "", IsChecked = check, IsEnabled = enabled };
        AutomationProperties.SetName(item, label);
        item.Click += (_, e) => { e.Handled = true; CloseMenu(); act(); };
        return item;
    }
    private MenuItem Submenu(string label, IEnumerable<object> items)
    {
        var item = new MenuItem { Style = (Style)FindResource("ShelfMenuItem"), Header = new TextBlock { Text = label, Foreground = UI.Brush(UI.Primary), VerticalAlignment = VerticalAlignment.Center } };
        AutomationProperties.SetName(item, label);
        foreach (var child in items) item.Items.Add(child);
        return item;
    }
    private Separator Separator() => new() { Style = (Style)FindResource("ShelfMenuSeparator") };
    private void CloseMenu() { if (_shelfMenu is { IsOpen: true } menu) menu.IsOpen = false; }
    private void OpenMenu(ContextMenu menu, FrameworkElement target, bool atPointer = false)
    {
        CloseMenu();
        _shelfMenu = menu;
        menu.PlacementTarget = target;
        menu.Placement = atPointer ? PlacementMode.MousePoint : PlacementMode.Bottom;
        if (!atPointer) menu.VerticalOffset = 4 * _scale;
        UI.Scale(menu, _scale);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_shelfMenu, menu)) _shelfMenu = null;
            Dispatcher.BeginInvoke(() => { if (_expanded && !_pointerInside && !ShelfHolds) PointerChanged(false); });
        };
        menu.IsOpen = true;
    }

    private ContextMenu ClipMenu(ClipEntry clip)
    {
        var menu = NewMenu();
        bool textual = clip.Kind != ClipKind.Image;
        menu.Items.Add(Item(Loc.T("粘贴", "Paste"), () => PasteClip(clip), hint: Loc.T("单击", "Click")));
        if (textual && (clip.Html.Length > 0 || clip.Rtf.Length > 0)) menu.Items.Add(Item(Loc.T("粘贴为纯文本", "Paste as plain text"), () => PasteClip(clip, plain: true)));
        menu.Items.Add(Item(Loc.T("只复制，不粘贴", "Copy only"), () => CopyClip(clip)));
        menu.Items.Add(Item(Loc.T("查看完整内容", "Show all"), () => OpenDetail(ShelfSide.Clipboard, clip.Id), hint: "›"));
        if (textual) menu.Items.Add(Item(Loc.T("编辑…", "Edit…"), () => BeginEdit(new ShelfEdit(ShelfEditKind.EditText, ShelfSide.Clipboard, clip.Id))));
        menu.Items.Add(Separator());
        menu.Items.Add(Submenu(Loc.T("加入分类", "Add to category"), CategoryItems(ShelfSide.Clipboard, [clip.Id], clip.Category)));
        menu.Items.Add(Separator());
        menu.Items.Add(Item(Loc.T("删除", "Delete"), () => DeleteClips([clip]), danger: true));
        return menu;
    }
    private ContextMenu FileMenu(TransferFile file)
    {
        var menu = NewMenu();
        if (!ShelfFiles.Exists(file.Path))
        {
            menu.Items.Add(Item(Loc.T("查看详情", "Details"), () => OpenDetail(ShelfSide.Files, file.Id), hint: "›"));
            menu.Items.Add(Submenu(Loc.T("加入分类", "Add to category"), CategoryItems(ShelfSide.Files, [file.Id], file.Category)));
            menu.Items.Add(Separator());
            menu.Items.Add(Item(Loc.T("从文件中转移除", "Remove"), () => RemoveFiles([file]), danger: true));
            return menu;
        }
        var files = _picked.Contains(file.Id) && _picked.Count > 1 ? ShelfData.Files.Where(f => _picked.Contains(f.Id)).ToList() : [file];
        bool many = files.Count > 1;
        menu.Items.Add(Item(many ? Loc.T($"粘贴所选 {files.Count} 项到当前窗口", $"Paste {files.Count} files into the current window") : Loc.T("粘贴到当前窗口", "Paste into the current window"),
            () => PasteFiles(files), hint: many ? null : Loc.T("单击", "Click")));
        if (!many)
        {
            menu.Items.Add(Item(Loc.T("打开", "Open"), () => OpenFile(file.Path)));
            menu.Items.Add(Item(Loc.T("在文件夹中显示", "Show in folder"), () => ShowInFolder(file.Path)));
            menu.Items.Add(Item(Loc.T("复制路径", "Copy path"), () => { if (_app.Shelf.Put(ShelfService.TextData(file.Path))) Flash(file.Id, Loc.T("已复制路径", "Path copied")); }));
            menu.Items.Add(Item(Loc.T("查看详情", "Details"), () => OpenDetail(ShelfSide.Files, file.Id), hint: "›"));
        }
        menu.Items.Add(Separator());
        menu.Items.Add(Submenu(Loc.T("加入分类", "Add to category"), CategoryItems(ShelfSide.Files, files.Select(f => f.Id).ToList(), many ? null : file.Category)));
        menu.Items.Add(Separator());
        menu.Items.Add(Item(many ? Loc.T($"从文件中转移除 {files.Count} 项", $"Remove {files.Count} files") : Loc.T("从文件中转移除", "Remove"), () => RemoveFiles(files), hint: Loc.T("原文件保留", "Original kept"), danger: true));
        return menu;
    }
    // The categories to put entries in, the one they are in checked; then taking them out, and a new category for them.
    private List<object> CategoryItems(ShelfSide side, IReadOnlyList<Guid> ids, Guid? current)
    {
        var items = new List<object>();
        foreach (var category in ShelfData.Categories(side))
            items.Add(Item(category.Name, () => Assign(side, ids, category.Id), check: current == category.Id, dot: category.Color));
        if (items.Count > 0) items.Add(Separator());
        if (current is not null) items.Add(Item(Loc.T("移出分类", "Remove from category"), () => Assign(side, ids, null)));
        items.Add(Item(Loc.T("新建分类…", "New category…"), () => BeginEdit(new ShelfEdit(ShelfEditKind.NewCategory, side, ForItems: ids))));
        return items;
    }
    private ContextMenu CategoryChoices(ShelfSide side, IReadOnlyList<Guid> ids, Guid? current)
    {
        var menu = NewMenu();
        foreach (var item in CategoryItems(side, ids, current)) menu.Items.Add(item);
        return menu;
    }
    private ContextMenu CategoryMenu(ShelfSide side, ShelfCategory category)
    {
        var menu = NewMenu();
        if (side == ShelfSide.Clipboard)
        {
            menu.Items.Add(Item(Loc.T("在这个分类中新建常用语…", "New phrase in this category…"), () => BeginEdit(new ShelfEdit(ShelfEditKind.NewPhrase, side, Category: category.Id))));
            menu.Items.Add(Separator());
        }
        menu.Items.Add(Item(Loc.T("重命名", "Rename"), () => BeginEdit(new ShelfEdit(ShelfEditKind.RenameCategory, side, category.Id))));
        var colors = PanelTheme.Current.Swatches.Take(8).Select(color => color.WithAlpha(255).ToString()).ToList();
        menu.Items.Add(Submenu(Loc.T("颜色", "Color"), colors.Select(color => (object)Item(color, () => { category.Color = color; _app.Shelf.Edited(); }, check: string.Equals(category.Color, color, StringComparison.OrdinalIgnoreCase), dot: color))));
        menu.Items.Add(Separator());
        menu.Items.Add(Item(Loc.T("删除分类", "Delete category"), () =>
        {
            Shelf.DeleteCategory(ShelfData, side, category.Id);
            if (side == ShelfSide.Clipboard && _clipTab == category.Id) _clipTab = null;
            if (side == ShelfSide.Files && _fileTab == category.Id) _fileTab = null;
            _app.Shelf.Edited();
        }, hint: Loc.T("内容回到全部", "Entries stay"), danger: true));
        return menu;
    }
    private ContextMenu CategoryListMenu(ShelfSide side)
    {
        var menu = NewMenu();
        var selected = side == ShelfSide.Clipboard ? _clipTab : _fileTab;
        int Count(Guid? id) => side == ShelfSide.Clipboard ? ShelfData.Clips.Count(c => id is null || c.Category == id) : ShelfData.Files.Count(f => id is null || f.Category == id);
        void Choose(Guid? id) { if (side == ShelfSide.Clipboard) _clipTab = id; else { _fileTab = id; _picked.Clear(); } RenderShelf(); }
        menu.Items.Add(Item(side == ShelfSide.Clipboard ? Loc.T("全部记录", "All entries") : Loc.T("全部文件", "All files"), () => Choose(null), hint: Count(null).ToString(CultureInfo.InvariantCulture), check: selected is null));
        foreach (var category in ShelfData.Categories(side))
            menu.Items.Add(Item(category.Name, () => Choose(category.Id), hint: Count(category.Id).ToString(CultureInfo.InvariantCulture), check: selected == category.Id, dot: category.Color));
        menu.Items.Add(Separator());
        if (side == ShelfSide.Clipboard) menu.Items.Add(Item(Loc.T("新建常用语…", "New phrase…"), () => BeginEdit(new ShelfEdit(ShelfEditKind.NewPhrase, side, Category: _clipTab))));
        menu.Items.Add(Item(Loc.T("新建分类…", "New category…"), () => BeginEdit(new ShelfEdit(ShelfEditKind.NewCategory, side))));
        if (selected is { } chosen && ShelfData.Categories(side).FirstOrDefault(c => c.Id == chosen) is { } current)
        {
            menu.Items.Add(Separator());
            foreach (var item in CategoryMenu(side, current).Items.Cast<object>().ToList()) { CategoryMenuDetach(item); menu.Items.Add(item); }
        }
        return menu;
    }
    private static void CategoryMenuDetach(object item) { if (item is FrameworkElement { Parent: ItemsControl owner } element) owner.Items.Remove(element); }
    private ContextMenu ClipMoreMenu()
    {
        var menu = NewMenu();
        menu.Items.Add(Item(Loc.T("新建常用语…", "New phrase…"), () => BeginEdit(new ShelfEdit(ShelfEditKind.NewPhrase, ShelfSide.Clipboard, Category: _clipTab))));
        menu.Items.Add(Separator());
        if (!Settings.ClipboardRecord) menu.Items.Add(Item(Loc.T("开启记录", "Turn on recording"), () => _app.UpdateSettings(SettingsWith(s => s.ClipboardRecord = true))));
        else menu.Items.Add(Item(_app.Shelf.Paused ? Loc.T("继续记录", "Resume recording") : Loc.T("暂停记录", "Pause recording"), () => _app.Shelf.SetPaused(!_app.Shelf.Paused)));
        menu.Items.Add(Item(Loc.T("清除历史记录", "Clear history"), () => _app.Shelf.ClearHistory(), hint: Loc.T("分类和常用语保留", "Categories and phrases stay"), danger: true,
            enabled: Shelf.History(ShelfData).Count > 0));
        menu.Items.Add(Separator());
        menu.Items.Add(Item(Loc.T("剪贴板设置…", "Clipboard settings…"), () => _app.OpenExpandedSettings()));
        return menu;
    }
    private ContextMenu FileMoreMenu()
    {
        var menu = NewMenu();
        menu.Items.Add(Item(Loc.T("全部移除", "Remove all"), () => RemoveFiles([.. ShelfData.Files]), hint: Loc.T("原文件保留", "Originals kept"), danger: true, enabled: ShelfData.Files.Count > 0));
        menu.Items.Add(Separator());
        menu.Items.Add(Item(Loc.T("文件中转设置…", "File transfer settings…"), () => _app.OpenExpandedSettings()));
        return menu;
    }
}
