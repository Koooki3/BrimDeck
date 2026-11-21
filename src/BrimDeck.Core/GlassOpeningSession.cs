namespace BrimDeck.Core;

// One opening of a panel and the read of the area it is about to cover (V3 V302, V304, V308). Each open intent is a new
// OpenEpoch; a close intent ends it at once, whatever the panel's motion still shows, so a capture started for an earlier
// opening (or before the panel moved to another display, or its scale changed) can finish but never reaches the glass: it is
// dropped with its reason. At most one capture runs per panel; an intent while one runs waits as the one pending (latest wins:
// nothing more queues). A capture that read nothing is Unknown: the glass keeps what the capsule asks for, never the worst range.
public sealed class GlassOpeningSession
{
    public readonly record struct Ticket(long OpenEpoch, long SceneEpoch, nint Monitor, int X, int Y, int Width, int Height,
        double Dpi, long Start);

    public enum Outcome { Applied, Closed, Superseded, SceneChanged, Moved, Unknown, Refused }

    private long _openEpoch, _sceneEpoch;
    private Ticket? _running;
    private readonly Dictionary<Outcome, int> _counts = [];

    public long OpenEpoch => _openEpoch;
    public long SceneEpoch => _sceneEpoch;
    public bool IsOpen { get; private set; }
    public bool Running => _running is not null;
    // An intent came while a capture ran: the next capture starts when it ends, if the panel is still open.
    public bool Pending { get; private set; }
    // The epoch whose cover is shown now (0: none).
    public long AppliedEpoch { get; private set; }
    public Outcome? Last { get; private set; }
    public int Count(Outcome outcome) => _counts.GetValueOrDefault(outcome);

    // The panel begins to open: a new epoch; an earlier opening's capture is stale from here on.
    public void Intent()
    {
        _openEpoch++;
        IsOpen = true;
        Pending = false;
        AppliedEpoch = 0;
    }

    // The panel begins to close: every ticket of this opening is stale at once, and its cover is gone.
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _openEpoch++;
        Pending = false;
        AppliedEpoch = 0;
    }

    // The place or the glass changed under the panel (hidden, another display, another scale or theme).
    public void SceneChanged()
    {
        _sceneEpoch++;
        AppliedEpoch = 0;
    }

    // A capture for the opening under way, or null: none open, or one already running (then this one is pending).
    public Ticket? Begin(nint monitor, int x, int y, int width, int height, double dpi, long now)
    {
        if (!IsOpen) return null;
        if (_running is not null) { Pending = true; return null; }
        Pending = false;
        var ticket = new Ticket(_openEpoch, _sceneEpoch, monitor, x, y, width, height, dpi, now);
        _running = ticket;
        return ticket;
    }

    // Whether the ticket is still the opening's own (for a worker deciding whether to read at all).
    public bool Current(Ticket ticket) => IsOpen && ticket.OpenEpoch == _openEpoch && ticket.SceneEpoch == _sceneEpoch;

    // The capture ended: what may be done with it. monitor and dpi are the panel's now; read is false when it read nothing;
    // refused when the app's read budget refused it before it started.
    public Outcome Finish(Ticket ticket, nint monitor, double dpi, bool read, bool refused = false)
    {
        if (_running == ticket) _running = null;
        var outcome = !IsOpen ? Outcome.Closed
            : ticket.OpenEpoch != _openEpoch ? Outcome.Superseded
            : ticket.SceneEpoch != _sceneEpoch ? Outcome.SceneChanged
            : ticket.Monitor != monitor || Math.Abs(ticket.Dpi - dpi) > 1e-6 ? Outcome.Moved
            : refused ? Outcome.Refused
            : !read ? Outcome.Unknown
            : Outcome.Applied;
        if (outcome == Outcome.Applied) AppliedEpoch = ticket.OpenEpoch;
        _counts[outcome] = Count(outcome) + 1;
        Last = outcome;
        return outcome;
    }

    // After Finish: whether a pending intent asks for a new capture now (and clears it).
    public bool TakePending()
    {
        bool take = Pending && IsOpen && _running is null;
        Pending = false;
        return take;
    }
}
