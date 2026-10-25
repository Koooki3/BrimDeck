using BrimDeck.Core;
using BrimDeck.Core.Notices;

// The motion review's fixes that can be checked without WPF: the panel's closing and keyboard lengths (C1, C4), a move turned
// back part way (F1, F2), the answers to the pointer keeping their own pace (C5, F14), what fades out leaving at once (F13),
// the focus accent only after a pause (C7), and the date picker's fixed timings (C5).
internal static class MotionReviewTests
{
    internal static void Run(Action<string, bool> check)
    {
        // C1, C4.
        check("Motion review: the panel opens in the set time and closes in three quarters of it",
            NoticeMotion.PanelLength(360, closing: false, keyboard: false) == 360 && NoticeMotion.PanelLength(360, closing: true, keyboard: false) == 270
            && NoticeMotion.PanelLength(1000, closing: true, keyboard: false) == 750 && NoticeMotion.PanelLength(100, closing: true, keyboard: false) == 75);
        check("Motion review: a move the keyboard asks for takes 0.6 of the set time, opening or closing",
            NoticeMotion.PanelLength(360, closing: false, keyboard: true) == 216 && NoticeMotion.PanelLength(360, closing: true, keyboard: true) == 216
            && NoticeMotion.PanelLength(360, true, true) < NoticeMotion.PanelLength(360, true, false));
        check("Motion review: without animations every move takes no time", NoticeMotion.PanelLength(0, true, true) == 0 && NoticeMotion.PanelLength(0, false, false) == 0);

        // F1: turned back, the surface starts at once; otherwise the accepted slow, delayed start stays.
        check("Motion review: a closing that turns an opening around starts its surface at once",
            NoticeMotion.ReturnSurfaceFor(true) == new NoticeMotion.Track(0, 1) && NoticeMotion.ReturnSurfaceFor(false) == NoticeMotion.PanelCloseSurface
            && NoticeMotion.PanelCloseSurface.Start > 0);

        // F2: the share of a whole move a turned-back move takes.
        check("Motion review: turned back half way, a move takes half its time",
            Math.Abs(NoticeMotion.RemainingShare((300, 400, 200)) - .5) < 1e-9 && Math.Abs(NoticeMotion.RemainingShare((300, 200, 400)) - .5) < 1e-9);
        check("Motion review: turned back just after it started, it still takes a readable share",
            NoticeMotion.RemainingShare((205, 400, 200)) == NoticeMotion.RedirectMin && NoticeMotion.RedirectMin == .4);
        check("Motion review: turned back near its end, it takes nearly the whole move", NoticeMotion.RemainingShare((395, 400, 200)) is > .97 and <= 1
            && NoticeMotion.RemainingShare((420, 400, 200)) == 1);
        check("Motion review: the size that has most to undo decides, a size that does not change has no say",
            Math.Abs(NoticeMotion.RemainingShare((300, 400, 200), (90, 100, 40)) - (50.0 / 60)) < 1e-9
            && Math.Abs(NoticeMotion.RemainingShare((300, 400, 200), (36, 36, 36)) - .5) < 1e-9);
        check("Motion review: with nothing to compare, the whole move", NoticeMotion.RemainingShare((36, 36, 36)) == 1
            && NoticeMotion.RemainingShare((double.NaN, double.NaN, 200)) == 1 && NoticeMotion.RemainingShare() == 1);

        // F1, F2: only an opening or closing still under way is turned back (the panel's and a notice's own record, MoveTurn).
        var turn = new MoveTurn();
        turn.Started(1000, 360, opening: true, 480, 300);
        check("Motion review: closing well after the opening has ended is a plain closing", !turn.Turns(1000 + 360 + 300, false) && !turn.Turns(1360, false)
            && turn.Share(1660, false, 480, 300, 200, 36) == 1);
        check("Motion review: closing while the opening is under way turns it back, in the share it has to undo",
            turn.Turns(1150, false) && Math.Abs(turn.Share(1150, false, 340, 168, 200, 36) - .5) < 1e-9);
        check("Motion review: a move the same way as the one under way turns nothing back", !turn.Turns(1150, true) && turn.Share(1150, true, 340, 168, 480, 300) == 1);
        turn.Started(2000, NoticeMotion.PanelLength(360, closing: true, keyboard: false), opening: false, 200, 36);
        check("Motion review: opening after the closing has ended is a plain opening", !turn.Turns(2000 + 270, true) && !turn.Turns(2600, true)
            && turn.Share(2600, true, 200, 36, 480, 300) == 1);
        check("Motion review: opening while the closing is under way turns it back (a notice's details opened again too)",
            turn.Turns(2100, true) && turn.Share(2100, true, 400, 250, 480, 300) is >= .4 and < 1);
        // The collapsed surface laid out again mid-closing (a new collapsed width) carries the closing on.
        check("Motion review: a lay-out during a closing carries it on over what is left of it", turn.Carried(2100, false, 360) == 170
            && turn.Carried(2250, false, 360) == 144 && turn.Carried(2100, false, 0) == 0);
        check("Motion review: a lay-out after the closing, or against the move under way, is a move of its own",
            turn.Carried(2270, false, 360) == 0 && turn.Carried(2100, true, 360) == 0);
        check("Motion review: a closing is under way until its end, and only the closing", turn.Underway(2100, false) && !turn.Underway(2270, false)
            && !turn.Underway(2100, true));
        turn.Started(3000, 0, opening: true, 480, 300);
        check("Motion review: a move without animation leaves nothing to turn back", !turn.Turns(3000, false) && turn.Carried(3000, true, 360) == 0);

        // C1 for notices: a notice goes back as the panel closes, and Escape's way back is the keyboard's.
        check("Motion review: a notice goes back in three quarters of the time, Escape in 0.6 of it",
            NoticeMotion.ReturnLength(360, keyboard: false) == 270 && NoticeMotion.ReturnLength(360, keyboard: true) == 216 && NoticeMotion.ReturnLength(0, false) == 0
            && NoticeMotion.ReturnLength(360, false) == NoticeMotion.PanelLength(360, closing: true, keyboard: false));

        // F7: the picture beneath a pop-up waits for its entrance, and then follows changes after the theme's hover time.
        var popup = new PopupPicture();
        bool early = popup.Changed();
        popup.Enter();
        check("Motion review: a pop-up's picture is not taken before it has entered, and is after", !early && popup.Changed() && popup.Entered);
        check("Motion review: a pop-up's picture is taken again once the theme's hover has settled",
            PopupPicture.SettleMilliseconds(MotionTiming.For(1, 0)) == 200 && PopupPicture.SettleMilliseconds(MotionTiming.For(1.2, 0)) == 232
            && PopupPicture.SettleMilliseconds(MotionTiming.For(.8, 3)) == MotionTiming.For(.8, 3).HoverIn + 40);

        // C5, F14: the pointer's answers keep their own pace, whatever the panel's animation length.
        var smooth = MotionTiming.For(1, 0);
        check("Motion review: a smooth theme's hover comes and goes in the monitor's hover time",
            smooth.HoverIn == (int)Math.Round(Eased.Hover * 1000) && smooth.HoverOut == smooth.HoverIn && smooth.HoverIn == 160);
        check("Motion review: the theme's pace still scales it", MotionTiming.For(1.2, 0).HoverIn == 192 && MotionTiming.For(1.2, 0).HoverOut == 192
            && MotionTiming.For(.75, 0).HoverIn == 120);
        check("Motion review: the rest of the timings are the default length's",
            smooth with { HoverIn = 0, HoverOut = 0 } == MotionTiming.From(AnimationLength.Default, 1) with { HoverIn = 0, HoverOut = 0 }
            && MotionTiming.For(1, 0).Focus == MotionTiming.From(360, 1).Focus && MotionTiming.For(1, 0).PressIn == MotionTiming.From(360, 1).PressIn);
        var stepped = MotionTiming.For(.8, 3);
        check("Motion review: a stepped theme keeps its quick arrival and its slower, stepped way out",
            stepped == MotionTiming.From(AnimationLength.Default, .8) && stepped.HoverOut > stepped.HoverIn && stepped.HoverIn < smooth.HoverIn);
        check("Motion review: a pace out of range is held to it", MotionTiming.For(double.NaN, 0) == smooth && MotionTiming.For(9, 0).HoverIn == 256);

        // F13: what fades out leaves at once and slows, and is gone exactly at its end.
        check("Motion review: a hover mark leaves at once", MotionCurve.HoverOut(.1, 0) > .3 && MotionCurve.HoverOut(.5, 0) > .8);
        check("Motion review: a hover mark is gone exactly at its end", MotionCurve.HoverOut(1, 0) == 1 && MotionCurve.HoverOut((14 + .18 - 14) / .18, 0) == 1
            && MotionCurve.HoverOut(.5, 3) == MotionCurve.Step(.5, 3));
        check("Motion review: a press letting go and the focus accent fading keep their slow start",
            MotionCurve.EaseOut(.1, 0) < .1 && Math.Abs(MotionCurve.EaseOut(.3, 0) - Curves.Bezier(.45, 0, .25, 1, .3)) < 1e-12 && MotionCurve.EaseOut(1, 0) == 1);

        // C7: the focus accent only on the first move after a pause.
        var gate = new FocusAccentGate();
        bool first = gate.Take(10), second = gate.Take(10.3), third = gate.Take(11.2), afterPause = gate.Take(12.3);
        check("Motion review: Tab after Tab plays the focus accent once, then again only after a pause",
            first && !second && !third && afterPause);
        gate.Clear();
        check("Motion review: cleared, the next focus move plays it", gate.Take(12.4));

        // C5: the date picker keeps the timings it had at the default length.
        check("Motion review: the date picker moves at its own pace", DatePickerTiming.Fixed == DatePickerTiming.From(AnimationLength.Default)
            && DatePickerTiming.Fixed.Glide == 315 && DatePickerTiming.Fixed.Arrive == 420);
    }
}
