namespace BrimDeck.Core;

// How many of liquid glass's backdrop captures may run at once in the whole app (review v2 U12, §5: at most 1-2 workers). Each
// panel holds at most one slot, under the id of its capture: a panel whose stuck capture was given up takes its slot again for
// the next one, and the stuck one's late Leave (an older id) frees nothing. A panel refused waits for its next tick and then
// reads the place it is at then (latest wins: nothing queues).
public sealed class GlassReadBudget<THolder>(int most = GlassReadBudget<THolder>.DefaultMost) where THolder : class
{
    public const int DefaultMost = 2;
    private readonly Dictionary<THolder, long> _held = new(ReferenceEqualityComparer.Instance);

    public int Running => _held.Count;
    public int Refused { get; private set; }

    public bool TryEnter(THolder holder, long id)
    {
        if (!_held.ContainsKey(holder) && _held.Count >= most) { Refused++; return false; }
        _held[holder] = id;
        return true;
    }

    public void Leave(THolder holder, long id)
    {
        if (_held.TryGetValue(holder, out long held) && held == id) _held.Remove(holder);
    }

    // The panel is gone (hidden, closed): its slot is free whatever runs.
    public void Drop(THolder holder) => _held.Remove(holder);
}
