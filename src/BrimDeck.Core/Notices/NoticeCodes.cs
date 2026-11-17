using System.Runtime.CompilerServices;

namespace BrimDeck.Core.Notices;

// How a code in a notice's words is written: left as it is, hidden behind dots, or worded for a screen reader, which must not be
// handed dots to read out one by one.
public enum CodeMask { Off, Dots, Spoken }

// A notice's title and body with the verification codes in them hidden (when asked), and the code that can be copied.
public sealed class NoticeWords
{
    private readonly (int Start, int Length)[] _titleSpans, _bodySpans;
    private readonly string _rawTitle, _rawBody;

    internal NoticeWords(string title, string body, string maskedTitle, string maskedBody, (int, int)[] titleSpans, (int, int)[] bodySpans, VerificationCodeHit? hit)
    {
        _rawTitle = title; _rawBody = body; Title = maskedTitle; Body = maskedBody; _titleSpans = titleSpans; _bodySpans = bodySpans; Hit = hit;
    }

    // The words with the codes as dots of their own length.
    public string Title { get; }
    public string Body { get; }
    // The first code recognised, or null when there is none (or nothing is hidden).
    public VerificationCodeHit? Hit { get; }
    public bool HasCode => Hit is not null;
    // The words as a screen reader should hear them: each code is "verification code hidden".
    public string SpokenTitle => Spoken(_rawTitle, _titleSpans);
    public string SpokenBody => Spoken(_rawBody, _bodySpans);

    private static string Spoken(string text, (int Start, int Length)[] spans)
    {
        if (spans.Length == 0) return text;
        string hidden = Loc.T("（验证码已隐藏）", " (verification code hidden) ");
        var result = new System.Text.StringBuilder();
        int at = 0;
        foreach (var (start, length) in spans.OrderBy(span => span.Start))
        {
            if (start < at) continue;
            result.Append(text, at, start - at).Append(hidden);
            at = start + length;
        }
        return result.Append(text, at, text.Length - at).ToString();
    }
}

// Applies VerificationCode to notices: what the screens show, what a screen reader hears, and what is written to the encrypted
// history. Nothing here writes, logs or keeps a code anywhere but in the notice itself.
public static class NoticeCodes
{
    private const int Rounds = 4;

    private sealed record Entry(string Title, string Body, NoticeWords Words);

    // A notice's words are read again only when its title or body changed.
    private static readonly ConditionalWeakTable<Notice, Entry> Cache = new();

    // The notice's words; with on false, as they are.
    public static NoticeWords Words(Notice notice, bool on)
    {
        if (!on) return Plain(notice.Title, notice.Body);
        if (Cache.TryGetValue(notice, out var entry) && ReferenceEquals(entry.Title, notice.Title) && ReferenceEquals(entry.Body, notice.Body)) return entry.Words;
        var words = Words(notice.Title, notice.Body);
        Cache.AddOrUpdate(notice, new Entry(notice.Title, notice.Body, words));
        return words;
    }

    private static NoticeWords Plain(string title, string body) => new(title, body, title, body, [], [], null);

    // Finds a code, hides it, and looks again, so that a second code (or the same one written another way) is hidden too; then
    // hides the first code wherever else it stands as it is.
    public static NoticeWords Words(string? title, string? body)
    {
        title ??= ""; body ??= "";
        var first = VerificationCode.Find(title, body);
        if (first is null) return Plain(title, body);
        string maskedTitle = title, maskedBody = body;
        var titleSpans = new List<(int, int)>(); var bodySpans = new List<(int, int)>();
        var hit = first;
        for (int round = 0; round < Rounds && hit is not null; round++)
        {
            if (hit.InTitle) { maskedTitle = VerificationCode.Mask(maskedTitle, hit.Start, hit.Length); titleSpans.Add((hit.Start, hit.Length)); }
            else { maskedBody = VerificationCode.Mask(maskedBody, hit.Start, hit.Length); bodySpans.Add((hit.Start, hit.Length)); }
            hit = VerificationCode.Find(maskedTitle, maskedBody);
        }
        maskedTitle = MaskLiteral(maskedTitle, first.Code, titleSpans);
        maskedBody = MaskLiteral(maskedBody, first.Code, bodySpans);
        return new(title, body, maskedTitle, maskedBody, [.. titleSpans], [.. bodySpans], first);
    }

    // The code where it still stands in the text as written, not set off by letters or digits: the first few places, in one pass.
    private static string MaskLiteral(string text, string code, List<(int, int)> spans)
    {
        char[]? letters = null;
        int found = 0;
        for (int at = text.IndexOf(code, StringComparison.Ordinal); at >= 0 && found < 16; at = text.IndexOf(code, at + code.Length, StringComparison.Ordinal))
        {
            bool alone = (at == 0 || !char.IsAsciiLetterOrDigit(text[at - 1])) && (at + code.Length == text.Length || !char.IsAsciiLetterOrDigit(text[at + code.Length]));
            if (!alone) continue;
            letters ??= text.ToCharArray();
            Array.Fill(letters, '•', at, code.Length); spans.Add((at, code.Length)); found++;
        }
        return letters is null ? text : new string(letters);
    }

    // What the encrypted history keeps of a notice: with codes on, its words with the codes hidden (the notice in memory keeps the
    // code, so that it can be copied until BrimDeck exits); otherwise the notice itself.
    public static Notice ForHistory(Notice notice, bool on)
    {
        if (!on) return notice;
        var words = Words(notice, true);
        if (!words.HasCode) return notice;
        var copy = notice.Copy();
        copy.Title = words.Title; copy.Body = words.Body;
        return copy;
    }
}
