using System.Diagnostics;
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
        using var capture = DesktopSource.TryCreate();
        if (capture is null) { Console.WriteLine("SKIP: liquid glass renderer (no Direct3D device on a desktop output)."); return; }
        var clock = Stopwatch.StartNew();
        using (new LiquidRenderer(capture.Device)) { }
        var first = clock.Elapsed.TotalMilliseconds;
        check($"liquid glass: a renderer is made without compiling ({first:0} ms)", first < 500);
    }
}
