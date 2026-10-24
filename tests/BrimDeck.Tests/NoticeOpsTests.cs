using BrimDeck.Core.Notices;

// Follow-up work on the notices after the audits: how the Windows source probes for new notices when tiles and badges take
// ids from the same count.
internal static class NoticeOpsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    internal static void Run(Action<string, bool> check)
    {
        Probe(check);
    }

    private static NoticeInput Toast(uint id) => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = "windows:App", AppName = "App", InstanceKey = "w:" + id, Title = "t" + id, CreatedAt = T0.AddSeconds(id)
    };

    private static void Probe(Action<string, bool> check)
    {
        check("Notices ops: the probe looks at the next id only", WindowsNoticePace.ProbeIds(100, T0, DateTimeOffset.MinValue).SequenceEqual([101u]));
        check("Notices ops: after a skipped id the probe looks a few ids ahead", WindowsNoticePace.ProbeIds(100, T0, T0.AddMinutes(1)).SequenceEqual([101u, 102u, 103u]));
        check("Notices ops: the wide probe ends", WindowsNoticePace.ProbeIds(100, T0.AddMinutes(1), T0.AddMinutes(1)).SequenceEqual([101u]));
        check("Notices ops: the probe never wraps past the largest id", WindowsNoticePace.ProbeIds(uint.MaxValue - 1, T0, T0.AddMinutes(1)).SequenceEqual([uint.MaxValue]));

        var list = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        list.Compare([10u], id => Toast(id));
        check("Notices ops: the first reading says nothing about skipped ids", !list.SkippedProbe);
        list.Compare([10u, 11u], id => Toast(id));
        check("Notices ops: a notice right after the newest skips nothing", !list.SkippedProbe && list.Confirmed && list.Newest == 11);
        list.Compare([10u, 11u, 13u], id => Toast(id));
        check("Notices ops: a notice past a taken id is seen to skip it", list.SkippedProbe && list.Newest == 13);
        list.Compare([10u, 11u, 13u], id => Toast(id));
        check("Notices ops: a reading with nothing new clears it", !list.SkippedProbe);
        list.Compare([10u, 11u, 13u, 14u, 16u], id => Toast(id));
        check("Notices ops: two notices in a row starting at the next id skip nothing", !list.SkippedProbe);

        var unconfirmed = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        unconfirmed.Compare([10u], id => Toast(id));
        unconfirmed.Compare([10u, 20u], id => Toast(id));
        check("Notices ops: before the newest id is confirmed a gap means nothing", !unconfirmed.SkippedProbe);
        list.ExpectBaseline(WindowsNoticeBaseline.Resume);
        list.Compare([10u, 11u, 13u, 14u, 16u, 30u], id => Toast(id));
        check("Notices ops: what arrived during sleep is no skipped id", !list.SkippedProbe);

        check("Notices ops: a reading may start when none is out", WindowsNoticePace.MayRead([], T0));
        check("Notices ops: a reading waits for one still out", !WindowsNoticePace.MayRead([T0.AddMinutes(-1)], T0));
        check("Notices ops: a reading hung for minutes no longer blocks (L4)", WindowsNoticePace.MayRead([T0 - WindowsNoticePace.StuckAfter], T0));
        check("Notices ops: hung readings never pile up", !WindowsNoticePace.MayRead([T0.AddHours(-2), T0.AddHours(-1)], T0)
            && !WindowsNoticePace.MayRead([T0.AddHours(-2), T0.AddMinutes(-1)], T0.AddMinutes(1)));
    }
}
