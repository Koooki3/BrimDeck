using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// Time-kind notices on the recent notices page: each row says how its time stands ("还剩 24:59", "已于 15:30 结束"). Rows of
// live ones follow NoticeCenter's shared second tick while the page shows; only their words change, the page is not rebuilt.
public partial class MainWindow
{
    private readonly List<(TextBlock Line, Notice Notice)> _liveRows = [];
    private bool _pageLive;

    // The row's line about its time, or null for notices without one.
    private TextBlock? NoticeTimeLine(Notice notice, DateTimeOffset now)
    {
        if (NoticeClock.Live(notice.Timing, now, TimeZoneInfo.Local) is not { } words) return null;
        var line = UI.Line(words, 11.5, UI.Accent);
        line.FontFamily = UI.PanelFont;
        if (NoticeClock.IsLive(notice.Timing, now)) _liveRows.Add((line, notice));
        return line;
    }

    // Follows the tick while the page shows live rows; let go as soon as it does not.
    private void WatchPageLive()
    {
        bool live = _liveRows.Count > 0 && _expanded && CurrentPage == DeckPage.Notices;
        if (live == _pageLive) return;
        _pageLive = live;
        if (live) _app.Notices.LiveTick += PageLiveStep; else _app.Notices.LiveTick -= PageLiveStep;
    }

    private void PageLiveStep()
    {
        if (!_expanded || CurrentPage != DeckPage.Notices) _liveRows.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var (line, notice) in _liveRows.ToList())
        {
            if (NoticeClock.Live(notice.Timing, now, TimeZoneInfo.Local) is { } words) line.Text = words;
            if (!NoticeClock.IsLive(notice.Timing, now)) _liveRows.RemoveAll(row => ReferenceEquals(row.Line, line));
        }
        WatchPageLive();
    }
}
