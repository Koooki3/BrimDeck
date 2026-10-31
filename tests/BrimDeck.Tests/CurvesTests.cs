using BrimDeck.Core;

// The curves behind the panel's timed animations (BrimDeck.Soft).
internal static class CurvesTests
{
    public static void Run(Action<string, bool> check)
    {
        double Out(double x) => Curves.Bezier(.22, .8, .28, 1, x);
        check("Curves: a Bézier curve starts at 0 and ends at 1", Out(0) == 0 && Math.Abs(Out(1) - 1) < 1e-9);
        // The soft landing is ahead of time from the start: past nine tenths halfway through, as the CSS curve is. Read the
        // other way round (EasingMode.EaseOut on EaseInCore) it would be at a few percent there.
        check("Curves: the soft landing is quick at first", Out(.25) > .65 && Out(.5) > .9);
        check("Curves: a CSS ease matches its published values", Math.Abs(Curves.Bezier(.25, .1, .25, 1, .5) - .8024) < .002);
        var samples = Enumerable.Range(0, 101).Select(i => Curves.Bezier(.45, 0, .12, 1, i / 100.0)).ToList();
        check("Curves: the settling curve rises steadily", samples.Zip(samples.Skip(1)).All(p => p.Second >= p.First - 1e-9));
        check("Curves: the settling curve starts slowly", samples[20] < .2);
        check("Curves: a fraction outside the move is held at its ends", Curves.Bezier(.22, .8, .28, 1, -1) == 0 && Math.Abs(Curves.Bezier(.22, .8, .28, 1, 2) - 1) < 1e-9);

        // The monitor's move: what the panel's content, selections and hover follow.
        var follow = Enumerable.Range(0, 101).Select(i => Curves.Follow(i / 100.0)).ToList();
        check("Curves: the monitor's move starts at 0 and ends at 1", follow[0] == 0 && follow[^1] == 1);
        check("Curves: the monitor's move rises steadily and never passes its end", follow.Zip(follow.Skip(1)).All(p => p.Second >= p.First - 1e-12) && follow.All(v => v <= 1));
        check("Curves: the monitor's move leaves at one and a half times its average speed", Math.Abs(follow[1] / .01 - 1.5) < .01);
        var eased = new Eased(0);
        eased.Set(1, true, 100);
        check("Curves: the monitor's move is the one its readings follow (Eased)", Enumerable.Range(0, 21).All(i => Math.Abs(eased.At(100 + Eased.Seconds * i / 20) - Curves.Follow(i / 20.0)) < 1e-9));

        var bounce = Enumerable.Range(0, 1001).Select(i => Curves.Bounce(i / 1000.0)).ToList();
        check("Curves: the opening spring starts at 0 and lands exactly on 1", bounce[0] == 0 && bounce[^1] == 1);
        check("Curves: the opening spring leaves at speed", bounce[100] > .45);
        double peak = bounce.Max();
        // The give was softened from 1.7 percent to about .3 (ExpandBounceTests holds the exact bounds).
        check("Curves: the opening spring passes its target only slightly (under half a percent)", peak > 1.001 && peak < 1.005 && Math.Abs(peak - 1 - Curves.BounceOvershoot) < .0005);
        check("Curves: the opening spring has settled before the end", bounce.Skip(900).All(v => Math.Abs(v - 1) < .005));
    }
}
