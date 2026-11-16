using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;

namespace BrimDeck;

public partial class MainWindow
{
    private GlassSurface? _glassSurface;
    private DispatcherTimer? _glassProbe;
    private bool _glassActive, _glassQueued, _glassReading;
    private long _glassGeneration, _glassDarkSince = -1;
    private bool? _glassDark;
    private bool _glassRise, _glassFirstSample;
    private Int32Rect _glassBounds;
    private double _glassRequired;
    private (Size Size, double Shoulder, CornerRadius Corners, Point Origin, Point Along, Point Depth)? _glassOutline;
    private GlassSurface.PixelPoint[]? _glassContour;

    internal static bool GlassAvailable => !SystemParameters.HighContrast && GetSystemMetrics(0x1000) == 0
        && Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) is not 0;

    internal void UpdateGlassTheme()
    {
        bool enabled = Look.Current.Effects.Glass is not null && !Look.HighContrast && GlassAvailable && !_retiring;
        if (enabled == (_glassSurface is not null)) { QueueGlass(); return; }
        if (!enabled) { CloseGlass(); return; }
        _glassSurface = new GlassSurface();
        _glassProbe = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, SampleGlass, Dispatcher);
        _glassProbe.Stop();
        LayoutUpdated += GlassLayout;
        LocationChanged += GlassLayout;
        IsVisibleChanged += GlassVisibility;
        QueueGlass();
    }

    private void GlassLayout(object? sender, EventArgs e) => QueueGlass();
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
        if (_glassDark != dark)
        {
            _glassDark = dark;
            _glassRequired = GlassEffect.LuminosityFor(dark, 1, 0);
            _glassFirstSample = true;
            _glassGeneration++;
            _glassDarkSince = -1;
            _glassRise = false;
        }
        var look = dark ? GlassEffect.AcrylicDark : GlassEffect.AcrylicLight;
        double openness = _expanded ? 1 : 0;
        look = look with { LuminosityOpacity = GlassMaterial.LuminosityOpacity(_glassRequired, openness, Settings.GlassTransparency) };
        var owner = new WindowInteropHelper(this).Handle;
        if (_glassContour is null || !_glassSurface.Show(owner, _glassBounds, 0, HoverZone.Opacity * Island.MaterialOpacity, look, _glassContour))
        {
            HideGlass();
            return;
        }
        if (!_glassActive) { _glassActive = true; ApplySurface(); }
        Island.Pattern = AcrylicNoise.Brush(VisualTreeHelper.GetDpi(Island).DpiScaleX, GlassMaterial.GrainStrength(Settings.GlassTransparency));
        if (_glassProbe is { IsEnabled: false } probe)
        {
            probe.Start();
            SampleGlass(this, EventArgs.Empty);
        }
    }

    private async void SampleGlass(object? sender, EventArgs e)
    {
        if (!_glassActive || _glassReading || _glassDark is not { } dark) return;
        _glassReading = true;
        long generation = _glassGeneration;
        var bounds = _glassBounds;
        var region = new RECT(bounds.X, bounds.Y, bounds.X + bounds.Width, bounds.Y + bounds.Height);
        try
        {
            double scale = VisualTreeHelper.GetDpi(Island).DpiScaleX;
            var reading = await Task.Run(() => AcrylicProbe.Read(region, (int)(24 * scale), (int)(40 * scale)));
            if (!_glassActive || generation != _glassGeneration || _glassBounds != bounds || reading is not { } sample) return;
            double openness = _expanded ? 1 : 0;
            double wanted = GlassEffect.LuminosityFor(dark, sample.BrightFor(openness), sample.DarkFor(openness));
            long now = Environment.TickCount64;
            if (_glassFirstSample)
            {
                _glassRequired = wanted;
                _glassFirstSample = false;
            }
            else if (wanted >= _glassRequired)
            {
                _glassDarkSince = -1;
                if (wanted > _glassRequired && _glassRise) { _glassRequired = wanted; _glassRise = false; }
                else _glassRise = wanted > _glassRequired;
            }
            else
            {
                _glassRise = false;
                if (_glassDarkSince < 0) _glassDarkSince = now;
                else if (now - _glassDarkSince >= 2500)
                {
                    _glassRequired = Math.Max(wanted, _glassRequired - .05);
                    _glassDarkSince = now;
                }
            }
            QueueGlass();
        }
        finally { _glassReading = false; }
    }

    private void HideGlass()
    {
        _glassProbe?.Stop();
        _glassSurface?.Hide();
        if (!_glassActive) return;
        _glassActive = false;
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
        _glassOutline = null;
        _glassContour = null;
    }

    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
}
