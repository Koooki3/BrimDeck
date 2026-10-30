using System.Diagnostics;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

// Reads this machine's wireless devices the way the devices page does (DeviceBatteries), read only: nothing is paired, written
// or changed. A machine may have none connected, so the checks hold either way: every reading is a percentage or none, every
// device has a name and a key, stopping clears the list, and starting and stopping again leaves no handles behind. What was
// read is printed.
internal static class DeviceChecks
{
    public static void Run(Action<string, bool> check)
    {
        PathFilters(check);
        var paths = Hid.Paths();
        var infos = paths.Select(path => Hid.Query(path, batteries: true)).OfType<HidInfo>().ToList();
        check("devices: HID collections list and open for their attributes", paths.Count == 0 || infos.Count > 0 && infos.All(info => info.Vendor >= 0 && info.InputLength >= 0));

        var batteries = new DeviceBatteries(Dispatcher.CurrentDispatcher);
        Pump(TimeSpan.FromMilliseconds(100));
        int handles = Process.GetCurrentProcess().HandleCount;
        int threads = Threads();
        batteries.SetDemand(DeviceDemand.Watch);
        // Until every source has listed what is connected, or the orchestrator's ten-second cap has passed.
        long settleBy = Environment.TickCount64 + 11_000;
        while (!batteries.Settled && Environment.TickCount64 < settleBy) Pump(TimeSpan.FromMilliseconds(250));
        var read = batteries.All.ToList();
        foreach (var device in read) Console.WriteLine($"  device: {device.Kind} {Devices.Figure(device.Lowest)}{(device.Charging ? " charging" : "")} via {device.Source}: {device.Name}");
        check("devices: the sources settle, and every reading is a percentage or none", batteries.Settled
            && read.All(device => device.Name.Length > 0 && device.Key.Length > 0 && device.Lowest is null or (>= 0 and <= 100)));
        check("devices: one entry per device", read.Select(device => device.Key).Distinct().Count() == read.Count);
        batteries.SetDemand(DeviceDemand.None);
        // Asked for less, the sources linger ten seconds before they stop.
        Pump(TimeSpan.FromSeconds(11));
        check("devices: stopped, nothing is listed", batteries.All.Count == 0 && batteries.Demand == DeviceDemand.None);
        // The first run loads what stays loaded (WinRT, the worker thread); later runs must not add to it.
        var counts = new List<int> { Handles() };
        var threadCounts = new List<int> { Threads() };
        for (int i = 0; i < 4; i++)
        {
            batteries.SetDemand(DeviceDemand.Watch); Pump(TimeSpan.FromSeconds(1.5));
            batteries.SetDemand(DeviceDemand.None); Pump(TimeSpan.FromSeconds(11));
            counts.Add(Handles());
            threadCounts.Add(Threads());
        }
        // A leak adds handles on every cycle, so the last two cycles both stand above the first two; a handle the thread pool or
        // WinRT holds for a moment raises one reading only. Over 12 cycles the steady counts here wander within some 15 handles
        // without growing, at times two cycles in a row at the top: a bound missed is measured again over two more cycles, up to
        // twice, which a leak, still growing, misses every time. A leak of 4 handles a cycle, the least the earlier bound of 12
        // over three cycles caught, misses the bound of 8 below.
        int early = Math.Min(counts[1], counts[2]), late = Math.Min(counts[^1], counts[^2]);
        for (int again = 0; again < 2 && late - early >= 8; again++)
        {
            Console.WriteLine($"  device handles: {late - early:+0;-0;0} after the cycles; two more cycles");
            for (int i = 0; i < 2; i++)
            {
                batteries.SetDemand(DeviceDemand.Watch); Pump(TimeSpan.FromSeconds(1.5));
                batteries.SetDemand(DeviceDemand.None); Pump(TimeSpan.FromSeconds(11));
                counts.Add(Handles());
            }
            late = Math.Min(counts[^1], counts[^2]);
        }
        Console.WriteLine($"  device handles: {handles} before the first start, then {string.Join(", ", counts)} after each stop");
        Console.WriteLine($"  threads: {threads} before the first start, then {string.Join(", ", threadCounts)} after each stop");
        check($"devices: starting and stopping again leaves no handles behind ({late - early:+0;-0;0})", late - early < 8);
        // The HID worker ends when nothing is wanted, so the threads after the last stop are no more than after the first; one
        // more is allowed for the thread pool and WinRT, which start and end threads of their own.
        check("devices: starting and stopping again leaves no threads behind", threadCounts[^1] <= threadCounts[0] + 1);
        batteries.Dispose();
    }

    // The steady count: the fewest of a few readings a moment apart, after a collection, so handles held for a moment by the thread
    // pool or WinRT (more of them while the machine is busy) are not counted.
    private static int Handles()
    {
        int fewest = int.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            fewest = Math.Min(fewest, Process.GetCurrentProcess().HandleCount);
            Pump(TimeSpan.FromMilliseconds(150));
        }
        return fewest;
    }
    private static int Threads() => Process.GetCurrentProcess().Threads.Count;

    // What the HID source opens, from the path alone: the vendor and product of a USB, classic Bluetooth and LE path, the link,
    // and which vendors' Bluetooth collections are opened at all.
    // Public for the hosted subset: it reads no device.
    public static void PathFilters(Action<string, bool> check)
    {
        const string usb = @"\\?\hid#vid_046d&pid_c52b&mi_02&col01#8&2a3b4c5d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string classic = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string le = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b023_rev&0007_d1c2b3a4f5e6&col02#a&1b2c3d4e&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        check("devices: vendor and product from USB, classic and LE paths", Hid.Ids(usb) == (0x046D, 0xC52B) && Hid.Ids(classic) == (0x054C, 0x0CE6) && Hid.Ids(le) == (0x046D, 0xB023)
            && Hid.Ids(@"\\?\hid#acpi0c50&col01#1#{4d1e55b2}") == (0, 0));
        check("devices: Bluetooth paths, classic and LE, are told from USB", !Hid.IsBluetooth(usb) && Hid.IsBluetooth(classic) && Hid.IsBluetooth(le));
        check("devices: on Bluetooth only the vendors a reader speaks to are opened", HidBatteries.Spoken(Hid.Ids(classic)) && !HidBatteries.Spoken(Hid.Ids(le))
            && HidBatteries.Spoken((0x1532, 0x00C1)) && !HidBatteries.Spoken((0x1532, 0x9999)) && !HidBatteries.Spoken((0x0BDA, 0x4762)) && !HidBatteries.Spoken((0x1915, 0x0001))
            && HidBatteries.Spoken((0x1038, 0x12AD)) && !HidBatteries.Spoken((0x045E, 0x0B13)) && !HidBatteries.Spoken((0, 0)));
        check("devices: an LE path carries the address, a classic one does not", Devices.AddressIn(le) == "D1C2B3A4F5E6" && Devices.AddressIn(classic) is null);
    }

    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
