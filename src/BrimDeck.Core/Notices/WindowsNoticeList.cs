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
    // A reading has been compared since the list was last cleared, so a Resume baseline has something to compare with.
    private bool _read;

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

    // Forgets the list: after starting, or when access was revoked (the list then reads as empty, which must not look like
    // every notice being removed).
    public void Reset()
    {
        _known.Clear(); _read = false;
        Newest = 0; Confirmed = false; Baseline = WindowsNoticeBaseline.Start; UnknownWait = FirstUnknownWait;
    }

    public void ExpectBaseline(WindowsNoticeBaseline baseline)
    {
        if (baseline > Baseline) Baseline = baseline;
        // Ids may have moved on while asleep: the unconfirmed newest is checked often again.
        if (baseline != WindowsNoticeBaseline.None) UnknownWait = FirstUnknownWait;
    }

    // The ids of a reading not seen yet, the ones Compare will ask to read.
    public List<uint> Unknown(IEnumerable<uint> ids) => ids.Where(id => !_known.ContainsKey(id)).Distinct().ToList();

    // Compares a reading (the listed ids in Windows' order) with what was seen. read gives a new id's notice, or null for one
    // that cannot be read or is not handed over; it is called once per new id. Arrived and Missed are oldest first, so the
    // newest ends up on top of the recent notices; a notice that took the place of a removed one names it (Replaces).
    public Changes Compare(IEnumerable<uint> ids, Func<uint, NoticeInput?> read)
    {
        var baseline = Baseline == WindowsNoticeBaseline.Resume && !_read ? WindowsNoticeBaseline.Start : Baseline;
        var added = new List<(NoticeInput Input, bool Missed)>();
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
            if (input is not null && baseline != WindowsNoticeBaseline.Start) added.Add((input, baseline == WindowsNoticeBaseline.Resume));
        }
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
        Baseline = WindowsNoticeBaseline.None; _read = true;
        var ordered = added.OrderBy(a => a.Input.CreatedAt).ToList();
        var (paired, left) = NoticeReplacements.Pair(ordered.Select(a => a.Input).ToList(), removed);
        return new(paired.Where((_, i) => !ordered[i].Missed).ToList(), paired.Where((_, i) => ordered[i].Missed).ToList(), left);
    }

    // Whether a notice's application id may be handed to the shell (shell:AppsFolder\<id>) to open the application: letters,
    // digits and the punctuation application ids use (package family names, "!App", "{GUID}\\folder\\app.exe"); no commas,
    // slashes, colons, quotes, ".." or leading dashes, which the shell would read as switches or paths.
    public static bool IsAppId(string appId)
        => appId.Length is > 0 and <= 512 && appId[0] is not ('-' or '/' or '\\' or '.') && !appId.Contains("..", StringComparison.Ordinal)
           && appId.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '!' or '{' or '}' or '-' or '\\' or ' ' or '+' or '(' or ')' or '~');
}
