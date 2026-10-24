using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using BrimDeck.Protocol;

// The agent only ever runs inside a WSL distribution.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace BrimDeck.WslAgent;

// brimdeck-agent serve [--idle-minutes N]   started by BrimDeck through wsl.exe: receives this distribution's notices, and
//                                           ends after N minutes (5 by default, 0 never) in which the user ran nothing there
// brimdeck-agent [notify] ARGS...           brim-notify inside WSL (the brim-notify script runs this)
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["serve", .. var options])
        {
            var idle = WslLink.IdleLimit;
            if (options is ["--idle-minutes", var minutes] && int.TryParse(minutes, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n <= 24 * 60)
                idle = TimeSpan.FromMinutes(n);
            return await AgentHost.RunAsync(idle);
        }
        var command = NotifyCommandLine.Parse(args is ["notify", .. var rest] ? rest : args);
        return await NotifyCli.RunAsync(command, new NotifySender(Version, SendAsync, Start,
            Unreachable: "BrimDeck could not be reached (is it running, with this distribution switched on under dynamic notices?).",
            NotReceiving: "BrimDeck is not receiving notices from this distribution."));
    }

    internal static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    // "run"'s program as the shell finds it; 127 when it is not found or cannot start, as in the shell.
    private static (Process? Process, int Failure, bool Report) Start(IReadOnlyList<string> program)
    {
        if (Native.FindProgram(program[0]) is not { } path) { Console.Error.WriteLine("brim-notify: " + program[0] + ": command not found"); return (null, 127, false); }
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        foreach (var argument in program.Skip(1)) start.ArgumentList.Add(argument);
        try { return (Process.Start(start), 127, false); }
        catch (Win32Exception ex) { Console.Error.WriteLine("brim-notify: could not start " + program[0] + ": " + ex.Message); return (null, 127, false); }
    }

    // Through the running agent's socket; without it, through BrimDeck.Notify.exe on Windows (WSL interop), whose path BrimDeck
    // wrote when it installed the agent.
    private static async Task<NoticeMessage?> SendAsync(NoticeMessage message)
    {
        message.EventId ??= Guid.NewGuid().ToString("N");
        NoticeProtocol.Stamp(message, DateTimeOffset.UtcNow);
        message.Distro ??= Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        // Through Windows only when the message never reached the agent: once it has, the agent passes it on even when BrimDeck
        // answers late, and sending it again would show it twice or start a second timer (W3).
        var (reply, written) = await ViaSocketAsync(message);
        if (written) return reply ?? NoticeProtocol.Reply(NoticeResults.Rejected, "no-reply", message.EventId);
        return await ViaHelperAsync(message);
    }

    private static async Task<(NoticeMessage? Reply, bool Written)> ViaSocketAsync(NoticeMessage message)
    {
        var path = AgentPaths.Socket();
        if (path is null || !File.Exists(path)) return (null, false);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using (var connecting = new CancellationTokenSource(TimeSpan.FromSeconds(5))) await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), connecting.Token);
            await using var stream = new NetworkStream(socket);
            // Longer than the agent waits for BrimDeck, so that the agent's "no-reply" comes first (W3).
            return await NoticeFrames.ExchangeAsync(stream, message, WslLink.CliReplyWait);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) { return (null, false); }
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
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception) { return null; }
        if (process is null) return null;
        using (process)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await NoticeFrames.WriteAsync(process.StandardInput.BaseStream, message, timeout.Token);
                process.StandardInput.Close();
                var reply = await NoticeFrames.ReadMessageAsync(process.StandardOutput.BaseStream, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return reply is { Result: NoticeResults.Rejected, Detail: "unreachable" } ? null : reply;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
            {
                // A helper that did not answer in time is ended, not left behind (life-L10).
                try { if (!process.HasExited) process.Kill(true); } catch (Exception kill) when (kill is InvalidOperationException or Win32Exception) { }
                return null;
            }
        }
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

    // statx(2) of the C library, or 0 without it (glibc before 2.28, musl before 1.2.5). "libc.so.6" is glibc's; musl
    // answers to any libc.* name with itself. Asked only when needed: brim-notify --version never loads it.
    private static nint _statx = -1;
    private static nint StatxFunction()
    {
        if (_statx == -1)
            _statx = System.Runtime.InteropServices.NativeLibrary.TryLoad("libc.so.6", out var library)
                && System.Runtime.InteropServices.NativeLibrary.TryGetExport(library, "statx", out var function) ? function : 0;
        return _statx;
    }

    // The owner of a file, not following a link (as stat(1) without -L); uint.MaxValue when it cannot be told. Through
    // statx, without a child process at each brim-notify (W16); stat(1) only where statx is missing or fails.
    public static uint OwnerOf(string path)
    {
        if (path.Contains('\0')) return Unknown;
        return OwnerByStatx(path) ?? OwnerByStatCommand(path);
    }

    private static unsafe uint? OwnerByStatx(string path)
    {
        const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100;
        const uint StatxUid = 0x8;
        nint function = StatxFunction();
        if (function == 0) return null;
        byte[] name = System.Text.Encoding.UTF8.GetBytes(path + "\0");
        // struct statx is 256 bytes with the same layout on every architecture: stx_mask at 0, stx_uid at 20.
        byte* result = stackalloc byte[256];
        int code;
        fixed (byte* file = name)
            code = ((delegate* unmanaged<int, byte*, int, uint, byte*, int>)function)(AtFdCwd, file, AtSymlinkNoFollow, StatxUid, result);
        if (code != 0 || (*(uint*)result & StatxUid) == 0) return null;
        return *(uint*)(result + 20);
    }

    // stat(1) by its full path: Process.Start looks for a bare name in the current directory before PATH. Its output is a
    // few bytes, so it is read after the wait, which then also bounds a stat that hangs (on a network file system, say).
    private static uint OwnerByStatCommand(string path)
    {
        if (new[] { "/usr/bin/stat", "/bin/stat" }.FirstOrDefault(File.Exists) is not { } stat) return Unknown;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(stat) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("%u"); start.ArgumentList.Add("--"); start.ArgumentList.Add(path);
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return Unknown;
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return Unknown;
            }
            var text = process.StandardOutput.ReadToEnd();
            return process.ExitCode == 0 && uint.TryParse(text.Trim(), out var uid) ? uid : Unknown;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return Unknown; }
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

    // Where the session bus is looked for (DBusConnection.SessionAddress): this process's environment and files.
    public static readonly BrimDeck.Protocol.DBus.BusLookup BusLookup = new(Environment.GetEnvironmentVariable, File.Exists, OwnerOf,
        folder =>
        {
            try { return Directory.Exists(folder) ? Directory.GetFiles(folder) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        },
        path =>
        {
            // An address file is a few lines; anything larger is not one.
            try { var info = new FileInfo(path); return info.Exists && info.Length <= 4096 ? File.ReadAllText(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        },
        MachineId());

    // A symbolic link's target as written, or null for anything else (a missing file included).
    public static string? LinkTarget(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // The processes in /proc, as far as they can be read; one that ends meanwhile is left out.
    public static IEnumerable<LinuxProcess> Processes()
    {
        foreach (var folder in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(folder), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)) continue;
            string status;
            try { status = File.ReadAllText(folder + "/status"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (WslIdle.Parse(pid, status) is { } process) yield return process;
        }
    }
}
