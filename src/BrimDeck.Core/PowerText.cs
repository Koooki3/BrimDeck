namespace BrimDeck.Core;

// The words of the power and battery readings' tooltips: a title line (the item, its figure, measured or estimated), then short lines of the
// form "item  value" (two spaces between), in the terms of the field and no sentences. The same text, at its own length, is the screen
// reader's name (Spoken). Everything is localized here, so the rows themselves carry none (they are marks).
public static class PowerText
{
    public const string Gap = "  ";
    private static string Line(string label, string value) => label + Gap + value;

    public static string Watts(double watts) => PowerFormat.Watts(watts) + " W";
    public static string Percent(double percent) => MonitorFormat.Percent(percent / 100) + "%";

    // "17.0 W (estimated)" / "23.4 W (measured)".
    private static string Quality(PowerSample power) => power.Estimated ? Loc.T("估算", "estimated") : Loc.T("实测", "measured");
    private static string Bracket(string text) => Loc.T("（" + text + "）", " (" + text + ")");

    public static string Source(PowerSample power) => power.Source switch
    {
        PowerSource.SystemMeter => Loc.T("整机功率计", "System meter"),
        PowerSource.Battery => Loc.T("电池放电", "Battery discharge"),
        _ => string.Join(" + ", power.Parts.Select(Part))
    };
    private static string Part(PowerPart part) => part.Name + " " + Watts(part.Watts) + Bracket(part.Source);

    // The power tooltip: title, source (or parts), what an estimate leaves out, what could not be read, the peak and the mean.
    public static string Power(Reading<PowerSample> power, PowerSummary? summary)
    {
        if (!power.TryGet(out var sample))
            return Line(Loc.T("整机功耗", "System power"), "—") + "\n" + Line(Loc.T("原因", "Reason"), power.Reason);
        var lines = new List<string> { Line(Loc.T("整机功耗", "System power"), Watts(sample.Watts) + Bracket(Quality(sample))) };
        lines.Add(Line(Loc.T("来源", "Source"), Source(sample)));
        if (sample.Source != PowerSource.Components && sample.Parts.Count > 0) lines.Add(Line(Loc.T("其中", "Of which"), string.Join(" + ", sample.Parts.Select(Part))));
        if (sample.Estimated) lines.Add(Line(Loc.T("不含", "Excludes"), Loc.T("显示器、存储、风扇、主板、供电损耗", "display, storage, fans, board, PSU loss")));
        if (sample.Missing.Count > 0) lines.Add(Line(Loc.T("未读取", "Not read"), string.Join(Loc.T("、", ", "), sample.Missing)));
        if (summary is { Count: > 0 }) lines.Add(Line(Loc.T("峰值 / 均值", "Peak / mean"), PowerFormat.Watts(summary.Peak) + " / " + Watts(summary.Average) + Bracket(Loc.T("自打开", "since opened"))));
        return string.Join("\n", lines);
    }

    // "放电 23.4 W", "充电 31.0 W", "交流供电，未充电", "已充满，交流供电".
    public static string State(BatterySample battery) => battery.State switch
    {
        BatteryState.Charging => Loc.T("充电", "Charging") + (battery.HasRate ? " " + Watts(battery.Watts) : ""),
        BatteryState.Full => Loc.T("已充满，交流供电", "Full, AC power"),
        BatteryState.PluggedIn => Loc.T("交流供电，未充电", "AC power, not charging"),
        _ => Loc.T("放电", "Discharging") + (battery.HasRate ? " " + Watts(battery.Watts) : "")
    };

    // The battery tooltip: charge, state, time, power mode, saver, health, number of batteries.
    public static string Battery(Reading<BatterySample> battery)
    {
        if (!battery.TryGet(out var sample))
            return Line(Loc.T("电池", "Battery"), "—") + "\n" + Line(Loc.T("原因", "Reason"), battery.Reason);
        var lines = new List<string> { Line(Loc.T("电池", "Battery"), Percent(sample.Percent) + (sample.RemainingWh is { } wh ? Bracket(wh.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Wh") : "")) };
        lines.Add(Line(Loc.T("状态", "State"), State(sample)));
        if (sample.TimeLeft is { } left) lines.Add(Line(Loc.T("剩余", "Remaining"), PowerFormat.Duration(left)));
        else if (sample.TimeToFull is { } full) lines.Add(Line(Loc.T("充满需", "To full"), PowerFormat.Duration(full)));
        if (BatteryModel.ModeName(sample.Mode) is { Length: > 0 } mode) lines.Add(Line(Loc.T("电源模式", "Power mode"), mode));
        if (sample.BatterySaver) lines.Add(Line(Loc.T("节电", "Battery saver"), Loc.T("开启", "on")));
        if (sample.Health is { } health && sample.FullWh is { } fullWh && sample.DesignWh is { } design)
            lines.Add(Line(Loc.T("健康度", "Health"), MonitorFormat.Percent(health) + "%" + Bracket(Loc.T("满充 ", "full ") + fullWh.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Wh / " + Loc.T("设计 ", "design ") + design.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Wh")));
        if (sample.Count > 1) lines.Add(Line(Loc.T("电池数", "Batteries"), sample.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return string.Join("\n", lines);
    }

    // The screen reader's name: the tooltip's items in one line, "item value" and commas.
    public static string Spoken(Reading<PowerSample> power, Reading<BatterySample> battery, bool batteryShown)
    {
        string text = power.TryGet(out var sample)
            ? Loc.T("整机功耗 ", "System power ") + (sample.Estimated ? "≈" : "") + Watts(sample.Watts) + Loc.T("，", ", ") + Quality(sample)
            : Loc.T("整机功耗 无读数", "System power: no reading");
        if (batteryShown && battery.TryGet(out var cell))
            text += Loc.T("；电池 ", "; battery ") + Percent(cell.Percent) + Loc.T("，", ", ") + State(cell)
                + (BatteryModel.ModeName(cell.Mode) is { Length: > 0 } mode ? Loc.T("；电源模式 ", "; power mode ") + mode : "");
        return text;
    }
}
