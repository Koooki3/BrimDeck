using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// What the page needs to build the power lines: the look of the theme, the low-battery threshold and whether this machine has a battery.
internal sealed record PowerOptions(PowerStyle Style, int Low, bool Battery);

// The whole machine's power draw and its own battery, under the drives on the monitor page. As memory and network are drawn, it is one
// element that renders directly: a new reading changes what it draws, never the layout, and the battery's fill eases to each reading.
// The rows carry figures and marks, no words (PowerIcons, in the theme's graphic language); the words are in the tooltip (PowerText).
//   Line 1: the power. A badge, the figure ("≈" before an estimate) and "W", a mark for where it comes from (a chip for the sum of the
//   processor's and the graphics' power, a battery for the discharge, an instrument for a meter of the machine; dashed for an estimate),
//   the last minute as a curve, and at the right end the peak and the mean, each a mark and its figure.
//   Line 2: the battery. The battery in the theme's shape, the charge, a plug while on mains and not charging, the rate (+ charging, − discharging)
//   and a clock with the time left or to full; at the right Battery saver (a leaf), the power mode (a dial whose needle is low, middle or high)
//   and the battery's health (a heart and its share).
// On a page with room for one line only, and a machine with a battery, the two share it: power at the left, the battery at the right. A figure
// that cannot be read is a dash, with the reason in the tooltip; a machine without a battery has no battery line at all.
internal sealed class PowerBlock : FrameworkElement
{
    private readonly MonitorColors _colors;
    private readonly PowerOptions _options;
    private readonly double _body, _caption, _row;
    private readonly int _lines;
    private Eased _fill;
    private Reading<PowerSample> _power;
    private Reading<BatterySample> _battery;
    private IReadOnlyList<double> _history = [];
    private PowerSummary? _summary;
    private string _spoken = "";
    // The watts that reach the top of the curve, worked out once per reading.
    private double _top = 10;

    public PowerBlock(MonitorColors colors, PowerOptions options, double body, double caption, double row, int lines)
    {
        _colors = colors; _options = options; _body = body; _caption = caption; _row = row; _lines = Math.Max(1, lines);
        Height = _lines * row;
        SnapsToDevicePixels = true;
    }

    // For screen readers: the items in one line (PowerText.Spoken).
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Text, () => _spoken);

    private PowerStyle Skin => _options.Style;
    private bool Shared => _lines == 1 && _options.Battery;
    private bool BatteryLine => _lines >= 2 && _options.Battery;

    public void Set(MonitorSnapshot snapshot, bool motion)
    {
        _power = snapshot.Power; _battery = snapshot.Battery; _history = snapshot.PowerHistory; _summary = snapshot.PowerSummary;
        _top = _history.Count == 0 ? 10 : Math.Max(10, _history.Max() * 1.15);
        bool moving = _fill.Set(_battery.TryGet(out var sample) ? sample.Percent / 100 : 0, motion);
        // One tooltip for the block: the power's lines, then the battery's, in the same form.
        ToolTip = PowerText.Power(_power, _summary) + (_options.Battery ? "\n" + PowerText.Battery(_battery) : "");
        string spoken = PowerText.Spoken(_power, _battery, _options.Battery);
        if (spoken != _spoken)
        {
            string old = _spoken; _spoken = spoken;
            // A screen reader hears the new reading: the automation name changed (it is asked for the name only on focus otherwise).
            if (AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) && UIElementAutomationPeer.FromElement(this) is { } peer)
                peer.RaisePropertyChangedEvent(System.Windows.Automation.AutomationElementIdentifiers.NameProperty, old, spoken);
        }
        if (moving) Motion.Start(this, now => { InvalidateVisual(); return _fill.Moving(now); }); else InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Height);

    // The figure: "38.2", with "≈" before an estimate.
    private static string Figure(PowerSample power) => (power.Estimated ? "≈" : "") + PowerFormat.Watts(power.Watts);

    private double Mark => Math.Round(_body + 1);
    private const double Gap = 6;

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), now = Eased.Now;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        double right = w;
        if (Shared) right = BatteryShared(drawing, new Rect(0, 0, w, _row), ppd, now, leftLimit: w * .5);
        Power(drawing, new Rect(0, 0, Math.Max(0, right - (Shared ? 12 : 0)), _row), ppd);
        if (BatteryLine) BatteryFull(drawing, new Rect(0, _row, w, _row), ppd, now);
    }

    // A mark centred on the line.
    private void DrawMark(DrawingContext drawing, PowerIcon icon, double x, Rect line, string color, double ppd, bool dashed = false)
        => PowerIcons.Draw(drawing, icon, new Rect(x, line.Y + (line.Height - Mark) / 2, Mark, Mark), color, Skin, ppd, dashed);

    private static PowerIcon SourceIcon(PowerSample power) => power.Source switch
    { PowerSource.SystemMeter => PowerIcon.Meter, PowerSource.Battery => PowerIcon.BatteryOut, _ => PowerIcon.Chip };

    // Line 1: the badge, the figure, the source mark, the curve, the peak and the mean.
    private void Power(DrawingContext drawing, Rect line, double ppd)
    {
        double badge = Math.Round(_body + 5), baseline = Math.Round(line.Y + line.Height / 2 + _body * .38), x = line.X;
        bool known = _power.TryGet(out var power);
        PowerGlyphs.Badge(drawing, new Point(x + badge / 2, line.Y + line.Height / 2), badge, Skin.Draw, known, Skin);
        x += badge + Math.Round(_body * .5);
        var figure = MonitorDraw.Text(known ? Figure(power!) : "—", _body + 1, UI.Primary, ppd, strong: true);
        MonitorDraw.Baseline(drawing, figure, x, baseline); x += figure.WidthIncludingTrailingWhitespace;
        if (!known) return;
        var unit = MonitorDraw.Text("W", _caption, UI.Tertiary, ppd);
        MonitorDraw.Baseline(drawing, unit, x + 3, baseline); x += 3 + unit.WidthIncludingTrailingWhitespace;
        // Where the figure comes from; dashed while it is an estimate.
        x += Gap + 2;
        if (x + Mark <= line.Right) { DrawMark(drawing, SourceIcon(power!), x, line, UI.Secondary, ppd, dashed: power!.Estimated); x += Mark; }
        double afterSource = x + Gap;
        // The peak and the mean at the right end: a mark and its figure each, where there is room for them.
        double reserved = 0;
        FormattedText? peak = null, mean = null;
        if (_summary is { Count: > 2 } summary)
        {
            peak = MonitorDraw.Text(PowerFormat.Watts(summary.Peak), _caption, UI.Tertiary, ppd);
            mean = MonitorDraw.Text(PowerFormat.Watts(summary.Average), _caption, UI.Tertiary, ppd);
            reserved = Mark + 3 + peak.WidthIncludingTrailingWhitespace + 8 + Mark + 3 + mean.WidthIncludingTrailingWhitespace;
        }
        if (reserved > 0 && line.Right - reserved < afterSource) { peak = mean = null; reserved = 0; }
        if (peak is not null && mean is not null)
        {
            double at = line.Right - reserved;
            DrawMark(drawing, PowerIcon.Peak, at, line, UI.Tertiary, ppd); at += Mark + 3;
            MonitorDraw.Baseline(drawing, peak, at, baseline); at += peak.WidthIncludingTrailingWhitespace + 8;
            DrawMark(drawing, PowerIcon.Mean, at, line, UI.Tertiary, ppd); at += Mark + 3;
            MonitorDraw.Baseline(drawing, mean, at, baseline);
        }
        double curveRight = line.Right - (reserved > 0 ? reserved + Gap : 0);
        if (curveRight - afterSource >= 40 && _history.Count >= 3)
            PowerGlyphs.Curve(drawing, new Rect(afterSource, line.Y + 2, curveRight - afterSource, line.Height - 4), _history, _top, Skin);
    }

    // The mark for the power mode: a dial with its needle low, in the middle or high.
    private static PowerIcon? ModeIcon(PowerMode mode) => mode switch
    {
        PowerMode.BestEfficiency => PowerIcon.GaugeLow, PowerMode.Balanced => PowerIcon.GaugeMid,
        PowerMode.BestPerformance or PowerMode.BetterPerformance => PowerIcon.GaugeHigh, _ => null
    };

    // The battery on its line, at the right edge of a line it shares: the battery, its charge and a plug while on mains and not charging.
    // Returns the left edge it took, so the line it shares can end before it.
    private double BatteryShared(DrawingContext drawing, Rect line, double ppd, double now, double leftLimit)
    {
        double height = Math.Round(_body * .95), width = Math.Round(height * 2.2), mid = line.Y + line.Height / 2, baseline = Math.Round(mid + _body * .38);
        bool known = _battery.TryGet(out var battery);
        var figure = MonitorDraw.Text(known ? MonitorFormat.Percent(battery!.Percent / 100) : "—", _body + 1, UI.Primary, ppd, strong: true);
        var unit = known ? MonitorDraw.Text("%", _caption, UI.Tertiary, ppd) : null;
        bool plug = known && battery!.State is BatteryState.PluggedIn or BatteryState.Full;
        double total = width + 7 + figure.WidthIncludingTrailingWhitespace + (unit is null ? 0 : 2 + unit.WidthIncludingTrailingWhitespace) + (plug ? Gap + Mark : 0);
        double left = Math.Max(leftLimit, line.Right - total);
        var visual = known ? BatteryVisual.Of(battery!, _options.Low) : new BatteryVisual(0, BatteryState.Discharging, false, MemoryLevel.Normal);
        PowerGlyphs.Battery(drawing, new Rect(left, Math.Round(mid - height / 2), width, height), visual, Skin, ppd, _fill.At(now), compact: false, unknown: !known);
        double x = left + width + 7;
        MonitorDraw.Baseline(drawing, figure, x, baseline); x += figure.WidthIncludingTrailingWhitespace;
        if (unit is not null) { MonitorDraw.Baseline(drawing, unit, x + 2, baseline); x += 2 + unit.WidthIncludingTrailingWhitespace; }
        if (plug) DrawMark(drawing, PowerIcon.Plug, x + Gap, line, UI.Secondary, ppd);
        return left;
    }

    // Line 2: the battery, its charge, the mains, the rate and the time; at the right the saver, the power mode and the health.
    private void BatteryFull(DrawingContext drawing, Rect line, double ppd, double now)
    {
        double height = Math.Round(_body * .95), width = Math.Round(height * 2.2), mid = line.Y + line.Height / 2, baseline = Math.Round(mid + _body * .38);
        bool known = _battery.TryGet(out var battery);
        var visual = known ? BatteryVisual.Of(battery!, _options.Low) : new BatteryVisual(0, BatteryState.Discharging, false, MemoryLevel.Normal);
        PowerGlyphs.Battery(drawing, new Rect(line.X, Math.Round(mid - height / 2), width, height), visual, Skin, ppd, _fill.At(now), compact: false, unknown: !known);
        double x = line.X + width + 7;
        var figure = MonitorDraw.Text(known ? MonitorFormat.Percent(battery!.Percent / 100) : "—", _body + 1, UI.Primary, ppd, strong: true);
        MonitorDraw.Baseline(drawing, figure, x, baseline); x += figure.WidthIncludingTrailingWhitespace;
        if (!known) return;
        var unit = MonitorDraw.Text("%", _caption, UI.Tertiary, ppd);
        MonitorDraw.Baseline(drawing, unit, x + 2, baseline); x += 2 + unit.WidthIncludingTrailingWhitespace;
        var b = battery!;

        // The right end first, so the left part gives way to it: saver, mode, health, as far as they fit after the left part's minimum.
        var right = new List<(PowerIcon Icon, string? Text, string Color)>();
        if (b.BatterySaver) right.Add((PowerIcon.Leaf, null, Skin.Saver));
        if (ModeIcon(b.Mode) is { } mode) right.Add((mode, null, UI.Secondary));
        if (b.Health is { } health) right.Add((PowerIcon.Heart, MonitorFormat.Percent(health) + "%", UI.Secondary));
        double RightWidth((PowerIcon Icon, string? Text, string Color) item) => Mark + (item.Text is null ? 0 : 3 + MonitorDraw.Text(item.Text, _caption, UI.Tertiary, ppd).WidthIncludingTrailingWhitespace);
        double used = 0;
        var shown = new List<(PowerIcon Icon, string? Text, string Color)>();
        for (int i = right.Count - 1; i >= 0; i--)
        {
            double take = RightWidth(right[i]) + (shown.Count > 0 ? Gap + 2 : 0);
            if (x + 24 + used + take > line.Right) break;
            shown.Insert(0, right[i]); used += take;
        }
        double end = line.Right - used - (used > 0 ? Gap : 0);

        // The left part: a plug while on mains and not charging, the rate with its sign, a clock and the time.
        if (b.State is BatteryState.PluggedIn or BatteryState.Full && x + Gap + Mark <= end) { x += Gap; DrawMark(drawing, PowerIcon.Plug, x, line, UI.Secondary, ppd); x += Mark; }
        if (b.HasRate && b.State is BatteryState.Charging or BatteryState.Discharging)
        {
            var rate = MonitorDraw.Text((b.State == BatteryState.Charging ? "+" : "−") + PowerFormat.Watts(b.Watts) + " W", _caption, UI.Secondary, ppd);
            if (x + Gap + rate.WidthIncludingTrailingWhitespace <= end) { x += Gap + 2; MonitorDraw.Baseline(drawing, rate, x, baseline); x += rate.WidthIncludingTrailingWhitespace; }
        }
        var time = b.TimeLeft ?? b.TimeToFull;
        if (time is { } span)
        {
            var text = MonitorDraw.Text(PowerFormat.Clock(span), _caption, UI.Secondary, ppd);
            double take = Gap + Mark + 3 + text.WidthIncludingTrailingWhitespace;
            if (x + take <= end)
            {
                x += Gap + 2; DrawMark(drawing, PowerIcon.Clock, x, line, UI.Tertiary, ppd); x += Mark + 3;
                MonitorDraw.Baseline(drawing, text, x, baseline);
            }
        }

        double at = line.Right - used;
        foreach (var (icon, text, color) in shown)
        {
            DrawMark(drawing, icon, at, line, color, ppd); at += Mark;
            if (text is not null) { at += 3; var label = MonitorDraw.Text(text, _caption, UI.Tertiary, ppd); MonitorDraw.Baseline(drawing, label, at, baseline); at += label.WidthIncludingTrailingWhitespace; }
            at += Gap + 2;
        }
    }
}
