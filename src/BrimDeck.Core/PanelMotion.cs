using BrimDeck.Core.Notices;

namespace BrimDeck.Core;

// How long the panel's opening and closing take, and what a move turned back part way does (BrimDeck.MainWindow.SetExpanded).
// The lengths are milliseconds; 0 means no animation. Dynamic notices move on the same rules, so the rules themselves are kept
// with theirs (NoticeMotion, MoveTurn) and the panel's names for them are here.
public static class PanelMotion
{
    // motion-review C1: the panel closes in three quarters of the time it takes to open, as it draws back into the dot on
    // powering off. C4: a move the keyboard asked for (a hotkey, Escape) takes 0.6 of it and lands without the opening
    // spring's give.
    public const double CloseFactor = NoticeMotion.CloseFactor, KeyFactor = NoticeMotion.KeyFactor;
    public static int Length(int length, bool closing, bool keyboard) => NoticeMotion.PanelLength(length, closing, keyboard);

    // The open panel taking a new size in place (another page, a changed setting) always takes this long, the default opening
    // length: the setting is for opening and closing, and a page switch slowed down with it felt like a stall.
    public const int Reshape = AnimationLength.Default;

    // Where the closing surface starts, as a fraction of the move: a little after the content has begun to leave; at once
    // when the closing turns back an opening still under way (motion-review F1), rather than stopping where the opening was
    // and starting back from rest after a pause.
    public static readonly double CloseSurfaceDelay = NoticeMotion.PanelCloseSurface.Start;
    public static double CloseSurfaceStart(bool turned) => NoticeMotion.ReturnSurfaceFor(turned).Start;

    // motion-review F2: a move turned back part way only has the way already covered to go back (NoticeMotion.RemainingShare).
    public const double RedirectMin = NoticeMotion.RedirectMin;
    public static double RemainingShare(params (double Now, double Left, double Target)[] sizes) => NoticeMotion.RemainingShare(sizes);
}
