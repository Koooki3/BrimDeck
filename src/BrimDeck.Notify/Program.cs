using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using BrimDeck.Protocol;

namespace BrimDeck.Notify;

// brim-notify for Windows. Exit codes: 0 delivered (or filtered by the user's rules), 1 usage error, 2 refused by BrimDeck,
// 3 BrimDeck is not running or its notices are off; "run" exits with the program's own code.
internal static class Program
{
    // How long BrimDeck may take to answer one message; a BrimDeck that hangs must not hang a script with it (life-L10).
    private static readonly TimeSpan ReplyWait = TimeSpan.FromSeconds(10);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var command = NotifyCommandLine.Parse(args);
        if (command.Kind == NotifyCommandKind.Stdio) return await RelayAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
        return await NotifyCli.RunAsync(command, new NotifySender(Version, SendAsync, Start,
            Unreachable: "BrimDeck is not running, or dynamic notices are switched off in its settings.",
            NotReceiving: "BrimDeck is not receiving notices (not running, or dynamic notices are off)."));
    }

    private static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    internal static string PipeName()
        => NoticeProtocol.PipeName(Process.GetCurrentProcess().SessionId, WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    private static async Task<NamedPipeClientStream?> ConnectAsync()
    {
        // CurrentUserOnly also checks that the pipe's owner is this user, so a pipe another account created under the same name is refused.
        var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(2000); return pipe; }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { await pipe.DisposeAsync(); return null; }
    }

    private static async Task<NoticeMessage?> SendAsync(NoticeMessage message)
    {
        message.EventId ??= Guid.NewGuid().ToString("N");
        NoticeProtocol.Stamp(message, DateTimeOffset.UtcNow);
        await using var pipe = await ConnectAsync();
        if (pipe is null) return null;
        return (await NoticeFrames.ExchangeAsync(pipe, message, ReplyWait)).Reply;
    }

    // "run"'s program, found and started as WindowsPrograms describes (sec-L1). A program that is not found exits with cmd's
    // 9009 and is still reported to BrimDeck as a finished run, as it was when cmd looked for it (C-M1).
    private static (Process? Process, int Failure, bool Report) Start(IReadOnlyList<string> program)
    {
        string name = program[0];
        string? path = Environment.GetEnvironmentVariable("PATH"), pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var file = WindowsPrograms.Find(name, path, pathExt, File.Exists, skip: AppContext.BaseDirectory);
        if (file is null && WindowsPrograms.Reparses(name))
        {
            Console.Error.WriteLine("brim-notify: " + name + " reads its arguments a second time, expanding %NAME% in them; refused. Run the program itself, or through cmd /c.");
            return (null, NotifyCli.Usage, false);
        }
        bool builtin = file is null && WindowsPrograms.IsBuiltin(name);
        if (file is null && !builtin)
        {
            // The current folder is not searched; say so when that is where the program is.
            bool here = WindowsPrograms.Find(Path.Combine(".", name), path, pathExt, File.Exists) is not null;
            Console.Error.WriteLine("brim-notify: " + name + ": command not found" + (here ? " (a program in the current folder is run as ." + Path.DirectorySeparatorChar + name + ")" : ""));
            return (null, WindowsPrograms.NotFound, true);
        }
        ProcessStartInfo start;
        if (file is not null && WindowsPrograms.RunsDirectly(file))
        {
            start = new ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var argument in program.Skip(1)) start.ArgumentList.Add(argument);
        }
        else
        {
            // Batch files, scripts and cmd's own commands run the way a prompt would run them. cmd by its full path: a bare
            // name is looked for in the current directory first.
            if (WindowsPrograms.CmdArguments(file ?? name, program.Skip(1), builtin) is not { } arguments)
            {
                Console.Error.WriteLine("brim-notify: " + name + " runs through cmd, which cannot be given a line break in an argument; refused.");
                return (null, NotifyCli.Usage, false);
            }
            start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments) { UseShellExecute = false };
        }
        try
        {
            var process = Process.Start(start);
            if (process is null) Console.Error.WriteLine("brim-notify: could not start " + name);
            return (process, NotifyCli.Usage, false);
        }
        catch (Win32Exception ex) { Console.Error.WriteLine("brim-notify: could not start " + name + ": " + ex.Message); return (null, NotifyCli.Usage, false); }
    }

    // --stdio: frames from standard input go to BrimDeck one by one; each reply goes to standard output. Used by brim-notify in
    // WSL when the agent is not running. A message BrimDeck does not answer gets a "rejected" reply, so the sender never hangs.
    internal static async Task<int> RelayAsync(Stream input, Stream output)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            while (true)
            {
                NoticeMessage? message;
                try { message = await NoticeFrames.ReadMessageAsync(input); }
                catch (InvalidDataException) { return NotifyCli.Refused; }
                if (message is null) return 0;
                NoticeMessage? reply = null;
                bool written = false;
                // A second connection only for a message that never reached BrimDeck: one it received may still show (W3).
                for (int attempt = 0; attempt < 2 && reply is null && !written; attempt++)
                {
                    pipe ??= await ConnectAsync();
                    if (pipe is null) break;
                    (reply, written) = await NoticeFrames.ExchangeAsync(pipe, message, ReplyWait);
                    if (reply is null) { await pipe.DisposeAsync(); pipe = null; }
                }
                await NoticeFrames.WriteAsync(output, reply ?? NoticeProtocol.Reply(NoticeResults.Rejected, written ? "no-reply" : "unreachable", message.EventId));
            }
        }
        finally { if (pipe is not null) await pipe.DisposeAsync(); }
    }
}
