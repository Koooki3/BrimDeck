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
        // A desktop notification from a WSL distribution: its urgency and category hints reach the plan the same way.
        static NoticeFxPlan Desktop(byte urgency, string category)
        {
            var hints = new Dictionary<object, object> { ["urgency"] = new BrimDeck.Protocol.DBus.DBusVariant("y", urgency), ["category"] = new BrimDeck.Protocol.DBus.DBusVariant("s", category) };
            var wire = BrimDeck.Protocol.DBus.FreedesktopNotifications.ToNotice(["make", 0u, "", "Build", "done", new List<object>(), hints, -1], 1);
            var input = NoticeIntake.FromPipe(wire);
            return NoticeFx.For(AttentionKind.Notice, input.PriorityHint, false, input.Call, input.Category);
        }
        var wslCritical = Desktop(2, "network.error"); var wslLow = Desktop(0, "im.received"); var wslNormal = Desktop(1, "im.received");
        check("Liquid plans: a WSL notification's urgency and error category set how strongly it plays (critical stronger, low lighter, an error in the danger color)",
            wslCritical.Intensity > normal.Intensity && wslCritical.Tone == NoticeFxTone.Danger && wslLow.Intensity < normal.Intensity && wslNormal == NoticeFxPlan.Standard);
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

    private static void Line(Action<string, bool> check)
    {
        var liquid = new NoticeLiquid(Spec(LiquidForm.Line, 240, 40));
        var frame = new LiquidFrame(); int sweeps = 0; double most = 0; int drops = 0; double biggest = 0;
        for (double t = 0; t < liquid.Length; t += 1d / 120)
        {
            liquid.Sample(t, frame);
            sweeps = Math.Max(sweeps, frame.SweepCount); drops = Math.Max(drops, frame.Discs.Count);
            foreach (var s in frame.Sweeps.Take(frame.SweepCount)) most = Math.Max(most, s.Amount);
            foreach (var d in frame.Discs) biggest = Math.Max(biggest, d.R);
        }
        check("Liquid indicator: the glow sweeps from the middle twice, and no droplet is large (it keeps to a thin line's restraint)",
            sweeps == 2 && most > .5 && most <= 1 + 1e-9 && biggest <= 8 && drops >= 1 && drops <= 6);
        check("Liquid indicator: the glow is a sweep, not the capsule's flash", frame.Glow == 0);
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
