using System.Text;

namespace BrimDeck.Core.ClipboardSync;

// The clipboard's HTML form (CF_HTML) is a short header of byte offsets followed by a document in which two comments mark
// the copied fragment. A sync service carries the fragment alone.
public static class ClipboardHtml
{
    private const string Start = "<!--StartFragment-->", End = "<!--EndFragment-->";

    public static string Fragment(string html)
    {
        int from = html.IndexOf(Start, StringComparison.OrdinalIgnoreCase), to = html.LastIndexOf(End, StringComparison.OrdinalIgnoreCase);
        if (from >= 0 && to > from) return html[(from + Start.Length)..to];
        // No marks: the header's offsets, which count the bytes of the UTF-8 text.
        var bytes = Encoding.UTF8.GetBytes(html);
        int start = Offset(html, "StartFragment:"), end = Offset(html, "EndFragment:");
        return start > 0 && end > start && end <= bytes.Length ? Encoding.UTF8.GetString(bytes, start, end - start) : "";
    }

    private static int Offset(string html, string name)
    {
        int at = html.IndexOf(name, 0, Math.Min(html.Length, 512), StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;
        int digits = at + name.Length, end = digits;
        while (end < html.Length && char.IsAsciiDigit(html[end])) end++;
        return int.TryParse(html.AsSpan(digits, end - digits), out int value) ? value : -1;
    }

    public static string Wrap(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before = "<html><body>" + Start, after = End + "</body></html>";
        int startHtml = string.Format(header, 0, 0, 0, 0).Length;
        int startFragment = startHtml + before.Length, endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        return string.Format(header, startHtml, endFragment + after.Length, startFragment, endFragment) + before + fragment + after;
    }
}
