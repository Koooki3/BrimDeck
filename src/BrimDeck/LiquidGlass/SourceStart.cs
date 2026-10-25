using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace BrimDeck.LiquidGlass;

// The capture for liquid glass, made off the UI thread. Making a DesktopSource's device takes some 200 ms (measured 194 ms; more
// on a hybrid GPU, where nothing bounds it), starting its duplication more, and the renderer's first shaders some 250 ms more the
// first time in a process: done on the UI thread, before the first paint and whenever a glass first shows, it held the UI that
// long. Here a worker makes the source, starts its duplication over the region the glass will need (and, for automatic liquid
// glass's first look, measures it: BackdropLook.Measure), makes the renderer's shaders once so the driver has them, and parks
// the source; the UI thread takes it (DesktopSource.Open), whole, so it is never made twice. A source not taken within SpareMs
// is let go (Desktop Duplication allows one duplication of an output per process).
//
// Bounded: a worker not done within BoundMs counts as a failure (the glass shows the blur and tries again later, automatic
// liquid glass follows the Windows app theme meanwhile); a source it still makes later is parked and taken then. Only one
// worker runs at a time, so a device that never comes holds one thread, not one per try.
//
// Prepare, Take, WhenDone and the rest are called on the UI thread; the worker only parks its source (under Gate) and posts
// WhenDone's callbacks back to that thread.
internal static class SourceStart
{
    public const int BoundMs = 1500, SpareMs = 5000;

    // What a worker found: the measured brightness (when asked to measure and the capture answered), and how long it took.
    public readonly record struct Prepared(bool Made, double? Luma, long Milliseconds);

    private static readonly object Gate = new();
    private static DesktopSource? _spare;
    private static Task<Prepared>? _task;
    private static long _startedAt;
    private static volatile bool _failed;
    private static Dispatcher? _dispatcher;
    private static readonly List<Action> Waiting = [];
    private static int _warmed;

    // A worker is under way; and it has been for longer than BoundMs.
    public static bool Pending => _task is { IsCompleted: false };
    public static bool Late => Pending && Environment.TickCount64 - _startedAt > BoundMs;

    // Starts a worker making a source and starting its duplication over region (device pixels), measuring the mean brightness
    // there when measure is set, unless one is under way already: then that one's task, or (to measure) one that measures with
    // its source once it is done. A parked source is used rather than a new one made.
    public static Task<Prepared> Prepare(RECT region, bool measure)
    {
        _dispatcher ??= Dispatcher.CurrentDispatcher;
        var previous = Pending ? _task : null;
        if (previous is not null && !measure) return previous;
        _failed = false;
        if (previous is null) _startedAt = Environment.TickCount64;
        var task = Task.Run(async () =>
        {
            if (previous is not null) await Task.WhenAny(previous).ConfigureAwait(false);
            return Work(region, measure);
        });
        _task = task;
        _ = task.ContinueWith(done =>
        {
            if (!done.IsCompletedSuccessfully) _failed = true;
            Notify();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return task;
    }

    // The parked source, if any: the caller owns it from here on.
    public static DesktopSource? Take()
    {
        lock (Gate) { var source = _spare; _spare = null; return source; }
    }

    // Whether the last worker made no source (no capture on this machine, or it failed): reported once.
    public static bool TakeFailure()
    {
        if (!_failed || Pending) return false;
        _failed = false;
        return true;
    }

    // Called on the UI thread once the worker under way is done (a source parked, or none made).
    public static void WhenDone(Action done)
    {
        if (!Waiting.Contains(done)) Waiting.Add(done);
    }

    private static void Notify()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            var waiting = Waiting.ToArray();
            Waiting.Clear();
            foreach (var each in waiting) each();
        });
    }

    private static Prepared Work(RECT region, bool measure)
    {
        var clock = Stopwatch.StartNew();
        DesktopSource? source = Take();
        double? luma = null;
        try
        {
            source ??= DesktopSource.TryCreate();
            if (source is null) { _failed = true; return new(false, null, clock.ElapsedMilliseconds); }
            if (measure) luma = BackdropLook.Measure(source, region);
            else source.Update(region, out _);
            Warm(source);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException or SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            source?.Dispose();
            _failed = true;
            return new(false, null, clock.ElapsedMilliseconds);
        }
        Park(source);
        return new(true, luma, clock.ElapsedMilliseconds);
    }

    // The renderer's shaders made once on this worker: the driver keeps what it compiled, and the glass's own renderer, made on
    // the UI thread, then takes about a millisecond instead of some 250.
    private static void Warm(DesktopSource source)
    {
        if (Interlocked.Exchange(ref _warmed, 1) != 0) return;
        using (new LiquidRenderer(source.Device)) { }
    }

    private static void Park(DesktopSource source)
    {
        DesktopSource? old;
        lock (Gate) { old = _spare; _spare = source; }
        old?.Dispose();
        _ = Task.Delay(SpareMs).ContinueWith(_ =>
        {
            lock (Gate) { if (!ReferenceEquals(_spare, source)) return; _spare = null; }
            source.Dispose();
        }, TaskScheduler.Default);
    }
}
