namespace BrimDeck.Core;

// How many of liquid glass's backdrop captures may run at once in the whole app (review v2 U12, §5: at most 1-2 workers). Every
// capture that has begun counts until its own Leave, a capture given up as stuck too (its worker still runs): a panel's
// replacement for it takes a free slot if there is one and is refused otherwise, and a hidden panel's capture still under way
// keeps its slot until it answers. A panel refused waits for its next tick and then reads the place it is at then (latest
// wins: nothing queues).
public sealed class GlassReadBudget<THolder>(int most = GlassReadBudget<THolder>.DefaultMost) where THolder : class
{
    public const int DefaultMost = 2;
    private readonly List<(THolder Holder, long Id)> _running = [];

    public int Running => _running.Count;
    public int Refused { get; private set; }

    public bool TryEnter(THolder holder, long id)
    {
        if (_running.Count >= most) { Refused++; return false; }
        _running.Add((holder, id));
        return true;
    }

    // The capture answered (or failed): its slot is free. An id that is not running frees nothing.
    public void Leave(THolder holder, long id)
    {
        int index = _running.FindIndex(read => ReferenceEquals(read.Holder, holder) && read.Id == id);
        if (index >= 0) _running.RemoveAt(index);
    }
}
