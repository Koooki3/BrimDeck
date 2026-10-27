using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The two clocks everything that moves on its own runs on. Motion follows the display, for what eases or glides (readings,
// hover, the lyric marquee); MotionTicker is a slow timer for the ambient light, which needs far fewer frames. Both run only
// while something needs them.

// Redraws the elements whose values are easing, while any is, with the display. Otherwise it is unhooked and the panel costs
// nothing between readings.
internal static class Motion
{
    private static readonly List<UIElement> Moving = [];
    private static bool _hooked;
    // Whether what moves on this clock eases (Soft.Enabled); otherwise it takes its new value at once.
    public static bool Enabled => Soft.Enabled;
    private static TimeSpan _last;
    // The display frame being drawn, in seconds (RenderingTime), for a step that moves at a steady speed (MusicMarquee):
    // each frame's own time spaces the steps evenly, where the time the step runs at would vary with the dispatcher.
    public static double FrameTime { get; private set; }
    private static double _period = 1000 / 60.0;
    private static int _every = 1;
    private static readonly Dictionary<UIElement, Func<double, bool>> Steps = [];

    // step(now) redraws the element and returns whether it still moves.
    public static void Start(UIElement element, Func<double, bool> step)
    {
        Steps[element] = step;
        if (!Moving.Contains(element)) Moving.Add(element);
        if (_hooked) return;
        _hooked = true; _last = TimeSpan.Zero; (_period, _every) = Pacing(); CompositionTarget.Rendering += Tick;
    }

    // The frames a second this clock draws at, for WPF's own animations too (App: Timeline.DesiredFrameRate).
    public static int FrameRate { get; } = Pacing() is var (period, every) ? (int)Math.Round(1000 / (period * every)) : 60;

    // The display's refresh period in milliseconds, from the desktop compositor, and how many of its frames make one of ours.
    // DWM_TIMING_INFO is packed to the byte and 292 bytes long; only its refresh rate is read (a ratio, after the size).
    private static (double Period, int Every) Pacing()
    {
        const int Size = 292;
        double hertz = 0;
        var info = Marshal.AllocHGlobal(Size);
        try
        {
            for (int i = 0; i < Size; i += 4) Marshal.WriteInt32(info, i, 0);
            Marshal.WriteInt32(info, 0, Size);
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, info) == 0)
            {
                uint numerator = (uint)Marshal.ReadInt32(info, 4), denominator = (uint)Marshal.ReadInt32(info, 8);
                if (denominator > 0) hertz = numerator / (double)denominator;
            }
        }
        finally { Marshal.FreeHGlobal(info); }
        if (!(hertz >= 24 && hertz <= 500)) hertz = 60;
        return (1000 / hertz, hertz > 180 ? 2 : 1);
    }
    [DllImport("dwmapi")] private static extern int DwmGetCompositionTimingInfo(IntPtr window, IntPtr info);

    private static void Tick(object? sender, EventArgs e)
    {
        // Every display frame, or every other one above 180 Hz. Drawing only every other frame at 144 Hz looked lighter, but a WPF
        // frame with nothing to draw lets the render loop fall out of step, and the next frame then came a refresh late: three
        // steps in ten were uneven, against one in sixteen when every frame draws. What a frame draws is kept small instead
        // (DrawingCache), so following the display costs little. The rhythm counts from the display's own refresh period.
        double now = Eased.Now;
        if (e is RenderingEventArgs args)
        {
            var time = args.RenderingTime;
            if (_last != TimeSpan.Zero && (time - _last).TotalMilliseconds < _every * _period - _period / 2) return;
            _last = time; FrameTime = time.TotalSeconds;
        }
        for (int i = Moving.Count - 1; i >= 0; i--)
        {
            var element = Moving[i];
            if (!Steps.TryGetValue(element, out var step) || !step(now)) { Moving.RemoveAt(i); Steps.Remove(element); }
        }
        if (Moving.Count > 0) return;
        _hooked = false; CompositionTarget.Rendering -= Tick;
    }
}

// Something that moves on the shared ambient clock. Tick returns the interval it wants next, in milliseconds, or 0 when done.
internal interface IMotionClient { int Tick(double now); }

// The slow clock behind every ambient light: a single timer at render priority that runs only while a client needs it, at the
// shortest interval any client asks for. Nothing is allocated per tick.
internal static class MotionTicker
{
    private static readonly List<IMotionClient> Clients = [];
    private static DispatcherTimer? _timer;
    private static int _interval;

    public static void Add(IMotionClient client, int interval)
    {
        if (!Clients.Contains(client)) Clients.Add(client);
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled || interval < _interval) SetInterval(interval);
        if (!_timer.IsEnabled) _timer.Start();
    }

    public static void Remove(IMotionClient client)
    {
        Clients.Remove(client);
        if (Clients.Count == 0) _timer?.Stop();
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render);
        timer.Tick += Tick;
        return timer;
    }

    private static void SetInterval(int interval)
    {
        interval = Math.Clamp(interval, 16, 1000);
        if (interval == _interval) return;
        _interval = interval;
        _timer!.Interval = TimeSpan.FromMilliseconds(interval);
    }

    private static void Tick(object? sender, EventArgs e)
    {
        double now = Eased.Now;
        int wanted = int.MaxValue;
        // From the end, by reference: a client that stops may already have removed itself.
        for (int i = Clients.Count - 1; i >= 0; i--)
        {
            if (i >= Clients.Count) continue;
            var client = Clients[i];
            int next = client.Tick(now);
            if (next <= 0) Clients.Remove(client);
            else wanted = Math.Min(wanted, next);
        }
        if (Clients.Count == 0) { _timer!.Stop(); return; }
        SetInterval(wanted);
    }
}
