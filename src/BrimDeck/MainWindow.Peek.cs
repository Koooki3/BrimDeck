using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The indicator that peeks (WindowBehavior.Peek). It stays the line; hovering it shows a small read-only bubble of the chosen
// system readings beside it (PeekBubbleWindow), placed on its inner side (PeekBubblePlacement). The bubble is a window of its own:
// the collapsed surface, its edge and its place never change for it. The system is read only while the bubble shows
// (UpdateMonitorDemand), so it shows dashes until the first reading arrives, never the figures of an earlier time.
public partial class MainWindow
{
    private readonly WindowPeek _peek = new();
    private PeekBubbleWindow? _peekBubble;
    // The bubble and the room between it and the indicator, in DIPs from the monitor's corner.
    private DipRect _peekRect, _peekBetween;
    // What the bubble's strip was built without (StripAdmission), to build it again when a reading joins.
    private CompactMonitorItems _peekHidden;
    private string _peekLayout = "";
    // The monitor's top-left corner in device pixels (UpdateCollapsedGeometry).
    private Point _monitorOriginPx;

    private void StartPeekLeave() { _leave.Stop(); _leave.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.CloseDelay)); _leave.Start(); }

    // The open delay ran out on the indicator: the bubble appears.
    private void BeginPeek()
    {
        if (!PeekShows || IsSuppressed || !IsVisible || DisplayPower.Off || !_peek.Begin()) return;
        _hover.Stop();
        if (!ShowPeekBubble()) _peek.End();
    }
    private void EndPeek()
    {
        if (!_peek.End()) return;
        _hover.Stop(); _leave.Stop();
        HidePeekBubble();
    }
    // The peeking rule with readings to show. Without readings the indicator behaves as an ordinary one: hovering (or a click, with
    // ClickToExpand) opens the panel.
    private bool PeekShows => PeekRule && StripNeedsSystem;
    // A context change, a new setting or hiding the panels (hotkey, tray, a hiding rule) may take the peek away. Hiding is immediate,
    // so the bubble goes without its animation.
    private void KeepPeekCurrent()
    {
        if (IsSuppressed && (_peek.Active || _peek.Resting || _peekBubble is not null)) { _peek.Reset(); HidePeekBubble(animate: false); return; }
        if ((_peek.Active || _peek.Resting) && !PeekShows) { _peek.Reset(); HidePeekBubble(); }
    }
    // Asked on every pointer check: the bubble goes at once when the panel is hidden, moved or opened, or the display is off.
    private void GuardPeekBubble()
    {
        if (_peekBubble is null) return;
        if (IsSuppressed || !IsVisible || _moveArmed || _expanded || !PeekShows || DisplayPower.Off) { _peek.Reset(); _hover.Stop(); _leave.Stop(); HidePeekBubble(); }
    }

    private bool ShowPeekBubble()
    {
        if (_expanded || Collapsed.Style != CompactStyle.Line) return false;
        if (_peekBubble is null)
        {
            var bubble = new PeekBubbleWindow(Look, _scale, Settings.WindowCapture);
            bubble.Clicked += OpenFromPeekBubble;
            _peekBubble = bubble;
        }
        BuildPeekBubble(_app.Monitor?.Latest);
        _peekBubble.Appear(Animates && Settings.AnimationDuration > 0);
        // The sampler starts for it.
        UpdateMonitorDemand();
        return true;
    }

    // The readings and the window's place, made again when a reading joins (the drives, a temperature) and changes the width.
    private void BuildPeekBubble(MonitorSnapshot? snapshot)
    {
        if (_peekBubble is not { } bubble) return;
        double scale = CompactMusicLayout.SafeScale(_scale), dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _peekHidden = _stripAdmission.Hidden; _peekLayout = _stripAdmission.Layout;
        var strip = new MonitorStrip(Settings.CompactReadings, MonitorThemeColors.Strip(Settings, Look), scale, true, new StripPresence(), dpi, _peekHidden, _stripAdmission,
            coreDisplay: Settings.CoreDisplay);
        bubble.SetStrip(strip);
        var (width, height) = bubble.Wanted;
        var indicator = _frame.ToScreen(Collapsed.Surface);
        _peekRect = PeekBubblePlacement.Place(indicator, _frame.Edge, width, height, _screenWork, _pixelsPerDip);
        _peekBetween = PeekBubblePlacement.Between(indicator, _frame.Edge, _peekRect);
        bubble.Place((int)Math.Round(_monitorOriginPx.X + _peekRect.X * _pixelsPerDip), (int)Math.Round(_monitorOriginPx.Y + _peekRect.Y * _pixelsPerDip),
            (int)Math.Round(_peekRect.Width * _pixelsPerDip), (int)Math.Round(_peekRect.Height * _pixelsPerDip), dpi);
        strip.Update(snapshot, false, (int)Settings.CompactSmoothing);
    }

    // Each system reading: into the bubble, which is built again first when a reading joined or left it.
    private void UpdatePeekBubble(MonitorSnapshot? snapshot)
    {
        if (_peekBubble is null) return;
        if (_stripAdmission.Hidden != _peekHidden || _stripAdmission.Layout != _peekLayout) { BuildPeekBubble(snapshot); return; }
        _peekBubble.Update(snapshot, MonitorMotion && Settings.CompactSmoothing != CompactSmoothing.Off, (int)Settings.CompactSmoothing);
    }

    // The bubble goes; the system is no longer read for it (UpdateMonitorDemand), after the sampler's own pause.
    private void HidePeekBubble(bool animate = true)
    {
        if (_peekBubble is not { } bubble) return;
        _peekBubble = null;
        bubble.Clicked -= OpenFromPeekBubble;
        bubble.Dismiss(animate && Animates && Settings.AnimationDuration > 0);
        UpdateMonitorDemand();
    }

    // A click on the bubble opens the panel, as one on the indicator does.
    private void OpenFromPeekBubble()
    {
        if (_expanded || _preview || _moveArmed || IsSuppressed) return;
        _hover.Stop(); _leave.Stop();
        OpenAtPointer();
    }

    // Whether the pointer (device pixels) is on the bubble or on the way to it from the indicator.
    private bool PointerOnPeekBubble(Point cursor)
        => _peekBubble is not null && _pixelsPerDip > 0
            && PeekBubblePlacement.Holds(_peekRect, _peekBetween, (cursor.X - _monitorOriginPx.X) / _pixelsPerDip, (cursor.Y - _monitorOriginPx.Y) / _pixelsPerDip);
}
