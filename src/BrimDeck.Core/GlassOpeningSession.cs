namespace BrimDeck.Core;

// One opening of a panel and the read of the area it is about to cover (V3 V302, V304, V308). Each open intent is a new
// OpenEpoch; a close intent ends it at once, whatever the panel's motion still shows, so a capture started for an earlier
// opening (or before the panel moved to another display, or its scale changed) can finish but never reaches the glass: it is
// dropped with its reason. At most one capture runs per panel; an intent while one runs waits as the one pending (latest wins:
// nothing more queues). A capture that read nothing is Unknown: the glass keeps what the capsule asks for, never the worst range.
public sealed class GlassOpeningSession
{
    public readonly record struct Ticket(long OpenEpoch, long SceneEpoch, nint Monitor, int X, int Y, int Width, int Height,
        double Dpi, long Start)
    {
        public (int X, int Y, int Width, int Height) Area => (X, Y, Width, Height);
    }

    public enum Outcome { Applied, Closed, Superseded, SceneChanged, Moved, StaleArea, Unknown, Refused }

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

    // The place or the glass changed under the panel (hidden, moved, another display, another scale or theme).
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

    // The capture ended: what may be done with it. monitor, dpi and area (the screen pixels the opening covers) are the panel's
    // now: a read of an area the panel left, on the same display at the same scale, is StaleArea (V4 F02). read is false when it
    // read nothing; refused when the app's read budget refused it before it started.
    public Outcome Finish(Ticket ticket, nint monitor, double dpi, (int X, int Y, int Width, int Height) area, bool read, bool refused = false)
    {
        if (_running == ticket) _running = null;
        var outcome = !IsOpen ? Outcome.Closed
            : ticket.OpenEpoch != _openEpoch ? Outcome.Superseded
            : ticket.SceneEpoch != _sceneEpoch ? Outcome.SceneChanged
            : ticket.Monitor != monitor || Math.Abs(ticket.Dpi - dpi) > 1e-6 ? Outcome.Moved
            : ticket.Area != area ? Outcome.StaleArea
            : refused ? Outcome.Refused
            : !read ? Outcome.Unknown
            : Outcome.Applied;
        if (outcome == Outcome.Applied) AppliedEpoch = ticket.OpenEpoch;
        _counts[outcome] = Count(outcome) + 1;
        Last = outcome;
        return outcome;
    }

    // One cover read for the ticket, from the app's read budget to the read that waited for it (V4 F04): read captures (null: it
    // read nothing), where tells the panel's monitor and scale once it answered, apply gets the outcome and the reading, again
    // starts the pending read. Whatever throws on the way (the capture, telling the place, applying) is handed to failed and
    // ends here: the budget's slot is always given back, a read that ended without an outcome is recorded as Unknown (or as the
    // staler reason it already had), so the opening is free for its next read, and a pending intent still gets its read.
    public async Task<Outcome> Read<T>(Ticket ticket, GlassReadBudget<object> budget, object key, Func<Task<T?>> read,
        Func<(nint Monitor, double Dpi, (int X, int Y, int Width, int Height) Area)> where, Action<Outcome, T?> apply, Action again, Action<Exception>? failed = null) where T : struct
    {
        Outcome? outcome = null;
        try
        {
            bool refused = !budget.TryEnter(key, ticket.OpenEpoch);
            T? reading = null;
            if (!refused)
            {
                try { reading = await read(); }
                finally { budget.Leave(key, ticket.OpenEpoch); }
            }
            var (monitor, dpi, area) = where();
            outcome = Finish(ticket, monitor, dpi, area, reading is not null, refused);
            apply(outcome.Value, reading);
        }
        catch (Exception error)
        {
            outcome ??= Finish(ticket, ticket.Monitor, ticket.Dpi, ticket.Area, read: false);
            Report(failed, error);
        }
        finally
        {
            if (TakePending())
                try { again(); }
                catch (Exception error) { Report(failed, error); }
        }
        return outcome.Value;
    }

    private static void Report(Action<Exception>? failed, Exception error)
    {
        try { failed?.Invoke(error); }
        catch (Exception) { }
    }

    // After Finish: whether a pending intent asks for a new capture now (and clears it).
    public bool TakePending()
    {
        bool take = Pending && IsOpen && _running is null;
        Pending = false;
        return take;
    }
}
