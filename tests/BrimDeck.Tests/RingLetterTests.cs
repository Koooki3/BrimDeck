using BrimDeck.Core;

// The letter in the middle of a collapsed quota ring (CompactMusicLayout.RingLetter): the first letter or digit of the application's
// name, upper case, so rings are told apart by more than their color.
internal static class RingLetterTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("Ring letter is the upper-case first letter", CompactMusicLayout.RingLetter("claude") == "C" && CompactMusicLayout.RingLetter("Codex") == "C");
        check("Ring letter skips leading punctuation and spaces", CompactMusicLayout.RingLetter("  [dsh]") == "D" && CompactMusicLayout.RingLetter("-- kimi") == "K");
        check("Ring letter takes a leading digit", CompactMusicLayout.RingLetter("1Password") == "1");
        check("Ring letter keeps a CJK character as it is", CompactMusicLayout.RingLetter("\u6587\u5fc3") == "\u6587");
        check("Ring letter is empty without a letter or digit", CompactMusicLayout.RingLetter("") == "" && CompactMusicLayout.RingLetter("  -_ ") == "" && CompactMusicLayout.RingLetter(null) == "");
    }
}
