namespace BrimDeck.Core;

// The machine's own battery (a laptop's or tablet's), not a wireless device's: one reading for
// all the machine's batteries together. The probe (SystemSampler) hands over each battery's report and the system's power state in
// BatteryInputs; BatteryModel.Build works out the rest, so every rule here is tested with made-up reports.

// What the batteries are doing as a whole. PluggedIn: on mains and not charging, below full (a charge limit or a battery held
// at a threshold, as some laptops do to spare the cell).
public enum BatteryState { Discharging, Charging, Full, PluggedIn }

// One battery's report. Milliwatt-hours and milliwatts as Windows gives them; null where the battery does not say. RateMilliwatts
// is positive while charging and negative while discharging. Charging, Discharging: the battery's own status (both false: idle).
public sealed record BatteryCell(string Id, double? RemainingMwh, double? FullMwh, double? DesignMwh, double? RateMilliwatts, bool Charging = false, bool Discharging = false);

// What the system says besides the batteries. AcOnline null: unknown. WindowsPercent and WindowsRemaining are the system's own figures
// (GetSystemPowerStatus), used where the reports lack capacities or a rate.
public sealed record BatteryInputs(IReadOnlyList<BatteryCell> Cells, bool? AcOnline = null, double? WindowsPercent = null, TimeSpan? WindowsRemaining = null);

// The batteries as the page shows them. Percent is 0 to 100; Watts the size of the charge or discharge rate (0 when idle or unknown, see
// HasRate). TimeLeft is set while discharging and TimeToFull while charging, each only when it can be told.
public sealed record BatterySample(double Percent, BatteryState State, int Count)
{
    public double Watts { get; init; }
    public bool HasRate { get; init; }
    public TimeSpan? TimeLeft { get; init; }
    public TimeSpan? TimeToFull { get; init; }
    public double? RemainingWh { get; init; }
    public double? FullWh { get; init; }
    public double? DesignWh { get; init; }
    // The full-charge capacity against the design capacity, 0 to 1; null without both.
    public double? Health { get; init; }
    public bool AcOnline { get; init; }
    public bool Charging => State == BatteryState.Charging;
}

public static class BatteryModel
{
    // A rate under this is rounding noise in a battery that is idle; a time computed from a smaller one would run to days.
    public const double RateFloorMilliwatts = 500;
    // Times past this are not worth saying ("12 天"): the page leaves them out.
    public static readonly TimeSpan LongestTime = TimeSpan.FromHours(48);
    // A battery at or above this share on mains is full (laptops stop charging a little short of 100).
    public const double FullPercent = 97;

    public static Reading<BatterySample> Build(BatteryInputs inputs)
    {
        var cells = inputs.Cells;
        if (cells.Count == 0) return Reading<BatterySample>.Unavailable(NoBattery);

        // Capacities add up over the batteries that give them; the share is the sum of what remains over the sum of what fits, so a
        // large battery weighs more than a small one (two batteries at 50 % and 100 % of 30 and 60 Wh are 83 %, not 75 %).
        double remaining = 0, full = 0, design = 0, rate = 0;
        bool hasRemaining = false, hasFull = false, hasDesign = false, hasRate = false, charging = false, discharging = false, sharable = true;
        double shareRemaining = 0, shareFull = 0;
        foreach (var cell in cells)
        {
            if (cell.RemainingMwh is { } r && Plausible(r)) { remaining += r; hasRemaining = true; }
            if (cell.FullMwh is { } f && Plausible(f) && f > 0) { full += f; hasFull = true; }
            if (cell.DesignMwh is { } d && Plausible(d) && d > 0) { design += d; hasDesign = true; }
            if (cell.RateMilliwatts is { } w && double.IsFinite(w)) { rate += w; hasRate = true; }
            charging |= cell.Charging; discharging |= cell.Discharging;
            if (cell.RemainingMwh is { } cr && Plausible(cr) && cell.FullMwh is { } cf && Plausible(cf) && cf > 0) { shareRemaining += cr; shareFull += cf; } else sharable = false;
        }
        double? percent = null;
        if (shareFull > 0 && (sharable || !inputs.WindowsPercent.HasValue)) percent = shareRemaining / shareFull * 100;
        else if (inputs.WindowsPercent is { } system && double.IsFinite(system)) percent = system;
        if (percent is not { } share) return Reading<BatterySample>.Unavailable(Loc.T("电池无电量数据", "battery reports no charge"));
        share = Math.Clamp(share, 0, 100);

        // What the batteries do. The reports' own statuses first; the sign of the rate when they say neither; mains alone
        // otherwise. A rate within the floor is idle.
        bool moving = hasRate && Math.Abs(rate) >= RateFloorMilliwatts;
        BatteryState state;
        if (charging && !discharging || !discharging && moving && rate > 0) state = BatteryState.Charging;
        else if (discharging && !charging || !charging && moving && rate < 0) state = BatteryState.Discharging;
        else if (inputs.AcOnline == false) state = BatteryState.Discharging;
        else state = share >= FullPercent ? BatteryState.Full : BatteryState.PluggedIn;
        // A mains connection that says it is there outweighs a battery claiming to discharge by a hair: it is held at its threshold.
        if (state == BatteryState.Discharging && inputs.AcOnline == true && !moving) state = share >= FullPercent ? BatteryState.Full : BatteryState.PluggedIn;

        double watts = hasRate ? Math.Abs(rate) / 1000 : 0;
        TimeSpan? left = null, toFull = null;
        if (state == BatteryState.Discharging)
        {
            if (inputs.WindowsRemaining is { } known && known > TimeSpan.Zero && known <= LongestTime) left = known;
            else if (hasRemaining && moving && rate < 0) left = Bounded(TimeSpan.FromHours(remaining / -rate));
        }
        else if (state == BatteryState.Charging && hasRemaining && hasFull && moving && rate > 0)
            toFull = Bounded(TimeSpan.FromHours(Math.Max(0, full - remaining) / rate));

        double? health = hasFull && hasDesign ? Math.Clamp(full / design, 0, 1) : null;
        return new BatterySample(share, state, cells.Count)
        {
            Watts = watts, HasRate = hasRate && moving, TimeLeft = left, TimeToFull = toFull,
            RemainingWh = hasRemaining ? remaining / 1000 : null, FullWh = hasFull ? full / 1000 : null, DesignWh = hasDesign ? design / 1000 : null,
            Health = health, AcOnline = inputs.AcOnline == true || state is BatteryState.Charging or BatteryState.Full or BatteryState.PluggedIn
        };
    }

    public static string NoBattery => Loc.T("无电池", "no battery");

    private static bool Plausible(double mwh) => double.IsFinite(mwh) && mwh >= 0 && mwh < 1e9;
    private static TimeSpan? Bounded(TimeSpan time) => time > TimeSpan.Zero && time <= LongestTime ? time : null;

    // The power the battery supplies, for the whole-machine reading (PowerResolver): the discharge rate, only while discharging.
    public static Reading<double> Discharge(BatteryInputs inputs, Reading<BatterySample> battery)
        // On mains (a charger weaker than the load) the battery covers only the shortfall, which is not what the machine draws.
        => battery.TryGet(out var sample) && sample.State == BatteryState.Discharging && sample.HasRate && inputs.AcOnline != true ? sample.Watts : Reading<double>.Unavailable(
            Loc.T("未使用电池供电", "not on battery"));
}
