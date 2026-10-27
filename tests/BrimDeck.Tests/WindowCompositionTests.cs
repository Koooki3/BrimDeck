using BrimDeck.Core;

internal static class WindowCompositionTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("Composition: a plain theme drawn by the graphics card is composed by DWM",
            WindowComposition.Choose(null, liquid: false, hardware: true, remote: false) == WindowSurface.Composed);
        check("Composition: a liquid glass theme stays layered",
            WindowComposition.Choose("", liquid: true, hardware: true, remote: false) == WindowSurface.Layered);
        check("Composition: software drawing is layered, whatever the theme or the variable (DWM would show black)",
            WindowComposition.Choose(null, false, hardware: false, remote: false) == WindowSurface.Layered
            && WindowComposition.Choose("dwm", false, hardware: false, remote: false) == WindowSurface.Layered);
        check("Composition: a remote session is layered, whatever is forced",
            WindowComposition.Choose("dwm", false, hardware: true, remote: true) == WindowSurface.Layered
            && WindowComposition.Choose(null, false, hardware: true, remote: true) == WindowSurface.Layered);
        check("Composition: BRIMDECK_COMPOSITION=layered forces the layered window for a plain theme",
            WindowComposition.Choose("layered", false, true, false) == WindowSurface.Layered
            && WindowComposition.Choose("LAYERED", false, true, false) == WindowSurface.Layered);
        check("Composition: BRIMDECK_COMPOSITION=dwm forces DWM for a liquid glass theme",
            WindowComposition.Choose("dwm", true, true, false) == WindowSurface.Composed);
        check("Composition: any other value chooses by the theme",
            WindowComposition.Choose("auto", true, true, false) == WindowSurface.Layered
            && WindowComposition.Choose("auto", false, true, false) == WindowSurface.Composed);
    }
}
