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
        var paths = Hid.Paths();
        var infos = paths.Select(path => Hid.Query(path, batteries: true)).OfType<HidInfo>().ToList();
        check("devices: HID collections list and open for their attributes", paths.Count == 0 || infos.Count > 0 && infos.All(info => info.Vendor >= 0 && info.InputLength >= 0));

        var batteries = new DeviceBatteries(Dispatcher.CurrentDispatcher);
        Pump(TimeSpan.FromMilliseconds(100));
        int handles = Process.GetCurrentProcess().HandleCount;
        batteries.SetDemand(DeviceDemand.Watch);
        Pump(TimeSpan.FromSeconds(4));
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
        for (int i = 0; i < 4; i++)
        {
            batteries.SetDemand(DeviceDemand.Watch); Pump(TimeSpan.FromSeconds(1.5));
            batteries.SetDemand(DeviceDemand.None); Pump(TimeSpan.FromSeconds(11));
            counts.Add(Handles());
        }
        Console.WriteLine($"  device handles: {handles} before the first start, then {string.Join(", ", counts)} after each stop");
        check("devices: starting and stopping again leaves no handles behind", counts[^1] - counts[1] < 12);
        batteries.Dispose();
    }

    private static int Handles() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return Process.GetCurrentProcess().HandleCount; }

    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
