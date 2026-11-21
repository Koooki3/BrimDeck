using System.Windows.Controls;
using System.Windows.Threading;

namespace BrimDeck;

// The theme's answer to the pointer over the open panel (InteractionFx): a mark on the control it is on. It follows the global
// animation settings (Soft.Enabled) and exists only while the panel is open; the controls of the settings window and the
// pop-ups keep their own looks.
public partial class MainWindow
{
    private FxSurface? _fx;

    private void InitializeMotion()
    {
        _fx = InteractionFx.Island(this, Island, (Panel)Island.Child, () => _expanded && !_preview, () => Animates, () => Look);
    }

    // After anything that opens or closes the panel: closed, whatever the pointer had marked fades with the content; opened under
    // a pointer that has not moved, the control under it is marked once the panel has its place.
    private void UpdateMotion()
    {
        if (!_expanded) _fx?.Release();
        else Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_expanded) _fx?.Refresh(); });
    }
}
