namespace BrimDeck.Core;

public enum MediaState { Stopped, Paused, Playing, Unknown }
public enum MediaRepeat { None, Track, List }
// Like adds the current song to the player's own liked songs, or removes it.
public enum MediaCommand { PlayPause, Previous, Next, Shuffle, Repeat, Like }
public sealed record MediaTrack
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string SongId { get; init; } = "";
    public string EmbeddedLyrics { get; init; } = "";
    public string? CurrentLyric { get; init; }
    public TimeSpan? CurrentLyricStart { get; init; }
    public bool IsQqMusic => Source == "QQ 音乐" || SongId.StartsWith("qq:", StringComparison.Ordinal) || SongId.StartsWith("qqid:", StringComparison.Ordinal);
    public MediaState State { get; init; }
    public bool IsNeteaseLog { get; init; }
    public bool PreviousRestricted { get; init; }
    public bool PositionKnown { get; init; } = true;
    public bool TimelinePending { get; init; }
    public bool HasTimeline => PositionKnown && Duration > TimeSpan.Zero;
    public bool CanToggle { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanNext { get; init; }
    public bool CanSeek { get; init; }
    public bool CanShuffle { get; init; }
    public bool CanRepeat { get; init; }
    public bool Shuffle { get; init; }
    public MediaRepeat Repeat { get; init; }
    // Only a player connection that both reports and changes the liked state offers it; Liked is meaningful only with CanLike.
    public bool CanLike { get; init; }
    public bool Liked { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public TimeSpan MinSeek { get; init; }
    public TimeSpan MaxSeek { get; init; }
    public TimeSpan ReportedPosition { get; init; }
    public DateTimeOffset PositionAt { get; init; } = DateTimeOffset.UtcNow;
    public double Rate { get; init; } = 1;
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
    public string SongKey => $"{Title}\n{Artist}\n{Album}\n{Math.Round(Duration.TotalSeconds)}";
    public string Caption => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} · {Artist}";
    public TimeSpan Position(DateTimeOffset now)
    {
        if (!HasTimeline) return TimeSpan.Zero;
        double elapsed = State == MediaState.Playing ? Math.Max(0, (now - PositionAt).TotalSeconds) * (double.IsFinite(Rate) ? Rate : 1) : 0;
        return TimeSpan.FromSeconds(Math.Clamp((ReportedPosition - Start).TotalSeconds + elapsed, 0, Duration.TotalSeconds));
    }
    public long SeekTicks(double fraction)
    {
        var target = Start + TimeSpan.FromSeconds(Duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
        var min = MinSeek > Start ? MinSeek : Start;
        var max = MaxSeek > min && MaxSeek <= End ? MaxSeek : End;
        return Math.Clamp(target.Ticks, min.Ticks, Math.Max(min.Ticks, max.Ticks));
    }
}

// All layout decisions depend on available content width, including future custom compact sizes.
public sealed record CompactMusicLayout(double MiddleWidth, double RingSize, double RingGap, int VisibleRings, double RingWidth, double TextWidth, bool ScrollRings)
{
    // The narrowest scrolling lyric: about one and a half characters. Rings and lyric both stay down to this width.
    private const double MinimumText = 18;
    public static CompactMusicLayout Calculate(double contentWidth, bool media, int rings, bool text)
    {
        double middle = Math.Max(0, contentWidth - (media ? 54 : 0));
        rings = Math.Max(0, rings);
        double size = 12, gap = 8;
        static double Span(int count, double size, double gap) => count == 0 ? 0 : count * size + (count - 1) * gap;
        double desiredText = text ? 56 : 0;
        double between = rings > 0 && text ? 8 : 0;
        if (Span(rings, size, gap) + between + desiredText > middle) { size = 10; gap = 5; }
        int visible = rings;
        if (Span(rings, size, gap) + between + desiredText > middle)
        {
            // Once rotating, leave most of the middle area to the scrolling lyric.
            double ringBudget = text ? Math.Min(middle * .4, middle - desiredText - between) : middle;
            visible = Math.Clamp((int)Math.Floor((ringBudget + gap) / (size + gap)), 0, rings);
            // Below its preferred width the lyric still scrolls, so one rotating ring stays beside it.
            if (text && visible == 0 && rings > 0 && middle - size - between >= MinimumText) visible = 1;
        }
        double ringWidth = Span(visible, size, gap);
        double textWidth = text ? Math.Max(0, middle - ringWidth - (visible > 0 ? 8 : 0)) : 0;
        if (text && textWidth < MinimumText)
        {
            textWidth = 0; size = 10; gap = 5;
            visible = Math.Clamp((int)Math.Floor((middle + gap) / (size + gap)), 0, rings);
            ringWidth = Span(visible, size, gap);
        }
        return new(middle, size, gap, visible, ringWidth, textWidth, visible < rings);
    }
}

public static class MusicSizes
{
    public static (double Width, double Height) For(PanelSize size) => size switch
    { PanelSize.Compact => (440, 140), PanelSize.Spacious => (760, 300), _ => (520, 200) };
}
