using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using BrimDeck.LiquidGlass;

// Review v2 U12: liquid glass's backdrop probe keeps one memory DC and DIB section for its life (no GDI objects made per
// reading), releases them on Dispose, and the title hint is one process-wide hook however many panels listen.
internal static class AcrylicProbeChecks
{
    [DllImport("user32")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    private static uint GdiObjects() => GetGuiResources(Process.GetCurrentProcess().Handle, 0);

    internal static void Run(Action<string, bool> check)
    {
        var display = new Int32Rect(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
        var island = new Int32Rect(display.Width / 2 - 200, 40, 400, 60);
        uint before = GdiObjects();
        var probe = new AcrylicProbe();
        var first = probe.Read(island, display, 24, 40);
        uint afterFirst = GdiObjects();
        int read = first is null ? 0 : 1;
        for (int i = 0; i < 200; i++) if (probe.Read(island, display, 24, 40) is not null) read++;
        uint afterMany = GdiObjects();
        probe.Dispose();
        uint afterDispose = GdiObjects();
        bool disposedReadsNothing = probe.Read(island, display, 24, 40) is null;
        check($"The glass probe reuses its GDI objects across readings and releases them ({before} -> {afterFirst} -> {afterMany} -> {afterDispose}, {read} readings)",
            read == 201 && afterMany == afterFirst && afterFirst - before <= 2 && afterDispose <= before && disposedReadsNothing);

        Action one = () => { }, two = () => { };
        bool hookedBefore = GlassHintBroker.Hooked;
        GlassHintBroker.Subscribe(one);
        GlassHintBroker.Subscribe(two);
        GlassHintBroker.Subscribe(one);
        bool shared = GlassHintBroker.Hooked && GlassHintBroker.Count == 2;
        GlassHintBroker.Unsubscribe(one);
        bool kept = GlassHintBroker.Hooked;
        GlassHintBroker.Unsubscribe(two);
        check("The glass's title hint is one process-wide hook for all panels, removed with the last",
            !hookedBefore && shared && kept && !GlassHintBroker.Hooked && GlassHintBroker.Count == 0);
    }
}
