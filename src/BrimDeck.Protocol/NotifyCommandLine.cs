using System.Globalization;

namespace BrimDeck.Protocol;

public enum NotifyCommandKind { Send, Run, Status, Stdio, Help, Version, Error }

// A parsed brim-notify command line. Send carries the notice; Run carries the notice options and the program to run.
public sealed record NotifyCommand(NotifyCommandKind Kind, NoticeMessage? Message = null, IReadOnlyList<string>? Program = null, string? Error = null);

// The command line shared by the Windows sender (BrimDeck.Notify.exe) and the WSL agent's brim-notify.
public static class NotifyCommandLine
{
    // In the platform's own line endings, whatever the source file's are (Git on Windows checks it out with CRLF).
    public static readonly string Usage = """
        brim-notify TITLE [BODY] [options]       Show a notice in BrimDeck
        brim-notify run [options] -- PROGRAM ... Run a program, then show its result
        brim-notify status                       Check that BrimDeck receives notices
        brim-notify timer DURATION [TITLE]       Count down (25m, 1h30m, 90s), then remind
        brim-notify timer --at TIME [TITLE]      Count down to a time (15:30)
        brim-notify stopwatch [TITLE]            Show the time elapsed since now
        brim-notify event TITLE --at TIME        An event; reminds before it starts
        brim-notify stop KEY                     End a timer, stopwatch or event

        Options:
          -b, --body TEXT        Body text
          -s, --source NAME      Name the notice comes from (default: brim-notify)
          -i, --icon PATH        Local icon file (a relative path is from the current folder)
          -c, --category KIND    message, task, success, warning, error or info
          -p, --priority LEVEL   low, normal or high (a hint; BrimDeck's rules decide)
          -r, --replace KEY      Update the earlier notice sent with the same key
              --id ID            Message id; a retry with the same id is shown once
              --progress N       Task progress, 0 to 100
          -t, --title TEXT       Title (for run: replaces the program name)
              --at TIME          When: 15:30, 2026-10-24 15:30, or ISO 8601 with offset
              --in DURATION      When, from now: 25m, 1h30m, 90s
              --ends TIME        An event's end
              --remind DURATION  How long before an event's start to remind (default 0)
              --kind KIND        alarm or mail, for a plain notice
          -h, --help             Show this help
              --version          Show the version
        """.ReplaceLineEndings();

    private static readonly HashSet<string> Categories = ["message", "task", "success", "warning", "error", "info"];
    private static readonly HashSet<string> Priorities = ["low", "normal", "high"];
    private static readonly HashSet<string> PlainKinds = [NoticeTimes.Alarm, NoticeTimes.Mail];

    public static NotifyCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new(NotifyCommandKind.Help);
        int index = 0;
        var kind = NotifyCommandKind.Send;
        // timer, stopwatch, event and stop are sends of a time-kind notice; timeKind says which.
        string? timeKind = null;
        switch (args[0])
        {
            case "run": kind = NotifyCommandKind.Run; index = 1; break;
            case "timer": timeKind = NoticeTimes.Countdown; index = 1; break;
            case "stopwatch": timeKind = NoticeTimes.Stopwatch; index = 1; break;
            case "event": timeKind = NoticeTimes.Event; index = 1; break;
            case "stop": timeKind = "stop"; index = 1; break;
            case "status": return args.Count == 1 ? new(NotifyCommandKind.Status) : Fail("status takes no arguments");
            case "--stdio": return args.Count == 1 ? new(NotifyCommandKind.Stdio) : Fail("--stdio takes no arguments");
            case "-h" or "--help" or "help": return new(NotifyCommandKind.Help);
            case "--version": return new(NotifyCommandKind.Version);
        }
        var message = new NoticeMessage { Origin = "cli" };
        var positional = new List<string>();
        List<string>? program = null;
        for (; index < args.Count; index++)
        {
            string arg = args[index];
            if (arg == "--")
            {
                if (kind == NotifyCommandKind.Run) { program = args.Skip(index + 1).ToList(); break; }
                positional.AddRange(args.Skip(index + 1)); break;
            }
            if (arg.Length > 1 && arg[0] == '-' && !IsNumber(arg))
            {
                string name = arg, value;
                int equals = arg.IndexOf('=');
                if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0) { name = arg[..equals]; value = arg[(equals + 1)..]; }
                else if (name is "-h" or "--help") return new(NotifyCommandKind.Help);
                else
                {
                    if (index + 1 >= args.Count) return Fail(arg + " needs a value");
                    value = args[++index];
                }
                switch (name)
                {
                    case "-b" or "--body": message.Body = value; break;
                    case "-s" or "--source": message.AppName = value; break;
                    case "-i" or "--icon": message.Icon = value; break;
                    case "-t" or "--title": message.Title = value; break;
                    case "-r" or "--replace": message.ReplaceId = value; break;
                    case "--id": message.EventId = value; break;
                    case "-c" or "--category":
                        if (!Categories.Contains(value.ToLowerInvariant())) return Fail("unknown category: " + value);
                        message.Category = value.ToLowerInvariant(); break;
                    case "-p" or "--priority":
                        if (!Priorities.Contains(value.ToLowerInvariant())) return Fail("unknown priority: " + value);
                        message.Priority = value.ToLowerInvariant(); break;
                    case "--at": message.At = value; break;
                    case "--ends": message.Until = value; break;
                    case "--in":
                        if (NoticeTimes.ParseDuration(value) is not { } inSpan || inSpan < NoticeTimes.MinDuration || inSpan > NoticeTimes.MaxDuration)
                            return Fail("--in takes a duration from 1s to 31d, for example 25m or 1h30m");
                        message.InMs = (long)inSpan.TotalMilliseconds; break;
                    case "--remind":
                        if (NoticeTimes.ParseDuration(value) is not { } lead || lead > NoticeTimes.MaxLead)
                            return Fail("--remind takes a duration up to 7d, for example 10m");
                        message.LeadMs = (long)lead.TotalMilliseconds; break;
                    case "--kind":
                        if (timeKind is not null || !PlainKinds.Contains(value.ToLowerInvariant())) return Fail("--kind takes alarm or mail");
                        message.Kind = value.ToLowerInvariant(); break;
                    case "--progress":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || !double.IsFinite(percent) || percent < 0 || percent > 100)
                            return Fail("--progress takes a number from 0 to 100");
                        message.Progress = percent / 100; break;
                    default: return Fail("unknown option: " + name);
                }
                continue;
            }
            if (kind == NotifyCommandKind.Run) { program = args.Skip(index).ToList(); break; }
            positional.Add(arg);
        }
        if (kind == NotifyCommandKind.Run)
        {
            if (program is not { Count: > 0 }) return Fail("run needs a program, for example: brim-notify run -- make test");
            if (positional.Count > 0) return Fail("unexpected argument: " + positional[0]);
            message.Command = Path.GetFileName(program[0]);
            message.Category ??= "task";
            return new(NotifyCommandKind.Run, message, program);
        }
        if (timeKind is not null) return TimeCommand(timeKind, message, positional);
        if (message.At is not null || message.Until is not null || message.InMs is not null || message.LeadMs is not null)
            return Fail("--at, --in, --ends and --remind are for timer and event");
        if (positional.Count > 2) return Fail("unexpected argument: " + positional[2] + " (quote a title or body with spaces)");
        if (positional.Count > 0)
        {
            if (message.Title is not null) return Fail("the title is given twice");
            message.Title = positional[0];
        }
        if (positional.Count > 1)
        {
            if (message.Body is not null) return Fail("the body is given twice");
            message.Body = positional[1];
        }
        if (string.IsNullOrWhiteSpace(message.Title) && string.IsNullOrWhiteSpace(message.Body)) return Fail("a title or body is needed");
        message.AppName ??= "brim-notify";
        return new(NotifyCommandKind.Send, message);
    }

    // timer, stopwatch, event and stop. Each running one needs a key for stop; without -r one is made up (and printed).
    private static NotifyCommand TimeCommand(string timeKind, NoticeMessage message, List<string> positional)
    {
        bool timed = message.At is not null || message.Until is not null || message.InMs is not null || message.LeadMs is not null;
        if (timeKind == "stop")
        {
            if (positional.Count != 1 || message.ReplaceId is not null) return Fail("stop takes one key: brim-notify stop KEY");
            if (timed || message.Title is not null || message.Body is not null || message.Kind is not null) return Fail("stop takes only a key and --source");
            message.ReplaceId = positional[0]; message.Stop = true; message.Kind = NoticeTimes.Stopwatch;
            message.AppName ??= "brim-notify";
            return new(NotifyCommandKind.Send, message);
        }
        if (message.Kind is not null) return Fail("--kind is for a plain notice");
        message.Kind = timeKind;
        // timer: a duration may come first, instead of --in.
        if (timeKind == NoticeTimes.Countdown && message.At is null && message.InMs is null && positional.Count > 0)
        {
            if (NoticeTimes.ParseDuration(positional[0]) is not { } span || span < NoticeTimes.MinDuration || span > NoticeTimes.MaxDuration)
                return Fail("timer needs a duration from 1s to 31d (25m, 1h30m, 90s) or --at TIME");
            message.InMs = (long)span.TotalMilliseconds;
            positional.RemoveAt(0);
        }
        if (timeKind is NoticeTimes.Countdown or NoticeTimes.Event && message.At is null && message.InMs is null)
            return Fail(timeKind == NoticeTimes.Event ? "event needs --at TIME or --in DURATION" : "timer needs a duration or --at TIME");
        if (message.At is not null && message.InMs is not null) return Fail("give either --at or --in");
        if (timeKind == NoticeTimes.Stopwatch && timed) return Fail("stopwatch starts now and takes no time options");
        if (timeKind != NoticeTimes.Event && (message.Until is not null || message.LeadMs is not null)) return Fail("--ends and --remind are for event");
        if (positional.Count > 2) return Fail("unexpected argument: " + positional[2] + " (quote a title or body with spaces)");
        if (positional.Count > 0) { if (message.Title is not null) return Fail("the title is given twice"); message.Title = positional[0]; }
        if (positional.Count > 1) { if (message.Body is not null) return Fail("the body is given twice"); message.Body = positional[1]; }
        if (timeKind == NoticeTimes.Event && string.IsNullOrWhiteSpace(message.Title)) return Fail("event needs a title");
        message.ReplaceId ??= timeKind + "-" + Guid.NewGuid().ToString("N")[..8];
        message.AppName ??= "brim-notify";
        return new(NotifyCommandKind.Send, message);
    }

    // -i as BrimDeck can use it (W9): BrimDeck takes only a full path, which a relative one is made into from the current
    // folder. On Windows every relative path is; in WSL a name with a slash is, and a bare name only when such a file is in the
    // current folder, since otherwise it is an icon name of the desktop's theme ("dialog-information"). Web addresses and
    // file:// links are left as they are, for BrimDeck to refuse or read.
    public static string? ResolveIcon(string? icon, bool windows, Func<string, string> fullPath, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(icon) || icon.Contains("://", StringComparison.Ordinal)) return icon;
        try
        {
            if (windows) return Path.IsPathFullyQualified(icon) ? icon : fullPath(icon);
            if (icon.StartsWith('/')) return icon;
            if (icon.Contains('/')) return fullPath(icon);
            return exists(icon) ? fullPath(icon) : icon;
        }
        // A path that cannot be made full (invalid characters) goes as given; BrimDeck refuses it.
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return icon; }
    }

    // A send whose key the sender prints, so that a script can stop it later ("brim-notify stop KEY").
    public static bool PrintsKey(NoticeMessage message) => message.Stop != true && message.Kind is NoticeTimes.Countdown or NoticeTimes.Stopwatch or NoticeTimes.Event;

    // The notice for a finished "run": BrimDeck writes the text from ExitCode, DurationMs and Command, in its own language.
    public static NoticeMessage RunResult(NoticeMessage options, int exitCode, TimeSpan duration)
    {
        var message = new NoticeMessage
        {
            Origin = options.Origin, AppName = options.AppName ?? "brim-notify", Icon = options.Icon, Title = options.Title, Body = options.Body,
            ReplaceId = options.ReplaceId, EventId = options.EventId, Priority = options.Priority, Command = options.Command,
            ExitCode = exitCode, DurationMs = (long)Math.Max(0, duration.TotalMilliseconds),
            Category = exitCode == 0 ? "success" : "error"
        };
        return message;
    }

    private static bool IsNumber(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    private static NotifyCommand Fail(string error) => new(NotifyCommandKind.Error, Error: error);
}
