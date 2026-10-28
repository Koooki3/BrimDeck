namespace BrimDeck.Core;

// The island's compact content giving way to another row in place: a quota alert arriving over the row, changing level, or ending so
// that the row comes back (MainWindow.RowSwap.cs plays this on WPF animations; this is its model, which the tests drive frame by
// frame). The island keeps its shape; only what it holds changes. The row on show leaves in the first 30 percent of the move, the
// next lands from 30 to 90 percent, so the two are never on show together, and both follow the same soft curve as the rest of the
// panel's content (Soft.Out). Without this the row was replaced in one frame, and the alert's end showed as a flash back to the row.
public static class RowSwap
{
    // The share of the move in which the row leaves, and the share in which the next lands.
    public const double LeaveStart = 0, LeaveEnd = .3, LandStart = .3, LandEnd = .9;

    // How long a swap takes: the length chosen for the panel's animations when the island is drawing the row (animations on, not
    // powering off or booting, the panel closed, not the indicator line), otherwise 0, and the row changes at once.
    public static int Length(int animationDuration, bool animate, bool drawn) => animate && drawn ? Math.Clamp(animationDuration, 100, 1000) : 0;

    // A row changes when the alert it was built for is not the one now (none counts as one): the same alert again keeps its row.
    public static bool Changes(object? shown, object? wanted) => !ReferenceEquals(shown, wanted);

    // How much of a move between start and end has been played at a point of the swap (0 to 1), on the soft curve.
    private static double Played(double start, double end, double progress)
        => Curves.Bezier(.22, .8, .28, 1, end <= start ? progress >= end ? 1 : 0 : Math.Clamp((progress - start) / (end - start), 0, 1));

    // The opacity of the row leaving (from where it was) and of the row landing at a point of the swap, progress 0 to 1.
    public static (double Leaving, double Landing) Opacity(double progress, double leavingFrom = 1)
        => (leavingFrom * (1 - Played(LeaveStart, LeaveEnd, progress)), Played(LandStart, LandEnd, progress));
}
