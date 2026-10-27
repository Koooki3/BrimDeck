using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// What the page needs to build the power lines: the look of the theme, the low-battery threshold and whether this machine has a battery.
internal sealed record PowerOptions(PowerStyle Style, int Low, bool Battery);

// The whole machine's power draw and its own battery, under the drives on the monitor page. As memory and network are drawn, it is one
// element that renders directly: a new reading changes what it draws, never the layout, and the battery's fill eases to each reading.
//   Line 1: the power. A badge, the figure ("≈" before an estimate), "W", the kind of reading ("实测" or "估算") and where it comes
//   from, then the last minute as a curve in the look of the theme, and the peak and average since the page opened at the right end.
//   Line 2: the battery. The battery in the look of the theme, the charge, what it is doing ("充电中 · 31.2 W · 约 12 分钟充满", "放电 ·
//   23.4 W · 约 3 小时 12 分", "已充满"), and at the right Battery saver and the power mode.
// On a page with room for one line only, and a machine with a battery, the two share it: power at the left, the battery at the right.
// A figure that cannot be read is a dash, with the reason in the tooltip; a machine without a battery has no battery line at all.
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

    public PowerBlock(MonitorColors colors, PowerOptions options, double body, double caption, double row, int lines)
    {
        _colors = colors; _options = options; _body = body; _caption = caption; _row = row; _lines = Math.Max(1, lines);
        Height = _lines * row;
        SnapsToDevicePixels = true;
    }

    // For screen readers: "整机功耗 约 38.2 W（估算）；电池 96%，充电中".
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Text, () => _spoken);

    private PowerStyle Skin => _options.Style;
    private bool Shared => _lines == 1 && _options.Battery;
    private bool BatteryLine => _lines >= 2 && _options.Battery;

    public void Set(MonitorSnapshot snapshot, bool motion)
    {
        _power = snapshot.Power; _battery = snapshot.Battery; _history = snapshot.PowerHistory; _summary = snapshot.PowerSummary;
        bool moving = _fill.Set(_battery.TryGet(out var sample) ? sample.Percent / 100 : 0, motion);
        ToolTip = Describe();
        _spoken = Spoken();
        if (moving) Motion.Start(this, now => { InvalidateVisual(); return _fill.Moving(now); }); else InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Height);

    // The figure: "38.2", with "≈" before an estimate.
    private string Figure(PowerSample power) => (power.Estimated ? "≈" : "") + PowerFormat.Watts(power.Watts);

    private string Describe()
    {
        var lines = new List<string>();
        if (_power.TryGet(out var power))
        {
            lines.Add(Loc.T("整机功耗 ", "System power ") + Figure(power) + " W · " + PowerResolver.OriginName(power) + " · " + PowerResolver.SourceName(power));
            foreach (var part in power.Parts) lines.Add("  " + part.Name + " " + PowerFormat.Watts(part.Watts) + " W · " + part.Source);
            foreach (var missing in power.Missing) lines.Add("  " + missing);
            if (power.Estimated)
                lines.Add(Loc.T("估算只含上面读到的部件：不含显示屏、硬盘、风扇、主板和电源损耗，实际耗电更高。", "The estimate covers only the parts above: not the display, drives, fans, board or the power supply's losses, so the real draw is higher."));
            else if (power.Source == PowerSource.Battery)
                lines.Add(Loc.T("电池供电时的放电功率就是整机耗电，含显示屏等所有部件。", "While on battery, the discharge power is everything the machine draws, display included."));
            if (_summary is { Count: > 0 } summary)
                lines.Add(Loc.T($"峰值 {PowerFormat.Watts(summary.Peak)} W · 平均 {PowerFormat.Watts(summary.Average)} W（自打开此页起）", $"Peak {PowerFormat.Watts(summary.Peak)} W · average {PowerFormat.Watts(summary.Average)} W (since this page opened)"));
        }
        else lines.Add(Loc.T("整机功耗暂无读数：", "System power: no reading: ") + _power.Reason);
        if (_options.Battery)
        {
            if (_battery.TryGet(out var battery))
            {
                lines.Add(Loc.T("电池 ", "Battery ") + MonitorFormat.Percent(battery.Percent / 100) + "% · " + BatteryModel.StateName(battery.State) + RateText(battery) + TimeText(battery));
                if (battery.Mode != PowerMode.Unknown) lines.Add(Loc.T("电源模式：", "Power mode: ") + BatteryModel.ModeName(battery.Mode));
                if (battery.BatterySaver) lines.Add(Loc.T("节电模式已开启", "Battery saver is on"));
                if (battery.Health is { } health && battery.FullWh is { } full && battery.DesignWh is { } design)
                    lines.Add(Loc.T($"电池健康 {MonitorFormat.Percent(health)}%（满充 {full:0.0} Wh / 设计 {design:0.0} Wh）", $"Battery health {MonitorFormat.Percent(health)}% (full charge {full:0.0} Wh / design {design:0.0} Wh)"));
                if (battery.Count > 1) lines.Add(Loc.T($"共 {battery.Count} 块电池", $"{battery.Count} batteries"));
            }
            else lines.Add(Loc.T("电池暂无读数：", "Battery: no reading: ") + _battery.Reason);
        }
        return string.Join("\n", lines);
    }

    private string Spoken()
    {
        string text = _power.TryGet(out var power) ? Loc.T("整机功耗 ", "System power ") + Figure(power) + " W，" + PowerResolver.OriginName(power) : Loc.T("整机功耗暂无读数", "System power: no reading");
        if (_options.Battery && _battery.TryGet(out var battery))
            text += Loc.T("；电池 ", "; battery ") + MonitorFormat.Percent(battery.Percent / 100) + "%，" + BatteryModel.StateName(battery.State) + RateText(battery) + TimeText(battery);
        return text;
    }

    // " · 31.2 W" while the battery moves at a rate; the rate is told from the sign of its state.
    private static string RateText(BatterySample battery) => battery.HasRate && battery.State is BatteryState.Charging or BatteryState.Discharging ? " · " + PowerFormat.Watts(battery.Watts) + " W" : "";
    private static string TimeText(BatterySample battery) => battery.TimeLeft is { } left ? Loc.T(" · 约可用 ", " · about ") + PowerFormat.Duration(left) + Loc.T("", " left")
        : battery.TimeToFull is { } toFull ? Loc.T(" · 约 ", " · about ") + PowerFormat.Duration(toFull) + Loc.T("充满", " to full") : "";

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), now = Eased.Now;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        double right = w;
        if (Shared) right = BatteryRight(drawing, new Rect(0, 0, w, _row), ppd, now, compact: true, leftLimit: w * .5);
        Power(drawing, new Rect(0, 0, Math.Max(0, right - (Shared ? 12 : 0)), _row), ppd);
        if (BatteryLine) BatteryRight(drawing, new Rect(0, _row, w, _row), ppd, now, compact: false, leftLimit: 0);
    }

    // Line 1: the badge, the figure, the kind and the source, the curve, the peak and the average.
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
        // The kind, in a small outline: an estimate is told from a measurement at a glance.
        var kind = MonitorDraw.Text(PowerResolver.OriginName(power!), _caption, power!.Estimated ? UI.Primary : UI.Secondary, ppd);
        double padding = 4, tagWidth = kind.WidthIncludingTrailingWhitespace + 2 * padding, tagHeight = Math.Ceiling(_caption * 1.5);
        x += 8;
        if (x + tagWidth <= line.Right)
        {
            var tag = new Rect(x, Math.Round(line.Y + (line.Height - tagHeight) / 2), tagWidth, tagHeight);
            double radius = PanelTheme.Corner(3);
            var outline = new Pen(UI.Brush(power.Estimated ? UI.Secondary : MonitorDraw.Faint(UI.Secondary, .55)), 1) { DashStyle = power.Estimated ? DashStyles.Dash : DashStyles.Solid };
            drawing.DrawRoundedRectangle(null, outline, tag, radius, radius);
            MonitorDraw.Baseline(drawing, kind, tag.X + padding, Math.Round(tag.Y + tag.Height / 2 + _caption * .36));
            x = tag.Right + 8;
        }
        var source = MonitorDraw.Text(PowerResolver.SourceName(power), _caption, UI.Tertiary, ppd);
        // The source is told only where the curve still has room after it.
        double peakWidth = 0;
        MonitorDraw.Baseline(drawing, source, x, baseline);
        double afterSource = x + source.WidthIncludingTrailingWhitespace + 10;
        FormattedText? peak = null;
        if (_summary is { Count: > 2 } summary)
        {
            peak = MonitorDraw.Text(Loc.T($"峰 {PowerFormat.Watts(summary.Peak)} · 均 {PowerFormat.Watts(summary.Average)}", $"pk {PowerFormat.Watts(summary.Peak)} · avg {PowerFormat.Watts(summary.Average)}"), _caption, UI.Tertiary, ppd);
            peakWidth = peak.WidthIncludingTrailingWhitespace + 10;
        }
        double curveRight = line.Right - peakWidth;
        if (curveRight - afterSource >= 40 && _history.Count >= 3)
        {
            double top = Math.Max(10, _history.Max() * 1.15);
            PowerGlyphs.Curve(drawing, new Rect(afterSource, line.Y + 2, curveRight - afterSource, line.Height - 4), _history, top, Skin);
        }
        else if (peak is not null && line.Right - afterSource < peakWidth) peak = null;
        if (peak is not null) MonitorDraw.Baseline(drawing, peak, line.Right - peak.WidthIncludingTrailingWhitespace, baseline);
    }

    // The battery on a line: it ends at the line's right edge (the shared line draws it there) or fills the line. Returns the left edge it
    // took, so the line it shares can end before it.
    private double BatteryRight(DrawingContext drawing, Rect line, double ppd, double now, bool compact, double leftLimit)
    {
        double height = Math.Round(_body * .95), width = Math.Round(height * 2.2), mid = line.Y + line.Height / 2, baseline = Math.Round(mid + _body * .38);
        bool known = _battery.TryGet(out var battery);
        string percent = known ? MonitorFormat.Percent(battery!.Percent / 100) : "—";
        var figure = MonitorDraw.Text(percent, _body + 1, UI.Primary, ppd, strong: true);
        var unit = known ? MonitorDraw.Text("%", _caption, UI.Tertiary, ppd) : null;
        string state = known ? compact ? ShortState(battery!) : StateLine(battery!) : "";
        var text = state.Length > 0 ? MonitorDraw.Text(state, _caption, UI.Secondary, ppd) : null;
        // The shared line: [battery] 96% 充电  at the right edge. The full line: [battery] 96% state ........ mode  at the left.
        double extra = compact && text is not null ? 6 + text.WidthIncludingTrailingWhitespace : 0;
        double total = width + 1 + 6 + figure.WidthIncludingTrailingWhitespace + (unit is null ? 0 : 2 + unit.WidthIncludingTrailingWhitespace) + extra;
        double left = compact ? Math.Max(leftLimit, line.Right - total) : line.X;
        if (compact && line.Right - total < leftLimit) { text = null; extra = 0; total = width + 7 + figure.WidthIncludingTrailingWhitespace + (unit is null ? 0 : 2 + unit.WidthIncludingTrailingWhitespace); left = Math.Max(leftLimit, line.Right - total); }
        var visual = known ? BatteryVisual.Of(battery!, _options.Low) : new BatteryVisual(0, BatteryState.Discharging, false, MemoryLevel.Normal);
        PowerGlyphs.Battery(drawing, new Rect(left, Math.Round(mid - height / 2), width, height), visual, Skin, ppd, _fill.At(now), compact: false, unknown: !known);
        double x = left + width + 1 + 6;
        MonitorDraw.Baseline(drawing, figure, x, baseline); x += figure.WidthIncludingTrailingWhitespace;
        if (unit is not null) { MonitorDraw.Baseline(drawing, unit, x + 2, baseline); x += 2 + unit.WidthIncludingTrailingWhitespace; }
        if (text is not null)
        {
            x += compact ? 6 : 8;
            if (compact || x + text.WidthIncludingTrailingWhitespace <= line.Right) MonitorDraw.Baseline(drawing, text, x, baseline);
            x += text.WidthIncludingTrailingWhitespace;
        }
        if (!compact && known) Chips(drawing, battery!, line, x, baseline, ppd);
        return left;
    }

    // The full battery line's right end: Battery saver (a leaf) and the power mode, as small outlined words, when the room allows.
    private void Chips(DrawingContext drawing, BatterySample battery, Rect line, double from, double baseline, double ppd)
    {
        var chips = new List<(string Text, bool Leaf)>();
        if (battery.BatterySaver) chips.Add((Loc.T("节电", "Saver"), true));
        if (battery.Mode != PowerMode.Unknown) chips.Add((BatteryModel.ModeName(battery.Mode), false));
        double right = line.Right, gap = 6, padding = 5, tagHeight = Math.Ceiling(_caption * 1.5), leaf = Math.Round(_caption);
        for (int i = chips.Count - 1; i >= 0; i--)
        {
            var (label, hasLeaf) = chips[i];
            var text = MonitorDraw.Text(label, _caption, UI.Secondary, ppd);
            double width = text.WidthIncludingTrailingWhitespace + 2 * padding + (hasLeaf ? leaf + 3 : 0);
            if (right - width < from + 8) break;
            var tag = new Rect(right - width, Math.Round(line.Y + (line.Height - tagHeight) / 2), width, tagHeight);
            double radius = PanelTheme.Corner(3);
            drawing.DrawRoundedRectangle(null, new Pen(UI.Brush(hasLeaf ? Skin.Saver : MonitorDraw.Faint(UI.Secondary, .55)), 1), tag, radius, radius);
            double textX = tag.X + padding;
            if (hasLeaf) { PowerGlyphs.Leaf(drawing, new Point(textX, Math.Round(tag.Y + (tag.Height - leaf) / 2)), leaf, Skin.Saver); textX += leaf + 3; }
            MonitorDraw.Baseline(drawing, text, textX, Math.Round(tag.Y + tag.Height / 2 + _caption * .36));
            right = tag.X - gap;
        }
    }

    // "充电" or "放电" on the shared line; the full line's words: "充电中 · 31.2 W · 约 12 分钟充满".
    private static string ShortState(BatterySample battery) => battery.State switch
    {
        BatteryState.Charging => Loc.T("充电", "charging"), BatteryState.Full => Loc.T("已充满", "full"), BatteryState.PluggedIn => Loc.T("已接电", "plugged"), _ => ""
    };
    private static string StateLine(BatterySample battery)
    {
        string state = battery.State switch
        {
            BatteryState.Charging => Loc.T("充电中", "Charging"), BatteryState.Full => Loc.T("已充满", "Fully charged"),
            BatteryState.PluggedIn => Loc.T("已接通电源，未在充电", "Plugged in, not charging"), _ => Loc.T("放电", "Discharging")
        };
        return state + RateText(battery) + TimeText(battery);
    }
}
