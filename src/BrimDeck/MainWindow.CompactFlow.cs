using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The collapsed row while its width is being changed continuously: the windows of the rings and the readings follow the room
// instead of stepping by whole items (RowFlow, CompactSpacing.ShareFlowing). At rest they hold whole items (CompactSpacing.Share),
// so as the surface moves a window jumps by an item and the gaps and the centring take the difference back, and the whole row
// jumped with every item; 1.2.1's readings were cut at the region's edge and moved with the pointer (maintainer, 10-28). Here
// the flow rises while the surface is dragged by the pointer or the resize shortcut, or while the settings window changes the
// width again and again (a slider, a stepper), and falls when that stops, when the windows are the whole items of the rest
// again and the glide starts. A width changed once (a number typed, a reset, another style, scale or monitor) does not flow: it
// is a new layout, not a motion.
public partial class MainWindow
{
    private readonly RowFlow _flow = new();
    private bool _flowRunning;
    private DispatcherTimer? _flowHold;
    private readonly UIElement _flowClock = new();

    private bool FlowDragging => Resizing && _resizeCompact;
    // The windows of the rest, taken whenever the row is built without the flow, for the flow to start from (RowFlow.Window).
    private double _restRings = -1, _restReadings = -1;
    // Whether those windows are the ones the flow rose from: not after it began to fall and rose again (a slider dragged again before
    // the flow of the last one was gone), when it rises from the whole items of the width it is at then.
    private bool _restHeld;
    private bool FlowRising => _flow.Target(FlowDragging) > 0;

    // The flow for the row being built now.
    private double CompactFlow()
    {
        if (!_flowRunning) _flow.Restart(Eased.Now);
        _flow.Advance(Eased.Now, FlowDragging, Motion.Enabled && IsVisible);
        if (_flow.Moving(FlowDragging) && !_flowRunning) StartFlow();
        return _flow.Eased;
    }

    private void StartFlow()
    {
        _flowRunning = true; _flow.Restart(Eased.Now);
        Motion.Start(_flowClock, FlowFrame);
    }

    // A frame while the flow moves. During a drag the drag's own frames build the row; otherwise this one does.
    private bool FlowFrame(double now)
    {
        if (!_flowRunning) return false;
        _flow.Advance(now, FlowDragging, Motion.Enabled && IsVisible);
        if (!FlowDragging && !_expanded && IsVisible) RenderCompact();
        if (_flow.Moving(FlowDragging)) return true;
        _flowRunning = false;
        // A row that was not on show (the panel opened meanwhile) is built when it comes back: the shape it was last built for is forgotten.
        if (_expanded || !IsVisible) _compactRendered = null;
        return false;
    }

    // The drag has ended or the changes have stopped: the windows ease to whole items.
    private void SettleCompactFlow()
    {
        if (_flowRunning || _flow.Value <= 0) return;
        double before = _flow.Value;
        _flow.Restart(Eased.Now);
        _flow.Advance(Eased.Now, FlowDragging, Motion.Enabled && IsVisible);
        if (_flow.Moving(FlowDragging)) StartFlow();
        else if (_expanded || !IsVisible) _compactRendered = null;
        else if (before != _flow.Value) RenderCompact();
    }

    // The settings changed the collapsed width, the style, the scale and the height staying as they were (ApplySettings).
    private void NoteCollapsedWidth(CollapsedShape before, double pixelsPerDipBefore)
    {
        var now = Collapsed;
        if (_expanded || !IsVisible || now.Style == CompactStyle.Line || before.Style != now.Style || pixelsPerDipBefore != _pixelsPerDip
            || before.Scale != now.Scale || before.Surface.Height != now.Surface.Height || Math.Abs(before.Surface.Width - now.Surface.Width) < .01) return;
        if (!_flow.NoteChange(Eased.Now)) return;
        _flowHold ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(RowFlow.Hold) };
        _flowHold.Tick -= EndHold; _flowHold.Tick += EndHold;
        _flowHold.Stop(); _flowHold.Start();
    }

    private void EndHold(object? sender, EventArgs e)
    {
        _flowHold?.Stop();
        SettleCompactFlow();
    }
}
