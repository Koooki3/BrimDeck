namespace BrimDeck.Core;

// Windows' "Text size" (Settings > Accessibility > Text size, 100-225 %) as a scale factor for BrimDeck. The panel is drawn
// larger by it as a whole, as with PanelScale, so every layout that adapts to its room (collapsed surface, pages, header,
// menus) works with it unchanged. Only the reading and the arithmetic are here; the Windows part is WindowsTextSize.
public static class TextScales
{
    public const double Max = 2.25;
    // The largest scale PanelScale and the text size make together: the largest page then still fits a common monitor.
    public const double MaxCombined = 2;
    public const double Step = 0.05;

    // A factor as a sensible text scale: 1 for anything unreadable or below 1, at most Max, in steps of Step.
    public static double Normalize(double factor)
        => !double.IsFinite(factor) || factor <= 1 ? 1 : Math.Round(Math.Round(Math.Min(factor, Max) / Step) * Step, 2);

    public static double FromPercent(double percent) => Normalize(percent / 100);

    // The scale the panel is drawn at: PanelScale's factor times the text size, within 1 and MaxCombined.
    public static double Combine(double panel, double text)
    {
        double product = (double.IsFinite(panel) && panel > 0 ? panel : 1) * Normalize(text);
        return Math.Round(Math.Clamp(product, 1, MaxCombined), 3);
    }
}

// Where the text size comes from: Windows, or a value a test sets. Read returns the factor (1.5 for 150 %); it may be anything.
public interface ITextSizeSource
{
    double Read();
}

// The text size now in effect, normalized. Refresh reads the source again and says whether the size changed, so the caller
// redraws only then.
public sealed class TextSize
{
    private readonly ITextSizeSource _source;
    public TextSize(ITextSizeSource source) { _source = source; Factor = Read(); }
    public double Factor { get; private set; }

    public bool Refresh()
    {
        double factor = Read();
        if (factor == Factor) return false;
        Factor = factor;
        return true;
    }

    // A source that fails is no reason to draw anything differently: 1 is Windows' default.
    private double Read()
    {
        try { return TextScales.Normalize(_source.Read()); }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or NotSupportedException) { return 1; }
    }
}
