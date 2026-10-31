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
        // U+20BB7 (a CJK ideograph outside the BMP) is two UTF-16 units; the letter must be the whole character, not the
        // second ideograph after the surrogate pair was skipped, and not half of a pair.
        string astralName = "\U00020BB7野", astralLetter = CompactMusicLayout.RingLetter(astralName);
        check("Ring letter keeps a supplementary-plane character whole", astralLetter == "\U00020BB7" && astralLetter.Length == 2 && char.IsSurrogatePair(astralLetter, 0));
        check("Ring letter upper-cases a supplementary-plane letter", CompactMusicLayout.RingLetter("\U00010428x") == "\U00010400");
        check("Ring letter skips punctuation before a supplementary-plane character", CompactMusicLayout.RingLetter("[ \U00020BB7]") == "\U00020BB7");
        check("Ring letter ignores a lone surrogate", CompactMusicLayout.RingLetter("\ud842x") == "X");
        check("Ring letter is empty without a letter or digit", CompactMusicLayout.RingLetter("") == "" && CompactMusicLayout.RingLetter("  -_ ") == "" && CompactMusicLayout.RingLetter(null) == "");
    }
}
