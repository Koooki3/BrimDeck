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
//     shown (GlassReadability.SwapNow); only the leading panel of a shared look publishes it.
// Optics: the rim's gradient and an inner bevel glow start on the brighter side of the desktop around the island (GlassLight,
// from the probe's four strips), turning there slowly; the bevel widens and the rim softens as the panel opens.
// The springs run on the display's frames (CompositionTarget.Rendering) only while one moves, and set the persistent graph's
// weights (GlassSurface.Present): nothing is compiled while the material moves. Sampling never stops for a turn. It runs
// twice a second at rest and in a short burst after a hint that the backdrop may have changed (another window came to the
// front, the window in front changed its title - a browser's tab or page -, the panel moved or began to open or close, a
// reading that changed a lot), one reading at a time.
public partial class MainWindow
{
    private const int GlassProbeInterval = 500, GlassBurstInterval = 80, GlassBurstMs = 700, GlassStaleMs = 1500;
    // Soft hints (a title change, a reading that moved a lot) start a burst at most this often, and one window's title changes
    // count once per GlassTitleGap: a player's progress, a download's percentage or a chat's unread count retitle every second.
    private const int GlassHintGap = 2000, GlassTitleGap = 3000;
    // Stiffness (rad/s): the veil thickens within 5 % in about 180 ms and thins in about 300 ms; a palette turn about 365 ms.
    private const double GlassVeilAttack = 26, GlassVeilRelease = 16, GlassPaletteOmega = 13;
    // A turn passes through greys where neither palette's text reaches 4.5:1; while it is there the palette's spring is stiffened
    // (from where it is, at its speed) so that stretch is short.
    private const double GlassPaletteHurry = 26, GlassReadable = 4.5;
    private AcrylicProbe _acrylicProbe = new();
    private GlassSurface? _glassSurface;
    private GlassAppearance? _glassAppearance;
    private PanelLook? _glassAppearanceLook;
    private DispatcherTimer? _glassProbe;
    private bool _glassActive, _glassQueued, _glassReading, _glassRendering;
    private long _glassGeneration;
    private Int32Rect _glassBounds;
    private double _glassOpenHeight;
    // The material's channels (GlassSurface.Material) and what they head for.
    private FluidSpring _glassDarkVeil, _glassLightVeil, _glassPalette;
    private bool _glassMaterialReady, _glassFirstSample, _glassTurning, _glassLeads, _glassGoalDark, _glassWasExpanded, _glassHurried;
    private double _glassRequiredDark, _glassRequiredLight, _glassWantedDark = double.NaN, _glassWantedLight = double.NaN;
    private long _glassSampledAt, _glassBurstUntil, _glassReadAt, _glassSoftBurstAt = long.MinValue / 2, _glassTitleAt = long.MinValue / 2;
    private IntPtr _glassTitleWindow;
    private double _glassLastMiddle = double.NaN;
    private (double Bright, double Dark) _glassTails = (1, 0);
    private (GlassSampleArea Area, long Generation, long At, AcrylicProbe.Reading Reading)? _glassLatestSample;
    private readonly record struct GlassSampleArea(nint Monitor, Rect Screen, Int32Rect Bounds, double Scale, PanelLook Look);
    private (double X, double Y) _glassLight = GlassLight.Default;
    private long _glassLightAt;
    private (double X, double Y, double Rim, double Width, bool Dark)? _glassOptics;
    private (Size Size, double Shoulder, CornerRadius Corners, Point Origin, Point Along, Point Depth)? _glassOutline;
    private GlassSurface.PixelPoint[]? _glassContour;

    // EVENT_OBJECT_NAMECHANGE out of context: a title change of the window in front (a browser's tab or page) hints at a new backdrop.
    private IntPtr _glassTitleHook;
    private WinEventProc? _glassTitleEvent;
    private bool _glassTitlePending;
    private delegate void WinEventProc(IntPtr hook, uint winEvent, IntPtr window, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();

    internal static bool GlassAvailable => PanelTheme.GlassAvailable;
    // A palette turn is under way: the palette still moves, or the text has not yet swapped to the goal.
    internal bool GlassPaletteTurning => _glassTurning;

    internal void UpdateGlassTheme()
    {
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) { _glassAppearance = null; EndGlassTurn(); }
        // The theme was applied again (Dress set the rim back to the theme's own): the optics are drawn anew.
        _glassOptics = null;
        bool enabled = Look.Current.Effects.Glass is not null && !Look.HighContrast && GlassAvailable && !_retiring;
        if (enabled == (_glassSurface is not null)) { QueueGlass(); return; }
        if (!enabled) { CloseGlass(); return; }
        _glassSurface = new GlassSurface();
        _glassProbe = new DispatcherTimer(TimeSpan.FromMilliseconds(GlassProbeInterval), DispatcherPriority.Background, SampleGlass, Dispatcher);
        _glassProbe.Stop();
        LayoutUpdated += GlassLayout;
        LocationChanged += GlassLocation;
        IsVisibleChanged += GlassVisibility;
        _host.ForegroundSwitched += GlassForeground;
        _glassTitleEvent ??= GlassTitle;
        _glassTitleHook = SetWinEventHook(0x800C, 0x800C, IntPtr.Zero, _glassTitleEvent, 0, 0, 0x0002); // OUTOFCONTEXT | SKIPOWNPROCESS
        QueueGlass();
    }

    private void GlassTitle(IntPtr hook, uint winEvent, IntPtr window, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || _glassTitlePending || !_glassActive || window != GetForegroundWindow()) return;
        long now = Environment.TickCount64;
        if (window == _glassTitleWindow && now - _glassTitleAt < GlassTitleGap) return;
        _glassTitleWindow = window; _glassTitleAt = now;
        _glassTitlePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _glassTitlePending = false; GlassBurst(soft: true); });
    }

    // A palette turn ends where the theme now is: a fixed glass chosen mid-way, or no automatic glass any more.
    private void EndGlassTurn()
    {
        if (!_glassTurning) return;
        _glassTurning = false; _glassHurried = false;
        _glassGoalDark = Look.Current.IsDark;
        _glassPalette.Reset(_glassGoalDark ? 0 : 1, Environment.TickCount64);
        QueueGlass();
    }

    private void GlassLayout(object? sender, EventArgs e) => QueueGlass();
    private void GlassLocation(object? sender, EventArgs e)
    {
        _glassLatestSample = null;
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
            _glassGoalDark = dark; _glassTurning = false; _glassLeads = false;
            _glassPalette.Reset(dark ? 0 : 1, now);
            _glassRequiredDark = GlassRecipe.LuminosityFor(true, 1, 0, Settings.GlassTransparency);
            _glassRequiredLight = GlassRecipe.LuminosityFor(false, 1, 0, Settings.GlassTransparency);
            _glassWantedDark = _glassWantedLight = double.NaN;
            _glassFirstSample = true;
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
        if (expanded != _glassWasExpanded) { _glassWasExpanded = expanded; GlassBurst(); }
        GlassTargets(now, snap: !Animates);
        var material = GlassMaterialNow(now);
        var owner = new WindowInteropHelper(this).Handle;
        if (!_glassSurface.Show(owner, _glassBounds, 0, HoverZone.Opacity * Island.MaterialOpacity, material, out var applied, _glassContour))
        {
            // Retry on a later settings or environment update instead of every layout pass.
            CloseGlass();
            return;
        }
        if (!_glassActive) { _glassActive = true; ApplySurface(); }
        GlassTurnStep(applied.Material, now);
        GlassRenderingNeeded();
        ApplyGlassOptics();
        Island.Pattern = AcrylicNoise.Brush(VisualTreeHelper.GetDpi(Island).DpiScaleX, GlassMaterial.GrainStrength(Settings.GlassTransparency));
        if (_glassProbe is { IsEnabled: false } probe)
        {
            probe.Start();
            SampleGlass(this, EventArgs.Empty);
        }
    }

    // Each look's veil heads for what the backdrop asks for at the opening shown now; a target that moved retargets its spring
    // from where it is (faster when thickening). Snap: at rest on the target at once (reduced motion, a first frame).
    private void GlassTargets(long now, bool snap)
    {
        int transparency = Settings.GlassTransparency;
        double openness = GlassOpenness();
        Head(ref _glassDarkVeil, GlassMaterial.LuminosityOpacity(_glassRequiredDark, openness, transparency));
        Head(ref _glassLightVeil, GlassMaterial.LuminosityOpacity(_glassRequiredLight, openness, transparency));
        if (snap) _glassPalette.Reset(_glassGoalDark ? 0 : 1, now);

        void Head(ref FluidSpring spring, double target)
        {
            if (snap) { spring.Reset(target, now); return; }
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
        if (!_glassSurface.Present(material, out var applied)) { CloseGlass(); return; }
        GlassTurnStep(applied.Material, now);
        GlassRenderingNeeded();
    }

    // A turn's text swap, by the leading panel only (the panels of a shared look follow its published palette), and its end.
    private void GlassTurnStep(GlassSurface.Material material, long now)
    {
        if (!_glassTurning) return;
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) { EndGlassTurn(); return; }
        bool showingDark = Look.Current.IsDark, arrived = !_glassPalette.Moving;
        if (!arrived && !_glassHurried && GlassReadability.Worst(showingDark, material.DarkVeil, material.LightVeil, material.Palette,
                _glassTails.Bright, _glassTails.Dark) < GlassReadable)
        {
            _glassHurried = true;
            _glassPalette.Retarget(_glassPalette.Target, now, GlassPaletteHurry);
        }
        // The leader swaps at the readable frame; any panel whose palette has arrived publishes the goal if nobody did (a leader
        // hidden mid-way), which is the same palette again for the others.
        if (showingDark != _glassGoalDark && (!Animates || arrived || _glassLeads && GlassReadability.SwapNow(showingDark, _glassGoalDark,
                material.DarkVeil, material.LightVeil, material.Palette, _glassTails.Bright, _glassTails.Dark)))
            PublishGlassPalette(_glassGoalDark, coordinated: true);
        if (arrived && Look.Current.IsDark == _glassGoalDark) _glassTurning = false;
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
        if (_glassActive && !_glassReading && now - _glassReadAt >= GlassBurstInterval) SampleGlass(this, EventArgs.Empty);
    }

    // No usable reading for a while (none came, or each was for a place the panel has left, as in a long drag): keep the text safe
    // over a backdrop we can no longer see, and let only readings that agree again thin the veil.
    private void GlassStale(long now)
    {
        if (_glassFirstSample || now - _glassSampledAt <= GlassStaleMs) return;
        int transparency = Settings.GlassTransparency;
        _glassRequiredDark = Math.Max(_glassRequiredDark, GlassRecipe.LuminosityFor(true, .85, .15, transparency));
        _glassRequiredLight = Math.Max(_glassRequiredLight, GlassRecipe.LuminosityFor(false, .85, .15, transparency));
        _glassWantedDark = _glassWantedLight = double.NaN;
        QueueGlass();
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
        if (_glassProbe is { } timer)
        {
            int interval = Environment.TickCount64 < _glassBurstUntil ? GlassBurstInterval : GlassProbeInterval;
            if (timer.Interval.TotalMilliseconds != interval) timer.Interval = TimeSpan.FromMilliseconds(interval);
        }
        if (!_glassActive || _glassReading || CurrentGlassSampleArea() is not { } area) return;
        _glassReading = true;
        _glassReadAt = Environment.TickCount64;
        long generation = _glassGeneration;
        var monitor = area.Screen;
        var screen = new Int32Rect((int)monitor.X, (int)monitor.Y, (int)monitor.Width, (int)monitor.Height);
        var probe = _acrylicProbe;
        try
        {
            var reading = await Task.Run(() => probe.Read(area.Bounds, screen, (int)(24 * area.Scale), (int)(40 * area.Scale)));
            if (!_glassActive) return;
            long now = Environment.TickCount64;
            int transparency = Settings.GlassTransparency;
            if (reading is not { } sample || generation != _glassGeneration || area != CurrentGlassSampleArea()) { GlassStale(now); return; }
            double openness = GlassOpenness();
            _glassSampledAt = now;
            _glassLatestSample = (area, generation, now, sample);
            _glassTails = (sample.BrightFor(openness), sample.DarkFor(openness));
            // A video's frames change most pixels every time and are no new scene: only the middle's brightness moving a lot hints at
            // one, and not while the backdrop is a video.
            if (double.IsFinite(_glassLastMiddle) && Math.Abs(sample.MiddleMean - _glassLastMiddle) > .12 && _glassAppearance?.Video != true) GlassBurst(soft: true);
            _glassLastMiddle = sample.MiddleMean;
            _glassLight = GlassLight.Follow(_glassLight, GlassLight.Direction(sample.Sides, _glassLight), _glassLightAt == 0 ? 0 : now - _glassLightAt);
            _glassLightAt = now;
            ApplyGlassOptics();
            double wantedDark = GlassRecipe.LuminosityFor(true, _glassTails.Bright, _glassTails.Dark, transparency);
            double wantedLight = GlassRecipe.LuminosityFor(false, _glassTails.Bright, _glassTails.Dark, transparency);
            double dark = _glassFirstSample ? wantedDark : GlassMaterial.Settle(wantedDark, _glassWantedDark, _glassRequiredDark);
            double light = _glassFirstSample ? wantedLight : GlassMaterial.Settle(wantedLight, _glassWantedLight, _glassRequiredLight);
            _glassFirstSample = false;
            _glassWantedDark = wantedDark; _glassWantedLight = wantedLight;
            bool changed = dark != _glassRequiredDark || light != _glassRequiredLight;
            _glassRequiredDark = dark; _glassRequiredLight = light;
            UpdateAutomaticGlass(sample, now);
            if (changed) QueueGlass();
        }
        finally { _glassReading = false; }
    }

    private void UpdateAutomaticGlass(AcrylicProbe.Reading sample, long now)
    {
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) return;
        var publisher = _app.Decks.FirstOrDefault(deck => deck._glassActive && deck.IsVisible && ReferenceEquals(deck.Look, Look)
            && PanelThemes.NormalizeId(deck.Settings.PanelTheme) == PanelThemes.GlassAuto);
        if (!ReferenceEquals(publisher, this)) return;
        bool appDark = SettingsPalette.IsDark(SettingsTheme.System);
        // A new decision object only for a new look or a palette set from outside, not for a turn of our own still under way.
        if (_glassAppearance is null || !ReferenceEquals(_glassAppearanceLook, Look) || _glassAppearance.Dark != _glassGoalDark)
        {
            _glassAppearanceLook = Look;
            _glassAppearance = new GlassAppearance(_glassGoalDark, appDark, videoGap: 2 * GlassProbeInterval);
        }
        _glassAppearance.Prior(appDark);
        _glassAppearance.Changed(sample.Changed, now);
        bool held = _expanded || _app.Decks.Any(deck => ReferenceEquals(deck.Look, Look) && (deck._expanded
            || deck._glassActive && deck.GlassOpenness() > .001));
        if (!_glassAppearance.Feed(sample.MiddleMean, now, held: held)) return;
        bool dark = _glassAppearance.Dark;
        if (!Animates || _glassSurface?.Visible != true)
        {
            _glassGoalDark = dark;
            PublishGlassPalette(dark, coordinated: false);
            return;
        }
        foreach (var deck in _app.Decks.ToList())
        {
            if (!ReferenceEquals(deck.Look, Look) || PanelThemes.NormalizeId(deck.Settings.PanelTheme) != PanelThemes.GlassAuto
                || !deck.Animates || deck._glassSurface?.Visible != true) continue;
            deck.BeginGlassPaletteTurn(dark, now, leads: ReferenceEquals(deck, this));
        }
    }

    // Heads the palette for dark or light from wherever it is now (a reversal mid-way included). The veils of both looks are
    // already following the backdrop, so the goal's branch is ready.
    private void BeginGlassPaletteTurn(bool dark, long now, bool leads)
    {
        _glassGoalDark = dark;
        _glassLeads = leads;
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
        _glassLatestSample = null;
        _glassSurface?.Hide();
        if (!_glassActive) return;
        _glassActive = false;
        GlassRenderingNeeded();
        _glassMaterialReady = false;
        _glassTurning = false;
        _glassAppearance = null;
        _acrylicProbe = new AcrylicProbe();
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
        if (_glassTitleHook != IntPtr.Zero) { UnhookWinEvent(_glassTitleHook); _glassTitleHook = IntPtr.Zero; }
        HideGlass();
        _glassActive = false;
        GlassRenderingNeeded();
        _glassSurface.Dispose();
        _glassSurface = null;
        _glassProbe = null;
        _glassAppearance = null;
        _glassAppearanceLook = null;
        _glassOutline = null;
        _glassContour = null;
        _glassMaterialReady = false;
    }
}
