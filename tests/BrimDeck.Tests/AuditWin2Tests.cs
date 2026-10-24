using BrimDeck.Core;
using BrimDeck.Core.Notices;

// Round-2 fixes of group "win2": the Windows source's reading pace and its retries, the stored icons' use mark, and the quota
// rings' cycle, which now runs on a timer through its rests with the old storyboard's timings.
internal static class AuditWin2Tests
{
    internal static void Run(Action<string, bool> check)
    {
        Pace(check);
        Icons(check);
        QuotaRings(check);
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

    private static void QuotaRings(Action<string, bool> check)
    {
        const double travel = 34;
        var cycle = new QuotaCycle(travel);
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        // The storyboard's values: rests of 2.5 s and 2 s, 12 DIP/s on the (.4, 0, .6, 1) spline, fades of .25 s, and a duration
        // of reset + 2 x fade.
        double end = 2.5 + travel / 12, reset = end + 2;
        check("Audit win2: quota cycle keeps the storyboard's timings", QuotaCycle.StartHold == 2.5 && QuotaCycle.EndHold == 2 && QuotaCycle.Speed == 12 && QuotaCycle.Fade == .25
            && (QuotaCycle.CurveX1, QuotaCycle.CurveY1, QuotaCycle.CurveX2, QuotaCycle.CurveY2) == (.4, 0, .6, 1)
            && Near(cycle.GlideEnd, end) && Near(cycle.ResetStart, reset) && Near(cycle.Period, reset + .5));
        // The shift's keyframes: 0 at 0 and at the start hold, -travel at the glide's end and until reset + fade, then 0.
        check("Audit win2: quota rings rest at the first ring", cycle.Shift(0) == 0 && cycle.Shift(2.49) == 0 && cycle.Opacity(1) == 1);
        check("Audit win2: quota rings glide on the spline", Near(cycle.Shift(2.5 + (end - 2.5) / 2), -travel / 2)
            && cycle.Shift(2.5 + (end - 2.5) * .25) > -travel * .25 && Near(cycle.Shift(end), -travel));
        check("Audit win2: quota rings rest at the last ring", Near(cycle.Shift(end + 1), -travel) && cycle.Opacity(end + 1) == 1 && Near(cycle.Shift(reset + .2), -travel));
        check("Audit win2: quota rings fade out, jump back and fade in", Near(cycle.Opacity(reset + .125), .5) && Near(cycle.Opacity(reset + .25), 0)
            && cycle.Shift(reset + .25) == 0 && Near(cycle.Opacity(reset + .375), .5) && Near(cycle.Opacity(cycle.Period - 1e-9), 1));
        // Only the glide and the reset run a clock; the rests (4.5 s of every cycle) are timer-only.
        check("Audit win2: quota rests run no clock", cycle.StretchAt(1) == (false, 0, 2.5) && cycle.StretchAt(end + .5) == (false, end, reset));
        check("Audit win2: quota moves run on a clock", cycle.StretchAt(3) == (true, 2.5, end) && cycle.StretchAt(reset + .1) == (true, reset, reset + .5));
        check("Audit win2: quota cycle wraps", Near(cycle.Wrap(cycle.Period + 1), 1) && Near(cycle.Wrap(-1), cycle.Period - 1));
    }
}
