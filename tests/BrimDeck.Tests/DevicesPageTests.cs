using System.Text.Json;
using BrimDeck.Core;

// The devices page: its layout steps, its settings, and the collapsed battery reading's place among the readings.
static class DevicesPageTests
{
    public static void Run(Action<string, bool> check)
    {
        Layout(check);
        Settings(check);
        Strip(check);
    }

    private static void Layout(Action<string, bool> check)
    {
        // The default page's content: 420 × 148.
        var one = DevicesLayout.For(420, 148, 1, null);
        var two = DevicesLayout.For(420, 148, 2, one);
        check("Devices layout: one and two devices share the comfortable step (no rebuild as the second arrives)", one == two && !one.Dense && one.Columns == 1 && one.Room == 2);
        var three = DevicesLayout.For(420, 148, 3, two);
        check("Devices layout: a third device takes the dense step, which holds six", three.Dense && three.Room == 6 && three.Columns == 2);
        check("Devices layout: four to six devices stay in that step", DevicesLayout.For(420, 148, 6, three) == three && DevicesLayout.For(420, 148, 4, three) == three);
        check("Devices layout: back to two keeps the dense step; one returns to the comfortable one", DevicesLayout.For(420, 148, 2, three) == three && !DevicesLayout.For(420, 148, 1, three).Dense);
        var wide = DevicesLayout.For(660, 248, 6, null);
        check("Devices layout: a large page holds six comfortably", !wide.Dense && wide.Room >= 6);
        check("Devices layout: no card is wider than the maximum", DevicesLayout.For(1160, 348, 1, null).CardSize(1160, 348).Width <= DevicesLayout.MaximumCard);
        var held = DevicesLayout.For(420, 148, 3, null);
        check("Devices layout: a resize within the band keeps the step", DevicesLayout.For(420 + DevicesLayout.Hysteresis - 1, 148, 3, held) == held);
        var (x, y, w, h) = three.Card(4, 420, 148, 6);
        check("Devices layout: cards lie within the page", x >= 0 && y >= 0 && x + w <= 420 + .5 && y + h <= 148 + .5 && w > 0 && h <= DevicesLayout.MaximumRow);
        var (alone, _, _, _) = wide.Card(0, 660, 248, 1);
        var (single, _) = wide.CardSize(660, 248);
        check("Devices layout: a single device sits in the middle", Math.Abs(alone + single / 2 - 330) <= 1);
        check("Devices layout: a size that is not a number is taken as nothing", DevicesLayout.For(double.NaN, double.PositiveInfinity, 3, null).Columns == 1);
    }

    private static void Settings(Action<string, bool> check)
    {
        var defaults = new DeckSettings();
        check("Devices settings: the page is on, low at 15 %", defaults.DevicesPage && defaults.DeviceLowPercent == 15 && defaults.HiddenDevices.Count == 0);
        var saved = new DeckSettings { DevicesWidth = 9999, DevicesHeight = double.NaN, DeviceLowPercent = 7, HiddenDevices = [new("bt:AA", "Mouse"), new("bt:AA", "Mouse"), new(" ", "x")] };
        saved.Normalize();
        check("Devices settings: normalized", saved.DevicesWidth == 1200 && saved.DevicesHeight == 200 && saved.DeviceLowPercent == Devices.DefaultLowPercent && saved.HiddenDevices.Count == 1);
        var back = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { HiddenDevices = [new("bt:AA", "Mouse")], LastPage = DeckPage.Devices, DefaultPage = DefaultDeckPage.Devices }))!;
        check("Devices settings: survive saving", back.Hides("bt:AA") && back.HiddenDevices[0].Name == "Mouse" && back.LastPage == DeckPage.Devices && back.DefaultPage == DefaultDeckPage.Devices);
        check("Devices settings: the page is saved as number 4, its opening choice as 5 (3 and 4 are kept free for a page in review)",
            (int)DeckPage.Devices == 4 && (int)DefaultDeckPage.Devices == 5 && DeckPages.Opening(DefaultDeckPage.Devices) == DeckPage.Devices);
        var sized = new DeckSettings();
        DeckPages.SetSize(sized, DeckPage.Devices, 600, 250);
        check("Devices settings: a drag sizes the devices page alone", DeckPages.Size(sized, DeckPage.Devices) == (600, 250) && sized.Width == new DeckSettings().Width);
        // A file saved before the devices page has none of its members and needs no migration step: the page takes its defaults, joins
        // the page order at the end and the scale and the other settings read as they were.
        var older = SettingsMigrations.Read("""{"AppVersion":"1.2.2","MonitorPage":true,"PanelScale":150,"PageOrder":[2,0,1]}""");
        check("Devices settings: a file from before the page reads with the page's defaults, last in the order",
            older.DevicesPage && older.DevicesWidth == 460 && older.DevicesHeight == 200 && older.HiddenDevices.Count == 0 && older.DeviceLowPercent == Devices.DefaultLowPercent
            && older.PageOrder.SequenceEqual([DeckPage.Monitor, DeckPage.Usage, DeckPage.Music, DeckPage.Devices]) && older.PanelScale == 150);
        check("Devices settings: the collapsed battery reading joins an older table hidden", older.CompactReadings.Single(r => r.Item == CompactMonitorItems.Battery) is { Visible: false });
    }

    private static void Strip(Action<string, bool> check)
    {
        var older = CompactMonitor.Normalize([new CompactReading { Item = CompactMonitorItems.Cpu }]);
        check("Battery reading: an older table gains it hidden, just before the frame rate at the end", older[^2].Item == CompactMonitorItems.Battery && !older[^2].Visible && older[^1].Item == CompactMonitorItems.Frames);
        check("Battery reading: it leaves right after the frame rate when the row is short", CompactMonitor.LeaveOrder[1] == CompactMonitorItems.Battery && (CompactMonitor.Every & CompactMonitorItems.Battery) != 0);
        var readings = CompactMonitor.Normalize(null);
        foreach (var reading in readings) reading.Visible = reading.Item == CompactMonitorItems.Battery;
        CompactMonitor.Apply(readings, CompactMonitorMode.Full);
        check("Battery reading: the Full preset shows it, as it shows every reading", readings.All(r => r.Visible));
        CompactMonitor.Apply(readings, CompactMonitorMode.Brief);
        check("Battery reading: the Brief preset hides it", !readings.Single(r => r.Item == CompactMonitorItems.Battery).Visible);
        var alone = CompactMonitor.Normalize(null);
        foreach (var reading in alone) reading.Visible = reading.Item == CompactMonitorItems.Battery;
        static SensorStates Battery(SensorState state) => SensorStates.Unknown with { Battery = state };
        var admission = new StripAdmission();
        var (hidden, _) = admission.Update(null, alone, Battery(SensorState.Unknown));
        check("Battery reading: left out until a device reports", (hidden & CompactMonitorItems.Battery) != 0);
        check("Battery reading: a row showing only it never asks for the system readings", !admission.Awaits(alone, null, Battery(SensorState.Unknown)));
        var (found, arriving) = admission.Update(null, alone, Battery(SensorState.Found));
        check("Battery reading: joins with the first reading", (found & CompactMonitorItems.Battery) == 0 && (arriving & CompactMonitorItems.Battery) != 0
            && admission.Layout.Contains(((int)CompactMonitorItems.Battery).ToString(), StringComparison.Ordinal));
        var (held, _) = admission.Update(null, alone, Battery(SensorState.Unknown));
        check("Battery reading: stays while the reading is away a moment", (held & CompactMonitorItems.Battery) == 0);
        var (none, _) = admission.Update(null, alone, Battery(SensorState.None));
        check("Battery reading: leaves once no device reports", (none & CompactMonitorItems.Battery) != 0);
    }
}
