using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using BrimDeck.Protocol;

// The agent only ever runs inside a WSL distribution.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace BrimDeck.WslAgent;

// brimdeck-agent serve              started by BrimDeck through wsl.exe: receives this distribution's notices
// brimdeck-agent [notify] ARGS...   brim-notify inside WSL (the brim-notify script runs this)
internal static class Program
{
    private const int Usage = 1, Refused = 2, Unreachable = 3;

    private static async Task<int> Main(string[] args)
    {
        if (args is ["serve", ..]) return await AgentHost.RunAsync();
        var command = NotifyCommandLine.Parse(args is ["notify", .. var rest] ? rest : args);
        switch (command.Kind)
        {
            case NotifyCommandKind.Help: Console.WriteLine(NotifyCommandLine.Usage); return 0;
            case NotifyCommandKind.Version: Console.WriteLine(Version); return 0;
            case NotifyCommandKind.Error: Console.Error.WriteLine("brim-notify: " + command.Error); Console.Error.WriteLine("Try brim-notify --help."); return Usage;
            case NotifyCommandKind.Stdio: Console.Error.WriteLine("brim-notify: --stdio is for the Windows sender."); return Usage;
            case NotifyCommandKind.Status:
            {
                var reply = await SendAsync(new NoticeMessage { Type = NoticeTypes.Status });
                if (reply is null) { Console.WriteLine("BrimDeck is not receiving notices from this distribution."); return Unreachable; }
                Console.WriteLine("BrimDeck is receiving notices." + (reply.Detail is { Length: > 0 } detail ? " " + detail : ""));
                return 0;
            }
            case NotifyCommandKind.Run: return await RunAsync(command);
            default:
            {
                int code = Report(await SendAsync(command.Message!));
                // A timer, stopwatch or event: its key, for "brim-notify stop KEY".
                if (code == 0 && NotifyCommandLine.PrintsKey(command.Message!)) Console.WriteLine(command.Message!.ReplaceId);
                return code;
            }
        }
    }

    internal static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    private static int Report(NoticeMessage? reply)
    {
        if (reply is null) { Console.Error.WriteLine("brim-notify: BrimDeck could not be reached (is it running, with this distribution switched on under dynamic notices?)."); return Unreachable; }
        if (reply.Result == NoticeResults.Rejected) { Console.Error.WriteLine("brim-notify: BrimDeck refused the notice: " + (reply.Detail ?? "unknown reason")); return Refused; }
        if (reply.Result == NoticeResults.Filtered) Console.Error.WriteLine("brim-notify: received; BrimDeck's rules keep this source quiet.");
        return 0;
    }

    private static async Task<int> RunAsync(NotifyCommand command)
    {
        var program = command.Program!;
        if (Native.FindProgram(program[0]) is not { } path) { Console.Error.WriteLine("brim-notify: " + program[0] + ": command not found"); return 127; }
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        foreach (var argument in program.Skip(1)) start.ArgumentList.Add(argument);
        var watch = Stopwatch.StartNew();
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception ex) { Console.Error.WriteLine("brim-notify: could not start " + program[0] + ": " + ex.Message); return 127; }
        if (process is null) return 127;
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        await process.WaitForExitAsync();
        int code = process.ExitCode;
        if (await SendAsync(NotifyCommandLine.RunResult(command.Message!, code, watch.Elapsed)) is null)
            Console.Error.WriteLine("brim-notify: the program finished, but BrimDeck could not be reached.");
        return code;
    }

    // Through the running agent's socket; without it, through BrimDeck.Notify.exe on Windows (WSL interop), whose path BrimDeck
    // wrote when it installed the agent.
    private static async Task<NoticeMessage?> SendAsync(NoticeMessage message)
    {
        message.EventId ??= Guid.NewGuid().ToString("N");
        message.CreatedAt ??= DateTimeOffset.UtcNow;
        message.Distro ??= Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        return await ViaSocketAsync(message) ?? await ViaHelperAsync(message);
    }

    private static async Task<NoticeMessage?> ViaSocketAsync(NoticeMessage message)
    {
        var path = AgentPaths.Socket();
        if (path is null || !File.Exists(path)) return null;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token);
            await using var stream = new NetworkStream(socket);
            await NoticeFrames.WriteAsync(stream, message, timeout.Token);
            return await NoticeFrames.ReadMessageAsync(stream, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or OperationCanceledException) { return null; }
    }

    private static async Task<NoticeMessage?> ViaHelperAsync(NoticeMessage message)
    {
        string? helper = null;
        if (AgentPaths.BridgeConfig is not { } config) return null;
        try
        {
            var json = await File.ReadAllBytesAsync(config);
            helper = JsonSerializer.Deserialize(json, NoticeJson.Default.AgentBridgeConfig)?.Helper;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        // BrimDeck writes the helper's full path (under /mnt); a bare name would be looked up in the current directory.
        if (string.IsNullOrEmpty(helper) || !Path.IsPathRooted(helper) || !File.Exists(helper)) return null;
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--stdio");
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await NoticeFrames.WriteAsync(process.StandardInput.BaseStream, message, timeout.Token);
            process.StandardInput.Close();
            var reply = await NoticeFrames.ReadMessageAsync(process.StandardOutput.BaseStream, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return reply is { Result: NoticeResults.Rejected, Detail: "unreachable" } ? null : reply;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidDataException or OperationCanceledException) { return null; }
    }
}

internal static class AgentPaths
{
    // Without HOME there is no data folder: a shared place like /tmp could hold another user's bridge.json.
    public static string? Home => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home && Path.IsPathRooted(home) ? home : null;
    public static string? BridgeConfig => Home is { } home ? Path.Combine(home, ".local", "share", "brimdeck", "bridge.json") : null;

    // The CLI socket lives in the user's runtime directory, which only the user can enter. Without such a directory there is
    // no socket: a shared place like /tmp could be taken over by another user first, and brim-notify then reaches Windows
    // through BrimDeck.Notify.exe instead.
    public static string? Socket()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime) || !Directory.Exists(runtime)) return null;
        try
        {
            if (File.GetUnixFileMode(runtime) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) return null;
            // Both sides fail as Native.Unknown; an unknown user id never matches anything.
            var uid = Native.UserId();
            if (uid == Native.Unknown || Native.OwnerOf(runtime) != uid) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return Path.Combine(runtime, "brimdeck-notify.sock");
    }
}

internal static class Native
{
    // The real user id, from /proc, without a native call.
    public static uint UserId()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("Uid:", StringComparison.Ordinal)
                    && uint.TryParse(line[4..].Trim().Split('\t', ' ')[0], out var uid)) return uid;
        }
        catch (IOException) { }
        // Unknown, never root: every check against it (socket owner, peer, bus) then fails safe.
        return Unknown;
    }
    public const uint Unknown = uint.MaxValue;
    // The owner of a file, from stat(1)'s output; uint.MaxValue when it cannot be told. stat is named by its full path:
    // Process.Start looks for a bare name in the current directory before PATH.
    public static uint OwnerOf(string path)
    {
        if (new[] { "/usr/bin/stat", "/bin/stat" }.FirstOrDefault(File.Exists) is not { } stat) return uint.MaxValue;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(stat) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("%u"); start.ArgumentList.Add("--"); start.ArgumentList.Add(path);
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return uint.MaxValue;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return process.ExitCode == 0 && uint.TryParse(text.Trim(), out var uid) ? uid : uint.MaxValue;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return uint.MaxValue; }
    }

    // A program as the shell finds it: a name with a slash from the current directory, otherwise the first executable file
    // of that name in PATH (an empty entry is the current directory, as in the shell). Process.Start alone would look in the
    // agent's folder and the current directory first.
    public static string? FindProgram(string name)
    {
        if (name.Length == 0) return null;
        if (name.Contains('/')) return Path.GetFullPath(name);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin").Split(':'))
        {
            var path = Path.Combine(folder.Length == 0 ? "." : folder, name);
            try
            {
                if (File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0) return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    // The user id of the process at the other end of a Unix socket (SO_PEERCRED), or null when it cannot be read.
    public static uint? PeerUser(System.Net.Sockets.Socket socket)
    {
        const int SolSocket = 1, SoPeerCred = 17;
        Span<byte> credentials = stackalloc byte[12];
        try
        {
            int length = socket.GetRawSocketOption(SolSocket, SoPeerCred, credentials);
            return length >= 8 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(credentials[4..]) : null;
        }
        catch (System.Net.Sockets.SocketException) { return null; }
    }

    public static string MachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            try { if (File.Exists(path)) return File.ReadAllText(path).Trim(); } catch (IOException) { }
        return "";
    }
}
