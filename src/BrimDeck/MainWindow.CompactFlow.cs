using BrimDeck.Core;

namespace BrimDeck;

// The collapsed row while its surface is dragged wider or narrower. At rest the rings and the readings show whole items in a
// window and scroll through the rest (CompactSpacing.Share): a window is as wide as the items it holds, so as the surface moves
// it jumps by an item and the gaps and the centring take the difference back, and the whole row jumped with every item. Against
// 1.2.1, whose readings were cut at the region's edge and moved with the pointer, that read as a jerk (maintainer, 10-28).
// So while the surface is dragged (the pointer, or the resize shortcut, until the spring has come to rest) the windows follow the
// room continuously, an item cut at the edge and faded (CompactSpacing.ShareFlowing, CutFade), and nothing glides. Flow is how
// much of that there is: 1 while dragging, easing to 0 over FlowSeconds once the surface has settled, when the windows are the
// whole items of the rest again, the gaps take what is left and the glide starts.
public partial class MainWindow
{
    private double _compactFlow;
    private bool _flowEasing;
    private double _flowFrom, _flowAt;
    private readonly System.Windows.UIElement _flowClock = new();
    private const double FlowSeconds = .2;

    // The flow for the row being built now.
    private double CompactFlow()
    {
        if (Resizing && _resizeCompact) { _compactFlow = 1; _flowEasing = false; return 1; }
        return _compactFlow;
    }

    // The surface has settled: the row's windows ease to whole items. Without animations, or with the panel open or hidden, at once.
    private void SettleCompactFlow()
    {
        if (_compactFlow <= 0 || _flowEasing) return;
        if (!Motion.Enabled || _expanded || !IsVisible) { EndFlow(); return; }
        _flowEasing = true; _flowFrom = _compactFlow; _flowAt = Eased.Now;
        Motion.Start(_flowClock, FlowFrame);
    }

    private bool FlowFrame(double now)
    {
        if (!_flowEasing) return false;
        double t = Math.Clamp((now - _flowAt) / FlowSeconds, 0, 1);
        if (t >= 1 || _expanded || !IsVisible) { EndFlow(); return false; }
        _compactFlow = _flowFrom * (1 - Soft.Out.Ease(t));
        RenderCompact();
        return true;
    }

    // The windows are whole items again. A row that is not on show (the panel opened meanwhile) is built when it comes back: the shape
    // it was last built for is forgotten (ApplyCompactGeometry).
    private void EndFlow()
    {
        _compactFlow = 0; _flowEasing = false;
        if (_expanded || !IsVisible) _compactRendered = null; else RenderCompact();
    }
}
