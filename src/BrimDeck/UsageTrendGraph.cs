using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// Daily usage bars for the model-details page. Values are tokens or USD cost; tooltips name the day and amount.
internal sealed class UsageTrendGraph : FrameworkElement
{
    private PanelLook Look => PanelLook.Of(this);
    private IReadOnlyList<UsageTrendDay> _days = [];
    private bool _cost;
    private string _color = "";
    private int _hot = -1;
    private readonly ToolTip _tip = new();

    public UsageTrendGraph()
    {
        Height = 88;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        _tip.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
        _tip.PlacementTarget = this;
    }

    public void Set(IReadOnlyList<UsageTrendDay> days, bool cost, string color)
    {
        _days = days; _cost = cost; _color = color.Length > 0 ? color : Look.Primary;
        _hot = -1; _tip.IsOpen = false;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int index = Hit(e.GetPosition(this).X);
        if (index == _hot) return;
        _hot = index;
        if (index < 0 || index >= _days.Count) { _tip.IsOpen = false; InvalidateVisual(); return; }
        var day = _days[index];
        string date = day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!_cost)
        {
            _tip.Content = date + "\n" + Loc.T($"{UI.Number(day.Tokens)} Token（{day.Tokens:N0}）", $"{UI.Number(day.Tokens)} tokens ({day.Tokens:N0})");
        }
        else
        {
            var cost = new CostSummary(day.Cost, day.Cost > 0 ? 1 : 0, 0, day.Unpriced);
            string tip = UI.Cost(cost);
            if (day.Unpriced > 0)
                tip += "\n" + (day.Cost == 0
                    ? Loc.T("未匹配到完整价格", "Prices could not be matched for every record")
                    : Loc.T($"{day.Unpriced} 条记录未计价", Loc.Count(day.Unpriced, "record has", "records have") + " no price"));
            _tip.Content = date + "\n" + tip;
        }
        _tip.IsOpen = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hot = -1; _tip.IsOpen = false; InvalidateVisual();
    }

    private int Hit(double x)
    {
        if (_days.Count == 0 || ActualWidth <= 0) return -1;
        var (gap, bar) = Layout();
        if (bar <= 0) return -1;
        double at = Pad;
        for (int i = 0; i < _days.Count; i++)
        {
            if (x >= at && x < at + bar) return i;
            at += bar + gap;
        }
        return -1;
    }

    private const double Pad = UsageTrend.GraphPad, LabelBand = 16;
    private (double Gap, double Bar) Layout() => UsageTrend.Layout(_days.Count, ActualWidth);

    protected override void OnRender(DrawingContext dc)
    {
        // A transparent ground makes zero-height days and the gaps around the labels hit-testable.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_days.Count == 0 || ActualWidth <= 0 || ActualHeight <= LabelBand) return;
        var (gap, bar) = Layout();
        double chartTop = 2, chartBottom = ActualHeight - LabelBand, chartHeight = Math.Max(1, chartBottom - chartTop);
        double max = 0;
        foreach (var day in _days) max = Math.Max(max, _cost ? (double)day.Cost : day.Tokens);
        if (max <= 0) max = 1;
        var fill = UI.Brush(_color);
        var hot = UI.Brush(Look.Primary);
        var labelBrush = UI.Brush(Look.Tertiary);
        var typeface = new Typeface(UI.PanelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool labelDays = _days.Count <= 10;
        double x = Pad;
        for (int i = 0; i < _days.Count; i++)
        {
            var day = _days[i];
            bool unknownCost = _cost && day.Unpriced > 0 && day.Cost == 0;
            double value = _cost ? (double)day.Cost : day.Tokens;
            double height = unknownCost ? 2 : Math.Max(value > 0 ? 2 : 0, chartHeight * (value / max));
            // Exact bar edges keep a sub-pixel layout from clipping the last day when width is rounded up.
            var rect = new Rect(x, Math.Round(chartBottom - height), Math.Max(0, bar), Math.Round(height));
            if (unknownCost) dc.PushOpacity(0.45);
            dc.DrawRoundedRectangle(i == _hot ? hot : fill, null, rect, 2, 2);
            if (unknownCost) dc.Pop();
            if (labelDays || i == 0 || i == _days.Count - 1 || (_days.Count > 14 && i % 7 == 0))
            {
                var text = new FormattedText(_days[i].Day.ToString("MM-dd", CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, 10, labelBrush, ppd);
                dc.DrawText(text, new Point(x + Math.Max(0, (bar - text.Width) / 2), chartBottom + 2));
            }
            x += bar + gap;
        }
    }
}
