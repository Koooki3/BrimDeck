using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace BrimDeck;

// Gives memory back while BrimDeck sits collapsed. Reading the usage logs, building a page and loading covers allocate in bursts;
// afterwards the garbage collector keeps the room it grew into. Once every panel has been collapsed and left alone for a while,
// the heap is compacted and the room it no longer needs is returned to Windows: measured, the committed heap went from about
// 61 MB to 25 MB and settled near 44 MB a minute later, where it had stayed at 61. That costs one collection of a few dozen
// milliseconds at most every five minutes, while nothing on screen moves: never while a panel is open, a surface is being resized
// or a notice is shown. Memory returned this way is free memory; nothing has to come back from anywhere on the next use.
// With DeckSettings.TrimMemoryWhenIdle (off by default) the working set is trimmed as well. That only moves BrimDeck's pages to
// the standby list: the number in Task Manager drops, but the pages come back on the next use, which can then stutter.
public partial class App
{
    private static readonly TimeSpan TrimIdle = TimeSpan.FromSeconds(45), TrimEvery = TimeSpan.FromMinutes(5);
    private DispatcherTimer? _trim;
    private DateTime _trimmedAt = DateTime.MinValue;

    // Called after a burst of work (a usage refresh) and whenever a panel collapses: the trim waits for a quiet stretch after it.
    internal void RequestTrim()
    {
        if (Exiting) { _trim?.Stop(); return; }
        _trim ??= CreateTrimTimer();
        _trim.Stop();
        var wait = _trimmedAt + TrimEvery - DateTime.UtcNow;
        _trim.Interval = wait > TrimIdle ? wait : TrimIdle;
        _trim.Start();
    }

    private DispatcherTimer CreateTrimTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (Exiting) return;
            // Someone is using a panel, or a notice is on screen: try again after the next quiet stretch.
            if (Decks.Any(deck => deck.IsExpanded || deck.IsResizing || deck.NoticeShown)) { RequestTrim(); return; }
            _trimmedAt = DateTime.UtcNow;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            // Aggressive also returns the memory the heap no longer uses to the system.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            if (Settings.TrimMemoryWhenIdle) SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        };
        return timer;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
