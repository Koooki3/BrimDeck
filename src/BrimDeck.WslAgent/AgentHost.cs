using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using BrimDeck.Protocol;
using BrimDeck.Protocol.DBus;

namespace BrimDeck.WslAgent;

// "brimdeck-agent serve": BrimDeck's end of a distribution. Standard output carries frames to BrimDeck (notices, closes,
// status); standard input carries BrimDeck's frames (replies, chosen actions, dismissals). The agent ends when standard
// input closes, which is when BrimDeck stops it or exits; owning the notification service name ends with it.
internal sealed class AgentHost
{
    private const string NameOwnerRule = "type='signal',sender='org.freedesktop.DBus',interface='org.freedesktop.DBus',member='NameOwnerChanged',arg0='org.freedesktop.Notifications'";
    private readonly Stream _output = Console.OpenStandardOutput();
    private readonly SemaphoreSlim _outputLock = new(1, 1), _claimLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NoticeMessage>> _replies = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly NotificationServer _server = new(Program.Version, Native.MachineId);
    private readonly NotificationMonitor _monitor = new();
    private readonly uint _uid = Native.UserId();
    private DBusConnection? _bus, _monitorBus;
    private string _state = AgentStates.NoBus, _detail = "";

    public static async Task<int> RunAsync()
    {
        var host = new AgentHost();
        // Only frames go to standard output; anything else written there would corrupt the stream.
        Console.SetOut(TextWriter.Null);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => host._stop.Cancel();
        var input = host.ReadBrimDeckAsync();
        var socket = host.ServeSocketAsync();
        var bus = host.KeepBusAsync();
        await input;
        host._stop.Cancel();
        await Task.WhenAll(socket, bus).WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => { });
        return 0;
    }

    private async Task SendAsync(NoticeMessage message)
    {
        await _outputLock.WaitAsync();
        try { await NoticeFrames.WriteAsync(_output, message); }
        catch (IOException) { _stop.Cancel(); }
        finally { _outputLock.Release(); }
    }

    private Task ReportAsync(string state, string detail = "")
    {
        lock (_gate) { _state = state; _detail = detail; }
        return SendAsync(new NoticeMessage { Type = NoticeTypes.Status, State = state, Detail = detail, Distro = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") });
    }

    // Frames from BrimDeck until it closes the stream.
    private async Task ReadBrimDeckAsync()
    {
        var input = Console.OpenStandardInput();
        while (!_stop.IsCancellationRequested)
        {
            NoticeMessage? message;
            try { message = await NoticeFrames.ReadMessageAsync(input, _stop.Token); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { return; }
            if (message is null) return;
            switch (message.Type)
            {
                case NoticeTypes.Reply when message.EventId is { } id && _replies.TryRemove(id, out var waiter):
                    waiter.TrySetResult(message); break;
                case NoticeTypes.Action when uint.TryParse(message.NativeId, NumberStyles.None, CultureInfo.InvariantCulture, out var actionId) && message.ActionKey is { } key:
                    await EmitAsync(() => _server.ActionInvoked(actionId, key)); break;
                case NoticeTypes.Dismissed when uint.TryParse(message.NativeId, NumberStyles.None, CultureInfo.InvariantCulture, out var dismissedId):
                    await EmitAsync(() => _server.Dismissed(dismissedId)); break;
                case NoticeTypes.Status:
                    string state, detail;
                    lock (_gate) { state = _state; detail = _detail; }
                    await ReportAsync(state, detail); break;
            }
        }
    }

    private async Task EmitAsync(Func<IReadOnlyList<DBusMessage>> produce)
    {
        IReadOnlyList<DBusMessage> messages;
        lock (_gate) messages = _state == AgentStates.Server ? produce() : [];
        if (_bus is { } bus) foreach (var message in messages) await SendBusAsync(bus, message);
    }

    private static async Task SendBusAsync(DBusConnection bus, DBusMessage message)
    {
        try { await bus.SendAsync(message); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
    }

    // Connects to the session bus and keeps the connection; a lost bus is tried again every 30 seconds.
    private async Task KeepBusAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var address = DBusConnection.SessionAddress(_uid);
            if (address is null) await ReportAsync(AgentStates.NoBus, "DBUS_SESSION_BUS_ADDRESS is not set and $XDG_RUNTIME_DIR/bus does not exist");
            else
            {
                try
                {
                    _bus = await DBusConnection.ConnectAsync(address, _uid, _stop.Token);
                    _bus.Received = HandleBusAsync;
                    _bus.HandlerFailed = ex => Console.Error.WriteLine("handler: " + ex.GetType().Name + ": " + ex.Message);
                    await _bus.CallBusAsync("AddMatch", "s", NameOwnerRule);
                    await _claimLock.WaitAsync(_stop.Token);
                    try { await ClaimOrWatchAsync(); } finally { _claimLock.Release(); }
                    await _bus.Completion.WaitAsync(_stop.Token);
                    // The bus went away (a restarted session bus): nothing is received until it is reached again.
                    await ReportAsync(AgentStates.NoBus, "the session bus closed the connection");
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                // Whatever went wrong (an address that cannot be parsed included), the agent says so and tries again later.
                catch (Exception ex)
                {
                    await ReportAsync(AgentStates.NoBus, ex.GetType().Name + ": " + ex.Message);
                }
                finally { await DisposeBusesAsync(); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token); } catch (OperationCanceledException) { break; }
        }
        await DisposeBusesAsync();
    }

    private async Task DisposeBusesAsync()
    {
        if (_monitorBus is { } monitor) { _monitorBus = null; await monitor.DisposeAsync(); }
        if (_bus is { } bus) { _bus = null; await bus.DisposeAsync(); }
    }

    private async Task ReclaimAsync(bool vacant)
    {
        await _claimLock.WaitAsync();
        try { if (_bus is not null) { if (vacant) await ClaimOrWatchAsync(); else await WatchAsync(); } }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine("reclaim: " + ex.GetType().Name + ": " + ex.Message); }
        catch (OperationCanceledException) { }
        finally { _claimLock.Release(); }
    }

    // Becomes the notification service if none runs; otherwise watches the one that does.
    private async Task ClaimOrWatchAsync()
    {
        // The bus may have closed meanwhile (a reclaim runs beside the reconnecting loop); the loop connects again.
        var bus = _bus ?? throw new IOException("The session bus closed.");
        const uint DoNotQueue = 4, PrimaryOwner = 1;
        var reply = await bus.CallBusAsync("RequestName", "su", FreedesktopNotifications.Name, DoNotQueue);
        if (reply.Body is [uint result, ..] && result == PrimaryOwner) { await ReportAsync(AgentStates.Server); return; }
        await WatchAsync();
    }

    private async Task WatchAsync()
    {
        var bus = _bus ?? throw new IOException("The session bus closed.");
        if (_monitorBus is { } old) { _monitorBus = null; await old.DisposeAsync(); }
        string owner;
        try { owner = (string)(await bus.CallBusAsync("GetNameOwner", "s", FreedesktopNotifications.Name)).Body[0]; }
        catch (DBusException) { await ClaimOrWatchAsync(); return; }
        if (owner == bus.UniqueName) { await ReportAsync(AgentStates.Server); return; }
        try
        {
            var address = DBusConnection.SessionAddress(_uid) ?? throw new IOException("The session bus address is gone.");
            var monitor = await DBusConnection.ConnectAsync(address, _uid, _stop.Token);
            lock (_gate) _monitor.Owner = owner;
            monitor.Received = message =>
            {
                List<NoticeMessage> notices;
                lock (_gate) notices = [.. _monitor.Observe(message, DateTimeOffset.UtcNow)];
                return ForwardAsync(notices);
            };
            await monitor.CallAsync(DBusMessage.Call(DBusConnection.BusName, DBusConnection.BusPath, "org.freedesktop.DBus.Monitoring", "BecomeMonitor", "asu",
                NotificationMonitor.Rules.Append(NotificationMonitor.ReplyRule(owner)).Cast<object>().ToList(), 0u));
            _monitorBus = monitor;
            _ = FlushMonitorAsync(monitor);
            await ReportAsync(AgentStates.Monitor, owner);
        }
        catch (DBusException ex)
        {
            await ReportAsync(AgentStates.MonitorDenied, ex.Name);
        }
    }

    private async Task FlushMonitorAsync(DBusConnection monitor)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (_monitorBus == monitor && await timer.WaitForNextTickAsync(_stop.Token))
            {
                List<NoticeMessage> late;
                lock (_gate) late = _monitor.Flush(DateTimeOffset.UtcNow);
                await ForwardAsync(late);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleBusAsync(DBusMessage message)
    {
        if (message.Type == DBusMessageType.Signal && message.Member == "NameOwnerChanged" && message.Body is [string name, string _, string newOwner]
            && name == FreedesktopNotifications.Name)
        {
            // The bus also announces this agent itself taking the name, possibly before RequestName's reply arrives.
            if (newOwner == _bus?.UniqueName) return;
            string state;
            lock (_gate) state = _state;
            // The other service went away: take its place. Another service started while only watching: watch it instead.
            // This runs on the bus's reading task, which must stay free to deliver the replies those calls wait for.
            if (state != AgentStates.Server) _ = Task.Run(() => ReclaimAsync(newOwner.Length == 0));
            return;
        }
        if (message.Type != DBusMessageType.MethodCall) return;
        ServiceOutput output;
        lock (_gate) output = _state == AgentStates.Server ? _server.Handle(message) : ServiceOutput.None;
        // A reply has nowhere to go once the bus closed; the application asks again on the new connection.
        if (_bus is { } bus) foreach (var reply in output.Bus) await SendBusAsync(bus, reply);
        await ForwardAsync(output.ToBrimDeck);
    }

    private async Task ForwardAsync(IEnumerable<NoticeMessage> notices)
    {
        foreach (var notice in notices) { notice.Distro = null; await SendAsync(notice); }
    }

    // brim-notify inside the distribution connects here; each frame goes to BrimDeck and its reply comes back.
    private async Task ServeSocketAsync()
    {
        if (AgentPaths.Socket() is not { } path) return;
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(16);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException) { return; }
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(_stop.Token);
                // Only the user's own processes may send; the socket's directory already keeps others out.
                if (Native.PeerUser(client) != _uid) { client.Dispose(); continue; }
                _ = ServeClientAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    private async Task ServeClientAsync(Socket client)
    {
        using (client)
        await using (var stream = new NetworkStream(client))
        {
            try
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                while (await NoticeFrames.ReadMessageAsync(stream, idle.Token) is { } message)
                {
                    NoticeMessage reply;
                    if (message.Type == NoticeTypes.Status)
                    {
                        string state, detail;
                        lock (_gate) { state = _state; detail = _detail; }
                        reply = NoticeProtocol.Reply(NoticeResults.Accepted, "agent " + state + (detail.Length > 0 ? " (" + detail + ")" : ""));
                    }
                    else if (message.Type != NoticeTypes.Notify) reply = NoticeProtocol.Reply(NoticeResults.Rejected, "unknown-type");
                    else reply = await RelayAsync(message);
                    await NoticeFrames.WriteAsync(stream, reply, idle.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or SocketException) { }
        }
    }

    private async Task<NoticeMessage> RelayAsync(NoticeMessage message)
    {
        message.Origin = "cli";
        message.Distro = null;
        message.EventId ??= Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<NoticeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A retry with the same id while the first is still on its way is the same notice; it must not take the first's reply.
        if (!_replies.TryAdd(message.EventId, waiter)) return NoticeProtocol.Reply(NoticeResults.Duplicate, null, message.EventId);
        await SendAsync(message);
        try { return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return NoticeProtocol.Reply(NoticeResults.Rejected, "no-reply"); }
        finally { _replies.TryRemove(message.EventId, out _); }
    }
}
