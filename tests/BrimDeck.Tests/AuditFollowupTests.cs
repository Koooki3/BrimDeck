using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

// The follow-up of the 2026-10-24 audit (group "followup"): notices keep the panel's animation length range, and the protocol's
// Clip drops the same invisible characters as NoticeText.Clean.
internal static class AuditFollowupTests
{
    // An emoji sequence held together by the zero-width joiner (woman technologist).
    private const string Coder = "\U0001F469\u200D\U0001F4BB";
    private const string Grin = "\U0001F600";

    public static void Run(Action<string, bool> check)
    {
        AnimationLengths(check);
        ClipCleans(check);
    }

    private static void AnimationLengths(Action<string, bool> check)
    {
        check("followup: notices keep the animation length to the settings' 100-1000 ms", NoticeMotion.Length(5, true) == 100 && NoticeMotion.Length(5000, true) == 1000
            && NoticeMotion.Length(360, false) == 0);
    }

    private static void ClipCleans(Action<string, bool> check)
    {
        // The right-to-left mark stays (core2 C-L11: right-to-left text needs it); overrides and isolates go.
        check("followup: Clip drops zero-width, bidi, BOM and tag characters",
            NoticeProtocol.Clip("a\u200Bb\u2066c\u2069\uFEFFd\u202Ee\u200Ff\U000E0041g", 100) == "abcde\u200Ffg");
        check("followup: Clip turns line and paragraph separators into line breaks", NoticeProtocol.Clip("x\u2028y\u2029z", 100) == "x\ny\nz");
        check("followup: Clip keeps an emoji sequence's joiner, and still its line breaks, tabs and control rules",
            NoticeProtocol.Clip(Coder, 100) == Coder && NoticeProtocol.Clip("a\u0007b\r\nc\td", 100) == "a b\nc\td");
        check("followup: Clip counts the limit after cleaning and never splits a pair", NoticeProtocol.Clip("\u200B\u200Babc", 2) == "ab"
            && NoticeProtocol.Clip("a\U000E0041" + Grin, 2) == "a" && NoticeProtocol.Clip("a" + Grin, 3) == "a" + Grin);
        string mixed = "t\u200Bi\u2066t\u2069l\u2028e " + Coder;
        check("followup: Clip and NoticeText.Clean agree on what they drop", NoticeProtocol.Clip(mixed, 1000) == NoticeText.Clean(mixed)
            && NoticeText.Clean("x\u2029y", singleLine: true) == "x y");
        var message = new NoticeMessage { Title = "Bu\u200Bild\u202E", Body = "line\u2028next", AppName = "m\uFEFFake" };
        check("followup: validation cleans the texts a sender sent", NoticeProtocol.Validate(message) is null && message.Title == "Build"
            && message.Body == "line\nnext" && message.AppName == "make");
    }
}
