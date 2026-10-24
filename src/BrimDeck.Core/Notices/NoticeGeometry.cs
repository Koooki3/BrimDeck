namespace BrimDeck.Core.Notices;

// The outline a notice takes, in DIPs relative to the monitor's top-left corner, like CollapsedShape.Surface. The notch keeps
// its shoulders and stays attached to the screen edge; the capsule and the indicator become a rounded pill (or card) anchored
// where they are.
public sealed record NoticeFrame(DipRect Rect, double TopRadius, double BottomRadius, double Shoulder);

public enum NoticeForm { Toast, Detail }

public static class NoticeGeometry
{
    // Width and height ranges of an arriving notice for each collapsed style, and of the details card.
    public static (double MinWidth, double MaxWidth, double MinHeight, double MaxHeight) ToastLimits(CompactStyle style) => style switch
    {
        CompactStyle.Notch => (280, 420, 44, 64),
        CompactStyle.Capsule => (260, 400, 44, 60),
        _ => (200, 340, 40, 52)
    };
    public const double DetailMinWidth = 360, DetailMaxWidth = 460, DetailMinHeight = 120, DetailMaxHeight = 420;
    public const double DetailRadius = 24, ScreenMargin = 8;

    // The frame for content that wants the given size. Toasts are never narrower or shorter than the collapsed surface they
    // grow from (the notch and the capsule), so the morph only ever widens; every frame stays on the monitor and, for the
    // capsule and the indicator, within the work area.
    public static NoticeFrame Frame(CollapsedShape shape, DipRect monitor, DipRect work, NoticeForm form, double wantedWidth, double wantedHeight)
    {
        var surface = shape.Surface;
        bool notch = shape.Style == CompactStyle.Notch;
        double width, height;
        if (form == NoticeForm.Toast)
        {
            var (minW, maxW, minH, maxH) = ToastLimits(shape.Style);
            width = Math.Clamp(Finite(wantedWidth, minW), minW, maxW);
            height = Math.Clamp(Finite(wantedHeight, minH), minH, maxH);
            if (shape.Style != CompactStyle.Line) { width = Math.Max(width, surface.Width); height = Math.Max(height, surface.Height); }
        }
        else
        {
            width = Math.Clamp(Finite(wantedWidth, DetailMinWidth), DetailMinWidth, DetailMaxWidth);
            height = Math.Clamp(Finite(wantedHeight, DetailMinHeight), DetailMinHeight, DetailMaxHeight);
        }
        double monitorWidth = Math.Max(0, monitor.Width);
        width = Math.Min(width, Math.Max(0, monitorWidth - 2 * ScreenMargin));
        double centre = surface.X + surface.Width / 2;
        double left = Math.Clamp(centre - width / 2, Math.Min(ScreenMargin, monitorWidth / 2), Math.Max(ScreenMargin, monitorWidth - ScreenMargin - width));
        // An island about as wide as the screen already reaches into the margins: a toast covers it exactly instead of
        // narrowing it (sync L7), so no end of the island shows beside the toast or shrinks back when it leaves.
        if (form == NoticeForm.Toast && shape.Style != CompactStyle.Line && surface.Width > width)
        {
            width = Math.Min(surface.Width, monitorWidth);
            left = Math.Clamp(surface.X, 0, Math.Max(0, monitorWidth - width));
        }
        double top;
        if (notch) top = 0;
        else
        {
            // Anchored at the surface's top; near the bottom of the work area it grows upward instead.
            double bottomLimit = Math.Min(work.Y + work.Height, monitor.Height) - ScreenMargin;
            height = Math.Min(height, Math.Max(0, bottomLimit - Math.Max(0, work.Y)));
            top = Math.Clamp(surface.Y, Math.Max(0, work.Y), Math.Max(Math.Max(0, work.Y), bottomLimit - height));
        }
        if (notch && form == NoticeForm.Detail) height = Math.Min(height, Math.Max(0, Math.Min(work.Y + work.Height, monitor.Height) - ScreenMargin));
        double radius = form == NoticeForm.Detail ? DetailRadius : height / 2;
        return notch
            ? new NoticeFrame(new DipRect(left, top, width, height), 0, Math.Min(form == NoticeForm.Detail ? DetailRadius : 20, height / 2), shape.Shoulder)
            : new NoticeFrame(new DipRect(left, top, width, height), radius, radius, 0);
    }

    // The frame matching the collapsed surface itself, where every notice starts and ends.
    public static NoticeFrame Collapsed(CollapsedShape shape) => new(shape.Surface, shape.TopRadius, shape.BottomRadius, shape.Shoulder);

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}

// Which panel shows an arriving notice. Panels are identified by their lane name; the monitor ids are opaque handles.
public static class NoticeRouting
{
    public readonly record struct Candidate(string Lane, long Monitor, bool Main, bool Available);

    // With NoticeScreen.Foreground, the panel on the foreground window's monitor if it can show the notice, otherwise the main
    // panel. Null when the chosen panel cannot show anything (hidden by a full-screen rule, or by the user): the notice then
    // waits in the recent notices; it does not jump to another monitor.
    public static string? Choose(IReadOnlyList<Candidate> panels, long? foregroundMonitor, NoticeScreen screen)
    {
        if (panels.Count == 0) return null;
        var main = panels.FirstOrDefault(p => p.Main);
        if (main.Lane is null) main = panels[0];
        if (screen == NoticeScreen.Foreground && foregroundMonitor is { } monitor && panels.FirstOrDefault(p => p.Monitor == monitor) is { Lane: not null } match)
            return match.Available ? match.Lane : null;
        return main.Available ? main.Lane : null;
    }
}
