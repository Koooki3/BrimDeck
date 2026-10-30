using BrimDeck.Core;
using BrimDeck.Core.Notices;

// Round-2 fixes of group "win2": the Windows source's reading pace and its retries, and the stored icons' use mark.
internal static class AuditWin2Tests
{
    internal static void Run(Action<string, bool> check)
    {
        Pace(check);
        Icons(check);
    }

    private static void Pace(Action<string, bool> check)
    {
        var t0 = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
        var quiet = TimeSpan.FromSeconds(15);
        // C-L8: brisk at first, after any activity, and for a few minutes after it; backed off only after a quiet stretch.
        check("Audit win2: the unconfirmed list is read every 3 s after starting", WindowsNoticePace.Unconfirmed(t0, t0, quiet) == TimeSpan.FromSeconds(3));
        check("Audit win2: still every 3 s a few minutes on", WindowsNoticePace.Unconfirmed(t0.AddMinutes(4.9), t0, quiet) == TimeSpan.FromSeconds(3));
        check("Audit win2: backed off only after a quiet stretch", WindowsNoticePace.QuietAfter >= TimeSpan.FromMinutes(2)
            && WindowsNoticePace.Unconfirmed(t0 + WindowsNoticePace.QuietAfter, t0, quiet) == quiet);
        check("Audit win2: activity makes it brisk again", WindowsNoticePace.Unconfirmed(t0.AddMinutes(30), t0.AddMinutes(29), quiet) == TimeSpan.FromSeconds(3));
        check("Audit win2: the brisk pace is the old first wait", WindowsNoticeList.FirstUnknownWait == TimeSpan.FromSeconds(3));

        // P2: 2.5 s, doubling after each failure in a row, never more than a minute.
        check("Audit win2: the first retry is the probe's 2.5 s", WindowsNoticePace.Retry(1) == TimeSpan.FromSeconds(2.5) && WindowsNoticePace.Retry(0) == TimeSpan.FromSeconds(2.5));
        check("Audit win2: retries double", WindowsNoticePace.Retry(2) == TimeSpan.FromSeconds(5) && WindowsNoticePace.Retry(3) == TimeSpan.FromSeconds(10)
            && WindowsNoticePace.Retry(5) == TimeSpan.FromSeconds(40));
        check("Audit win2: retries stop at a minute", WindowsNoticePace.Retry(6) == TimeSpan.FromSeconds(60) && WindowsNoticePace.Retry(1000) == TimeSpan.FromSeconds(60));
    }

    private static void Icons(Action<string, bool> check)
    {
        var now = new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc);
        // P5: reading an icon again marks it at most once a day.
        check("Audit win2: an icon marked today is not marked again", !NoticeIconStore.NeedsMark(now.AddHours(-23), now) && !NoticeIconStore.NeedsMark(now, now));
        check("Audit win2: an icon marked a day ago is marked", NoticeIconStore.NeedsMark(now.AddDays(-1), now) && NoticeIconStore.NeedsMark(now.AddDays(-40), now));
        check("Audit win2: a mark in the future (clock set back) is renewed", NoticeIconStore.NeedsMark(now.AddDays(3), now) && !NoticeIconStore.NeedsMark(now.AddSeconds(5), now));
    }
}
