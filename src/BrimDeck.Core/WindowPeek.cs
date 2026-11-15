namespace BrimDeck.Core;

// What the window rules offer for a screen context, in the order the settings list them. Peek is the indicator that shows a small
// readout of the system readings beside it while the pointer is on it (WindowPeek, PeekBubblePlacement).
public static class WindowBehaviors
{
    public static readonly IReadOnlyList<WindowBehavior> All = [WindowBehavior.Normal, WindowBehavior.Line, WindowBehavior.Peek, WindowBehavior.Hide];
    // The panel is the thin indicator at rest: the line, and the line that peeks.
    public static bool IsIndicator(this WindowBehavior behavior) => behavior is WindowBehavior.Line or WindowBehavior.Peek;
}

// The state of an indicator that peeks (WindowBehavior.Peek): hovering the line shows the readout bubble beside it, leaving hides
// it again; the line itself never changes. It holds no timers; the window starts its open and close delays and tells this what
// happened.
//   Line     the indicator at rest; hovering it, after the open delay, begins the peek
//   Peeking  the bubble is shown (a click opens the panel, which closes back to the line and ends the peek)
// After a peek ends, the pointer may still be on the line: the line rests until the pointer has left it, so that it does not peek
// again at once.
public sealed class WindowPeek
{
    public bool Active { get; private set; }
    public bool Resting { get; private set; }
    // The pointer is on the indicator and the open delay has run out: whether the bubble is to be shown.
    public bool Begin()
    {
        if (Active || Resting) return false;
        Active = true; return true;
    }
    // The pointer has been away from the indicator and the bubble for the close delay.
    public bool End()
    {
        if (!Active) return false;
        Active = false; Resting = true; return true;
    }
    // The panel opened from the peek has closed: back to the line, which rests until the pointer has left it, whether or not the
    // pointer is still on it.
    public bool PanelClosed() => End();
    // Each pointer check at rest: a resting line wakes once the pointer is off it.
    public void Observe(bool overIndicator)
    {
        if (Resting && !overIndicator) Resting = false;
    }
    // The window's rule is no longer the peeking one.
    public void Reset() { Active = false; Resting = false; }
}
