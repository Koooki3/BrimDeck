using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private readonly HashSet<ProviderId> _modelApps = [];
    private readonly HashSet<ProviderId> _knownModelApps = [];
    private UsageDateRange? _customRange;
    private DateTime? _historyStart;
    private ScrollViewer? _modelScroll;
    private double _modelScrollOffset;
    private Window? _detailPopup;
    internal UsageDateRange ModelRange => _customRange ?? UsageDateRange.Recent(_days, DateTime.Today);

    private Button DetailLink(UIElement content, string name, Action click)
    {
        var button = new Button { Content = content, Style = (Style)FindResource("DetailLinkButton") };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private void SynchronizeModelSelection(IReadOnlyList<ProviderId> apps)
    {
        bool all = _knownModelApps.SetEquals(_modelApps);
        var enabled = apps.ToHashSet();
        _modelApps.IntersectWith(enabled);
        if (all || _modelApps.Count == 0) _modelApps.UnionWith(enabled);
        _knownModelApps.Clear(); _knownModelApps.UnionWith(enabled);
    }

    internal void OpenModelDetails(ProviderId provider, int days)
    {
        if (IsMusicPage && Settings.UsagePage) SelectPage(DeckPage.Usage, remember: false);
        _modelApps.Clear(); _modelApps.Add(provider);
        _knownModelApps.Clear(); _knownModelApps.UnionWith(Settings.StatisticsProviders);
        _customRange = null; _days = days; _details = true;
        ResetModelScroll(); RenderUsage();
    }

    private void ResetModelScroll() { _modelScroll?.ScrollToTop(); _modelScroll = null; _modelScrollOffset = 0; }
    internal void ToggleModelApp(ProviderId provider)
    {
        if (!_modelApps.Contains(provider)) _modelApps.Add(provider);
        else if (_modelApps.Count > 1) _modelApps.Remove(provider);
        ResetModelScroll(); RenderUsage();
    }
    internal void SelectModelPeriod(int days)
    {
        _ = UsageDateRange.Recent(days, DateTime.Today);
        _days = days; _customRange = null; ResetModelScroll(); RenderUsage();
    }
    internal void SelectModelRange(UsageDateRange range)
    {
        if (range.End > DateTime.Today) throw new ArgumentException(Loc.T("结束日期不能晚于今天。", "The end date cannot be later than today."));
        _customRange = range; ResetModelScroll();
        var earliest = _historyStart ?? DateTime.Today.AddDays(-29);
        if (range.Start < earliest)
        {
            _historyStart = range.Start;
            _ = RefreshAsync();
        }
        RenderUsage();
    }

    private string ModelPeriodLabel => _customRange is { } range ? $"{range.Start:MM-dd}–{range.End:MM-dd}" : PeriodName(_days);
    private static string PeriodName(int days) => days == 1 ? Loc.T("今日", "Today") : Loc.T($"{days} 天", $"{days} days");

    private Button ModelAction(string text, string name, Action click, bool active = false, bool icon = false)
    {
        var label = UI.Centered(text, icon ? 14 : 12, UI.Secondary, font: icon ? new FontFamily("Segoe MDL2 Assets") : null);
        label.ClearValue(TextBlock.ForegroundProperty); label.ClearValue(TextBlock.FontWeightProperty);
        var button = new Button { Content = label, Height = 24, Padding = new Thickness(icon ? 6 : 9, 0, icon ? 6 : 9, 0),
            Style = (Style)FindResource("SegmentButton"), Tag = active ? "Selected" : null, ToolTip = name };
        AutomationProperties.SetName(button, name); button.Click += (_, _) => click(); return button;
    }

    private string StatisticsTheme(ProviderId id) => Settings.EnabledApps.FirstOrDefault(app => app.UsageSource == id)?.Theme ?? UI.ColorFor(id);

    private FrameworkElement ModelApplicationFilter(IReadOnlyList<ProviderId> apps, int stage)
    {
        if (stage == 4)
            return ModelAction(Loc.T($"应用 · {_modelApps.Count} ▾", $"Apps · {_modelApps.Count} ▾"), Loc.T("筛选应用", "Filter apps"), () => OpenModelApplications(apps));
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var app in apps)
        {
            var name = AppPresets.Name(app);
            var label = stage == 0 ? name : stage == 1 ? name.Length > 3 ? name[..3] + "…" : name : name[..1];
            var button = ModelAction(label, Loc.T("筛选 ", "Filter ") + name, () => ToggleModelApp(app), _modelApps.Contains(app));
            button.Padding = new Thickness(stage >= 2 ? 5 : 8, 0, stage >= 2 ? 5 : 8, 0);
            if (stage >= 2)
            {
                var contents = new StackPanel { Orientation = Orientation.Horizontal };
                contents.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(StatisticsTheme(app)), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                var labelContent = (UIElement)button.Content; button.Content = null;
                contents.Children.Add(labelContent); button.Content = contents;
            }
            strip.Children.Add(button);
        }
        return new Border { Child = strip, Background = UI.Brush(UI.Fill), CornerRadius = new CornerRadius(7), Padding = new Thickness(1) };
    }

    private Button ModelDateButton(bool iconOnly)
    {
        Button button = null!;
        button = ModelAction(iconOnly ? "\uE787" : ModelPeriodLabel, Loc.T("按时间", "By date"), () => OpenModelDates(button), _customRange is not null, iconOnly);
        if (!iconOnly)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(UI.Centered("\uE787", 12, UI.Secondary, font: new FontFamily("Segoe MDL2 Assets")));
            var label = UI.Centered(_customRange is null ? Loc.T("按时间", "By date") : ModelPeriodLabel, 12, _customRange is null ? UI.Secondary : UI.Primary);
            label.Margin = new Thickness(6, 0, 0, 0); content.Children.Add(label); button.Content = content;
        }
        button.ToolTip = _customRange is { } range
            ? Loc.T($"按时间\n{range.Start:yyyy-MM-dd} 至 {range.End:yyyy-MM-dd}（含结束日期）", $"By date\n{range.Start:yyyy-MM-dd} to {range.End:yyyy-MM-dd} (end date included)")
            : Loc.T("按时间", "By date");
        return button;
    }

    private void RenderModelToolbar(IReadOnlyList<ProviderId> apps)
    {
        ModelToolbar.ColumnDefinitions.Clear();
        ModelToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        ModelToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        ModelToolbar.ColumnDefinitions.Add(new ColumnDefinition());
        var back = ModelAction("\uE76B", Loc.T("返回用量", "Back to usage"), () => ShowModelDetails(false), icon: true);
        ModelToolbar.Children.Add(back);
        var filters = new Grid { Name = "ModelFilters", HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < 5; i++) filters.ColumnDefinitions.Add(new ColumnDefinition
        { Width = i % 2 == 0 ? GridLength.Auto : new GridLength(8) });
        Border Periods(double padding) => Segmented(new[] { 1, 7, 30 }.Select(days => new SegmentItem(PeriodName(days), PeriodName(days),
            _customRange is null && days == _days, () => SelectModelPeriod(days))).ToList(), 12, 22, padding, centerText: true);
        var periods = Periods(7);
        FrameworkElement applications = null!; Button dates = null!;
        double available = Math.Max(0, ContentCanvas.Width - ModelToolbar.Margin.Left - ModelToolbar.Margin.Right - 36);
        for (int stage = 0; stage <= 4; stage++)
        {
            applications = ModelApplicationFilter(apps, stage);
            dates = ModelDateButton(stage >= 3 && _customRange is null);
            if (stage == 4) periods = Periods(3);
            foreach (var element in new[] { applications, periods, dates }) element.Measure(new Size(double.PositiveInfinity, 28));
            if (applications.DesiredSize.Width + periods.DesiredSize.Width + dates.DesiredSize.Width + 16 <= available) break;
            if (stage == 4)
                applications = ModelAction(Loc.T("应用 ▾", "Apps ▾"), Loc.T("筛选应用", "Filter apps"), () => OpenModelApplications(apps));
        }
        Grid.SetColumn(periods, 2); Grid.SetColumn(dates, 4);
        filters.Children.Add(applications); filters.Children.Add(periods); filters.Children.Add(dates);
        Grid.SetColumn(filters, 2); ModelToolbar.Children.Add(filters);
    }

    private void OpenModelDates(FrameworkElement anchor)
    {
        var range = ModelRange;
        var popup = new ModelDateWindow(range);
        void PreserveReturnArea() => _popupReturnArea = new Rect(popup.PointToScreen(new Point()), popup.PointToScreen(new Point(popup.ActualWidth, popup.ActualHeight)));
        popup.Applied += selected =>
        {
            PreserveReturnArea();
            SelectModelRange(selected);
        };
        popup.Cancelled += PreserveReturnArea;
        ShowModelPopup(popup, anchor);
    }

    private void OpenModelApplications(IReadOnlyList<ProviderId> apps)
    {
        var stack = new StackPanel();
        var popup = new Window { Title = Loc.T("筛选应用", "Filter apps"), Width = 210, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true };
        foreach (var app in apps)
        {
            Button button = null!;
            button = ModelAction(AppPresets.Name(app), Loc.T("筛选 ", "Filter ") + AppPresets.Name(app), () =>
            { ToggleModelApp(app); button.Tag = _modelApps.Contains(app) ? "Selected" : null; }, _modelApps.Contains(app));
            button.Margin = new Thickness(0, 3, 0, 3); stack.Children.Add(button);
        }
        popup.Content = new Border { Child = stack, Background = UI.Brush("#1A1A1D"), CornerRadius = new CornerRadius(10), Padding = new Thickness(8), BorderBrush = UI.Brush(UI.Outline), BorderThickness = new Thickness(1) };
        ShowModelPopup(popup, ModelToolbar);
    }

    private void ShowModelPopup(Window popup, FrameworkElement anchor)
    {
        if (_detailPopup is not null) { _detailPopup.Activate(); return; }
        _leave.Stop(); _popupReturnArea = null; _detailPopup = popup; popup.Owner = this;
        // A layered window renders grayscale text unless told otherwise; its rounded background is opaque, so ClearType is safe.
        RenderOptions.SetClearTypeHint(popup, ClearTypeHint.Enabled);
        var point = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        point = transform.Transform(point);
        // The primary monitor's work area in this window's DIPs, the same units as the point above. SystemParameters.WorkArea
        // uses the scale the session started with, which differs after the primary's scale changes.
        var (_, primaryWork, _) = Native.WindowsHost.PrimaryScreen();
        var work = new Rect(transform.Transform(primaryWork.TopLeft), transform.Transform(primaryWork.BottomRight));
        popup.Left = Math.Clamp(point.X - popup.Width, work.Left, Math.Max(work.Left, work.Right - popup.Width)); popup.Top = point.Y + 3;
        popup.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { popup.Close(); SetExpanded(false); e.Handled = true; } };
        // Closing an active owned window also raises Deactivated. Do not close it
        // again from that nested native activation message.
        bool closing = false;
        popup.Closing += (_, _) => { closing = true; if (ReferenceEquals(_detailPopup, popup)) _detailPopup = null; };
        popup.Deactivated += (_, _) => { if (!closing && popup.IsVisible) popup.Close(); };
        popup.Closed += (_, _) =>
        {
            _detailPopup = null; RenderUsage();
            if (_expanded && !IsMouseOver) PointerChanged(false);
        };
        popup.Show(); popup.Activate();
    }

    private static string TokenBreakdown(IEnumerable<TokenEntry> source)
    {
        var entries = source.ToList();
        return Loc.T($"输入 {entries.Sum(t => t.Input):N0}\n缓存写入 {entries.Sum(t => t.CacheWrite + t.CacheWriteHour):N0}\n缓存读取 {entries.Sum(t => t.CacheRead):N0}\n输出 {entries.Sum(t => t.Output):N0}\nToken 合计 {entries.Sum(t => t.Total):N0}",
            $"Input {entries.Sum(t => t.Input):N0}\nCache write {entries.Sum(t => t.CacheWrite + t.CacheWriteHour):N0}\nCache read {entries.Sum(t => t.CacheRead):N0}\nOutput {entries.Sum(t => t.Output):N0}\nTotal tokens {entries.Sum(t => t.Total):N0}");
    }

    private void RenderModels(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        var result = ModelUsage.Filter(snapshots, _modelApps, ModelRange);
        bool expanded = ContentCanvas.Width >= 600, compact = ContentCanvas.Width < 640 || ContentCanvas.Height < 170, tiny = ContentCanvas.Height < 170;
        double font = tiny ? 11.5 : 12.5, rowHeight = tiny ? 22 : 27, headerHeight = tiny ? 18 : 22, totalHeight = tiny ? 24 : 29;
        double inset = tiny ? 14 : 20, gap = expanded ? 8 : 12;
        UsageContent.Margin = new Thickness(inset, 0, inset, tiny ? 8 : 10);
        string[] Values(IEnumerable<TokenEntry> source)
        {
            var entries = source.ToList();
            return expanded ? [UI.Number(entries.Sum(t => t.Input)), UI.Number(entries.Sum(t => t.CacheWrite + t.CacheWriteHour)),
                UI.Number(entries.Sum(t => t.CacheRead)), UI.Number(entries.Sum(t => t.Output)), UI.Number(entries.Sum(t => t.Total)), UI.Cost(entries, _app.Prices)]
                : [UI.Number(entries.Sum(t => t.Total)), UI.Cost(entries, _app.Prices)];
        }
        var totals = Values(result.Entries);
        bool incomplete = result.IncompleteProviders.Count > 0;
        if (incomplete && result.Entries.Count == 0) totals = totals.Select(_ => "—").ToArray();
        double[] widths = expanded ? [compact ? 72 : 80, 0, ..Enumerable.Repeat(compact ? 52d : 56d, 4), compact ? 58 : 62, compact ? 64 : 78] : [84, 0, 84, 84];
        var values = result.Rows.Select(row => Values(row.Entries)).ToArray();
        var headings = expanded
            ? new[] { Loc.T("输入", "Input"), Loc.T("缓存写入", "Cache write"), Loc.T("缓存读取", "Cache read"), Loc.T("输出", "Output"), Loc.T("Token 合计", "Total tokens"), Loc.T("费用 · USD", "Cost · USD") }
            : [Loc.T("Token 合计", "Total tokens"), Loc.T("费用 · USD", "Cost · USD")];
        for (int n = 0; n < totals.Length; n++)
        {
            // English headings are longer than the numbers under them; a column also fits its heading.
            var heading = UI.Centered(headings[n], tiny ? 10 : 11, UI.Tertiary); heading.Measure(new Size(double.PositiveInfinity, headerHeight));
            widths[n + 2] = Math.Max(widths[n + 2], Math.Ceiling(heading.DesiredSize.Width));
            // Amounts such as $1,234.56 + give up model-name space, never numeric digits.
            foreach (var value in values.Select(row => row[n]).Append(totals[n]))
            {
                var text = UI.Centered(value, font, UI.Primary, FontWeights.SemiBold); text.Measure(new Size(double.PositiveInfinity, rowHeight));
                widths[n + 2] = Math.Max(widths[n + 2], Math.Ceiling(text.DesiredSize.Width));
            }
        }
        var panel = new Grid { Name = "ModelTable" };
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight + 1) });
        panel.RowDefinitions.Add(new RowDefinition()); panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(totalHeight + 1) });
        Grid Row(string app, string model, string[] numbers, bool header, bool total, ProviderId? provider = null)
        {
            double height = header ? headerHeight : total ? totalHeight : rowHeight;
            var row = new Grid { Height = height, Name = header ? "ModelHeader" : total ? "ModelTotal" : "ModelDataRow" };
            for (int i = 0; i < widths.Length; i++)
            {
                if (i > 0) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i]) });
            }
            string[] labels = [app, model, ..numbers];
            for (int i = 0; i < labels.Length; i++)
            {
                var text = UI.Centered(labels[i], header ? tiny ? 10 : 11 : font,
                    header || total && i < 2 ? UI.Tertiary : i == 1 || i >= 2 && i < widths.Length - 2 && !total ? UI.Secondary : UI.Primary,
                    !header && (total && i >= 2 || !total && i == 0 || i == widths.Length - 1) ? FontWeights.SemiBold : FontWeights.Normal);
                text.TextAlignment = i >= 2 ? TextAlignment.Right : TextAlignment.Left;
                // The app and model cells show no tooltip: it would only repeat the visible name.
                if (i >= 2) text.ToolTip = labels[i];
                FrameworkElement cell = text;
                if (i == 0 && provider is { } id)
                {
                    var box = new Grid(); box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) }); box.ColumnDefinitions.Add(new ColumnDefinition());
                    // Match the visible Latin glyphs, whose center sits just below the font's line-box center.
                    box.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = UI.Brush(StatisticsTheme(id)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                        RenderTransform = new TranslateTransform(0, 1) });
                    Grid.SetColumn(text, 1); box.Children.Add(text); cell = box;
                }
                Grid.SetColumn(cell, i * 2); row.Children.Add(cell);
            }
            return row;
        }
        panel.Children.Add(new Border { Child = Row(Loc.T("应用", "App"), Loc.T("模型", "Model"), headings, true, false), BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(0, 0, 0, 1) });
        var list = new StackPanel();
        for (int i = 0; i < result.Rows.Count; i++)
        {
            var record = result.Rows[i];
            var row = Row(AppPresets.Name(record.Provider), record.Model, values[i], false, false, record.Provider);
            var tip = $"{AppPresets.Name(record.Provider)} · {record.Model}\n{TokenBreakdown(record.Entries)}\n{UI.CostNote(record.Entries, _app.Prices)}";
            // The breakdown sits on the figure cells only, so hovering the app or model name shows no tooltip.
            foreach (var text in row.Children.OfType<TextBlock>().Where(text => Grid.GetColumn(text) >= 4)) text.ToolTip = tip;
            var item = new Border { Child = row, Background = Brushes.Transparent, BorderBrush = UI.Brush("#0DFFFFFF"), BorderThickness = new Thickness(0, i > 0 ? 1 : 0, 0, 0) };
            item.MouseEnter += (_, _) => item.Background = UI.Brush("#0AFFFFFF"); item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            list.Children.Add(item);
        }
        // The viewport clip would turn the rows grayscale; they lie over the island's opaque black surface.
        RenderOptions.SetClearTypeHint(list, ClearTypeHint.Enabled);
        // Focusable so the arrow keys scroll the list; an outline around the whole list would only add noise.
        var scroll = new ScrollViewer { Content = list, Style = (Style)FindResource("ModelScrollViewer"), Focusable = true, FocusVisualStyle = null };
        _modelScroll = scroll;
        var offset = _modelScrollOffset;
        scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(offset);
        Grid.SetRow(scroll, 1); panel.Children.Add(scroll);
        if (result.Rows.Count == 0)
        {
            var message = UI.Centered(incomplete ? _refreshing ? Loc.T("正在读取所选时段…", "Reading the selected period…") : Loc.T("所选应用的用量明细暂不可用", "Usage details for the selected apps are not available yet")
                : Loc.T("所选时段暂无用量记录", "No usage records in the selected period"), 12, UI.Tertiary);
            message.HorizontalAlignment = HorizontalAlignment.Center; message.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(message, 1); panel.Children.Add(message);
        }
        string selected = _modelApps.SetEquals(Settings.StatisticsProviders) ? Loc.T("全部", "All") : _modelApps.Count == 1 ? AppPresets.Name(_modelApps.Single()) : Loc.T($"{_modelApps.Count} 个应用", Loc.Count(_modelApps.Count, "app", "apps"));
        string summary = $"{selected} · {ModelPeriodLabel} · " + Loc.T($"{result.Rows.Count} 项", Loc.Count(result.Rows.Count, "item", "items"));
        var totalRow = Row(incomplete ? Loc.T("已知合计", "Known total") : Loc.T("合计", "Total"), summary, totals, false, true);
        string totalTip = $"{summary}\n{ModelRange.Start:yyyy-MM-dd}" + Loc.T(" 至 ", " to ") + $"{ModelRange.End:yyyy-MM-dd}\n{TokenBreakdown(result.Entries)}\n{UI.CostNote(result.Entries, _app.Prices)}";
        if (incomplete) totalTip += Loc.T("\n部分明细尚未读取完整：", "\nSome details are not fully read yet: ") + string.Join(Loc.T("、", ", "), result.IncompleteProviders.Select(AppPresets.Name));
        totalRow.ToolTip = totalTip;
        foreach (var cell in totalRow.Children.OfType<FrameworkElement>()) cell.ToolTip = totalTip;
        var footer = new Border { Child = totalRow, BorderBrush = UI.Brush(UI.Outline), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(footer, 2); panel.Children.Add(footer); UsageContent.Children.Add(panel);
    }
}
