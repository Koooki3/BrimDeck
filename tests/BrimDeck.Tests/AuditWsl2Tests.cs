using System.Diagnostics;
using System.Text;
using BrimDeck.Protocol;
using BrimDeck.Protocol.DBus;

// The second round of audit fixes for brim-notify, the WSL agent and the D-Bus reader (group "wsl2").
internal static class AuditWsl2Tests
{
    public static async Task Run(Action<string, bool> check)
    {
        CmdEscaping(check);
        CmdThroughCmd(check);
        ProgramSearch(check);
        await NotFoundIsReported(check);
    }

    private const string Prefix = "/e:on /d /v:off /s /c \"";

    // C-M1: an argument is quoted only when cmd needs it to be, as Rust's standard library does for batch files.
    private static void CmdEscaping(Action<string, bool> check)
    {
        string? Line(params string[] arguments) => WindowsPrograms.CmdArguments(@"C:\s\build.bat", arguments, false);
        string Expected(string arguments) => Prefix + "\"C:\\s\\build.bat\"" + arguments + "\"";
        check("A plain argument reaches the batch file unquoted", Line("release") == Expected(" release"));
        check("Plain paths, options and numbers stay unquoted", Line(@"C:\x\y.txt", "--flag=1", "-v", "#$*+./:?@_") == Expected(@" C:\x\y.txt ""--flag=1"" -v #$*+./:?@_"));
        check("An argument with a space is quoted", Line("a b") == Expected(" \"a b\""));
        check("cmd's special characters are quoted", Line("&|^<>()", "a,b;c") == Expected(" \"&|^<>()\" \"a,b;c\""));
        check("A quote inside is doubled", Line("say \"hi\"") == Expected(" \"say \"\"hi\"\"\""));
        check("Backslashes before a quote are doubled with it", Line("a\\\"b") == Expected(" \"a\\\\\"\"b\""));
        check("% cannot start a variable", Line("%PATH%") == Expected(" \"%%cd:~,%PATH%%cd:~,%\""));
        check("! is quoted and left to /v:off", Line("!x!") == Expected(" \"!x!\""));
        check("An empty argument is kept", Line("") == Expected(" \"\""));
        check("A trailing backslash is quoted and doubled", Line(@"C:\dir\") == Expected(@" ""C:\dir\\"""));
        check("Letters beyond ASCII stay as they are", Line("中文", "café") == Expected(" 中文 café"));
        check("A line break or NUL is still refused", Line("a\nb") is null && Line("a\rb") is null && Line("a\0b") is null);
        check("A program path with a quote or a trailing backslash is refused",
            WindowsPrograms.CmdArguments("C:\\a\"b.bat", [], false) is null && WindowsPrograms.CmdArguments(@"C:\a\", [], false) is null);
        check("A builtin's name stays bare, and so does a plain argument", WindowsPrograms.CmdArguments("echo", ["hello"], true) == Prefix + "echo hello\"");
    }

    // C-M1, end to end: what cmd actually hands a batch file and its own echo (Windows only).
    private static void CmdThroughCmd(Action<string, bool> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck-wsl2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // Prints its arguments without running anything in them: %1 inside brackets, and %~1 through a variable read
            // with delayed expansion, which does not expand what it reads.
            var batch = Path.Combine(folder, "args.bat");
            File.WriteAllText(batch, string.Join("\r\n",
                "@echo off", "chcp 65001 >nul", "echo 1=[%1]", "echo 2=[%2]", "set \"v=%~1\"", "setlocal enabledelayedexpansion", "echo t=[!v!]", "endlocal", ""), new UTF8Encoding(false));
            // The usual way a build script reads its first argument.
            var release = Path.Combine(folder, "release.bat");
            File.WriteAllText(release, string.Join("\r\n", "@echo off", "if \"%1\"==\"release\" echo matched", "set NAME=%1", "echo name=%NAME%", "exit /b 7", ""));
            var lines = Run(release, false, "release");
            check("if \"%1\"==\"release\" matches again, and set takes no quotes", lines.SequenceEqual(["matched", "name=release"]));
            check("A batch file's own exit code comes back", Run(release, false, out int code, "x") is not null && code == 7);
            lines = Run(batch, false, "release");
            check("A plain argument arrives unquoted", lines.Contains("1=[release]") && lines.Contains("2=[]"));
            lines = Run(batch, false, "a b", "c");
            check("An argument with a space is one argument", lines.Contains("1=[\"a b\"]") && lines.Contains("t=[a b]") && lines.Contains("2=[c]"));
            lines = Run(batch, false, "Release %s");
            check("npm's %s is passed as it is", lines.Contains("t=[Release %s]"));
            lines = Run(batch, false, "%PATH%", "%~1%");
            check("A variable is not expanded", lines.Contains("t=[%PATH%]") && lines.Contains("2=[\"%~1%\"]"));
            lines = Run(batch, false, "!OS!");
            check("! is passed as it is", lines.Contains("t=[!OS!]"));
            lines = Run(batch, false, "&|^<>()");
            check("cmd's special characters arrive literally", lines.Contains("t=[&|^<>()]"));
            lines = Run(batch, false, "x\" & echo INJECTED & \"", "a & echo INJECTED", "(echo INJECTED)", "^& echo INJECTED");
            check("Nothing in an argument runs as a command", !lines.Any(line => line.StartsWith("INJECTED", StringComparison.Ordinal)) && lines.Contains("2=[\"a & echo INJECTED\"]"));
            lines = Run(batch, false, "中文 ✓");
            check("Letters beyond ASCII arrive intact", lines.Contains("t=[中文 ✓]"));
            lines = Run("echo", true, "hello");
            check("echo hello prints hello, not \"hello\"", lines.SequenceEqual(["hello"]));
            lines = Run("echo", true, "a & echo INJECTED");
            check("A builtin's arguments do not run either", lines.SequenceEqual(["\"a & echo INJECTED\""]));
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    private static List<string> Run(string program, bool builtin, params string[] arguments) => Run(program, builtin, out _, arguments) ?? [];

    private static List<string>? Run(string program, bool builtin, out int code, params string[] arguments)
    {
        code = -1;
        var line = WindowsPrograms.CmdArguments(program, arguments, builtin);
        if (line is null) return null;
        // Its own hidden console: chcp must not change the test runner's.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), line)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        code = process.ExitCode;
        return output.Split('\n').Select(text => text.TrimEnd('\r', ' ')).Where(text => text.Length > 0).ToList();
    }

    // S5 and C-M1: BrimDeck's folder is never searched; a script with its own extension is found by that name.
    private static void ProgramSearch(Action<string, bool> check)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\BrimDeck\git.exe", @"C:\tools\git.exe", @"C:\scripts\tool.py", @"C:\scripts\a.ps1" };
        const string pathExt = ".COM;.EXE;.BAT;.CMD";
        // Found with the extension as PATHEXT spells it; Windows paths compare without case.
        string? Find(string name, string path, string? skip = null) => WindowsPrograms.Find(name, path, pathExt, files.Contains, skip) is { } found ? files.First(file => file.Equals(found, StringComparison.OrdinalIgnoreCase)) : null;
        check("BrimDeck's folder in PATH is skipped", Find("git", @"C:\BrimDeck;C:\tools", @"C:\BrimDeck\") == @"C:\tools\git.exe"
            && Find("git", @"""C:\BrimDeck\"";C:\tools", @"C:\brimdeck") == @"C:\tools\git.exe");
        check("Without it, PATH's first match wins", Find("git", @"C:\BrimDeck;C:\tools") == @"C:\BrimDeck\git.exe");
        check("A script with its own extension is found in PATH", Find("tool.py", @"C:\scripts") == @"C:\scripts\tool.py" && Find("a.ps1", @"C:\scripts") == @"C:\scripts\a.ps1");
        check("A script is never run directly", !WindowsPrograms.RunsDirectly(@"C:\scripts\tool.py"));
        check("A name without an extension is not found as a bare file", WindowsPrograms.Find("tool", @"C:\scripts", pathExt, name => name == @"C:\scripts\tool") is null);
    }

    // C-M1: a program that is not found exits with 9009 and is still reported to BrimDeck as a finished run.
    private static async Task NotFoundIsReported(Action<string, bool> check)
    {
        var sent = new List<NoticeMessage>();
        var sender = new NotifySender("1",
            message => { sent.Add(message); return Task.FromResult<NoticeMessage?>(NoticeProtocol.Reply(NoticeResults.Accepted)); },
            _ => (null, WindowsPrograms.NotFound, true), "x", "x");
        int code = await NotifyCli.RunAsync(NotifyCommandLine.Parse(["run", "--", "nope", "a"]), sender);
        check("A program not found exits with 9009 and reports its run", code == 9009 && sent is [{ ExitCode: 9009 }]);
        sent.Clear();
        var quiet = sender with { Start = _ => (null, NotifyCli.Usage, false) };
        check("A refused start reports nothing", await NotifyCli.RunAsync(NotifyCommandLine.Parse(["run", "--", "x"]), quiet) == NotifyCli.Usage && sent.Count == 0);
    }
}
