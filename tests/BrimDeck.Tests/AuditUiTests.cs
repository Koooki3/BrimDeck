using BrimDeck.Core;
using BrimDeck.Core.Notices;

// The fixes of the UI audit that can be checked without WPF: the panel and its notices take their tracks from one place, with
// the accepted values kept, and the particles' strength keeps its theme-file name.
internal static class AuditUiTests
{
    internal static void Run(Action<string, bool> check)
    {
        // q-P1.1: the notice moves on the panel's own tracks wherever it moves as the panel does.
        check("Audit ui: the notice's closing tracks are the panel's", NoticeMotion.ReturnContent == NoticeMotion.PanelCloseContent
            && NoticeMotion.ReturnSurface == NoticeMotion.PanelCloseSurface && NoticeMotion.ReturnTint == NoticeMotion.PanelCloseTint && NoticeMotion.Landing == NoticeMotion.PanelLandCompact);
        check("Audit ui: the notice's opening tracks are the panel's", NoticeMotion.Surface == NoticeMotion.PanelOpenSurface && NoticeMotion.ArriveTint == NoticeMotion.PanelOpenTint);
        check("Audit ui: the accepted panel timings are unchanged", NoticeMotion.PanelOpenContent == new NoticeMotion.Track(.12, .8)
            && NoticeMotion.PanelCloseContent == new NoticeMotion.Track(0, .3) && NoticeMotion.PanelCloseSurface == new NoticeMotion.Track(.12, 1)
            && NoticeMotion.PanelCloseTint == new NoticeMotion.Track(.85, 1) && NoticeMotion.PanelLandCompact == new NoticeMotion.Track(.62, 1)
            && NoticeMotion.PanelOpenSurface == new NoticeMotion.Track(0, 1) && NoticeMotion.PanelOpenTint == new NoticeMotion.Track(0, .12));
        check("Audit ui: the toast's text keeps its own, later track beside the page's", NoticeMotion.ArriveContent == new NoticeMotion.Track(.2, .85)
            && NoticeMotion.ArriveContent.Start > NoticeMotion.PanelOpenContent.Start);

    }
}
