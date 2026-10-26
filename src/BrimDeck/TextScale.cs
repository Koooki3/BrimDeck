using Microsoft.Win32;

namespace BrimDeck;

// Windows "Text size" (Settings > Accessibility > Text size, 100–225 %). The open panel follows it: its pages keep the layout
// they have at the size the user gave them, and the panel grows by the factor with everything on it, so larger text never
// meets a layout it was not laid out for (MainWindow.Zoom). The collapsed surface keeps its own size, which the user sets in
// DIPs. The factor is capped at 1.5, which keeps the largest pages on a common monitor.
internal static class TextScale
{
    public const double Max = 1.5;
    private const string Key = @"Software\Microsoft\Accessibility";

    // The factor in effect. The offscreen page sheets set their own (tests/BrimDeck.FxPreview --text-scale).
    public static double Factor { get; private set; } = Read();
    internal static double? Override { get; set; }
    public static double Current => Override ?? Factor;

    // Reads the setting again; true when it changed.
    public static bool Refresh()
    {
        double factor = Read();
        if (factor == Factor) return false;
        Factor = factor;
        return true;
    }

    public static double Clamp(double percent) => Math.Clamp(double.IsFinite(percent) ? percent / 100 : 1, 1, Max);

    private static double Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue("TextScaleFactor") is int percent ? Clamp(percent) : 1;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return 1;
        }
    }
}
