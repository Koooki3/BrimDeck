using BrimDeck.Core;
using BrimDeck.Protocol;
using BrimDeck.Core.Notices;

// The liquid motion of an arriving notice (NoticeLiquid): its timing, its bounds, its determinism and its ending.
internal static class NoticeLiquidTests
{
    internal static void Run(Action<string, bool> check)
    {
        Timing(check);
        Capsule(check);
        Notch(check);
        Line(check);
        Joined(check);
        Ending(check);
        Plans(check);
    }

    // How strongly it plays for each kind of notice (NoticeFx).
    private static void Plans(Action<string, bool> check)
    {
        var normal = NoticeFx.For(AttentionKind.Notice, NoticePriority.Normal, false, false, NoticeCategory.Message);
        var high = NoticeFx.For(AttentionKind.Notice, NoticePriority.High, false, false, NoticeCategory.Message);
        var urgent = NoticeFx.For(AttentionKind.Notice, NoticePriority.High, true, false, NoticeCategory.Message);
        var call = NoticeFx.For(AttentionKind.Notice, NoticePriority.Normal, false, true, NoticeCategory.Message);
        var low = NoticeFx.For(AttentionKind.Notice, NoticePriority.Low, false, false, NoticeCategory.Message);
        var summary = NoticeFx.For(AttentionKind.Summary, NoticePriority.Normal, false, false, null);
        var error = NoticeFx.For(AttentionKind.Notice, NoticePriority.Normal, false, false, NoticeCategory.Error);
        var warning = NoticeFx.For(AttentionKind.Notice, NoticePriority.Normal, false, false, NoticeCategory.Warning);
        // A message from brim-notify: its category and priority hint reach the plan through the notice BrimDeck keeps.
        var cliError = NoticeIntake.FromPipe(new NoticeMessage { AppName = "build", Category = "error" });
        var cliLow = NoticeIntake.FromPipe(new NoticeMessage { AppName = "build", Priority = "low" });
        var cliError2 = NoticeFx.For(AttentionKind.Notice, cliError.PriorityHint, false, cliError.Call, cliError.Category);
        var cliLow2 = NoticeFx.For(AttentionKind.Notice, cliLow.PriorityHint, false, cliLow.Call, cliLow.Category);
        check("Liquid plans: a brim-notify error plays in the danger color, one marked low plays lightly", cliError2.Tone == NoticeFxTone.Danger && cliLow2.Intensity < normal.Intensity);
        check("Liquid plans: an ordinary notice plays as designed (intensity 5, a separation of 0.6 s, the theme's glow)", normal == NoticeFxPlan.Standard);
        check("Liquid plans: the weight of a notice follows its priority: low, normal, high, urgent",
            low.Intensity < normal.Intensity && normal.Intensity < high.Intensity && high.Intensity < urgent.Intensity
            && low.Separation < normal.Separation && normal.Separation < high.Separation && high.Separation <= urgent.Separation
            && low.Glow < normal.Glow && normal.Glow < high.Glow && high.Glow < urgent.Glow);
        check("Liquid plans: what only informs (a summary) plays lightly, and in the theme's own color",
            summary.Intensity <= 2 && summary.Separation < low.Separation && summary.Glow < low.Glow && summary.Tone == NoticeFxTone.Theme);
        check("Liquid plans: a warning, an alarm and a call take the warning color; an error the danger color",
            warning.Tone == NoticeFxTone.Warning && urgent.Tone == NoticeFxTone.Warning && call.Tone == NoticeFxTone.Warning && error.Tone == NoticeFxTone.Danger);
        check("Liquid plans: an error is stronger than an ordinary notice and no stronger than an alarm", error.Glow > normal.Glow && error.Glow <= urgent.Glow);
        bool ranges = new[] { normal, high, urgent, call, low, summary, error, warning }.All(p => p.Intensity is >= 1 and <= 10 && p.Separation is >= .2 and <= 2 && p.Glow is > 0 and <= 1.25);
        check("Liquid plans: every plan is within the motion's own ranges", ranges);
        check("Liquid pace: the panel's animation duration sets it, 1 at the default, within 0.75 to 1.5",
            NoticeFx.Pace(360) == 1 && NoticeFx.Pace(100) == .75 && NoticeFx.Pace(1000) == 1.5 && NoticeFx.Pace(540) == 1.5 && NoticeFx.Pace(0) == .75 && NoticeFx.Pace(9999) == 1.5 && Math.Abs(NoticeFx.Pace(450) - 1.25) < 1e-9);
    }

    private static LiquidSpec Spec(LiquidForm form, double width = 376, double height = 32, int seed = -1, double intensity = 5)
        => new(form, width, height, seed < 0 ? NoticeLiquid.DesignSeed : seed, Intensity: intensity, CornerRadius: 12);

    private static List<(double T, double Glow, double Left, double Right, int Discs, int Necks)> Walk(NoticeLiquid liquid)
    {
        var frame = new LiquidFrame(); var list = new List<(double, double, double, double, int, int)>();
        for (double t = 0; t < liquid.Length + .1; t += 1d / 60)
        {
            liquid.Sample(t, frame);
            list.Add((t, frame.Glow, frame.InsetLeft, frame.InsetRight, frame.Discs.Count, frame.Necks.Count));
        }
        return list;
    }

    private static void Timing(Action<string, bool> check)
    {
        check("Liquid: the glow lights at 0.12 s and 0.42 s, dims between and is gone by 0.75 s",
            NoticeLiquid.GlowAt(0) == 0 && Math.Abs(NoticeLiquid.GlowAt(.12) - 1) < 1e-9 && Math.Abs(NoticeLiquid.GlowAt(.3) - .12) < 1e-9
            && Math.Abs(NoticeLiquid.GlowAt(.42) - 1) < 1e-9 && NoticeLiquid.GlowAt(.75) == 0 && NoticeLiquid.GlowAt(2) == 0);
        var liquid = new NoticeLiquid(Spec(LiquidForm.Capsule));
        check("Liquid: the motion lasts the separation, a second of tail and a moment of rest", Math.Abs(liquid.Length - (.6 + 1 + .15)) < 1e-9);
        long s = 11; for (int i = 0; i < 4; i++) s = (s * 48271 + 11) % 2147483647;
        check("Liquid: the design's seed is the fourth step of its sequence from 11", NoticeLiquid.DesignSeed == s && s > 0);
    }

    private static void Capsule(Action<string, bool> check)
    {
        var liquid = new NoticeLiquid(Spec(LiquidForm.Capsule));
        var again = new NoticeLiquid(Spec(LiquidForm.Capsule));
        var a = new LiquidFrame(); var b = new LiquidFrame();
        bool same = true, bounded = true, finite = true, insets = true; int most = 0, necks = 0;
        for (double t = 0; t < liquid.Length; t += 1d / 120)
        {
            liquid.Sample(t, a); again.Sample(t, b);
            same &= a.Discs.SequenceEqual(b.Discs) && a.InsetLeft == b.InsetLeft && a.Necks.Count == b.Necks.Count;
            foreach (var d in a.Discs)
            {
                finite &= double.IsFinite(d.X) && double.IsFinite(d.Y) && double.IsFinite(d.R);
                // Within the pill's height, and no further out than the zone and the extra room allow.
                bounded &= Math.Abs(d.Y) + d.R <= 16 + 1e-6 && d.X + d.R <= 16 * 2.4 + 8 + 1e-6 && d.R > 0;
            }
            insets &= a.InsetLeft >= 0 && a.InsetLeft <= 16 + 1e-9 && a.InsetRight >= 0 && a.InsetRight <= 16 + 1e-9;
            most = Math.Max(most, a.Discs.Count); necks = Math.Max(necks, a.Necks.Count);
        }
        check("Liquid capsule: the same seed gives the same droplets and ends", same);
        check("Liquid capsule: every droplet stays inside the pill's height and its room, with finite numbers", bounded && finite);
        check("Liquid capsule: an end never draws in past the body (16 DIPs of a 376 wide capsule)", insets);
        check("Liquid capsule: droplets leave, with necks while they part, but never a crowd", most >= 2 && most <= 24 && necks >= 1);
        var walk = Walk(liquid);
        check("Liquid capsule: nothing is left of the motion after its length",
            walk.Where(frame => frame.T >= liquid.Length).All(frame => frame.Discs == 0 && frame.Left == 0 && frame.Right == 0 && frame.Glow == 0));
        check("Liquid capsule: the ends draw in only while droplets part, and flow back out by the end",
            walk.Any(frame => frame.Left > 1) && walk.Any(frame => frame.Right > 1) && walk.Where(frame => frame.T > .6 + .8).All(frame => frame.Left < .5 && frame.Right < .5));
        var narrow = new NoticeLiquid(Spec(LiquidForm.Capsule, 260, 44));
        var frameNarrow = new LiquidFrame(); double most2 = 0;
        for (double t = 0; t < narrow.Length; t += 1d / 120) { narrow.Sample(t, frameNarrow); most2 = Math.Max(most2, Math.Max(frameNarrow.InsetLeft, frameNarrow.InsetRight)); }
        check("Liquid capsule: a toast narrower than the kept body still leaves its ends room, a width of 16 at the least in all",
            most2 > 0 && most2 <= 16 + 1e-9);
        var padded = new NoticeLiquid(Spec(LiquidForm.Capsule) with { MaxInset = 10 });
        var framePadded = new LiquidFrame(); double most3 = 0, drops3 = 0;
        for (double t = 0; t < padded.Length; t += 1d / 120) { padded.Sample(t, framePadded); most3 = Math.Max(most3, Math.Max(framePadded.InsetLeft, framePadded.InsetRight)); drops3 = Math.Max(drops3, framePadded.Discs.Count); }
        check("Liquid capsule: an end never draws in further than it is allowed (the room the content leaves it), and the droplets still leave",
            most3 > 5 && most3 <= 10 + 1e-9 && drops3 >= 2);
        // The curve takes more of the padding the taller the content: a 44 high pill with 16 of padding and content 16 up and down.
        double allowed = NoticeLiquid.MaxInset(44, 16, 16);
        check("Liquid capsule: the room the content leaves an end is its padding less the end's own curve at the content's height",
            Math.Abs(NoticeLiquid.MaxInset(44, 16, 0) - 13) < 1e-9 && allowed > 4 && allowed < 7 && NoticeLiquid.MaxInset(44, 2, 16) == 0 && NoticeLiquid.MaxInset(44, 30, 99) < NoticeLiquid.MaxInset(44, 30, 0));
        var tall = new NoticeLiquid(Spec(LiquidForm.Capsule, 376, 60));
        var frameTall = new LiquidFrame(); bool inside = true;
        for (double t = 0; t < tall.Length; t += 1d / 120) { tall.Sample(t, frameTall); inside &= frameTall.Discs.All(d => Math.Abs(d.Y) + d.R <= 30 + 1e-6); }
        check("Liquid capsule: a taller pill keeps its droplets inside its own height", inside);
    }

    private static void Notch(Action<string, bool> check)
    {
        var liquid = new NoticeLiquid(Spec(LiquidForm.Notch, 376, 44));
        var frame = new LiquidFrame();
        bool band = true, runs = false; double outline = 0;
        for (double t = 0; t < liquid.Length; t += 1d / 120)
        {
            liquid.Sample(t, frame, 376, 44);
            outline = frame.OutlineLength;
            int at = 0;
            foreach (int count in frame.BumpRuns)
            {
                runs = true;
                int half = count / 2;
                // Each run lists its outer edge, then its base; the outer edge lies within the band of the base.
                for (int i = 0; i < half; i++)
                {
                    var o = frame.BumpPoints[at + i]; var bPoint = frame.BumpPoints[at + count - 1 - i];
                    double distance = Math.Sqrt((o.X - bPoint.X) * (o.X - bPoint.X) + (o.Y - bPoint.Y) * (o.Y - bPoint.Y));
                    band &= distance <= NoticeLiquid.Band + 1.5 + 1e-6;
                }
                at += count;
            }
            band &= frame.Discs.All(d => d.Side == 0 && d.R <= 2.4 + 1e-6);
        }
        check("Liquid notch: the bulges and beads stay within five DIPs of the outline (and a bead is small)", band);
        check("Liquid notch: bulges do run, and the outline is as long as the shoulders, walls, corners and bottom make it", runs && outline > 376 && outline < 376 + 2 * 44 + 80);
        liquid.Sample(.05, frame, 376, 44);
        check("Liquid notch: at the start nothing but the corners has begun to swell", frame.BumpRuns.Count <= 2 && frame.InsetLeft == 0 && frame.InsetRight == 0);
        var other = new LiquidFrame();
        new NoticeLiquid(Spec(LiquidForm.Notch, 376, 44)).Sample(.3, other, 376, 44); liquid.Sample(.3, frame, 376, 44);
        check("Liquid notch: the same seed gives the same bulges", frame.BumpPoints.SequenceEqual(other.BumpPoints));
        liquid.Sample(.3, frame, 420, 44);
        check("Liquid notch: a toast that grows meanwhile gets the outline of its new size", frame.Outline.Max(p => p.X) > 400);
    }

    // What is joined to the surface shares its outline (LiquidFrame's Attached flags); what has parted has its own.
    private static void Joined(Action<string, bool> check)
    {
        var liquid = new NoticeLiquid(Spec(LiquidForm.Capsule, 376, 44));
        var frame = new LiquidFrame(); int joinedFrames = 0, partedFrames = 0; bool mouthsJoined = true, necksConsistent = true, separate = true, touchingJoined = true;
        for (double t = 0; t < liquid.Length; t += 1d / 120)
        {
            liquid.Sample(t, frame, 376, 44);
            if (frame.Discs.Any(d => d.Attached)) joinedFrames++;
            if (frame.Discs.Any(d => !d.Attached)) partedFrames++;
            foreach (var neck in frame.Necks)
            {
                if (neck.Mouth && !(neck.Attached && frame.Discs[neck.Second].Attached)) mouthsJoined = false;
                if (neck.Attached && !neck.Mouth && !(frame.Discs[neck.First].Attached && frame.Discs[neck.Second].Attached)) necksConsistent = false;
                if (!neck.Attached && !neck.Mouth && (frame.Discs[neck.First].Attached != frame.Discs[neck.Second].Attached)) necksConsistent = false;
            }
            foreach (var disc in frame.Discs)
            {
                // One overlapping the end (its cap is a circle of the surface's own radius, 22) is joined.
                double inset = disc.Side < 0 ? frame.InsetLeft : frame.InsetRight;
                double fromCap = Math.Sqrt(Math.Pow(disc.X + inset + 22, 2) + disc.Y * disc.Y);
                if (!disc.Attached && fromCap < 22 + disc.R) touchingJoined = false;
            }
            foreach (var a in frame.Discs.Where(d => d.Attached))
                foreach (var d in frame.Discs.Where(x => !x.Attached && x.Side == a.Side))
                    if (Math.Sqrt((a.X - d.X) * (a.X - d.X) + (a.Y - d.Y) * (a.Y - d.Y)) < a.R + d.R) separate = false;
        }
        check("Liquid capsule: droplets are joined to the surface for most of the motion, and some part from it", joinedFrames > 100 && partedFrames >= 1);
        check("Liquid capsule: a neck to the end, and the droplet at its far end, are joined; necks between droplets agree with their droplets", mouthsJoined && necksConsistent);
        check("Liquid capsule: a droplet that touches a joined droplet or the end is itself joined (its outline would cross the other's)", separate && touchingJoined);
        var notch = new NoticeLiquid(Spec(LiquidForm.Notch, 376, 44));
        bool offsets = true; double most = 0;
        for (double t = 0; t < notch.Length; t += 1d / 60)
        {
            notch.Sample(t, frame, 376, 44);
            if (frame.Offsets.Length != frame.Outline.Length) offsets = false;
            most = Math.Max(most, frame.Offsets.Length == 0 ? 0 : frame.Offsets.Max());
        }
        check("Liquid notch: every point of its outline has the offset the bulges give it, within the band", offsets && most > 1 && most <= NoticeLiquid.Band + 1e-9);
    }

    // The indicator's own motion: the surface's size on two curves (Curves.LineArrive, Curves.LineReturn), no liquid layer.
    private static void Line(Action<string, bool> check)
    {
        const double h = 1e-5;
        double Speed(Func<double, double> f, double x) => (f(x + h) - f(x - h)) / (2 * h);
        double windUp = .03, carry = .04;
        double Arrive(double x) => Curves.LineArrive(x, windUp);
        double Return(double x) => Curves.LineReturn(x, carry);
        check("Line arrival: from the line to the card, and the opening spring alone without a wind-up",
            Arrive(0) == 0 && Arrive(1) == 1 && Curves.LineArrive(.37, 0) == Curves.Bounce(.37) && Curves.LineArrive(.9, 0) == Curves.Bounce(.9));
        double least = 0; for (double x = 0; x < Curves.LineWindUpLength; x += .001) least = Math.Min(least, Arrive(x));
        check("Line arrival: it first draws back by the wind-up (against the way it will go) and is back at its length when it is let go",
            Math.Abs(least + windUp) < 1e-3 && Math.Abs(Arrive(Curves.LineWindUpLength + 1e-9)) < 1e-6);
        double before = Speed(Arrive, Curves.LineWindUpLength - 1e-3), after = Speed(Arrive, Curves.LineWindUpLength + 1e-3);
        check("Line arrival: its speed runs on through the release (no stop, no turn, no jump), outward on both sides",
            before > 0 && after > 0 && Math.Abs(after - before) < .08 * before + .1);
        double most = 0; bool grows = true; double previous = Arrive(Curves.LineWindUpLength + .02);
        for (double x = Curves.LineWindUpLength + .03; x < .5; x += .01) { double v = Arrive(x); grows &= v >= previous - 1e-9; previous = v; most = Math.Max(most, v); }
        double high = 0; for (double x = .5; x < 1; x += .002) high = Math.Max(high, Arrive(x));
        check("Line arrival: after the release it only grows until it is nearly there, and passes its size by under 2 percent", grows && high < 1.02);
        check("Line return: from the card to the line, and the closing curve alone without a carry",
            Return(0) == 0 && Return(1) == 1 && Curves.LineReturn(.3, 0) == Curves.Bezier(.45, 0, .12, 1, .3) && Curves.LineReturn(.8, 0) == Curves.Bezier(.45, 0, .12, 1, .8));
        check("Line return: it carries on from whichever closing curve it is given",
            Curves.LineReturn(.3, 0, .32, .72, 0, 1) == Curves.Bezier(.32, .72, 0, 1, .3) && Curves.LineReturn(.75, carry, .32, .72, 0, 1) > 1 + carry - 2e-3);
        double top = 0, at = 0; for (double x = 0; x <= 1; x += .001) { double v = Return(x); if (v > top) { top = v; at = x; } }
        check("Line return: it carries on past the line's size by the carry, at three quarters of the move, and is back at the end",
            Math.Abs(top - (1 + carry)) < 2e-3 && Math.Abs(at - .75) < .02 && Math.Abs(Return(.999) - 1) < 5e-3);
        double speedBefore = Speed(Return, Curves.LineCarryFrom - 1e-3), speedAfter = Speed(Return, Curves.LineCarryFrom + 1e-3);
        double Accel(double x) => (Return(x + 2e-4) - 2 * Return(x) + Return(x - 2e-4)) / 4e-8;
        double peak = 0; for (double x = .1; x < .9; x += .005) peak = Math.Max(peak, Math.Abs(Accel(x)));
        double accelBefore = Accel(Curves.LineCarryFrom - 3e-4), accelAfter = Accel(Curves.LineCarryFrom + 3e-4);
        check("Line return: its speed and its acceleration are continuous where the carry begins",
            Math.Abs(speedAfter - speedBefore) < .02 * Math.Abs(speedBefore) + 1e-3 && Math.Abs(accelAfter - accelBefore) < .03 * peak);
        var normal = NoticeFxPlan.Standard; var urgent = NoticeFx.For(AttentionKind.Notice, NoticePriority.High, true, false, NoticeCategory.Message); var low = NoticeFx.For(AttentionKind.Summary, NoticePriority.Normal, false, false, null);
        double Px(double fraction, double span) => fraction * span;
        check("Line amplitude: at most 5 DIPs of draw-back and 7 of carry for the standard notice, more for an urgent one, less for what only informs",
            Math.Abs(Px(NoticeFx.LineWindUp(56, 240, normal), 184) - 5) < 1e-6 && Math.Abs(Px(NoticeFx.LineCarry(56, 240, normal), 184) - 6.72) < 1e-6
            && NoticeFx.LineWindUp(56, 240, urgent) > NoticeFx.LineWindUp(56, 240, normal) && NoticeFx.LineWindUp(56, 240, low) < NoticeFx.LineWindUp(56, 240, normal)
            && NoticeFx.LineWindUp(24, 240, normal) < NoticeFx.LineWindUp(56, 240, normal) && NoticeFx.LineWindUp(56, 70, normal) == 0 && NoticeFx.LineCarry(56, 70, normal) == 0);
        // At the panel's scale (150%) the line is longer and the distances follow it: 7.5 DIPs of draw-back (5 times 1.5) and 10.08 of carry
        // (an eighth of the line's length, 84, which is under the cap of 10.5), and a card hardly wider than the line still gets none.
        check("Line amplitude at 150%: the draw-back and the carry are the 100% distances times 1.5 (or the line's own share)",
            Math.Abs(Px(NoticeFx.LineWindUp(84, 360, normal, 1.5), 276) - 7.5) < 1e-6 && Math.Abs(Px(NoticeFx.LineCarry(84, 360, normal, 1.5), 276) - 10.08) < 1e-6
            && NoticeFx.LineWindUp(84, 100, normal, 1.5) == 0 && NoticeFx.LineCarry(84, 100, normal, 1.5) == 0 && NoticeFx.LineWindUp(84, 360, normal, double.NaN) == NoticeFx.LineWindUp(84, 360, normal));
    }

    private static void Ending(Action<string, bool> check)
    {
        var liquid = new NoticeLiquid(Spec(LiquidForm.Capsule));
        var frame = new LiquidFrame();
        liquid.Sample(.4, frame); int before = frame.Discs.Count; double glow = frame.Glow;
        liquid.Abort(.4);
        check("Liquid ending: aborted, the motion is over within a third of a second", liquid.Aborted && !liquid.Finished(.4) && liquid.Finished(.4 + NoticeLiquid.AbortLength));
        liquid.Sample(.4, frame);
        check("Liquid ending: at the moment of the abort nothing has changed", frame.Discs.Count == before && Math.Abs(frame.Glow - glow) < 1e-9);
        liquid.Sample(.4 + NoticeLiquid.AbortLength / 2, frame);
        check("Liquid ending: halfway through, the droplets have shrunk and the glow dimmed", frame.Discs.All(d => d.R < 14) && frame.Glow <= glow + 1e-9);
        liquid.Sample(.4 + NoticeLiquid.AbortLength + .01, frame);
        check("Liquid ending: and then there is nothing to draw", !frame.Alive && frame.Discs.Count == 0 && frame.Necks.Count == 0 && frame.Glow == 0);
        check("Liquid ending: a negative time or one past its length draws nothing", Draws(new NoticeLiquid(Spec(LiquidForm.Notch, 376, 44)), -1) == 0
            && Draws(new NoticeLiquid(Spec(LiquidForm.Capsule)), 5) == 0);
    }

    private static int Draws(NoticeLiquid liquid, double time)
    {
        var frame = new LiquidFrame(); liquid.Sample(time, frame, 376, 44);
        return frame.Discs.Count + frame.BumpRuns.Count + (frame.Alive ? 1 : 0);
    }
}
