using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;
using BrimDeck.Native;

namespace BrimDeck;

public partial class MainWindow
{
    private const int GlassProbeInterval = 500;
    private const double GlassPaletteTurnMs = 500, GlassTextCrossover = .48;
    private AcrylicProbe _acrylicProbe = new();
    private GlassSurface? _glassSurface;
    private GlassAppearance? _glassAppearance;
    private PanelLook? _glassAppearanceLook;
    private DispatcherTimer? _glassProbe;
    private bool _glassActive, _glassQueued, _glassReading;
    private long _glassGeneration;
    private bool? _glassDark;
    private bool _glassFirstSample;
    private Int32Rect _glassBounds;
    private double _glassRequired, _glassPreviousVeil;
    private double _glassOpenHeight;
    private GlassVeilTransition _glassVeil;
    private DispatcherTimer? _glassVeilTimer;
    private bool _glassSampleChanged;
    private GlassPaletteTurn? _glassPaletteTurn;
    private (GlassSampleArea Area, long Generation, AcrylicProbe.Reading Reading)? _glassLatestSample;
    private readonly record struct GlassSampleArea(nint Monitor, Rect Screen, Int32Rect Bounds, double Scale, PanelLook Look);
    private sealed record GlassPaletteTurn(PanelLook Look, GlassEffect.AcrylicLook From, GlassEffect.AcrylicLook To,
        long Start, double Backdrop, double Required, bool Dark, bool Leads);
    private (Size Size, double Shoulder, CornerRadius Corners, Point Origin, Point Along, Point Depth)? _glassOutline;
    private GlassSurface.PixelPoint[]? _glassContour;

    internal static bool GlassAvailable => PanelTheme.GlassAvailable;

    internal void UpdateGlassTheme()
    {
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto)
        {
            _glassAppearance = null;
            _glassPaletteTurn = null;
        }
        else if (_glassPaletteTurn is { } turn && !ReferenceEquals(turn.Look, Look)) _glassPaletteTurn = null;
        bool enabled = Look.Current.Effects.Glass is not null && !Look.HighContrast && GlassAvailable && !_retiring;
        if (enabled == (_glassSurface is not null)) { QueueGlass(); return; }
        if (!enabled) { CloseGlass(); return; }
        _glassSurface = new GlassSurface();
        _glassProbe = new DispatcherTimer(TimeSpan.FromMilliseconds(GlassProbeInterval), DispatcherPriority.Background, SampleGlass, Dispatcher);
        _glassProbe.Stop();
        LayoutUpdated += GlassLayout;
        LocationChanged += GlassLocation;
        IsVisibleChanged += GlassVisibility;
        QueueGlass();
    }

    private void GlassLayout(object? sender, EventArgs e) => QueueGlass();
    private void GlassLocation(object? sender, EventArgs e)
    {
        _glassLatestSample = null;
        _glassGeneration++;
        QueueGlass();
    }
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
        var turn = _glassPaletteTurn;
        long now = Environment.TickCount64;
        double turnProgress = turn is null || !Animates ? 1 : Math.Clamp((now - turn.Start) / GlassPaletteTurnMs, 0, 1);
        bool dark = Look.Current.IsDark;
        bool paletteChanged = _glassDark != dark;
        int transparency = Settings.GlassTransparency;
        if (paletteChanged && turn is null)
        {
            _glassDark = dark;
            _glassRequired = GlassEffect.LuminosityFor(dark, 1, 0, transparency);
            _glassFirstSample = true;
            _glassGeneration++;
        }
        var look = dark ? GlassEffect.AcrylicDark : GlassEffect.AcrylicLight;
        double target = GlassMaterial.LuminosityOpacity(_glassRequired, GlassOpenness(), transparency);
        double opacity = _glassVeil.Update(target, now, _glassActive && !paletteChanged && Animates, _glassSampleChanged);
        _glassSampleChanged = false;
        look = look with { LuminosityOpacity = opacity };
        if (turn is not null)
            look = GlassEffect.BlendAcrylic(turn.From, turn.To, Math.Round(Soft.InOut.Ease(turnProgress) * 12) / 12);
        var owner = new WindowInteropHelper(this).Handle;
        if (_glassContour is null)
        {
            HideGlass();
            return;
        }
        if (!_glassSurface.Show(owner, _glassBounds, 0, HoverZone.Opacity * Island.MaterialOpacity, look, _glassContour,
            transient: turn is not null && look != turn.From && look != turn.To))
        {
            // Retry on a later settings or environment update instead of every layout pass.
            CloseGlass();
            return;
        }
        if (!_glassActive) { _glassActive = true; ApplySurface(); }
        if (turn is not null)
        {
            double luma = GlassEffect.AcrylicLuma(look, turn.Backdrop);
            bool crossed = turn.Dark ? luma <= GlassTextCrossover : luma >= GlassTextCrossover;
            if (Look.Current.IsDark != turn.Dark && (turnProgress >= 1 || turn.Leads && crossed)) PublishGlassPalette(turn.Dark, coordinated: true);
            if (turnProgress >= 1)
            {
                _glassPaletteTurn = null;
                _glassDark = turn.Dark;
                _glassRequired = _glassPreviousVeil = turn.Required;
                _glassFirstSample = true;
                _glassVeil.Update(look.LuminosityOpacity, now, animate: false);
                _glassSampleChanged = true;
                QueueGlass();
            }
        }
        if (_glassVeil.Active || _glassPaletteTurn is not null)
        {
            _glassVeilTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(24), DispatcherPriority.Render, (_, _) => QueueGlass(), Dispatcher);
            _glassVeilTimer.Start();
        }
        else _glassVeilTimer?.Stop();
        Island.Pattern = AcrylicNoise.Brush(VisualTreeHelper.GetDpi(Island).DpiScaleX, GlassMaterial.GrainStrength(Settings.GlassTransparency));
        if (_glassProbe is { IsEnabled: false } probe)
        {
            probe.Start();
            SampleGlass(this, EventArgs.Empty);
        }
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
        if (!_glassActive || _glassReading || _glassPaletteTurn is not null || _glassDark is not { } dark
            || CurrentGlassSampleArea() is not { } area) return;
        _glassReading = true;
        long generation = _glassGeneration;
        var monitor = area.Screen;
        var screen = new Int32Rect((int)monitor.X, (int)monitor.Y, (int)monitor.Width, (int)monitor.Height);
        var probe = _acrylicProbe;
        try
        {
            var reading = await Task.Run(() => probe.Read(area.Bounds, screen, (int)(24 * area.Scale), (int)(40 * area.Scale)));
            if (reading is not { } sample || !_glassActive || generation != _glassGeneration
                || area != CurrentGlassSampleArea() || area.Look.Current.IsDark != dark) return;
            double openness = GlassOpenness();
            long now = Environment.TickCount64;
            _glassLatestSample = (area, generation, sample);
            if (UpdateAutomaticGlass(sample, now)) return;
            double wanted = GlassEffect.LuminosityFor(dark, sample.BrightFor(openness), sample.DarkFor(openness), Settings.GlassTransparency);
            double next = _glassFirstSample ? wanted : GlassMaterial.FollowBackdrop(_glassRequired, wanted, _glassPreviousVeil);
            _glassFirstSample = false;
            _glassPreviousVeil = wanted;
            if (next == _glassRequired) return;
            _glassRequired = next;
            _glassSampleChanged = true;
            QueueGlass();
        }
        finally { _glassReading = false; }
    }

    private bool UpdateAutomaticGlass(AcrylicProbe.Reading sample, long now)
    {
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) return false;
        var publisher = _app.Decks.FirstOrDefault(deck => deck._glassActive && deck.IsVisible && ReferenceEquals(deck.Look, Look)
            && PanelThemes.NormalizeId(deck.Settings.PanelTheme) == PanelThemes.GlassAuto);
        if (!ReferenceEquals(publisher, this)) return false;
        bool appDark = SettingsPalette.IsDark(SettingsTheme.System);
        if (_glassAppearance is null || !ReferenceEquals(_glassAppearanceLook, Look) || _glassAppearance.Dark != Look.Current.IsDark)
        {
            _glassAppearanceLook = Look;
            _glassAppearance = new GlassAppearance(Look.Current.IsDark, appDark, videoGap: 2 * GlassProbeInterval);
        }
        _glassAppearance.Prior(appDark);
        _glassAppearance.Changed(sample.Changed, now);
        bool held = _expanded || _app.Decks.Any(deck => ReferenceEquals(deck.Look, Look) && (deck._expanded || deck._glassPaletteTurn is not null
            || deck._glassActive && deck.GlassOpenness() > .001));
        if (!_glassAppearance.Feed(sample.MiddleMean, now, held: held)) return false;
        bool dark = _glassAppearance.Dark;
        if (!Animates || _glassSurface?.DisplayedLook is null)
        {
            PublishGlassPalette(dark, coordinated: false);
            return true;
        }
        foreach (var deck in _app.Decks.ToList())
        {
            if (!ReferenceEquals(deck.Look, Look) || PanelThemes.NormalizeId(deck.Settings.PanelTheme) != PanelThemes.GlassAuto
                || !deck.Animates || deck._glassSurface?.DisplayedLook is not { } from) continue;
            deck.BeginGlassPaletteTurn(from, dark, now, leads: ReferenceEquals(deck, this));
        }
        return true;
    }

    private void BeginGlassPaletteTurn(GlassEffect.AcrylicLook from, bool dark, long now, bool leads)
    {
        var sample = _glassLatestSample is { } current && current.Generation == _glassGeneration && current.Area == CurrentGlassSampleArea()
            ? (AcrylicProbe.Reading?)current.Reading : null;
        int transparency = Settings.GlassTransparency;
        double required = GlassEffect.LuminosityFor(dark, sample?.Median ?? 1, sample?.Median ?? 0, transparency);
        var palette = dark ? GlassEffect.AcrylicDark : GlassEffect.AcrylicLight;
        var to = palette with { LuminosityOpacity = GlassMaterial.LuminosityOpacity(required, 0, transparency) };
        _glassPaletteTurn = new(Look, from, to, now, sample?.MiddleMean ?? (dark ? 1 : 0), required, dark, leads);
        _glassGeneration++;
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
        _glassVeilTimer?.Stop();
        _glassSampleChanged = false;
        _glassPaletteTurn = null;
        _glassLatestSample = null;
        _glassSurface?.Hide();
        if (!_glassActive) return;
        _glassActive = false;
        _glassAppearance = null;
        _acrylicProbe = new AcrylicProbe();
        _glassDark = null;
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
        HideGlass();
        _glassSurface.Dispose();
        _glassSurface = null;
        _glassProbe = null;
        _glassAppearance = null;
        _glassAppearanceLook = null;
        _glassOutline = null;
        _glassContour = null;
    }
}
