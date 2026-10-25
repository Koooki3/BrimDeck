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
        // The liquid looks' text over the darkest (light) or brightest (dark) glass the legibility band lets through: the light
        // glass's floor without its white tint (the stricter case), the dark glass's ceiling under its dark base.
        byte Gray(float v) => (byte)Math.Round(v * 255);
        var floor = new ThemeColor(255, Gray(LiquidFrame.TextLight), Gray(LiquidFrame.TextLight), Gray(LiquidFrame.TextLight));
        var ceiling = ThemeColor.Mix(new ThemeColor(255, Gray(LiquidFrame.TextDark), Gray(LiquidFrame.TextDark), Gray(LiquidFrame.TextDark)),
            new ThemeColor(255, 0x12, 0x14, 0x19), .3);
        foreach (var (id, glass) in new[] { ("liquid-glass-light", floor), ("liquid-glass-dark", ceiling) })
        {
            var palette = PanelThemes.Resolve(PanelThemes.BuiltIn(id)!);
            foreach (var key in new[] { "textPrimary", "textSecondary" })
            {
                double ratio = ThemeColor.Contrast(palette[key].Over(glass), glass);
                check($"liquid glass: {id} {key} keeps 4.5:1 over the glass's band ({ratio:0.00})", ratio >= 4.5);
            }
        }
        using var capture = DesktopSource.TryCreate();
        if (capture is null) { Console.WriteLine("SKIP: liquid glass renderer (no Direct3D device on a desktop output)."); return; }
        var clock = Stopwatch.StartNew();
        using (new LiquidRenderer(capture.Device)) { }
        var first = clock.Elapsed.TotalMilliseconds;
        check($"liquid glass: a renderer is made without compiling ({first:0} ms)", first < 500);
    }
}
