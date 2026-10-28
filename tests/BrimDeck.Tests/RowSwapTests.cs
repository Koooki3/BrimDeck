using BrimDeck.Core;

// A quota alert coming, changing or ending swaps the island's compact row in place (MainWindow.RowSwap.cs). It used to replace the
// row in one frame (the flash back when the alert ended); the swap is now a move of its own whose frames are checked here: the row
// on show leaves, the next one lands, and no frame shows the old row again or both at once.
internal static class RowSwapTests
{
    // The opacities (leaving, landing) of every display frame of a swap of the given length, the first frame being the one the row
    // changes in and the last the first at or after its end.
    private static List<(double Leaving, double Landing)> Frames(int length, double hertz, double leavingFrom = 1)
    {
        var frames = new List<(double, double)>();
        for (int frame = 0; ; frame++)
        {
            double milliseconds = frame * 1000 / hertz;
            frames.Add(RowSwap.Opacity(Math.Min(1, milliseconds / length), leavingFrom));
            if (milliseconds >= length) break;
        }
        return frames;
    }

    public static void Run(Action<string, bool> check)
    {
        check("Row swap: the row leaves before the next lands, and the move spans the whole length",
            RowSwap.LeaveStart == 0 && RowSwap.LeaveEnd <= RowSwap.LandStart && RowSwap.LandStart < RowSwap.LandEnd && RowSwap.LandEnd <= 1);
        check("Row swap: the length is the setting's, none when the island is not drawing the row or animations are off",
            RowSwap.Length(360, true, true) == 360 && RowSwap.Length(360, true, false) == 0 && RowSwap.Length(360, false, true) == 0
            && RowSwap.Length(5, true, true) == 100 && RowSwap.Length(5000, true, true) == 1000);
        var alert = new object();
        check("Row swap: a new alert or none swaps the row, the same alert keeps it",
            RowSwap.Changes(null, alert) && RowSwap.Changes(alert, null) && RowSwap.Changes(alert, new object()) && !RowSwap.Changes(alert, alert) && !RowSwap.Changes(null, null));

        foreach (var hertz in new[] { 60.0, 144.0 })
            foreach (var length in new[] { 100, 240, 360, 1000 })
            {
                var frames = Frames(length, hertz);
                string name = $"Row swap at {hertz:0} Hz over {length} ms";
                check(name + ": starts on the old row and ends on the new one", frames[0] == (1, 0) && frames[^1] == (0, 1));
                bool leavingFalls = true, landingRises = true, never = true, inRange = true;
                for (int i = 1; i < frames.Count; i++)
                {
                    leavingFalls &= frames[i].Leaving <= frames[i - 1].Leaving + 1e-12;
                    landingRises &= frames[i].Landing >= frames[i - 1].Landing - 1e-12;
                }
                foreach (var (leaving, landing) in frames)
                {
                    never &= leaving <= 1e-9 || landing <= 1e-9;
                    inRange &= leaving is >= 0 and <= 1 && landing is >= 0 and <= 1;
                }
                check(name + ": the old row never comes back and the new one never fades again", leavingFalls && landingRises);
                check(name + ": the two rows are never on show together, and stay within 0 and 1", never && inRange);
                // The flash was one frame carrying the whole change. Now the change takes several frames, and no frame carries more
                // than the leaving row's first, which the curve makes quick (about half of it in a frame at 60 Hz).
                double step = 0; int moving = 0;
                for (int i = 1; i < frames.Count; i++)
                {
                    double d = Math.Max(Math.Abs(frames[i].Leaving - frames[i - 1].Leaving), Math.Abs(frames[i].Landing - frames[i - 1].Landing));
                    step = Math.Max(step, d); if (d > .02 * 60 / hertz) moving++;
                }
                // The shortest length the setting allows (100 ms) is two frames of leaving at 60 Hz: its steps are the curve's, only the count is held.
                check(name + $": spread over several frames (largest step {step:0.00}, {moving} moving)", (length < 200 || step < .8) && moving >= Math.Max(3, (int)(length * hertz / 1000 / 4)));
            }

        // A swap that follows another within the first third of its move leaves from where the row had got to (nothing shows
        // from a row that had not started to land), so no frame jumps up to full opacity first.
        var halfLanded = Frames(360, 60, leavingFrom: .4);
        check("Row swap: a row that was still landing leaves from its own opacity", halfLanded[0].Leaving == .4 && halfLanded.All(frame => frame.Leaving <= .4 + 1e-12));
    }
}
