using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The theme's motion on this panel: the island's ambient light (AmbientGlow) and the answers to the pointer over the open panel
// (InteractionFx). Both follow the global animation settings; the ambient light also stops while the panel is hidden, powering
// off, shown as the indicator line, or while the session is locked, the display is off or battery saver is on. The music and
// the readings in the collapsed row follow the same environment (MotionEnvironment).
public partial class MainWindow
{
    private AmbientGlow? _ambient;
    private FxSurface? _fx;
    private BitmapCache? _shadowCache;
    private DispatcherTimer? _shadowSettle;

    private void InitializeMotion()
    {
        var content = (Panel)Island.Child;
        _ambient = new AmbientGlow(Island, content, MotionState, () => _expanded);
        _fx = InteractionFx.Island(this, Island, content, () => _expanded && !_preview);
        IsVisibleChanged += (_, _) => _ambient.Update();
        MotionEnvironment.Changed += EnvironmentChanged;
        Closed += (_, _) => { MotionEnvironment.Changed -= EnvironmentChanged; _ambient.Stop(); };
        _shadowCache = ShadowHost.CacheMode as BitmapCache;
        SourceInitialized += (_, _) => FitShadowCache();
        DpiChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) FitShadowCache(e.NewDpi.DpiScaleX); };
        // While the island changes size (opening, closing, a resize) the picture would be made again on every frame, which saved
        // nothing and in one measurement cost more (28 % of a core against 24 % in a continuous resize at 144 Hz): the shadow is
        // drawn directly until its size has held still for a moment.
        _shadowSettle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        // The same holds while what the picture shows changes in place: the surface color (Changed is raised on each frame
        // of a color animation), the corners and the shoulders. The shadow fades on ShadowHost, outside the cache, so a fade
        // only composes the kept picture; while it is not shown at all there is nothing to keep.
        _shadowSettle.Tick += (_, _) => { _shadowSettle.Stop(); if (ShadowHost.Opacity > 0) ShadowHost.CacheMode = _shadowCache; };
        IslandShadow.SizeChanged += (_, _) => ShadowChanging();
        _surface.Changed += (_, _) => ShadowChanging();
        // A descriptor's listener is held by a static table: each is removed when the window closes, or a panel closed with its
        // monitor (or its panel turned off) would never be freed (#75 review).
        var listeners = new List<(DependencyPropertyDescriptor Property, object Element, EventHandler Handler)>();
        void Listen(DependencyProperty property, Type owner, object element, EventHandler handler)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, owner);
            descriptor.AddValueChanged(element, handler);
            listeners.Add((descriptor, element, handler));
        }
        Listen(Border.CornerRadiusProperty, typeof(IslandBorder), Island, (_, _) => ShadowChanging());
        Listen(IslandBorder.ShoulderRadiusProperty, typeof(IslandBorder), Island, (_, _) => ShadowChanging());
        // Hidden (the open panel, the line), the picture is let go; shown again, it is kept at once unless a change is under way.
        Listen(OpacityProperty, typeof(Grid), ShadowHost, (_, _) =>
        {
            if (ShadowHost.Opacity <= 0) ShadowHost.CacheMode = null;
            else if (ShadowHost.CacheMode is null && !_shadowSettle.IsEnabled) ShadowHost.CacheMode = _shadowCache;
        });
        if (ShadowHost.Opacity <= 0) ShadowHost.CacheMode = null;
        Closed += (_, _) =>
        {
            _shadowSettle.Stop();
            foreach (var (property, element, handler) in listeners) property.RemoveValueChanged(element, handler);
            listeners.Clear();
        };
    }

    private void ShadowChanging()
    {
        if (_shadowSettle is null) return;
        ShadowHost.CacheMode = null; _shadowSettle.Stop(); _shadowSettle.Start();
    }

    // Windows changed something motion depends on (lock, display, battery saver, animation effects, high contrast).
    // Transparency effects or high contrast turning glass on or off dresses the surface again.
    private void EnvironmentChanged()
    {
        if (_glassShown != PanelTheme.GlassShown && _themeGeneration >= 0) ApplySettings();
        // Battery saver, a remote session: the blur under glass may come or go.
        _glass?.Update();
        UpdateAmbient(); UpdateMusicTimer(); UpdateMonitorDemand();
    }

    // The shadow's cached picture (ShadowHost) is drawn at the display's scale, and reaches past the blur by whole device
    // pixels: a picture whose edge fell between pixels was resampled when drawn, and differed from the shadow drawn directly
    // by up to 44 levels along the island's edge at 125 %. Laid out with whole device pixels (UseLayoutRounding), the picture
    // then matches the uncached shadow exactly at every scale (measured at 100 to 200 %). WPF already draws a cached picture
    // at the system's DPI, so the cache is asked only for what this monitor's scale adds to it (#75 review).
    private void FitShadowCache(double scale = 0)
    {
        if (_shadowCache is not { } cache || IslandShadow.Effect is not DropShadowEffect effect) return;
        if (!(scale > 0)) scale = PresentationSource.FromVisual(this) is null ? 1 : VisualTreeHelper.GetDpi(this).DpiScaleX;
        double render = scale / SystemScale;
        if (cache.RenderAtScale != render) cache.RenderAtScale = render;
        double reach = Math.Ceiling((effect.BlurRadius + effect.ShadowDepth + 2) * scale) / scale;
        var margin = new Thickness(-reach);
        if (ShadowBounds.Margin != margin) ShadowBounds.Margin = margin;
    }

    private static readonly double SystemScale = GetDpiForSystem() is var dpi and > 0 ? dpi / 96d : 1;
    [DllImport("user32")] private static extern uint GetDpiForSystem();

    private MotionConditions MotionState() => MotionEnvironment.Conditions(IsVisible, IsSuppressed, _poweringOff, _expanded || EffectiveStyle != CompactStyle.Line);

    // After the theme has dressed the island: the light takes the new theme, and whatever the pointer had lit ends.
    private void ApplyThemeMotion() { FitShadowCache(); _ambient?.Apply(); _fx?.Reset(); }

    // After anything that can show, hide, open or close the panel: closed, whatever the pointer had lit fades with the content;
    // opened under a pointer that has not moved, the control under it is answered at once.
    private void UpdateAmbient()
    {
        _ambient?.Update();
        if (!_expanded) _fx?.Release(); else _fx?.Refresh();
    }
}
