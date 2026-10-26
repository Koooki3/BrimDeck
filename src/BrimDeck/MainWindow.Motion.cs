using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The theme's motion on this panel: the island's ambient light (AmbientGlow) and the answers to the pointer over the open panel
// (InteractionFx). Both follow the global animation settings; the ambient light also stops while the panel is hidden, powering
// off, shown as the indicator line, while a maximized or full-screen window is in front on its monitor (the context the
// window rules read, WindowsHost), or while the session is locked, the display is off or battery saver is on. The music and
// the readings in the collapsed row follow the same environment (MotionEnvironment).
public partial class MainWindow
{
    private AmbientGlow? _ambient;
    private FxSurface? _fx;

    private void InitializeMotion()
    {
        var content = (Panel)Island.Child;
        _ambient = new AmbientGlow(Island, content, MotionState, () => _expanded, () => LiquidGlassShown);
        _fx = InteractionFx.Island(this, Island, content, () => _expanded && !_preview);
        IsVisibleChanged += (_, _) => _ambient.Update();
        MotionEnvironment.Changed += EnvironmentChanged;
        Closed += (_, _) => { MotionEnvironment.Changed -= EnvironmentChanged; _ambient.Stop(); };
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

    private MotionConditions MotionState() => MotionEnvironment.Conditions(IsVisible, IsSuppressed, _poweringOff, _expanded || EffectiveStyle != CompactStyle.Line,
        covered: !_preview && _context != ScreenContext.Desktop);

    // After the theme has dressed the island: the light takes the new theme, and whatever the pointer had lit ends.
    private void ApplyThemeMotion() { _ambient?.Apply(); _fx?.Reset(); }

    // After anything that can show, hide, open or close the panel: closed, whatever the pointer had lit fades with the content;
    // opened under a pointer that has not moved, the control under it is answered at once.
    private void UpdateAmbient()
    {
        _ambient?.Update();
        if (!_expanded) _fx?.Release(); else _fx?.Refresh();
    }
}
