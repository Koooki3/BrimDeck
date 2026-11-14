using System.Text.Json;
using BrimDeck.Core;

// The bytes moved since the app began reading the network (TrafficTotals), the collapsed row's choice between them and the rate
// (CompactTraffic), how they are written and spoken.
static class TrafficTotalTests
{
    private const CompactMonitorItems Down = CompactMonitorItems.Download, Up = CompactMonitorItems.Upload, Cpu = CompactMonitorItems.Cpu;

    public static void Run(Action<string, bool> check)
    {
        Counting(check);
        Breaks(check);
        Format(check);
        Choice(check);
        Saved(check);
        Speech(check);
    }

    private static Dictionary<ulong, (ulong, ulong)> Counters(params (ulong Adapter, ulong Received, ulong Sent)[] adapters) =>
        adapters.ToDictionary(adapter => adapter.Adapter, adapter => (adapter.Received, adapter.Sent));

    private static void Counting(Action<string, bool> check)
    {
        var totals = new TrafficTotals();
        var first = totals.Add(Counters((1, 1_000, 500)), 10);
        check("Traffic totals: the first reading only sets the baseline", first == (0, 0));
        var second = totals.Add(Counters((1, 3_000, 700)), 11);
        check("Traffic totals: the next adds what the counters moved", second == (2_000, 200) && totals.Received == 2_000 && totals.Sent == 200);
        totals.Add(Counters((1, 3_000, 700), (2, 50, 60)), 12);
        check("Traffic totals: an adapter that appears counts from its second reading", totals.Received == 2_000 && totals.Sent == 200);
        totals.Add(Counters((1, 3_500, 900), (2, 150, 90)), 13);
        check("Traffic totals: adapters add up", totals.Received == 2_600 && totals.Sent == 430);

        totals.Add(Counters((2, 400, 100)), 14);
        check("Traffic totals: an adapter that vanishes adds nothing, the others still count", totals.Received == 2_850 && totals.Sent == 440);
        totals.Add(Counters((1, 9_000, 9_000), (2, 450, 120)), 15);
        check("Traffic totals: an adapter back after a gap starts from its new baseline", totals.Received == 2_900 && totals.Sent == 460);
    }

    private static void Breaks(Action<string, bool> check)
    {
        var totals = new TrafficTotals();
        totals.Add(Counters((1, 10_000, 10_000)), 0);
        totals.Add(Counters((1, 12_000, 11_000)), 1);
        totals.Add(Counters((1, 100, 11_500)), 2);
        check("Traffic totals: a counter that went back adds nothing, never a negative", totals.Received == 2_000 && totals.Sent == 1_000);
        totals.Add(Counters((1, 600, 11_600)), 3);
        check("Traffic totals: after a reset the counter counts again from its new value", totals.Received == 2_500 && totals.Sent == 1_100);

        totals.Add(Counters((1, 600 + 5_000_000_000, 11_600 + 5_000_000_000)), 1_000);
        check("Traffic totals: a long gap (sleep) adds nothing, even when the counters moved", totals.Received == 2_500 && totals.Sent == 1_100);
        totals.Add(Counters((1, 600 + 5_000_000_000 + 40, 11_600 + 5_000_000_000 + 10)), 1_001);
        check("Traffic totals: after the gap it counts again", totals.Received == 2_540 && totals.Sent == 1_110);

        totals.Add(Counters((1, 600 + 5_000_000_000 + 40 + 400_000_000_000, 11_600 + 5_000_000_000 + 10)), 1_002);
        check("Traffic totals: a step faster than any link is a jump, not traffic", totals.Received == 2_540 && totals.Sent == 1_110);
        totals.Add(Counters((1, 600 + 5_000_000_000 + 40 + 400_000_000_000 + 7, 11_600 + 5_000_000_000 + 10 + 3)), 1_003);
        check("Traffic totals: the next step after a jump counts", totals.Received == 2_547 && totals.Sent == 1_113);

        totals.Add(Counters((1, 600 + 5_000_000_000 + 40 + 400_000_000_000 + 7, 11_600 + 5_000_000_000 + 10 + 3)), 1_003);
        check("Traffic totals: a clock that did not move adds nothing", totals.Received == 2_547 && totals.Sent == 1_113);

        var off = new TrafficTotals();
        off.Add(Counters((1, 0, 0)), 0);
        off.Add(Counters((1, 100, 50)), 1);
        off.Forget();
        off.Add(Counters((1, 90_000, 90_000)), 5);
        check("Traffic totals: after the network was off the sums stay and the time off is not counted", off.Received == 100 && off.Sent == 50);
        off.Add(Counters((1, 90_030, 90_010)), 6);
        check("Traffic totals: and counting goes on from the first reading after", off.Received == 130 && off.Sent == 60);

        var empty = new TrafficTotals();
        empty.Add(Counters(), 0);
        check("Traffic totals: no adapter, no traffic", empty.Add(Counters(), 1) == (0, 0));
    }

    private static void Format(Action<string, bool> check)
    {
        string T(double bytes) => MonitorFormat.Total(bytes).Text;
        check("Total format: the reporter's figures read 21.0 MB and 202 MB", T(21_000_000) == "21.0 MB" && T(202_000_000) == "202 MB");
        check("Total format: bytes, kilobytes, gigabytes, terabytes", T(0) == "0 B" && T(512) == "512 B" && T(1_500) == "1.50 KB" && T(512_000) == "512 KB"
            && T(1_240_000_000) == "1.24 GB" && T(2_500_000_000_000) == "2.50 TB");
        check("Total format: the edges move up a unit, never show 1000", T(999) == "999 B" && T(999.6) == "1.00 KB" && T(999_400) == "999 KB" && T(999_600) == "1.00 MB"
            && T(999_600_000) == "1.00 GB");
        check("Total format: a bad value reads as zero", T(-5) == "0 B" && T(double.NaN) == "0 B" && T(double.PositiveInfinity) == "0 B");
        var compact = MonitorFormat.CompactTotal(21_000_000);
        check("Total format: the collapsed row's figure has a one-letter unit", compact.Number + compact.Unit == "21.0M" && MonitorFormat.CompactTotal(202_000_000) is { Number: "202", Unit: "M" }
            && MonitorFormat.CompactTotal(0) is { Number: "0", Unit: "B" } && MonitorFormat.CompactTotal(3_000_000_000) is { Number: "3.00", Unit: "G" });
    }

    private static MonitorSnapshot Snapshot(NetworkSample? network) => MonitorSnapshot.Empty(4, "test") with
    {
        Network = network is { } sample ? Reading<NetworkSample>.Of(sample) : Reading<NetworkSample>.Unavailable("waiting")
    };

    private static void Choice(Action<string, bool> check)
    {
        check("Traffic choice: only the download and the upload offer it", CompactTraffic.Has(Down) && CompactTraffic.Has(Up) && !CompactTraffic.Has(Cpu) && !CompactTraffic.Has(CompactMonitorItems.Storage));
        check("Traffic choice: the total, in any case; else the rate (null)", CompactTraffic.Normalize(Down, ["total"])!.SequenceEqual(["Total"]) && CompactTraffic.Normalize(Up, [" TOTAL ", "x"])!.SequenceEqual(["Total"])
            && CompactTraffic.Normalize(Down, ["Usage"]) is null && CompactTraffic.Normalize(Down, null) is null && CompactTraffic.Normalize(Down, []) is null);
        check("Traffic choice: other readings never carry it", CompactTraffic.Normalize(Cpu, ["Total"]) is null && CompactFigures.Normalize(Down, ["Usage"]) is null);
        check("Traffic choice: the rate is the default", !CompactTraffic.Shows(new CompactReading { Item = Down }) && CompactTraffic.Shows(new CompactReading { Item = Up, Figures = ["Total"] })
            && !CompactTraffic.Shows(new CompactReading { Item = Cpu, Figures = ["Total"] }));

        var sample = new NetworkSample(1_000, 2_000) { DownTotalBytes = 202_000_000, UpTotalBytes = 21_000_000 };
        var snapshot = Snapshot(sample);
        check("Traffic total: each direction's own bytes in this snapshot", CompactTraffic.Of(Down, snapshot) == 202_000_000 && CompactTraffic.Of(Up, snapshot) == 21_000_000);
        check("Traffic total: none without a reading, or one that carries none, or for another reading",
            CompactTraffic.Of(Down, null) is null && CompactTraffic.Of(Down, Snapshot(null)) is null && CompactTraffic.Of(Down, Snapshot(new NetworkSample(1, 1))) is null
            && CompactTraffic.Of(Cpu, snapshot) is null);
        check("Traffic total: a sample made as before carries none", new NetworkSample(1, 2) is { DownTotalBytes: null, UpTotalBytes: null });
    }

    private static void Saved(Action<string, bool> check)
    {
        var old = SettingsMigrations.Read("""{"AppVersion":"1.3.1","CompactReadings":[{"Item":16},{"Item":32,"Figures":["Total","Usage"]},{"Item":2,"Figures":["Total"]}]}""");
        var down = old.CompactReadings.Single(r => r.Item == Down);
        var up = old.CompactReadings.Single(r => r.Item == Up);
        check("Traffic saved: a table without the choice keeps the rate", down.Figures is null && !CompactTraffic.Shows(down));
        check("Traffic saved: the total is kept on the upload, any other figure dropped, and CPU takes none", up.Figures!.SequenceEqual(["Total"]) && CompactTraffic.Shows(up)
            && old.CompactReadings.Single(r => r.Item == Cpu).Figures is null);

        var settings = SettingsMigrations.Read("""{"AppVersion":"1.3.1"}""");
        settings.CompactReadings.Single(r => r.Item == Down).Figures = ["Total"];
        var again = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Traffic saved: the total round-trips, the other reading stays on the rate", CompactTraffic.Shows(again.CompactReadings.Single(r => r.Item == Down))
            && again.CompactReadings.Single(r => r.Item == Up).Figures is null);
        check("Traffic saved: the default writes nothing for it", !JsonSerializer.Serialize(SettingsMigrations.Read("""{"AppVersion":"1.3.1"}""")).Contains("\"Total\""));

        var table = CompactMonitor.Normalize(null);
        CompactMonitor.Apply(table, CompactMonitorMode.Full);
        table.Single(r => r.Item == Down).Figures = ["Total"];
        check("Traffic saved: Full still matches with a total chosen, as the rates are not among its figures", CompactMonitor.Preset(table) == CompactMonitorMode.Full);
        CompactMonitor.Apply(table, CompactMonitorMode.Full);
        check("Traffic saved: choosing a preset leaves the choice as it was", CompactTraffic.Shows(table.Single(r => r.Item == Down)));
    }

    private static void Speech(Action<string, bool> check)
    {
        var language = Loc.Language;
        try
        {
            Loc.Use(Loc.Chinese);
            check("Traffic speech: the rate as before", MonitorSpeech.StripTraffic(true, 1_200_000, 5, false) == "下载 1.2 MB/s" && MonitorSpeech.StripTraffic(false, 85_000, null, false) == "上传 85 KB/s");
            check("Traffic speech: the total says it is bytes moved", MonitorSpeech.StripTraffic(true, 1, 202_000_000, true) == "已下载 202 MB" && MonitorSpeech.StripTraffic(false, 1, 21_000_000, true) == "已上传 21.0 MB");
            check("Traffic speech: a total not read yet says nothing", MonitorSpeech.StripTraffic(true, 1, null, true) == "");
            Loc.Use(Loc.English);
            check("Traffic speech: in English", MonitorSpeech.StripTraffic(true, 1, 202_000_000, true) == "Downloaded 202 MB" && MonitorSpeech.StripTraffic(false, 85_000, null, false) == "Upload 85 KB/s");
        }
        finally { Loc.Use(language); }
    }
}
