using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck;

// What the ambient light and the answers to the pointer need to know about Windows besides the settings: whether the session is
// locked, whether battery saver is on, and when high contrast or Windows' "animation effects" change. Changed is raised on the
// UI thread.
internal static class MotionEnvironment
{
    public static bool Locked { get; private set; }
    public static bool BatterySaver { get; private set; }
    public static event Action? Changed;
    private static Dispatcher? _dispatcher;

    public static void Install(Dispatcher dispatcher)
    {
        if (_dispatcher is not null) return;
        _dispatcher = dispatcher;
        BatterySaver = ReadBatterySaver();
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        SystemParameters.StaticPropertyChanged += SystemParameterChanged;
    }

    public static void Uninstall()
    {
        if (_dispatcher is null) return;
        SystemEvents.SessionSwitch -= SessionSwitch;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameterChanged;
        _dispatcher = null; Changed = null;
    }

    private static void SessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        bool? locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect => true,
            SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon => false,
            _ => null
        };
        if (locked is { } value) _dispatcher?.BeginInvoke(() => { if (Locked == value) return; Locked = value; Changed?.Invoke(); });
    }

    private static void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) return;
        _dispatcher?.BeginInvoke(() => { bool saver = ReadBatterySaver(); if (saver == BatterySaver) return; BatterySaver = saver; Changed?.Invoke(); });
    }

    private static void SystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation)) Changed?.Invoke();
    }

    // Battery saver (energy saver on Windows 11): SYSTEM_POWER_STATUS.SystemStatusFlag is 1 while it is on.
    private static bool ReadBatterySaver() => GetSystemPowerStatus(out var status) && (status.SystemStatusFlag & 1) != 0;
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32")] private static extern bool GetSystemPowerStatus(out PowerStatus status);

    // The conditions shared by every window; the window adds whether it is on screen.
    public static MotionConditions Conditions(bool visible = true, bool suppressed = false, bool poweringOff = false, bool hasMaterial = true) => new(
        (Application.Current as App)?.Settings.Animations != false, SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast,
        visible, suppressed, poweringOff, Locked, BatterySaver, hasMaterial);
}

// Something that moves on the shared ambient clock. Tick returns the interval it wants next, in milliseconds, or 0 when done.
internal interface IMotionClient { int Tick(double now); }

// The one slow clock behind every ambient light: a single timer at render priority that runs only while a client needs it, at
// the shortest interval any client asks for. Nothing is allocated per tick.
internal static class MotionTicker
{
    private static readonly List<IMotionClient> Clients = [];
    private static DispatcherTimer? _timer;
    private static int _interval;
    // Ticks since start, for tests and diagnostics.
    public static long Ticks { get; private set; }
    public static bool Running => _timer?.IsEnabled == true;

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
        Ticks++;
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

// The theme's ambient light on one island (ThemeMotion.Ambient): the rim and the glow breathe, the rim's colors drift along it,
// a faint scan line passes behind the content now and then, and the neon sags softly once in a long while. Only cheap properties
// change: the opacity and transform of an unfrozen copy of the rim brush, the glow's opacity (never its blur) and the opacity
// and transform of one rectangle. It runs only while MotionGate.Ambient allows it and stops by itself otherwise; stopping puts
// everything back at rest.
internal sealed class AmbientGlow : IMotionClient
{
    private const int CollapsedMs = 66, OpenMs = 40, SweepMs = 33;
    private readonly IslandBorder _island;
    private readonly DropShadowEffect? _shadow;
    private readonly Func<MotionConditions> _conditions;
    private readonly Func<bool> _open;
    private readonly Rectangle _sweep = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, Opacity = 0 };
    private readonly TranslateTransform _drift = new(), _sweepShift = new();
    private ThemeMotion _motion = ThemeMotion.Default;
    private Brush? _rim;
    private double _shadowBase = 1, _origin, _rimShown = 1, _driftShown, _sweepShown = -1, _sweepOpacityShown;
    private bool _glow, _running;

    public AmbientGlow(IslandBorder island, Panel content, DropShadowEffect? shadow, Func<MotionConditions> conditions, Func<bool> open)
    {
        _island = island; _shadow = shadow; _conditions = conditions; _open = open;
        // Behind everything on the surface, above its own layers.
        content.Children.Insert(0, _sweep);
    }

    public bool Running => _running;

    // Takes the current theme; call after PanelTheme.Dress has given the island its new rim and glow.
    public void Apply()
    {
        Halt();
        var palette = PanelTheme.Current;
        _motion = palette.Motion;
        _rim = null;
        if (_motion.Ambient.Any)
        {
            // An unfrozen copy that reflects past its ends, so the drift can move it any distance without a seam.
            var rim = PanelTheme.RimBrush(palette).Clone();
            if (rim is LinearGradientBrush gradient) { gradient.SpreadMethod = GradientSpreadMethod.Reflect; gradient.RelativeTransform = _drift; }
            _island.RimBrush = _rim = rim;
            var color = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, _motion.Color));
            var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), RelativeTransform = _sweepShift };
            band.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), .38));
            band.GradientStops.Add(new GradientStop(color, .5));
            band.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), .62));
            _sweep.Fill = band;
        }
        var effects = palette.Effects;
        _glow = effects.ShadowDepth <= 0 && effects.ShadowOpacity > 0;
        _shadowBase = effects.ShadowOpacity;
        Update();
    }

    // Starts or stops the light for the current conditions.
    public void Update()
    {
        bool run = _rim is not null && MotionGate.Ambient(_conditions(), _motion);
        if (run == _running) return;
        if (run) { _running = true; _origin = Eased.Now; MotionTicker.Add(this, Interval(-1)); }
        else Stop();
    }

    public void Stop()
    {
        Halt();
        if (_rim is not null) _rim.Opacity = 1;
        _drift.X = 0;
        if (_glow && _shadow is not null) _shadow.Opacity = _shadowBase;
    }

    private void Halt()
    {
        if (_running) MotionTicker.Remove(this);
        _running = false;
        _sweep.Visibility = Visibility.Collapsed; _sweep.Opacity = 0;
        _rimShown = 1; _driftShown = 0; _sweepShown = -1; _sweepOpacityShown = 0;
    }

    public int Tick(double now)
    {
        if (!_running) return 0;
        if (_rim is null || !MotionGate.Ambient(_conditions(), _motion)) { Stop(); return 0; }
        var frame = _motion.Ambient.At(now - _origin);
        // Changes below what eight bits of opacity can show are left for a later frame.
        // Only the rim breathes. The glow is a blurred shadow effect: any change to it, its opacity included, has WPF blur the
        // whole island again, which cost about 4% of a core at this rate (measured on the cyberpunk island).
        if (Math.Abs(frame.Rim - _rimShown) >= .002) _rim.Opacity = _rimShown = frame.Rim;
        if (Math.Abs(frame.Drift - _driftShown) >= .0015) _drift.X = _driftShown = frame.Drift;
        if (frame.Sweep >= 0)
        {
            if (_sweep.Visibility != Visibility.Visible) _sweep.Visibility = Visibility.Visible;
            if (Math.Abs(frame.Sweep - _sweepShown) >= .002) { _sweepShown = frame.Sweep; _sweepShift.Y = -.65 + 1.3 * frame.Sweep; }
            if (Math.Abs(frame.SweepOpacity - _sweepOpacityShown) >= .001) _sweep.Opacity = _sweepOpacityShown = frame.SweepOpacity;
        }
        else if (_sweepShown >= 0)
        {
            _sweepShown = -1; _sweepOpacityShown = 0; _sweep.Opacity = 0; _sweep.Visibility = Visibility.Collapsed;
        }
        return Interval(frame.Sweep);
    }

    // Slow light needs few frames: fewer still on the small collapsed island, a few more while a scan line moves.
    private int Interval(double sweep) => sweep >= 0 ? SweepMs : _open() ? OpenMs : CollapsedMs;
}
