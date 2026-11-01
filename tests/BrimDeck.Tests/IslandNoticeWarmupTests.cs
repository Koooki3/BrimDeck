using System.Text.RegularExpressions;

// The notice window's warm-up (IslandNotice.Prepare, six seconds after the panel starts) shows the window once before any island has
// been laid out. Until 11-01 its layers drew then and asked for the island's form, a NullReferenceException on every start that
// crash.log recorded from 10-31 and that left the window shown. The real path (the window on the real MainWindow, offscreen) is
// checked by tests/BrimDeck.IslandChecks; what is checked here is that the guards stay in the source.
internal static class IslandNoticeWarmupTests
{
    internal static void Run(Action<string, bool> check)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BrimDeck.sln"))) root = root.Parent;
        check("Island notice warm-up: the source tree is found", root is not null);
        if (root is null) return;
        string code = File.ReadAllText(Path.Combine(root.FullName, "src", "BrimDeck", "IslandNotice.cs"));
        check("Island notice warm-up: no layer draws before the island has been laid out (the layers wait for _shape)",
            Regex.IsMatch(code, @"layer\.Ready\s*=\s*\(\)\s*=>\s*_shape is not null") && Regex.IsMatch(code, @"OnRender\([^)]*\)\s*\{\s*if \(Ready\?\.Invoke\(\) != false\) Draw\?\.Invoke"));
        check("Island notice warm-up: a warm-up that fails half way does not leave the window shown",
            Regex.IsMatch(code, @"finally\s*\{[^}]*if \(IsVisible && _item is null\) Hide\(\);"));
    }
}
