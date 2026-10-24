using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// What the frame-strip previews and their pixel checks (tests/BrimDeck.FxPreview) need from the pointer layer (FxSurface) and the
// date range picker (ModelDatePicker), kept apart from what the app uses: a clock, a palette, the "system" choice, an animation
// length, the motion conditions and whether anything moves, instead of the app's; and a look at what was drawn. In the app
// Preview is null and none of this is read.
internal sealed class PreviewHooks
{
    public Func<double> Clock { get; set; } = () => Eased.Now;
    public Func<PanelPalette>? Palette { get; set; }
    public bool? SystemTheme { get; set; }
    public int? AnimationDuration { get; set; }
    public Func<MotionConditions>? Conditions { get; set; }
    public Func<bool>? Moves { get; set; }
}

internal sealed partial class FxSurface
{
    internal PreviewHooks? Preview { get; set; }
    internal bool Hooked => _hooked || _watchingLayout;
    internal Rect DrawnBounds => VisualTreeHelper.GetContentBounds(_visual);
    internal Rect HoverBox => _hover?.Box ?? Rect.Empty;
    // The pointer left or came back onto the pressed control while it is held.
    internal void ArmPress(bool inside) { if (_pressed is not null) { _press.Arm(Clock(), inside, _timing, _motion.Steps); Wake(); } }
    // A new palette: dressed again before the next frame.
    internal void Restyle() { _generation = -1; Reset(); }
}
