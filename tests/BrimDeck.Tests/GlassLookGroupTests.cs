using BrimDeck.Core;

// Review v2 U08: the panels sharing one automatic liquid glass look turn its palette as one transaction: one decision epoch per
// turn or reversal, only the leader holding the current epoch publishes, a hidden leader hands over with the decision's history,
// and a reduced-motion panel neither leads while another can nor publishes for it.
internal static class GlassLookGroupTests
{
    private sealed class Deck(string name)
    {
        public override string ToString() => name;
    }

    internal static void Run(Action<string, bool> check)
    {
        var a = new Deck("A");
        var b = new Deck("B");
        var c = new Deck("C");

        // A over a light backdrop, B over a dark one: only the leader's readings decide, B (a follower) cannot publish its own wish.
        var group = new GlassLookGroup<Deck>();
        var leader = group.Elect([new(a, true, true), new(b, true, true)]);
        group.Decision = new GlassAppearance(dark: true, appDark: true);
        group.Decision.Reset(true, 0);
        long turned = -1;
        for (long t = 3000; t <= 4000 && turned < 0; t += 80) if (group.Decision.Feed(.95, t)) turned = t;
        long epoch = group.Begin(group.Decision.Dark);
        bool followerRefused = !group.TryPublish(b, epoch, dark: true) && !group.TryPublish(b, epoch, dark: false);
        bool leaderPublished = group.TryPublish(a, epoch, dark: false);
        check("A shared liquid glass look publishes only its leader's decision, never a follower's",
            ReferenceEquals(leader, a) && turned > 0 && followerRefused && leaderPublished && group.Published == false);

        // The leader hidden mid-turn: B takes over, keeps the decision with its history, and publishes the current epoch's goal.
        var shared = new GlassLookGroup<Deck>();
        shared.Elect([new(a, true, true), new(b, true, true)]);
        var decision = shared.Decision = new GlassAppearance(dark: true, appDark: true);
        decision.Reset(true, 0);
        for (long t = 0; t <= 3000; t += 80) decision.Feed(.1, t);
        long turn = shared.Begin(dark: false);
        var heir = shared.Elect([new(a, false, true), new(b, true, true)]);
        bool kept = ReferenceEquals(shared.Decision, decision) && Math.Abs(decision.Smoothed - .1) < .01;
        check("A hidden leader hands the shared look's turn and decision history over to the next panel",
            ReferenceEquals(heir, b) && kept && !shared.TryPublish(a, turn, false) && shared.TryPublish(b, turn, false));

        // A reversal while a follower's palette arrives first: the follower holds the old epoch and its goal can no longer be
        // published by anyone, the leader publishes only the new goal.
        var reverse = new GlassLookGroup<Deck>();
        reverse.Elect([new(a, true, true), new(b, true, true)]);
        long toLight = reverse.Begin(dark: false);
        long backToDark = reverse.Begin(dark: true);
        bool stale = !reverse.TryPublish(b, toLight, false) && !reverse.TryPublish(a, toLight, false) && !reverse.TryPublish(a, backToDark, false);
        check($"A reversal starts a new epoch ({toLight} -> {backToDark}) and no older goal is published late",
            backToDark == toLight + 1 && stale && reverse.TryPublish(a, backToDark, true) && reverse.PublishedEpoch == backToDark);

        // Reduced motion: C (no animation) does not lead while A animates; alone it leads (and the window publishes at once).
        var motion = new GlassLookGroup<Deck>();
        var first = motion.Elect([new(c, true, false), new(a, true, true)]);
        var alone = motion.Elect([new(c, true, false), new(a, false, true)]);
        long snap = motion.Begin(dark: true);
        check("A reduced-motion panel leads a shared look only when no animating panel can",
            ReferenceEquals(first, a) && ReferenceEquals(alone, c) && motion.TryPublish(c, snap, true));

        // Nobody eligible: the decision is dropped (a look shown again starts from fresh readings); set from outside: no turn goes on.
        var gone = new GlassLookGroup<Deck> { Decision = new GlassAppearance(true, true) };
        gone.Elect([new(a, true, true)]);
        long running = gone.Begin(false);
        gone.Abandon();
        bool abandoned = !gone.TryPublish(a, running, false);
        gone.Elect([new(a, false, true)]);
        check("A shared look with no panel shown drops its decision, and a palette set from outside ends the turn",
            abandoned && gone.Decision is null && gone.Leader is null);
    }
}
