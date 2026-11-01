using System.Text.RegularExpressions;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// A notice on the island dressed for the theme (B8, 2026-11-01): how it arrives (the notice's weight on the theme's way of moving),
// the light it throws, what lies under it over glass, the countdown's ring and the source guard that keeps IslandNotice on the
// theme's tokens. The window itself is drawn by the app; what is checked here is what decides it.
internal static class IslandNoticeThemeTests
{
    private static IEnumerable<(string Id, DaylightLook Look, PanelPalette Palette)> Looks()
    {
        foreach (var theme in PanelThemes.BuiltIns)
            foreach (var look in new[] { DaylightLook.Day, DaylightLook.Night })
                yield return (theme.Id, look, PanelThemes.Resolve(theme, look));
    }

    private static ThemeArrival ThemeOf(PanelPalette palette, bool highContrast = false, bool animations = true)
        => NoticeArrivals.Theme(palette.Motion, palette.Effects.Pixel > 0, NoticeArrivals.Glows(palette.Effects), highContrast, animations);

    internal static void Run(Action<string, bool> check)
    {
        Weight(check);
        Theme(check);
        Light(check);
        Backdrop(check);
        Ring(check);
        Guards(check);
    }

    private static void Weight(Action<string, bool> check)
    {
        var standard = NoticeArrivals.Weigh(AttentionKind.Notice, NoticePriority.Normal, false, false, NoticeCategory.Message);
        var summary = NoticeArrivals.Weigh(AttentionKind.Summary, NoticePriority.Normal, false, false, null);
        var low = NoticeArrivals.Weigh(AttentionKind.Notice, NoticePriority.Low, false, false, NoticeCategory.Info);
        var high = NoticeArrivals.Weigh(AttentionKind.Notice, NoticePriority.High, false, false, NoticeCategory.Info);
        var call = NoticeArrivals.Weigh(AttentionKind.Notice, NoticePriority.Normal, false, true, NoticeCategory.Message);
        var alarm = NoticeArrivals.Weigh(AttentionKind.Notice, NoticePriority.High, true, false, NoticeCategory.Info);
        var error = NoticeArrivals.Weigh(AttentionKind.Notice, NoticePriority.Normal, false, false, NoticeCategory.Error);
        var warning = NoticeArrivals.Weigh(AttentionKind.Notice, NoticePriority.Normal, false, false, NoticeCategory.Warning);
        check("Notice arrival: an ordinary notice plays the prototype's motion (intensity 5, separation 0.6 s)", standard == NoticeWeight.Standard && standard.Intensity == 5 && standard.Separation == .6);
        check("Notice arrival: the weight grows from a summary through a low, a normal and a high priority notice to a call and an alarm",
            summary.Intensity < low.Intensity && low.Intensity < standard.Intensity && standard.Intensity < high.Intensity && high.Intensity < call.Intensity
            && call.Glow > high.Glow && alarm == call && summary.Glow < standard.Glow);
        check("Notice arrival: an error is danger-toned, a warning warning-toned, a call or an alarm at least warning-toned",
            error.Tone == NoticeTone.Danger && warning.Tone == NoticeTone.Warning && call.Tone == NoticeTone.Warning && alarm.Tone == NoticeTone.Warning && standard.Tone == NoticeTone.Theme);
    }

    private static void Theme(Action<string, bool> check)
    {
        var standard = NoticeArrivals.Combine(NoticeWeight.Standard, ThemeArrival.Standard);
        check("Notice arrival: the prototype's own motion (fury 5, separation 0.6 s, pace 1, two flashes) is what a plain theme gives an ordinary notice",
            standard.Fury == 5 && standard.Separation == .6 && standard.Pace == 1 && standard.Flashes == 2 && standard.Liquid && standard.Animated);
        foreach (var id in new[] { PanelThemes.Dark, PanelThemes.Light })
        {
            var plain = NoticeArrivals.Combine(NoticeWeight.Standard, ThemeOf(PanelThemes.Resolve(PanelThemes.BuiltIn(id)!)));
            check($"Notice arrival: the {id} theme arrives as the prototype does", plain.Fury == 5 && plain.Separation == .6 && plain.Pace == 1 && plain.Flashes == 2 && plain.Liquid);
        }

        // Every built-in theme, in both of its looks: ranges, and the way each kind of theme differs from the rest.
        bool ranges = true;
        string broken = "";
        foreach (var (id, look, palette) in Looks())
        {
            var theme = ThemeOf(palette);
            var arrival = NoticeArrivals.Combine(NoticeWeight.Standard, theme);
            bool ok = theme.Pace is >= NoticeArrivals.PaceMin and <= NoticeArrivals.PaceMax && arrival.Fury is >= NoticeArrivals.FuryMin and <= NoticeArrivals.FuryMax
                && arrival.Separation is >= NoticeArrivals.SeparationMin and <= NoticeArrivals.SeparationMax && theme.Flashes is 1 or 2 && theme.Animated
                && theme.Liquid == (palette.Effects.Pixel <= 0) && arrival.Glow is > 0 and <= NoticeArrivals.GlowMax;
            if (!ok) { ranges = false; broken += $" {id}/{look}"; }
        }
        check("Notice arrival: every built-in theme has a fury, separation, pace and glow in range:" + broken, ranges);
        var endfield = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Endfield)!, DaylightLook.Night);
        var cyber = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Demo)!);
        var pixel = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.PixelBlocks)!);
        var glass = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.GlassDark)!);
        var flat = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!);
        var jelly = NoticeArrivals.Theme(ThemeMotion.Default with { Spring = SpringTier.Jelly }, false, false, false, true);
        check("Notice arrival: a firm theme's liquid swells less than a plain one's, a jelly one's more",
            endfield.Motion.Spring == SpringTier.Firm && cyber.Motion.Spring == SpringTier.Firm && ThemeOf(endfield).FuryScale < ThemeOf(flat).FuryScale && ThemeOf(cyber).FuryScale < ThemeOf(flat).FuryScale
            && jelly.FuryScale > 1 && jelly.FuryScale > ThemeOf(flat).FuryScale && NoticeArrivals.Combine(NoticeWeight.Standard, ThemeOf(endfield)).Fury < 5 && NoticeArrivals.Combine(NoticeWeight.Standard, jelly).Fury > 5
            && ThemeOf(glass).Liquid);
        check("Notice arrival: a theme whose surface glows flashes its backlight once, and so does a pixel theme; a plain one twice",
            NoticeArrivals.Glows(cyber.Effects) && ThemeOf(cyber).Flashes == 1 && ThemeOf(pixel).Flashes == 1 && ThemeOf(flat).Flashes == 2 && ThemeOf(endfield).Flashes == 2);
        check("Notice arrival: a pixel theme keeps its outline still (the backlight and the words alone); every other built-in theme's island moves",
            !ThemeOf(pixel).Liquid && Looks().Where(l => l.Palette.Effects.Pixel <= 0).All(l => ThemeOf(l.Palette).Liquid));
        check("Notice arrival: a theme that moves slowly slows the liquid by at most a tenth, and none speeds it past the prototype's by more than a twentieth",
            Looks().All(l => ThemeOf(l.Palette).Pace is >= .9 and <= 1.05) && ThemeOf(PanelThemes.Resolve(PanelThemes.BuiltIn("github-dark")!)).Pace == .9
            && ThemeOf(PanelThemes.Resolve(PanelThemes.BuiltIn("night-owl")!)).Pace == 1.05);

        // Missing or odd values fall back to the prototype's.
        check("Notice arrival: no motion section, or a pace that is not a number, gives the prototype's own pace and strength",
            NoticeArrivals.Theme(null, false, false, false, true) == ThemeArrival.Standard
            && NoticeArrivals.Theme(ThemeMotion.Default, false, false, false, true) == ThemeArrival.Standard
            && NoticeArrivals.Theme(ThemeMotion.Default with { Pace = double.NaN }, false, false, false, true).Pace == 1
            && NoticeArrivals.Theme(ThemeMotion.Default with { Pace = 99 }, false, false, false, true).Pace == NoticeArrivals.PaceMax
            && NoticeArrivals.Theme(ThemeMotion.Default with { Pace = -4 }, false, false, false, true).Pace == NoticeArrivals.PaceMin);
        // High contrast and animations off play nothing, whatever the theme.
        check("Notice arrival: high contrast, or animations turned off, plays no motion on any theme",
            Looks().All(l => !ThemeOf(l.Palette, highContrast: true).Animated && !ThemeOf(l.Palette, highContrast: true).Liquid && !ThemeOf(l.Palette, animations: false).Animated && !ThemeOf(l.Palette, animations: false).Liquid));
        check("Notice arrival: the weight is limited to the design's ranges whatever a theme asks",
            NoticeArrivals.Combine(new NoticeWeight(8, 1, 1.25, NoticeTone.Theme), new ThemeArrival(5, 5, 1, 2, true, true)) is { Fury: NoticeArrivals.FuryMax, Separation: NoticeArrivals.SeparationMax }
            && NoticeArrivals.Combine(new NoticeWeight(2, .4, .6, NoticeTone.Theme), new ThemeArrival(.1, .1, 1, 2, true, true)) is { Fury: NoticeArrivals.FuryMin, Separation: NoticeArrivals.SeparationMin }
            && NoticeArrivals.Combine(new NoticeWeight(5, .6, 9, NoticeTone.Theme), ThemeArrival.Standard).Glow == NoticeArrivals.GlowMax);

        // The flashes themselves.
        check("Island liquid: one flash is the first of the prototype's two and dies away by 0.42 s; two flashes are as before",
            IslandLiquid.GlowAt(0, 1) == 0 && Math.Abs(IslandLiquid.GlowAt(.12, 1) - 1) < 1e-9 && IslandLiquid.GlowAt(.3, 1) is > 0 and < .6 && IslandLiquid.GlowAt(.42, 1) == 0 && IslandLiquid.GlowAt(.6, 1) == 0
            && Enumerable.Range(0, 200).All(i => IslandLiquid.GlowAt(i / 200d, 2) == IslandLiquid.GlowAt(i / 200d)) && IslandLiquid.FlashEnd(1) == .42 && IslandLiquid.FlashEnd(2) == .75);
        check("Island liquid: one breath of the indicator is the first of the two",
            Math.Abs(IslandLiquid.BreathAt(.45, 1) - 1) < 1e-9 && IslandLiquid.BreathAt(.9, 1) == 0 && IslandLiquid.BreathAt(1.35, 1) == 0 && Math.Abs(IslandLiquid.BreathAt(1.35, 2) - 1) < 1e-9);
        // A plan that plays no liquid reaches the island's model: nothing swells there, only the light.
        var still = new IslandLiquid(IslandForm.Notch, 196, 32, liquid: false, fury: NoticeArrivals.Combine(NoticeWeight.Standard, ThemeOf(pixel)).Fury);
        check("Island liquid: a model made without liquid never swells", Enumerable.Range(0, 150).All(i => still.Outline.All(p => still.EdgeOut(p.S, i / 100d) == 0)));
        var fierce = new IslandLiquid(IslandForm.Notch, 196, 32, fury: 8, separation: 1);
        var gentle = new IslandLiquid(IslandForm.Notch, 196, 32, fury: 2, separation: .4);
        check("Island liquid: the plan's separation sets how long the motion plays", fierce.Total > gentle.Total && Math.Abs(fierce.Total - (1 + IslandLiquid.Tail + .15)) < 1e-9);
    }

    private static void Light(Action<string, bool> check)
    {
        bool read = true, flash = true, state = true;
        string broken = "";
        foreach (var (id, look, palette) in Looks())
        {
            bool glows = NoticeArrivals.Glows(palette.Effects);
            foreach (var tone in Enum.GetValues<NoticeTone>())
            {
                var light = NoticeLight.Of(palette, glows, tone);
                // Seen on the surface (the backlight lies behind the island, on the desktop; the bloom and the words' flash on it).
                if (light.MinimumContrast(palette.SurfaceStops) < PanelThemes.GraphicContrast - .01) { read = false; broken += $" {id}/{look}/{tone}"; }
            }
            var indicator = palette["indicator"];
            var lit = NoticeLight.LineFlash(palette, indicator, glows, NoticeTone.Theme);
            if (lit == indicator) { flash = false; broken += $" {id}/{look}/flash"; }
            state &= NoticeLight.Of(palette, glows, NoticeTone.Danger) == palette.Readable(palette["danger"]) && NoticeLight.Of(palette, glows, NoticeTone.Warning) == palette.WarningColor;
        }
        check("Notice light: on every built-in theme, in both looks and in each tone, the light reads against the surface at 3:1:" + broken, read);
        check("Notice light: the indicator always has a color to brighten toward that differs from its own", flash);
        check("Notice light: a dark theme's light is its primary text color (the prototype's pale one) and an error's and a warning's are the theme's danger and warning colors",
            NoticeLight.Of(PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!), false, NoticeTone.Theme) == PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!)["textPrimary"] && state);
        var paper = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Light)!);
        check("Notice light: a light theme's light is its accent, not white on white",
            NoticeLight.Of(paper, false, NoticeTone.Theme) == paper.Readable(paper["accent"]) && NoticeLight.Of(paper, false, NoticeTone.Theme).MinimumContrast(paper.SurfaceStops) >= 3);
    }

    private static void Backdrop(Action<string, bool> check)
    {
        check("Notice backdrop: a theme without glass shows its opaque surface, blur available or not",
            NoticeBackdrops.Of(IslandForm.Notch, false, true) == NoticeBackdrop.Opaque && NoticeBackdrops.Of(IslandForm.Capsule, false, false) == NoticeBackdrop.Opaque);
        check("Notice backdrop: glass shown and Windows able to blur: the blur lies under the tint, on the notch and the capsule",
            NoticeBackdrops.Of(IslandForm.Notch, true, true) == NoticeBackdrop.Blur && NoticeBackdrops.Of(IslandForm.Capsule, true, true) == NoticeBackdrop.Blur);
        check("Notice backdrop: glass shown but no blur (battery saver, a remote session, composition not available): the clear tint over the desktop",
            NoticeBackdrops.Of(IslandForm.Notch, true, false) == NoticeBackdrop.Clear && NoticeBackdrops.Of(IslandForm.Capsule, true, false) == NoticeBackdrop.Clear);
        check("Notice backdrop: the thin indicator has no surface, glass or not",
            NoticeBackdrops.Of(IslandForm.Line, true, true) == NoticeBackdrop.Opaque && NoticeBackdrops.Of(IslandForm.Line, false, false) == NoticeBackdrop.Opaque);
        // The glass itself is not drawn when its gate says no: the gate is GlassBackdrop.Available's first condition, the theme's glass.
        check("Notice backdrop: glass is shown only for a glass theme (the liquid glass pair) and never on the others",
            PanelThemes.BuiltIns.Where(t => PanelThemes.Resolve(t).IsGlass).Select(t => t.Id).OrderBy(id => id).SequenceEqual([PanelThemes.GlassDark, PanelThemes.GlassLight]));
    }

    private static void Ring(Action<string, bool> check)
    {
        var start = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var timing = new NoticeTiming { Kind = NoticeTimeKind.Countdown, StartsAt = start, EndsAt = start.AddMinutes(10), Native = true };
        double? At(double minutes, NoticeTiming? t = null) => NoticeClock.RingFraction(t ?? timing, start.AddMinutes(minutes));
        check("Countdown ring: it is empty at the start, half full halfway and full at the end, and stays full after",
            At(0) == 0 && Math.Abs(At(5)!.Value - .5) < 1e-9 && At(10) == 1 && At(12) == 1 && Math.Abs(At(2.5)!.Value - .25) < 1e-9);
        check("Countdown ring: it only grows as time passes", Enumerable.Range(0, 120).Select(i => At(i / 10d)!.Value).Zip(Enumerable.Range(1, 119).Select(i => At(i / 10d)!.Value)).All(p => p.First <= p.Second));
        check("Countdown ring: a stopped or cancelled countdown, a stopwatch, an alarm and a notice without a time have none",
            At(5, timing.Copy().Also(t => t.Stopped = true)) is null && At(5, new NoticeTiming { Kind = NoticeTimeKind.Stopwatch, StartsAt = start }) is null
            && At(5, new NoticeTiming { Kind = NoticeTimeKind.Alarm, EndsAt = start }) is null && NoticeClock.RingFraction(null, start) is null);
        // The words follow the same clock, so the ring and the words agree on how long is left.
        var zone = TimeZoneInfo.Utc;
        string language = Loc.Language;
        Loc.Use(Loc.English);
        try
        {
            check("Countdown ring: the words say what is left as the ring fills, and a countdown that ended says so",
                NoticeClock.Live(timing, start.AddMinutes(5), zone) == "05:00 left" && NoticeClock.Live(timing, start.AddMinutes(7.5), zone) == "02:30 left"
                && NoticeClock.Live(timing, start.AddMinutes(10), zone) == "Time's up" && NoticeClock.IsLive(timing, start.AddMinutes(9.99)) && !NoticeClock.IsLive(timing, start.AddMinutes(10)));
        }
        finally { Loc.Use(language); }

        // The arc: from the top, counterclockwise.
        var quarter = RingArc.Of(.25, 18, 2);
        var half = RingArc.Of(.5, 18, 2);
        var threeQuarters = RingArc.Of(.75, 18, 2);
        check("Countdown ring: its arc ends at the left a quarter in, at the bottom halfway, at the right three quarters in, going the short way up to halfway",
            Near(quarter.EndX, 9 - 8) && Near(quarter.EndY, 9) && Near(half.EndX, 9) && Near(half.EndY, 9 + 8) && Near(threeQuarters.EndX, 9 + 8) && Near(threeQuarters.EndY, 9)
            && !quarter.Large && !half.Large && threeQuarters.Large && quarter.Radius == 8 && quarter.Center == 9);
        check("Countdown ring: nothing is drawn at 0, the whole circle from 0.999, and a fraction out of range or not a number is held to them",
            RingArc.Of(0, 18, 2).None && !RingArc.Of(.5, 18, 2).None && RingArc.Of(.999, 18, 2).Full && RingArc.Of(1, 18, 2).Full && !RingArc.Of(.99, 18, 2).Full
            && RingArc.Of(-3, 18, 2).None && RingArc.Of(7, 18, 2).Full && RingArc.Of(double.NaN, 18, 2).None);
        // The arc advances with the clock: over a ten minute countdown it moves every second.
        var ends = Enumerable.Range(0, 60).Select(i => RingArc.Of(NoticeClock.RingFraction(timing, start.AddSeconds(i * 10))!.Value, 18, 2)).ToList();
        check("Countdown ring: a minute of a ten minute countdown moves the arc's end on every tenth second", ends.Zip(ends.Skip(1)).All(p => p.First.EndX != p.Second.EndX || p.First.EndY != p.Second.EndY));
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static T Also<T>(this T value, Action<T> change) { change(value); return value; }

    // The source: the island notice reads its colors, its text sizes and its spacings from the theme's tokens, its glass from
    // GlassBackdrop, its arrival from the plan, and no longer carries the constants B8 replaced.
    private static void Guards(Action<string, bool> check)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BrimDeck.sln"))) root = root.Parent;
        check("Island notice source: the source tree is found", root is not null);
        if (root is null) return;
        string app = Path.Combine(root.FullName, "src", "BrimDeck");
        string notice = File.ReadAllText(Path.Combine(app, "IslandNotice.cs")), metrics = File.ReadAllText(Path.Combine(app, "NoticeMetrics.cs"));
        string code = Regex.Replace(Regex.Replace(notice, @"//[^\r\n]*", ""), "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
        check("Island notice source: no color is written into it (no FromRgb or FromArgb of numbers, no #RRGGBB, no Colors.White or Black)",
            !Regex.IsMatch(code, @"Color\.From(Rgb|Argb)\(\s*(0x|\d)") && !Regex.IsMatch(notice, "\"#[0-9A-Fa-f]{6,8}\"") && !Regex.IsMatch(code, @"Colors\.(White|Black|Gray|LightGray)\b") && !Regex.IsMatch(code, @"Brushes\.(White|Black|Gray)\b"));
        check("Island notice source: no text size is a number times the scale (the sizes are TextSize.*) and the plate and gap are the panel's tokens",
            !Regex.IsMatch(code, @"\b\d+(\.\d+)?\s*\*\s*scale\b") && !Regex.IsMatch(code, @"FontSize\s*=\s*\d") && code.Contains("TextSize.Strong * scale") && code.Contains("TextSize.Body * scale")
            && code.Contains("NoticeMetrics.PlateSize") && code.Contains("NoticeMetrics.PlateGap") && !Regex.IsMatch(code, @"Math\.Round\(\d+\s*\*\s*scale\)")
            && metrics.Contains("PlateSize = Space.S16 + Space.S4, PlateGap = Space.S8") && metrics.Contains("RingSize = Space.S16 + Space.Hair, RingThickness = Space.Hair"));
        check("Island notice source: the arrival is the notice's plan on the theme (the old Pace and Fury constants, the near-white flash and glow are gone)",
            !Regex.IsMatch(code, @"\bconst double Pace\b") && !Regex.IsMatch(code, @"\bFury\s*=\s*5") && !code.Contains("LineFlash =") && !code.Contains("GlowColor") && code.Contains("_arrival.Fury") && code.Contains("_arrival.Pace")
            && code.Contains("_arrival.Separation") && code.Contains("_arrival.Flashes") && code.Contains("_look.ArrivalOf(") && code.Contains("_look.Light(") && code.Contains("_look.LineFlash("));
        check("Island notice source: over glass it has the island's blur (GlassBackdrop, decided by NoticeBackdrops) and follows what the glass reports (Held)",
            code.Contains("new GlassBackdrop(") && code.Contains("NoticeBackdrops.Of(") && code.Contains("GlassBackdrop.Available") && code.Contains("_glass.Held +=") && code.Contains("GlassBackdrop.SurfaceOpacity(") && code.Contains("_glass.LiquidTint")
            && code.Contains("_glass.Dispose()"));
        check("Island notice source: the theme's marks are drawn at the notice's height around the words, and the countdown's ring is followed by the shared tick and let go with it",
            code.Contains("decor.Draw(") && code.Contains("Words()") && code.Contains("new CountdownRing(") && code.Contains("NoticeClock.RingFraction(") && code.Contains("Center.LiveTick += LiveStep") && code.Contains("Center.LiveTick -= LiveStep"));
        // No new standing timer or frame subscription: the only timers are the hover timer and the shared tick.
        check("Island notice source: no timer of its own beyond the hover delay (no DispatcherTimer, no Timer)", !Regex.IsMatch(code, @"new\s+(System\.Windows\.Threading\.)?DispatcherTimer\b") && !Regex.IsMatch(code, @"new\s+(System\.Threading\.)?Timer\b"));
    }
}
