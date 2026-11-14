namespace BrimDeck.Core.Notices;

// A long list built a few rows a pass instead of all at once: the first rows now, the rest from queued passes (the page posts them
// at background priority, so input and the panel's own motion go first). Cancel drops what is left, for a list that is built again.
// The scheduler is injected: a test runs the passes by hand.
public sealed class RowBatches<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly Action<int, T> _add;
    private readonly Action<Action> _post;
    private readonly int _batch;
    private readonly Action? _stepped;
    private bool _cancelled;

    public RowBatches(IReadOnlyList<T> items, Action<int, T> add, Action<Action> post, int batch, Action? stepped = null)
    {
        _items = items; _add = add; _post = post; _batch = Math.Max(1, batch); _stepped = stepped;
    }

    public IReadOnlyList<T> Items => _items;
    // How many rows exist so far; they are always the first Built items, in order.
    public int Built { get; private set; }
    // Whether rows are still to come.
    public bool Pending => !_cancelled && Built < _items.Count;

    // Adds the first rows (at least `first`) now and queues the rest.
    public void Start(int first)
    {
        Build(Math.Clamp(first, 0, _items.Count));
        if (Pending) Queue();
    }

    public void Cancel() => _cancelled = true;

    private void Build(int end)
    {
        for (; Built < end; Built++) _add(Built, _items[Built]);
    }

    private void Queue() => _post(() =>
    {
        if (_cancelled) return;
        Build(Math.Min(_items.Count, Built + _batch));
        _stepped?.Invoke();
        if (Pending) Queue();
    });
}
