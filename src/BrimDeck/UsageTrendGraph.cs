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
        _tip.Content = day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\n"
            + (_cost
                ? "$" + day.Cost.ToString("N2", CultureInfo.InvariantCulture)
                : Loc.T($"{UI.Number(day.Tokens)} Token（{day.Tokens:N0}）", $"{UI.Number(day.Tokens)} tokens ({day.Tokens:N0})"));
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
        double gap = Gap(), bar = BarWidth(gap);
        if (bar <= 0) return -1;
        double at = Pad;
        for (int i = 0; i < _days.Count; i++)
        {
            if (x >= at && x < at + bar) return i;
            at += bar + gap;
        }
        return -1;
    }

    private const double Pad = 2, LabelBand = 16, MinGap = 2;
    private double Gap() => _days.Count <= 1 ? 0 : Math.Max(MinGap, Math.Min(6, (ActualWidth - Pad * 2) / _days.Count * .18));
    private double BarWidth(double gap) => _days.Count == 0 ? 0 : Math.Max(1, (ActualWidth - Pad * 2 - gap * (_days.Count - 1)) / _days.Count);

    protected override void OnRender(DrawingContext dc)
    {
        if (_days.Count == 0 || ActualWidth <= 0 || ActualHeight <= LabelBand) return;
        double gap = Gap(), bar = BarWidth(gap);
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
            double value = _cost ? (double)_days[i].Cost : _days[i].Tokens;
            double height = Math.Max(value > 0 ? 2 : 0, chartHeight * (value / max));
            var rect = new Rect(Math.Round(x), Math.Round(chartBottom - height), Math.Max(1, Math.Round(bar)), Math.Round(height));
            dc.DrawRoundedRectangle(i == _hot ? hot : fill, null, rect, 2, 2);
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
