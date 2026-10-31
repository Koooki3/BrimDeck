using BrimDeck.Core;
using BrimDeck.Core.Notices;

// The fixes of the UI audit that can be checked without WPF: the panel's own tracks keep the accepted values, and the particles'
// strength keeps its theme-file name.
internal static class AuditUiTests
{
    internal static void Run(Action<string, bool> check)
    {
        // q-P1.1: what moves as the panel does takes its tracks from one place.
        check("Audit ui: what moves as the panel does closes on the panel's tracks", NoticeMotion.ReturnContent == NoticeMotion.PanelCloseContent
            && NoticeMotion.ReturnSurface == NoticeMotion.PanelCloseSurface);
        check("Audit ui: the accepted panel timings are unchanged", NoticeMotion.PanelOpenContent == new NoticeMotion.Track(.12, .8)
            && NoticeMotion.PanelCloseContent == new NoticeMotion.Track(0, .3) && NoticeMotion.PanelCloseSurface == new NoticeMotion.Track(.12, 1)
            && NoticeMotion.PanelCloseTint == new NoticeMotion.Track(.85, 1) && NoticeMotion.PanelLandCompact == new NoticeMotion.Track(.62, 1)
            && NoticeMotion.PanelOpenSurface == new NoticeMotion.Track(0, 1) && NoticeMotion.PanelOpenTint == new NoticeMotion.Track(0, .12));

        // q-P2: the particles' strength under its own name, still read from the file's "trail".
        var dracula = PanelThemes.Resolve(PanelThemes.BuiltIn("dracula")!).Motion;
        check("Audit ui: particle strength is the theme file's trail", dracula.ParticleStrength > 0);
        check("Audit ui: the system look has no particles", dracula.ForSystem().ParticleStrength == 0);
    }
}
