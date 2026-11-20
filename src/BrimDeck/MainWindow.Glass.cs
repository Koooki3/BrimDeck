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
// The springs run on the display's frames (CompositionTarget.Rendering) only while one moves, and set the persistent graph's
// weights (GlassSurface.Present): nothing is compiled while the material moves. Sampling never stops for a turn. It runs
// twice a second at rest and in a short burst after a hint that the backdrop may have changed (another window came to the
// front, the panel moved or began to open or close, a reading that changed a lot), one reading at a time.
public partial class MainWindow
{
    private const int GlassProbeInterval = 500, GlassBurstInterval = 80, GlassBurstMs = 700, GlassStaleMs = 1500;
    // Stiffness (rad/s): the veil thickens within 5 % in about 180 ms and thins in about 300 ms; a palette turn about 365 ms.
    private const double GlassVeilAttack = 26, GlassVeilRelease = 16, GlassPaletteOmega = 13;
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
    private bool _glassMaterialReady, _glassFirstSample, _glassTurning, _glassLeads, _glassGoalDark, _glassWasExpanded;
    private double _glassRequiredDark, _glassRequiredLight, _glassWantedDark = double.NaN, _glassWantedLight = double.NaN;
    private long _glassSampledAt, _glassBurstUntil, _glassReadAt;
    private double _glassLastMiddle = double.NaN;
    private (double Bright, double Dark) _glassTails = (1, 0);
    private (GlassSampleArea Area, long Generation, long At, AcrylicProbe.Reading Reading)? _glassLatestSample;
    private readonly record struct GlassSampleArea(nint Monitor, Rect Screen, Int32Rect Bounds, double Scale, PanelLook Look);
    private (Size Size, double Shoulder, CornerRadius Corners, Point Origin, Point Along, Point Depth)? _glassOutline;
    private GlassSurface.PixelPoint[]? _glassContour;

    internal static bool GlassAvailable => PanelTheme.GlassAvailable;
    // A palette turn is under way: the palette still moves, or the text has not yet swapped to the goal.
    internal bool GlassPaletteTurning => _glassTurning;

    internal void UpdateGlassTheme()
    {
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) _glassAppearance = null;
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
        if (!_glassSurface.Show(owner, _glassBounds, 0, HoverZone.Opacity * Island.MaterialOpacity, material, _glassContour))
        {
            // Retry on a later settings or environment update instead of every layout pass.
            CloseGlass();
            return;
        }
        if (!_glassActive) { _glassActive = true; ApplySurface(); }
        GlassTurnStep(material, now);
        GlassRenderingNeeded();
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
        if (!_glassSurface.Present(material)) { CloseGlass(); return; }
        GlassTurnStep(material, now);
        GlassRenderingNeeded();
    }

    // A turn's text swap, by the leading panel only (the panels of a shared look follow its published palette), and its end.
    private void GlassTurnStep(GlassSurface.Material material, long now)
    {
        if (!_glassTurning) return;
        bool showingDark = Look.Current.IsDark, arrived = !_glassPalette.Moving;
        // The leader swaps at the readable frame; any panel whose palette has arrived publishes the goal if nobody did (a leader
        // hidden mid-way), which is the same palette again for the others.
        if (showingDark != _glassGoalDark && (!Animates || arrived || _glassLeads && GlassReadability.SwapNow(showingDark, _glassGoalDark,
                material.DarkVeil, material.LightVeil, material.Palette, _glassTails.Bright, _glassTails.Dark)))
            PublishGlassPalette(_glassGoalDark, coordinated: true);
        if (arrived && Look.Current.IsDark == _glassGoalDark) _glassTurning = false;
    }

    // A hint that the backdrop may have changed: read at once (unless a reading runs or one was just taken) and keep reading
    // quickly for a while.
    private void GlassBurst()
    {
        if (_glassProbe is null) return;
        long now = Environment.TickCount64;
        _glassBurstUntil = now + GlassBurstMs;
        if (_glassProbe.Interval.TotalMilliseconds != GlassBurstInterval) _glassProbe.Interval = TimeSpan.FromMilliseconds(GlassBurstInterval);
        if (_glassActive && !_glassReading && now - _glassReadAt > 30) SampleGlass(this, EventArgs.Empty);
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
            if (!_glassActive || generation != _glassGeneration || area != CurrentGlassSampleArea()) return;
            long now = Environment.TickCount64;
            int transparency = Settings.GlassTransparency;
            if (reading is not { } sample)
            {
                // No reading: after a while without one, keep the text safe over a backdrop we can no longer see.
                if (!_glassFirstSample && now - _glassSampledAt > GlassStaleMs)
                {
                    _glassRequiredDark = Math.Max(_glassRequiredDark, GlassRecipe.LuminosityFor(true, .85, .15, transparency));
                    _glassRequiredLight = Math.Max(_glassRequiredLight, GlassRecipe.LuminosityFor(false, .85, .15, transparency));
                    QueueGlass();
                }
                return;
            }
            double openness = GlassOpenness();
            _glassSampledAt = now;
            _glassLatestSample = (area, generation, now, sample);
            _glassTails = (sample.BrightFor(openness), sample.DarkFor(openness));
            if (double.IsFinite(_glassLastMiddle) && (Math.Abs(sample.MiddleMean - _glassLastMiddle) > .12 || sample.Changed > .5)) GlassBurst();
            _glassLastMiddle = sample.MiddleMean;
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
        _glassPalette.Retarget(dark ? 0 : 1, now, GlassPaletteOmega);
        GlassRenderingNeeded();
        QueueGlass();
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
