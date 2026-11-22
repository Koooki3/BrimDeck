using BrimDeck.Core;

// V3 V302/V304/V308: the read of the area an opening panel covers belongs to one opening. A read for an opening that has
// ended (closed, opened again), or made before the panel moved display or scale, finishes but never reaches the glass; a read
// that found nothing is Unknown and leaves the capsule's veil, not the worst range. V306/V307: the covered area is judged by a
// grid's shares by area and its trust, not by one middle mean alone.
internal static class GlassOpeningSessionTests
{
    // The panel's side of a cover read, as MainWindow.ReadGlassCover does it, with the capture a hand-completed task.
    private sealed class Panel
    {
        public readonly GlassOpeningSession Session = new();
        public nint Monitor = 1;
        public double Dpi = 1.25;

        public readonly List<(GlassOpeningSession.Ticket Ticket, TaskCompletionSource<bool> Read)> Running = [];
        public readonly List<long> AppliedEpochs = [];

        public void Open() { Session.Intent(); Read(); }
        public void Close() => Session.Close();

        public async void Read()
        {
            if (Session.Begin(Monitor, 0, 0, 100, 100, Dpi, 0) is not { } ticket) return;
            var read = new TaskCompletionSource<bool>();
            Running.Add((ticket, read));
            bool ok = await read.Task;
            if (Session.Finish(ticket, Monitor, Dpi, ok) == GlassOpeningSession.Outcome.Applied) AppliedEpochs.Add(ticket.OpenEpoch);
            if (Session.TakePending()) Read();
        }

        public void Answer(int index, bool ok = true)
        {
            var (_, read) = Running[index];
            read.SetResult(ok);
        }
    }

    internal static void Run(Action<string, bool> check)
    {
        // open#1 -> close -> open#2 -> callback#1 late: the old reading reaches nothing; open#2's own read follows and applies.
        var panel = new Panel();
        panel.Open();
        long first = panel.Session.OpenEpoch;
        panel.Close();
        panel.Open();
        long second = panel.Session.OpenEpoch;
        bool waited = panel.Running.Count == 1 && panel.Session.Pending;
        panel.Answer(0);
        bool staleDropped = panel.AppliedEpochs.Count == 0 && panel.Session.Count(GlassOpeningSession.Outcome.Superseded) == 1;
        bool followed = panel.Running.Count == 2 && panel.Running[1].Ticket.OpenEpoch == second;
        panel.Answer(1);
        check("A cover read for an earlier opening never reaches the glass; the latest opening's own read follows it and applies",
            first != second && waited && staleDropped && followed && panel.AppliedEpochs.SequenceEqual([second])
            && panel.Session.AppliedEpoch == second);

        // Closed while reading: the late answer is dropped as Closed, and nothing is pending.
        var closed = new Panel();
        closed.Open();
        closed.Close();
        closed.Answer(0);
        check("A cover read that ends after the panel began closing is dropped (V308), and no read follows it",
            closed.AppliedEpochs.Count == 0 && closed.Session.Last == GlassOpeningSession.Outcome.Closed && closed.Running.Count == 1
            && closed.Session.AppliedEpoch == 0);

        // Ten quick toggles, the reads answering late and out of order: only a read of the opening still under way applies.
        var quick = new Panel();
        for (int i = 0; i < 10; i++) { quick.Open(); quick.Close(); }
        quick.Open();
        long last = quick.Session.OpenEpoch;
        for (int round = 0; round < 20 && quick.Running.Any(r => !r.Read.Task.IsCompleted); round++)
            for (int i = quick.Running.Count - 1; i >= 0; i--) if (!quick.Running[i].Read.Task.IsCompleted) { quick.Answer(i); break; }
        check("Ten quick openings and closings with late reads: only the final opening's read applies, the stale ones zero times",
            quick.AppliedEpochs.All(e => e == last) && quick.AppliedEpochs.Count == 1 && quick.Running.Count == 2);

        // The scale changes, the panel moves display, or the glass is hidden while reading: dropped.
        var moved = new Panel();
        moved.Open();
        moved.Dpi = 1.5;
        moved.Answer(0);
        var display = new Panel();
        display.Open();
        display.Monitor = 2;
        display.Answer(0);
        var hidden = new Panel();
        hidden.Open();
        hidden.Session.SceneChanged();
        hidden.Answer(0);
        check("A cover read made before the panel's scale or display changed, or its glass was hidden, is dropped",
            moved.Session.Last == GlassOpeningSession.Outcome.Moved && display.Session.Last == GlassOpeningSession.Outcome.Moved
            && hidden.Session.Last == GlassOpeningSession.Outcome.SceneChanged
            && moved.AppliedEpochs.Count + display.AppliedEpochs.Count + hidden.AppliedEpochs.Count == 0);

        // A read that found nothing: Unknown, no cover, and the veil keeps the capsule's requirement (NaN cover) instead of the
        // worst range.
        var empty = new Panel();
        empty.Open();
        empty.Answer(0, ok: false);
        var refused = new GlassOpeningSession();
        refused.Intent();
        var ticket = refused.Begin(1, 0, 0, 10, 10, 1, 0)!.Value;
        bool refusedOutcome = refused.Finish(ticket, 1, 1, read: false, refused: true) == GlassOpeningSession.Outcome.Refused;
        check("A cover read that read nothing (or was refused) is Unknown: the opening keeps the capsule's veil, not the worst range",
            empty.Session.Last == GlassOpeningSession.Outcome.Unknown && empty.AppliedEpochs.Count == 0 && refusedOutcome
            && GlassOpening.Required(.3, double.NaN, 1) == .3);

        Faults(check);
        Scenes(check);
    }

    // V4 F04: a cover read whose capture (or what is done with it) throws. The read is the panel's own Task: nothing escapes to
    // the window's dispatcher, the read budget's slot comes back, the opening records the read as Unknown (the capsule's veil,
    // never the worst range) and is free for the next read, and an intent that waited for it still gets its read.
    private static void Faults(Action<string, bool> check)
    {
        var budget = new GlassReadBudget<object>();
        var key = new object();
        var session = new GlassOpeningSession();
        session.Intent();
        var ticket = session.Begin(1, 0, 0, 100, 100, 1.25, 0)!.Value;
        var capture = new TaskCompletionSource<int?>();
        int again = 0, failures = 0;
        Task<GlassOpeningSession.Outcome> task;
        try
        {
            task = session.Read(ticket, budget, key, () => capture.Task, () => (1, 1.25), (_, _) => { }, () => again++, _ => failures++);
        }
        catch (Exception) { task = Task.FromException<GlassOpeningSession.Outcome>(new InvalidOperationException("thrown at once")); }
        bool held = budget.Running == 1;
        // An intent while the capture runs waits for it.
        bool waits = session.Begin(1, 0, 0, 100, 100, 1.25, 0) is null && session.Pending;
        capture.SetException(new System.ComponentModel.Win32Exception(5, "the screen could not be read"));
        bool quiet = task.IsCompletedSuccessfully;
        check("A cover read whose capture throws ends quietly: its budget slot is released and it is recorded as Unknown",
            held && quiet && task.Result == GlassOpeningSession.Outcome.Unknown && budget.Running == 0
            && session.Count(GlassOpeningSession.Outcome.Unknown) == 1 && !session.Running && failures == 1);
        check("The intent that waited for a cover read that threw still gets its own read (none starves)",
            waits && again == 1 && session.Begin(1, 0, 0, 100, 100, 1.25, 0) is not null);

        // The capture throwing before it returns a task, and the glass's own handling of a reading throwing.
        var early = new GlassOpeningSession();
        early.Intent();
        var first = early.Begin(1, 0, 0, 100, 100, 1.25, 0)!.Value;
        Task<GlassOpeningSession.Outcome> thrown, applying;
        try { thrown = early.Read<int>(first, budget, key, () => throw new OutOfMemoryException("injected"), () => (1, 1.25), (_, _) => { }, () => { }); }
        catch (Exception) { thrown = Task.FromException<GlassOpeningSession.Outcome>(new InvalidOperationException("thrown at once")); }
        var second = early.Begin(1, 0, 0, 100, 100, 1.25, 0);
        try
        {
            applying = second is { } next
                ? early.Read<int>(next, budget, key, () => Task.FromResult<int?>(1), () => (1, 1.25), (_, _) => throw new InvalidOperationException("injected"), () => { })
                : Task.FromException<GlassOpeningSession.Outcome>(new InvalidOperationException("no second read"));
        }
        catch (Exception) { applying = Task.FromException<GlassOpeningSession.Outcome>(new InvalidOperationException("thrown at once")); }
        check("A capture that throws at once, or applying a reading that throws, escapes nowhere and frees the budget and the opening",
            thrown.IsCompletedSuccessfully && thrown.Result == GlassOpeningSession.Outcome.Unknown && applying.IsCompletedSuccessfully
            && applying.Result == GlassOpeningSession.Outcome.Applied && budget.Running == 0 && !early.Running);
    }

    private static byte[] Image(Func<int, int, (byte R, byte G, byte B)> at, int size = 24)
    {
        var rgb = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var (r, g, b) = at(x, y);
            int i = (y * size + x) * 3;
            rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
        }
        return rgb;
    }

    private static bool Turns(GlassSceneSnapshot scene, bool dark, bool appDark = true)
    {
        var decision = new GlassAppearance(dark, appDark);
        return decision.Open(scene, 10_000) && decision.Dark != dark;
    }

    private static void Scenes(Action<string, bool> check)
    {
        (byte, byte, byte) white = (255, 255, 255), orange = (200, 70, 10), ide = (30, 30, 32), black = (0, 0, 0);
        // A white page under an orange bar, the bar 30 % (7 of 24 rows) of the covered area.
        var page = GlassSceneSnapshot.FromPixels(Image((_, y) => y < 7 ? orange : white), 24, 24)!;
        // The bar 37.5 %: the middle mean alone (.815) falls short of the dark mode's .82, the area is three quarters light.
        var wide = GlassSceneSnapshot.FromPixels(Image((_, y) => y < 9 ? orange : white), 24, 24)!;
        var legacy = new GlassAppearance(dark: true, appDark: true);
        bool legacyKept = !legacy.Open(wide.MiddleMean, 10_000);
        check("A white page under a 30 % orange bar turns a dark look light with the opening (dark Windows mode)",
            page.LightShare >= .7 && page.Confidence >= GlassAppearance.OpenConfidence && Turns(page, dark: true));
        check("A white page under a 37.5 % orange bar turns light by area, where the middle mean alone kept it dark",
            legacyKept && wide.MiddleMean < .82 && wide.LightShare > .74 && Turns(wide, dark: true));

        var pure = GlassSceneSnapshot.FromPixels(Image((_, _) => white), 24, 24)!;
        var code = GlassSceneSnapshot.FromPixels(Image((x, y) => y % 3 == 0 && x > 3 && x < 16 ? ((byte)150, (byte)150, (byte)150) : ide), 24, 24)!;
        check("A pure white area turns a dark look light; a dark editor keeps it dark and turns a light look dark",
            Turns(pure, dark: true) && !Turns(pure, dark: false) && !Turns(code, dark: true) && Turns(code, dark: false, appDark: false)
            && Turns(code, dark: false, appDark: true));

        // Checkerboards: fine (pixels alternate, so every cell spreads fully) and coarse (whole cells alternate): neither kind of
        // area wins, so the look stays as it is either way.
        var fine = GlassSceneSnapshot.FromPixels(Image((x, y) => (x + y) % 2 == 0 ? white : black), 24, 24)!;
        var coarse = GlassSceneSnapshot.FromPixels(Image((x, y) => (x / 4 + y / 6) % 2 == 0 ? white : black), 24, 24)!;
        check("A checkerboard (fine or by cells) is neither light nor dark: the look stays either way",
            fine.Confidence < GlassAppearance.OpenConfidence && Math.Abs(coarse.LightShare - .5) < 1e-9
            && !Turns(fine, true) && !Turns(fine, false) && !Turns(coarse, true) && !Turns(coarse, false));

        // A capture that may already show the panel's own glass is trusted half: it turns nothing.
        check("A covered-area capture taken after the opening's first frame is trusted less and turns no look",
            pure.Occluded().OcclusionPossible && pure.Occluded().Confidence <= GlassSceneSnapshot.OccludedTrust && !Turns(pure.Occluded(), dark: true));

        // v3b: the first opening over a white page turned the look light about 20 ms after the intent, the panel still almost
        // shut, and the palette's spring then took 250-365 ms from dark: the panel's body showed dark glass (.50 of its settled
        // .94) for 100-200 ms. A turn the opening itself decides while the panel is still mostly shut takes the new look at once
        // (the opening is the panel's new scene); one decided later, over a panel mostly open, still turns on the spring.
        var opening = new GlassAppearance(dark: true, appDark: true);
        bool turnedLight = opening.Open(pure, 0) && !opening.Dark;
        check("An opening's own look turn decided while the panel is mostly shut takes the new look at once (no dark-first turn)",
            turnedLight && GlassOpening.TurnsAtOnce(0, animates: true) && GlassOpening.TurnsAtOnce(.05, animates: true)
            && GlassOpening.TurnsAtOnce(double.NaN, animates: true));
        check("An opening's look turn decided over a panel mostly open turns on the spring; reduced motion turns at once anyway",
            !GlassOpening.TurnsAtOnce(GlassOpening.TurnAtOnceBelow, animates: true) && !GlassOpening.TurnsAtOnce(.9, animates: true)
            && GlassOpening.TurnsAtOnce(.9, animates: false));
    }
}
