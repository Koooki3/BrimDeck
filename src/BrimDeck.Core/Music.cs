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

// The collapsed row, left to right: the cover, the quota rings, the lyric or title, the system readings, the equalizer and the
// clock, with one gap between neighbours. Every part but the title (which scrolls) is shown whole or not at all. Parts are taken
// in priority order while they fit: first what the row must show (the clock; while media plays, the cover, the equalizer and
// the title at its shortest), then the quota rings one by one, then the system readings one by one in the order they stay
// (CompactMonitor.Priority). The first part that does not fit ends the row, so a wider row only ever adds parts and a narrower
// one only removes them. The title takes the space that is left; without a title that space is free. Where the parts then
// stand, evenly spaced, is CompactSpacing's.
// Sizes follow the height through Scale (1 at the default 32 DIP height) and are whole DIPs; font sizes are whole or half DIPs.
public sealed record CompactMusicLayout(double MiddleWidth, double RingSize, double RingGap, int VisibleRings, double RingWidth, double TextWidth, bool ScrollRings)
{
    // The shortest title the row keeps while media plays: about four characters, scrolling. The surface grows to hold it; a row
    // that still cannot gives the title what is left, down to ScrollingText (about one and a half characters).
    public const double MinimumText = 48, ScrollingText = 18;
    public double Scale { get; init; } = 1;
    public double ContentWidth { get; init; }
    public double RowHeight { get; init; } = 24;
    // The space between two neighbouring parts.
    public double Gap { get; init; } = 8;
    // Each part's width when shown, 0 when not.
    public double CoverWidth { get; init; }
    public double CoverSize { get; init; } = 20;
    public double CoverRadius { get; init; } = 4;
    public double EqualizerWidth { get; init; }
    public double EqualizerSize { get; init; } = 18;
    public double EqualizerHeight { get; init; } = 14;
    public double EqualizerBar { get; init; } = 3;
    // The system readings (CompactMonitor): the first MonitorItems of those offered, in their priority order, with their own gaps.
    public double MonitorWidth { get; init; }
    public int MonitorItems { get; init; }
    public double ClockWidth { get; init; }
    public double ClockFace { get; init; }
    // The gap between the rings and the title, while both show.
    public double Between { get; init; }
    // Width left over when there is no title to take it.
    public double Free { get; init; }
    // The width the parts the row must show take (Required), and the width every part takes with the title at its shortest
    // (Natural). The collapsed surface never gets narrower than Required (CollapsedGeometry.Resolve).
    public double Required { get; init; }
    public double Natural { get; init; }
    public double RingThickness { get; init; } = 2;
    // Fade beside rotating rings and at the lyric's edges.
    public double RingEdge { get; init; } = 3;
    public double TextEdge { get; init; } = 6;
    public double TextFont { get; init; } = 11.5;
    public double TextHeight { get; init; } = 22;
    public bool TextScrolls { get; init; }

    public static double Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
    public static double Half(double value) => Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;
    // Any scale is accepted; non-numbers and non-positive values mean the default size.
    public static double SafeScale(double scale) => double.IsFinite(scale) && scale > 0 ? Math.Clamp(scale, .25, 8) : 1;
    private static double Safe(double width) => double.IsFinite(width) && width > 0 ? width : 0;

    // The width a row with this content takes with every part shown and the title at its shortest. A row with system readings
    // grows to it (CollapsedGeometry.Resolve), as ftop's strip fits its readings.
    public static double NaturalWidth(double scale, bool media, int rings, bool text, double clockFace, double monitorFace)
        => Calculate(0, scale, media, rings, text, clockFace, monitorFace).Natural;

    // contentWidth here excludes the clock, as before custom sizes: the cover, middle and equalizer share it.
    public static CompactMusicLayout Calculate(double contentWidth, bool media, int rings, bool text)
        => Calculate(contentWidth, 1, media, rings, text, 0);

    // monitorFace: the system readings as one part of this width, or 0 without them.
    public static CompactMusicLayout Calculate(double contentWidth, double scale, bool media, int rings, bool text, double clockFace, double monitorFace = 0)
        => Calculate(contentWidth, scale, media, rings, text, clockFace, Safe(monitorFace) > 0 ? [monitorFace] : [], 0);

    // contentWidth is the whole row in DIPs; clockFace is the clock's reserved width at this scale, or 0 without a clock.
    // monitorItems: the width of each system reading on offer, in the order they stay when the row is short; monitorGap is the
    // space between two readings.
    public static CompactMusicLayout Calculate(double contentWidth, double scale, bool media, int rings, bool text, double clockFace,
        IReadOnlyList<double> monitorItems, double monitorGap)
    {
        double s = SafeScale(scale), width = Safe(contentWidth), face = Safe(clockFace), inner = Safe(monitorGap);
        rings = Math.Max(0, rings);
        double cover = Math.Max(1, Whole(20 * s)), gap = Whole(8 * s);
        double eqW = Math.Max(1, Whole(18 * s)), eqH = Math.Max(1, Whole(14 * s)), bar = Math.Max(1, Whole(3 * s));
        double size = Math.Max(1, Whole(12 * s)), minimum = Whole(MinimumText * s);
        var readings = monitorItems ?? [];

        // Every part in priority order, as its width and the space before it when something already stands in the row (a ring
        // or a reading after another of its kind uses its own spacing). Required parts come first.
        double used = 0, required = 0, natural = 0; int count = 0, naturalCount = 0; bool open = true;
        bool Take(double part, double before, double after, bool must)
        {
            natural += part + (naturalCount > 0 ? after : 0); naturalCount++;
            if (must) required = natural;
            if (!open) return false;
            double need = part + (count > 0 ? before : 0);
            if (used + need > width + 1e-9) { open = false; return false; }
            used += need; count++;
            return true;
        }
        bool clockShown = face > 0 && Take(face, gap, gap, true);
        bool coverShown = media && Take(cover, gap, gap, true);
        bool eqShown = media && Take(eqW, gap, gap, true);
        bool textShown = text && Take(minimum, gap, gap, true);
        // The title alone may show in part, since it scrolls: a row too short for its shortest still gives it what is left, down
        // to about one and a half characters, and then holds nothing more.
        double textFloor = minimum;
        if (text && !textShown && (face == 0 || clockShown) && (!media || eqShown))
        {
            double left = width - used - (count > 0 ? gap : 0);
            if (left >= Whole(ScrollingText * s) - 1e-9) { textShown = true; textFloor = left; used = width; count++; }
        }
        int ringsShown = 0, readingsShown = 0;
        for (int i = 0; i < rings; i++) if (Take(size, gap, gap, false)) ringsShown++;
        for (int i = 0; i < readings.Count; i++)
        {
            double reading = Safe(readings[i]);
            // The first reading stands a gap from its neighbour; the others its own spacing from the reading before.
            if (Take(reading, i == 0 ? gap : inner, i == 0 ? gap : inner, false)) readingsShown++;
        }
        double rest = Math.Max(0, width - used);
        double textWidth = textShown ? textFloor + rest : 0;
        double ringWidth = ringsShown == 0 ? 0 : ringsShown * size + (ringsShown - 1) * gap;
        double monitorWidth = 0;
        for (int i = 0; i < readingsShown; i++) monitorWidth += Safe(readings[i]) + (i > 0 ? inner : 0);
        double between = ringsShown > 0 && textShown ? gap : 0;
        return new(ringWidth + between + textWidth, size, gap, ringsShown, ringWidth, textWidth, ringsShown > 0 && ringsShown < rings)
        {
            Scale = s, ContentWidth = width, RowHeight = Math.Max(1, Whole(24 * s)), Gap = gap,
            CoverWidth = coverShown ? cover : 0, CoverSize = cover, CoverRadius = Whole(4 * s),
            EqualizerWidth = eqShown ? eqW : 0, EqualizerSize = eqW, EqualizerHeight = eqH, EqualizerBar = bar,
            ClockWidth = clockShown ? face : 0, ClockFace = face,
            MonitorWidth = monitorWidth, MonitorItems = readingsShown,
            Between = between, Free = textShown ? 0 : rest, Required = required, Natural = natural,
            RingThickness = Math.Max(.5, Half(2 * s)), RingEdge = Whole(3 * s), TextEdge = Whole(6 * s),
            TextFont = Math.Max(1, Half(11.5 * s)), TextHeight = Math.Max(1, Whole(22 * s)), TextScrolls = textShown
        };
    }
}

// The quota notice that replaces the collapsed row: ring, application name, "used" text and reset time, centred.
// As in the collapsed row, each part shows whole or not at all. The used text always stays; then the ring, the name and the
// reset time are taken in turn while they fit, and the first that does not fit ends the row. Only a row too short for the used
// text alone cuts it.
public sealed record CompactAlertLayout(double Scale, double RingSize, double RingThickness, double FontSize, double LineHeight, double Gap,
    double RingWidth, double NameWidth, double TextWidth, double ResetWidth)
{
    public double Total => RingWidth + NameWidth + TextWidth + ResetWidth;
    // The name's natural width is capped at 76 DIP at the default height.
    public static CompactAlertLayout Calculate(double contentWidth, double scale, double name, double text, double reset)
    {
        double s = CompactMusicLayout.SafeScale(scale);
        static double Safe(double width) => double.IsFinite(width) && width > 0 ? width : 0;
        double width = Safe(contentWidth);
        double ring = Math.Max(1, CompactMusicLayout.Whole(14 * s)), gap = CompactMusicLayout.Whole(8 * s);
        double nameW = Math.Min(Safe(name), CompactMusicLayout.Whole(76 * s));
        // The ring and the name each hold the gap after them, the reset time the gap before it.
        double textR = Math.Min(Safe(text), width), left = width - textR;
        bool open = true;
        double Take(double region)
        {
            if (!open || region <= 0) return 0;
            if (region > left + 1e-9) { open = false; return 0; }
            left -= region; return region;
        }
        double ringR = Take(ring + gap), nameR = nameW > 0 ? Take(nameW + gap) : 0, resetR = Safe(reset) > 0 ? Take(gap + Safe(reset)) : 0;
        return new(s, ring, 2.2 * s, Math.Max(1, CompactMusicLayout.Half(11.5 * s)), Math.Max(1, CompactMusicLayout.Whole(16 * s)), gap, ringR, nameR, textR, resetR);
    }
}

public static class MusicSizes
{
    public static (double Width, double Height) For(PanelSize size) => size switch
    { PanelSize.Compact => (440, 140), PanelSize.Spacious => (760, 300), _ => (440, 200) };
}
