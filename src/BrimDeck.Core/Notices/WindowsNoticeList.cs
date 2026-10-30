namespace BrimDeck.Core.Notices;

// How the next reading of Windows' list is taken. Start: the list as it is, nothing in it handed over (after starting or
// being granted access). Resume: after waking; what arrived since the last reading is handed over as missed, recorded but
// not shown as it arrives. A Start asked for earlier is not weakened by a later Resume.
public enum WindowsNoticeBaseline { None, Resume, Start }

// What WindowsNoticeSource has seen of Windows' notification list, and how a new reading differs from it. Not thread-safe:
// the source calls it under its own lock.
public sealed class WindowsNoticeList(TimeSpan longestUnknownWait)
{
    // While the newest id is unconfirmed the whole list is read this often at first, half as often again after each reading
    // that found nothing new, up to longestUnknownWait (perf-6: a quiet machine may see no notice for hours).
    public static readonly TimeSpan FirstUnknownWait = TimeSpan.FromSeconds(3);
    // Each listed notification's instance key (empty when it was not read), application key and title.
    private readonly Dictionary<uint, (string Key, string AppKey, string Title)> _known = [];
    // Direct observations survive older snapshots until a replacement is found or a full read started after them finishes.
    private readonly Dictionary<uint, (NoticeInput? Input, bool Missed, bool Delivered, long Generation)> _observed = [];
    // A reading has been compared since the list was last cleared, so a Resume baseline has something to compare with.
    private bool _read;
    // The reading that settles the pending baseline has started (BeginRead): a notice the log names after that is live.
    private bool _baselineReadStarted;
    private long _observationGeneration;

    public long ObservationGeneration => _observationGeneration;
    public HashSet<uint> KnownIds => _known.Keys.ToHashSet();
    public uint Newest { get; private set; }
    // A reading has seen a notice arrive, so Newest is the newest id Windows has given out and the probe can rely on it.
    public bool Confirmed { get; private set; }
    public WindowsNoticeBaseline Baseline { get; private set; } = WindowsNoticeBaseline.Start;
    public TimeSpan UnknownWait { get; private set; } = FirstUnknownWait;
    // The last Compare found a new notice whose id is past the one after Newest. Tiles, badges and toasts share Windows' ids,
    // so an application that updates its badge before each notice (Teams, mail) leaves the probe looking at a used id; the
    // source then probes a few ids ahead for a while (WindowsNoticePace.ProbeIds).
    public bool SkippedProbe { get; private set; }

    public sealed record Changes(IReadOnlyList<NoticeInput> Arrived, IReadOnlyList<NoticeInput> Missed, IReadOnlyList<string> Removed);

    public bool Contains(uint id) => _known.ContainsKey(id);

    // Forgets the list: after starting, or when access was revoked (the list then reads as empty, which must not look like
    // every notice being removed).
    public void Reset()
    {
        _known.Clear(); _observed.Clear(); _read = false; _baselineReadStarted = false; _observationGeneration++;
        Newest = 0; Confirmed = false; Baseline = WindowsNoticeBaseline.Start; UnknownWait = FirstUnknownWait;
    }

    public void ExpectBaseline(WindowsNoticeBaseline baseline)
    {
        if (baseline > Baseline) Baseline = baseline;
        if (baseline == WindowsNoticeBaseline.Start) _observed.Clear();
        // Ids may have moved on while asleep: the unconfirmed newest is checked often again.
        if (baseline != WindowsNoticeBaseline.None) { UnknownWait = FirstUnknownWait; _baselineReadStarted = false; }
    }

    // A full reading starts: returns the generation to give Compare when it ends, and notes that the reading settling a
    // pending baseline is under way (Observe).
    public long BeginRead()
    {
        if (Baseline != WindowsNoticeBaseline.None) _baselineReadStarted = true;
        return _observationGeneration;
    }

    // Records a notification fetched from an event-log TrackingId. It does not infer deletions; only Compare has the complete
    // present-id set needed to make that decision. The notification is remembered until that full reading for replacement
    // pairing, but the immediate change is returned now. The log names only toasts delivered after BrimDeck subscribed, so
    // one is new even while a Start baseline is pending: the baseline keeps what was already listed from being shown, and
    // Compare skips an id recorded here instead of absorbing it. After waking, a toast logged before the reading that hands
    // the sleep over has started belongs to that sleep (Missed); one logged after it started is live.
    public Changes Observe(uint id, NoticeInput? input)
    {
        uint before = Newest;
        if (id > Newest) Newest = id;
        if (_known.ContainsKey(id)) return Empty;
        bool missed = Baseline == WindowsNoticeBaseline.Resume && _read && !_baselineReadStarted;
        _known[id] = (input?.InstanceKey ?? "", input?.AppKey ?? "", input?.Title ?? "");
        _observed[id] = (input, missed, input is not null, ++_observationGeneration);
        if (Confirmed && id > before + 1 && Baseline == WindowsNoticeBaseline.None) SkippedProbe = true;
        // A logged toast has just been given its id, so Newest is the newest id Windows has handed out.
        Confirmed = true;
        UnknownWait = FirstUnknownWait;
        if (input is null) return Empty;
        return missed ? new([], [input], []) : new([input], [], []);
    }

    // The ids of a reading not seen yet, the ones Compare will ask to read.
    public List<uint> Unknown(IEnumerable<uint> ids) => ids.Where(id => !_known.ContainsKey(id)).Distinct().ToList();

    // Compares a reading (the listed ids in Windows' order) with what was seen. read gives a new id's notice, or null for one
    // that cannot be read or is not handed over; it is called once per new id. Arrived and Missed are oldest first, so the
    // newest ends up on top of the recent notices; a notice that took the place of a removed one names it (Replaces).
    public Changes Compare(IEnumerable<uint> ids, Func<uint, NoticeInput?> read)
        => Compare(ids, read, _observationGeneration);

    // A full read started at readGeneration. Direct observations made after that point are merged into its id set: the
    // snapshot may have been captured before those notices arrived, so it must not remove them or consume their pairing chance.
    public Changes Compare(IEnumerable<uint> ids, Func<uint, NoticeInput?> read, long readGeneration)
    {
        var baseline = Baseline == WindowsNoticeBaseline.Resume && !_read ? WindowsNoticeBaseline.Start : Baseline;
        var added = new List<(NoticeInput Input, bool Missed, bool Direct)>();
        var present = new HashSet<uint>();
        bool arrived = false;
        uint before = Newest, firstNew = uint.MaxValue;
        foreach (var id in ids)
        {
            if (!present.Add(id)) continue;
            if (id > Newest) Newest = id;
            if (id > before && id < firstNew) firstNew = id;
            if (_known.ContainsKey(id)) continue;
            // One unreadable notification is skipped without affecting the others.
            var input = read(id);
            _known[id] = (input?.InstanceKey ?? "", input?.AppKey ?? "", input?.Title ?? "");
            arrived = true;
            if (input is not null && baseline != WindowsNoticeBaseline.Start) added.Add((input, baseline == WindowsNoticeBaseline.Resume, false));
        }

        // The snapshot may predate an Observe that happened while its asynchronous read was in flight. Preserve that id for
        // this comparison and leave its pairing chance for the next full read.
        foreach (var (id, observed) in _observed)
            if (observed.Generation > readGeneration) present.Add(id);

        if (baseline != WindowsNoticeBaseline.Start)
            foreach (var (id, observed) in _observed)
                if (observed.Delivered && observed.Input is { } observedInput) added.Add((observedInput, observed.Missed, true));

        // The first new notice skipped the id the probe looks at: a tile, a badge or a notice already gone took it.
        SkippedProbe = Confirmed && baseline == WindowsNoticeBaseline.None && firstNew != uint.MaxValue && firstNew > before + 1;
        if (arrived && baseline != WindowsNoticeBaseline.Start) Confirmed = true;
        else if (baseline == WindowsNoticeBaseline.None && !Confirmed) UnknownWait = TimeSpan.FromTicks(Math.Min(UnknownWait.Ticks * 2, longestUnknownWait.Ticks));
        var removed = new List<NoticeReplacements.Gone>();
        foreach (var gone in _known.Keys.Where(id => !present.Contains(id)).ToList())
        {
            if (_known[gone] is { Key.Length: > 0 } known) removed.Add(new(known.Key, known.AppKey, known.Title));
            _known.Remove(gone);
        }
        Baseline = WindowsNoticeBaseline.None; _read = true; _baselineReadStarted = false;
        var ordered = added.OrderBy(a => a.Input.CreatedAt).ToList();
        var (paired, left) = NoticeReplacements.Pair(ordered.Select(a => a.Input).ToList(), removed);
        var pairedDirectKeys = new HashSet<string>(StringComparer.Ordinal);
        var arrivedInputs = new List<NoticeInput>();
        var missedInputs = new List<NoticeInput>();
        for (int i = 0; i < paired.Count; i++)
        {
            // An event-log notice was already handed over. Re-send it only if this full read has now found its replacement,
            // so the hub can merge the old notice without presenting the same toast twice (NoticeInput.Redelivery).
            if (ordered[i].Direct && paired[i].Replaces == ordered[i].Input.Replaces) continue;
            if (ordered[i].Direct && paired[i].InstanceKey is { } pairedKey) pairedDirectKeys.Add(pairedKey);
            (ordered[i].Missed ? missedInputs : arrivedInputs).Add(ordered[i].Direct ? paired[i] with { Redelivery = true } : paired[i]);
        }
        foreach (var id in _observed.Where(pair => pair.Value.Generation <= readGeneration || pair.Value.Input?.InstanceKey is { } key && pairedDirectKeys.Contains(key)).Select(pair => pair.Key).ToList())
            _observed.Remove(id);
        return new(arrivedInputs, missedInputs, left);
    }

    private static readonly Changes Empty = new([], [], []);

    // Whether a notice's application id may be handed to the shell (shell:AppsFolder\<id>) to open the application: letters,
    // digits and the punctuation application ids use (package family names, "!App", "{GUID}\\folder\\app.exe"); no commas,
    // slashes, colons, quotes, ".." or leading dashes, which the shell would read as switches or paths.
    public static bool IsAppId(string appId)
        => appId.Length is > 0 and <= 512 && appId[0] is not ('-' or '/' or '\\' or '.') && !appId.Contains("..", StringComparison.Ordinal)
           && appId.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '!' or '{' or '}' or '-' or '\\' or ' ' or '+' or '(' or ')' or '~');
}
