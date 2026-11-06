using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using Path = System.Windows.Shapes.Path;

namespace BrimDeck;

// The clipboard and file transfer page: the clipboard at the left, the file transfer at the right, both lists at once. A click on a
// row pastes it into the window the user is typing in (the panel takes no focus, so that window keeps it); "›" or the details show
// the whole entry in place of the page (MainWindow.ShelfDetail.cs). The lists, categories and files are shared by every panel
// (ShelfService); what each panel shows (the chosen category, the search, the details) is its own.
public partial class MainWindow
{
    private Guid? _clipTab, _fileTab;
    private string _shelfQuery = "";
    private bool _shelfSearching;
    private (ShelfSide Side, Guid Id)? _shelfDetail;
    private ShelfEdit? _shelfEdit;
    private readonly HashSet<Guid> _picked = [];
    private Guid? _pickAnchor;
    // The row that was just pasted: its time reads "已粘贴" on a blue background for a moment.
    private (Guid Id, string Text, long Until)? _flash;
    private DispatcherTimer? _flashTimer;
    // Files dragged over the panel (ShelfDragEnter): the page shown before, to go back to if they are not dropped.
    private bool _dropping, _dropOpened, _draggingOut;
    private DeckPage? _dropReturn;
    private DispatcherTimer? _dropLeave;
    private ContextMenu? _shelfMenu;
    private TextBox? _searchBox, _editBox, _nameBox;
    private double _clipOffset, _fileOffset;
    private Border? _fileFrame;
    private readonly List<(Border Tab, Guid? Id, ShelfSide Side)> _shelfTabs = [];
    private readonly Dictionary<Guid, (Border Row, Grid Aux)> _shelfRows = [];
    private ShelfLayout _shelfLayout = ShelfLayout.For(ShelfSizes.Default.Width, ShelfSizes.Default.Height);
    private int _transferShown = -1;

    private enum ShelfEditKind { NewPhrase, EditText, NewCategory, RenameCategory }
    private sealed record ShelfEdit(ShelfEditKind Kind, ShelfSide Side, Guid? Id = null, Guid? Category = null, IReadOnlyList<Guid>? ForItems = null);

    // The details replace the page switch and the clock with their own header.
    internal bool ShelfHidesSwitch => _shelfDetail is not null;
    // The panel stays open while a menu of the page is open, while the user types in it, and while files are dragged over it.
    internal bool ShelfHolds => _shelfMenu?.IsOpen == true || _shelfEdit is not null || _dropping || _draggingOut || _searchBox?.IsKeyboardFocusWithin == true;
    private ShelfData ShelfData => _app.Shelf.Data;

    private void InitializeShelf()
    {
        Action changed = ShelfChanged;
        _app.Shelf.Changed += changed;
        ShelfFiles.Changed += changed;
        Closed += (_, _) => { _app.Shelf.Changed -= changed; ShelfFiles.Changed -= changed; };
        HoverZone.AllowDrop = true;
        HoverZone.PreviewDragEnter += ShelfDragEnter;
        HoverZone.PreviewDragOver += ShelfDragOver;
        HoverZone.PreviewDragLeave += ShelfDragLeave;
        HoverZone.PreviewDrop += ShelfDrop;
        // Typing ends when the user goes to another window; the panel then closes as usual once the pointer is away.
        Deactivated += (_, _) =>
        {
            if (_shelfEdit is not null && !_editCommitting) { _shelfEdit = null; if (IsShelfPage) RenderShelf(); }
            if (_expanded && !_pointerInside && !ShelfHolds) PointerChanged(false);
        };
    }

    // A copy or a dropped file while the panel is closed is shown when it opens (ApplyDefaultMusicPage), not built at every copy.
    private bool _shelfStale;
    private void ShelfChanged()
    {
        if (IsShelfPage && !_preview) { if (_expanded) RenderShelf(); else _shelfStale = true; }
        int count = Settings.ShowsTransfer(EffectiveStyle) ? ShelfData.Files.Count : 0;
        if (count != _transferShown && !_expanded) RenderCompact();
    }

    // ---- The collapsed count ----
    // The width the count keeps: the tray and "9+", so the surface does not change width as files come and go (CompactMusicLayout).
    private static double FixedFace(double clock, double transfer, double scale)
        => clock > 0 && transfer > 0 ? clock + CompactMusicLayout.Gap(scale) + transfer : clock + transfer;
    private FrameworkElement? _compactTransfer;
    private FrameworkElement? TransferBadge(double scale)
    {
        int count = Settings.ShowsTransfer(EffectiveStyle) ? ShelfData.Files.Count : 0;
        _transferShown = count; _compactTransfer = null;
        if (count == 0) return null;
        double s = CompactMusicLayout.SafeScale(scale), icon = CompactMusicLayout.Whole(14 * s);
        var badge = new Grid { Width = CompactMusicLayout.Whole(32 * s), Height = CompactMusicLayout.Whole(16 * s), Background = Brushes.Transparent };
        var tray = new Path
        {
            Data = Geometry.Parse("M 3,13 L 6,5 H 18 L 21,13 V 19 A 1,1 0 0 1 20,20 H 4 A 1,1 0 0 1 3,19 Z M 3,13 H 8.5 L 10,15.5 H 14 L 15.5,13 H 21"),
            Stroke = UI.Brush(UI.Secondary), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform, Width = icon, Height = icon,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        };
        var number = new TextBlock
        {
            Text = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture), FontFamily = UI.PanelFont, FontSize = CompactMusicLayout.Half(11.5 * s), FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(UI.Primary), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(icon + CompactMusicLayout.Whole(3 * s), 0, 0, 0)
        };
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        badge.Children.Add(tray); badge.Children.Add(number);
        string tip = Loc.T($"文件中转：{count} 个文件", count == 1 ? "File transfer: 1 file" : $"File transfer: {count} files");
        badge.ToolTip = tip; AutomationProperties.SetName(badge, tip);
        _compactTransfer = badge;
        return badge;
    }
    // Opened while the pointer is on the count, the panel opens on this page.
    private bool PointerOnTransfer()
    {
        if (_compactTransfer is not { IsVisible: true } badge || PresentationSource.FromVisual(badge) is null) return false;
        var rect = new Rect(badge.PointToScreen(new Point()), badge.PointToScreen(new Point(badge.ActualWidth, badge.ActualHeight)));
        rect.Inflate(4, 6);
        return rect.Contains(WindowsHost.Cursor());
    }

    // ---- The page ----
    private void RenderShelf()
    {
        // Built while the panel is closed as well: the panel opens on what was built last.
        _shelfStale = false;
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed;
        ShelfContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        var layout = _shelfLayout = ShelfLayout.For(width, height);
        var header = PanelHeader.For(ToolbarFor(height), layout.Side);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        ShelfContent.Margin = new Thickness(layout.Side, 0, layout.Side, layout.Tier == ShelfTier.Compact ? 8 : 12);
        // The pointer may have left a row that is about to be replaced; its scroll positions are kept.
        RememberShelfScroll();
        ShelfContent.Children.Clear(); _shelfRows.Clear(); _shelfTabs.Clear(); _fileFrame = null;
        RenderShelfToolbar(width);
        if (_shelfDetail is { } detail && Find(detail.Side, detail.Id) is not null) { ShelfContent.Children.Add(DetailView(detail.Side, detail.Id, width - 2 * layout.Side, layout)); return; }
        _shelfDetail = null;
        RenderPageSwitch();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.FileColumn) });
        var clips = ClipColumn(layout);
        var divider = new Border { Width = 1, Background = UI.Brush(UI.Hairline), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        Grid.SetColumn(divider, 1);
        var files = FileColumn(layout);
        Grid.SetColumn(files, 2);
        grid.Children.Add(clips); grid.Children.Add(divider); grid.Children.Add(files);
        ShelfContent.Children.Add(grid);
        RefocusTyping();
    }
    private void RememberShelfScroll()
    {
        foreach (var viewer in ShelfContent.Children.OfType<Grid>().SelectMany(grid => FindScrolls(grid)))
        {
            if (Equals(viewer.Tag, ShelfSide.Clipboard)) _clipOffset = viewer.VerticalOffset;
            else if (Equals(viewer.Tag, ShelfSide.Files)) _fileOffset = viewer.VerticalOffset;
        }
        static IEnumerable<ScrollViewer> FindScrolls(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer viewer && viewer.Tag is ShelfSide) yield return viewer;
                else foreach (var inner in FindScrolls(child)) yield return inner;
            }
        }
    }

    private object? Find(ShelfSide side, Guid id) => side == ShelfSide.Clipboard ? ShelfData.Clips.FirstOrDefault(clip => clip.Id == id) : ShelfData.Files.FirstOrDefault(file => file.Id == id);
    private bool Searching => _shelfSearching && _shelfQuery.Trim().Length > 0;
    private List<ClipEntry> VisibleClips() => Searching
        ? ShelfData.Clips.Where(clip => clip.Kind != ClipKind.Image && Shelf.Matches(clip.Text, _shelfQuery)).ToList()
        : ShelfData.Clips.Where(clip => _clipTab is null || clip.Category == _clipTab).ToList();
    private List<TransferFile> VisibleFiles() => Searching
        ? ShelfData.Files.Where(file => Shelf.Matches(file.Name, _shelfQuery)).ToList()
        : ShelfData.Files.Where(file => _fileTab is null || file.Category == _fileTab).ToList();

    // ---- Columns ----
    private Grid ClipColumn(ShelfLayout layout)
    {
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.Header) });
        column.RowDefinitions.Add(new RowDefinition());
        column.Children.Add(ColumnHeader(ShelfSide.Clipboard, layout.ClipColumn, layout));
        FrameworkElement body;
        if (_shelfEdit is { Kind: ShelfEditKind.NewPhrase or ShelfEditKind.EditText } edit) body = PhraseEditor(edit, layout);
        else
        {
            var clips = VisibleClips();
            if (clips.Count == 0) body = ClipEmpty(layout);
            else
            {
                var rows = new StackPanel();
                foreach (var clip in clips) rows.Children.Add(ClipRow(clip, layout));
                body = Scroll(rows, ShelfSide.Clipboard, _clipOffset);
            }
        }
        Grid.SetRow(body, 1); column.Children.Add(body);
        return column;
    }
    private Grid FileColumn(ShelfLayout layout)
    {
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.Header) });
        column.RowDefinitions.Add(new RowDefinition());
        // The drop highlight: 10 % blue with a 1.5 DIP blue outline, corner 10, around the whole column; the list stays visible.
        var frame = new Border { CornerRadius = new CornerRadius(PanelTheme.Corner(10)), Margin = new Thickness(-6, 0, -6, 0), BorderThickness = new Thickness(1.5), IsHitTestVisible = false };
        Grid.SetRowSpan(frame, 2); column.Children.Add(frame); _fileFrame = frame; ShowDropFrame();
        column.Children.Add(ColumnHeader(ShelfSide.Files, layout.FileColumn, layout));
        var files = VisibleFiles();
        FrameworkElement body;
        if (files.Count == 0) body = FileEmpty(layout);
        else
        {
            var rows = new StackPanel();
            foreach (var file in files) rows.Children.Add(FileRow(file, layout));
            body = Scroll(rows, ShelfSide.Files, _fileOffset);
        }
        Grid.SetRow(body, 1); column.Children.Add(body);
        return column;
    }
    private void ShowDropFrame()
    {
        if (_fileFrame is null) return;
        var accent = PanelTheme.ColorOf("accent");
        _fileFrame.Background = _dropping ? new SolidColorBrush(Color.FromArgb(26, accent.R, accent.G, accent.B)) : Brushes.Transparent;
        _fileFrame.BorderBrush = _dropping ? new SolidColorBrush(accent) : Brushes.Transparent;
    }
    // The list scrolls inside its column and fades out at the bottom; the scroll bar is only a thin handle (App.xaml).
    private static ScrollViewer Scroll(FrameworkElement rows, ShelfSide side, double offset)
    {
        var viewer = new ScrollViewer { Content = rows, Tag = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0)); fade.GradientStops.Add(new GradientStop(Colors.Black, .9)); fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        void Mask() => viewer.OpacityMask = viewer.ScrollableHeight > 1 && viewer.VerticalOffset < viewer.ScrollableHeight - 1 ? fade : null;
        viewer.ScrollChanged += (_, _) => Mask();
        viewer.Loaded += (_, _) => { if (offset > 0) viewer.ScrollToVerticalOffset(offset); Mask(); };
        return viewer;
    }

    private FrameworkElement ClipEmpty(ShelfLayout layout)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, layout.Header / 2) };
        string text = Searching ? Loc.T("没有匹配的内容", "Nothing matches")
            : _clipTab is not null ? Loc.T("这个分类还没有内容", "This category is empty")
            : !Settings.ClipboardRecord ? Loc.T("剪贴板记录尚未开启", "Clipboard history is off")
            : _app.Shelf.Paused ? Loc.T("已暂停记录", "Recording is paused") : Loc.T("复制的内容会显示在这里", "What you copy appears here");
        panel.Children.Add(Label(text, layout.Body, UI.Secondary, HorizontalAlignment.Center));
        if (!Searching && _clipTab is null && !Settings.ClipboardRecord)
        {
            panel.Children.Add(Label(Loc.T("开启后保存在本机，加密存放", "Kept on this computer, encrypted"), layout.Aux, UI.Tertiary, HorizontalAlignment.Center, top: 4));
            var on = PillButton(Loc.T("开启记录", "Turn on"), primary: true, () => _app.UpdateSettings(SettingsWith(s => s.ClipboardRecord = true)));
            on.Margin = new Thickness(0, 10, 0, 0); on.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(on);
        }
        else if (!Searching && _clipTab is { } category && ShelfData.ClipCategories.Any(c => c.Id == category))
        {
            var add = PillButton(Loc.T("新建常用语", "New phrase"), primary: false, () => BeginEdit(new ShelfEdit(ShelfEditKind.NewPhrase, ShelfSide.Clipboard, Category: category)));
            add.Margin = new Thickness(0, 10, 0, 0); add.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(add);
        }
        else if (!Searching && _app.Shelf.Paused)
        {
            var on = PillButton(Loc.T("继续记录", "Resume"), primary: false, () => _app.Shelf.SetPaused(false));
            on.Margin = new Thickness(0, 10, 0, 0); on.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(on);
        }
        return panel;
    }
    private FrameworkElement FileEmpty(ShelfLayout layout)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, layout.Header / 2) };
        if (Searching) { panel.Children.Add(Label(Loc.T("没有匹配的文件", "No files match"), layout.Body, UI.Secondary, HorizontalAlignment.Center)); return panel; }
        panel.Children.Add(Label(_fileTab is null ? Loc.T("把文件拖到这里", "Drag files here") : Loc.T("这个分类还没有文件", "This category is empty"), layout.Body, UI.Secondary, HorizontalAlignment.Center));
        panel.Children.Add(Label(Loc.T("只记录位置，不复制文件", "Only their places are kept"), layout.Aux, UI.Tertiary, HorizontalAlignment.Center, top: 4));
        return panel;
    }

    // ---- The column's header: its name (which is "all"), the categories, "＋", and its buttons ----
    private FrameworkElement ColumnHeader(ShelfSide side, double width, ShelfLayout layout)
    {
        var row = new Grid { Height = layout.Header };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string name = side == ShelfSide.Clipboard ? Loc.T("剪贴板", "Clipboard") : Loc.T("文件中转", "File transfer");
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, -4, 0) };
        Grid.SetColumn(buttons, 1);
        row.Children.Add(tabs); row.Children.Add(buttons);
        var categories = ShelfData.Categories(side);
        var selected = side == ShelfSide.Clipboard ? _clipTab : _fileTab;
        if (Searching)
        {
            int found = side == ShelfSide.Clipboard ? VisibleClips().Count : VisibleFiles().Count;
            tabs.Children.Add(Tab(side, null, name, layout, bold: true, selected: false, clickable: false));
            tabs.Children.Add(Label(Loc.T($"{found} 个结果", found == 1 ? "1 result" : $"{found} results"), layout.Aux, UI.Tertiary, HorizontalAlignment.Left));
            return row;
        }
        if (side == ShelfSide.Clipboard)
            buttons.Children.Add(IconButton("⋯", Loc.T("更多", "More"), layout, button => OpenMenu(ClipMoreMenu(), button)));
        else
        {
            var all = IconButton("", Loc.T("拖出全部", "Drag out all"), layout, _ => { }, icon: DragAllGlyph());
            all.IsEnabled = VisibleFiles().Any(file => ShelfFiles.Exists(file.Path));
            all.ToolTip = Loc.T("按住拖出这里的全部文件", "Drag to take out every file here");
            AttachFileDrag(all, () => VisibleFiles().Where(file => ShelfFiles.Exists(file.Path)).ToList());
            buttons.Children.Add(all);
            buttons.Children.Add(IconButton("⋯", Loc.T("更多", "More"), layout, button => OpenMenu(FileMoreMenu(), button)));
        }
        // The size of everything on the file transfer, where there is room for it beside the categories.
        double buttonsWidth = (side == ShelfSide.Clipboard ? 1 : 2) * (layout.Header - 4) + 4;
        double room = width - buttonsWidth - 8;
        var naming = _shelfEdit is { Kind: ShelfEditKind.NewCategory or ShelfEditKind.RenameCategory } edit && edit.Side == side ? edit : null;
        double tabsWidth = TabWidth(name, layout, true) + categories.Sum(category => TabWidth(category.Name, layout, false) + 12) + 26 + (naming is not null ? 110 : 0);
        bool fits = tabsWidth <= room;
        tabs.Children.Add(Tab(side, null, name, layout, bold: true, selected: selected is null && fits || !fits && selected is null));
        if (fits)
        {
            foreach (var category in categories)
            {
                if (naming is { Kind: ShelfEditKind.RenameCategory } rename && rename.Id == category.Id) { tabs.Children.Add(NameBox(naming, category.Name, layout)); continue; }
                tabs.Children.Add(Tab(side, category.Id, category.Name, layout, bold: false, selected: selected == category.Id, color: category.Color));
            }
            if (naming is { Kind: ShelfEditKind.NewCategory }) tabs.Children.Add(NameBox(naming, "", layout));
            else tabs.Children.Add(IconButton("＋", Loc.T("新建分类", "New category"), layout, _ => BeginEdit(new ShelfEdit(ShelfEditKind.NewCategory, side)), small: true));
            if (side == ShelfSide.Files && layout.FileColumn >= 260 && ShelfData.Files.Count > 0 && room - tabsWidth > 70)
            {
                long total = ShelfData.Files.Sum(file => ShelfFiles.FileSize(file.Path) ?? (ShelfFiles.IsFolder(file.Path) ? ShelfFiles.Folder(file.Path).Size ?? 0 : 0));
                var size = Label(Shelf.Size(total), layout.Aux, UI.Tertiary, HorizontalAlignment.Right);
                size.Margin = new Thickness(0, 0, 4, 0);
                buttons.Children.Insert(0, size);
            }
        }
        else
        {
            // The categories go into a menu beside the name, with their counts; the chosen one stays in view.
            if (selected is { } chosen && categories.FirstOrDefault(category => category.Id == chosen) is { } current)
                tabs.Children.Add(Tab(side, current.Id, current.Name, layout, bold: false, selected: true, color: current.Color));
            if (naming is not null) tabs.Children.Add(NameBox(naming, naming.Kind == ShelfEditKind.RenameCategory ? categories.FirstOrDefault(c => c.Id == naming.Id)?.Name ?? "" : "", layout));
            else tabs.Children.Add(IconButton("⌄", Loc.T("分类", "Categories"), layout, button => OpenMenu(CategoryListMenu(side), button), small: true));
        }
        return row;
    }
    private double TabWidth(string text, ShelfLayout layout, bool bold) => TextWidth(text, layout.Title, bold ? FontWeights.SemiBold : FontWeights.Normal) + 16 + (bold ? 0 : 10);
    private Border Tab(ShelfSide side, Guid? id, string text, ShelfLayout layout, bool bold, bool selected, string? color = null, bool clickable = true)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (color is not null) content.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(color), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = layout.Title, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = UI.Brush(selected || bold ? UI.Primary : UI.Secondary), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 });
        var tab = new Border
        {
            Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 2, 0), Child = content,
            Background = selected ? UI.Brush(UI.FillSelected) : Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Cursor = clickable ? Cursors.Hand : null, AllowDrop = clickable
        };
        AutomationProperties.SetName(tab, text);
        if (!clickable) return tab;
        _shelfTabs.Add((tab, id, side));
        tab.MouseEnter += (_, _) => { if (!selected) tab.Background = UI.Brush(PanelTheme.Hex("hover")); };
        tab.MouseLeave += (_, _) => { if (!selected) tab.Background = Brushes.Transparent; };
        tab.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (side == ShelfSide.Clipboard) _clipTab = id; else { _fileTab = id; _picked.Clear(); }
            if (side == ShelfSide.Clipboard) _clipOffset = 0; else _fileOffset = 0;
            RenderShelf();
        };
        tab.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (id is { } category && ShelfData.Categories(side).FirstOrDefault(c => c.Id == category) is { } found) OpenMenu(CategoryMenu(side, found), tab);
        };
        // Entries dragged from the list, and files dragged in from the computer onto a file category, go into that category.
        tab.DragOver += (_, e) => { e.Effects = TabAccepts(side, id, e.Data) ? DragDropEffects.Copy : DragDropEffects.None; HighlightTab(tab, e.Effects != DragDropEffects.None); e.Handled = true; };
        tab.DragLeave += (_, _) => HighlightTab(tab, false);
        tab.Drop += (_, e) => { e.Handled = true; HighlightTab(tab, false); DropOnTab(side, id, e.Data); };
        return tab;
    }
    private void HighlightTab(Border tab, bool on)
    {
        var accent = PanelTheme.ColorOf("accent");
        tab.BorderThickness = new Thickness(on ? 1 : 0);
        tab.BorderBrush = on ? new SolidColorBrush(accent) : null;
        if (on) tab.Background = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B));
        else if (_shelfTabs.FirstOrDefault(t => t.Tab == tab) is var entry && entry.Tab is not null)
            tab.Background = (entry.Side == ShelfSide.Clipboard ? _clipTab : _fileTab) == entry.Id ? UI.Brush(UI.FillSelected) : Brushes.Transparent;
    }
    private const string ShelfFormat = "BrimDeck.ShelfItems";
    private static bool TabAccepts(ShelfSide side, Guid? id, IDataObject data)
    {
        if (data.GetDataPresent(ShelfFormat)) return data.GetData(ShelfFormat) is string text && text.StartsWith(side + "|", StringComparison.Ordinal);
        return side == ShelfSide.Files && data.GetDataPresent(DataFormats.FileDrop);
    }
    private void DropOnTab(ShelfSide side, Guid? id, IDataObject data)
    {
        if (data.GetDataPresent(ShelfFormat) && data.GetData(ShelfFormat) is string text && text.StartsWith(side + "|", StringComparison.Ordinal))
        {
            var ids = text[(side.ToString().Length + 1)..].Split(',').Select(part => Guid.TryParse(part, out var g) ? g : Guid.Empty).ToHashSet();
            Assign(side, ids, id);
            return;
        }
        if (side == ShelfSide.Files && data.GetData(DataFormats.FileDrop) is string[] paths) { EndDrop(dropped: true); AddDropped(paths, id); }
    }
    private void Assign(ShelfSide side, IEnumerable<Guid> ids, Guid? category)
    {
        var set = ids.ToHashSet();
        if (side == ShelfSide.Clipboard) foreach (var clip in ShelfData.Clips.Where(clip => set.Contains(clip.Id))) clip.Category = category;
        else foreach (var file in ShelfData.Files.Where(file => set.Contains(file.Id))) file.Category = category;
        _app.Shelf.Edited();
    }

    // ---- Rows ----
    private Border RowFrame(ShelfLayout layout, double height, bool picked)
    {
        var frame = new Border
        {
            Height = height, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Padding = new Thickness(8, 0, 4, 0), Margin = new Thickness(-8, 0, 0, 0),
            Background = picked ? UI.Brush(UI.FillSelected) : Brushes.Transparent, Cursor = Cursors.Hand
        };
        return frame;
    }
    private Border ClipRow(ClipEntry clip, ShelfLayout layout)
    {
        bool two = layout.TwoLines && clip.Kind is ClipKind.Text or ClipKind.Code && Shelf.LineCount(clip.Text.Trim()) > 1 || layout.TwoLines && clip.Kind == ClipKind.Image;
        double height = two ? layout.Row + (clip.Kind == ClipKind.Image ? Math.Max(0, layout.ThumbHeight + 6 - layout.Row) : layout.Body * 1.45) : layout.Row;
        var frame = RowFrame(layout, height, false);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var content = ClipContent(clip, layout, two);
        grid.Children.Add(content);
        if (clip.Category is { } id && _clipTab is null && ShelfData.ClipCategories.FirstOrDefault(c => c.Id == id) is { } category)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(category.Color), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = category.Name };
            Grid.SetColumn(dot, 1); grid.Children.Add(dot);
        }
        var aux = AuxSlot(clip.Manual ? Loc.T("常用语", "Phrase") : Shelf.When(clip.Copied, DateTimeOffset.Now), layout, () => OpenDetail(ShelfSide.Clipboard, clip.Id));
        Grid.SetColumn(aux, 2); grid.Children.Add(aux);
        frame.Child = grid;
        AutomationProperties.SetName(frame, clip.Kind == ClipKind.Image ? Loc.T("图片", "Picture") : Shelf.FirstLine(clip.Text).First);
        _shelfRows[clip.Id] = (frame, aux);
        AttachRow(frame, aux, ShelfSide.Clipboard, clip.Id, picked: false,
            click: () => PasteClip(clip),
            menu: () => ClipMenu(clip),
            drag: () => ClipDrag(clip));
        if (_flash is { } flash && flash.Id == clip.Id && flash.Until > Environment.TickCount64) ShowFlash(frame, aux, flash.Text);
        return frame;
    }
    private FrameworkElement ClipContent(ClipEntry clip, ShelfLayout layout, bool two)
    {
        switch (clip.Kind)
        {
            case ClipKind.Image:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var thumb = new Border { Width = layout.Thumb, Height = layout.ThumbHeight, CornerRadius = new CornerRadius(PanelTheme.Corner(3)), Background = UI.Brush(UI.Fill), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
                if (_app.Shelf.Picture(clip.Id, (int)Math.Ceiling(layout.Thumb * 2)) is { } picture)
                    thumb.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
                panel.Children.Add(thumb);
                panel.Children.Add(Label(Loc.T("图片", "Picture") + $"  {clip.PixelWidth}×{clip.PixelHeight}", layout.Aux, UI.Secondary, HorizontalAlignment.Left, left: 8));
                return panel;
            }
            case ClipKind.Color when Shelf.TryColor(clip.Text, out var c):
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                panel.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(PanelTheme.Corner(3)), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                    BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
                var text = Highlighted(clip.Text.Trim(), layout.Body, UI.Primary, mono: true); text.Margin = new Thickness(8, 0, 0, 0);
                panel.Children.Add(text);
                return panel;
            }
            case ClipKind.Link:
            {
                var (host, rest) = Shelf.SplitLink(clip.Text);
                var block = new TextBlock { FontFamily = UI.PanelFont, FontSize = layout.Body, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                if (Searching) AddHighlighted(block.Inlines, clip.Text.Trim(), UI.Primary);
                else { block.Inlines.Add(new Run(host) { Foreground = UI.Brush(UI.Primary) }); block.Inlines.Add(new Run(rest) { Foreground = UI.Brush(UI.Tertiary) }); }
                return block;
            }
            default:
            {
                bool code = clip.Kind == ClipKind.Code;
                var (first, more) = Shelf.FirstLine(clip.Text);
                if (two)
                {
                    var lines = clip.Text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Trim().Length > 0).ToList();
                    string shown = string.Join("\n", lines.Take(2).Select(line => code ? line.Replace("\t", "  ") : line.Trim()));
                    var block = Highlighted(shown, layout.Body, UI.Primary, mono: code);
                    block.TextWrapping = TextWrapping.NoWrap;
                    if (lines.Count > 2) { block.Inlines.Add(new Run("  +" + (lines.Count - 2) + Loc.T(" 行", " lines")) { Foreground = UI.Brush(UI.Tertiary), FontSize = layout.Aux }); }
                    return block;
                }
                var line = Highlighted(code ? first : first, layout.Body, UI.Primary, mono: code);
                if (more > 0) line.Inlines.Add(new Run("  +" + more + Loc.T(" 行", more == 1 ? " line" : " lines")) { Foreground = UI.Brush(UI.Tertiary), FontSize = layout.Aux, FontFamily = UI.PanelFont });
                return line;
            }
        }
    }
    private TextBlock Highlighted(string text, double size, string color, bool mono = false)
    {
        var block = new TextBlock { FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI") : UI.PanelFont, FontSize = mono ? size - .5 : size, Foreground = UI.Brush(color),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        AddHighlighted(block.Inlines, text, color);
        return block;
    }
    // The search's matches on a blue background.
    private void AddHighlighted(InlineCollection inlines, string text, string color)
    {
        if (!Searching) { inlines.Add(new Run(text)); return; }
        var accent = PanelTheme.ColorOf("accent");
        var mark = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B));
        int at = 0;
        foreach (var (start, length) in Shelf.MatchesIn(text, _shelfQuery))
        {
            if (start > at) inlines.Add(new Run(text[at..start]));
            inlines.Add(new Run(text.Substring(start, length)) { Background = mark, Foreground = UI.Brush(UI.Primary) });
            at = start + length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }

    private Border FileRow(TransferFile file, ShelfLayout layout)
    {
        bool exists = ShelfFiles.Exists(file.Path), folder = exists && ShelfFiles.IsFolder(file.Path);
        var frame = RowFrame(layout, layout.Row, _picked.Contains(file.Id));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Icon + 8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Source = ShelfFiles.Icon(file.Path), Width = layout.Icon, Height = layout.Icon, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Opacity = exists ? 1 : .45 };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);
        // The facts at the right: size (or the folder's items, then its size), and the date where there is room.
        string facts = !exists ? Loc.T("已不存在", "Missing") : folder ? FolderFacts(file.Path) : ShelfFiles.FileSize(file.Path) is { } bytes ? Shelf.Size(bytes) : "";
        if (layout.FileDate && exists && ShelfFiles.Modified(file.Path) is { } modified) facts += "   " + modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool showFacts = !exists || layout.FileSize;
        double factsWidth = showFacts ? TextWidth(facts, layout.Aux, FontWeights.Normal) + 10 : 0;
        double nameRoom = Math.Max(20, layout.FileColumn - layout.Icon - 8 - 12 - Math.Max(factsWidth, 22));
        string shown = Shelf.MiddleEllipsis(file.Name, candidate => TextWidth(candidate, layout.Body, FontWeights.Normal) <= nameRoom);
        var name = Label(shown, layout.Body, UI.Primary, HorizontalAlignment.Left);
        name.Opacity = exists ? 1 : .45;
        if (Searching) { name.Inlines.Clear(); AddHighlighted(name.Inlines, shown, UI.Primary); }
        // The whole name, after a moment, when the row shows only part of it.
        if (shown != file.Name) { name.ToolTip = file.Name; ToolTipService.SetInitialShowDelay(name, 600); }
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        var aux = AuxSlot(showFacts ? facts : "", layout, () => OpenDetail(ShelfSide.Files, file.Id), warn: !exists);
        Grid.SetColumn(aux, 2); grid.Children.Add(aux);
        frame.Child = grid;
        AutomationProperties.SetName(frame, file.Name);
        _shelfRows[file.Id] = (frame, aux);
        AttachRow(frame, aux, ShelfSide.Files, file.Id, picked: _picked.Contains(file.Id),
            click: () => ClickFile(file),
            menu: () => FileMenu(file),
            drag: null);
        AttachFileDrag(frame, () => _picked.Contains(file.Id) && _picked.Count > 1 ? ShelfData.Files.Where(f => _picked.Contains(f.Id) && ShelfFiles.Exists(f.Path)).ToList() : exists ? [file] : []);
        if (_flash is { } flash && flash.Id == file.Id && flash.Until > Environment.TickCount64) ShowFlash(frame, aux, flash.Text);
        return frame;
    }
    private static string FolderFacts(string path)
    {
        var stats = ShelfFiles.Folder(path);
        if (stats.Size is { } size) return Shelf.Size(size);
        return stats.Items is { } items ? Loc.T($"{items} 项", items == 1 ? "1 item" : $"{items} items") : "…";
    }

    // The time or size at the end of a row; under the pointer it gives way to "›", which shows the whole entry.
    private Grid AuxSlot(string text, ShelfLayout layout, Action open, bool warn = false)
    {
        var slot = new Grid { VerticalAlignment = VerticalAlignment.Center, MinWidth = 22, Margin = new Thickness(8, 0, 0, 0) };
        var label = Label(text, layout.Aux, warn ? "#E5C890" : UI.Tertiary, HorizontalAlignment.Right);
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        var more = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(5)), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Hidden, Cursor = Cursors.Hand, ToolTip = Loc.T("查看完整内容", "Show all"),
            Child = new Path { Data = Geometry.Parse("M 0,0 L 3.5,3.5 L 0,7"), Stroke = UI.Brush(UI.Secondary), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        more.MouseEnter += (_, _) => more.Background = UI.Brush(PanelTheme.Hex("hover"));
        more.MouseLeave += (_, _) => more.Background = Brushes.Transparent;
        more.MouseLeftButtonDown += (_, e) => e.Handled = true;
        more.MouseLeftButtonUp += (_, e) => { e.Handled = true; open(); };
        slot.Children.Add(label); slot.Children.Add(more);
        slot.Tag = (label, more);
        return slot;
    }
    private static void ShowMore(Grid aux, bool on)
    {
        if (aux.Tag is not (TextBlock label, Border more)) return;
        label.Visibility = on ? Visibility.Hidden : Visibility.Visible;
        more.Visibility = on ? Visibility.Visible : Visibility.Hidden;
    }

    // Hover lights the row and shows "›"; press darkens it; a click (a press and release without moving away) does the row's action.
    private void AttachRow(Border frame, Grid aux, ShelfSide side, Guid id, bool picked, Action click, Func<ContextMenu> menu, Func<DataObject>? drag)
    {
        Brush rest = picked ? UI.Brush(UI.FillSelected) : Brushes.Transparent;
        Point? pressed = null;
        bool flashing() => _flash is { } flash && flash.Id == id && flash.Until > Environment.TickCount64;
        frame.MouseEnter += (_, _) => { if (!flashing()) frame.Background = picked ? rest : UI.Brush(PanelTheme.Hex("hover")); if (!flashing()) ShowMore(aux, true); };
        frame.MouseLeave += (_, _) => { pressed = null; if (!flashing()) { frame.Background = rest; ShowMore(aux, false); } };
        frame.MouseLeftButtonDown += (_, e) => { pressed = e.GetPosition(frame); if (!flashing()) frame.Background = UI.Brush(PanelTheme.Hex("pressed")); e.Handled = true; };
        frame.MouseLeftButtonUp += (_, e) =>
        {
            if (pressed is null) return;
            pressed = null; e.Handled = true;
            if (!flashing()) frame.Background = picked ? rest : UI.Brush(PanelTheme.Hex("hover"));
            click();
        };
        frame.MouseRightButtonUp += (_, e) => { e.Handled = true; OpenMenu(menu(), frame, atPointer: true); };
        if (drag is not null)
            frame.MouseMove += (_, e) =>
            {
                if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
                var now = e.GetPosition(frame);
                if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = null; frame.Background = rest;
                var data = drag();
                data.SetData(ShelfFormat, side + "|" + id);
                DragOut(frame, data);
            };
    }
    private void AttachFileDrag(FrameworkElement source, Func<List<TransferFile>> files)
    {
        Point? start = null;
        source.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(source);
        source.PreviewMouseLeftButtonUp += (_, _) => start = null;
        source.MouseLeave += (_, e) => { if (e.LeftButton != MouseButtonState.Pressed) start = null; };
        source.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(source);
            if (Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            var list = files();
            if (list.Count == 0) return;
            var data = ShelfService.FileData(list.Select(file => file.Path));
            data.SetData(ShelfFormat, ShelfSide.Files + "|" + string.Join(",", list.Select(file => file.Id)));
            if (source is Border row) row.Background = _picked.Count > 0 && list.Count > 1 ? UI.Brush(UI.FillSelected) : Brushes.Transparent;
            DragOut(source, data);
            e.Handled = true;
        };
    }
    // Dragged out of the panel, entries and files are only copied: the originals stay where they are.
    private void DragOut(DependencyObject source, DataObject data)
    {
        _draggingOut = true;
        try { DragDrop.DoDragDrop(source, data, DragDropEffects.Copy); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        finally
        {
            _draggingOut = false;
            if (_expanded && !_pointerInside) PointerChanged(false);
        }
    }
    private DataObject ClipDrag(ClipEntry clip) => _app.Shelf.ClipData(clip);

    // ---- Actions ----
    private void PasteClip(ClipEntry clip, bool plain = false)
        => _app.Shelf.Paste(_app.Shelf.ClipData(clip, plain), done => Flash(clip.Id, done ? Loc.T("已粘贴", "Pasted") : Loc.T("已复制", "Copied")));
    private void CopyClip(ClipEntry clip) { if (_app.Shelf.Put(_app.Shelf.ClipData(clip))) Flash(clip.Id, Loc.T("已复制", "Copied")); }
    private void ClickFile(TransferFile file)
    {
        bool ctrl = PasteInput.KeyDown(0x11), shift = PasteInput.KeyDown(0x10);
        if (ctrl || shift)
        {
            // Ctrl adds or removes one file; Shift takes every file from the last one chosen to this one.
            var visible = VisibleFiles();
            if (shift && _pickAnchor is { } anchor && visible.FindIndex(f => f.Id == anchor) is var from and >= 0)
            {
                int to = visible.FindIndex(f => f.Id == file.Id);
                if (!ctrl) _picked.Clear();
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _picked.Add(visible[i].Id);
            }
            else if (!_picked.Remove(file.Id)) { _picked.Add(file.Id); _pickAnchor = file.Id; }
            RenderShelf();
            return;
        }
        if (_picked.Count > 0) { _picked.Clear(); RenderShelf(); }
        _pickAnchor = file.Id;
        if (!ShelfFiles.Exists(file.Path)) return;
        PasteFiles([file]);
    }
    private void PasteFiles(IReadOnlyList<TransferFile> files)
    {
        var present = files.Where(file => ShelfFiles.Exists(file.Path)).ToList();
        if (present.Count == 0) return;
        _app.Shelf.Paste(ShelfService.FileData(present.Select(file => file.Path)), done => { foreach (var file in present) Flash(file.Id, done ? Loc.T("已粘贴", "Pasted") : Loc.T("已复制", "Copied")); });
    }
    private void Flash(Guid id, string text)
    {
        _flash = (id, text, Environment.TickCount64 + 1200);
        if (_shelfRows.TryGetValue(id, out var row)) ShowFlash(row.Row, row.Aux, text);
        _flashTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(1250) };
        _flashTimer.Tick -= FlashEnded; _flashTimer.Tick += FlashEnded;
        _flashTimer.Stop(); _flashTimer.Start();
    }
    private void FlashEnded(object? sender, EventArgs e)
    {
        _flashTimer?.Stop();
        if (_flash is not { } flash) return;
        _flash = null;
        if (_shelfRows.TryGetValue(flash.Id, out var row))
        {
            bool over = row.Row.IsMouseOver;
            row.Row.Background = _picked.Contains(flash.Id) ? UI.Brush(UI.FillSelected) : over ? UI.Brush(PanelTheme.Hex("hover")) : Brushes.Transparent;
            if (row.Aux.Tag is (TextBlock label, Border _) && label.Tag is string original) { label.Text = original; label.Tag = null; label.Foreground = UI.Brush(UI.Tertiary); }
            ShowMore(row.Aux, over);
        }
    }
    private void ShowFlash(Border row, Grid aux, string text)
    {
        var accent = PanelTheme.ColorOf("accent");
        row.Background = new SolidColorBrush(Color.FromArgb(51, accent.R, accent.G, accent.B));
        if (aux.Tag is (TextBlock label, Border more))
        {
            label.Tag ??= label.Text;
            label.Text = text; label.Foreground = UI.Brush(UI.Primary); label.Visibility = Visibility.Visible; more.Visibility = Visibility.Hidden;
        }
    }

    private static void OpenFile(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { }
    }
    private static void ShowInFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ---- Files dragged in ----
    // Into the collapsed island the panel opens on this page; into the panel on another page it turns to this page. The file transfer
    // column lights up; over a file category, that category does too. Taken away without dropping, the panel goes back.
    private void ShelfDragEnter(object sender, DragEventArgs e)
    {
        _dropLeave?.Stop();
        if (_draggingOut || !Settings.ClipboardPage || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy; e.Handled = true;
        if (_dropping) return;
        _dropping = true;
        _shelfDetail = null; _shelfEdit = null; _shelfSearching = false; _shelfQuery = "";
        if (!_expanded)
        {
            _dropOpened = true; _dropReturn = null;
            _hover.Stop();
            if (!IsShelfPage) SelectPage(DeckPage.Clipboard, remember: false);
            SetExpanded(true);
        }
        else
        {
            _dropOpened = false;
            _dropReturn = IsShelfPage ? null : CurrentPage;
            if (!IsShelfPage) SelectPage(DeckPage.Clipboard, remember: false);
        }
        RenderShelf();
    }
    private void ShelfDragOver(object sender, DragEventArgs e)
    {
        _dropLeave?.Stop();
        if (_draggingOut || !_dropping) return;
        e.Effects = DragDropEffects.Copy;
        // A file category under the pointer lights up as well (Tab's own DragOver); elsewhere the whole column takes the files.
        if (e.OriginalSource is DependencyObject over && _shelfTabs.Any(tab => tab.Side == ShelfSide.Files && tab.Tab.IsAncestorOf(over))) return;
        e.Handled = true;
    }
    private void ShelfDragLeave(object sender, DragEventArgs e)
    {
        if (!_dropping) return;
        // Leave also fires when the pointer moves between parts of the panel; it counts only if no part takes the drag right after.
        _dropLeave ??= new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _dropLeave.Tick -= DropLeft; _dropLeave.Tick += DropLeft;
        _dropLeave.Stop(); _dropLeave.Start();
    }
    private void DropLeft(object? sender, EventArgs e) => EndDrop(dropped: false);
    private void ShelfDrop(object sender, DragEventArgs e)
    {
        _dropLeave?.Stop();
        if (_draggingOut || !_dropping) return;
        if (e.OriginalSource is DependencyObject over && _shelfTabs.FirstOrDefault(tab => tab.Side == ShelfSide.Files && tab.Tab.IsAncestorOf(over)) is { Tab: not null } tab)
        { HighlightTab(tab.Tab, false); EndDrop(dropped: true); AddDropped(e.Data.GetData(DataFormats.FileDrop) as string[] ?? [], tab.Id); e.Handled = true; return; }
        e.Handled = true;
        EndDrop(dropped: true);
        AddDropped(e.Data.GetData(DataFormats.FileDrop) as string[] ?? [], null);
    }
    private void AddDropped(string[] paths, Guid? category)
    {
        foreach (var path in paths) ShelfFiles.Forget(path);
        if (paths.Length == 0) return;
        _fileTab = category; _fileOffset = 0; _picked.Clear();
        _app.Shelf.AddFiles(paths, category);
    }
    private void EndDrop(bool dropped)
    {
        _dropLeave?.Stop();
        if (!_dropping) return;
        _dropping = false;
        if (!dropped)
        {
            if (_dropOpened) { _dropOpened = false; if (!_pointerInside) SetExpanded(false); }
            else if (_dropReturn is { } back) SelectPage(back, remember: false);
            _dropReturn = null;
        }
        _dropOpened = false; _dropReturn = null;
        if (IsShelfPage && _expanded) RenderShelf();
    }

    // ---- Small parts ----
    private static TextBlock Label(string text, double size, string color, HorizontalAlignment align, double top = 0, double left = 0) => new()
    {
        Text = text, FontFamily = UI.PanelFont, FontSize = size, Foreground = UI.Brush(color), HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(left, top, 0, 0)
    };
    private double TextWidth(string text, double size, FontWeight weight, FontFamily? font = null)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(font ?? UI.PanelFont, FontStyles.Normal, weight, FontStretches.Normal),
            size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }
    private Button IconButton(string glyph, string name, ShelfLayout layout, Action<Button> click, bool small = false, FrameworkElement? icon = null)
    {
        double size = small ? 22 : Math.Min(26, layout.Header - 4);
        var button = new Button { Style = (Style)FindResource("PanelIconButton"), Width = size, Height = Math.Min(size, 24), Margin = new Thickness(1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Content = icon ?? (object)new TextBlock { Text = glyph, FontFamily = UI.PanelFont, FontSize = glyph == "⋯" ? 14 : 13, Foreground = UI.Brush(UI.Secondary), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, glyph == "⌄" ? -6 : 0, 0, 0) },
            ToolTip = name };
        AutomationProperties.SetName(button, name);
        button.Click += (_, e) => { e.Handled = true; click(button); };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        return button;
    }
    private static FrameworkElement DragAllGlyph() => new Path
    {
        Data = Geometry.Parse("M 12,3 V 15 M 7,10 L 12,15 L 17,10 M 4,17 V 20 H 20 V 17"), Stroke = UI.Brush(UI.Secondary), StrokeThickness = 1.8,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform, Width = 13, Height = 13
    };
    // A capsule button: 26 high, corner 13, 12 px text; the main action is white with black text, the others 12 % white.
    private Button PillButton(string text, bool primary, Action click)
    {
        var label = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = 12, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = primary ? UI.Brush(PanelTheme.Hex("surface")) : UI.Brush(UI.Primary), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var face = new Border { Height = 26, CornerRadius = new CornerRadius(PanelTheme.Corner(13)), Padding = new Thickness(12, 0, 12, 0), Child = label,
            Background = primary ? UI.Brush(UI.Primary) : UI.Brush(UI.FillSelected) };
        var button = new Button { Content = face, Cursor = Cursors.Hand, Focusable = false, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        button.MouseEnter += (_, _) => face.Opacity = .86;
        button.MouseLeave += (_, _) => face.Opacity = 1;
        button.Click += (_, e) => { e.Handled = true; click(); };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        AutomationProperties.SetName(button, text);
        return button;
    }
    private DeckSettings SettingsWith(Action<DeckSettings> change)
    {
        var copy = Settings.Copy();
        change(copy);
        return copy;
    }
}
