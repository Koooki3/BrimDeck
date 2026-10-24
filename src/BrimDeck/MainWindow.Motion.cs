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
        _shadowSettle.Tick += (_, _) => { _shadowSettle.Stop(); ShadowHost.CacheMode = _shadowCache; };
        IslandShadow.SizeChanged += (_, _) => { ShadowHost.CacheMode = null; _shadowSettle.Stop(); _shadowSettle.Start(); };
        Closed += (_, _) => _shadowSettle.Stop();
    }

    // Windows changed something motion depends on (lock, display, battery saver, animation effects, high contrast).
    // Transparency effects or high contrast turning glass on or off dresses the surface again.
    private void EnvironmentChanged()
    {
        if (_glassShown != PanelTheme.GlassShown && _themeGeneration >= 0) ApplySettings();
        UpdateAmbient(); UpdateMusicTimer(); UpdateMonitorDemand();
    }

    // The shadow's cached picture (ShadowHost) is drawn at the display's scale, and reaches past the blur by whole device
    // pixels: a picture whose edge fell between pixels was resampled when drawn, and differed from the shadow drawn directly
    // by up to 44 levels along the island's edge at 125 %. Laid out with whole device pixels (UseLayoutRounding), the picture
    // then matches the uncached shadow exactly at every scale (measured at 100 to 200 %).
    private void FitShadowCache(double scale = 0)
    {
        if (_shadowCache is not { } cache || IslandShadow.Effect is not DropShadowEffect effect) return;
        if (!(scale > 0)) scale = PresentationSource.FromVisual(this) is null ? 1 : VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (cache.RenderAtScale != scale) cache.RenderAtScale = scale;
        double reach = Math.Ceiling((effect.BlurRadius + effect.ShadowDepth + 2) * scale) / scale;
        var margin = new Thickness(-reach);
        if (ShadowBounds.Margin != margin) ShadowBounds.Margin = margin;
    }

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
