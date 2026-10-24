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

    // Text as a sender wrote it, without the invisible characters that let it pass for something else (sec-L4). The rules
    // live with the protocol, whose Clip drops the same characters (NoticeProtocol.Clean).
    // Whether a text reads right to left: its first letter is Hebrew, Arabic or another right-to-left script, so that a
    // notice in such a language is laid out and aligned as its readers expect (break-ui test). Digits and punctuation decide nothing.
    public static bool RightToLeft(string text)
    {
        foreach (char c in text)
        {
            if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF') { if (char.IsLetter(c)) return true; continue; }
            if (char.IsLetter(c)) return false;
        }
        return false;
    }

    public static string Clean(string text, bool singleLine = false) => BrimDeck.Protocol.NoticeProtocol.Clean(text, singleLine);

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
