using System.Diagnostics;
using System.IO;
using BrimDeck.Core;
using BrimDeck.Native;

// The guard over readings that ask a driver (SensorGuard), in a folder of its own: the mark on disk while a reading is asked, a
// reading that never answers stopped and its thread told, a mark left behind stopping the reading at the next start, and turning
// a reading on again. Run last: the guard is static, and its stops would hold for the other checks.
internal static class SensorGuardChecks
{
    public static void Run(Action<string, bool> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck-SensorGuard-" + Guid.NewGuid().ToString("N"));
        string pending = Path.Combine(folder, SensorStops.PendingFile);
        try
        {
            SensorGuard.Start(folder);
            check("sensor guard: nothing stopped in a new folder", SensorGuard.Stops.Count == 0);
            var drive = new GuardedDevice(GuardedReading.DriveTemperature, "Test SSD | 17", "Test SSD");
            string during = SensorGuard.Ask(drive, () => File.ReadAllText(pending));
            check("sensor guard: the reading is marked on disk while it is asked, and the mark goes once it answers",
                SensorStops.ParsePending(during, DateTimeOffset.Now) is [{ Device: "Test SSD | 17" }] && SensorStops.ParsePending(File.ReadAllText(pending), DateTimeOffset.Now).Count == 0);

            // A reading that does not answer in time: stopped, its caller told, and its thread ends once it answers after all.
            var gpu = new GuardedDevice(GuardedReading.GpuTemperature, "Test GPU", "Test GPU");
            int told = 0, changed = 0;
            void Changed() => changed++;
            SensorGuard.Changed += Changed;
            bool abandoned = false;
            var watch = Stopwatch.StartNew();
            try { SensorGuard.Ask(gpu, () => { Thread.Sleep((SensorStops.StuckSeconds + 3) * 1000); return 0; }, () => Interlocked.Increment(ref told)); }
            catch (ReadingAbandoned) { abandoned = true; }
            Console.WriteLine($"  a stuck reading was given up after at most {watch.Elapsed.TotalSeconds:0.0} s");
            check("sensor guard: a reading that does not answer is stopped, its caller told once, and its thread ends", abandoned && told == 1 && changed == 1
                && SensorGuard.Stopped(GuardedReading.GpuTemperature, "Test GPU") && !SensorGuard.Stopped(GuardedReading.GpuTemperature, "Other GPU"));
            check("sensor guard: the stop is kept on disk", SensorStops.Parse(File.ReadAllText(Path.Combine(folder, SensorStops.StopsFile))) is [{ Device: "Test GPU" }]);

            // A mark left behind by a run that ended while a reading was asked (a frozen machine) stops that reading at the next start.
            File.WriteAllText(pending, SensorStops.SerializePending([drive]));
            SensorGuard.Start(folder);
            check("sensor guard: a mark left behind stops its reading at the next start", SensorGuard.Stopped(GuardedReading.DriveTemperature, "Test SSD | 17")
                && SensorGuard.Stopped(GuardedReading.GpuTemperature, "Test GPU") && SensorStops.ParsePending(File.ReadAllText(pending), DateTimeOffset.Now).Count == 0);

            foreach (var stop in SensorGuard.Stops) SensorGuard.Resume(stop);
            SensorGuard.Changed -= Changed;
            check("sensor guard: turned on again, nothing is stopped, now or at the next start", SensorGuard.Stops.Count == 0 && changed == 3
                && SensorStops.Parse(File.ReadAllText(Path.Combine(folder, SensorStops.StopsFile))).Count == 0);
            SensorGuard.Finish();
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
