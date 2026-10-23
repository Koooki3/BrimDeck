using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// Time-kind notices on the island: the words about their time ("还剩 24:59", "14 分钟后开始") take the summary's line, a
// countdown shows how far it has run as a ring, and the details add the time and what BrimDeck can do (stop, cancel, remind
// again). While a live one shows, the window follows NoticeCenter's shared second tick and changes only those words and the
// ring; nothing else is rebuilt, and nothing ticks once it ends.
internal sealed partial class NoticeWindow
{
    private TextBlock? _liveLine, _liveDetail;
    private FrameworkElement? _liveRing;
    private bool _watchingLive;

    // The words about a time-kind notice's time; they replace its summary. Null keeps the notice's own summary.
    private static string? LiveWords(Notice? notice) => NoticeClock.Live(notice?.Timing, DateTimeOffset.UtcNow, TimeZoneInfo.Local);

    // The summary's line, when it shows the live words, so that the tick changes its text only.
    private void KeepLiveLine(TextBlock? line) => _liveLine = line is not null && LiveWords(Notice) is { } words && line.Text == words ? line : null;

    // A countdown's ring: how far it has run.
    private FrameworkElement? CountdownRing(Notice? notice)
    {
        _liveRing = null;
        if (NoticeClock.Fraction(notice?.Timing, DateTimeOffset.UtcNow) is not { } fraction) return null;
        var ring = UI.Ring(fraction * 100, AccentHex, 18, 2.2);
        ring.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(ring, LiveWords(notice) ?? "");
        _liveRing = ring;
        return ring;
    }

    private bool HasCountdownRing(Notice? notice) => NoticeClock.Fraction(notice?.Timing, DateTimeOffset.UtcNow) is not null;

    // The details' line with the live words, under the title.
    private TextBlock? LiveDetailLine(Notice notice)
    {
        _liveDetail = null;
        if (LiveWords(notice) is not { } words) return null;
        var line = Line(words, 12.5, AccentHex, FontWeights.SemiBold);
        line.Margin = new Thickness(0, 4, 0, 0);
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Off);
        _liveDetail = line;
        return line;
    }

    // Buttons for what BrimDeck itself can do with the notice (NoticeClock.Actions). The center updates the shown notice in
    // place afterwards (stopped, cancelled) or ends it (reminded again later).
    private void AddTimeActions(Panel actions, Notice notice)
    {
        foreach (var action in Center.TimeActions(notice))
            actions.Children.Add(TextButton(action.Label, () => Center.TimeAction(notice, action.Key), primary: false));
    }

    // Follows the shared tick while the shown notice is live; called whenever the shown item changes or ends.
    private void WatchLive()
    {
        bool live = _item is not null && !_leaving && NoticeClock.IsLive(Notice?.Timing, DateTimeOffset.UtcNow);
        if (live == _watchingLive) return;
        _watchingLive = live;
        if (live) Center.LiveTick += LiveStep; else Center.LiveTick -= LiveStep;
    }

    private void LiveStep()
    {
        var notice = Notice;
        if (notice is null || _leaving) { WatchLive(); return; }
        var words = LiveWords(notice);
        if (words is not null)
        {
            if (_liveLine is not null) _liveLine.Text = words;
            if (_liveDetail is not null) _liveDetail.Text = words;
        }
        if (_liveRing is { Parent: Grid grid } old && CountdownRing(notice) is { } ring)
        {
            Grid.SetColumn(ring, Grid.GetColumn(old));
            int index = grid.Children.IndexOf(old);
            grid.Children.RemoveAt(index); grid.Children.Insert(index, ring);
        }
        // Ended (the countdown reached its end, the event started): the last words stay; the tick is let go.
        WatchLive();
    }
}
