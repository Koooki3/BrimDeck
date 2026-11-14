namespace BrimDeck.Core;

// The bytes received and sent since the app began reading the network, summed over the adapters that count for the rate. Only what
// can be told to be traffic is added: an adapter counts from the second reading it is seen in; a counter that went back (an adapter
// reset), an adapter that went away, a reading after a gap too long to be a tick (sleep, a stalled sampler), and a step faster than
// any link (a counter that jumped) add nothing and only set a new baseline. Forget drops the baseline and keeps the sums, so the
// time the network was not read is not counted. The sums are not saved; they start at zero with the app.
public sealed class TrafficTotals
{
    // Longer than any interval between two readings; beyond it the machine slept or the sampler stood still.
    public const double LongestGapSeconds = 30;
    // No physical link carries more (800 Gbit/s); a step above it is a counter that jumped.
    public const double FastestBytesPerSecond = 100_000_000_000;
    private IReadOnlyDictionary<ulong, (ulong Received, ulong Sent)> _previous = new Dictionary<ulong, (ulong, ulong)>();
    private double? _time;

    public double Received { get; private set; }
    public double Sent { get; private set; }

    // The next reading starts a new baseline; the sums stay.
    public void Forget() { _previous = new Dictionary<ulong, (ulong, ulong)>(); _time = null; }

    // Adds the traffic since the last reading (the caller does not change the dictionary afterwards) and returns the sums.
    public (double Received, double Sent) Add(IReadOnlyDictionary<ulong, (ulong Received, ulong Sent)> counters, double now)
    {
        var before = _previous; var then = _time;
        _previous = counters; _time = now;
        if (then is not { } start || !double.IsFinite(now) || now <= start || now - start > LongestGapSeconds) return (Received, Sent);
        double seconds = now - start, limit = FastestBytesPerSecond * seconds;
        foreach (var (adapter, (received, sent)) in counters)
        {
            if (!before.TryGetValue(adapter, out var was) || received < was.Received || sent < was.Sent) continue;
            double down = received - was.Received, up = sent - was.Sent;
            if (down > limit || up > limit) continue;
            Received += down; Sent += up;
        }
        return (Received, Sent);
    }
}
