using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace BrimDeck.Protocol.DBus;

public sealed class DBusException(string name, string message) : Exception(message)
{
    public string Name { get; } = name;
}

// A connection to a message bus over a Unix socket, authenticated as the calling user (SASL EXTERNAL). Method calls and
// signals addressed to this connection, and everything a monitor connection receives, arrive through Received; replies to
// CallAsync are matched by serial and returned instead.
public sealed class DBusConnection : IAsyncDisposable
{
    public const string BusName = "org.freedesktop.DBus", BusPath = "/org/freedesktop/DBus", BusInterface = "org.freedesktop.DBus";
    // Notifications are small; a larger message (a huge image hint) is read past and dropped instead of held in memory.
    public const int MaxMessage = 16 * 1024 * 1024;
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<DBusMessage>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private int _serial;
    private Task _reader = Task.CompletedTask;

    public string UniqueName { get; private set; } = "";
    // Called on the reading task, one message at a time; a slow handler delays the next message.
    public Func<DBusMessage, Task>? Received { get; set; }
    // Reports an exception a Received handler threw; the connection goes on.
    public Action<Exception>? HandlerFailed { get; set; }
    // Completes when the connection ends, with the error that ended it, if any.
    public Task Completion => _reader;

    private DBusConnection(Socket socket) { _socket = socket; _stream = new NetworkStream(socket, ownsSocket: true); }

    // The session bus address of this login, as libdbus finds it.
    public static string? SessionAddress(uint uid)
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrWhiteSpace(address)) return address;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtime)) runtime = "/run/user/" + uid;
        var path = Path.Combine(runtime, "bus");
        return File.Exists(path) ? "unix:path=" + path : null;
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
            try
            {
                await socket.ConnectAsync(endpoint, cancellation).ConfigureAwait(false);
                var connection = new DBusConnection(socket);
                await connection.AuthenticateAsync(uid, cancellation).ConfigureAwait(false);
                connection._reader = Task.Run(connection.ReadLoopAsync, CancellationToken.None);
                var hello = await connection.CallAsync(DBusMessage.Call(BusName, BusPath, BusInterface, "Hello"), cancellation).ConfigureAwait(false);
                connection.UniqueName = (string)hello.Body[0];
                return connection;
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or DBusException)
            {
                socket.Dispose(); last = ex;
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
                if (total > MaxMessage) { await DiscardAsync(total - start.Length).ConfigureAwait(false); continue; }
                var data = new byte[total];
                start.CopyTo(data, 0);
                await _stream.ReadExactlyAsync(data.AsMemory(16), _closed.Token).ConfigureAwait(false);
                DBusMessage message;
                try { message = DBusMessage.Decode(data); }
                catch (InvalidDataException) { continue; }
                // A monitor connection also receives replies addressed to other connections; only those to this one answer a call.
                bool ours = message.Destination is null || message.Destination == UniqueName || UniqueName.Length == 0;
                if (ours && message.Type is DBusMessageType.MethodReturn or DBusMessageType.Error && message.ReplySerial is { } serial && _pending.TryRemove(serial, out var waiter))
                {
                    if (message.Type == DBusMessageType.Error)
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
            foreach (var waiter in _pending.Values) waiter.TrySetException(new IOException("The bus connection closed."));
            _pending.Clear();
        }
    }

    private async Task DiscardAsync(long count)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), _closed.Token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            count -= read;
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
        using var registration = cancellation.Register(() => { if (_pending.TryRemove(call.Serial, out var w)) w.TrySetCanceled(); });
        await SendAsync(call, cancellation).ConfigureAwait(false);
        return await waiter.Task.ConfigureAwait(false);
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
