using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// An owned window lets the calendar extend past a short island without clipping,
// and accepts keyboard input while the overlay itself stays non-activating.
internal sealed class ModelDateWindow : Window
{
    private DateTime _start, _end, _month;
    private bool _editingEnd;
    private readonly StackPanel _body = new();
    private readonly TextBlock _error = UI.Line("", 11, PanelTheme.Hex("danger"));
    internal event Action<UsageDateRange>? Applied;
    internal event Action? Cancelled;
    internal UsageDateRange? SelectedRange => _start <= _end ? new(_start, _end) : null;

    internal ModelDateWindow(UsageDateRange range)
    {
        _start = range.Start; _end = range.End; _month = new(_start.Year, _start.Month, 1);
        Title = Loc.T("按时间", "By date"); Width = 246; SizeToContent = SizeToContent.Height; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        FontFamily = UI.PanelFont; PanelTheme.Text(this);
        Content = new Border { Child = _body, Background = UI.Brush(PanelTheme.Hex("popup")), BorderBrush = UI.Brush(PanelTheme.Hex("popupBorder")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(PanelTheme.Corner(10)), Padding = new Thickness(8) };
        Render();
    }

    private Button Control(string label, string name, Action click, bool selected = false)
    {
        var text = UI.Centered(label, 12, UI.Primary); text.ClearValue(TextBlock.ForegroundProperty);
        var button = new Button { Content = text, Padding = new Thickness(4, 1, 4, 1), Foreground = UI.Brush(selected ? PanelTheme.Hex("onAccent") : UI.Primary),
            Background = selected ? UI.Brush(UI.Accent) : Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center, MinWidth = 0, MinHeight = 24 };
        AutomationProperties.SetName(button, name); button.Click += (_, _) => click(); return button;
    }

    private void Render()
    {
        _body.Children.Clear();
        var fields = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        fields.ColumnDefinitions.Add(new ColumnDefinition()); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); fields.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (bool end in new[] { false, true })
        {
            var date = end ? _end : _start;
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(21) });
            content.Children.Add(UI.Centered(end ? Loc.T("结束", "End") : Loc.T("开始", "Start"), 10, UI.Tertiary));
            var value = UI.Centered(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 12, UI.Primary);
            Grid.SetRow(value, 1); content.Children.Add(value);
            var field = Control("", end ? Loc.T("选择结束日期", "Choose the end date") : Loc.T("选择开始日期", "Choose the start date"), () => { _editingEnd = end; _month = new(date.Year, date.Month, 1); Render(); });
            field.Content = content; field.Padding = new Thickness(7, 2, 7, 2); field.BorderBrush = UI.Brush(_editingEnd == end ? UI.Accent : UI.Outline);
            field.Background = UI.Brush(PanelTheme.Hex("input")); field.FocusVisualStyle = (Style)FindResource("PanelOutsetKeyboardFocus");
            Grid.SetColumn(field, end ? 2 : 0); fields.Children.Add(field);
        }
        _body.Children.Add(fields);
        var navigation = new Grid { Height = 26, Margin = new Thickness(0, 2, 0, 3) };
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); navigation.ColumnDefinitions.Add(new ColumnDefinition()); navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        var previous = MonthButton(false, () => { _month = _month.AddMonths(-1); Render(); }); previous.IsEnabled = _month > DateTime.MinValue.AddMonths(1); navigation.Children.Add(previous);
        var month = UI.Centered(Loc.IsEnglish ? _month.ToString("MMMM yyyy", Loc.Culture) : _month.ToString("yyyy 年 M 月", CultureInfo.InvariantCulture), 12, UI.Secondary); month.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(month, 1); navigation.Children.Add(month);
        var next = MonthButton(true, () => { _month = _month.AddMonths(1); Render(); }); next.IsEnabled = _month.AddMonths(1) <= DateTime.Today;
        Grid.SetColumn(next, 2); navigation.Children.Add(next); _body.Children.Add(navigation);
        var weekdays = new UniformGrid { Columns = 7, Rows = 1, Height = 18 };
        foreach (var day in Loc.IsEnglish ? new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" } : new[] { "一", "二", "三", "四", "五", "六", "日" })
        { var label = UI.Centered(day, 10, UI.Tertiary); label.TextAlignment = TextAlignment.Center; weekdays.Children.Add(label); }
        _body.Children.Add(weekdays);
        var calendar = new UniformGrid { Columns = 7, Rows = 6 };
        int first = ((int)_month.DayOfWeek + 6) % 7;
        for (int i = 0; i < 42; i++)
        {
            var day = _month.AddDays(i - first);
            var button = Control(day.Day.ToString(CultureInfo.InvariantCulture), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), () => ChooseDate(day), day == _start || day == _end);
            button.Height = 24; button.Padding = new Thickness(0); button.IsEnabled = day <= DateTime.Today;
            if (day != _start && day != _end) button.Foreground = UI.Brush(day.Month == _month.Month ? UI.Primary : UI.Tertiary);
            if (day > _start && day < _end) button.Background = UI.Brush(PanelTheme.Hex("rangeFill"));
            calendar.Children.Add(button);
        }
        _body.Children.Add(calendar);
        _error.Text = _start > _end ? Loc.T("开始日期不能晚于结束日期。", "The start date cannot be later than the end date.") : "";
        _error.Visibility = _start > _end ? Visibility.Visible : Visibility.Collapsed; _error.Margin = new Thickness(0, 5, 0, 0); _body.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 7, 0, 0) };
        var cancel = Control(Loc.T("取消", "Cancel"), Loc.T("取消日期筛选", "Cancel the date filter"), CancelSelection); cancel.Padding = new Thickness(10, 1, 10, 1); cancel.Margin = new Thickness(0, 0, 6, 0); actions.Children.Add(cancel);
        var apply = Control(Loc.T("应用", "Apply"), Loc.T("应用日期筛选", "Apply the date filter"), ApplySelection, true); apply.IsEnabled = SelectedRange is not null; apply.Padding = new Thickness(10, 1, 10, 1); actions.Children.Add(apply); _body.Children.Add(actions);
    }

    private Button MonthButton(bool next, Action click)
    {
        var button = Control("", next ? Loc.T("下个月", "Next month") : Loc.T("上个月", "Previous month"), click);
        var icon = UI.Centered(next ? "\uE76C" : "\uE76B", 10, UI.Primary, font: new FontFamily("Segoe MDL2 Assets"));
        icon.ClearValue(TextBlock.ForegroundProperty); button.Content = icon;
        return button;
    }

    internal void ChooseDate(DateTime date)
    {
        if (date.Date > DateTime.Today) return;
        if (_editingEnd) _end = date.Date;
        else { _start = date.Date; _editingEnd = true; }
        Render();
    }
    internal void ApplySelection()
    {
        if (SelectedRange is not { } range) return;
        Applied?.Invoke(range); Close();
    }
    internal void CancelSelection() { Cancelled?.Invoke(); Close(); }
}
