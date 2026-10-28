using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// What the page needs to build the power line: the look of the theme, and whether the page is being opened for the first time (not
// rebuilt for a new size), so that the line comes in (Animate).
internal sealed record PowerOptions(PowerStyle Style, bool Animate = false);

// The whole machine's power draw, under the drives on the monitor page. As memory and network are drawn, it is one element that renders
// directly: a new reading changes what it draws, never the layout. The line carries figures and marks, no words (PowerIcons, in the
// theme's graphic language); the words are in the tooltip (PowerText).
//
// Its columns (Columns) are each as wide as the widest thing they can hold, so nothing moves when a figure changes length: the badge, the
// figure (right-aligned, "≈" inside its room) and its unit, a mark for where the figure comes from (a chip for the sum of the processor's
// and the graphics' power, a battery for the discharge, an instrument for a meter; dashed for an estimate), the last minute as a curve,
// and at the right end the peak and the mean (a mark and a figure each).
// The line comes in as the page opens (PowerOptions.Animate), whether or not a reading is there yet: it rises a few DIP into place as it
// fades in, the watts count up from nothing to their reading, as the page's gauges do, and the curve is drawn from left to right.
// Before the first reading the columns hold a dash.
internal sealed class PowerBlock : FrameworkElement
{
    private readonly MonitorColors _colors;
    private readonly PowerOptions _options;
    private readonly double _body, _caption, _row;
    // The line's entrance, the watts as shown, and how far the curve is drawn.
    private Eased _appear, _watts, _reveal;
    private bool _entered, _hasWatts, _hasCurve;
    private Reading<PowerSample> _power;
    private IReadOnlyList<double> _history = [];
    private PowerSummary? _summary;
    private string _spoken = "";
    // The watts that reach the top of the curve, worked out once per reading.
    private double _top = 10;

    public PowerBlock(MonitorColors colors, PowerOptions options, double body, double caption, double row)
    {
        _colors = colors; _options = options; _body = body; _caption = caption; _row = row;
        Height = row;
        SnapsToDevicePixels = true;
    }

    // For screen readers: the title of the tooltip in one line (PowerText.Spoken).
    protected override AutomationPeer OnCreateAutomationPeer() => new SpokenPeer(this, () => _spoken);

    private PowerStyle Skin => _options.Style;

    public void Set(MonitorSnapshot snapshot, bool motion)
    {
        _power = snapshot.Power; _history = snapshot.PowerHistory; _summary = snapshot.PowerSummary;
        _top = _history.Count == 0 ? 10 : Math.Max(10, _history.Max() * 1.15);
        bool moving = false;
        // The entrance runs as the page is opened, with or without a reading; a page rebuilt for a new size shows its line at once.
        if (!_entered) { _entered = true; moving |= _appear.Set(1, _options.Animate); }
        // A figure's first value counts up from nothing while the page opens; later ones ease from reading to reading as the page's gauges do.
        bool ease = motion || _options.Animate;
        if (_power.TryGet(out var drawn)) { moving |= _watts.Set(drawn.Watts, ease); _hasWatts = true; }
        if (!_hasCurve && _history.Count >= 3) { _hasCurve = true; moving |= _reveal.Set(1, ease); }
        ToolTip = PowerText.Power(_power, _summary);
        string spoken = PowerText.Spoken(_power);
        if (spoken != _spoken)
        {
            string old = _spoken; _spoken = spoken;
            // A screen reader hears the new reading: the automation name changed (it is asked for the name only on focus otherwise).
            if (AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) && UIElementAutomationPeer.FromElement(this) is { } peer)
                peer.RaisePropertyChangedEvent(System.Windows.Automation.AutomationElementIdentifiers.NameProperty, old, spoken);
        }
        if (moving) Motion.Start(this, Step); else InvalidateVisual();
    }
    // One frame of the entrance and of the figure counting: drawn again while anything moves, and no callback after.
    private bool Step(double now) { InvalidateVisual(); return _appear.Moving(now) || _watts.Moving(now) || _reveal.Moving(now); }

    protected override Size MeasureOverride(Size availableSize) => new(0, Height);

    // The figure as shown: "38.2", counting up to the reading while the page opens, with "≈" before an estimate.
    private string Figure(PowerSample power, double now) => (power.Estimated ? "≈" : "") + PowerFormat.Watts(_hasWatts ? Math.Max(0, _watts.At(now)) : power.Watts);
    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private double Mark => Math.Round(_body + 1);
    private const double Gap = 6;

    // The columns, from the widest thing each can hold; made once per scale.
    private readonly record struct Columns(double Lead, double Number, double UnitAt, double Unit, double State, double Middle, double Slot)
    {
        public double NumberRight => Lead + Number;
    }
    private (double Ppd, Columns Columns)? _columns;
    private Columns ColumnsAt(double ppd)
    {
        if (_columns is { } known && known.Ppd == ppd) return known.Columns;
        double Width(string text, double size, bool strong = false) => Math.Ceiling(MonitorDraw.Text(text, size, UI.Primary, ppd, strong: strong).WidthIncludingTrailingWhitespace);
        double badge = Math.Round(_body + 5);
        double lead = badge + Math.Round(_body * .5);
        double number = Width("≈199.9", _body + 1, strong: true);
        double unit = Width("W", _caption);
        double unitAt = lead + number + 3;
        double state = unitAt + unit + Gap + 2;
        double middle = state + Mark + Gap + 2;
        // The right end: two slots, each a mark and a figure of the widest it can show ("199.9").
        double slot = Mark + 3 + Width("199.9", _caption);
        var columns = new Columns(lead, number, unitAt, unit, state, middle, slot);
        _columns = (ppd, columns);
        return columns;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), now = Eased.Now;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        var c = ColumnsAt(ppd);
        double appear = Math.Clamp(_appear.At(now), 0, 1);
        if (appear <= .001) return;
        // The line rises into its place as it fades in.
        if (appear < .999) { drawing.PushOpacity(appear); drawing.PushTransform(new TranslateTransform(0, (1 - appear) * 6)); }
        Power(drawing, new Rect(0, 0, w, _row), ppd, c, now);
        if (appear < .999) { drawing.Pop(); drawing.Pop(); }
    }

    // A mark centred on the line.
    private void DrawMark(DrawingContext drawing, PowerIcon icon, double x, Rect line, string color, double ppd, bool dashed = false)
        => PowerIcons.Draw(drawing, icon, new Rect(x, line.Y + (line.Height - Mark) / 2, Mark, Mark), color, Skin, ppd, dashed);

    private static PowerIcon SourceIcon(PowerSample power) => power.Source switch
    { PowerSource.SystemMeter => PowerIcon.Meter, PowerSource.Battery => PowerIcon.BatteryOut, _ => PowerIcon.Chip };

    // The figure and its unit in their columns: the figure's right edge on the column's, the unit after it.
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

    // The badge, the figure, the source mark, the curve, the peak and the mean.
    private void Power(DrawingContext drawing, Rect line, double ppd, Columns c, double now)
    {
        double badge = Math.Round(_body + 5), baseline = Math.Round(line.Y + line.Height / 2 + _body * .38);
        bool known = _power.TryGet(out var power);
        PowerGlyphs.Badge(drawing, new Point(line.X + badge / 2, line.Y + line.Height / 2), badge, Skin.Draw, known, Skin);
        FigureAt(drawing, known ? Figure(power!, now) : "—", known ? "W" : "", line, ppd, c, line.X);
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
        {
            // Drawn from the left as the page opens.
            double reveal = Clamp01(_reveal.At(now));
            if (reveal < .999) drawing.PushClip(new RectangleGeometry(new Rect(curveLeft, line.Y, (curveRight - curveLeft) * reveal, line.Height)));
            PowerGlyphs.Curve(drawing, new Rect(curveLeft, line.Y + 2, curveRight - curveLeft, line.Height - 4), _history, _top, Skin);
            if (reveal < .999) drawing.Pop();
        }
    }
}

// What a screen reader gets from a drawn element: one text with the name, since the element draws its readings instead of holding controls.
internal sealed class SpokenPeer : FrameworkElementAutomationPeer
{
    private readonly Func<string> _name;
    public SpokenPeer(FrameworkElement owner, Func<string> name) : base(owner) => _name = name;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override string GetNameCore() => _name();
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
}
