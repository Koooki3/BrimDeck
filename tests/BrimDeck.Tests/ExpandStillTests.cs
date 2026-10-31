using System.Text.RegularExpressions;

// The open panel's content only fades while the panel opens and closes. It used to scale between .96 and 1 as well, and text
// under a scale that is not exactly 1 is drawn unhinted at fractional places on the pixel grid: for the last hundred milliseconds of
// the opening (the scale's tail) the text shimmered, and on the frame the scale reached 1 it snapped onto the grid. Measured offscreen
// (tests/BrimDeck.FxPreview --expand-jitter --snap): at a scale of .9992, 1249 of the page's pixels at 100 % (3195 at 150 %) differ
// from the page at rest. What is checked here is that nothing puts a transform on the page's content again; the moves themselves
// are the FxPreview's.
internal static class ExpandStillTests
{
    // Where the page content (ExpandedContent) is given a render transform: in XAML as a property element or attribute, in code by
    // assignment or by an animation of a transform named for it.
    internal static List<string> Transforms(string fileName, string text)
    {
        var found = new List<string>();
        if (fileName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match element in Regex.Matches(text, @"<(\w+)\s[^>]*x:Name=""ExpandedContent""[^>]*>(?<body>.*?)</\1>", RegexOptions.Singleline))
                if (Regex.IsMatch(element.Value.Split('>')[0], "RenderTransform=") || Regex.IsMatch(element.Groups["body"].Value, @"^\s*(<!--.*?-->\s*)*<\w+\.RenderTransform>", RegexOptions.Singleline))
                    found.Add(fileName + ": ExpandedContent has a render transform");
            return found;
        }
        foreach (Match m in Regex.Matches(text, @"ExpandedContent\s*\.\s*(RenderTransform|LayoutTransform)\s*=[^=]|\bContentScale\b|Animate\(\s*ExpandedContent[^;]*ScaleTransform"))
            found.Add(fileName + ": " + m.Value.Trim());
        return found;
    }

    public static void Run(Action<string, bool> check)
    {
        // The finder itself: what the panel had before the fix is found, what it has now is not.
        check("Expand still: the old scale on the page's content is found (XAML and code)",
            Transforms("old.xaml", """<Grid x:Name="ExpandedContent" Visibility="Hidden" Opacity="0" RenderTransformOrigin="0.5,0"><Grid.RenderTransform><ScaleTransform x:Name="ContentScale"/></Grid.RenderTransform><Grid x:Name="ContentCanvas"/></Grid>""").Count == 1
            && Transforms("old.cs", "Animate(ContentScale, ScaleTransform.ScaleXProperty, expanded ? 1 : .96, duration, content, easeOut);").Count >= 1
            && Transforms("old2.cs", "ExpandedContent.RenderTransform = new ScaleTransform(.96, .96);").Count == 1);
        check("Expand still: a page that only fades, and a transform on something else, are not found",
            Transforms("new.xaml", """<Grid x:Name="ExpandedContent" Visibility="Hidden" Opacity="0"><Grid x:Name="ContentCanvas"><Grid.RenderTransform><ScaleTransform/></Grid.RenderTransform></Grid></Grid>""").Count == 0
            && Transforms("new.cs", "Animate(ExpandedContent, OpacityProperty, expanded ? 1 : 0, duration, content, easeOut);").Count == 0);

        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        check("Expand still: the source tree is found", source is not null);
        if (source is null) return;
        var app = Path.Combine(source.FullName, "src", "BrimDeck");
        var files = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(file => (file.EndsWith(".cs") || file.EndsWith(".xaml")) && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)).ToList();
        check("Expand still: the window's XAML is among the sources", files.Any(file => file.EndsWith("MainWindow.xaml")) && File.ReadAllText(Path.Combine(app, "MainWindow.xaml")).Contains("x:Name=\"ExpandedContent\""));
        var found = files.SelectMany(file => Transforms(Path.GetFileName(file), File.ReadAllText(file))).ToList();
        check("Expand still: nothing scales or moves the open panel's content as it fades: " + string.Join("; ", found.Take(5)), found.Count == 0);
    }
}
