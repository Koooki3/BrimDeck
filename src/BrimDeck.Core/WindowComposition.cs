namespace BrimDeck.Core;

// How a panel window reaches the screen: as a layered window (Windows takes each frame through UpdateLayeredWindow, and
// clicks pass where it is fully transparent) or composed by DWM like any window, its transparent pixels showing what lies
// below. DWM keeps the opening at the display's rate on a wide window where the layered one drops to every other refresh;
// the liquid glass themes stay layered (their glass backer, not the window, holds their rate), and so does anything WPF
// cannot draw with the graphics card, where DWM would show the transparent pixels black.
public enum WindowSurface { Layered, Composed }

public static class WindowComposition
{
    // forced: BRIMDECK_COMPOSITION, "layered" or "dwm" to choose regardless of the theme (empty or anything else: by the theme);
    // liquid: a liquid glass theme is shown; hardware: WPF draws with the graphics card (render tier 2); remote: a remote
    // desktop session. Without the graphics card or in a remote session the window is layered, whatever is forced.
    public static WindowSurface Choose(string? forced, bool liquid, bool hardware, bool remote)
    {
        if (!hardware || remote) return WindowSurface.Layered;
        if (string.Equals(forced, "layered", StringComparison.OrdinalIgnoreCase)) return WindowSurface.Layered;
        if (string.Equals(forced, "dwm", StringComparison.OrdinalIgnoreCase)) return WindowSurface.Composed;
        return liquid ? WindowSurface.Layered : WindowSurface.Composed;
    }
}
