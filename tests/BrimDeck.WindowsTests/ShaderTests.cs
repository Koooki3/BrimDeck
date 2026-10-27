using System.Diagnostics;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;

// The liquid glass's shaders: compiled ahead (FxPreview --compile-liquid) from the very source that is embedded, so the renderer
// never compiles at run time (about 4 s on the UI thread); the source plain ASCII (the compiler is handed a string whose length
// it takes in bytes); and a renderer made quickly.
internal static class ShaderTests
{
    public static void Run(Action<string, bool> check)
    {
        var source = LiquidRenderer.EmbeddedSource();
        check("liquid glass: the shaders compiled ahead are of the embedded source (run FxPreview --compile-liquid after editing Liquid.hlsl)",
            LiquidRenderer.EmbeddedHash() == LiquidRenderer.SourceHash(source));
        check("liquid glass: the shader source is plain ASCII", source.All(b => b < 128));
        var frost = LiquidRenderer.EmbeddedFrostSource();
        check("liquid glass: the frost's shaders compiled ahead are of the embedded Frost.hlsl (run FxPreview --compile-liquid after editing it)",
            LiquidRenderer.EmbeddedFrostHash() == LiquidRenderer.SourceHash(frost));
        check("liquid glass: the frost's shader source is plain ASCII", frost.All(b => b < 128));
        // The liquid looks' text over the darkest (light) or brightest (dark) glass the legibility band lets through: the light
        // glass's floor without its white tint (the stricter case), the dark glass's ceiling under its thin dark base.
        byte Gray(float v) => (byte)Math.Round(v * 255);
        var floor = new ThemeColor(255, Gray(LiquidFrame.TextLight), Gray(LiquidFrame.TextLight), Gray(LiquidFrame.TextLight));
        var ceiling = ThemeColor.Mix(new ThemeColor(255, Gray(LiquidFrame.TextDark), Gray(LiquidFrame.TextDark), Gray(LiquidFrame.TextDark)),
            new ThemeColor(255, 0x12, 0x14, 0x19), .08);
        foreach (var (id, glass) in new[] { ("liquid-glass-light", floor), ("liquid-glass-dark", ceiling) })
        {
            var palette = PanelThemes.Resolve(PanelThemes.BuiltIn(id)!);
            foreach (var (key, least) in new[] { ("textPrimary", 4.5), ("textSecondary", 4.5), ("textTertiary", 3.0) })
            {
                double ratio = ThemeColor.Contrast(palette[key].Over(glass), glass);
                check($"liquid glass: {id} {key} keeps {least:0.0}:1 over the glass's band ({ratio:0.00})", ratio >= least);
            }
        }
        using var capture = DesktopSource.TryCreate();
        if (capture is null) { Console.WriteLine("SKIP: liquid glass renderer (no Direct3D device on a desktop output)."); return; }
        int compiles = LiquidRenderer.RuntimeCompiles;
        var clock = Stopwatch.StartNew();
        using (new LiquidRenderer(capture.Device)) { }
        var first = clock.Elapsed.TotalMilliseconds;
        // Whether the renderer compiled is counted rather than told by its time: cold, the driver turns the precompiled bytecode
        // into its own in some 375-600 ms here, slower while a game or a video loads the GPU, and a compile at run time takes from
        // about 1.4 s (three shaders) to 4 s. The count is what tells them apart: it found Liquid.ps.cso left out of the assembly
        // (MSBuild took ".ps." for a culture), the renderer compiling at every start. The times are printed, and held to a bound
        // only with BRIMDECK_TIMING=1 (a quiet machine).
        check($"liquid glass: a renderer is made without compiling ({first:0} ms; compiled {LiquidRenderer.RuntimeCompiles - compiles}, last {LiquidRenderer.LastCompile})", LiquidRenderer.RuntimeCompiles == compiles);
        clock.Restart();
        using (new LiquidRenderer(capture.Device)) { }
        var again = clock.Elapsed.TotalMilliseconds;
        // Warm, on the same device, it takes next to nothing: what the app's glass pays, its shaders made once before on a worker
        // (SourceStart.Warm). Held against the cold make on the same machine at the same moment, so a loaded GPU stretches both.
        check($"liquid glass: a renderer made again on the same device is quick ({again:0.0} ms, cold {first:0} ms)",
            LiquidRenderer.RuntimeCompiles == compiles && again < Math.Max(50, first / 4));
        if (Environment.GetEnvironmentVariable("BRIMDECK_TIMING") == "1")
            check($"liquid glass (timing): a renderer is made in under 1.5 s cold ({first:0} ms)", first < 1500);
    }
}
