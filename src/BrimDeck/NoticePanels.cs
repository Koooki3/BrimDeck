using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrimDeck;

// The notices page's two lines that must keep their words at any width of the panel: the list's header and a row's text.
internal static class NoticeLines
{
    // A line of the page that is cut by its own size rather than by the page's: the header drops its count under the title when the
    // three do not fit side by side, and a row moves its time under the title when the title would be left too short to read.
    public const double HeadLine = 20, HeadGap = 2, CountLine = 15, CountLines = 2;
    public const double RowTitleLine = 18, RowPreviewLine = 17, RowGap = 1, RowTitleMinimum = 88, RowTimeReserve = 52;
    public const double Gap = 6;
    // The opacity (0 to 255) of the glow in the application's color behind the focus column. Text on it (the status line, the
    // category, the letter plate) keeps its contrast there, so it stays faint.
    public const byte GlowAlpha = 28;

    // Whether a row's time sits at the right of its title: when the title keeps at least RowTitleMinimum beside a time of the usual
    // width. It depends on the row's width alone, not on its words, so every row of the list is laid out the same way.
    public static bool TimeBesideTitle(double width) => width - Gap - RowTimeReserve >= RowTitleMinimum;

    // Whether the header's title, count and tools fit on one line.
    public static bool HeadFits(double width, double titleWidth, double countWidth, double toolsWidth)
        => titleWidth + Gap + countWidth + Gap + toolsWidth <= width;
}

// The list's header: the title with its count and, at the right, the tools (pause, all / unread). On one line while they fit; when
// they do not, the tools stay beside the title and the count, which carries the pause time, wraps under them instead of being cut.
// Children in order: title, count, tools.
internal sealed class NoticeHead : Panel
{
    private bool _stacked;

    protected override Size MeasureOverride(Size available)
    {
        var (title, count, tools) = (Children[0], Children[1], Children[2]);
        var open = new Size(double.PositiveInfinity, double.PositiveInfinity);
        title.Measure(open); count.Measure(open); tools.Measure(open);
        double width = double.IsInfinity(available.Width) ? title.DesiredSize.Width + NoticeLines.Gap + count.DesiredSize.Width + NoticeLines.Gap + tools.DesiredSize.Width : available.Width;
        _stacked = !NoticeLines.HeadFits(width, title.DesiredSize.Width, count.DesiredSize.Width, tools.DesiredSize.Width);
        if (!_stacked) return new Size(width, NoticeLines.HeadLine);
        // The title gives way to the tools; the count has the whole width and at most two lines.
        title.Measure(new Size(Math.Max(0, width - tools.DesiredSize.Width - NoticeLines.Gap), NoticeLines.HeadLine));
        count.Measure(new Size(width, NoticeLines.CountLine * NoticeLines.CountLines));
        return new Size(width, NoticeLines.HeadLine + NoticeLines.HeadGap + count.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (title, count, tools) = (Children[0], Children[1], Children[2]);
        double toolsX = Math.Max(0, final.Width - tools.DesiredSize.Width);
        tools.Arrange(new Rect(toolsX, 0, tools.DesiredSize.Width, NoticeLines.HeadLine));
        if (!_stacked)
        {
            title.Arrange(new Rect(0, 0, title.DesiredSize.Width, NoticeLines.HeadLine));
            // The count sits a hair low, on the title's baseline as it did beside it.
            count.Arrange(new Rect(title.DesiredSize.Width + NoticeLines.Gap, 1, count.DesiredSize.Width, NoticeLines.HeadLine - 1));
            return final;
        }
        title.Arrange(new Rect(0, 0, Math.Min(title.DesiredSize.Width, Math.Max(0, toolsX - NoticeLines.Gap)), NoticeLines.HeadLine));
        count.Arrange(new Rect(0, NoticeLines.HeadLine + NoticeLines.HeadGap, final.Width, count.DesiredSize.Height));
        return final;
    }
}

// What a row says: the title and when, and a line of preview. The time sits at the right of the title; when that would leave the
// title under RowTitleMinimum it moves to the front of the preview line, so the title is read whole. Children in order: title,
// time, preview.
internal sealed class NoticeRowTexts : Panel
{
    private bool _beside;

    protected override Size MeasureOverride(Size available)
    {
        var (title, time, preview) = (Children[0], Children[1], Children[2]);
        var open = new Size(double.PositiveInfinity, double.PositiveInfinity);
        title.Measure(open); time.Measure(open);
        double width = double.IsInfinity(available.Width) ? title.DesiredSize.Width + NoticeLines.Gap + time.DesiredSize.Width : available.Width;
        _beside = NoticeLines.TimeBesideTitle(width);
        double timeWidth = time.DesiredSize.Width + NoticeLines.Gap;
        title.Measure(new Size(_beside ? Math.Max(0, width - timeWidth) : width, NoticeLines.RowTitleLine));
        preview.Measure(new Size(_beside ? width : Math.Max(0, width - timeWidth), NoticeLines.RowPreviewLine));
        return new Size(width, NoticeLines.RowTitleLine + NoticeLines.RowGap + NoticeLines.RowPreviewLine);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (title, time, preview) = (Children[0], Children[1], Children[2]);
        double timeWidth = time.DesiredSize.Width;
        double secondY = NoticeLines.RowTitleLine + NoticeLines.RowGap;
        if (_beside)
        {
            time.Arrange(new Rect(Math.Max(0, final.Width - timeWidth), 0, timeWidth, NoticeLines.RowTitleLine));
            title.Arrange(new Rect(0, 0, Math.Max(0, final.Width - timeWidth - NoticeLines.Gap), NoticeLines.RowTitleLine));
            preview.Arrange(new Rect(0, secondY, final.Width, NoticeLines.RowPreviewLine));
        }
        else
        {
            title.Arrange(new Rect(0, 0, final.Width, NoticeLines.RowTitleLine));
            time.Arrange(new Rect(0, secondY, timeWidth, NoticeLines.RowPreviewLine));
            preview.Arrange(new Rect(timeWidth + NoticeLines.Gap, secondY, Math.Max(0, final.Width - timeWidth - NoticeLines.Gap), NoticeLines.RowPreviewLine));
        }
        return final;
    }
}

// The focus's buttons: delete and the actions flow from the left and wrap to a new line where one does not fit; with MainLast, the
// last child (the main button) sits at the right end of the last line it lands on. On one line this is what a dock panel gave; at
// the narrowest page, where the buttons did not fit, they are all reached instead of the later ones being cut off.
internal sealed class NoticeButtons : Panel
{
    public const double LineGap = 8;
    public bool MainLast { get; set; }
    private readonly List<Point> _at = [];

    protected override Size MeasureOverride(Size available)
    {
        _at.Clear();
        double x = 0, y = 0, line = 0, widest = 0;
        var open = new Size(double.PositiveInfinity, double.PositiveInfinity);
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(open);
            var (w, h) = (child.DesiredSize.Width, child.DesiredSize.Height);
            if (x > 0 && x + w > available.Width) { y += line + LineGap; x = 0; line = 0; }
            // The main button, last, goes to the right end of its line (or, on a line of its own, of the row).
            double at = MainLast && i == Children.Count - 1 && !double.IsInfinity(available.Width) ? Math.Max(x, available.Width - w) : x;
            _at.Add(new Point(at, y));
            x += w; line = Math.Max(line, h); widest = Math.Max(widest, at + w);
        }
        return new Size(double.IsInfinity(available.Width) ? widest : available.Width, y + line);
    }

    protected override Size ArrangeOverride(Size final)
    {
        for (int i = 0; i < Children.Count && i < _at.Count; i++)
            Children[i].Arrange(new Rect(_at[i], Children[i].DesiredSize));
        return final;
    }
}

// A scrolling column that fades out at its foot into the panel, whatever the panel is made of: the fade is a mask on the viewer
// (the content shows through it less and less), not a color laid over it that would have to match the surface, its gradient, its
// texture or the glass. The content leaves the same height free below it, so scrolled to its end the last line is not in the fade.
internal static class NoticeFade
{
    public static void Foot(ScrollViewer viewer, double height)
    {
        // Only the alpha of the mask's colors counts: opaque down to the fade, transparent at the foot.
        var shown = new GradientStop(Color.FromArgb(255, 0, 0, 0), 1);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0));
        mask.GradientStops.Add(shown);
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        void Fit() => shown.Offset = viewer.ActualHeight > height ? 1 - height / viewer.ActualHeight : 0;
        viewer.SizeChanged += (_, _) => Fit();
        Fit();
        viewer.OpacityMask = mask;
    }
}
