using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private const double GridRowHeight = 48;

    private static string GridTileName(ExpandedGridTile tile) => tile.Letter ?? ExpandedName(tile.Item);

    private string[] GridDriveLetters() => [.. (_app.Monitor?.Latest?.Storage ?? [])
        .SelectMany(drive => drive.Volumes).Select(volume => volume.Letter)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    private FrameworkElement ExpandedGridEditor(List<Action> refresh)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var hint = TextLine(Loc.T("将项目拖入网格，拖动下角改变大小。CPU 和 GPU 占一行时只显示总占用。",
            "Drag readings into the grid and resize from the lower corner. A one-row CPU or GPU tile shows total usage."), 12, TextSecondary);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 2, 0, 8);
        panel.Children.Add(hint);

        var surface = new Grid { AllowDrop = true, Background = Brushes.Transparent };
        AutomationProperties.SetName(surface, Loc.T("展开监控布局网格", "Expanded monitor layout grid"));
        for (int i = 0; i < ExpandedGrid.ColumnCount; i++) surface.ColumnDefinitions.Add(new ColumnDefinition());
        var viewport = new ScrollViewer { Content = surface, MaxHeight = 390, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var preview = GridCell("SettingsAccentTintStrong", "SettingsAccentText");
        preview.Visibility = Visibility.Collapsed;
        preview.IsHitTestVisible = false;
        Panel.SetZIndex(preview, 1);
        var palette = new WrapPanel { Margin = new Thickness(0, 4, 0, 3) };
        var options = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        var paletteLabel = SettingLabel(Loc.T("可添加项目", "Available readings"));
        panel.Children.Add(paletteLabel);
        panel.Children.Add(palette);
        panel.Children.Add(viewport);
        panel.Children.Add(options);

        ExpandedGridTile[] built = [];
        string[] builtDrives = [];
        ExpandedGridTile? selected = null;
        bool initialized = false, interacting = false;

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
        }
        void Remove(ExpandedGridTile tile)
        {
            Save(Current().Where(current => !ExpandedGrid.SameReading(current, tile)).ToList(), null);
            palette.Children.OfType<Button>().FirstOrDefault(button =>
                button.Tag is ExpandedGridTile reading && ExpandedGrid.SameReading(reading, tile))?.Focus();
        }

        double ColumnWidth() => Math.Max(1, surface.ActualWidth / ExpandedGrid.ColumnCount);
        ExpandedGridTile At(ExpandedGridTile tile, Point point) => ExpandedGrid.Constrain(tile with
        {
            Column = Math.Clamp((int)(point.X / ColumnWidth()), 0, ExpandedGrid.ColumnCount - tile.Columns),
            Row = Math.Clamp((int)(point.Y / GridRowHeight), 0, Math.Max(0, surface.RowDefinitions.Count - 1))
        });
        void SetCell(FrameworkElement element, ExpandedGridTile tile)
        {
            Grid.SetColumn(element, tile.Column);
            Grid.SetRow(element, tile.Row);
            Grid.SetColumnSpan(element, tile.Columns);
            Grid.SetRowSpan(element, tile.Rows);
        }
        void ShowPreview(ExpandedGridTile tile)
        {
            SetCell(preview, tile with { Rows = Math.Min(tile.Rows, surface.RowDefinitions.Count - tile.Row) });
            preview.Visibility = Visibility.Visible;
        }
        static ExpandedGridTile? Payload(DragEventArgs eventArgs) =>
            eventArgs.Data.GetData(typeof(ExpandedGridTile)) as ExpandedGridTile;

        surface.DragOver += (_, e) =>
        {
            var tile = Payload(e);
            e.Effects = tile is null ? DragDropEffects.None : DragDropEffects.Move;
            if (tile is not null)
            {
                ShowPreview(At(tile, e.GetPosition(surface)));
                double pointer = e.GetPosition(viewport).Y;
                if (pointer > viewport.ViewportHeight - 22) viewport.LineDown();
                else if (pointer < 22) viewport.LineUp();
            }
            e.Handled = true;
        };
        surface.DragLeave += (_, _) => preview.Visibility = Visibility.Collapsed;
        surface.Drop += (_, e) =>
        {
            var tile = Payload(e);
            preview.Visibility = Visibility.Collapsed;
            if (tile is null) return;
            Place(At(tile, e.GetPosition(surface)), focusKeyboard: true);
            e.Handled = true;
        };

        void AllowMove(FrameworkElement control, ExpandedGridTile tile, Func<MouseButtonEventArgs, bool>? starts = null)
        {
            Point? pressed = null;
            control.PreviewMouseLeftButtonDown += (_, e) =>
            {
                pressed = starts?.Invoke(e) == false ? null : e.GetPosition(control);
            };
            control.PreviewMouseLeftButtonUp += (_, _) => pressed = null;
            control.PreviewMouseMove += (_, e) =>
            {
                if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed) { pressed = null; return; }
                var point = e.GetPosition(control);
                if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = null;
                interacting = true;
                try { DragDrop.DoDragDrop(control, new DataObject(typeof(ExpandedGridTile), tile), DragDropEffects.Move); }
                finally { interacting = false; preview.Visibility = Visibility.Collapsed; }
            };
        }

        Border GridCell(string background, string border)
        {
            var cell = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(2) };
            cell.SetResourceReference(Border.BackgroundProperty, background);
            cell.SetResourceReference(Border.BorderBrushProperty, border);
            return cell;
        }
        FrameworkElement Tile(ExpandedGridTile tile, bool unavailable, bool active)
        {
            string name = GridTileName(tile);
            var tileSurface = GridCell(active ? "SettingsAccentTintStrong" : "SettingsAccentTint",
                active ? "SettingsAccentText" : "SettingsBorder");
            tileSurface.Tag = tile;
            tileSurface.Cursor = Cursors.SizeAll;
            tileSurface.Focusable = true;
            tileSurface.FocusVisualStyle = (Style)FindResource("SettingsFocus");
            tileSurface.ToolTip = Loc.T("方向键移动，Shift+方向键缩放，Delete 移出。", "Arrow keys move, Shift+arrow keys resize, Delete removes.");
            AutomationProperties.SetName(tileSurface, Loc.T($"{name} · 第 {tile.Row + 1} 行第 {tile.Column + 1} 列 · {tile.Columns}×{tile.Rows}",
                $"{name} · row {tile.Row + 1}, column {tile.Column + 1} · {tile.Columns}×{tile.Rows}"));
            AutomationProperties.SetHelpText(tileSurface, (string)tileSurface.ToolTip);
            var content = new Grid();
            tileSurface.Child = content;
            var label = TextLine(unavailable ? Loc.T($"{name}（未连接）", $"{name} (disconnected)") : name, 12.5, TextPrimary, FontWeights.SemiBold);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.Margin = new Thickness(10, 7, 34, 0);
            label.VerticalAlignment = VerticalAlignment.Top;
            content.Children.Add(label);
            bool graph = (tile.Item is ExpandedItem.Cpu or ExpandedItem.Gpu) && tile.Rows > 1;
            bool resizeFromLeft = tile.Column > 0;
            var size = TextLine(graph ? Loc.T($"{tile.Columns}×{tile.Rows} · 含图表", $"{tile.Columns}×{tile.Rows} · graph")
                : $"{tile.Columns}×{tile.Rows}", 11, TextSecondary);
            size.Margin = new Thickness(resizeFromLeft ? 27 : 10, 25, 24, 0);
            size.VerticalAlignment = VerticalAlignment.Top;
            content.Children.Add(size);
            if (graph)
            {
                var bars = new UniformGrid { Rows = 1, Columns = 8, Margin = new Thickness(8, 49, 22, 8), IsHitTestVisible = false };
                foreach (int height in new[] { 11, 19, 15, 24, 17, 27, 21, 15 })
                {
                    var bar = new Border { Height = height, Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(2),
                        VerticalAlignment = VerticalAlignment.Bottom };
                    bar.SetResourceReference(Border.BackgroundProperty, "SettingsAccentTintStrong");
                    bars.Children.Add(bar);
                }
                content.Children.Add(bars);
            }
            var handle = new Thumb { Width = 24, Height = 24, HorizontalAlignment = resizeFromLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Cursor = resizeFromLeft ? Cursors.SizeNESW : Cursors.SizeNWSE, Focusable = false };
            var grip = new FrameworkElementFactory(typeof(TextBlock));
            grip.SetValue(TextBlock.TextProperty, resizeFromLeft ? "◣" : "◢");
            grip.SetValue(TextBlock.FontSizeProperty, 13d);
            grip.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            grip.SetValue(TextBlock.BackgroundProperty, Brushes.Transparent);
            grip.SetResourceReference(TextBlock.ForegroundProperty, "SettingsAccentText");
            handle.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = grip };
            AutomationProperties.SetName(handle, Loc.T("拖动以调整大小", "Drag to resize"));
            content.Children.Add(handle);

            tileSurface.GotKeyboardFocus += (_, _) =>
            {
                selected = tile;
                foreach (var element in surface.Children.OfType<Border>())
                {
                    if (element.Tag is not ExpandedGridTile existing) continue;
                    bool chosen = ExpandedGrid.SameReading(existing, tile);
                    element.SetResourceReference(Border.BackgroundProperty, chosen ? "SettingsAccentTintStrong" : "SettingsAccentTint");
                    element.SetResourceReference(Border.BorderBrushProperty, chosen ? "SettingsAccentText" : "SettingsBorder");
                }
                RefreshOptions();
            };
            tileSurface.MouseLeftButtonDown += (_, _) => tileSurface.Focus();
            AllowMove(tileSurface, tile, _ => !handle.IsMouseOver);
            double dx = 0, dy = 0;
            ExpandedGridTile candidate = tile;
            handle.DragStarted += (_, _) =>
            {
                interacting = true;
                dx = dy = 0;
                candidate = tile;
                AddRows(Math.Min(ExpandedGrid.MaxRows, tile.Row + 4));
            };
            handle.DragDelta += (_, e) =>
            {
                dx += e.HorizontalChange; dy += e.VerticalChange;
                candidate = Sized((int)Math.Round(dx / ColumnWidth()), (int)Math.Round(dy / GridRowHeight));
                ShowPreview(candidate);
            };
            handle.DragCompleted += (_, e) =>
            {
                interacting = false;
                preview.Visibility = Visibility.Collapsed;
                if (!e.Canceled && candidate != tile) Place(candidate, focusKeyboard: true);
                else Build(force: true, focusKeyboard: true);
            };
            ExpandedGridTile Sized(int across, int down)
            {
                int edge = tile.Column + tile.Columns;
                int columns = resizeFromLeft
                    ? Math.Clamp(tile.Columns - across, 1, edge)
                    : Math.Clamp(tile.Columns + across, 1, ExpandedGrid.ColumnCount - tile.Column);
                return ExpandedGrid.Constrain(tile with
                {
                    Column = resizeFromLeft ? edge - columns : tile.Column,
                    Columns = columns,
                    Rows = Math.Clamp(tile.Rows + down, 1, tile.Item is ExpandedItem.Cpu or ExpandedItem.Gpu or ExpandedItem.Processes ? 4 : 1)
                });
            }
            tileSurface.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Delete) { Remove(tile); e.Handled = true; return; }
                int x = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                int y = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                if (x == 0 && y == 0) return;
                Place((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? Sized(x, y) : tile with
                {
                    Column = Math.Clamp(tile.Column + x, 0, ExpandedGrid.ColumnCount - tile.Columns),
                    Row = Math.Clamp(tile.Row + y, 0, Math.Max(0, surface.RowDefinitions.Count - 1))
                }, focusKeyboard: true);
                e.Handled = true;
            };
            return tileSurface;
        }

        void RefreshOptions()
        {
            options.Children.Clear();
            if (selected is { } tile)
            {
                var remove = ActionButton(Loc.T("移出网格", "Remove from grid"), () => Remove(tile));
                remove.Style = (Style)FindResource("SettingsLink");
                DockPanel.SetDock(remove, Dock.Right);
                options.Children.Add(remove);
                if (tile.Item == ExpandedItem.Memory)
                {
                    var length = MusicChoice(Loc.T("内存条长度", "Memory bar length"),
                        S.ExpandedCardOrder.Any(card => card.Item == ExpandedItem.Memory && card.CompactMemoryBar),
                        [(false, Loc.T("常规", "Full")), (true, Loc.T("紧凑", "Compact"))],
                        compact =>
                        {
                            var current = Current();
                            Resize(settings =>
                            {
                                // The migrated card list can also set the seed order. Keep the visible positions when changing the bar.
                                settings.ExpandedGridTiles ??= current;
                                var cards = settings.ExpandedCardOrder.ToList();
                                int index = cards.FindIndex(card => card.Item == ExpandedItem.Memory);
                                if (index < 0) cards.Add(new(ExpandedItem.Memory) { CompactMemoryBar = compact });
                                else cards[index] = cards[index] with { CompactMemoryBar = compact };
                                settings.ExpandedCardOrder = cards;
                            }, DeckPage.Monitor);
                            Build(force: true, focusKeyboard: true);
                        }, keepPreview: true);
                    DockPanel.SetDock(length, Dock.Left);
                    options.Children.Add(length);
                }
            }
            var reset = ActionButton(Loc.T("恢复默认网格", "Restore default grid"), () =>
            {
                bool hadFrames = Current().Any(tile => tile.Item == ExpandedItem.Frames);
                selected = null;
                Resize(settings => settings.ExpandedGridTiles = null, DeckPage.Monitor);
                Build(force: true);
                bool hasFrames = Current().Any(tile => tile.Item == ExpandedItem.Frames);
                if (!hadFrames && hasFrames) AskFrameAccess();
                else if (hadFrames && !hasFrames) FrameAccessChanged?.Invoke();
            });
            reset.Style = (Style)FindResource("SettingsLink");
            reset.HorizontalAlignment = HorizontalAlignment.Right;
            reset.IsEnabled = S.ExpandedGridTiles is not null;
            options.Children.Add(reset);
        }

        void Build(bool force = false, bool focusKeyboard = false)
        {
            if (!S.ExpandedCustomCards || interacting && !force) return;
            var tiles = Current();
            var drives = GridDriveLetters();
            if (!force && initialized && tiles.SequenceEqual(built) && drives.SequenceEqual(builtDrives)) return;
            initialized = true;
            built = [.. tiles]; builtDrives = drives;
            if (selected is { } target) selected = tiles.FirstOrDefault(tile => ExpandedGrid.SameReading(tile, target));
            surface.Children.Clear();
            surface.RowDefinitions.Clear();
            int bottom = tiles.Select(tile => tile.Row + tile.Rows).DefaultIfEmpty(0).Max();
            AddRows(Math.Clamp(bottom + 1, 3, ExpandedGrid.MaxRows));
            FrameworkElement? focus = null;
            foreach (var tile in tiles)
            {
                bool active = selected is { } current && ExpandedGrid.SameReading(tile, current);
                var view = Tile(tile, tile.Letter is { } letter && !drives.Contains(letter, StringComparer.OrdinalIgnoreCase), active);
                SetCell(view, tile);
                surface.Children.Add(view);
                if (active) focus = view;
            }
            preview.Visibility = Visibility.Collapsed;
            surface.Children.Add(preview);
            palette.Children.Clear();
            foreach (var reading in ExpandedMonitor.Order.SelectMany(item => item == ExpandedItem.Storage
                ? drives.Select(letter => new ExpandedGridTile(item, letter, 0, bottom, 1, 1))
                : [new ExpandedGridTile(item, null, 0, bottom,
                    item is ExpandedItem.Cpu or ExpandedItem.Gpu or ExpandedItem.Power or ExpandedItem.Frames or ExpandedItem.Processes ? ExpandedGrid.ColumnCount : 1,
                    item is ExpandedItem.Cpu or ExpandedItem.Gpu or ExpandedItem.Processes ? 2 : 1)]))
            {
                if (tiles.Any(tile => ExpandedGrid.SameReading(tile, reading))) continue;
                var button = ActionButton(GridTileName(reading), () => Place(reading, focusKeyboard: true));
                button.Tag = reading;
                button.Style = (Style)FindResource("SettingsOutlineButton");
                button.Margin = new Thickness(0, 0, 6, 6);
                button.ToolTip = Loc.T("拖入网格，或点击添加到末尾。", "Drag into the grid or click to add at the end.");
                AllowMove(button, reading);
                palette.Children.Add(button);
            }
            paletteLabel.Visibility = palette.Visibility = palette.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshOptions();
            if (focusKeyboard) focus?.Focus();
        }
        void AddRows(int count)
        {
            for (int row = surface.RowDefinitions.Count; row < count; row++)
            {
                surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GridRowHeight) });
                for (int col = 0; col < ExpandedGrid.ColumnCount; col++)
                {
                    var cell = GridCell("SettingsSurface", "SettingsBorder");
                    cell.Opacity = .38;
                    Grid.SetRow(cell, row);
                    Grid.SetColumn(cell, col);
                    surface.Children.Add(cell);
                }
            }
        }
        void OnReading(MonitorSnapshot _) => Build();
        void WatchDrives() => _app.WatchMonitorGridDrives(panel.IsLoaded && panel.IsVisible && S.ExpandedCustomCards);
        panel.Loaded += (_, _) => { _app.MonitorUpdated += OnReading; Build(); WatchDrives(); };
        panel.IsVisibleChanged += (_, _) => WatchDrives();
        panel.Unloaded += (_, _) => { _app.MonitorUpdated -= OnReading; _app.WatchMonitorGridDrives(false); };
        refresh.Add(() => Build());
        return KeepPreview(panel);
    }
}
