using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The grid editor of the expanded monitor: the tiles sit on a board of dashed cells. A tile follows the pointer while it is dragged,
// the others make way at once (ExpandedGrid.Place), and a dashed frame shows where it lands. Its left, right and bottom edges resize
// it the same way. Readings not on the board wait above it and are dragged in, or clicked to go at the end.
public sealed partial class SettingsWindow
{
    private const double GridRowHeight = 36, GridGap = 6, GridEdge = 7;

    private static string GridTileName(ExpandedGridTile tile) => tile.Letter ?? ExpandedName(tile.Item);

    private string[] GridDriveLetters() => [.. (_app.Monitor?.Latest?.Storage ?? [])
        .SelectMany(drive => drive.Volumes).Select(volume => volume.Letter)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    [Flags]
    private enum GridEdges { None = 0, Left = 1, Right = 2, Bottom = 4, Top = 8 }

    private FrameworkElement ExpandedGridEditor(List<Action> refresh)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        // Everything dragged is drawn here, above the readings waiting and the board.
        var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        var root = new Grid();
        root.Children.Add(panel);
        root.Children.Add(overlay);

        var reset = ActionButton(Loc.T("恢复默认排布", "Restore default layout"), () => { });
        reset.Style = (Style)FindResource("SettingsLink");
        reset.VerticalAlignment = VerticalAlignment.Center;
        var title = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(reset, Dock.Right);
        title.Children.Add(reset);
        title.Children.Add(SettingLabel(Loc.T("页面排布", "Page layout"), () => Loc.T(
            "拖动方块移动位置，拖动方块边缘的把手调整大小，点方块右上角的叉号移出网格。放到已有方块上时，原来的方块让到旁边或下方的空位。CPU 和 GPU 占一行时显示总占用，占两行以上时显示各核心或各引擎。内存可以占两行，其他读数只占一行。",
            "Drag a tile to move it, drag the handles on its edges to resize it, and click the cross at its top right to remove it. A tile dropped on another moves that one to the next free space beside or below. A CPU or GPU tile one row high shows total usage; two rows or more show each core or engine. Memory can take two rows; the other readings take one.")));
        panel.Children.Add(title);

        var paletteLabel = TextLine(Loc.T("可添加项目", "Readings to add"), 12, TextSecondary);
        paletteLabel.Margin = new Thickness(0, 10, 0, 6);
        var palette = new WrapPanel();
        panel.Children.Add(paletteLabel);
        panel.Children.Add(palette);

        var board = new Grid { Background = Brushes.Transparent, ClipToBounds = false };
        AutomationProperties.SetName(board, Loc.T("展开页排布网格", "Expanded page layout grid"));
        var cells = new Canvas { IsHitTestVisible = false };
        var ghost = new Rectangle { RadiusX = 7, RadiusY = 7, StrokeThickness = 1.5, StrokeDashArray = [4, 3], IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        ghost.SetResourceReference(Shape.StrokeProperty, "SettingsAccentText");
        ghost.SetResourceReference(Shape.FillProperty, "SettingsAccentTint");
        var tilesLayer = new Canvas { ClipToBounds = false };
        var ghostLayer = new Canvas { IsHitTestVisible = false };
        ghostLayer.Children.Add(ghost);
        board.Children.Add(cells);
        board.Children.Add(ghostLayer);
        board.Children.Add(tilesLayer);
        var host = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0), Child = board };
        host.SetResourceReference(Border.BackgroundProperty, "SettingsInset");
        panel.Children.Add(host);


        ExpandedGridTile[] built = [];
        string[] builtDrives = [];
        ExpandedGridTile? selected = null;
        bool initialized = false;
        int rows = 3, drawnRows = -1;
        double drawnWidth = -1;
        var views = new List<(ExpandedGridTile Tile, Border View)>();
        var targets = new Dictionary<FrameworkElement, Rect>();
        var pillsOf = new Dictionary<Border, List<FrameworkElement>>();
        Border? hovered = null;
        // A press on the board that becomes a drag once the pointer has moved a little: the tile, the edge pressed and where.
        (ExpandedGridTile Tile, Border View, GridEdges Edges, Point At)? press = null;

        // A drag in progress: a tile of the board moved or resized, or a reading waiting above the board brought in.
        ExpandedGridTile? dragTile = null;          // the tile as it was when the drag began (from the board or the readings waiting)
        bool fromBoard = false;
        GridEdges dragEdges = GridEdges.None;       // None: moving
        Border? dragView = null;                    // the board's own view of the tile, hidden while it moves, resized in place
        Border? floating = null;                    // what follows the pointer while moving
        Point grab = default, start = default;
        Rect startRect = default;
        ExpandedGridTile? candidate = null;
        List<ExpandedGridTile>? arranged = null;
        ScrollViewer? page = null;
        var scroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };

        List<ExpandedGridTile> Current() => S.ExpandedGridTiles ?? ExpandedGrid.Seed(S, GridDriveLetters());
        void Save(List<ExpandedGridTile> tiles, ExpandedGridTile? focus, bool focusKeyboard = false)
        {
            bool hadFrames = Current().Any(tile => tile.Item == ExpandedItem.Frames);
            bool hasFrames = tiles.Any(tile => tile.Item == ExpandedItem.Frames);
            selected = focus;
            Resize(settings => settings.ExpandedGridTiles = tiles, DeckPage.Monitor);
            Build(force: true, focusKeyboard);
            if (hadFrames != hasFrames)
            {
                if (hasFrames) AskFrameAccess();
                else FrameAccessChanged?.Invoke();
            }
        }
        void Place(ExpandedGridTile tile, bool focusKeyboard = false)
        {
            var old = Current();
            var next = ExpandedGrid.Place(old, tile);
            if (!old.SequenceEqual(next)) Save(next, tile, focusKeyboard);
            else Build(force: true, focusKeyboard);
        }
        void Remove(ExpandedGridTile tile)
        {
            Save([.. Current().Where(current => !ExpandedGrid.SameReading(current, tile))], null);
            palette.Children.OfType<Button>().FirstOrDefault(button =>
                button.Tag is ExpandedGridTile reading && ExpandedGrid.SameReading(reading, tile))?.Focus();
        }
        reset.Click += (_, _) =>
        {
            bool hadFrames = Current().Any(tile => tile.Item == ExpandedItem.Frames);
            selected = null;
            Resize(settings => settings.ExpandedGridTiles = null, DeckPage.Monitor);
            Build(force: true);
            bool hasFrames = Current().Any(tile => tile.Item == ExpandedItem.Frames);
            if (!hadFrames && hasFrames) AskFrameAccess();
            else if (hadFrames && !hasFrames) FrameAccessChanged?.Invoke();
        };

        // Geometry: the board's width shared by the columns, rows of a fixed height, the same gap between both.
        double ColumnWidth() => Math.Max(1, (board.ActualWidth - GridGap * (ExpandedGrid.ColumnCount - 1)) / ExpandedGrid.ColumnCount);
        Rect RectOf(ExpandedGridTile tile) => new(tile.Column * (ColumnWidth() + GridGap), tile.Row * (GridRowHeight + GridGap),
            tile.Columns * ColumnWidth() + (tile.Columns - 1) * GridGap, tile.Rows * GridRowHeight + (tile.Rows - 1) * GridGap);
        static int Bottom(IEnumerable<ExpandedGridTile> tiles) => tiles.Select(tile => tile.Row + tile.Rows).DefaultIfEmpty(0).Max();

        void SetRows(int count)
        {
            rows = Math.Clamp(count, 3, ExpandedGrid.MaxRows);
            board.Height = rows * GridRowHeight + (rows - 1) * GridGap;
            DrawCells();
        }
        void DrawCells()
        {
            if (board.ActualWidth <= 0 || rows == drawnRows && Math.Abs(board.ActualWidth - drawnWidth) < .5) return;
            drawnRows = rows; drawnWidth = board.ActualWidth;
            cells.Children.Clear();
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < ExpandedGrid.ColumnCount; column++)
                {
                    var rect = RectOf(new ExpandedGridTile(ExpandedItem.Memory, null, column, row, 1, 1));
                    var cell = new Rectangle { Width = rect.Width, Height = rect.Height, RadiusX = 6, RadiusY = 6, StrokeThickness = 1, StrokeDashArray = [4, 3] };
                    cell.SetResourceReference(Shape.StrokeProperty, "SettingsBorder");
                    Canvas.SetLeft(cell, rect.X); Canvas.SetTop(cell, rect.Y);
                    cells.Children.Add(cell);
                }
        }

        // Puts an element at a place, gliding there when asked to and when the target has changed.
        void Put(FrameworkElement element, Rect rect, bool animate)
        {
            bool moved = !targets.TryGetValue(element, out var was) || was != rect;
            targets[element] = rect;
            if (!moved && animate) return;
            bool glide = animate && Soft.Enabled;
            void Set(DependencyProperty property, double value)
            {
                double from = (double)element.GetValue(property);
                if (!glide || double.IsNaN(from) || Math.Abs(from - value) < .5) { element.BeginAnimation(property, null); element.SetValue(property, value); return; }
                element.BeginAnimation(property, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(Soft.Quick)) { EasingFunction = Soft.Out });
            }
            Set(Canvas.LeftProperty, rect.X); Set(Canvas.TopProperty, rect.Y);
            Set(WidthProperty, rect.Width); Set(HeightProperty, rect.Height);
        }
        void ShowGhost(ExpandedGridTile? tile)
        {
            if (tile is null) { ghost.Visibility = Visibility.Collapsed; targets.Remove(ghost); return; }
            bool appearing = ghost.Visibility != Visibility.Visible;
            ghost.Visibility = Visibility.Visible;
            Put(ghost, RectOf(tile), animate: !appearing);
        }

        Border TileView(string name, bool unavailable, bool active)
        {
            var view = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
            view.SetResourceReference(Border.BackgroundProperty, active ? "SettingsAccentTintStrong" : "SettingsAccentTint");
            view.SetResourceReference(Border.BorderBrushProperty, "SettingsAccentText");
            var label = new TextBlock
            {
                Text = unavailable ? Loc.T($"{name} 未连接", $"{name} disconnected") : name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), IsHitTestVisible = false
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SettingsAccentText");
            if (unavailable) label.Opacity = .6;
            view.Child = new Grid { Children = { label } };
            return view;
        }
        static Cursor CursorFor(GridEdges edges) => edges switch
        {
            GridEdges.Left or GridEdges.Right => Cursors.SizeWE,
            GridEdges.Bottom or GridEdges.Top => Cursors.SizeNS,
            GridEdges.Bottom | GridEdges.Right or GridEdges.Top | GridEdges.Left => Cursors.SizeNWSE,
            GridEdges.Bottom | GridEdges.Left or GridEdges.Top | GridEdges.Right => Cursors.SizeNESW,
            _ => Cursors.SizeAll
        };
        Border Pill(GridEdges edge)
        {
            bool across = edge is GridEdges.Bottom or GridEdges.Top;
            var pill = new Border
            {
                Width = across ? 28 : 4, Height = across ? 4 : 14, CornerRadius = new CornerRadius(2), IsHitTestVisible = false, Visibility = Visibility.Hidden,
                HorizontalAlignment = edge switch { GridEdges.Left => HorizontalAlignment.Left, GridEdges.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Center },
                VerticalAlignment = edge switch { GridEdges.Bottom => VerticalAlignment.Bottom, GridEdges.Top => VerticalAlignment.Top, _ => VerticalAlignment.Center },
                Margin = edge switch
                {
                    GridEdges.Left => new Thickness(-3, 0, 0, 0), GridEdges.Right => new Thickness(0, 0, -3, 0),
                    GridEdges.Top => new Thickness(0, -3, 0, 0), _ => new Thickness(0, 0, 0, -3)
                },
                Tag = edge
            };
            pill.SetResourceReference(Border.BackgroundProperty, "SettingsAccentText");
            return pill;
        }


        void Arrange(ExpandedGridTile? target)
        {
            var committed = Current();
            candidate = target;
            arranged = target is null ? committed : ExpandedGrid.Place(committed, target);
            int needed = Bottom(arranged) + 1;
            if (needed > rows) SetRows(needed);
            foreach (var (tile, view) in views)
            {
                if (view == dragView) continue;
                var place = arranged.FirstOrDefault(other => ExpandedGrid.SameReading(other, tile)) ?? tile;
                Put(view, RectOf(place), animate: true);
            }
            ShowGhost(target);
        }

        void Follow()
        {
            if (dragTile is not { } tile) return;
            var at = Mouse.GetPosition(board);
            if (dragEdges == GridEdges.None)
            {
                var spot = Mouse.GetPosition(overlay);
                Canvas.SetLeft(floating!, spot.X - grab.X); Canvas.SetTop(floating!, spot.Y - grab.Y);
                double left = at.X - grab.X, top = at.Y - grab.Y;
                // Away from the board nothing makes way, and letting go puts the tile back where it was.
                bool over = at.X > -24 && at.X < board.ActualWidth + 24 && at.Y > -24 && at.Y < board.ActualHeight + 24;
                if (!over) { if (candidate is not null || arranged is null) Arrange(null); return; }
                int column = (int)Math.Round(left / (ColumnWidth() + GridGap));
                int row = (int)Math.Round(top / (GridRowHeight + GridGap));
                var target = ExpandedGrid.Constrain(tile with
                {
                    Column = Math.Clamp(column, 0, ExpandedGrid.ColumnCount - tile.Columns),
                    Row = Math.Clamp(row, 0, Math.Max(0, rows - 1))
                });
                if (target != candidate) Arrange(target);
                return;
            }

            // Resizing: the view follows the pointer, the frame shows the size it settles to.
            double dx = at.X - start.X, dy = at.Y - start.Y;
            var rect = startRect;
            double minimum = ColumnWidth() / 2;
            if (dragEdges.HasFlag(GridEdges.Right)) rect.Width = Math.Clamp(startRect.Width + dx, minimum, board.ActualWidth - startRect.X);
            if (dragEdges.HasFlag(GridEdges.Left))
            {
                double width = Math.Clamp(startRect.Width - dx, minimum, startRect.Right);
                rect.X = startRect.Right - width; rect.Width = width;
            }
            if (dragEdges.HasFlag(GridEdges.Bottom)) rect.Height = Math.Max(GridRowHeight / 2, startRect.Height + dy);
            if (dragEdges.HasFlag(GridEdges.Top))
            {
                double height = Math.Clamp(startRect.Height - dy, GridRowHeight / 2, startRect.Bottom);
                rect.Y = startRect.Bottom - height; rect.Height = height;
            }
            Put(dragView!, rect, animate: false);
            int columns = (int)Math.Round((rect.Width + GridGap) / (ColumnWidth() + GridGap));
            int rowSpan = (int)Math.Round((rect.Height + GridGap) / (GridRowHeight + GridGap));
            int edge = tile.Column + tile.Columns;
            columns = dragEdges.HasFlag(GridEdges.Left) ? Math.Clamp(columns, 1, edge) : Math.Clamp(columns, 1, ExpandedGrid.ColumnCount - tile.Column);
            int bottomEdge = tile.Row + tile.Rows;
            if (dragEdges.HasFlag(GridEdges.Top)) rowSpan = Math.Min(rowSpan, bottomEdge);
            var sized = ExpandedGrid.Constrain(tile with
            {
                Column = dragEdges.HasFlag(GridEdges.Left) ? edge - columns : tile.Column,
                Columns = columns,
                Rows = Math.Max(1, rowSpan)
            });
            // From the top edge the bottom stays where it was.
            if (dragEdges.HasFlag(GridEdges.Top)) sized = sized with { Row = Math.Max(0, bottomEdge - sized.Rows) };
            if (sized != candidate) Arrange(sized);
        }

        void Begin(ExpandedGridTile tile, Border? view, GridEdges edges, Point pressed, Size size, Point? from = null)
        {
            dragTile = tile; fromBoard = view is not null; dragEdges = edges; dragView = view; candidate = null; arranged = null;
            // From where the press was, so the edge does not jump by the distance moved before the drag began.
            start = from ?? Mouse.GetPosition(board);
            if (view is not null) startRect = targets.TryGetValue(view, out var rect) ? rect : RectOf(tile);
            if (edges == GridEdges.None)
            {
                grab = pressed;
                floating = TileView(GridTileName(tile), unavailable: false, active: true);
                floating.SetResourceReference(Border.BackgroundProperty, "SettingsAccentTintStrong");
                floating.Width = size.Width; floating.Height = size.Height;
                floating.Opacity = .95;
                overlay.Children.Add(floating);
                if (view is not null) view.Opacity = 0;
            }
            else if (view is not null) Panel.SetZIndex(view, 2);
            // Under the capture the pointer takes the board's cursor, so it keeps the shape it had over the tile or its edge.
            root.Cursor = CursorFor(edges);
            page = GridScrollHost(root);
            root.CaptureMouse();
            scroll.Start();
            Follow();
        }
        void End(bool commit)
        {
            if (dragTile is null) return;
            var target = candidate;
            var tile = dragTile;
            dragTile = null;
            scroll.Stop();
            root.Cursor = null;
            if (floating is not null) { overlay.Children.Remove(floating); floating = null; }
            if (dragView is not null) { dragView.Opacity = 1; Panel.SetZIndex(dragView, 0); }
            dragView = null;
            ShowGhost(null);
            if (root.IsMouseCaptured) root.ReleaseMouseCapture();
            if (commit && target is not null) Place(target, focusKeyboard: true);
            else
            {
                selected = fromBoard ? tile : selected;
                Build(force: true, focusKeyboard: fromBoard);
            }
        }
        root.MouseMove += (_, _) => { if (dragTile is not null && root.IsMouseCaptured) Follow(); };
        root.MouseLeftButtonUp += (_, e) => { if (dragTile is not null) { e.Handled = true; End(commit: true); } };
        // LostMouseCapture bubbles: the board or a waiting reading handing the pointer on to the editor must not end the drag, only
        // the editor's own loss of it (another window, a click elsewhere).
        root.LostMouseCapture += (_, e) => { if (e.OriginalSource == root) End(commit: false); };
        root.PreviewKeyDown += (_, e) => { if (dragTile is not null && e.Key == Key.Escape) { e.Handled = true; End(commit: false); } };
        // Near the top or the bottom of the settings page while dragging, the page scrolls on.
        scroll.Tick += (_, _) =>
        {
            if (page is null || dragTile is null) return;
            double y = Mouse.GetPosition(page).Y;
            double step = y < 28 ? -10 : y > page.ViewportHeight - 28 ? 10 : 0;
            if (step == 0) return;
            page.ScrollToVerticalOffset(page.VerticalOffset + step);
            Follow();
        };

        // A press on a tile or a waiting reading becomes a drag once the pointer has moved a little; a press that does not move
        // selects the tile, or adds the reading at the end.
        void Pressable(FrameworkElement control, Func<ExpandedGridTile> tile, Border? view, Func<Point, GridEdges> edgesAt)
        {
            Point? pressed = null;
            GridEdges edges = GridEdges.None;
            control.PreviewMouseLeftButtonDown += (_, e) =>
            {
                var at = e.GetPosition(control);
                pressed = at; edges = edgesAt(at);
            };
            control.PreviewMouseLeftButtonUp += (_, _) => pressed = null;
            control.PreviewMouseMove += (_, e) =>
            {
                if (pressed is not { } down || e.LeftButton != MouseButtonState.Pressed || dragTile is not null) { pressed = null; return; }
                var at = e.GetPosition(control);
                if (Math.Abs(at.X - down.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(at.Y - down.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = null;
                var reading = tile();
                var size = view is not null ? view.RenderSize : RectOf(reading).Size;
                var grabAt = view is not null ? down : new Point(size.Width / 2, size.Height / 2);
                Begin(reading, view, edges, grabAt, size);
                e.Handled = true;
            };
        }

        // Which edges of a tile resize it. The right and the bottom are its own: the right while it can be narrower or wider, the
        // bottom for a reading that may be more than one row high (ExpandedGrid.RowRange). The left and the top only where there is
        // room to grow that way: an edge against the board's left or top shows no handle.
        static GridEdges Resizable(ExpandedGridTile tile)
        {
            var allowed = GridEdges.None;
            var (least, most) = ExpandedGrid.RowRange(tile.Item);
            if (most > least) { allowed |= GridEdges.Bottom; if (tile.Row > 0) allowed |= GridEdges.Top; }
            if (tile.Column > 0) allowed |= GridEdges.Left;
            if (tile.Columns > 1 || tile.Column + tile.Columns < ExpandedGrid.ColumnCount) allowed |= GridEdges.Right;
            return allowed;
        }
        Rect Where(ExpandedGridTile tile, Border view) => targets.TryGetValue(view, out var rect) ? rect : RectOf(tile);

        // What the pointer is at on the board: an edge within GridEdge of it on either side (the nearest one, and not reaching into
        // another tile), else the tile under it.
        (ExpandedGridTile Tile, Border View, GridEdges Edges)? Hit(Point at)
        {
            (ExpandedGridTile, Border)? inside = null;
            foreach (var (tile, view) in views) if (Where(tile, view).Contains(at)) { inside = (tile, view); break; }
            (ExpandedGridTile, Border, GridEdges)? best = null;
            double nearest = double.MaxValue;
            foreach (var (tile, view) in views)
            {
                var rect = Where(tile, view);
                if (inside is { } under && under.Item2 != view) continue;
                var allowed = Resizable(tile);
                var edges = GridEdges.None;
                double distance = double.MaxValue;
                bool besideRows = at.Y >= rect.Top - (allowed.HasFlag(GridEdges.Top) ? GridEdge : 0) && at.Y <= rect.Bottom + (allowed.HasFlag(GridEdges.Bottom) ? GridEdge : 0);
                bool besideColumns = at.X >= rect.Left - GridEdge && at.X <= rect.Right + GridEdge;
                double left = Math.Abs(at.X - rect.Left), right = Math.Abs(at.X - rect.Right), bottom = Math.Abs(at.Y - rect.Bottom), top = Math.Abs(at.Y - rect.Top);
                if (besideRows && allowed.HasFlag(GridEdges.Left) && left <= GridEdge && left <= right) { edges |= GridEdges.Left; distance = left; }
                else if (besideRows && allowed.HasFlag(GridEdges.Right) && right <= GridEdge) { edges |= GridEdges.Right; distance = right; }
                if (besideColumns && allowed.HasFlag(GridEdges.Top) && top <= GridEdge && top <= bottom) { edges |= GridEdges.Top; distance = Math.Min(distance, top); }
                else if (besideColumns && allowed.HasFlag(GridEdges.Bottom) && bottom <= GridEdge) { edges |= GridEdges.Bottom; distance = Math.Min(distance, bottom); }
                if (edges != GridEdges.None && distance < nearest) { best = (tile, view, edges); nearest = distance; }
            }
            if (best is { } edge) return edge;
            return inside is { } over ? (over.Item1, over.Item2, GridEdges.None) : null;
        }

        // The handles and the remove button of the tile under the pointer, the handle at its edge brighter.
        void ShowPills(Border? view, GridEdges edges)
        {
            if (hovered is not null && hovered != view && pillsOf.TryGetValue(hovered, out var old))
                foreach (var pill in old) pill.Visibility = Visibility.Hidden;
            hovered = view;
            if (view is null || !pillsOf.TryGetValue(view, out var pills)) return;
            foreach (var pill in pills)
            {
                pill.Visibility = Visibility.Visible;
                if (pill.Tag is GridEdges edge)
                    pill.SetResourceReference(Border.BackgroundProperty, edges.HasFlag(edge) ? "SettingsTextPrimary" : "SettingsAccentText");
            }
        }
        board.MouseMove += (_, e) =>
        {
            if (dragTile is not null || press is not null) return;
            var hit = Hit(e.GetPosition(board));
            board.Cursor = hit is { } over ? CursorFor(over.Edges) : null;
            ShowPills(hit?.View, hit?.Edges ?? GridEdges.None);
        };
        board.MouseLeave += (_, _) => { if (dragTile is null && press is null) { board.Cursor = null; ShowPills(null, GridEdges.None); } };
        board.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (dragTile is not null) return;
            // The remove button takes its own click.
            for (var element = e.OriginalSource as DependencyObject; element is not null && element != board; element = VisualTreeHelper.GetParent(element))
                if (element is Button) return;
            var at = e.GetPosition(board);
            if (Hit(at) is not { } hit) return;
            press = (hit.Tile, hit.View, hit.Edges, at);
            hit.View.Focus();
            board.CaptureMouse();
            e.Handled = true;
        };
        board.PreviewMouseMove += (_, e) =>
        {
            if (press is not { } down || dragTile is not null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { press = null; if (board.IsMouseCaptured) board.ReleaseMouseCapture(); return; }
            var at = e.GetPosition(board);
            if (Math.Abs(at.X - down.At.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(at.Y - down.At.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            press = null;
            var rect = Where(down.Tile, down.View);
            Begin(down.Tile, down.View, down.Edges, new Point(down.At.X - rect.X, down.At.Y - rect.Y), rect.Size, down.At);
            e.Handled = true;
        };
        board.PreviewMouseLeftButtonUp += (_, _) => { if (press is not null) { press = null; if (board.IsMouseCaptured) board.ReleaseMouseCapture(); } };

        FrameworkElement BoardTile(ExpandedGridTile tile, bool unavailable, bool active)
        {
            string name = GridTileName(tile);
            var view = TileView(name, unavailable, active);
            view.Tag = tile;
            view.Focusable = true;
            view.FocusVisualStyle = (Style)FindResource("SettingsFocus");
            AutomationProperties.SetName(view, Loc.T($"{name}，第 {tile.Row + 1} 行第 {tile.Column + 1} 列，{tile.Columns} 列 × {tile.Rows} 行",
                $"{name}, row {tile.Row + 1}, column {tile.Column + 1}, {tile.Columns} × {tile.Rows}"));
            AutomationProperties.SetHelpText(view, Loc.T("方向键移动，Shift 加方向键调整大小，Delete 移出网格。",
                "Arrow keys move it, Shift with the arrow keys resizes it, Delete removes it."));

            // The board finds which tile and edge the pointer is at (Hit) and starts the drag; the tile only shows its handles.
            var allowed = Resizable(tile);
            var content = (Grid)view.Child;
            var pills = new List<FrameworkElement>();
            foreach (var edge in new[] { GridEdges.Left, GridEdges.Right, GridEdges.Top, GridEdges.Bottom })
                if (allowed.HasFlag(edge)) { var pill = Pill(edge); pills.Add(pill); content.Children.Add(pill); }
            // A cross in the top right corner while the pointer is on the tile removes it from the grid.
            var remove = RemoveCross(Loc.T($"将{name}移出网格", $"Remove {name} from the grid"), () => Remove(tile));
            content.Children.Add(remove);
            pills.Add(remove);
            pillsOf[view] = pills;

            view.GotKeyboardFocus += (_, _) =>
            {
                selected = tile;
                foreach (var (other, otherView) in views)
                    otherView.SetResourceReference(Border.BackgroundProperty, ExpandedGrid.SameReading(other, tile) ? "SettingsAccentTintStrong" : "SettingsAccentTint");
                RefreshOptions();
            };
            view.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Delete) { Remove(tile); e.Handled = true; return; }
                int x = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                int y = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                if (x == 0 && y == 0) return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    int columns = Math.Clamp(tile.Columns + x, 1, ExpandedGrid.ColumnCount - tile.Column);
                    Place(ExpandedGrid.Constrain(tile with { Columns = columns, Rows = Math.Max(1, tile.Rows + y) }), focusKeyboard: true);
                }
                else
                    Place(tile with
                    {
                        Column = Math.Clamp(tile.Column + x, 0, ExpandedGrid.ColumnCount - tile.Columns),
                        Row = Math.Clamp(tile.Row + y, 0, Math.Max(0, rows - 1))
                    }, focusKeyboard: true);
                e.Handled = true;
            };
            return view;
        }

        // The tile's width is the user's to drag, so memory's bar always fills its tile: there is no bar length to choose here.
        void RefreshOptions() => reset.IsEnabled = S.ExpandedGridTiles is not null;

        void Layout()
        {
            DrawCells();
            if (dragTile is not null) return;
            foreach (var (tile, view) in views) Put(view, RectOf(tile), animate: false);
        }

        void Build(bool force = false, bool focusKeyboard = false)
        {
            if (!S.ExpandedCustomCards || dragTile is not null) return;
            var tiles = Current();
            var drives = GridDriveLetters();
            if (!force && initialized && tiles.SequenceEqual(built) && drives.SequenceEqual(builtDrives)) return;
            initialized = true;
            built = [.. tiles]; builtDrives = drives;
            if (selected is { } target) selected = tiles.FirstOrDefault(tile => ExpandedGrid.SameReading(tile, target));
            tilesLayer.Children.Clear();
            views.Clear();
            targets.Clear();
            pillsOf.Clear();
            hovered = null;
            int bottom = Bottom(tiles);
            SetRows(bottom + 1);
            Border? focus = null;
            foreach (var tile in tiles)
            {
                bool active = selected is { } current && ExpandedGrid.SameReading(tile, current);
                var view = (Border)BoardTile(tile, tile.Letter is { } letter && !drives.Contains(letter, StringComparer.OrdinalIgnoreCase), active);
                views.Add((tile, view));
                tilesLayer.Children.Add(view);
                if (board.ActualWidth > 0) Put(view, RectOf(tile), animate: false);
                if (active) focus = view;
            }

            palette.Children.Clear();
            foreach (var reading in ExpandedMonitor.Order.SelectMany(item => item == ExpandedItem.Storage
                ? drives.Select(letter => new ExpandedGridTile(item, letter, 0, bottom, 1, 1))
                : [new ExpandedGridTile(item, null, 0, bottom,
                    item is ExpandedItem.Cpu or ExpandedItem.Gpu or ExpandedItem.Power or ExpandedItem.Frames or ExpandedItem.Processes ? ExpandedGrid.ColumnCount : ExpandedGrid.ColumnCount / 2,
                    item is ExpandedItem.Cpu or ExpandedItem.Gpu or ExpandedItem.Processes ? 2 : 1)]))
            {
                if (tiles.Any(tile => ExpandedGrid.SameReading(tile, reading))) continue;
                var button = ActionButton(GridTileName(reading), () => Place(reading, focusKeyboard: true));
                button.Tag = reading;
                button.Style = (Style)FindResource("SettingsOutlineButton");
                button.Margin = new Thickness(0, 0, 6, 6);
                button.Cursor = Cursors.SizeAll;
                AutomationProperties.SetHelpText(button, Loc.T("拖入网格，或点击添加到末尾。", "Drag it into the grid, or click to add it at the end."));
                Pressable(button, () => reading, null, _ => GridEdges.None);
                palette.Children.Add(button);
            }
            paletteLabel.Visibility = palette.Visibility = palette.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshOptions();
            if (focusKeyboard) focus?.Focus();
        }

        board.SizeChanged += (_, _) => Layout();
        void OnReading(MonitorSnapshot _) => Build();
        void WatchDrives() => _app.WatchMonitorGridDrives(panel.IsLoaded && panel.IsVisible && S.ExpandedCustomCards);
        root.Loaded += (_, _) => { _app.MonitorUpdated += OnReading; Build(); WatchDrives(); };
        root.IsVisibleChanged += (_, _) => WatchDrives();
        root.Unloaded += (_, _) => { _app.MonitorUpdated -= OnReading; _app.WatchMonitorGridDrives(false); End(commit: false); };
        refresh.Add(() => Build());
        return KeepPreview(root);
    }

    // A small cross in a tile's top right corner, shown while the pointer is on the tile (ShowPills).
    private Button RemoveCross(string name, Action remove)
    {
        var cross = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 1,1 L 9,9 M 9,1 L 1,9"), StrokeThickness = 1.3, Width = 10, Height = 10 };
        cross.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var button = new Button
        {
            Style = (Style)FindResource("SettingsGlyphButton"), Content = cross, Width = 20, Height = 20, Margin = new Thickness(0, 2, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden,
            ToolTip = Loc.T("移出网格", "Remove from the grid")
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => remove();
        return button;
    }

    // The settings page's own scroller, which a drag near its top or bottom scrolls.
    private static ScrollViewer? GridScrollHost(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer found) return found;
        return null;
    }
}
