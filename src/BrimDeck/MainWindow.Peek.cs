using System.Windows;
using BrimDeck.Core;

namespace BrimDeck;

// The indicator that opens into the capsule or the notch (DeckSettings.LineOpens, IndicatorPeek). Hovering the line, or clicking it
// with ClickToExpand, turns the panel's own surface into that style in the line's place: its content and size, read-only. The capsule
// sits against the line's edge (IndicatorCapsule); the notch hangs from where the line does, centred on it. A click on it opens the
// panel from it; leaving it, or closing the panel opened from it, turns it back into the line. It is the panel itself that changes its
// style (EffectiveStyle), so the shape morphs as a window rule's does, and the window's order, the forced topmost mirror
// (TaskbarMirror) and the pointer checks all hold for it as they do for the line.
public partial class MainWindow
{
    private readonly IndicatorPeek _peek = new();
    // The area that keeps the capsule or the notch (IndicatorCapsule.Holds), in DIPs from the monitor's corner, as it was last placed.
    private DipRect _peekHolds;
    // The monitor's top-left corner in device pixels (UpdateCollapsedGeometry).
    private Point _monitorOriginPx;

    // The style shown without the capsule or the notch: the line where a window rule asks for it, else the chosen style.
    private CompactStyle RestStyle => !_preview && Settings.Behavior(_context, _host.App.Name, _host.App.Path, _host.BackgroundKind) == WindowBehavior.Line ? CompactStyle.Line : Settings.Style;
    // The style the line shown opens into; null when it opens the panel.
    private CompactStyle? LineIsland => _preview || RestStyle != CompactStyle.Line ? null
        : Settings.LineOpens switch { IndicatorOpens.Capsule => CompactStyle.Capsule, IndicatorOpens.Notch => CompactStyle.Notch, _ => null };
    private bool LineOpensIsland => LineIsland is not null;
    // The capsule or the notch shows in the line's place, or the panel opened from it.
    private bool IslandFromLine => _peek.Active && LineOpensIsland;
    private bool CapsuleFromLine => _peek.Active && LineIsland == CompactStyle.Capsule;
    private bool NotchFromLine => _peek.Active && LineIsland == CompactStyle.Notch;

    // The open delay ran out on the line, or the line was clicked: it turns into the capsule or the notch.
    private void BeginPeek()
    {
        if (!LineOpensIsland || _expanded || IsSuppressed || !IsVisible || _moveArmed || !_peek.Begin()) return;
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
    // A new setting, window rule or hiding the panels may take the capsule or the notch away; ApplySettings then lays out the line.
    private void KeepPeekCurrent()
    {
        if ((_peek.Active || _peek.Resting) && (!LineOpensIsland || IsSuppressed)) _peek.Reset();
    }
    // The panel opened from the capsule or the notch has closed (to it): the line comes back once that is laid out.
    private void PeekPanelClosed()
    {
        if (!_peek.Active) return;
        Dispatcher.BeginInvoke(() => { if (!_expanded) EndPeek(); });
    }

    // The line as it would be placed, in DIPs from the monitor's corner (the screen's frame), with its edge and where it senses the
    // pointer (IndicatorCapsule.LineReach).
    private (DipRect Surface, ScreenEdge Edge, DipRect Reach) LineOnScreen()
    {
        var (edge, overTaskbar) = Settings.Dock(CompactStyle.Line);
        var frame = EdgeFrame.For(edge, _screenMonitor.Width, _screenMonitor.Height, _screenWork, overTaskbar);
        var monitor = new DipRect(MonitorBoundsDip.X, MonitorBoundsDip.Y, frame.Monitor.Width, frame.Monitor.Height);
        var line = CollapsedGeometry.Resolve(Settings, CompactStyle.Line, monitor, frame.FromScreen(_screenWork), _pixelsPerDip, null, 0, _scale, null, overTaskbar);
        return (frame.ToScreen(line.Surface), edge, IndicatorCapsule.LineReach(frame, line.Surface));
    }

    // The capsule in the line's place, in DIPs from the monitor's corner (the screen's frame): the capsule's own size (its width as
    // its content asks, with auto width), against the line's edge at the capsule's own distance from it.
    private CollapsedShape PeekCapsule(double? wanted)
    {
        var own = CollapsedGeometry.Resolve(Settings, CompactStyle.Capsule, _screenMonitor, _screenWork, _pixelsPerDip, wanted, _compactRequired, _scale);
        var (line, edge, reach) = LineOnScreen();
        var surface = IndicatorCapsule.Place(line, edge, own.Surface.Width, own.Surface.Height, Settings.CapsuleTop, _screenWork);
        _peekHolds = IndicatorCapsule.Holds(reach, surface);
        return own with { Surface = surface };
    }

    // The notch in the line's place, in the frame of the line's edge (Dock gives the notch the line's edge and anchor while it shows):
    // the notch's own size, centred on the line along the edge (IndicatorCapsule.Along).
    private CollapsedShape PeekNotch(double? wanted)
    {
        var notch = CollapsedGeometry.Resolve(Settings, CompactStyle.Notch, MonitorBoundsDip, WorkDip, _pixelsPerDip, wanted, _compactRequired, _scale,
            ColumnFor(CompactStyle.Notch) ? double.IsNaN(_compactColumn) ? 0 : _compactColumn : null);
        var line = CollapsedGeometry.Resolve(Settings, CompactStyle.Line, MonitorBoundsDip, WorkDip, _pixelsPerDip, null, 0, _scale, null, Settings.Dock(CompactStyle.Line).OverTaskbar).Surface;
        var surface = notch.Surface with { X = IndicatorCapsule.Along(line.X + line.Width / 2, notch.Surface.Width, MonitorBoundsDip.Width) };
        _peekHolds = IndicatorCapsule.Holds(IndicatorCapsule.LineReach(_frame, line), _frame.ToScreen(surface));
        return notch with { Surface = surface };
    }

    // Whether the pointer (device pixels) is on the capsule or the notch, on the line's place or between them.
    private bool PointerOnPeek(Point cursor)
    {
        if (!IslandFromLine || !(_pixelsPerDip > 0)) return false;
        double x = (cursor.X - _monitorOriginPx.X) / _pixelsPerDip, y = (cursor.Y - _monitorOriginPx.Y) / _pixelsPerDip;
        return x >= _peekHolds.X && x <= _peekHolds.Right && y >= _peekHolds.Y && y <= _peekHolds.Bottom;
    }
}
