using System.Windows;
using BrimDeck.Core;

namespace BrimDeck;

// The indicator that opens into the capsule (DeckSettings.LineOpensCapsule, IndicatorPeek). Hovering the line, or clicking it with
// ClickToExpand, turns the panel's own surface into the capsule in the line's place (IndicatorCapsule): the capsule's content and
// size, read-only. A click on it opens the panel from it; leaving it, or closing the panel opened from it, turns it back into the
// line. It is the panel itself that changes its style (EffectiveStyle), so the shape morphs as a window rule's does, and the window's
// order, the forced topmost mirror (TaskbarMirror) and the pointer checks all hold for the capsule as they do for the line.
public partial class MainWindow
{
    private readonly IndicatorPeek _peek = new();
    // The line's place and the area that keeps the capsule (IndicatorCapsule.Holds), in DIPs from the monitor's corner, as the capsule
    // was last placed.
    private DipRect _peekHolds;
    // The monitor's top-left corner in device pixels (UpdateCollapsedGeometry).
    private Point _monitorOriginPx;

    // The style shown without the capsule: the line where a window rule asks for it, else the chosen style.
    private CompactStyle RestStyle => !_preview && Settings.Behavior(_context, _host.App.Name, _host.App.Path, _host.BackgroundKind) == WindowBehavior.Line ? CompactStyle.Line : Settings.Style;
    // The line shown opens into the capsule.
    private bool LineOpensCapsule => !_preview && Settings.LineOpensCapsule && RestStyle == CompactStyle.Line;
    // The capsule shows in the line's place, or the panel opened from it.
    private bool CapsuleFromLine => _peek.Active && LineOpensCapsule;

    // The open delay ran out on the line, or the line was clicked: it turns into the capsule.
    private void BeginPeek()
    {
        if (!LineOpensCapsule || _expanded || IsSuppressed || !IsVisible || _moveArmed || !_peek.Begin()) return;
        _hover.Stop(); _leave.Stop();
        ApplySettings(true);
    }
    // Back to the line, which rests until the pointer has left it.
    private void EndPeek()
    {
        if (!_peek.End()) return;
        _hover.Stop(); _leave.Stop();
        ApplySettings(true);
    }
    private void StartPeekLeave() { _leave.Stop(); _leave.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.CloseDelay)); _leave.Start(); }
    // A new setting, window rule or hiding the panels may take the capsule away; ApplySettings then lays out the line.
    private void KeepPeekCurrent()
    {
        if ((_peek.Active || _peek.Resting) && (!LineOpensCapsule || IsSuppressed)) _peek.Reset();
    }
    // The panel opened from the capsule has closed (to the capsule): the line comes back once that is laid out.
    private void PeekPanelClosed()
    {
        if (!_peek.Active) return;
        Dispatcher.BeginInvoke(() => { if (!_expanded) EndPeek(); });
    }

    // The capsule in the line's place, in DIPs from the monitor's corner (the screen's frame): the capsule's own size (its width as
    // its content asks, with auto width), against the line's edge at the capsule's own distance from it.
    private CollapsedShape PeekCapsule(double? wanted)
    {
        var own = CollapsedGeometry.Resolve(Settings, CompactStyle.Capsule, _screenMonitor, _screenWork, _pixelsPerDip, wanted, _compactRequired, _scale);
        var (edge, overTaskbar) = Settings.Dock(CompactStyle.Line);
        var frame = EdgeFrame.For(edge, _screenMonitor.Width, _screenMonitor.Height, _screenWork, overTaskbar);
        var monitor = new DipRect(MonitorBoundsDip.X, MonitorBoundsDip.Y, frame.Monitor.Width, frame.Monitor.Height);
        var line = frame.ToScreen(CollapsedGeometry.Resolve(Settings, CompactStyle.Line, monitor, frame.FromScreen(_screenWork), _pixelsPerDip,
            null, 0, _scale, null, overTaskbar).Surface);
        var surface = IndicatorCapsule.Place(line, edge, own.Surface.Width, own.Surface.Height, Settings.CapsuleTop, _screenWork);
        _peekHolds = IndicatorCapsule.Holds(line, surface);
        return own with { Surface = surface };
    }

    // Whether the pointer (device pixels) is on the capsule, on the line it came from or between them.
    private bool PointerOnPeek(Point cursor)
    {
        if (!CapsuleFromLine || !(_pixelsPerDip > 0)) return false;
        double x = (cursor.X - _monitorOriginPx.X) / _pixelsPerDip, y = (cursor.Y - _monitorOriginPx.Y) / _pixelsPerDip;
        return x >= _peekHolds.X && x <= _peekHolds.Right && y >= _peekHolds.Y && y <= _peekHolds.Bottom;
    }
}
