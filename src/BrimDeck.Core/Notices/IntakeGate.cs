namespace BrimDeck.Core.Notices;

// Lets at most limit messages through within any window; thread-safe. Times are a monotonic clock in milliseconds (such as
// Environment.TickCount64), so a change of the wall clock neither opens nor shuts the gate.
public sealed class IntakeGate(int limit, TimeSpan window)
{
    private readonly Queue<long> _times = new();

    public bool Admit(long nowMilliseconds)
    {
        lock (_times)
        {
            while (_times.Count > 0 && nowMilliseconds - _times.Peek() >= (long)window.TotalMilliseconds) _times.Dequeue();
            if (_times.Count >= limit) return false;
            _times.Enqueue(nowMilliseconds);
            return true;
        }
    }
}
