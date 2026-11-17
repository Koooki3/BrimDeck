using System.Text;
using System.Text.RegularExpressions;

namespace BrimDeck.Core.Notices;

// A verification code recognised in a notice: the code as written (full-width characters made ordinary), whether it stands in the
// title, and where it is in the field it was found in (the title or the body, as they are, UTF-16 units).
public sealed record VerificationCodeHit(string Code, bool InTitle, int Start, int Length, int Score)
{
    // What a paste should receive: digits grouped with spaces or hyphens ("123 456") are pasted together; a code with letters
    // keeps its hyphen ("K7Q2-9XMP"), which is part of it.
    public string CopyText => Code.All(c => c is >= '0' and <= '9' or ' ' or '-') ? new string(Code.Where(c => c is >= '0' and <= '9').ToArray()) : Code;
}

// Recognises a verification code in a notice's words, and masks it. Ported from the otp.rs of xiaotang-daozai (MIT, see
// ThirdPartyNotices.txt), whose scoring is itself taken from pulse-mail: a number is a code only when a keyword ("验证码", "code"
// ...) stands near it and nothing says it is an amount, a phone or order number, a date, a time or a link; the threshold is
// kept so that a doubtful number is left alone rather than hidden or offered. Two changes: it reads a notice's title and body
// (there is no HTML to find emphasised text in), and every pattern runs in the engine that cannot backtrack, over a text cut to
// what a notice may hold.
public static class VerificationCode
{
    // The score a number must reach.
    public const int Threshold = 65;
    // What a notice's fields can hold (NoticeWire.MaxTitle, MaxBody): a longer text is cut before it is read, so that the work
    // is bounded whatever arrives.
    public const int MaxTitle = 512, MaxBody = 4000;

    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Strong = new(
        @"[验驗][证證][码碼]|校[验驗][码碼]|[确確][认認][码碼]|[动動][态態](?:密)?[码碼]|安全[码碼]|登[录錄入][码碼]|激活[码碼]|" +
        @"一次性(?:密[码碼]|口令)|\botp\b|\bpin(?:\s+code)?\b|one[-\s]?time\s+(?:code|password|passcode)|" +
        @"verification\s+code|security\s+code|auth(?:entication)?\s+code|" +
        @"passcode|\bcode\b|認証コード|인증번호", Options);

    private static readonly Regex Weak = new(
        @"登[录錄入陆陸]|[验驗][证證]|校[验驗]|身份|[确確][认認]|安全|" +
        @"sign[-\s]?in|log[-\s]?in|verify|verification|confirm|authenticate", Options);

    // A letter prefix, groups of 3-4 digits with a separator, 4-8 digits, 4-8 letters and digits.
    private static readonly Regex Candidate = new(
        @"(?:[A-Za-z]{1,3}-)?(?:[0-9]{3,4}[- ][0-9]{3,4}|[0-9]{4}-[0-9]{4}|[A-Za-z0-9]{3,4}-[A-Za-z0-9]{3,4}|[0-9]{4,8}|[A-Za-z0-9]{4,8})", Options);

    private static readonly Regex Clock = new(@"[0-9]{1,2}:[0-9]{2}", Options);

    private static readonly string[] OrderWords =
    [
        "订单", "单号", "快递", "运单", "物流", "包裹", "编号", "序号", "发票", "金额", "合计", "实付", "应付", "电话", "手机号", "会议号", "会议", "卡尾号", "尾号", "邮编", "sku",
        "訂單", "單號", "快遞", "運單", "編號", "序號", "發票", "金額", "合計", "實付", "應付", "電話", "手機號", "會議", "尾號", "郵編",
        "order", "track", "invoice", "amount", "phone", "meeting", "card ending"
    ];
    private static readonly string[] MoneyWords = ["¥", "￥", "$", "€", "元", "价格", "金额", "合计", "实付", "应付", "價格", "金額", "合計", "實付", "應付"];
    private static readonly string[] PromoWords = ["优惠", "折扣", "促销", "满减", "券", "红包", "coupon", "promo", "discount", "sale", "優惠", "促銷", "滿減", "紅包"];
    private static readonly string[] ExpireWords = ["有效", "过期", "失效", "expire", "valid", "minutes", "分钟", "小时内", "過期", "分鐘", "小時內"];
    private static readonly string[] IsWords = ["验证码是", "验证码为", "校验码是", "驗證碼是", "驗證碼為", "校驗碼是", "code is", "code:"];
    private static readonly char[] Quotes = ['"', '\'', '“', '”', '‘', '’', '「', '」', '`'];

    // Words that are not a code even where a keyword stands before them.
    private static readonly HashSet<string> Prose = new(StringComparer.Ordinal)
    {
        "please", "verify", "confirm", "code", "your", "this", "that", "enter", "click", "continue",
        "account", "email", "password", "security", "valid", "invalid", "expired", "expires", "expire",
        "required", "secret", "secure", "private", "unique", "ready", "active", "inactive", "pending", "sent", "received",
        "request", "requested", "welcome", "thanks", "thank", "regards", "dear", "hello", "team",
        "support", "help", "here", "open", "login", "signin", "sign", "will", "soon", "sample", "example",
        "test", "token", "success", "failure", "correct", "check", "located", "below", "above",
        "copy", "paste", "next", "back", "cancel", "close", "submit", "resend", "retry", "start", "finish",
        "send", "reset", "refresh", "change", "update", "updated", "enabled", "disabled", "complete",
        "only", "none", "normal", "unknown", "missing", "allowed", "denied", "blocked",
        "google", "github", "openai", "apple", "amazon"
    };

    // The first (best) code in a notice's title and body, or null. Deterministic; the work grows with the text's length, which is
    // cut to MaxTitle and MaxBody first.
    public static VerificationCodeHit? Find(string? title, string? body)
    {
        title ??= ""; body ??= "";
        if (title.Length > MaxTitle) title = title[..MaxTitle];
        if (body.Length > MaxBody) body = body[..MaxBody];
        Normalize(title, out var subject, out var subjectMap);
        Normalize(body, out var rest, out var bodyMap);
        string hay = subject + "\n" + rest;
        int subjectEnd = subject.Length + 1;

        (int Score, int Position, bool InSubject, string Text, int Start, int End)? best = null;
        string? alphabetic = null; int alphabeticStart = 0, alphabeticEnd = 0;
        bool ambiguous = false;
        foreach (Match match in Candidate.Matches(hay))
        {
            int start = match.Index, end = match.Index + match.Length;
            // Only an ASCII letter or digit next to it makes it part of a longer word: Chinese text sits against a code as a rule.
            if (start > 0 && AsciiAlnum(hay[start - 1]) || end < hay.Length && AsciiAlnum(hay[end])) continue;
            string text = match.Value;
            if (text.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or ' '))
            {
                if (AlphabeticCodeAt(hay, start, end, text))
                {
                    if (alphabetic is not null) ambiguous |= alphabetic != text;
                    else { alphabetic = text; alphabeticStart = start; alphabeticEnd = end; }
                }
                continue;
            }
            var scored = ScoreAt(hay, start, end, text, subjectEnd);
            if (scored is not { Strong: true }) continue;
            int score = scored.Value.Score;
            bool inSubject = start < subjectEnd;
            bool better = best is not { } b || score > b.Score || score == b.Score && inSubject && !b.InSubject || score == b.Score && inSubject == b.InSubject && start < b.Position;
            if (better) best = (score, start, inSubject, text, start, end);
        }
        if (best is { } found && found.Score >= Threshold) return Hit(found.Text, found.Score, found.Start, found.End, subject.Length, subjectMap, bodyMap);
        if (alphabetic is not null && !ambiguous) return Hit(alphabetic, Threshold, alphabeticStart, alphabeticEnd, subject.Length, subjectMap, bodyMap);
        return null;
    }

    private static VerificationCodeHit Hit(string text, int score, int start, int end, int subjectLength, int[] subjectMap, int[] bodyMap)
    {
        // The span in the field as it was written, not as it was normalised.
        if (start < subjectLength) return new(text, true, subjectMap[start], subjectMap[end - 1] + 1 - subjectMap[start], score);
        int from = start - subjectLength - 1, to = end - subjectLength - 1;
        return new(text, false, bodyMap[from], bodyMap[to - 1] + 1 - bodyMap[from], score);
    }

    // The text with dots of the same length in place of the span (a span outside the text leaves it as it is).
    public static string Mask(string text, int start, int length, char dot = '•')
    {
        if (start < 0 || length <= 0 || start > text.Length || length > text.Length - start) return text;
        return string.Concat(text.AsSpan(0, start), new string(dot, length), text.AsSpan(start + length));
    }

    // Full-width characters become ordinary ones, zero-width characters are dropped and the ideographic space becomes a space;
    // map[i] is where character i of the result stands in the text that came in.
    private static void Normalize(string text, out string result, out int[] map)
    {
        var builder = new StringBuilder(text.Length);
        var positions = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '​' or '‌' or '‍' or '﻿' or '⁠') continue;
            builder.Append(c == '　' ? ' ' : c is >= '！' and <= '～' ? (char)(c - 0xfee0) : c);
            positions.Add(i);
        }
        result = builder.ToString(); map = [.. positions];
    }

    private static bool AsciiAlnum(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool AsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static string Before(string text, int at, int n) { int from = Math.Max(0, at - n); return text.Substring(from, at - from); }
    private static string After(string text, int at, int n) => text.Substring(at, Math.Min(n, text.Length - at));
    private static string Around(string text, int start, int end, int n) => Before(text, start, n) + After(text, end, n);
    private static bool Has(string window, params string[] words) { foreach (var word in words) if (window.Contains(word, StringComparison.OrdinalIgnoreCase)) return true; return false; }

    // Letters alone are accepted only where the code's field is named plainly, so that titles or capitals cannot add up to a
    // code out of ordinary words.
    private static bool AlphabeticCodeAt(string hay, int start, int end, string text)
    {
        string core = text.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (core.Length is < 4 or > 8 || !core.All(AsciiLetter) || Prose.Contains(core.ToLowerInvariant())) return false;
        string prefix = Before(hay, start, 64);
        Match? label = null;
        foreach (Match m in Strong.Matches(prefix))
        {
            if (m.Value.Equals("code", StringComparison.OrdinalIgnoreCase)) continue;
            if (m.Index > 0 && AsciiAlnum(prefix[m.Index - 1])) continue;
            int after = m.Index + m.Length;
            if (after < prefix.Length && AsciiAlnum(prefix[after])) continue;
            label = m;
        }
        if (label is null) return false;
        string rawGap = prefix[(label.Index + label.Length)..];
        string gap = rawGap.Trim().TrimEnd(Quotes).Trim().TrimEnd(':', '：', '=').Trim().ToLowerInvariant();
        if (gap is not ("" or "is" or "为" or "為" or "是")) return false;
        string tail = hay[end..].TrimStart(Quotes).TrimStart(' ', '\t');
        if (tail.Length > 0 && "@/\\_-".Contains(tail[0])) return false;
        if (tail.Length > 1 && tail[0] == '.' && AsciiAlnum(tail[1])) return false;
        bool terminal = tail.Length == 0 || "\r\n.,，。;；!！)）".Contains(tail[0]);
        if (!terminal) return false;
        int lineStart = start == 0 ? 0 : hay.LastIndexOf('\n', start - 1) + 1;
        int lineEnd = hay.IndexOf('\n', end); if (lineEnd < 0) lineEnd = hay.Length;
        bool ownLine = hay[lineStart..lineEnd].Trim().Trim(Quotes) == text;
        if (rawGap.Contains('\n')) { if (!ownLine) return false; }
        else if (!rawGap.Any(c => c is ':' or '：' or '=') && !(gap != "" && core.Count(c => c is >= 'A' and <= 'Z') >= 2)) return false;
        return !Has(Around(hay, start, end, 6), MoneyWords) && !Has(Before(hay, start, 20), OrderWords) && !Has(After(hay, end, 8), OrderWords)
            && !Has(Around(hay, start, end, 40), PromoWords);
    }

    // The score of a number, and whether something besides its shape points to a code (a keyword, the title, its own line, a
    // colon before it); null when it cannot be one.
    private static (int Score, bool Strong)? ScoreAt(string hay, int start, int end, string text, int subjectEnd)
    {
        string core = new(text.Where(char.IsLetterOrDigit).ToArray());
        int digits = core.Count(c => c is >= '0' and <= '9'), letters = core.Count(AsciiLetter);
        if (digits == 0) return null;
        bool mixed = letters > 0, separated = text.Contains('-') || text.Contains(' ');
        int length = core.Length, score;
        if (mixed)
        {
            if (length < 5) return null;
            score = length == 6 ? 20 : separated ? 22 : 14;
        }
        else
        {
            switch (length)
            {
                case 6: score = separated ? 28 : 30; break;
                case 7 or 8: score = separated ? 22 : 18; break;
                case 5: score = 20; break;
                case 4: score = 16; break;
                default: return null;
            }
        }

        string b40 = Before(hay, start, 40), b250 = Before(hay, start, 250), b60 = Before(hay, start, 60), a40 = After(hay, end, 40);

        // Dropped at once: a link, an address, a date, a time, an amount, a phone or order number.
        int left = start; while (left > 0 && !char.IsWhiteSpace(hay[left - 1])) left--;
        int right = end; while (right < hay.Length && !char.IsWhiteSpace(hay[right])) right++;
        string token = hay[left..right];
        if (token.Contains("http", StringComparison.OrdinalIgnoreCase) || token.Contains("www.", StringComparison.OrdinalIgnoreCase) || token.Contains('/') || token.Contains('@')) return null;
        string near = Around(hay, start, end, 6);
        if (Has(near, "年", "月", "日", "有效期至") || Clock.IsMatch(near) || Clock.IsMatch(Before(hay, start, 8))) return null;
        if (Has(near, MoneyWords)) return null;
        // Order, phone and meeting words count only just before the number or just after it: a real code is often followed by
        // "if this was not you ... order number" and a wider window would drop it.
        if (Has(Before(hay, start, 20), OrderWords) || Has(After(hay, end, 8), OrderWords)) return null;
        if (length == 4 && !mixed && int.TryParse(core, out int year) && year is >= 1900 and <= 2100) score -= 35;

        bool strongBefore = Strong.IsMatch(b40);
        if (strongBefore) score += 40;
        else if (Strong.IsMatch(b250)) score += 22;
        else if (Weak.IsMatch(b60)) score += 15;
        bool strongAfter = Strong.IsMatch(a40);
        if (strongAfter) score += 28;

        int lineStart = start == 0 ? 0 : hay.LastIndexOf('\n', start - 1) + 1;
        int lineEnd = hay.IndexOf('\n', start); if (lineEnd < 0) lineEnd = hay.Length;
        string line = hay[lineStart..lineEnd].Trim().TrimEnd('。', '，', ',', '.', '！', '!', '、', ';', '；').Trim();
        bool ownLine = line == text;
        bool colon = hay[..start].TrimEnd().EndsWith(':');
        bool inSubject = start < subjectEnd;
        if (mixed && !(ownLine || colon || inSubject)) return null;
        if (ownLine) score += 15;
        if (colon) score += 10;
        if (inSubject) score += 15;
        if (Has(b40, IsWords)) score += 5;
        if (Has(a40, ExpireWords) || Has(b60, ExpireWords)) score += 5;
        if (Has(Around(hay, start, end, 40), PromoWords)) score -= 30;
        return (score, strongBefore || strongAfter || inSubject || ownLine || colon);
    }
}
