using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;
using BrimDeck.Native;

namespace BrimDeck;

// Liquid glass's material (the DWM glass under the island, GlassSurface) as three channels, after the review of 11-19:
//   Material response: each look's luminosity opacity follows what the backdrop around the island asks for (GlassRecipe.
//     LuminosityFor, from AcrylicProbe), on its own spring (FluidSpring): thicker at once, thinner only when two readings agree
//     (GlassMaterial.Settle). Both looks are followed all the time, so a palette turn starts from fresh numbers.
//   Semantic palette (automatic liquid glass only): GlassAppearance decides dark or light, with its hysteresis, dwell, video
//     state and the open panel's hold; a decision moves the palette spring (0 dark, 1 light). A new decision mid-way retargets
//     it from where it is, at its speed: nothing queues, nothing restarts.
//   Readability: the text swaps once, at the frame its new colours read at least as well as the old ones over the glass then
//     shown (GlassReadability.SwapNow); only the leading panel of a shared look publishes it, for the turn's current epoch
//     (GlassLookGroup: one decision per shared look, kept across a change of leader).
// Readings (GlassSampleValidity): trusted by their capture's start, dropped when late or for a place the panel has left; a
// watchdog of its own (a one-shot timer, never waiting for the worker) widens the tails the glass answers for toward the worst
// range when readings stop, and only two fresh readings that agree let it go back. At most GlassReadBudget captures run in the
// app at once.
// Optics: the rim's gradient and an inner bevel glow start on the brighter side of the desktop around the island (GlassLight,
// from the probe's four strips), turning there slowly; the bevel widens and the rim softens as the panel opens.
// The springs run on the display's frames (CompositionTarget.Rendering) only while one moves, and set the persistent graph's
// weights (GlassSurface.Present): nothing is compiled while the material moves. Sampling never stops for a turn. It runs
// twice a second at rest and in a short burst after a hint that the backdrop may have changed (another window came to the
// front, the window in front changed its title - a browser's tab or page, told by GlassHintBroker -, the panel moved or began
// to open or close, a reading that changed a lot), one reading at a time.
public partial class MainWindow
{
    private const int GlassProbeInterval = 500, GlassBurstInterval = 80, GlassBurstMs = 700;
    // Soft hints (a title change, a reading that moved a lot) start a burst at most this often.
    private const int GlassHintGap = 2000;
    // Stiffness (rad/s): the veil thickens within 5 % in about 180 ms and thins in about 300 ms; a palette turn about 365 ms.
    private const double GlassVeilAttack = 26, GlassVeilRelease = 16, GlassPaletteOmega = 13;
    // A turn passes through greys where neither palette's text reaches 4.5:1; while it is there the palette's spring is stiffened
    // (from where it is, at its speed) so that stretch is short.
    private const double GlassPaletteHurry = 26, GlassReadable = 4.5;
    private AcrylicProbe _acrylicProbe = new();
    private GlassSurface? _glassSurface;
    // The panels that share a look share its palette decision (keyed by the look, gone with it).
    private static readonly ConditionalWeakTable<PanelLook, GlassLookGroup<MainWindow>> GlassGroups = new();
    // Keyed by the panel for its ring reads and by its _glassCoverKey for its covered area's.
    private static readonly GlassReadBudget<object> GlassReads = new();
    private GlassLookGroup<MainWindow> GlassGroup => GlassGroups.GetValue(Look, _ => new());
    private DispatcherTimer? _glassProbe, _glassWatchdog;
    private GlassSampleValidity _glassValidity = new(0);
    private bool _glassActive, _glassQueued, _glassRendering, _glassProtected;
    private long _glassGeneration, _glassTurnEpoch;
    private Int32Rect _glassBounds;
    private double _glassOpenHeight;
    // The material's channels (GlassSurface.Material) and what they head for.
    private FluidSpring _glassDarkVeil, _glassLightVeil, _glassPalette;
    private bool _glassMaterialReady, _glassFirstSample, _glassTurning, _glassGoalDark, _glassWasExpanded, _glassHurried;
    private double _glassRequiredDark, _glassRequiredLight, _glassWantedDark = double.NaN, _glassWantedLight = double.NaN;
    private long _glassBurstUntil, _glassReadAt, _glassSoftBurstAt = long.MinValue / 2;
    private double _glassLastMiddle = double.NaN;
    // The luminosity each look asks for over the area the opening panel covers (read as it began; NaN: none), reached in step with
    // the opening shown (GlassOpening), and the openness the veils last followed.
    private double _glassCoverDark = double.NaN, _glassCoverLight = double.NaN, _glassVeilOpenness = double.NaN;
    private AcrylicProbe? _glassCoverProbe;
    // The panel's openings (V302/V304/V308): a cover read belongs to one; the cancel of the one running; when the panel's first
    // frame after the open intent began (a read that began later may show our own glass, V303/V319).
    private readonly GlassOpeningSession _glassOpening = new();
    private readonly object _glassCoverKey = new();
    private CancellationTokenSource? _glassCoverCancel;
    private long _glassIntentAt, _glassIntentStamp, _glassIntentFrameAt; // _glassIntentStamp, _glassIntentFrameAt: Stopwatch timestamps
    // Diagnostics for the isolated harness (one line per cover read's outcome); null in normal use.
    internal static Action<string>? GlassCoverTrace { get; set; }
    private (double Bright, double Dark) _glassTails = (1, 0);
    private readonly record struct GlassSampleArea(nint Monitor, Rect Screen, Int32Rect Bounds, double Scale, PanelLook Look);
    // The light's direction shown (moved toward the target on the display's frames, GlassOpticsFrame) and the one the latest
    // reading asks for.
    private (double X, double Y) _glassLight = GlassLight.Default, _glassLightTarget = GlassLight.Default;
    private double _glassLightAt;
    private bool _glassOpticsRendering;
    private GlassEdge? _glassEdge;
    private IReadOnlyList<ThemeColor>? _glassEdgeStops;
    // The outline's shape in the window's pixels apart from where it is (a move keeps it), its points in the island's own
    // units for a contour, and the contour for the place's fraction of a pixel.
    private (Size Size, double Shoulder, CornerRadius Corners, Vector Along, Vector Depth)? _glassOutline;
    private Point[]? _glassOutlinePoints;
    private (double X, double Y) _glassContourAt;
    private GlassSurface.PixelPoint[]? _glassContour;

    // A title change of the window in front (GlassHintBroker) hints at a new backdrop.
    private Action? _glassTitleHint;
    private bool _glassTitlePending;

    internal static bool GlassAvailable => PanelTheme.GlassAvailable;
    // A palette turn is under way: the palette still moves, or the text has not yet swapped to the goal.
    internal bool GlassPaletteTurning => _glassTurning;

    internal void UpdateGlassTheme()
    {
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) { GlassGroup.Decision = null; EndGlassTurn(); }
        // The theme was applied again: the edge takes its light's shape from it anew.
        _glassEdgeStops = null;
        bool enabled = Look.Current.Effects.Glass is not null && !Look.HighContrast && GlassAvailable && !_retiring;
        if (enabled == (_glassSurface is not null)) { QueueGlass(); return; }
        if (!enabled) { CloseGlass(); return; }
        if (GlassComposition.Compositor is { } compositor) GlassEffect.Prewarm(compositor);
        _glassSurface = new GlassSurface();
        _glassProbe = new DispatcherTimer(TimeSpan.FromMilliseconds(GlassProbeInterval), DispatcherPriority.Background, SampleGlass, Dispatcher);
        _glassProbe.Stop();
        // The watchdog (U05): Normal priority, so a busy Background queue does not delay the text's protection.
        _glassWatchdog = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _glassWatchdog.Tick += GlassWatchdogTick;
        LayoutUpdated += GlassLayout;
        LocationChanged += GlassLocation;
        IsVisibleChanged += GlassVisibility;
        _host.ForegroundSwitched += GlassForeground;
        _glassTitleHint ??= GlassTitle;
        GlassHintBroker.Subscribe(_glassTitleHint);
        QueueGlass();
    }

    private void GlassTitle()
    {
        if (_glassTitlePending || !_glassActive) return;
        _glassTitlePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _glassTitlePending = false; GlassBurst(soft: true); });
    }

    // A palette turn ends where the theme now is: a fixed glass chosen mid-way, or no automatic glass any more.
    private void EndGlassTurn()
    {
        if (!_glassTurning) return;
        _glassTurning = false; _glassHurried = false;
        GlassGroup.Abandon();
        _glassGoalDark = Look.Current.IsDark;
        _glassPalette.Reset(_glassGoalDark ? 0 : 1, Environment.TickCount64);
        QueueGlass();
    }

    private void GlassLayout(object? sender, EventArgs e) => QueueGlass();
    private void GlassLocation(object? sender, EventArgs e)
    {
        _glassGeneration++;
        GlassBurst();
        QueueGlass();
    }
    private void GlassForeground() => GlassBurst();
    private double GlassOpenness()
    {
        double compact = Collapsed.Surface.Height, height = Island.ActualHeight;
        return _glassOpenHeight > compact + 1 && double.IsFinite(height)
            ? Math.Clamp((height - compact) / (_glassOpenHeight - compact), 0, 1) : _expanded ? 1 : 0;
    }
    private void GlassVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) HideGlass();
        else QueueGlass();
    }

    private void QueueGlass()
    {
        if (_glassSurface is null || _glassQueued || !IsVisible) return;
        _glassQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _glassQueued = false;
            ShowGlass();
        });
    }

    private void ShowGlass()
    {
        if (_glassSurface is null) return;
        if (!IsVisible || !Island.IsVisible || !HoverZone.IsVisible || HoverZone.Opacity < .005
            || Island.MaterialOpacity < .005 || WindowState == WindowState.Minimized
            || PresentationSource.FromVisual(Island) is null || Island.RenderSize is not { Width: > 1, Height: > 1 } size)
        {
            HideGlass();
            return;
        }
        var origin = Island.PointToScreen(new Point());
        var along = Island.PointToScreen(new Point(size.Width, 0));
        var depth = Island.PointToScreen(new Point(0, size.Height));
        var far = Island.PointToScreen(new Point(size.Width, size.Height));
        int left = (int)Math.Floor(Math.Min(Math.Min(origin.X, along.X), Math.Min(depth.X, far.X)));
        int top = (int)Math.Floor(Math.Min(Math.Min(origin.Y, along.Y), Math.Min(depth.Y, far.Y)));
        int right = (int)Math.Ceiling(Math.Max(Math.Max(origin.X, along.X), Math.Max(depth.X, far.X)));
        int bottom = (int)Math.Ceiling(Math.Max(Math.Max(origin.Y, along.Y), Math.Max(depth.Y, far.Y)));
        _glassBounds = new Int32Rect(left, top, right - left, bottom - top);
        // A rounded outline is the glass layer's own clip (no region); one with shoulders is a contour, built from the outline only
        // for a new shape and placed anew only for a new fraction of a pixel, so a move by whole pixels changes neither.
        var corners = Island.CornerRadius;
        var layout = GlassBoundaryMath.Measure(size.Width, size.Height, Island.ShoulderRadius, corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft, 0);
        (double X, double Y) at = (origin.X - left, origin.Y - top);
        GlassBoundaryMath.GlassClip? clip = GlassBoundaryMath.TryRoundedClip(layout, at, (along.X - left, along.Y - top), (depth.X - left, depth.Y - top), out var rounded)
            ? rounded : null;
        if (clip is null)
        {
            var shape = (size, Island.ShoulderRadius, corners, along - origin, depth - origin);
            if (_glassOutline != shape || _glassOutlinePoints is null)
            {
                _glassOutline = shape;
                // Flattened to about a seventh of a pixel.
                double perDip = (along - origin).Length / size.Width;
                var flat = IslandBorder.BuildOutline(size.Width, size.Height, shape.ShoulderRadius, corners, 0).GetFlattenedPathGeometry(.15 / perDip, ToleranceType.Absolute);
                var local = new List<Point>();
                foreach (var figure in flat.Figures)
                {
                    local.Add(figure.StartPoint);
                    foreach (var segment in figure.Segments)
                        if (segment is PolyLineSegment lines) local.AddRange(lines.Points);
                        else if (segment is LineSegment line) local.Add(line.Point);
                }
                _glassOutlinePoints = [.. local];
                _glassContour = null;
            }
            if (_glassContour is null || _glassContourAt != at)
            {
                _glassContourAt = at;
                var points = _glassOutlinePoints;
                Vector u = (along - origin) / size.Width, v = (depth - origin) / size.Height;
                var contour = new GlassSurface.PixelPoint[points.Length];
                for (int i = 0; i < points.Length; i++)
                    contour[i] = new((int)Math.Round(at.X + points[i].X * u.X + points[i].Y * v.X), (int)Math.Round(at.Y + points[i].X * u.Y + points[i].Y * v.Y));
                _glassContour = contour.Length >= 3 ? contour : null;
            }
            if (_glassContour is null)
            {
                HideGlass();
                return;
            }
        }
        long now = Environment.TickCount64;
        bool dark = Look.Current.IsDark;
        if (!_glassMaterialReady)
        {
            // Shown anew: the current palette at rest, both looks at the floor for the brightest backdrop until a reading comes.
            _glassMaterialReady = true;
            _glassGoalDark = dark; _glassTurning = false;
            _glassPalette.Reset(dark ? 0 : 1, now);
            _glassRequiredDark = GlassRecipe.LuminosityFor(true, 1, 0, Settings.GlassTransparency);
            _glassRequiredLight = GlassRecipe.LuminosityFor(false, 1, 0, Settings.GlassTransparency);
            _glassWantedDark = _glassWantedLight = double.NaN;
            _glassFirstSample = true;
            _glassValidity.Restart(now);
            _glassProtected = false;
            _glassTails = (1, 0);
            _glassGeneration++;
            GlassTargets(now, snap: true);
        }
        else if (!_glassTurning && dark != _glassGoalDark)
        {
            // Turned from outside (another theme, the Windows mode for a fixed look): no turn of ours to continue.
            _glassGoalDark = dark;
            _glassPalette.Reset(dark ? 0 : 1, now);
        }
        bool expanded = _expanded;
        if (expanded != _glassWasExpanded)
        {
            _glassWasExpanded = expanded;
            // An opening or closing SetExpanded did not see (a new foreground context closes the panel directly).
            if (expanded != _glassOpening.IsOpen) GlassOpenChanged(expanded);
            GlassBurst();
        }
        GlassTargets(now, snap: !Animates);
        var material = GlassMaterialNow(now);
        var owner = new WindowInteropHelper(this).Handle;
        if (!_glassSurface.Show(owner, _glassBounds, clip, clip is null ? _glassContour : null, HoverZone.Opacity * Island.MaterialOpacity, material, out var applied))
        {
            // Retry on a later settings or environment update instead of every layout pass.
            CloseGlass();
            return;
        }
        if (!_glassActive) { _glassActive = true; ApplySurface(); }
        GlassTurnStep(applied.Material, now);
        GlassRenderingNeeded();
        ApplyGlassOptics();
        if (_glassProbe is { IsEnabled: false } probe)
        {
            probe.Start();
            SampleGlass(this, EventArgs.Empty);
        }
    }

    // Each look's veil heads for what the backdrop asks for at the opening shown now (with the covered area's share growing with
    // the opening, GlassOpening); a target that moved retargets its spring from where it is (faster when thickening). A veil at
    // rest whose target moved only with the opening follows the opening shown exactly, frame by frame, instead of a spring
    // catching up after it. Snap: at rest on the target at once (reduced motion, a first frame).
    private void GlassTargets(long now, bool snap)
    {
        int transparency = Settings.GlassTransparency;
        double openness = GlassOpenness();
        if (!_expanded && openness <= 0) _glassCoverDark = _glassCoverLight = double.NaN;
        bool opening = openness != _glassVeilOpenness;
        _glassVeilOpenness = openness;
        Head(ref _glassDarkVeil, GlassMaterial.LuminosityOpacity(GlassOpening.Required(_glassRequiredDark, _glassCoverDark, openness), openness, transparency));
        Head(ref _glassLightVeil, GlassMaterial.LuminosityOpacity(GlassOpening.Required(_glassRequiredLight, _glassCoverLight, openness), openness, transparency));
        if (snap) _glassPalette.Reset(_glassGoalDark ? 0 : 1, now);

        void Head(ref FluidSpring spring, double target)
        {
            if (snap || opening && !spring.Moving) { spring.Reset(target, now); return; }
            if (Math.Abs(spring.Target - target) < 1e-6) return;
            double current = spring.Evaluate(now).Position;
            spring.Retarget(target, now, target > current ? GlassVeilAttack : GlassVeilRelease);
        }
    }

    private GlassSurface.Material GlassMaterialNow(long now)
        => new(_glassDarkVeil.Sample(now), _glassLightVeil.Sample(now), _glassPalette.Sample(now),
            GlassClarityPolicy.Tint(Settings.GlassTransparency), GlassClarityPolicy.Saturation(Settings.GlassTransparency));

    // The display's frames while a channel moves: the springs' values to the graph, the text swap checked; then it stops.
    private void GlassRenderingNeeded()
    {
        bool moving = _glassActive && (_glassDarkVeil.Moving || _glassLightVeil.Moving || _glassPalette.Moving);
        if (moving == _glassRendering) return;
        _glassRendering = moving;
        if (moving) CompositionTarget.Rendering += GlassFrame;
        else CompositionTarget.Rendering -= GlassFrame;
    }

    private void GlassFrame(object? sender, EventArgs e)
    {
        if (_glassSurface is null || !_glassActive) { GlassRenderingNeeded(); return; }
        long now = Environment.TickCount64;
        var material = GlassMaterialNow(now);
        if (!_glassSurface.Present(material, out var applied)) { CloseGlass(); return; }
        GlassTurnStep(applied.Material, now);
        GlassRenderingNeeded();
    }

    // A turn's text swap, by the leading panel only and only for the group's current epoch (the panels of a shared look follow
    // its published palette; a follower whose palette arrives first waits for it), and its end.
    private void GlassTurnStep(GlassSurface.Material material, long now)
    {
        if (!_glassTurning) return;
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) { EndGlassTurn(); return; }
        var group = GlassGroup;
        bool showingDark = Look.Current.IsDark, arrived = !_glassPalette.Moving;
        bool leads = ReferenceEquals(group.Leader, this) && _glassTurnEpoch == group.Epoch;
        var amounts = GlassRecipe.Coefficients(material.DarkVeil, material.LightVeil, material.Palette, material.Tint, material.Saturation);
        if (!arrived && !_glassHurried && GlassReadability.Worst(showingDark, amounts, _glassTails.Bright, _glassTails.Dark) < GlassReadable)
        {
            _glassHurried = true;
            _glassPalette.Retarget(_glassPalette.Target, now, GlassPaletteHurry);
        }
        // The leader swaps at the readable frame (at the latest when its palette arrived); a leader hidden mid-way hands the turn
        // to the next panel (HideGlass elects it), which publishes the same epoch's goal.
        if (leads && showingDark != _glassGoalDark && (!Animates || arrived || GlassReadability.SwapNow(showingDark, _glassGoalDark,
                amounts, _glassTails.Bright, _glassTails.Dark))
            && group.TryPublish(this, _glassTurnEpoch, _glassGoalDark))
            PublishGlassPalette(_glassGoalDark, coordinated: true);
        if (!arrived) return;
        if (Look.Current.IsDark == _glassGoalDark)
        {
            _glassTurning = false;
            if (leads) group.Decision?.Arrived(now);
        }
        // A turn of an epoch the group has left (reversed, abandoned) ends where it is; ShowGlass sets the palette to the look shown.
        else if (_glassTurnEpoch != group.Epoch) { _glassTurning = false; QueueGlass(); }
    }

    // A hint that the backdrop may have changed: read at once (unless a reading runs or one was taken within a burst's interval)
    // and keep reading quickly for a while. Soft hints start a burst at most every GlassHintGap.
    private void GlassBurst(bool soft = false)
    {
        if (_glassProbe is null) return;
        long now = Environment.TickCount64;
        if (soft)
        {
            if (now - _glassSoftBurstAt < GlassHintGap) return;
            _glassSoftBurstAt = now;
        }
        _glassBurstUntil = now + GlassBurstMs;
        if (_glassProbe.Interval.TotalMilliseconds != GlassBurstInterval) _glassProbe.Interval = TimeSpan.FromMilliseconds(GlassBurstInterval);
        if (_glassActive && !_glassValidity.Reading && now - _glassReadAt >= GlassBurstInterval) SampleGlass(this, EventArgs.Empty);
    }

    private void GlassWatchdogTick(object? sender, EventArgs e) => GlassWatch(Environment.TickCount64);

    // The watchdog (U05): however the readings fail (none starts, none answers, each is late or for a place the panel has left),
    // the tails the glass answers for widen toward the worst range (1 and 0) as GlassSampleValidity's protection rises, the veils
    // thicken for them at once, the decision drops its candidate, and only fresh readings that agree thin them again. Then it
    // sets itself for the next deadline.
    private void GlassWatch(long now)
    {
        if (_glassWatchdog is not { } watchdog) return;
        watchdog.Stop();
        if (!_glassActive) return;
        if (_glassValidity.Protection(now) > 0 || _glassValidity.Protecting)
        {
            if (!_glassProtected)
            {
                _glassProtected = true;
                _glassWantedDark = _glassWantedLight = double.NaN;
                if (ReferenceEquals(GlassGroup.Leader, this)) GlassGroup.Decision?.Invalidate();
            }
            var safe = _glassValidity.SafeTails(now);
            _glassTails = (Math.Max(_glassTails.Bright, safe.Bright), Math.Min(_glassTails.Dark, safe.Dark));
            int transparency = Settings.GlassTransparency;
            double dark = GlassRecipe.LuminosityFor(true, _glassTails.Bright, _glassTails.Dark, transparency);
            double light = GlassRecipe.LuminosityFor(false, _glassTails.Bright, _glassTails.Dark, transparency);
            if (dark > _glassRequiredDark || light > _glassRequiredLight)
            {
                _glassRequiredDark = Math.Max(_glassRequiredDark, dark);
                _glassRequiredLight = Math.Max(_glassRequiredLight, light);
                QueueGlass();
            }
        }
        else _glassProtected = false;
        long next = _glassValidity.NextDeadline(now);
        if (next == long.MaxValue) return;
        watchdog.Interval = TimeSpan.FromMilliseconds(Math.Max(1, next - now));
        watchdog.Start();
    }

    private GlassSampleArea? CurrentGlassSampleArea()
    {
        var monitorHandle = MonitorHandle;
        return WindowsHost.MonitorScreen(monitorHandle) is { Bounds: var screen }
            ? new(monitorHandle, screen, _glassBounds, VisualTreeHelper.GetDpi(Island).DpiScaleX, Look)
            : null;
    }

    private async void SampleGlass(object? sender, EventArgs e)
    {
        // The reading's continuation and everything it starts (the springs' frames, a turn) belong on this window's thread.
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => SampleGlass(sender, e)); return; }
        long start = Environment.TickCount64;
        bool bursting = start < _glassBurstUntil;
        if (_glassProbe is { } timer)
        {
            int interval = bursting ? GlassBurstInterval : GlassProbeInterval;
            if (timer.Interval.TotalMilliseconds != interval) timer.Interval = TimeSpan.FromMilliseconds(interval);
        }
        var validity = _glassValidity;
        validity.Mode(bursting, start);
        GlassWatch(start);
        if (!_glassActive || CurrentGlassSampleArea() is not { } area) return;
        int abandoned = validity.Abandoned;
        if (validity.Begin(start, _glassGeneration) is not { } ticket) return;
        if (validity.Abandoned != abandoned)
        {
            // The capture given up still holds its probe on its worker: the next ones read with a new one.
            _acrylicProbe.Dispose();
            _acrylicProbe = new AcrylicProbe();
        }
        if (!GlassReads.TryEnter(this, ticket.Id)) { validity.Cancel(ticket); return; }
        _glassReadAt = start;
        var monitor = area.Screen;
        var screen = new Int32Rect((int)monitor.X, (int)monitor.Y, (int)monitor.Width, (int)monitor.Height);
        var probe = _acrylicProbe;
        AcrylicProbe.Reading? reading;
        try { reading = await Task.Run(() => probe.Read(area.Bounds, screen, (int)(24 * area.Scale), (int)(40 * area.Scale))); }
        finally { GlassReads.Leave(this, ticket.Id); }
        if (!_glassActive || !ReferenceEquals(validity, _glassValidity)) return;
        long now = Environment.TickCount64;
        int transparency = Settings.GlassTransparency;
        double openness = GlassOpenness();
        (double, double)? tails = reading is { } read ? (read.BrightFor(openness), read.DarkFor(openness)) : null;
        bool first = _glassFirstSample;
        var verdict = validity.Complete(ticket, now, _glassGeneration, area == CurrentGlassSampleArea(), tails);
        GlassWatch(now);
        if (verdict is not (GlassSampleValidity.Verdict.Accepted or GlassSampleValidity.Verdict.Recovering) || reading is not { } sample) return;
        // Recovering (the first fresh reading after the protection rose): the glass may thicken for it, not thin yet.
        bool recovering = verdict == GlassSampleValidity.Verdict.Recovering;
        _glassTails = recovering ? (Math.Max(_glassTails.Bright, tails!.Value.Item1), Math.Min(_glassTails.Dark, tails.Value.Item2)) : tails!.Value;
        // A video's frames change most pixels every time and are no new scene: only the middle's brightness moving a lot hints at
        // one, and not while the backdrop is a video.
        if (double.IsFinite(_glassLastMiddle) && Math.Abs(sample.MiddleMean - _glassLastMiddle) > .12 && GlassGroup.Decision?.Video != true) GlassBurst(soft: true);
        _glassLastMiddle = sample.MiddleMean;
        _glassLightTarget = GlassLight.Direction(sample.Sides, _glassLight);
        GlassOpticsNeeded();
        double wantedDark = GlassRecipe.LuminosityFor(true, tails!.Value.Item1, tails.Value.Item2, transparency);
        double wantedLight = GlassRecipe.LuminosityFor(false, tails.Value.Item1, tails.Value.Item2, transparency);
        double dark = first ? wantedDark : recovering ? Math.Max(_glassRequiredDark, wantedDark) : GlassMaterial.Settle(wantedDark, _glassWantedDark, _glassRequiredDark);
        double light = first ? wantedLight : recovering ? Math.Max(_glassRequiredLight, wantedLight) : GlassMaterial.Settle(wantedLight, _glassWantedLight, _glassRequiredLight);
        _glassFirstSample = false;
        _glassWantedDark = wantedDark; _glassWantedLight = wantedLight;
        bool changed = dark != _glassRequiredDark || light != _glassRequiredLight;
        _glassRequiredDark = dark; _glassRequiredLight = light;
        if (!recovering) UpdateAutomaticGlass(sample, now);
        if (changed) QueueGlass();
    }

    // The leader of this panel's shared look among the panels shown now (GlassLookGroup.Elect).
    private MainWindow? ElectGlassLeader()
    {
        var look = Look;
        return GlassGroup.Elect(_app.Decks.Where(deck => ReferenceEquals(deck.Look, look)).Select(deck => new GlassLookGroup<MainWindow>.Member(deck,
            deck._glassActive && deck.IsVisible && PanelThemes.NormalizeId(deck.Settings.PanelTheme) == PanelThemes.GlassAuto, deck.Animates)).ToList());
    }

    private void UpdateAutomaticGlass(AcrylicProbe.Reading sample, long now)
    {
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) return;
        var group = GlassGroup;
        if (!ReferenceEquals(ElectGlassLeader(), this)) return;
        bool appDark = SettingsPalette.IsDark(SettingsTheme.System);
        // One decision per shared look, kept across leaders; a palette set from outside (no turn of ours under way) resets it to
        // the look shown, with its measures' history.
        if (group.Decision is not { } decision) decision = group.Decision = new GlassAppearance(Look.Current.IsDark, appDark, videoGap: 2 * GlassProbeInterval);
        else if (decision.StateAt(now) != GlassTurnState.InFlight && decision.Dark != Look.Current.IsDark)
        {
            decision.Reset(Look.Current.IsDark, now);
            group.Abandon();
        }
        decision.Prior(appDark);
        decision.Changed(sample.Changed, now);
        bool held = _expanded || _app.Decks.Any(deck => ReferenceEquals(deck.Look, Look) && (deck._expanded
            || deck._glassActive && deck.GlassOpenness() > .001));
        if (!decision.Feed(sample.MiddleMean, now, held: held)) return;
        StartGlassTurn(decision, now);
    }

    // The decision turned (or reversed): a new epoch for the shared look; the panels that animate head their palettes for it and
    // the leader publishes it at the readable frame; a leader that does not animate (or shows no glass) publishes it at once.
    // At once (an opening's own turn while the panel is mostly shut, GlassOpening.TurnsAtOnce): the panels that animate take the
    // new palette at rest now and the leader publishes it at once, as at a readable frame (the opening's motion goes on), so the
    // opening grows in the new look from the next frame.
    private void StartGlassTurn(GlassAppearance decision, long now, bool atOnce = false)
    {
        var group = GlassGroup;
        bool dark = decision.Dark;
        long epoch = group.Begin(dark);
        var leader = group.Leader ?? this;
        if (!leader.Animates || leader._glassSurface?.Visible != true)
        {
            leader._glassGoalDark = dark; leader._glassTurnEpoch = epoch;
            if (group.TryPublish(leader, epoch, dark)) leader.PublishGlassPalette(dark, coordinated: false);
            decision.Arrived(now);
            return;
        }
        foreach (var deck in _app.Decks.ToList())
        {
            if (!ReferenceEquals(deck.Look, Look) || PanelThemes.NormalizeId(deck.Settings.PanelTheme) != PanelThemes.GlassAuto
                || !deck.Animates || deck._glassSurface?.Visible != true) continue;
            deck.BeginGlassPaletteTurn(dark, now, epoch, atOnce);
        }
        // The turn ends on the panels' next pass (GlassTurnStep: arrived, the look shown is the goal).
        if (atOnce && group.TryPublish(leader, epoch, dark)) leader.PublishGlassPalette(dark, coordinated: true);
    }

    // The panel begins to open or close (SetExpanded, as the intent comes: before the opening's first frame). Opening: a new
    // epoch of the panel's GlassOpeningSession and a read of the desktop the panel is about to cover, so the veils reach what
    // that area asks for in step with the opening and a clearly light or dark area turns the shared look with it. Closing: the
    // opening's cover and any read still running for it are void at once (V308), whatever the closing motion still shows.
    private void GlassOpenChanged(bool open)
    {
        if (!open)
        {
            _glassOpening.Close();
            _glassCoverCancel?.Cancel();
            _glassCoverDark = _glassCoverLight = double.NaN;
            if (_glassActive) QueueGlass();
            return;
        }
        _glassOpening.Intent();
        _glassCoverDark = _glassCoverLight = double.NaN;
        _glassIntentAt = Environment.TickCount64;
        _glassIntentStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _glassIntentFrameAt = long.MaxValue;
        CompositionTarget.Rendering -= GlassIntentFrame;
        CompositionTarget.Rendering += GlassIntentFrame;
        ReadGlassCover();
    }

    private void GlassIntentFrame(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= GlassIntentFrame;
        if (_glassIntentFrameAt == long.MaxValue) _glassIntentFrameAt = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    // One read of the area for the opening under way (at most one runs; one asked for meanwhile follows it, the latest). A read
    // for an opening that has ended, or for another display or scale, is dropped with its reason; one that read nothing (refused
    // by the app's read budget, the probe busy, the area off the display) is Unknown: the veils keep what the capsule asks for,
    // never the worst range (V304). A read that began after the opening's first frame may show the panel's own glass: its grid
    // is trusted less (GlassSceneSnapshot.Occluded).
    private async void ReadGlassCover()
    {
        // Always on the window's thread: a pending read restarts from a continuation, which must not run elsewhere.
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(ReadGlassCover); return; }
        if (!_glassActive || CurrentGlassSampleArea() is not { } area || GlassCoveredArea() is not { } covered) return;
        if (_glassOpening.Begin(area.Monitor, covered.X, covered.Y, covered.Width, covered.Height, area.Scale, Environment.TickCount64) is not { } ticket) return;
        var monitor = area.Screen;
        var screen = new Int32Rect((int)monitor.X, (int)monitor.Y, (int)monitor.Width, (int)monitor.Height);
        var probe = _glassCoverProbe ??= new AcrylicProbe();
        _glassCoverCancel?.Dispose();
        var cancel = _glassCoverCancel = new CancellationTokenSource();
        var token = cancel.Token;
        bool refused = !GlassReads.TryEnter(_glassCoverKey, ticket.OpenEpoch);
        AcrylicProbe.Reading? reading = null;
        if (!refused)
        {
            try { reading = await Task.Run(() => probe.ReadArea(covered, screen, token)); }
            finally { GlassReads.Leave(_glassCoverKey, ticket.OpenEpoch); }
        }
        long now = Environment.TickCount64;
        var current = CurrentGlassSampleArea();
        var outcome = _glassOpening.Finish(ticket, current?.Monitor ?? 0, current?.Scale ?? double.NaN, reading is not null, refused);
        bool occluded = reading is { } read && read.Started > _glassIntentFrameAt;
        var scene = reading?.Scene is { } grid ? occluded ? grid.Occluded() : grid : null;
        GlassCoverTrace?.Invoke(FormattableString.Invariant(
            $"cover epoch={ticket.OpenEpoch} outcome={outcome} frame={(_glassIntentFrameAt == long.MaxValue ? -1 : StampMs(_glassIntentFrameAt - _glassIntentStamp))} gdi={(reading is { } g ? StampMs(g.Started - _glassIntentStamp) : -1)} done={now - _glassIntentAt} area={covered.X},{covered.Y},{covered.Width}x{covered.Height} middle={reading?.MiddleMean ?? double.NaN:0.000} bright={reading?.Bright ?? double.NaN:0.000} dark={reading?.Dark ?? double.NaN:0.000} {scene}"));
        if (outcome == GlassOpeningSession.Outcome.Applied && reading is { } sample && _glassActive) ApplyGlassCover(sample, scene, now);
        if (_glassOpening.TakePending()) ReadGlassCover();
    }

    private static long StampMs(long ticks) => ticks * 1000 / System.Diagnostics.Stopwatch.Frequency;

    private void ApplyGlassCover(AcrylicProbe.Reading sample, GlassSceneSnapshot? scene, long now)
    {
        int transparency = Settings.GlassTransparency;
        _glassCoverDark = GlassRecipe.LuminosityFor(true, sample.Bright, sample.Dark, transparency);
        _glassCoverLight = GlassRecipe.LuminosityFor(false, sample.Bright, sample.Dark, transparency);
        QueueGlass();
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) return;
        // Only the shared look's leader turns it (V314/V317): a follower opening over another backdrop keeps the shared look and
        // answers it with its veils. A leader without a decision yet (opened before its first ring reading) makes it now.
        if (!ReferenceEquals(ElectGlassLeader(), this)) { GlassCoverTrace?.Invoke("cover decision=follower"); return; }
        var decision = GlassGroup.Decision ??= new GlassAppearance(Look.Current.IsDark, SettingsPalette.IsDark(SettingsTheme.System), videoGap: 2 * GlassProbeInterval);
        bool turned = scene is not null ? decision.Open(scene, now) : decision.Open(sample.MiddleMean, now);
        GlassCoverTrace?.Invoke($"cover decision={(turned ? decision.Dark ? "turn-dark" : "turn-light" : "kept")} dark={decision.Dark}");
        if (turned) StartGlassTurn(decision, now, atOnce: GlassOpening.TurnsAtOnce(GlassOpenness(), Animates));
    }

    // The screen pixels the open panel will cover beyond the island shown now (a margin past it, as the ring keeps from its
    // shadow): the open panel's height away from the edge, its width along it (the island's own on a side edge, where the open
    // width is not kept), centred on the island; null when nothing of it can be told.
    private Int32Rect? GlassCoveredArea()
    {
        if (Island.RenderSize is not { Width: > 1, Height: > 1 } size || PresentationSource.FromVisual(Island) is null) return null;
        double open = _glassOpenHeight, from = size.Height + 24;
        if (!double.IsFinite(open) || open - from < 8) return null;
        double width = _frame.Across ? size.Width : Math.Max(size.Width, _pageWidth * _scale), left = (size.Width - width) / 2;
        Point[] corners = [new(left, from), new(left + width, from), new(left, open), new(left + width, open)];
        var screen = corners.Select(Island.PointToScreen).ToArray();
        int x = (int)Math.Floor(screen.Min(p => p.X)), y = (int)Math.Floor(screen.Min(p => p.Y));
        var rect = new Int32Rect(x, y, (int)Math.Ceiling(screen.Max(p => p.X)) - x, (int)Math.Ceiling(screen.Max(p => p.Y)) - y);
        return WindowsHost.MonitorScreen(MonitorHandle) is { Bounds: var monitor }
            && AcrylicProbe.Clip(rect, new Int32Rect((int)monitor.X, (int)monitor.Y, (int)monitor.Width, (int)monitor.Height)) is { IsEmpty: false } clipped
            ? clipped : null;
    }

    // Heads the palette for dark or light from wherever it is now (a reversal mid-way included), for the group's epoch; at once:
    // at rest there now. The veils of both looks are already following the backdrop, so the goal's branch is ready.
    private void BeginGlassPaletteTurn(bool dark, long now, long epoch, bool atOnce = false)
    {
        _glassGoalDark = dark;
        _glassTurnEpoch = epoch;
        _glassTurning = true;
        _glassHurried = false;
        if (atOnce) _glassPalette.Reset(dark ? 0 : 1, now);
        else _glassPalette.Retarget(dark ? 0 : 1, now, GlassPaletteOmega);
        GlassRenderingNeeded();
        QueueGlass();
    }

    // The optical edge (GlassEdge) for the light's direction, the opening and the palette shown now. The edge's brushes are moved
    // in place, so this runs on any frame without a new brush; the island draws anew only when the rings change (a new width).
    private void ApplyGlassOptics()
    {
        if (!_glassActive) return;
        double openness = GlassOpenness();
        double strength = Math.Min(1, GlassMaterial.RimStrength(Settings.GlassTransparency, openness));
        var (peak, baseline) = GlassBoundaryMath.Light(_glassPalette.Evaluate(Environment.TickCount64).Position);
        var edge = _glassEdge ??= new GlassEdge();
        var stops = Look.Current.Effects.RimGradient;
        if (!ReferenceEquals(_glassEdgeStops, stops))
        {
            _glassEdgeStops = stops;
            edge.Stops([.. stops.Select(PanelTheme.ToColor)]);
        }
        var (start, end) = GlassLight.Gradient(_glassLight);
        edge.Light(start, end);
        bool rings = edge.Shape(GlassBoundaryMath.BevelWidth(openness), 1 / VisualTreeHelper.GetDpi(Island).PixelsPerDip, peak, baseline, strength);
        if (!ReferenceEquals(Island.Edge, edge)) Island.Edge = edge;
        else if (rings) Island.InvalidateVisual();
    }

    // The light turns toward the latest reading's direction on the display's frames (GlassLight.Follow by the time passed), and the
    // edge follows a palette turn as it goes; neither waits for the next reading, and nothing runs once both are at rest.
    private void GlassOpticsNeeded()
    {
        bool moving = _glassActive && (_glassPalette.Moving
            || Math.Abs(Math.IEEERemainder(Math.Atan2(_glassLight.Y, _glassLight.X) - Math.Atan2(_glassLightTarget.Y, _glassLightTarget.X), 2 * Math.PI)) > .002);
        if (moving == _glassOpticsRendering) return;
        _glassOpticsRendering = moving;
        if (moving)
        {
            _glassLightAt = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
            CompositionTarget.Rendering += GlassOpticsFrame;
        }
        else CompositionTarget.Rendering -= GlassOpticsFrame;
    }

    private void GlassOpticsFrame(object? sender, EventArgs e)
    {
        if (_glassActive)
        {
            double now = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
            _glassLight = GlassLight.Follow(_glassLight, _glassLightTarget, now - _glassLightAt);
            _glassLightAt = now;
            ApplyGlassOptics();
        }
        GlassOpticsNeeded();
    }

    private void PublishGlassPalette(bool dark, bool coordinated)
    {
        var look = Look;
        look.AutomaticGlassDark = dark;
        look.GlassTurnPublishing = coordinated;
        try { _app.ApplyPanelTheme(); }
        finally { look.GlassTurnPublishing = false; }
    }

    private void HideGlass()
    {
        _glassProbe?.Stop();
        _glassWatchdog?.Stop();
        _glassSurface?.Hide();
        if (!_glassActive) return;
        _glassActive = false;
        GlassRenderingNeeded();
        _glassMaterialReady = false;
        _glassTurning = false;
        // A hidden leader hands its look's turn and decision over (U08); the heir looks at its turn on its next pass.
        if (ElectGlassLeader() is { } heir && !ReferenceEquals(heir, this)) heir.QueueGlass();
        GlassReads.Drop(this);
        // A worker still reading keeps the old probe until it answers (then it is released); late answers are dropped.
        _acrylicProbe.Dispose();
        _acrylicProbe = new AcrylicProbe();
        _glassCoverProbe?.Dispose();
        _glassCoverProbe = null;
        GlassReads.Drop(_glassCoverKey);
        _glassCoverCancel?.Cancel();
        _glassOpening.SceneChanged();
        _glassCoverDark = _glassCoverLight = _glassVeilOpenness = double.NaN;
        _glassValidity = new GlassSampleValidity(Environment.TickCount64);
        _glassLastMiddle = double.NaN;
        _glassGeneration++;
        _glassLight = _glassLightTarget = GlassLight.Default;
        GlassOpticsNeeded();
        Island.Edge = null;
        ApplySurface();
    }

    private void CloseGlass()
    {
        if (_glassSurface is null) return;
        LayoutUpdated -= GlassLayout;
        LocationChanged -= GlassLocation;
        IsVisibleChanged -= GlassVisibility;
        _host.ForegroundSwitched -= GlassForeground;
        if (_glassTitleHint is not null) GlassHintBroker.Unsubscribe(_glassTitleHint);
        HideGlass();
        _glassActive = false;
        GlassRenderingNeeded();
        _glassSurface.Dispose();
        _glassSurface = null;
        _glassProbe = null;
        if (_glassWatchdog is not null) { _glassWatchdog.Stop(); _glassWatchdog.Tick -= GlassWatchdogTick; _glassWatchdog = null; }
        _glassOutline = null;
        _glassOutlinePoints = null;
        _glassContour = null;
        _glassMaterialReady = false;
    }
}
