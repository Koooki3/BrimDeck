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
    private (Size Size, double Shoulder, CornerRadius Corners, Point Origin, Point Along, Point Depth)? _glassOutline;
    private GlassSurface.PixelPoint[]? _glassContour;

    internal static bool GlassAvailable => PanelTheme.GlassAvailable;

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
        LocationChanged += GlassLayout;
        IsVisibleChanged += GlassVisibility;
        QueueGlass();
    }

    private void GlassLayout(object? sender, EventArgs e) => QueueGlass();
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
        bool dark = Look.Current.IsDark;
        bool paletteChanged = _glassDark != dark;
        int transparency = Settings.GlassTransparency;
        if (paletteChanged)
        {
            _glassDark = dark;
            _glassRequired = GlassEffect.LuminosityFor(dark, 1, 0, transparency);
            _glassFirstSample = true;
            _glassGeneration++;
        }
        var look = dark ? GlassEffect.AcrylicDark : GlassEffect.AcrylicLight;
        double target = GlassMaterial.LuminosityOpacity(_glassRequired, GlassOpenness(), transparency);
        double opacity = _glassVeil.Update(target, Environment.TickCount64, _glassActive && !paletteChanged && Animates, _glassSampleChanged);
        _glassSampleChanged = false;
        look = look with { LuminosityOpacity = opacity };
        var owner = new WindowInteropHelper(this).Handle;
        if (_glassContour is null)
        {
            HideGlass();
            return;
        }
        if (!_glassSurface.Show(owner, _glassBounds, 0, HoverZone.Opacity * Island.MaterialOpacity, look, _glassContour))
        {
            // Retry on a later settings or environment update instead of every layout pass.
            CloseGlass();
            return;
        }
        if (!_glassActive) { _glassActive = true; ApplySurface(); }
        if (_glassVeil.Active)
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

    private async void SampleGlass(object? sender, EventArgs e)
    {
        var monitorHandle = MonitorHandle;
        if (!_glassActive || _glassReading || _glassDark is not { } dark
            || WindowsHost.MonitorScreen(monitorHandle) is not { Bounds: var monitor }) return;
        _glassReading = true;
        long generation = _glassGeneration;
        var bounds = _glassBounds;
        var sourceLook = Look;
        var screen = new Int32Rect((int)monitor.X, (int)monitor.Y, (int)monitor.Width, (int)monitor.Height);
        var probe = _acrylicProbe;
        try
        {
            double scale = VisualTreeHelper.GetDpi(Island).DpiScaleX;
            var reading = await Task.Run(() => probe.Read(bounds, screen, (int)(24 * scale), (int)(40 * scale)));
            if (reading is not { } sample || !_glassActive || generation != _glassGeneration
                || !ReferenceEquals(sourceLook, Look) || sourceLook.Current.IsDark != dark || _glassBounds != bounds
                || MonitorHandle != monitorHandle || WindowsHost.MonitorScreen(monitorHandle)?.Bounds != monitor
                || VisualTreeHelper.GetDpi(Island).DpiScaleX != scale) return;
            double openness = GlassOpenness();
            long now = Environment.TickCount64;
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
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto || Look.IsMain && this != _app.Deck) return false;
        bool appDark = SettingsPalette.IsDark(SettingsTheme.System);
        if (_glassAppearance is null || !ReferenceEquals(_glassAppearanceLook, Look))
        {
            _glassAppearanceLook = Look;
            _glassAppearance = new GlassAppearance(Look.Current.IsDark, appDark, videoGap: 2 * GlassProbeInterval);
        }
        _glassAppearance.Prior(appDark);
        _glassAppearance.Changed(sample.Changed, now);
        bool held = _expanded || _app.Decks.Any(deck => ReferenceEquals(deck.Look, Look) && deck._expanded);
        if (!_glassAppearance.Feed(sample.MiddleMean, now, held: held)) return false;
        Look.AutomaticGlassDark = _glassAppearance.Dark;
        _app.ApplyPanelTheme();
        return true;
    }

    private void HideGlass()
    {
        _glassProbe?.Stop();
        _glassVeilTimer?.Stop();
        _glassSampleChanged = false;
        _glassSurface?.Hide();
        if (!_glassActive) return;
        _glassActive = false;
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
        LocationChanged -= GlassLayout;
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
