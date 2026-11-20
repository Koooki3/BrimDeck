using System.Runtime.CompilerServices;
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
    private static readonly GlassReadBudget<MainWindow> GlassReads = new();
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
    private (double Bright, double Dark) _glassTails = (1, 0);
    private readonly record struct GlassSampleArea(nint Monitor, Rect Screen, Int32Rect Bounds, double Scale, PanelLook Look);
    private (double X, double Y) _glassLight = GlassLight.Default;
    private long _glassLightAt;
    private (double X, double Y, double Rim, double Width, bool Dark)? _glassOptics;
    private (Size Size, double Shoulder, CornerRadius Corners, Point Origin, Point Along, Point Depth)? _glassOutline;
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
        // The theme was applied again (Dress set the rim back to the theme's own): the optics are drawn anew.
        _glassOptics = null;
        bool enabled = Look.Current.Effects.Glass is not null && !Look.HighContrast && GlassAvailable && !_retiring;
        if (enabled == (_glassSurface is not null)) { QueueGlass(); return; }
        if (!enabled) { CloseGlass(); return; }
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
        var shape = (size, Island.ShoulderRadius, Island.CornerRadius, origin, along, depth);
        if (_glassOutline != shape)
        {
            _glassOutline = shape;
            var outline = IslandBorder.BuildOutline(size.Width, size.Height, shape.ShoulderRadius, shape.CornerRadius, 0).GetFlattenedPathGeometry(.25, ToleranceType.Absolute);
            var points = new List<GlassSurface.PixelPoint>();
            void Add(Point point)
            {
                var pixel = Island.PointToScreen(point);
                points.Add(new GlassSurface.PixelPoint((int)Math.Round(pixel.X - left), (int)Math.Round(pixel.Y - top)));
            }
            foreach (var figure in outline.Figures)
            {
                Add(figure.StartPoint);
                foreach (var segment in figure.Segments)
                    if (segment is PolyLineSegment lines) foreach (var point in lines.Points) Add(point);
                    else if (segment is LineSegment line) Add(line.Point);
            }
            _glassContour = points.Count >= 3 ? points.ToArray() : null;
        }
        if (_glassContour is null)
        {
            HideGlass();
            return;
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
            if (expanded) ReadGlassCover();
            GlassBurst();
        }
        GlassTargets(now, snap: !Animates);
        var material = GlassMaterialNow(now);
        var owner = new WindowInteropHelper(this).Handle;
        if (!_glassSurface.Show(owner, _glassBounds, 0, HoverZone.Opacity * Island.MaterialOpacity, material, _glassContour))
        {
            // Retry on a later settings or environment update instead of every layout pass.
            CloseGlass();
            return;
        }
        if (!_glassActive) { _glassActive = true; ApplySurface(); }
        GlassTurnStep(material, now);
        GlassRenderingNeeded();
        ApplyGlassOptics();
        Island.Pattern = AcrylicNoise.Brush(VisualTreeHelper.GetDpi(Island).DpiScaleX, GlassMaterial.GrainStrength(Settings.GlassTransparency));
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
        => new(_glassDarkVeil.Sample(now), _glassLightVeil.Sample(now), _glassPalette.Sample(now));

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
        if (!_glassSurface.Present(material)) { CloseGlass(); return; }
        GlassTurnStep(material, now);
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
        if (!arrived && !_glassHurried && GlassReadability.Worst(showingDark, material.DarkVeil, material.LightVeil, material.Palette,
                _glassTails.Bright, _glassTails.Dark) < GlassReadable)
        {
            _glassHurried = true;
            _glassPalette.Retarget(_glassPalette.Target, now, GlassPaletteHurry);
        }
        // The leader swaps at the readable frame (at the latest when its palette arrived); a leader hidden mid-way hands the turn
        // to the next panel (HideGlass elects it), which publishes the same epoch's goal.
        if (leads && showingDark != _glassGoalDark && (!Animates || arrived || GlassReadability.SwapNow(showingDark, _glassGoalDark,
                material.DarkVeil, material.LightVeil, material.Palette, _glassTails.Bright, _glassTails.Dark))
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
        _glassLight = GlassLight.Follow(_glassLight, GlassLight.Direction(sample.Sides, _glassLight), _glassLightAt == 0 ? 0 : now - _glassLightAt);
        _glassLightAt = now;
        ApplyGlassOptics();
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
    private void StartGlassTurn(GlassAppearance decision, long now)
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
            deck.BeginGlassPaletteTurn(dark, now, epoch);
        }
    }

    // The panel begins to open (user 11-21): read the desktop it is about to cover before it does, so the veils reach what that
    // area asks for in step with the opening (a failed read: the worst range), and a clearly light or dark area turns the shared
    // look with the opening, before the open panel's lock holds it. Where the area cannot be told (no larger open height known,
    // nothing of it on the display), the opening goes on as before.
    private async void ReadGlassCover()
    {
        if (!_glassActive || CurrentGlassSampleArea() is not { } area || GlassCoveredArea() is not { } covered) return;
        var monitor = area.Screen;
        var screen = new Int32Rect((int)monitor.X, (int)monitor.Y, (int)monitor.Width, (int)monitor.Height);
        var probe = _glassCoverProbe ??= new AcrylicProbe();
        var reading = await Task.Run(() => probe.ReadArea(covered, screen));
        if (!_glassActive || !_expanded) return;
        long now = Environment.TickCount64;
        int transparency = Settings.GlassTransparency;
        var (bright, dark) = reading is { } read ? (read.Bright, read.Dark) : (1d, 0d);
        _glassCoverDark = GlassRecipe.LuminosityFor(true, bright, dark, transparency);
        _glassCoverLight = GlassRecipe.LuminosityFor(false, bright, dark, transparency);
        QueueGlass();
        if (reading is not { } sample || PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) return;
        ElectGlassLeader();
        if (GlassGroup.Decision is { } decision && decision.Open(sample.MiddleMean, now)) StartGlassTurn(decision, now);
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

    // Heads the palette for dark or light from wherever it is now (a reversal mid-way included), for the group's epoch. The veils
    // of both looks are already following the backdrop, so the goal's branch is ready.
    private void BeginGlassPaletteTurn(bool dark, long now, long epoch)
    {
        _glassGoalDark = dark;
        _glassTurnEpoch = epoch;
        _glassTurning = true;
        _glassHurried = false;
        _glassPalette.Retarget(dark ? 0 : 1, now, GlassPaletteOmega);
        GlassRenderingNeeded();
        QueueGlass();
    }

    // The rim and the bevel glow for the light's direction, the opening and the look; rebuilt only when one of them moved enough to
    // see (the brushes are frozen): the light turns slowly, and an opening or closing passes in about ten steps of the rim and the
    // bevel, which are exact again at rest.
    private void ApplyGlassOptics()
    {
        if (!_glassActive) return;
        double openness = GlassOpenness();
        bool dark = Look.Current.IsDark, atRest = openness is <= 0 or >= 1;
        double rim = Math.Min(1, GlassMaterial.RimStrength(Settings.GlassTransparency, openness)), width = GlassLight.BevelWidth(openness);
        double rimStep = atRest ? .005 : .06, widthStep = atRest ? .01 : .35;
        if (_glassOptics is { } shown && shown.Dark == dark && Math.Abs(shown.Rim - rim) < rimStep && Math.Abs(shown.Width - width) < widthStep
            && Math.Abs(Math.IEEERemainder(Math.Atan2(shown.Y, shown.X) - Math.Atan2(_glassLight.Y, _glassLight.X), 2 * Math.PI)) < .03) return;
        _glassOptics = (_glassLight.X, _glassLight.Y, rim, width, dark);
        var (start, end) = GlassLight.Gradient(_glassLight);
        var stops = Look.Current.Effects.RimGradient;
        Brush rimBrush = stops.Count >= 2
            ? new LinearGradientBrush(new GradientStopCollection(stops.Select((stop, i) => new GradientStop(PanelTheme.ToColor(stop), (double)i / (stops.Count - 1)))),
                new Point(start.X, start.Y), new Point(end.X, end.Y))
            : Look.RimBrush();
        rimBrush = rimBrush.CloneCurrentValue();
        rimBrush.Opacity = rim;
        rimBrush.Freeze();
        Island.RimBrush = rimBrush;
        byte peak = dark ? (byte)26 : (byte)36;
        var glow = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(peak, 255, 255, 255), 0), new(Color.FromArgb((byte)(peak * .35), 255, 255, 255), .6), new(Color.FromArgb((byte)(peak * .15), 255, 255, 255), 1)
        }, new Point(start.X, start.Y), new Point(end.X, end.Y));
        glow.Freeze();
        Island.BevelGlow = glow;
        Island.BevelGlowWidth = width;
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
        _glassCoverDark = _glassCoverLight = _glassVeilOpenness = double.NaN;
        _glassValidity = new GlassSampleValidity(Environment.TickCount64);
        _glassLastMiddle = double.NaN;
        _glassGeneration++;
        _glassLight = GlassLight.Default; _glassLightAt = 0; _glassOptics = null;
        Island.RimBrush = Look.RimBrush();
        Island.BevelGlow = null;
        ApplySurface();
        if (AcrylicNoise.Is(Island.Pattern))
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Island.Pattern = Look.Texture(dpiScale: dpi) ?? Look.Scanlines(dpiScale: dpi);
        }
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
        _glassContour = null;
        _glassMaterialReady = false;
    }
}
