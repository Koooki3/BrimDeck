namespace BrimDeck.Core.Notices;

// The icons kept beside the saved notices (NoticeIcons): a stored icon's time of last writing says when it was last used, so
// the least recently used go first. Reading one again only marks it when the mark is a day old or more (win2 P5): an icon
// used every few minutes then costs one metadata write a day instead of one per read.
public static class NoticeIconStore
{
    public static readonly TimeSpan MarkEvery = TimeSpan.FromDays(1);

    // A mark in the future (the clock was set back) is renewed too, or the icon would look recently used for good.
    public static bool NeedsMark(DateTime lastWriteUtc, DateTime nowUtc) => nowUtc - lastWriteUtc >= MarkEvery || lastWriteUtc - nowUtc > TimeSpan.FromMinutes(1);
}
