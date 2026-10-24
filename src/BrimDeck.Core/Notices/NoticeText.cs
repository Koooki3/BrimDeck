using System.Globalization;

namespace BrimDeck.Core.Notices;

public static class NoticeText
{
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 10) return span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + Loc.T(" 秒", " s");
        if (span.TotalMinutes < 1) return ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + Loc.T(" 秒", " s");
        if (span.TotalHours < 1) return Loc.T($"{(int)span.TotalMinutes} 分 {span.Seconds} 秒", $"{(int)span.TotalMinutes} min {span.Seconds} s");
        return Loc.T($"{(int)span.TotalHours} 小时 {span.Minutes} 分", $"{(int)span.TotalHours} h {span.Minutes} min");
    }

    // "刚刚", "5 分钟前", then the clock time today and the date before that.
    public static string Relative(DateTimeOffset time, DateTimeOffset now)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1)) return Loc.T("刚刚", "Just now");
        if (age < TimeSpan.FromHours(1)) return Loc.T($"{(int)age.TotalMinutes} 分钟前", $"{(int)age.TotalMinutes} min ago");
        var local = time.ToLocalTime();
        if (local.Date == now.ToLocalTime().Date) return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == now.ToLocalTime().Date.AddDays(-1)) return Loc.T("昨天 ", "Yesterday ") + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Count(int count) => Loc.T($"{count} 条新通知", Loc.Count(count, "new notice", "new notices"));

    // The single line an arriving notice shows under its title, by the application's privacy choice.
    public static (string Title, string Summary) Presented(Notice notice, NoticePrivacy privacy) => privacy switch
    {
        NoticePrivacy.AppOnly => (notice.AppName, Loc.T("新通知", "New notice")),
        NoticePrivacy.TitleOnly => (notice.Title.Length > 0 ? notice.Title : notice.AppName, ""),
        _ => (notice.Title.Length > 0 ? notice.Title : notice.AppName, Summary(notice.Body))
    };

    // The first line of the body, with runs of white space collapsed.
    public static string Summary(string body)
    {
        var line = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // Text as a sender wrote it, without the invisible characters that let it pass for something else (sec-L4): format
    // characters (zero-width spaces, bidi marks, word joiners, the BOM, Unicode tags) are dropped, the zero-width joiner of
    // emoji sequences excepted; line and paragraph separators, which WPF breaks lines on, become a line break, or a space
    // in a single line.
    public static string Clean(string text, bool singleLine = false)
    {
        int i = 0;
        for (; i < text.Length; i++)
            if (Unwanted(text, i, out _)) break;
        if (i == text.Length) return text;
        var builder = new System.Text.StringBuilder(text.Length);
        builder.Append(text, 0, i);
        for (; i < text.Length; i++)
        {
            bool pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            if (Unwanted(text, i, out bool breaks)) { if (breaks) builder.Append(singleLine ? ' ' : '\n'); }
            else { builder.Append(text[i]); if (pair) builder.Append(text[i + 1]); }
            if (pair) i++;
        }
        return builder.ToString();
    }

    private static bool Unwanted(string text, int index, out bool breaks)
    {
        var category = char.GetUnicodeCategory(text, index);
        breaks = category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
        return breaks || category == UnicodeCategory.Format && text[index] != '‍';
    }

    public static string CategoryName(NoticeCategory category) => category switch
    {
        NoticeCategory.Task => Loc.T("任务", "Task"), NoticeCategory.Success => Loc.T("完成", "Done"), NoticeCategory.Warning => Loc.T("警告", "Warning"),
        NoticeCategory.Error => Loc.T("失败", "Failed"), NoticeCategory.Info => Loc.T("提示", "Info"), _ => Loc.T("消息", "Message")
    };

    public static string OriginName(NoticeOrigin origin, string? distro) => origin switch
    {
        NoticeOrigin.Windows => "Windows", NoticeOrigin.Wsl => "WSL" + (distro is { Length: > 0 } ? " · " + distro : ""),
        NoticeOrigin.Cli => "brim-notify" + (distro is { Length: > 0 } ? " · " + distro : ""), _ => "BrimDeck"
    };
}
