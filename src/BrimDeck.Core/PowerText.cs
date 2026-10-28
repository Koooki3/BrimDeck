namespace BrimDeck.Core;

// The words of the power reading's tooltip: a title line (the item, its figure, measured or estimated), then short lines of the
// form "item  value" (two spaces between), in the terms of the field and no sentences. The same text, at its own length, is the screen
// reader's name (Spoken). Everything is localized here, so the rows themselves carry none (they are marks).
public static class PowerText
{
    public const string Gap = "  ";
    private static string Line(string label, string value) => label + Gap + value;

    public static string Watts(double watts) => PowerFormat.Watts(watts) + " W";

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

    // The screen reader's name: the tooltip's title in one line, "item value" and a comma.
    public static string Spoken(Reading<PowerSample> power)
        => power.TryGet(out var sample)
            ? Loc.T("整机功耗 ", "System power ") + (sample.Estimated ? "≈" : "") + Watts(sample.Watts) + Loc.T("，", ", ") + Quality(sample)
            : Loc.T("整机功耗 无读数", "System power: no reading");
}
