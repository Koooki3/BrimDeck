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
    // The service's ids start at 1 in every run of the agent; the session sent with them is new each run, so BrimDeck never
    // takes a new notice for an earlier run's with the same id (core-H2).
    private readonly NotificationServer _server = new(Program.Version, Native.MachineId, "server-" + Nonce());
    private readonly NotificationMonitor _monitor = new();
    // Released when the monitor holds a call for its reply; FlushMonitorAsync sleeps until then (perf-12).
    private readonly SemaphoreSlim _held = new(0, 1);
    // Released when a notice with an expire_timeout arrives; ExpireAsync sleeps until then or until the next one is due.
    private readonly SemaphoreSlim _expiring = new(0, 1);
    private readonly uint _uid = Native.UserId();
    private DBusConnection? _bus, _monitorBus;
    // The address the bus connection was made to; the monitor connection goes to the same bus.
    private string? _address;
    private string _state = AgentStates.NoBus, _detail = "";
    // Set from just before RequestName until its answer is handled: the bus may route an application's Notify to the agent
    // before the reply has been read, and that call is served instead of dropped (W10). Under _gate.
    private bool _claiming;

    // idleLimit: how long the user may run nothing in the distribution before the agent ends with WslLink.IdleExitCode (W2);
    // zero or less keeps it running until BrimDeck stops it.
    public static async Task<int> RunAsync(TimeSpan idleLimit)
    {
        var host = new AgentHost();
        // Only frames go to standard output; anything else written there would corrupt the stream.
        Console.SetOut(TextWriter.Null);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => host._stop.Cancel();
        var input = host.ReadBrimDeckAsync();
        var socket = host.ServeSocketAsync();
        var bus = host.KeepBusAsync();
        var flush = host.FlushMonitorAsync();
        var expire = host.ExpireAsync();
        var idle = host.WatchIdleAsync(idleLimit);
        bool idled = await Task.WhenAny(input, idle) == idle && await idle;
        host._stop.Cancel();
        await Task.WhenAll(socket, bus, flush, expire, input).WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => { });
        return idled ? WslLink.IdleExitCode : 0;
    }

    // True once the user ran nothing but plumbing in the distribution at every check for limit (WslIdle); false when the
    // agent stops first. A check that fails counts as busy: the agent never ends on what it could not read.
    private async Task<bool> WatchIdleAsync(TimeSpan limit)
    {
        var clock = new IdleClock(limit);
        int self = Environment.ProcessId;
        while (true)
        {
            try { await Task.Delay(limit > TimeSpan.Zero ? WslLink.IdleCheck : Timeout.InfiniteTimeSpan, _stop.Token); }
            catch (OperationCanceledException) { return false; }
            bool busy;
            try { busy = _uid == Native.Unknown || WslIdle.Busy(Native.Processes(), self, _uid); }
            catch (Exception ex) { Console.Error.WriteLine("idle: " + ex.GetType().Name + ": " + ex.Message); busy = true; }
            if (clock.Due(busy, DateTimeOffset.UtcNow)) return true;
        }
    }

    private static string Nonce() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private async Task SendAsync(NoticeMessage message)
    {
        // A notice's age by this distribution's clock, the same one its creation time was read from; BrimDeck cannot compare
        // that time with Windows' clock.
        if (message.Type == NoticeTypes.Notify)
        {
            NoticeProtocol.Stamp(message, DateTimeOffset.UtcNow);
            // A theme icon installed as a link within the icon folders, as the file it leads to: BrimDeck reads no links (I1).
            message.Icon = LinuxIcons.Resolve(message.Icon, Native.LinkTarget, File.Exists);
        }
        await _outputLock.WaitAsync();
        try { await NoticeFrames.WriteAsync(_output, message); }
        catch (IOException) { _stop.Cancel(); }
        // Too large for a frame even after ToNotice cut its texts: that one message is lost, said on standard error (sec-L3).
        catch (InvalidDataException ex) { Console.Error.WriteLine("send: " + ex.Message); }
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
                case NoticeTypes.Action when ForThisRun(message) && uint.TryParse(message.NativeId, NumberStyles.None, CultureInfo.InvariantCulture, out var actionId) && message.ActionKey is { } key:
                    await EmitAsync(() => _server.ActionInvoked(actionId, key)); break;
                case NoticeTypes.Dismissed when ForThisRun(message) && uint.TryParse(message.NativeId, NumberStyles.None, CultureInfo.InvariantCulture, out var dismissedId):
                    await EmitAsync(() => _server.Dismissed(dismissedId)); break;
                case NoticeTypes.Status:
                    string state, detail;
                    lock (_gate) { state = _state; detail = _detail; }
                    await ReportAsync(state, detail); break;
            }
        }
    }

    private bool ForThisRun(NoticeMessage message) => _server.Addresses(message);

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

    // Connects to the session bus and keeps the connection. While there is none, the agent looks for one every BusCheck,
    // which only reads a few file names (W6); a bus that is there but cannot be reached is tried again after 30 seconds.
    private async Task KeepBusAsync()
    {
        const string Missing = "no session bus: DBUS_SESSION_BUS_ADDRESS is not set, and neither $XDG_RUNTIME_DIR/bus, /run/user/<uid>/bus nor ~/.dbus/session-bus has one";
        while (!_stop.IsCancellationRequested)
        {
            var wait = WslLink.BusCheck;
            var address = DBusConnection.SessionAddress(_uid, Native.BusLookup);
            if (address is null)
            {
                // Said once, not at every look.
                bool said;
                lock (_gate) said = _state == AgentStates.NoBus && _detail == Missing;
                if (!said) await ReportAsync(AgentStates.NoBus, Missing);
            }
            else
            {
                try
                {
                    _address = address;
                    _bus = await DBusConnection.ConnectAsync(address, _uid, _stop.Token);
                    _bus.Received = HandleBusAsync;
                    _bus.HandlerFailed = ex => Console.Error.WriteLine("handler: " + ex.GetType().Name + ": " + ex.Message);
                    await _bus.CallBusAsync("AddMatch", "s", NameOwnerRule);
                    await _claimLock.WaitAsync(_stop.Token);
                    try { await ClaimOrWatchAsync(); } finally { _claimLock.Release(); }
                    long connected = Environment.TickCount64;
                    await _bus.Completion.WaitAsync(_stop.Token);
                    // The bus went away (a restarted session bus): nothing is received until it is reached again, which is
                    // looked for again within BusCheck.
                    await ReportAsync(AgentStates.NoBus, "the session bus closed the connection");
                    // A bus that drops the agent again at once is not asked every few seconds.
                    if (Environment.TickCount64 - connected < 30_000) wait = TimeSpan.FromSeconds(30);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                // Whatever went wrong (an address that cannot be parsed included), the agent says so and tries again later.
                catch (Exception ex)
                {
                    await ReportAsync(AgentStates.NoBus, ex.GetType().Name + ": " + ex.Message);
                    wait = TimeSpan.FromSeconds(30);
                }
                finally { await DisposeBusesAsync(); }
            }
            try { await Task.Delay(wait, _stop.Token); } catch (OperationCanceledException) { break; }
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
        bool owner;
        lock (_gate) _claiming = true;
        try
        {
            var reply = await bus.CallBusAsync("RequestName", "su", FreedesktopNotifications.Name, DoNotQueue);
            owner = reply.Body is [uint result, ..] && result == PrimaryOwner;
            // Server before the claim ends, so that no call falls between the two.
            if (owner) await ReportAsync(AgentStates.Server);
        }
        finally { lock (_gate) _claiming = false; }
        if (!owner) await WatchAsync();
    }

    private async Task WatchAsync()
    {
        var bus = _bus ?? throw new IOException("The session bus closed.");
        if (_monitorBus is { } old) { _monitorBus = null; await old.DisposeAsync(); }
        string owner;
        try { owner = (string)(await bus.CallBusAsync("GetNameOwner", "s", FreedesktopNotifications.Name)).Body[0]; }
        catch (DBusException) { await ClaimOrWatchAsync(); return; }
        if (owner == bus.UniqueName) { await ReportAsync(AgentStates.Server); return; }
        DBusConnection? monitor = null;
        try
        {
            var address = _address ?? throw new IOException("The session bus address is gone.");
            monitor = await DBusConnection.ConnectAsync(address, _uid, _stop.Token);
            // A new session for each service watched: a restarted service hands out its ids again (core-H2).
            lock (_gate) { _monitor.Owner = owner; _monitor.Session = "monitor-" + Nonce(); }
            monitor.Received = message =>
            {
                List<NoticeMessage> notices;
                bool held;
                lock (_gate) { notices = [.. _monitor.Observe(message, DateTimeOffset.UtcNow)]; held = _monitor.NextDue is not null; }
                if (held) { try { _held.Release(); } catch (SemaphoreFullException) { } }
                return ForwardAsync(notices);
            };
            await monitor.CallAsync(DBusMessage.Call(DBusConnection.BusName, DBusConnection.BusPath, "org.freedesktop.DBus.Monitoring", "BecomeMonitor", "asu",
                NotificationMonitor.Rules.Append(NotificationMonitor.ReplyRule(owner)).Cast<object>().ToList(), 0u));
            _monitorBus = monitor;
            // The monitor connection can end on its own while the main one stays (the bus drops a monitor that reads too
            // slowly): watching starts again instead of reporting Monitor while nothing is seen (W13). One the agent closed
            // itself (a reclaim, a lost bus, the agent stopping) is no longer _monitorBus by then.
            var watched = monitor;
            _ = watched.Completion.ContinueWith(_ => { if (!_stop.IsCancellationRequested && _monitorBus == watched) _ = RewatchAsync(watched, bus); },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            monitor = null;
            await ReportAsync(AgentStates.Monitor, owner);
        }
        catch (DBusException ex)
        {
            await ReportAsync(AgentStates.MonitorDenied, ex.Name);
        }
        // A connection that did not become the monitor is released, whatever stopped it (life-L7).
        finally { if (monitor is not null) await monitor.DisposeAsync(); }
    }

    // Watches again after the monitor connection ended on its own, after a pause that grows while it keeps failing. It gives
    // up once anything else changed: another monitor took its place, the agent became the service or was refused watching,
    // or the main connection went (KeepBusAsync starts over then).
    private async Task RewatchAsync(DBusConnection ended, DBusConnection bus)
    {
        var pause = TimeSpan.FromSeconds(2);
        while (true)
        {
            try { await Task.Delay(pause, _stop.Token); await _claimLock.WaitAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            try
            {
                string state;
                lock (_gate) state = _state;
                if (_bus != bus || state != AgentStates.Monitor || _monitorBus is { } current && current != ended) return;
                await WatchAsync();
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Console.Error.WriteLine("rewatch: " + ex.GetType().Name + ": " + ex.Message); }
            finally { _claimLock.Release(); }
            pause = TimeSpan.FromSeconds(Math.Min(60, pause.TotalSeconds * 2));
        }
    }

    // Passes on the watched calls whose reply did not come in time. It sleeps while no call is held, instead of waking every
    // second (perf-12), and goes on after any failure, which it reports on standard error (sec-L3).
    private async Task FlushMonitorAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset? due;
                lock (_gate) due = _monitor.NextDue;
                if (due is null) { await _held.WaitAsync(_stop.Token); continue; }
                var wait = due.Value - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _stop.Token);
                List<NoticeMessage> late;
                lock (_gate) late = _monitor.Flush(DateTimeOffset.UtcNow);
                await ForwardAsync(late);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex) { Console.Error.WriteLine("flush: " + ex.GetType().Name + ": " + ex.Message); }
        }
    }

    // Closes the notices whose expire_timeout ran out, for the applications waiting for that (W12). It sleeps until the next
    // one is due, or a new one arrives, and goes on after any failure, as FlushMonitorAsync does.
    private async Task ExpireAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset? due;
                lock (_gate) due = _server.NextExpiry;
                if (due is null) { await _expiring.WaitAsync(_stop.Token); continue; }
                // At most about 24 days, the longest a timeout (milliseconds in an int) can be; a new notice wakes it sooner.
                var wait = TimeSpan.FromMilliseconds(Math.Clamp((due.Value - DateTimeOffset.UtcNow).TotalMilliseconds, 0, int.MaxValue - 1));
                if (wait > TimeSpan.Zero && await _expiring.WaitAsync(wait, _stop.Token)) continue;
                // Taken off the list even while the agent is not the service (its bus lost), so the loop never spins on them.
                ServiceOutput closed;
                bool server;
                lock (_gate) { closed = _server.Expire(DateTimeOffset.UtcNow); server = _state == AgentStates.Server; }
                if (server && _bus is { } bus) foreach (var signal in closed.Bus) await SendBusAsync(bus, signal);
                // BrimDeck hears of it either way: the notice's buttons no longer reach the application.
                await ForwardAsync(closed.ToBrimDeck);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex) { Console.Error.WriteLine("expire: " + ex.GetType().Name + ": " + ex.Message); }
        }
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
        bool expiring;
        lock (_gate)
        {
            // While claiming the name too (W10): the bus may route a Notify here before RequestName's reply was read.
            output = _state == AgentStates.Server || _claiming ? _server.Handle(message) : NotificationServer.NotServing(message);
            expiring = message.Member == "Notify" && _server.NextExpiry is not null;
        }
        if (expiring) { try { _expiring.Release(); } catch (SemaphoreFullException) { } }
        // A reply has nowhere to go once the bus closed; the application asks again on the new connection.
        if (_bus is { } bus) foreach (var reply in output.Bus) await SendBusAsync(bus, reply);
        await ForwardAsync(output.ToBrimDeck);
    }

    private async Task ForwardAsync(IEnumerable<NoticeMessage> notices)
    {
        foreach (var notice in notices) { notice.Distro = null; await SendAsync(notice); }
    }

    // brim-notify inside the distribution connects here; each frame goes to BrimDeck and its reply comes back; meanwhile
    // brim-notify goes through Windows (life-L9). A socket whose file went (the runtime directory cleaned when the user's last
    // session ended) is set up again within SocketCheck; without a runtime directory yet (logind not done when the agent
    // started) the agent looks again every SocketCheck (W4); a socket that fails otherwise is tried again after a growing pause.
    private async Task ServeSocketAsync()
    {
        var pause = TimeSpan.FromSeconds(10);
        while (!_stop.IsCancellationRequested)
        {
            var wait = WslLink.SocketCheck;
            if (AgentPaths.Socket() is { } path)
            {
                // The file went: set up again at once, which a listening of at least one SocketCheck came before.
                try { await ListenAsync(path); pause = TimeSpan.FromSeconds(10); continue; }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception ex) { Console.Error.WriteLine("socket: " + ex.GetType().Name + ": " + ex.Message); }
                wait = pause;
                pause = TimeSpan.FromSeconds(Math.Min(300, pause.TotalSeconds * 2));
            }
            try { await Task.Delay(wait, _stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    // Listens until the agent stops, or returns once the socket's file is gone.
    private async Task ListenAsync(string path)
    {
        // An agent still listening there (one stopping while BrimDeck already started this one) keeps its socket: deleting it
        // would let that agent delete this one's in its turn as it ends (C-L12). This one tries again after its pause; a
        // socket nobody listens on is left over and replaced.
        if (File.Exists(path))
        {
            if (await AnswersAsync(path)) throw new IOException("another agent still listens on " + path);
            try { File.Delete(path); } catch (IOException) { }
        }
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        bool gone = false;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(16);
            while (!_stop.IsCancellationRequested)
            {
                var client = await WslLink.AcceptWhileThereAsync(token => listener.AcceptAsync(token).AsTask(), () => File.Exists(path), WslLink.SocketCheck, _stop.Token);
                if (client is null) { gone = true; return; }
                // Only the user's own processes may send; the socket's directory already keeps others out.
                if (Native.PeerUser(client) != _uid) { client.Dispose(); continue; }
                _ = ServeClientAsync(client);
            }
        }
        // A file that went is not deleted: what is there now is another agent's.
        finally { if (!gone) try { File.Delete(path); } catch (IOException) { } }
    }

    private static async Task<bool> AnswersAsync(string path)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token); return true; }
        // Refused: nobody listens. A connection that neither succeeds nor fails has a listener behind it, too busy to accept.
        catch (SocketException) { return false; }
        catch (OperationCanceledException) { return true; }
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
        // The distribution is the one this agent runs in; BrimDeck takes it from the link, never from the sender (sec-L4).
        message.Distro = null;
        // As BrimDeck will read the id, so that its reply finds this waiter; a blank id would make concurrent sends collide.
        message.EventId = NoticeProtocol.Clip(message.EventId, NoticeProtocol.MaxField);
        if (string.IsNullOrWhiteSpace(message.EventId)) message.EventId = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<NoticeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A retry with the same id while the first is still on its way is the same notice; it must not take the first's reply.
        if (!_replies.TryAdd(message.EventId, waiter)) return NoticeProtocol.Reply(NoticeResults.Duplicate, null, message.EventId);
        await SendAsync(message);
        // Shorter than brim-notify's own wait, so that it hears "no-reply" instead of sending the notice again (W3).
        try { return await waiter.Task.WaitAsync(WslLink.AgentReplyWait, _stop.Token); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return NoticeProtocol.Reply(NoticeResults.Rejected, "no-reply"); }
        finally { _replies.TryRemove(message.EventId, out _); }
    }
}
