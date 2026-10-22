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
    private const int Usage = 1, Refused = 2, Unreachable = 3;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var command = NotifyCommandLine.Parse(args);
        switch (command.Kind)
        {
            case NotifyCommandKind.Help: Console.WriteLine(NotifyCommandLine.Usage); return 0;
            case NotifyCommandKind.Version: Console.WriteLine(Version); return 0;
            case NotifyCommandKind.Error: Console.Error.WriteLine("brim-notify: " + command.Error); Console.Error.WriteLine("Try brim-notify --help."); return Usage;
            case NotifyCommandKind.Status: return await StatusAsync();
            case NotifyCommandKind.Stdio: return await RelayAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
            case NotifyCommandKind.Run: return await RunAsync(command);
            default: return Report(await SendAsync(command.Message!));
        }
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
        message.CreatedAt ??= DateTimeOffset.UtcNow;
        await using var pipe = await ConnectAsync();
        if (pipe is null) return null;
        try
        {
            await NoticeFrames.WriteAsync(pipe, message);
            return await NoticeFrames.ReadMessageAsync(pipe);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
    }

    private static int Report(NoticeMessage? reply)
    {
        if (reply is null)
        {
            Console.Error.WriteLine("brim-notify: BrimDeck is not running, or dynamic notices are switched off in its settings.");
            return Unreachable;
        }
        if (reply.Result == NoticeResults.Rejected)
        {
            Console.Error.WriteLine("brim-notify: BrimDeck refused the notice: " + (reply.Detail ?? "unknown reason"));
            return Refused;
        }
        if (reply.Result == NoticeResults.Filtered) Console.Error.WriteLine("brim-notify: received; BrimDeck's rules keep this source quiet.");
        return 0;
    }

    private static async Task<int> StatusAsync()
    {
        var reply = await SendAsync(new NoticeMessage { Type = NoticeTypes.Status });
        if (reply is null) { Console.WriteLine("BrimDeck is not receiving notices (not running, or dynamic notices are off)."); return Unreachable; }
        Console.WriteLine("BrimDeck is receiving notices." + (reply.Detail is { Length: > 0 } detail ? " " + detail : ""));
        return 0;
    }

    private static async Task<int> RunAsync(NotifyCommand command)
    {
        var program = command.Program!;
        var start = new ProcessStartInfo(program[0]) { UseShellExecute = false };
        foreach (var argument in program.Skip(1)) start.ArgumentList.Add(argument);
        var watch = Stopwatch.StartNew();
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception)
        {
            // Batch files and shell built-ins are not executables; cmd runs them the way a prompt would.
            start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c");
            foreach (var argument in program) start.ArgumentList.Add(argument);
            try { process = Process.Start(start); }
            catch (Win32Exception ex) { Console.Error.WriteLine("brim-notify: could not start " + program[0] + ": " + ex.Message); return Usage; }
        }
        if (process is null) { Console.Error.WriteLine("brim-notify: could not start " + program[0]); return Usage; }
        // Ctrl+C reaches the program too; brim-notify waits for it to finish and still reports the result.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        await process.WaitForExitAsync();
        int code = process.ExitCode;
        var reply = await SendAsync(NotifyCommandLine.RunResult(command.Message!, code, watch.Elapsed));
        if (reply is null) Console.Error.WriteLine("brim-notify: the program finished, but BrimDeck could not be reached.");
        return code;
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
                catch (InvalidDataException) { return Refused; }
                if (message is null) return 0;
                NoticeMessage? reply = null;
                for (int attempt = 0; attempt < 2 && reply is null; attempt++)
                {
                    pipe ??= await ConnectAsync();
                    if (pipe is null) break;
                    try { await NoticeFrames.WriteAsync(pipe, message); reply = await NoticeFrames.ReadMessageAsync(pipe); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException) { reply = null; }
                    if (reply is null) { await pipe.DisposeAsync(); pipe = null; }
                }
                await NoticeFrames.WriteAsync(output, reply ?? NoticeProtocol.Reply(NoticeResults.Rejected, "unreachable"));
            }
        }
        finally { if (pipe is not null) await pipe.DisposeAsync(); }
    }
}
