using System.Diagnostics;
using System.Text;

namespace BrimDeck.Protocol;

// What one brim-notify differs in: how a message reaches BrimDeck, how "run" starts a program, and what it says when BrimDeck
// cannot be reached. Start returns the started process, or null with the exit code to return (it printed why); Report then
// says whether that code still counts as the program's result, sent to BrimDeck as a finished run (a program not found on
// Windows, as cmd reported it before).
public sealed record NotifySender(
    string Version,
    Func<NoticeMessage, Task<NoticeMessage?>> Send,
    Func<IReadOnlyList<string>, (Process? Process, int Failure, bool Report)> Start,
    string Unreachable,
    string NotReceiving);

// brim-notify's commands, shared by the Windows sender (BrimDeck.Notify.exe) and the WSL agent (q-P1.5). Exit codes: 0
// delivered (or filtered by the user's rules), 1 usage error (a message too large to send included), 2 refused by BrimDeck
// (a stop that found nothing to stop included), 3 BrimDeck could not be reached or did not answer in time; "run" exits with
// the program's own code.
public static class NotifyCli
{
    public const int Usage = 1, Refused = 2, Unreachable = 3;
    // The reply brim-notify makes up itself for a message too large for a frame even with its texts cut (W7).
    public const string TooLarge = "too-large";

    public static async Task<int> RunAsync(NotifyCommand command, NotifySender sender)
    {
        switch (command.Kind)
        {
            case NotifyCommandKind.Help: Console.WriteLine(NotifyCommandLine.Usage); return 0;
            case NotifyCommandKind.Version: Console.WriteLine(sender.Version); return 0;
            case NotifyCommandKind.Error: Console.Error.WriteLine("brim-notify: " + command.Error); Console.Error.WriteLine("Try brim-notify --help."); return Usage;
            case NotifyCommandKind.Stdio: Console.Error.WriteLine("brim-notify: --stdio is for the Windows sender."); return Usage;
            case NotifyCommandKind.Status:
            {
                var reply = await sender.Send(new NoticeMessage { Type = NoticeTypes.Status });
                if (reply is null) { Console.WriteLine(sender.NotReceiving); return Unreachable; }
                // Written but not answered ("no-reply": an agent ending, or an old one still holding the socket), or refused:
                // not confirmed, so not reported as receiving (review-batch1 item 6).
                if (reply.Result != NoticeResults.Accepted)
                {
                    Console.WriteLine("BrimDeck did not confirm that it is receiving notices" + (reply.Detail is { Length: > 0 } why ? " (" + why + ")." : "."));
                    return Unreachable;
                }
                Console.WriteLine("BrimDeck is receiving notices." + (reply.Detail is { Length: > 0 } detail ? " " + detail : ""));
                return 0;
            }
            case NotifyCommandKind.Run: return await RunProgramAsync(command, sender);
            default:
            {
                int code = Report(await SendAsync(sender, command.Message!), sender.Unreachable, command.Message);
                // A timer, stopwatch or event: its key, for "brim-notify stop KEY".
                if (code == 0 && NotifyCommandLine.PrintsKey(command.Message!)) Console.WriteLine(command.Message!.ReplaceId);
                return code;
            }
        }
    }

    // A notice cut to BrimDeck's limits and its icon given as a full path before it goes; one that still does not fit in a
    // frame is not sent, and gets TooLarge (W7).
    private static async Task<NoticeMessage?> SendAsync(NotifySender sender, NoticeMessage message)
    {
        NoticeProtocol.ClipTexts(message);
        message.Icon = NotifyCommandLine.ResolveIcon(message.Icon, OperatingSystem.IsWindows(), Path.GetFullPath, File.Exists);
        // Room for the id and times the sender still adds.
        if (NoticeProtocol.Serialize(message).Length > NoticeProtocol.MaxFrame - 512) return NoticeProtocol.Reply(NoticeResults.Rejected, TooLarge);
        return await sender.Send(message);
    }

    // sent: the message the reply is for, which names the key of a stop.
    public static int Report(NoticeMessage? reply, string unreachable, NoticeMessage? sent = null)
    {
        if (reply is null) { Console.Error.WriteLine("brim-notify: " + unreachable); return Unreachable; }
        if (reply is { Result: NoticeResults.Rejected, Detail: TooLarge })
        {
            Console.Error.WriteLine("brim-notify: the message is too large to send, even with its texts cut to BrimDeck's limits.");
            return Usage;
        }
        // The message went out, but no answer came in time (BrimDeck busy, starting or waking up): it may still show (W3).
        if (reply is { Result: NoticeResults.Rejected, Detail: "no-reply" })
        {
            Console.Error.WriteLine("brim-notify: BrimDeck did not answer in time; the notice may still be shown.");
            return Unreachable;
        }
        if (reply.Result == NoticeResults.Rejected) { Console.Error.WriteLine("brim-notify: BrimDeck refused the notice: " + (reply.Detail ?? "unknown reason")); return Refused; }
        // A stop that found nothing: a mistyped key, or a timer started with another -s (W8).
        if (reply is { Result: NoticeResults.Filtered, Detail: "no-timer" })
        {
            Console.Error.WriteLine("brim-notify: no timer, stopwatch or event has the key " + (sent?.ReplaceId ?? "given")
                + " (one started with -s is stopped with the same -s).");
            return Refused;
        }
        if (reply.Result == NoticeResults.Filtered) Console.Error.WriteLine("brim-notify: received; BrimDeck's rules keep this source quiet.");
        return 0;
    }

    private static async Task<int> RunProgramAsync(NotifyCommand command, NotifySender sender)
    {
        var watch = Stopwatch.StartNew();
        var (process, failure, report) = sender.Start(command.Program!);
        if (process is null) return report ? await ReportRunAsync(command, sender, failure, watch.Elapsed) : failure;
        using (process)
        {
            // Ctrl+C reaches the program too; brim-notify waits for it to finish and still reports the result.
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            await process.WaitForExitAsync();
            return await ReportRunAsync(command, sender, process.ExitCode, watch.Elapsed);
        }
    }

    private static async Task<int> ReportRunAsync(NotifyCommand command, NotifySender sender, int code, TimeSpan elapsed)
    {
        if (await SendAsync(sender, NotifyCommandLine.RunResult(command.Message!, code, elapsed)) is null)
            Console.Error.WriteLine("brim-notify: the program finished, but BrimDeck could not be reached.");
        return code;
    }
}

/// How the Windows brim-notify finds and starts "run"'s program (sec-L1). A bare name is looked for in PATH only, the way a
// shell would with the current folder left out: CreateProcess alone would first look in BrimDeck's folder and the current one.
// Programs other than .exe and .com (batch files, scripts opened by their file type) and cmd's own commands run through
// cmd, with a command line built so that cmd hands every argument over as it was given (C-M1).
public static class WindowsPrograms
{
    // What a program that is not found exits with, as cmd says it: brim-notify used to leave that to cmd, and scripts check it.
    public const int NotFound = 9009;

    // cmd's own commands, which are no files to find.
    private static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        "assoc", "cd", "chdir", "cls", "copy", "date", "del", "dir", "echo", "erase", "ftype", "md", "mkdir", "mklink",
        "move", "path", "rd", "ren", "rename", "rmdir", "set", "time", "title", "type", "ver", "vol"
    };

    // cmd's own commands that read their arguments as a command line once more (call, and start's new cmd), expanding %NAME%
    // again after CmdArguments kept it from the first reading (N2). They are refused; "cmd /c" says so openly when meant.
    public static bool Reparses(string name) => name.Equals("call", StringComparison.OrdinalIgnoreCase) || name.Equals("start", StringComparison.OrdinalIgnoreCase);

    // The full path of the program, or null. pathExt is %PATHEXT%; exists says whether a file is there. skip is BrimDeck's
    // own folder, never searched even when it is in PATH: it holds no program of the user's, and others may be able to write
    // to it (S5).
    public static string? Find(string name, string? path, string? pathExt, Func<string, bool> exists, string? skip = null)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || name.Contains('*') || name.Contains('?')) return null;
        var extensions = (string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Given with a folder (.\build.bat, C:\tools\x): that file, as written.
        if (name.Contains('\\') || name.Contains('/') || name.Contains(':'))
            return Candidates(Path.GetFullPath(name), extensions).FirstOrDefault(exists);
        string? skipped = skip is null ? null : Folder(skip);
        foreach (var folder in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folderPath = folder.Trim('"');
            if (!Path.IsPathFullyQualified(folderPath)) continue;
            if (skipped is not null && string.Equals(Folder(folderPath), skipped, StringComparison.OrdinalIgnoreCase)) continue;
            if (Candidates(Path.Combine(folderPath, name), extensions).FirstOrDefault(exists) is { } found) return found;
        }
        return null;
    }

    private static string Folder(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // The name as given when it already has one of the extensions; otherwise with each in turn, and a name with an extension
    // of its own (script.py) first as it is, which cmd then opens by its file type.
    private static IEnumerable<string> Candidates(string file, string[] extensions)
    {
        if (extensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) return [file];
        var appended = extensions.Select(extension => file + extension);
        return Path.HasExtension(file) ? appended.Prepend(file) : appended;
    }

    public static bool IsBuiltin(string name) => Builtins.Contains(name);

    // Whether the program is started itself rather than through cmd.
    public static bool RunsDirectly(string file)
        => file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".com", StringComparison.OrdinalIgnoreCase);

    // The arguments for cmd.exe that run the program with its arguments, escaped the way Rust's standard library escapes
    // them for batch files (its fix of CVE-2024-24576): an argument is quoted only when it needs to be, so "release" reaches
    // %1 as release; a quote inside is doubled; % becomes %%cd:~,% (an empty substring of %cd% after it), which keeps cmd from
    // expanding %NAME% even inside quotes; /v:off leaves ! alone. Null only when a part holds a line break or NUL, which would
    // end cmd's command line early, or the program's path cannot be quoted. A builtin's name stays bare, since cmd does not
    // know "echo" with quotes.
    public static string? CmdArguments(string program, IEnumerable<string> arguments, bool builtin)
    {
        // /e:on: the %cd:~,% substring needs cmd's extensions, which the registry could have switched off.
        var line = new StringBuilder("/e:on /d /v:off /s /c \"");
        if (builtin) line.Append(program);
        else
        {
            // A file name cannot hold a quote, and a trailing \ would escape the closing one.
            if (program.Length == 0 || program.Contains('"') || program.EndsWith('\\') || Unsafe(program)) return null;
            line.Append('"');
            AppendEscaped(line, program);
            line.Append('"');
        }
        foreach (var argument in arguments)
        {
            if (Unsafe(argument)) return null;
            line.Append(' ');
            AppendArgument(line, argument);
        }
        // /s: cmd takes everything between the first and the last quote as the command, as it is.
        return line.Append('"').ToString();
    }

    private static bool Unsafe(string text) => text.IndexOfAny(['\r', '\n', '\0']) >= 0;

    private static void AppendArgument(StringBuilder line, string argument)
    {
        // An empty argument would vanish unquoted; one ending in \ is quoted for scripts that pass "%~1" on to a program,
        // whose parser would otherwise read that \ as escaping the closing quote.
        bool quote = argument.Length == 0 || argument.EndsWith('\\');
        // Every ASCII symbol is taken as one cmd may treat specially, except these known to be plain; control characters too.
        foreach (char c in argument)
            if (c < 128 && !(char.IsAsciiLetterOrDigit(c) || "#$*+-./:?@\\_".Contains(c)) || char.IsControl(c)) { quote = true; break; }
        if (!quote) { line.Append(argument); return; }
        line.Append('"');
        int backslashes = AppendEscaped(line, argument);
        // Backslashes before the closing quote are doubled, so that they stay backslashes.
        line.Append('\\', backslashes).Append('"');
    }

    // The text with each quote doubled (the backslashes before it doubled too) and each % made one cmd does not expand;
    // returns how many backslashes it ends with.
    private static int AppendEscaped(StringBuilder line, string text)
    {
        int backslashes = 0;
        foreach (char c in text)
        {
            if (c == '\\') { backslashes++; line.Append(c); continue; }
            if (c == '"') line.Append('\\', backslashes).Append('"');
            else if (c == '%') line.Append("%%cd:~,");
            backslashes = 0;
            line.Append(c);
        }
        return backslashes;
    }
}
