using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// What the page needs to build the power lines: the look of the theme, the low-battery threshold and whether this machine has a battery.
// Quick: the battery as the system itself says it at once (BatteryProvider.Quick), shown until the sampler's first reading arrives.
internal sealed record PowerOptions(PowerStyle Style, int Low, bool Battery, Reading<BatterySample> Quick = default);

// The whole machine's power draw and its own battery, under the drives on the monitor page. As memory and network are drawn, it is one
// element that renders directly: a new reading changes what it draws, never the layout, and the battery's fill eases to each reading.
// The rows carry figures and marks, no words (PowerIcons, in the theme's graphic language); the words are in the tooltip (PowerText).
//
// Both rows sit on one set of columns (PowerColumns), each as wide as the widest thing it can hold, so nothing moves when a figure changes
// length: the lead (the badge, the battery), the figure (right-aligned, "≈" inside its room) and its unit, a mark, then the middle, and at
// the right end two slots. Line 1: the badge, the figure and "W", where it comes from (a chip for the sum of the processor's and the
// graphics' power, a battery for the discharge, an instrument for a meter; dashed for an estimate), the last minute as a curve, and the
// peak and the mean (a mark and a figure each). Line 2: the battery, the charge and "%", a plug while on mains and not charging, the rate
// (+ charging, − discharging) and a clock with the time; at the right the saver (a leaf), the power mode (a dial) and the health (a heart).
// On a page with room for one line only, and a machine with a battery, the two share it: power at the left, the battery at the right.
// Before the first reading the columns hold dashes; the figures and marks then fade in as the monitor page's other parts do.
internal sealed class PowerBlock : FrameworkElement
{
    private readonly MonitorColors _colors;
    private readonly PowerOptions _options;
    private readonly double _body, _caption, _row;
    private readonly int _lines;
    private Eased _fill, _appear;
    private bool _appeared;
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
        _battery = options.Quick;
    }

    // For screen readers: the items in one line (PowerText.Spoken).
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Text, () => _spoken);

    private PowerStyle Skin => _options.Style;
    private bool Shared => _lines == 1 && _options.Battery;
    private bool BatteryLine => _lines >= 2 && _options.Battery;

    public void Set(MonitorSnapshot snapshot, bool motion)
    {
        _power = snapshot.Power; _history = snapshot.PowerHistory; _summary = snapshot.PowerSummary;
        // The sampler's battery reading once it has one, the system's own until then.
        _battery = snapshot.Battery.HasValue || !_options.Quick.HasValue ? snapshot.Battery : _options.Quick;
        _top = _history.Count == 0 ? 10 : Math.Max(10, _history.Max() * 1.15);
        bool moving = _fill.Set(_battery.TryGet(out var sample) ? sample.Percent / 100 : 0, motion);
        // The figures and marks come in with the first reading, easing as the page's other parts do; one that is there when the page is built
        // (a resize) is shown at once.
        if (!_appeared && (_power.HasValue || _battery.HasValue)) { _appeared = true; moving |= _appear.Set(1, motion); }
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
        if (moving) Motion.Start(this, now => { InvalidateVisual(); return _fill.Moving(now) || _appear.Moving(now); }); else InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Height);

    // The figure: "38.2", with "≈" before an estimate.
    private static string Figure(PowerSample power) => (power.Estimated ? "≈" : "") + PowerFormat.Watts(power.Watts);

    private double Mark => Math.Round(_body + 1);
    private const double Gap = 6;

    // The columns of both rows, from the widest thing each can hold; made once per scale.
    private readonly record struct Columns(double Lead, double Number, double UnitAt, double Unit, double State, double Middle, double Slot, double Rate)
    {
        public double NumberRight => Lead + Number;
    }
    private (double Ppd, Columns Columns)? _columns;
    private Columns ColumnsAt(double ppd)
    {
        if (_columns is { } known && known.Ppd == ppd) return known.Columns;
        double Width(string text, double size, bool strong = false) => Math.Ceiling(MonitorDraw.Text(text, size, UI.Primary, ppd, strong: strong).WidthIncludingTrailingWhitespace);
        double badge = Math.Round(_body + 5), battery = Math.Round(Math.Round(_body * .95) * 2.2);
        double lead = Math.Max(badge, battery) + Math.Round(_body * .5);
        double number = Math.Max(Width("≈199.9", _body + 1, strong: true), Width("100", _body + 1, strong: true));
        double unit = Math.Max(Width("W", _caption), Width("%", _caption));
        double unitAt = lead + number + 3;
        double state = unitAt + unit + Gap + 2;
        double middle = state + Mark + Gap + 2;
        // The right end: two slots, each a mark and a figure of the widest it can show ("199.9", "100%").
        double slot = Mark + 3 + Math.Max(Width("199.9", _caption), Width("100%", _caption));
        double rate = Width("−199.9 W", _caption);
        var columns = new Columns(lead, number, unitAt, unit, state, middle, slot, rate);
        _columns = (ppd, columns);
        return columns;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), now = Eased.Now;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        var c = ColumnsAt(ppd);
        double appear = Math.Clamp(_appear.At(now), 0, 1);
        // Until the first reading: the figures' places hold dashes. They give way to the figures as those come in.
        if (appear < .999) { drawing.PushOpacity(1 - appear); Placeholders(drawing, w, ppd, c); drawing.Pop(); }
        if (appear <= .001) return;
        if (appear < .999) drawing.PushOpacity(appear);
        double right = w;
        if (Shared) right = BatteryShared(drawing, new Rect(0, 0, w, _row), ppd, now, leftLimit: w * .5);
        Power(drawing, new Rect(0, 0, Math.Max(0, right - (Shared ? 12 : 0)), _row), ppd, c);
        if (BatteryLine) BatteryFull(drawing, new Rect(0, _row, w, _row), ppd, now, c);
        if (appear < .999) drawing.Pop();
    }

    // A dash in the figure's place of each row, right-aligned where the figure will be.
    private void Placeholders(DrawingContext drawing, double w, double ppd, Columns c)
    {
        var dash = MonitorDraw.Text("—", _body + 1, UI.Tertiary, ppd, strong: true);
        for (int row = 0; row < (BatteryLine ? 2 : 1); row++)
        {
            double mid = row * _row + _row / 2, baseline = Math.Round(mid + _body * .38);
            MonitorDraw.Baseline(drawing, dash, c.NumberRight - dash.WidthIncludingTrailingWhitespace, baseline);
        }
    }

    // A mark centred on the line.
    private void DrawMark(DrawingContext drawing, PowerIcon icon, double x, Rect line, string color, double ppd, bool dashed = false)
        => PowerIcons.Draw(drawing, icon, new Rect(x, line.Y + (line.Height - Mark) / 2, Mark, Mark), color, Skin, ppd, dashed);

    private static PowerIcon SourceIcon(PowerSample power) => power.Source switch
    { PowerSource.SystemMeter => PowerIcon.Meter, PowerSource.Battery => PowerIcon.BatteryOut, _ => PowerIcon.Chip };

    // A figure and its unit in their columns: the figure's right edge on the column's, the unit after it.
    private void FigureAt(DrawingContext drawing, string figure, string unit, Rect line, double ppd, Columns c, double origin = 0)
    {
        double baseline = Math.Round(line.Y + line.Height / 2 + _body * .38);
        var number = MonitorDraw.Text(figure, _body + 1, UI.Primary, ppd, strong: true);
        MonitorDraw.Baseline(drawing, number, origin + c.NumberRight - number.WidthIncludingTrailingWhitespace, baseline);
        if (unit.Length > 0) MonitorDraw.Baseline(drawing, MonitorDraw.Text(unit, _caption, UI.Tertiary, ppd), origin + c.UnitAt, baseline);
    }

    // The right end's two slots (mark, figure): where they begin, or none where the line is too short for them and the curve.
    private static (double A, double B)? RightSlots(Rect line, Columns c)
    {
        double b = line.Right - c.Slot, a = b - 8 - c.Slot;
        return a - Gap >= line.X + c.Middle + 40 ? (a, b) : null;
    }

    // Line 1: the badge, the figure, the source mark, the curve, the peak and the mean.
    private void Power(DrawingContext drawing, Rect line, double ppd, Columns c)
    {
        double badge = Math.Round(_body + 5), baseline = Math.Round(line.Y + line.Height / 2 + _body * .38);
        bool known = _power.TryGet(out var power);
        PowerGlyphs.Badge(drawing, new Point(line.X + badge / 2, line.Y + line.Height / 2), badge, Skin.Draw, known, Skin);
        FigureAt(drawing, known ? Figure(power!) : "—", known ? "W" : "", line, ppd, c, line.X);
        if (!known) return;
        // Where the figure comes from; dashed while it is an estimate.
        double stateX = line.X + c.State;
        if (stateX + Mark <= line.Right) DrawMark(drawing, SourceIcon(power!), stateX, line, UI.Secondary, ppd, dashed: power!.Estimated);
        double curveLeft = line.X + c.Middle, curveRight = line.Right;
        if (RightSlots(line, c) is { } slots)
        {
            curveRight = slots.A - Gap;
            if (_summary is { Count: > 2 } summary)
            {
                DrawMark(drawing, PowerIcon.Peak, slots.A, line, UI.Tertiary, ppd);
                MonitorDraw.Baseline(drawing, MonitorDraw.Text(PowerFormat.Watts(summary.Peak), _caption, UI.Tertiary, ppd), slots.A + Mark + 3, baseline);
                DrawMark(drawing, PowerIcon.Mean, slots.B, line, UI.Tertiary, ppd);
                MonitorDraw.Baseline(drawing, MonitorDraw.Text(PowerFormat.Watts(summary.Average), _caption, UI.Tertiary, ppd), slots.B + Mark + 3, baseline);
            }
        }
        if (curveRight - curveLeft >= 40 && _history.Count >= 3)
            PowerGlyphs.Curve(drawing, new Rect(curveLeft, line.Y + 2, curveRight - curveLeft, line.Height - 4), _history, _top, Skin);
    }

    // The mark for the power mode: a dial with its needle low, in the middle or high.
    private static PowerIcon? ModeIcon(PowerMode mode) => mode switch
    {
        PowerMode.BestEfficiency => PowerIcon.GaugeLow, PowerMode.Balanced => PowerIcon.GaugeMid,
        PowerMode.BestPerformance or PowerMode.BetterPerformance => PowerIcon.GaugeHigh, _ => null
    };

    // The battery on a line it shares with the power, at the right edge on columns of its own that count from that edge: the plug's place,
    // the unit, the figure, the battery. Returns the left edge it took, so the line it shares can end before it.
    private double BatteryShared(DrawingContext drawing, Rect line, double ppd, double now, double leftLimit)
    {
        double height = Math.Round(_body * .95), width = Math.Round(height * 2.2), mid = line.Y + line.Height / 2, baseline = Math.Round(mid + _body * .38);
        var c = ColumnsAt(ppd);
        double plugX = line.Right - Mark, unitX = plugX - Gap - c.Unit, numberRight = unitX - 2, left = numberRight - c.Number - 7 - width;
        left = Math.Max(leftLimit, left);
        bool known = _battery.TryGet(out var battery);
        var visual = known ? BatteryVisual.Of(battery!, _options.Low) : new BatteryVisual(0, BatteryState.Discharging, false, MemoryLevel.Normal);
        PowerGlyphs.Battery(drawing, new Rect(left, Math.Round(mid - height / 2), width, height), visual, Skin, ppd, _fill.At(now), compact: false, unknown: !known);
        var figure = MonitorDraw.Text(known ? MonitorFormat.Percent(battery!.Percent / 100) : "—", _body + 1, UI.Primary, ppd, strong: true);
        MonitorDraw.Baseline(drawing, figure, numberRight - figure.WidthIncludingTrailingWhitespace, baseline);
        if (known) MonitorDraw.Baseline(drawing, MonitorDraw.Text("%", _caption, UI.Tertiary, ppd), unitX, baseline);
        if (known && battery!.State is BatteryState.PluggedIn or BatteryState.Full) DrawMark(drawing, PowerIcon.Plug, plugX, line, UI.Secondary, ppd);
        return left;
    }

    // Line 2, on the same columns as line 1: the battery in the lead, the charge and "%", the plug in the mark's column; in the middle the
    // rate and the time in columns of their own; at the right the saver, the power mode and the health in the slots under the peak and the mean.
    private void BatteryFull(DrawingContext drawing, Rect line, double ppd, double now, Columns c)
    {
        double height = Math.Round(_body * .95), width = Math.Round(height * 2.2), mid = line.Y + line.Height / 2, baseline = Math.Round(mid + _body * .38);
        bool known = _battery.TryGet(out var battery);
        var visual = known ? BatteryVisual.Of(battery!, _options.Low) : new BatteryVisual(0, BatteryState.Discharging, false, MemoryLevel.Normal);
        PowerGlyphs.Battery(drawing, new Rect(line.X, Math.Round(mid - height / 2), width, height), visual, Skin, ppd, _fill.At(now), compact: false, unknown: !known);
        FigureAt(drawing, known ? MonitorFormat.Percent(battery!.Percent / 100) : "—", known ? "%" : "", line, ppd, c, line.X);
        if (!known) return;
        var b = battery!;
        if (b.State is BatteryState.PluggedIn or BatteryState.Full) DrawMark(drawing, PowerIcon.Plug, line.X + c.State, line, UI.Secondary, ppd);
        var slots = RightSlots(line, c);
        double end = slots is { } s ? s.A - Gap : line.Right;
        // The rate in a column of its own, the time after it in another; either is left out where it does not fit, the other keeps its place.
        double x = line.X + c.Middle;
        if (b.HasRate && b.State is BatteryState.Charging or BatteryState.Discharging && x + c.Rate <= end)
        {
            var rate = MonitorDraw.Text((b.State == BatteryState.Charging ? "+" : "−") + PowerFormat.Watts(b.Watts) + " W", _caption, UI.Secondary, ppd);
            MonitorDraw.Baseline(drawing, rate, x + c.Rate - rate.WidthIncludingTrailingWhitespace, baseline);
        }
        x += c.Rate + Gap;
        if ((b.TimeLeft ?? b.TimeToFull) is { } span)
        {
            var text = MonitorDraw.Text(PowerFormat.Clock(span), _caption, UI.Secondary, ppd);
            double wide = Mark + 3 + Math.Ceiling(MonitorDraw.Text("99:59", _caption, UI.Secondary, ppd).WidthIncludingTrailingWhitespace);
            if (x + wide <= end)
            {
                DrawMark(drawing, PowerIcon.Clock, x, line, UI.Tertiary, ppd);
                MonitorDraw.Baseline(drawing, text, x + Mark + 3, baseline);
            }
        }
        if (slots is not { } slot) return;
        // The slot under the peak holds the saver and the power mode (two marks), the slot under the mean the health (a mark and its figure).
        if (b.BatterySaver) DrawMark(drawing, PowerIcon.Leaf, slot.A, line, Skin.Saver, ppd);
        if (ModeIcon(b.Mode) is { } mode) DrawMark(drawing, mode, slot.A + Mark + Gap, line, UI.Secondary, ppd);
        if (b.Health is { } health)
        {
            DrawMark(drawing, PowerIcon.Heart, slot.B, line, UI.Secondary, ppd);
            MonitorDraw.Baseline(drawing, MonitorDraw.Text(MonitorFormat.Percent(health) + "%", _caption, UI.Tertiary, ppd), slot.B + Mark + 3, baseline);
        }
    }
}
