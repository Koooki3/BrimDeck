namespace BrimDeck.Core;

public enum MusicLyricsLayout { Stacked, Columns }

// Where the artist's column of the stacked layout sits when the cover is not shown: against the page's left edge or centred.
public enum MusicLyricsAlign { Left, Center }

// Type sizes and spacing of the music page. Each value is given at the three size presets and at the largest panel
// (density 0, 1, 2 and 3) and runs linearly between them, so a panel between two presets gets sizes between theirs.
// Bottom is the margin below the transport glyphs and equals Side, so the page has the same inset on three sides.
public sealed record MusicMetrics(double Toolbar, double Top, double Bottom, double Side, double Gap, double MinColumn,
    double Title, double TitleLine, double Artist, double ArtistLine, double Lyric, double LyricLine, double MiddleGap,
    double Time, double TimeLine, double SeekRow, double Bar, double BarHover, double SectionGap, double ControlsGap,
    double Play, double Skip, double Mode, double Equalizer)
{
    public static MusicMetrics At(double density)
    {
        double d = Math.Clamp(density, 0, 3);
        int i = Math.Min(2, (int)d);
        double Value(double compact, double standard, double spacious, double largest)
        {
            double[] values = [compact, standard, spacious, largest];
            return values[i] + (values[i + 1] - values[i]) * (d - i);
        }
        double Whole(double compact, double standard, double spacious, double largest) => Math.Round(Value(compact, standard, spacious, largest), MidpointRounding.AwayFromZero);
        double Half(double compact, double standard, double spacious, double largest) => Math.Round(Value(compact, standard, spacious, largest) * 2, MidpointRounding.AwayFromZero) / 2;
        double side = Whole(16, 20, 24, 28);
        return new(
            Toolbar: Whole(28, 36, 40, 40), Top: Whole(4, 8, 10, 12), Bottom: side, Side: side,
            Gap: Whole(18, 24, 30, 36), MinColumn: Whole(200, 220, 260, 300),
            Title: Half(15.5, 19, 25, 31), TitleLine: Whole(21, 25, 32, 40), Artist: Half(12, 13, 15.5, 19), ArtistLine: Whole(16, 18, 21, 26),
            Lyric: Half(12, 12.5, 14, 16.5), LyricLine: Whole(16, 19, 23, 28), MiddleGap: Whole(4, 8, 12, 16),
            Time: Half(10, 11, 11.5, 12.5), TimeLine: Whole(12, 15, 16, 18), SeekRow: Whole(12, 16, 18, 20), Bar: Half(3, 4, 4, 5), BarHover: Whole(6, 8, 8, 10),
            SectionGap: Whole(6, 10, 14, 18), ControlsGap: Whole(2, 6, 8, 10),
            Play: Whole(24, 30, 38, 44), Skip: Whole(18, 22, 28, 32), Mode: Whole(14, 17, 21, 24), Equalizer: Whole(12, 15, 19, 23));
    }
}

// The music page has two parts. The upper part holds the cover with the title, artist and lyrics beside it; the lower
// part holds the progress bar with the times below its ends, then the transport buttons. Everything shares one left
// and one right edge: the cover, the bar and the elapsed time start at the left; the text, the bar and the duration end
// at the right. Positions are relative to the page body, the panel without its toolbar and margins.
public sealed record MusicLayout(MusicMetrics Metrics, double Density, double BodyWidth, double BodyHeight, double Cover, double CoverY, double CoverRadius,
    double TextX, double TextWidth, bool MergeTitle, double HeaderHeight, double MiddleHeight, int LyricLines, bool ShowMessage,
    double ProgressY, double ProgressHeight, double TimesY, double ControlsY, double ControlGap, double TripletX, double TripletWidth,
    double ModeX, bool ShowFullControl, double FullControlRoom)
{
    // The entry label shortens step by step to the room right of the transport.
    // In English the second step keeps the meaning in two words; "En…" in Segoe UI is narrower than "启…".
    public static string[] FullControlLabels => Loc.IsEnglish ? ["Enable full control", "Full control", "Enable…", "En…"] : ["启用完整控制", "启用完整…", "启用…", "启…"];
    // The chrome is 6 padding and a 1 border on each side. The minimum holds the shortest label at the largest (13) type.
    public const double FullControlGap = 8, FullControlChrome = 14, FullControlMinimum = 38;
    // Height sets the density, from the smallest preset (0) to the largest panel (3). Width caps it: the narrowest
    // panel goes no further than the standard preset, and the cap reaches the largest density at the spacious width,
    // so a tall, narrow panel does not get type its title column cannot hold.
    public static double DensityFor(double width, double height)
    {
        var (compact, standard, spacious) = (MusicSizes.For(PanelSize.Compact), MusicSizes.For(PanelSize.Standard), MusicSizes.For(PanelSize.Spacious));
        double byHeight = Math.Clamp(height <= standard.Height ? (height - compact.Height) / (standard.Height - compact.Height)
            : 1 + (height - standard.Height) / (spacious.Height - standard.Height), 0, 3);
        double byWidth = Math.Clamp(1 + 2 * (width - standard.Width) / (spacious.Width - standard.Width), 1, 3);
        return Math.Min(byHeight, byWidth);
    }
    // The height of the upper part, above the progress bar; the cover is centred in it.
    public double UpperHeight { get; init; }
    // The lyric lines the room beside the cover holds, whether or not there are lyrics to show in them.
    public int LyricRoom => Math.Max(0, (int)Math.Floor((MiddleHeight - Metrics.MiddleGap) / Metrics.LyricLine));
    // lyricsExpected lays out the titles as if lyrics were shown, so they keep their place while lyrics are still looked up,
    // or when a song has none.
    public static MusicLayout Calculate(double width, double height, bool lyrics, bool timeline, bool message = false, bool fullControlEntry = false, bool transport = true,
        bool lyricsExpected = false)
    {
        double density = DensityFor(width, height);
        var c = MusicMetrics.At(density);
        double bodyW = width - 2 * c.Side, bodyH = height - c.Toolbar - c.Top - c.Bottom;
        // The transport glyphs occupy 3..21 of a 24-unit square; their ink ends at the body bottom.
        double controlsY = Math.Round(bodyH - c.Play * 7 / 8), inkTop = controlsY + c.Play / 8;
        // The times sit just below the bar, clear of it while the hovered bar is thicker.
        double timesY = Math.Round(inkTop - c.ControlsGap - c.TimeLine);
        double progressY = Math.Round(timesY - c.BarHover / 2 - 2 - c.SeekRow / 2), barTop = progressY + (c.SeekRow - c.Bar) / 2;
        double upper = Math.Floor((timeline ? barTop : inkTop) - c.SectionGap);
        double cover = Math.Max(40, Math.Min(upper, bodyW - c.Gap - c.MinColumn));
        double coverY = Math.Floor((upper - cover) / 2), textX = cover + c.Gap, textW = bodyW - textX;
        // The text beside the cover is as tall as the cover. Title and artist take a line each unless the lyrics or the
        // message would then have no line left. Below them sit the current lyric, then the next one, then the one before.
        double separate = c.TitleLine + c.ArtistLine;
        double Room(double header) => cover - header - c.MiddleGap;
        bool below = lyrics || lyricsExpected || message;
        bool merge = below ? Room(separate) < c.LyricLine : cover < separate;
        double headH = merge ? c.TitleLine : separate, room = Room(headH);
        // The room under the titles holds as many lines as the room allows: it is filled rather than capped at three.
        int lines = !lyrics ? 0 : Math.Max(0, (int)Math.Floor(room / c.LyricLine));
        bool showMessage = message && room >= c.LyricLine;
        // The play button sits at the centre of the panel, with previous and next spread out with the panel's width
        // rather than packed beside it. The mode button is left of previous, with the same gap as between the others.
        double gap = Math.Round(Math.Clamp(bodyW * .14, c.Play * 1.2, c.Play * 2.6));
        double tripletW = c.Skip * 2 + c.Play + 2 * gap;
        double tripletX = Math.Floor((bodyW - tripletW) / 2 + .5), modeX = tripletX - gap - c.Mode;
        // Netease and Apple Music without full control offer the entry at the right end of the transport row. The entry
        // never moves the transport; the window shortens its label to the room right of the buttons.
        double fullControlRoom = !fullControlEntry ? 0 : transport ? bodyW - (tripletX + tripletW) - FullControlGap : bodyW;
        bool enable = fullControlRoom >= FullControlMinimum;
        if (!enable) fullControlRoom = 0;
        double radius = Math.Clamp(Math.Round(cover * .08), 5, 16);
        return new(c, density, bodyW, bodyH, cover, coverY, radius, textX, textW, merge, headH, cover - headH, lines, showMessage,
            timeline ? progressY : 0, timeline ? c.SeekRow : 0, timesY, controlsY, gap, tripletX, tripletW, modeX, enable, fullControlRoom) { UpperHeight = upper };
    }

    // In the column layout the upper part has two columns, so the titles and the lyrics are not read as one block: the title,
    // artist and album centred in the left one, and the lyrics right of a thin divider. The left column takes about a third of
    // the width; a wider panel gives the lyrics the rest. The lyrics fill the upper part's height, the current line a size
    // larger, with as many lines before it as after it. An even count gives the extra line to the lines still to come, so the
    // current line stays as high as the room allows. Without lyrics the titles are centred across the whole width.
    //
    // Without the cover the equalizer is under the titles, in the middle of their column (EqualizerBelow), where the height
    // holds it; the album's own line gives way to it first. A part too short for that leaves it at the upper part's top right
    // corner, where the one-column layout has it.
    //
    // The cover, when shown, stands above the titles and comes before what is under them: it takes the height the title and one
    // line of artist and album leave, no wider than the column and no larger than the one-column layout's cover. The equalizer
    // is at the top right corner then and takes no height. The album's own line comes back only where the cover still has
    // CoverShare of the height with it. A part too short for a cover of that share above the titles has it beside them instead,
    // as tall as the part and no wider than .42 of the column, and the titles read from the left there; the album then follows
    // the height.
    public const double CoverShare = .45, MinimumColumnCover = 24;
    public MusicColumns Columns(bool lyrics, bool cover = false)
    {
        var c = Metrics;
        double height = Math.Max(0, UpperHeight), gap = c.Gap;
        double info = lyrics ? Math.Round(Math.Clamp(BodyWidth * .36, 120, 480), MidpointRounding.AwayFromZero) : BodyWidth;
        double lyricsX = lyrics ? info + 2 * gap + 1 : BodyWidth, lyricsW = Math.Max(0, BodyWidth - lyricsX);
        double currentFont = Math.Round(c.Lyric * 1.15 * 2, MidpointRounding.AwayFromZero) / 2, currentLine = Math.Round(c.LyricLine * 1.15, MidpointRounding.AwayFromZero);
        // The current line is a size larger; the rest of the height holds as many lines as fit, above and below it.
        int lines = !lyrics ? 0 : 1 + Math.Max(0, (int)Math.Floor((height - currentLine) / c.LyricLine));
        double bars = c.SectionGap + c.Equalizer;
        bool below = !cover && c.TitleLine + c.ArtistLine + bars <= height;
        // The current line sits as high in the room as it can; the lines after it take the extra one when the count is even,
        // so the lines still to come read below it. The equalizer no longer decides which side gets the extra line.
        int before = Math.Max(0, lines - 1) / 2, after = Math.Max(0, lines - 1 - before);
        bool album = c.TitleLine + 2 * c.ArtistLine + (below ? bars : 0) <= height;
        double above = 0, beside = 0;
        if (cover)
        {
            double widest = Math.Min(Cover, info);
            double Above(bool twoLines) => Math.Floor(Math.Min(widest, height - c.TitleLine - (twoLines ? 2 : 1) * c.ArtistLine - c.MiddleGap));
            double wanted = Math.Max(MinimumColumnCover, Math.Min(widest, Math.Round(height * CoverShare)));
            double side = Math.Floor(Math.Min(height, info * .42));
            if (Above(false) >= wanted) { album = Above(true) >= wanted; above = Above(album); }
            else if (side >= MinimumColumnCover && info >= side + c.MiddleGap + 40) beside = side;
            else if (Above(false) >= MinimumColumnCover) { album = false; above = Above(false); }
        }
        return new(height, info, gap, lyricsX, lyricsW, currentFont, currentLine, before, after, album, below, c.SectionGap) { Cover = above, SideCover = beside };
    }
}

// The two columns of the music page's column layout (MusicLayout.Columns). Widths and positions are relative to the page body.
public sealed record MusicColumns(double Height, double InfoWidth, double DividerGap, double LyricsX, double LyricsWidth,
    double CurrentLyric, double CurrentLine, int Before, int After, bool ShowAlbum, bool EqualizerBelow, double EqualizerGap)
{
    public int LyricLines => LyricsWidth > 0 ? Before + 1 + After : 0;
    // The cover's size above the titles, or beside them; at most one is set, and neither without a cover or the room for one.
    public double Cover { get; init; }
    public double SideCover { get; init; }
}

// What the open music page's title needs worked out outside the window. The title's room is decided by the page's grid (the line
// less the equalizer's column, EqualizerWidth + EqualizerGap) and whether it scrolls by the MusicMarquee itself, which measures
// its text against its room with Overflow; this holds the equalizer's width from the bar count (3 to 20), that measure, and the
// name a screen reader reads.
public static class MusicTitleFit
{
    // The gap between the title's room and the equalizer.
    public const double EqualizerGap = 16;
    // Five bars span 1.7 times their height (at least 27); other counts keep the five bars' spacing.
    public static double EqualizerWidth(double equalizerHeight, int bars)
    {
        double fiveBars = Math.Max(27, Math.Round(equalizerHeight * 1.7));
        return Math.Round(3 + (CompactMusicLayout.SafeBars(bars) - 1) * (fiveBars - 3) / 4);
    }
    // Centred without the cover, the title has the page's whole line and the equalizer stands over its right end. The title keeps
    // this far from both ends of the line: the equalizer and its gap at the right, and as much at the left, so the room left for
    // it is centred on the page. Both follow the equalizer's width, which grows with the bar count and the page's type size.
    public static double CenteredInset(double equalizerWidth) => Math.Max(0, equalizerWidth) + EqualizerGap;
    public static double CenteredRoom(double line, double equalizerWidth) => Math.Max(0, line - 2 * CenteredInset(equalizerWidth));
    // What a screen reader reads for the title: the whole title, and the artist when the short page merges it into the line.
    public static string ReadAs(string title, string tail = "") => Loc.T("歌名 ", "Title ") + title + tail;
    // How far the text overhangs its room, in whole device pixels (scale: device pixels per DIP). The small allowance keeps
    // rounding noise from starting a one-pixel scroll.
    public static double Overflow(double textWidth, double room, double scale)
    {
        if (scale <= 0 || double.IsNaN(textWidth) || double.IsNaN(room)) return 0;
        return Math.Max(0, Math.Ceiling((textWidth - room) * scale - .01) / scale);
    }
}

public enum MusicPlaybackMode { List, Track, Shuffle, Order }

public static class MusicPlaybackModes
{
    public static MusicPlaybackMode Current(MediaTrack track) => track.Shuffle ? MusicPlaybackMode.Shuffle : FromRepeat(track.Repeat);
    private static MusicPlaybackMode FromRepeat(MediaRepeat repeat) => repeat switch
    { MediaRepeat.List => MusicPlaybackMode.List, MediaRepeat.Track => MusicPlaybackMode.Track, _ => MusicPlaybackMode.Order };
    public static IReadOnlyList<MusicPlaybackMode> Supported(MediaTrack track)
    {
        if (!track.CanRepeat && !track.CanShuffle) return [];
        return Enum.GetValues<MusicPlaybackMode>().Where(mode => mode == MusicPlaybackMode.Shuffle ? track.CanShuffle
            : track.CanRepeat && (!track.Shuffle || track.CanShuffle) || track.CanShuffle && mode == FromRepeat(track.Repeat)).ToArray();
    }
    public static MusicPlaybackMode Next(MediaTrack track)
    {
        var modes = Supported(track).ToList();
        return modes.Count == 0 ? Current(track) : modes[(modes.IndexOf(Current(track)) + 1) % modes.Count];
    }
    public static string Label(MusicPlaybackMode mode) => mode switch
    { MusicPlaybackMode.List => Loc.T("列表循环", "Repeat all"), MusicPlaybackMode.Track => Loc.T("单曲循环", "Repeat one"), MusicPlaybackMode.Shuffle => Loc.T("随机播放", "Shuffle"), _ => Loc.T("顺序播放", "In order") };
}