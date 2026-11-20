using BrimDeck;

// Liquid glass on the real compositor: the persistent graph compiles once, takes its two amounts on any number of brushes and
// frames without compiling again, and lets every native effect go. (What it shows on screen is compared with GlassRecipe by a
// capture-based oracle outside the test suite: GDI cannot read the composed glass back.)
internal static class GlassChecks
{
    public static void Run(Action<string, bool> check)
    {
        if (GlassComposition.Compositor is not { } compositor)
        {
            Console.WriteLine("SKIPPED: Composition is not available here, so the glass graph cannot be checked.");
            return;
        }
        var start = GlassEffect.Stats;
        var brushes = new List<Windows.UI.Composition.CompositionEffectBrush>();
        for (int i = 0; i < 20; i++)
            if (GlassEffect.Graph(compositor) is { } brush) brushes.Add(brush);
        check("Composition accepts the persistent glass graph with its two animatable amounts", brushes.Count == 20 && GlassEffect.GraphAvailable);
        var rng = new Random(11);
        bool set = true;
        try
        {
            for (int frame = 0; frame < 2000; frame++)
                GlassEffect.Present(brushes[frame % brushes.Count], rng.NextDouble(), rng.NextDouble(), rng.NextDouble());
        }
        catch (Exception) { set = false; }
        var after = GlassEffect.Stats;
        check($"2000 material frames on 20 brushes compile nothing after the first graph ({after.Compiled - start.Compiled} compiled)",
            set && after.Compiled - start.Compiled <= 1 && after.Brushes - start.Brushes == 20);
        foreach (var brush in brushes) GlassEffect.Release(brush);
        brushes.Clear();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        var released = GlassEffect.Stats;
        check($"Releasing the brushes leaves only the graph's own native effects ({released.Live} alive)", released.Live <= 4);
    }
}
