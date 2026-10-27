using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

// The collapsed row giving way to a quota alert and back (MainWindow.RowSwap.cs), frame by frame: for every built-in theme and
// each of the three collapsed forms (notch, capsule, indicator line), the real panel offscreen (LiquidPages.Offscreen, animations on)
// shows an alert and then ends it, as PresentAlert and EndAlert do, and every frame the display would draw is read back from the
// compact content (the row and the row leaving it; the surface, its rim and the ambient light are not what swaps). Each swap is run twice, as it was (MainWindow.RowSwapOff: the row replaced in one frame) and as it is now.
// For a swap from picture A to picture B, d(x, y) being the mean absolute difference between two frames:
//   step    the largest d(frame, previous frame) over the largest d(frame, A or B): 1.0 is a swap in one frame (the flash); a smooth swap stays well below.
//   rate    the same, taken per 16.7 ms display frame when the offscreen frame took longer, as if the picture changed evenly through it.
//   moving  the frames in which the picture changed by more than 2 percent of that; a flash is 1 or 2, a swap 5 or more.
//   back    frames that are a tenth of d(A, B) further from B than the nearest frame so far was: a frame that goes back.
//   gap     the share of the swap in which the picture is blank (nearer neither A nor B than a fifth of d(A, B)).
// One alert is shown and ended first for each theme and form, so the first swap measured is not the one that sets the island up. The frames come
// at the pace the offscreen source draws (about 30 a second), so a swap spread over the same time shows larger steps than on a display.
//   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --alert-return [<output folder>] [--themes id,id]
// With a folder, a strip of the frames of each swap is written as <theme>-<form>-<in|out>-<before|after>.png.
internal static class AlertReturn
{
    private const double Scale = 1;
    private sealed record Frame(double Ms, byte[] Pixels, int Width, int Height, double Opacity);
    private sealed record Result(string Theme, string Form, string Direction, bool Fixed, int Frames, double Step, double Rate, int Moving, int Back, double Gap, double Ms);

    public static int Run(string? folder, HashSet<string>? only)
    {
        var (app, deck, root, source) = LiquidPages.Offscreen(animations: true);
        using var _ = source;
        if (!Soft.Enabled) { Console.WriteLine("FAIL: animations are off (Windows' animation effects); nothing to measure"); return 1; }
        var entry = app.Settings.Apps.FirstOrDefault(candidate => candidate.Enabled);
        if (entry is null) { Console.WriteLine("FAIL: no application entry to raise an alert for"); return 1; }
        deck.OffscreenMedia(LiquidPages.Track(), LiquidPages.Cover());
        if (folder is not null) Directory.CreateDirectory(folder);
        var results = new List<Result>();
        foreach (var theme in PanelThemes.BuiltIns.Where(theme => only is null || only.Contains(theme.Id)))
            foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Capsule, CompactStyle.Line })
            {
                LiquidPages.UseLook(app, deck, root, theme.Id);
                app.Settings.Style = style;
                deck.Preview(true, expanded: false);
                deck.ApplySettings();
                deck.OffscreenLiquidFrame();
                LiquidPages.Rescale(root);
                LiquidPages.Settle(root, deck);
                // The theme and the form settle (the theme fade, the morph between forms) before anything is measured.
                Wait(1800);
                // One alert before anything is measured: the first one after a change of form or theme sets the island and its parts
                // up (the shape settling, the row built for the first time), which is not the swap.
                deck.OffscreenAlert(entry, true); Wait(1200); deck.OffscreenAlert(entry, false); Wait(1200);
                foreach (bool fixedNow in new[] { false, true })
                {
                    MainWindow.RowSwapOff = !fixedNow;
                    foreach (bool into in new[] { true, false })
                    {
                        var frames = Capture(deck, root, () => deck.OffscreenAlert(entry, into));
                        string direction = into ? "in" : "out";
                        results.Add(Measure(theme.Id, style.ToString(), direction, fixedNow, frames));
                        if (folder is not null) Strip(frames, Path.Combine(folder, $"{theme.Id}-{style}-{direction}-{(fixedNow ? "after" : "before")}.png"));
                        if (Environment.GetEnvironmentVariable("ALERT_DUMP") is { Length: > 0 } dump) { Directory.CreateDirectory(dump); Strip(frames, Path.Combine(dump, $"{theme.Id}-{style}-{direction}-{(fixedNow ? "after" : "before")}-whole.png"), whole: true); }
                    }
                }
                MainWindow.RowSwapOff = false;
            }
        Console.WriteLine($"{"theme",-20} {"form",-8} {"dir",-4} {"swap",-7} {"frames",6} {"step",6} {"rate",5} {"moving",6} {"back",5} {"gap",5} {"ms",5}");
        foreach (var r in results)
            Console.WriteLine($"{r.Theme,-20} {r.Form,-8} {r.Direction,-4} {(r.Fixed ? "after" : "before"),-7} {r.Frames,6} {r.Step,6:0.00} {r.Rate,5:0.00} {r.Moving,6} {r.Back,5} {r.Gap,5:0.00} {r.Ms,5:0}");
        // The line shows no row at all (the indicator has no content), so there is nothing to swap or to flash.
        var visible = results.Where(r => r.Form != nameof(CompactStyle.Line)).ToList();
        var after = visible.Where(r => r.Fixed).ToList();
        var before = visible.Where(r => !r.Fixed).ToList();
        Console.WriteLine($"notch and capsule: before worst step {before.Max(r => r.Step):0.00} (flash in {before.Count(r => r.Step > .9)} of {before.Count}), "
            + $"after worst step {after.Max(r => r.Step):0.00} ({after.Max(r => r.Rate):0.00} a display frame), frames that go back {after.Sum(r => r.Back)}, worst blank share {after.Max(r => r.Gap):0.00}");
        bool ok = after.All(r => r.Rate < .6 && r.Moving >= 5 && r.Back == 0) && before.All(r => r.Step > .9 && r.Moving <= 2);
        Console.WriteLine(ok ? "OK" : "FAIL");
        return ok ? 0 : 1;
    }

    // The island's frames from the call to 750 ms after it (longer than any swap), one per display frame.
    private static List<Frame> Capture(MainWindow deck, System.Windows.Controls.Grid root, Action change)
    {
        var frames = new List<Frame>();
        var clock = Stopwatch.StartNew();
        void Take(object? sender, EventArgs e) => frames.Add(Read(deck, clock.Elapsed.TotalMilliseconds));
        frames.Add(Read(deck, 0));
        CompositionTarget.Rendering += Take;
        change();
        Wait(750);
        CompositionTarget.Rendering -= Take;
        frames.Add(Read(deck, clock.Elapsed.TotalMilliseconds));
        return frames;
    }

    private static Frame Read(MainWindow deck, double ms)
    {
        // The compact content alone (the row and the row leaving it): the surface, its rim and the theme's ambient light are not what swaps.
        var island = deck.Compact;
        int width = Math.Max(1, (int)Math.Ceiling(island.ActualWidth * Scale)), height = Math.Max(1, (int)Math.Ceiling(island.ActualHeight * Scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(island) { Stretch = Stretch.Fill }, null, new Rect(0, 0, island.ActualWidth * Scale, island.ActualHeight * Scale));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return new Frame(ms, pixels, width, height, deck.Compact.Opacity);
    }

    private static double Distance(Frame a, Frame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 1;
        long sum = 0;
        for (int i = 0; i < a.Pixels.Length; i++) sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        return sum / (double)a.Pixels.Length;
    }

    private static Result Measure(string theme, string form, string direction, bool fixedNow, List<Frame> frames)
    {
        var a = frames[0]; var b = frames[^1];
        double whole = Distance(a, b), swing = frames.Max(f => Math.Max(Distance(f, a), Distance(f, b)));
        // Nothing to compare when the two ends look the same (the line has no row).
        if (whole < .05) return new Result(theme, form, direction, fixedNow, frames.Count, 0, 0, 0, 0, 0, 0);
        double step = 0, last = double.MaxValue;
        if (Environment.GetEnvironmentVariable("ALERT_DEBUG") == "1")
            Console.WriteLine($"  {theme} {form} {direction} {(fixedNow ? "after" : "before")}: " + string.Join(" ", frames.Select((f, i) => $"{f.Ms:0}:{Distance(f, a) / swing:0.00}/{Distance(f, b) / swing:0.00}@{f.Width}x{f.Height}")));
        int back = 0, blank = 0, moving = 0, inMotion = 0;
        double rate = 0;
        bool left = false;
        double start = 0, end = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            double moved = Distance(frames[i], frames[i - 1]) / swing;
            step = Math.Max(step, moved);
            rate = Math.Max(rate, moved / Math.Max(1, (frames[i].Ms - frames[i - 1].Ms) / 16.7));
            if (moved > .02) inMotion++;
            double toB = Distance(frames[i], b), fromA = Distance(frames[i], a);
            if (fromA > whole * .02 && !left) { left = true; start = frames[i].Ms; }
            if (left && toB > last + whole * .1) back++;
            last = Math.Min(last, toB);
            if (left && toB > whole * .02) end = frames[i].Ms;
            if (left && toB > whole * .02) { moving++; if (fromA > whole * .2 && toB > whole * .2) blank++; }
        }
        return new Result(theme, form, direction, fixedNow, frames.Count, step, rate, inMotion, back, moving == 0 ? 0 : blank / (double)moving, left ? end - start : 0);
    }

    private static void Strip(List<Frame> frames, string path, bool whole = false)
    {
        // About a dozen frames across the swap, from the first that differs to the last.
        var a = frames[0]; var b = frames[^1];
        int first = Math.Max(1, frames.FindIndex(f => Distance(f, a) > .05)), last = frames.FindLastIndex(f => Distance(f, b) > .05);
        if (whole) (first, last) = (1, frames.Count - 2);
        if (first <= 0 || last < first) return;
        var picks = Enumerable.Range(0, 12).Select(i => frames[Math.Min(frames.Count - 1, first - 1 + (int)Math.Round(i * (last - first + 2) / 11.0))]).ToList();
        int w = picks[0].Width, h = picks[0].Height;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), null, new Rect(0, 0, 3 * (w + 4), 4 * (h + 4)));
            for (int i = 0; i < picks.Count; i++)
            {
                var tile = BitmapSource.Create(picks[i].Width, picks[i].Height, 96, 96, PixelFormats.Pbgra32, null, picks[i].Pixels, picks[i].Width * 4);
                dc.DrawImage(tile, new Rect(i % 3 * (w + 4), i / 3 * (h + 4), w, h));
            }
        }
        var sheet = new RenderTargetBitmap(3 * (w + 4), 4 * (h + 4), 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        LiquidPreview.Save(sheet, path);
    }

    private static void Wait(double milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
