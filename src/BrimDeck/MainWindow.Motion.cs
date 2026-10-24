using System.Windows.Controls;
using System.Windows.Media.Effects;
using BrimDeck.Core;

namespace BrimDeck;

// The theme's motion on this panel: the island's ambient light (AmbientGlow) and the answers to the pointer over the open panel
// (InteractionFx). Both follow the global animation settings; the ambient light also stops while the panel is hidden, powering
// off, shown as the indicator line, or while the session is locked or battery saver is on.
public partial class MainWindow
{
    private AmbientGlow? _ambient;
    private FxSurface? _fx;

    private void InitializeMotion()
    {
        var content = (Panel)Island.Child;
        _ambient = new AmbientGlow(Island, content, IslandShadow.Effect as DropShadowEffect, MotionState, () => _expanded);
        _fx = InteractionFx.Island(this, Island, content, () => _expanded && !_preview);
        IsVisibleChanged += (_, _) => _ambient.Update();
        MotionEnvironment.Changed += UpdateAmbient;
        Closed += (_, _) => { MotionEnvironment.Changed -= UpdateAmbient; _ambient.Stop(); };
    }

    private MotionConditions MotionState() => MotionEnvironment.Conditions(IsVisible, IsSuppressed, _poweringOff, _expanded || EffectiveStyle != CompactStyle.Line);

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
