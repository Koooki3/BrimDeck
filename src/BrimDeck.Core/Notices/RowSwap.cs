namespace BrimDeck.Core.Notices;

// The island's compact content giving way to another row in place: a quota alert arriving over the row, changing level, or ending
// so that the row comes back (MainWindow.RowSwap.cs plays this on WPF animations; this is its model, which the tests drive frame
// by frame). The island keeps its shape; only what it holds changes, as when a notice follows another in place: the row on show
// leaves on the panel's closing content track, the next lands on the track a following notice's text arrives on, and the two
// are never on show together. Both take Soft.Out's curve.
public static class RowSwap
{
    public static readonly NoticeMotion.Track Leave = NoticeMotion.ReturnContent, Land = NoticeMotion.FollowContent;

    // How long a swap takes: the panel's length when the island is drawing the row (animations on, not hidden behind a notice, the
    // panel open or the indicator line), otherwise 0, and the row changes at once.
    public static int Length(int animationDuration, bool animate, bool drawn) => drawn ? NoticeMotion.Length(animationDuration, animate) : 0;

    // A row changes when the alert it was built for is not the one now (none counts as one): the same alert again keeps its row.
    public static bool Changes(object? shown, object? wanted) => !ReferenceEquals(shown, wanted);

    // How much of its move a track has played at a point of the swap (0 to 1).
    private static double Played(NoticeMotion.Track track, double progress)
        => track.Length <= 0 ? progress >= track.End ? 1 : 0 : Math.Clamp((progress - track.Start) / track.Length, 0, 1);

    // The opacity of the row leaving (from where it was) and of the row landing at a point of the swap, progress 0 to 1.
    public static (double Leaving, double Landing) Opacity(double progress, double leavingFrom = 1)
        => (leavingFrom * (1 - MotionCurve.Ease(Played(Leave, progress), 0)), MotionCurve.Ease(Played(Land, progress), 0));
}
