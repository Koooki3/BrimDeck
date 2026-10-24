using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace BrimDeck.Protocol.DBus;

public sealed class DBusException(string name, string message) : Exception(message)
{
    public string Name { get; } = name;
}

// What DBusConnection.SessionAddress reads: environment variables, whether a file is there, its owner's user id, the files
// in a folder (full paths) and a small file's text. The agent passes the real ones; the tests pass their own.
public sealed record BusLookup(Func<string, string?> Variable, Func<string, bool> Exists, Func<string, uint> OwnerOf,
    Func<string, IEnumerable<string>> Files, Func<string, string?> Read, string MachineId);

// A connection to a message bus over a Unix socket, authenticated as the calling user (SASL EXTERNAL). Method calls and
// signals addressed to this connection, and everything a monitor connection receives, arrive through Received; replies to
// CallAsync are matched by serial and returned instead.
public sealed class DBusConnection : IAsyncDisposable
{
    public const string BusName = "org.freedesktop.DBus", BusPath = "/org/freedesktop/DBus", BusInterface = "org.freedesktop.DBus";
    // Notifications are small; a larger message (a huge image hint, which the agent does not use) is not held in memory
    // whole (sec-M3): DBusOversized reads it without its large byte arrays (C-L6).
    public const int MaxMessage = 2 * 1024 * 1024;
    public const string LimitsExceeded = "org.freedesktop.DBus.Error.LimitsExceeded";
    // A call the bus does not answer in this time fails, so that no caller waits forever on a connection that stopped
    // answering (life-L8).
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<DBusMessage>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private int _serial;
    private Task _reader = Task.CompletedTask;
    // Set when the reading task stops answering calls; a call made after that fails at once instead of waiting.
    private volatile bool _ended;

    public string UniqueName { get; private set; } = "";
    // Called on the reading task, one message at a time; a slow handler delays the next message.
    public Func<DBusMessage, Task>? Received { get; set; }
    // Reports an exception a Received handler threw; the connection goes on.
    public Action<Exception>? HandlerFailed { get; set; }
    // Completes when the connection ends, with the error that ended it, if any.
    public Task Completion => _reader;

    private DBusConnection(Socket socket) { _socket = socket; _stream = new NetworkStream(socket, ownsSocket: true); }

    // The session bus address of this login, as libdbus and GIO find it (W6), in this order: DBUS_SESSION_BUS_ADDRESS;
    // $XDG_RUNTIME_DIR/bus and /run/user/<uid>/bus, each only when it is the user's own (WSLg may set XDG_RUNTIME_DIR to its
    // own folder, which has no bus, while systemd's is in /run/user); and last the address an X11 autolaunched bus wrote to
    // ~/.dbus/session-bus/<machine id>-<display> (a distribution without systemd). Null when none is there yet; finding none
    // touches nothing but a few names, so it can be asked every few seconds.
    public static string? SessionAddress(uint uid, BusLookup look)
    {
        var address = look.Variable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrWhiteSpace(address)) return address.Trim();
        // An unknown user owns nothing (its id never matches a file's).
        if (uid == uint.MaxValue) return null;
        foreach (var folder in new[] { look.Variable("XDG_RUNTIME_DIR"), "/run/user/" + uid.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            if (string.IsNullOrWhiteSpace(folder) || !folder.StartsWith('/')) continue;
            var path = folder.TrimEnd('/') + "/bus";
            if (look.Exists(path) && look.OwnerOf(path) == uid) return "unix:path=" + path;
        }
        if (look.Variable("HOME") is not { Length: > 1 } home || !home.StartsWith('/') || DisplayNumber(look.Variable("DISPLAY")) is not { } display) return null;
        var folderPath = home.TrimEnd('/') + "/.dbus/session-bus/";
        var files = look.Files(folderPath).Select(file => file[(file.LastIndexOf('/') + 1)..]).Where(name => name.EndsWith("-" + display, StringComparison.Ordinal)).ToList();
        // The file of this machine first, as libdbus names it; another machine id's (a copied home) only without one.
        foreach (var name in files.OrderBy(name => name == look.MachineId + "-" + display ? 0 : 1))
            if (AutolaunchAddress(look.Read(folderPath + name)) is { } found) return found;
        return null;
    }

    // The display's number in DISPLAY (":0", ":0.0", "host:10.0"); null without one.
    public static string? DisplayNumber(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return null;
        int colon = display.LastIndexOf(':');
        if (colon < 0) return null;
        var number = display[(colon + 1)..].Split('.')[0];
        return number.Length > 0 && number.All(char.IsAsciiDigit) ? number : null;
    }

    // The DBUS_SESSION_BUS_ADDRESS line of an autolaunch file, its quotes taken off.
    public static string? AutolaunchAddress(string? text)
    {
        foreach (var line in (text ?? "").Split('\n'))
        {
            const string Key = "DBUS_SESSION_BUS_ADDRESS=";
            if (!line.StartsWith(Key, StringComparison.Ordinal)) continue;
            var value = line[Key.Length..].Trim();
            if (value.Length >= 2 && value[0] is '\'' or '"' && value[^1] == value[0]) value = value[1..^1];
            return value.Length > 0 ? value : null;
        }
        return null;
    }

    // The socket endpoints in an address list such as "unix:path=/run/user/1000/bus;unix:abstract=/tmp/x,guid=...".
    public static IEnumerable<UnixDomainSocketEndPoint> Endpoints(string address)
    {
        foreach (var entry in address.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = entry.IndexOf(':');
            if (colon < 0 || entry[..colon] != "unix") continue;
            var keys = entry[(colon + 1)..].Split(',').Select(part => part.Split('=', 2)).Where(part => part.Length == 2)
                .ToDictionary(part => part[0], part => Unescape(part[1]));
            if (keys.TryGetValue("path", out var path)) yield return new UnixDomainSocketEndPoint(path);
            else if (keys.TryGetValue("abstract", out var name)) yield return new UnixDomainSocketEndPoint("\0" + name);
        }
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('%')) return value;
        var bytes = new List<byte>();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && byte.TryParse(value.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) { bytes.Add(b); i += 2; }
            else bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static async Task<DBusConnection> ConnectAsync(string address, uint uid, CancellationToken cancellation = default)
    {
        Exception? last = null;
        foreach (var endpoint in Endpoints(address))
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            DBusConnection? connection = null;
            // A bus that accepts the connection but never answers must not hold the caller (life-L8).
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(CallTimeout);
            try
            {
                await socket.ConnectAsync(endpoint, limit.Token).ConfigureAwait(false);
                connection = new DBusConnection(socket);
                await connection.AuthenticateAsync(uid, limit.Token).ConfigureAwait(false);
                connection._reader = Task.Run(connection.ReadLoopAsync, CancellationToken.None);
                var hello = await connection.CallAsync(DBusMessage.Call(BusName, BusPath, BusInterface, "Hello"), cancellation).ConfigureAwait(false);
                connection.UniqueName = (string)hello.Body[0];
                return connection;
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or DBusException or TimeoutException
                || ex is OperationCanceledException && !cancellation.IsCancellationRequested)
            {
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                else socket.Dispose();
                last = ex;
            }
            catch
            {
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                else socket.Dispose();
                throw;
            }
        }
        throw new IOException("No session bus could be reached at " + address + ".", last);
    }

    private async Task AuthenticateAsync(uint uid, CancellationToken cancellation)
    {
        var hex = Convert.ToHexString(Encoding.ASCII.GetBytes(uid.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToLowerInvariant();
        await _stream.WriteAsync(new byte[] { 0 }, cancellation).ConfigureAwait(false);
        await _stream.WriteAsync(Encoding.ASCII.GetBytes("AUTH EXTERNAL " + hex + "\r\n"), cancellation).ConfigureAwait(false);
        var reply = await ReadLineAsync(cancellation).ConfigureAwait(false);
        if (!reply.StartsWith("OK ", StringComparison.Ordinal)) throw new InvalidDataException("The bus refused authentication: " + reply);
        await _stream.WriteAsync(Encoding.ASCII.GetBytes("BEGIN\r\n"), cancellation).ConfigureAwait(false);
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellation)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (line.Length < 4096)
        {
            if (await _stream.ReadAsync(one, cancellation).ConfigureAwait(false) == 0) throw new IOException("The bus closed the connection.");
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            line.Append((char)one[0]);
        }
        throw new InvalidDataException("Authentication line too long.");
    }

    private async Task ReadLoopAsync()
    {
        var start = new byte[16];
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(start, _closed.Token).ConfigureAwait(false);
                int total = DBusMessage.TotalLength(start);
                DBusMessage message;
                if (total > MaxMessage)
                {
                    // Read without its large byte arrays (an image hint), or as its header alone, which still lets a call be
                    // answered at once instead of leaving its sender waiting for the bus's timeout (C-L6).
                    var (slim, header) = await DBusOversized.ReadAsync(_stream, start, total, MaxMessage, _closed.Token).ConfigureAwait(false);
                    if (header is null) continue;
                    header.Oversized = true;
                    message = header;
                    if (slim is not null) try { message = DBusMessage.Decode(slim); } catch (InvalidDataException) { }
                }
                else
                {
                    var data = new byte[total];
                    start.CopyTo(data, 0);
                    await _stream.ReadExactlyAsync(data.AsMemory(16), _closed.Token).ConfigureAwait(false);
                    try { message = DBusMessage.Decode(data); }
                    catch (InvalidDataException) { continue; }
                }
                // A monitor connection also receives replies addressed to other connections; only those to this one answer a call.
                bool ours = message.Destination is null || message.Destination == UniqueName || UniqueName.Length == 0;
                if (ours && message.Type is DBusMessageType.MethodReturn or DBusMessageType.Error && message.ReplySerial is { } serial && _pending.TryRemove(serial, out var waiter))
                {
                    if (message.Oversized) waiter.TrySetException(new DBusException(LimitsExceeded, "The reply is too large."));
                    else if (message.Type == DBusMessageType.Error)
                        waiter.TrySetException(new DBusException(message.ErrorName ?? "", message.Body.FirstOrDefault() as string ?? message.ErrorName ?? ""));
                    else waiter.TrySetResult(message);
                    continue;
                }
                // A failing handler loses that one message, never the connection.
                if (Received is { } handler)
                    try { await handler(message).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { HandlerFailed?.Invoke(ex); }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidDataException) { }
        finally
        {
            _ended = true;
            foreach (var waiter in _pending.Values) waiter.TrySetException(new IOException("The bus connection closed."));
            _pending.Clear();
        }
    }

    public async Task SendAsync(DBusMessage message, CancellationToken cancellation = default)
    {
        if (message.Serial == 0) message.Serial = (uint)Interlocked.Increment(ref _serial);
        var bytes = message.Encode();
        await _writeLock.WaitAsync(cancellation).ConfigureAwait(false);
        try { await _stream.WriteAsync(bytes, cancellation).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public async Task<DBusMessage> CallAsync(DBusMessage call, CancellationToken cancellation = default)
    {
        call.Serial = (uint)Interlocked.Increment(ref _serial);
        var waiter = new TaskCompletionSource<DBusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[call.Serial] = waiter;
        try
        {
            // Registered after the reading task failed the waiting calls: nothing would answer this one (life-L8).
            if (_ended) throw new IOException("The bus connection closed.");
            await SendAsync(call, cancellation).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(CallTimeout, cancellation).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(call.Serial, out _); }
    }

    public Task<DBusMessage> CallBusAsync(string member, string signature = "", params object[] body)
        => CallAsync(DBusMessage.Call(BusName, BusPath, BusInterface, member, signature, body));

    public async ValueTask DisposeAsync()
    {
        _closed.Cancel();
        try { _socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        await _stream.DisposeAsync().ConfigureAwait(false);
        try { await _reader.ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        _closed.Dispose();
        _writeLock.Dispose();
    }
}
