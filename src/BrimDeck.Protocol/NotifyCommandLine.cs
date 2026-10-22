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

        Options:
          -b, --body TEXT        Body text
          -s, --source NAME      Name the notice comes from (default: brim-notify)
          -i, --icon PATH        Local icon file
          -c, --category KIND    message, task, success, warning, error or info
          -p, --priority LEVEL   low, normal or high (a hint; BrimDeck's rules decide)
          -r, --replace KEY      Update the earlier notice sent with the same key
              --id ID            Message id; a retry with the same id is shown once
              --progress N       Task progress, 0 to 100
          -t, --title TEXT       Title (for run: replaces the program name)
          -h, --help             Show this help
              --version          Show the version
        """.ReplaceLineEndings();

    private static readonly HashSet<string> Categories = ["message", "task", "success", "warning", "error", "info"];
    private static readonly HashSet<string> Priorities = ["low", "normal", "high"];

    public static NotifyCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new(NotifyCommandKind.Help);
        int index = 0;
        var kind = NotifyCommandKind.Send;
        switch (args[0])
        {
            case "run": kind = NotifyCommandKind.Run; index = 1; break;
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
