using System.Diagnostics;

namespace BrimDeck.Protocol;

// What one brim-notify differs in: how a message reaches BrimDeck, how "run" starts a program, and what it says when BrimDeck
// cannot be reached. Start returns the started process, or null with the exit code to return (it printed why).
public sealed record NotifySender(
    string Version,
    Func<NoticeMessage, Task<NoticeMessage?>> Send,
    Func<IReadOnlyList<string>, (Process? Process, int Failure)> Start,
    string Unreachable,
    string NotReceiving);

// brim-notify's commands, shared by the Windows sender (BrimDeck.Notify.exe) and the WSL agent (q-P1.5). Exit codes: 0
// delivered (or filtered by the user's rules), 1 usage error, 2 refused by BrimDeck, 3 BrimDeck could not be reached; "run"
// exits with the program's own code.
public static class NotifyCli
{
    public const int Usage = 1, Refused = 2, Unreachable = 3;

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
                Console.WriteLine("BrimDeck is receiving notices." + (reply.Detail is { Length: > 0 } detail ? " " + detail : ""));
                return 0;
            }
            case NotifyCommandKind.Run: return await RunProgramAsync(command, sender);
            default:
            {
                int code = Report(await sender.Send(command.Message!), sender.Unreachable);
                // A timer, stopwatch or event: its key, for "brim-notify stop KEY".
                if (code == 0 && NotifyCommandLine.PrintsKey(command.Message!)) Console.WriteLine(command.Message!.ReplaceId);
                return code;
            }
        }
    }

    public static int Report(NoticeMessage? reply, string unreachable)
    {
        if (reply is null) { Console.Error.WriteLine("brim-notify: " + unreachable); return Unreachable; }
        if (reply.Result == NoticeResults.Rejected) { Console.Error.WriteLine("brim-notify: BrimDeck refused the notice: " + (reply.Detail ?? "unknown reason")); return Refused; }
        if (reply.Result == NoticeResults.Filtered) Console.Error.WriteLine("brim-notify: received; BrimDeck's rules keep this source quiet.");
        return 0;
    }

    private static async Task<int> RunProgramAsync(NotifyCommand command, NotifySender sender)
    {
        var watch = Stopwatch.StartNew();
        var (process, failure) = sender.Start(command.Program!);
        if (process is null) return failure;
        using (process)
        {
            // Ctrl+C reaches the program too; brim-notify waits for it to finish and still reports the result.
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            await process.WaitForExitAsync();
            int code = process.ExitCode;
            if (await sender.Send(NotifyCommandLine.RunResult(command.Message!, code, watch.Elapsed)) is null)
                Console.Error.WriteLine("brim-notify: the program finished, but BrimDeck could not be reached.");
            return code;
        }
    }
}

// How the Windows brim-notify finds and starts "run"'s program (sec-L1). A bare name is looked for in PATH only, the way a
// shell would with the current folder left out: CreateProcess alone would first look in BrimDeck's folder and the current one.
// Programs other than .exe and .com (batch files, scripts) and cmd's own commands run through cmd, which parses its command
// line by rules of its own, so an argument it could misread is refused instead of passed on.
public static class WindowsPrograms
{
    // cmd's own commands, which are no files to find.
    private static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        "assoc", "call", "cd", "chdir", "cls", "copy", "date", "del", "dir", "echo", "erase", "ftype", "md", "mkdir", "mklink",
        "move", "path", "rd", "ren", "rename", "rmdir", "set", "start", "time", "title", "type", "ver", "vol"
    };

    // The full path of the program, or null. pathExt is %PATHEXT%; exists says whether a file is there.
    public static string? Find(string name, string? path, string? pathExt, Func<string, bool> exists)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || name.Contains('*') || name.Contains('?')) return null;
        var extensions = (string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Given with a folder (.\build.bat, C:\tools\x): that file, as written.
        if (name.Contains('\\') || name.Contains('/') || name.Contains(':'))
            return Candidates(Path.GetFullPath(name), extensions).FirstOrDefault(exists);
        foreach (var folder in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folderPath = folder.Trim('"');
            if (!Path.IsPathFullyQualified(folderPath)) continue;
            if (Candidates(Path.Combine(folderPath, name), extensions).FirstOrDefault(exists) is { } found) return found;
        }
        return null;
    }

    // The name as given when it already has one of the extensions, otherwise with each in turn.
    private static IEnumerable<string> Candidates(string file, string[] extensions)
        => extensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ? [file] : extensions.Select(extension => file + extension);

    public static bool IsBuiltin(string name) => Builtins.Contains(name);

    // Whether the program is started itself rather than through cmd.
    public static bool RunsDirectly(string file)
        => file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".com", StringComparison.OrdinalIgnoreCase);

    // The arguments for cmd.exe that run the program with its arguments, or null when one of them cannot be passed through
    // cmd safely. Each part is quoted, which keeps & | < > ( ) ^ literal; cmd still expands % inside quotes (and ! with
    // delayed expansion, switched off here), and a quote inside a part would end the quoting, so those are refused. A
    // builtin's name stays unquoted, since cmd does not know "echo" with quotes.
    public static string? CmdArguments(string program, IEnumerable<string> arguments, bool builtin)
    {
        var parts = new List<string?> { builtin ? program : Quote(program) };
        foreach (var argument in arguments) parts.Add(Quote(argument));
        if (parts.Contains(null)) return null;
        // /s: cmd takes everything between the first and the last quote as the command, as it is.
        return "/d /v:off /s /c \"" + string.Join(' ', parts) + "\"";
    }

    private static string? Quote(string text) => text.IndexOfAny(['"', '%', '!', '\r', '\n', '\0']) >= 0 ? null : "\"" + text + "\"";
}
