namespace BrimDeck.Core;

// How far the island reached (its height) and when, its last Capacity changes (V5 S02): what the panel may have covered while a
// capture of the area an opening covers ran. The capture is judged by the heights of its own time: a height laid out after it
// ended hides nothing it shows, and one laid out just before it began may still have been on the screen (an opening turned back
// from a closing under way; an opening without motion is laid out in full while the capture runs).
// Fixed arrays, nothing allocated when adding or asking. Times are Stopwatch timestamps, in the order they come.
public sealed class GlassReachHistory
{
    public const int Capacity = 128;
    // A height is on the screen until a later one is composed: two frames at 60 Hz (ms). Measured on 278 reads of openings
    // turned back part way: 17 ms left the panel in one of those judged, 25 ms in none.
    public const double ShownFor = 34;

    private readonly long[] _at = new long[Capacity];
    private readonly double[] _reach = new double[Capacity];
    private long _added;

    // The island's height from now on; the same height again, or one that is not known, adds nothing.
    public void Add(long at, double reach)
    {
        if (!double.IsFinite(reach) || _added > 0 && _reach[(_added - 1) % Capacity] == reach) return;
        int slot = (int)(_added++ % Capacity);
        _at[slot] = at;
        _reach[slot] = reach;
    }

    // The farthest the island may have been shown to reach during a capture: of the heights laid out until it ended, back to the
    // one in effect ShownFor before it began. Infinite when the history does not tell.
    public double Farthest(long began, long ended)
    {
        long since = began - (long)(ShownFor * System.Diagnostics.Stopwatch.Frequency / 1000);
        double farthest = double.NegativeInfinity;
        for (long i = _added - 1; i >= Math.Max(0, _added - Capacity); i--)
        {
            int slot = (int)(i % Capacity);
            if (_at[slot] > ended) continue;
            farthest = Math.Max(farthest, _reach[slot]);
            if (_at[slot] <= since) return farthest;
        }
        return _added <= Capacity && double.IsFinite(farthest) ? farthest : double.PositiveInfinity;
    }

    // The share of an area's depth (from..open, along the island's height) the panel, with its margin, may have covered during
    // the capture (0: none of it; 1: all, or it cannot be told).
    public double Covered(long began, long ended, double margin, double from, double open)
    {
        double farthest = Farthest(began, ended);
        return double.IsFinite(farthest) && open > from ? Math.Clamp((farthest + margin - from) / (open - from), 0, 1) : 1;
    }
}
