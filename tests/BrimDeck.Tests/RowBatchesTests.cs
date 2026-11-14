using BrimDeck.Core.Notices;

// The long notice list built in batches (RowBatches): the first rows at once, the rest per queued pass, in order, and a cancelled
// list builds nothing more. The scheduler is a queue the test runs by hand.
internal static class RowBatchesTests
{
    internal static void Run(Action<string, bool> check)
    {
        var queue = new Queue<Action>();
        void Pump(int passes = int.MaxValue) { while (passes-- > 0 && queue.Count > 0) queue.Dequeue()(); }
        var items = Enumerable.Range(0, 20).ToList();

        var built = new List<int>(); int steps = 0;
        var batches = new RowBatches<int>(items, (i, n) => { if (i != n) built.Add(-1); built.Add(n); }, queue.Enqueue, 3, () => steps++);
        batches.Start(8);
        check("The first rows are built at once and the rest wait for a pass", built.Count == 8 && batches.Built == 8 && batches.Pending && queue.Count == 1);
        Pump(1);
        check("Each pass adds one batch of rows", built.Count == 11 && steps == 1 && batches.Pending && queue.Count == 1);
        Pump();
        check("The passes end once every row is built, in order, none twice, and no pass is left queued",
            built.SequenceEqual(items) && !batches.Pending && queue.Count == 0 && steps == 4 && batches.Built == 20);

        var old = new List<int>();
        var first = new RowBatches<int>(items, (_, n) => old.Add(n), queue.Enqueue, 3);
        first.Start(8); Pump(1);
        first.Cancel();
        var fresh = new List<int>();
        var second = new RowBatches<int>(items, (_, n) => fresh.Add(n), queue.Enqueue, 3);
        second.Start(8);
        Pump();
        check("A cancelled list adds no more rows, even when its pass was already queued", old.Count == 11 && !first.Pending && first.Built == 11);
        check("The list that replaced it is built whole and in order", fresh.SequenceEqual(items) && !second.Pending);

        var all = new List<int>();
        var restore = new RowBatches<int>(items, (_, n) => all.Add(n), queue.Enqueue, 3);
        restore.Start(items.Count);
        check("A first count as long as the list builds it whole and queues nothing", all.Count == 20 && !restore.Pending && queue.Count == 0);

        var short_ = new List<int>();
        var small = new RowBatches<int>(new[] { 7, 8, 9 }, (_, n) => short_.Add(n), queue.Enqueue, 3);
        small.Start(8);
        check("A list shorter than the first batch is built at once", short_.SequenceEqual(new[] { 7, 8, 9 }) && !small.Pending && queue.Count == 0);

        var none = new RowBatches<int>(Array.Empty<int>(), (_, _) => { }, queue.Enqueue, 3);
        none.Start(8);
        check("An empty list is not pending", !none.Pending && queue.Count == 0);

        var keyed = new List<int>();
        var through = new RowBatches<int>(items, (_, n) => keyed.Add(n), queue.Enqueue, 3);
        through.Start(12);
        check("A first count past the initial batch (the row the keyboard was on) is built before the focus is asked for",
            keyed.Count == 12 && keyed[11] == 11 && through.Pending);
        through.Cancel(); Pump();
    }
}
