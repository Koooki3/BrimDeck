using System.Globalization;

namespace BrimDeck.Protocol.DBus;

// The WSL agent's two ways of receiving desktop notifications, as logic without I/O, so that they can be tested anywhere.
// Server answers the org.freedesktop.Notifications calls itself while the agent owns the name. Monitor watches the calls
// another notification service receives, and learns each notice's id from that service's reply.

// What handling one bus message produced: a reply or signals to send on the bus, and messages for BrimDeck.
public sealed record ServiceOutput(IReadOnlyList<DBusMessage> Bus, IReadOnlyList<NoticeMessage> ToBrimDeck)
{
    public static readonly ServiceOutput None = new([], []);
}

// session names this run of the service in the notices it passes on: its ids start again at 1 each run, so BrimDeck keys a
// notice by the session and the id, and a new run's notice 1 never updates an earlier run's (core-H2).
public sealed class NotificationServer(string version, Func<string>? machineId = null, string session = "server")
{
    private uint _next;
    public string Session { get; } = session;
    // Ids handed out and still open, so that CloseNotification and BrimDeck's dismissals only signal real notices.
    private readonly HashSet<uint> _open = [];
    // When the open notices whose application asked for an expire_timeout expire; only ids still in _open (W12).
    private readonly Dictionary<uint, DateTimeOffset> _expires = [];
    public const int MaxOpen = 4096;

    // When the next notice expires; null while none asked to, so nothing needs to wake.
    public DateTimeOffset? NextExpiry => _expires.Count == 0 ? null : _expires.Values.Min();

    // now: when the call arrived, from which an expire_timeout counts; the present by default.
    public ServiceOutput Handle(DBusMessage call, DateTimeOffset? now = null)
    {
        if (call.Type != DBusMessageType.MethodCall) return ServiceOutput.None;
        bool wantsReply = (call.Flags & DBusMessage.NoReplyExpected) == 0;
        // Too large to read even without its image: the sender hears so at once instead of waiting for the bus's timeout (C-L6).
        if (call.Oversized)
            return wantsReply ? new([call.ErrorFor(DBusConnection.LimitsExceeded, "The message is too large for BrimDeck.")], []) : ServiceOutput.None;
        DBusMessage? reply;
        var forward = new List<NoticeMessage>();
        var bus = new List<DBusMessage>();
        try
        {
            switch ((call.Interface, call.Member))
            {
                case (FreedesktopNotifications.Interface or null, "GetCapabilities"):
                    reply = call.ReturnFor("as", FreedesktopNotifications.Capabilities.Cast<object>().ToList()); break;
                case (FreedesktopNotifications.Interface or null, "GetServerInformation"):
                    reply = call.ReturnFor("ssss", "BrimDeck", "BrimDeck", version, "1.2"); break;
                case (FreedesktopNotifications.Interface or null, "Notify") when call.Signature == FreedesktopNotifications.NotifySignature:
                {
                    uint replaces = (uint)call.Body[1];
                    uint id = replaces != 0 && _open.Contains(replaces) ? replaces : NextId();
                    var message = FreedesktopNotifications.ToNotice(call.Body, id);
                    message.Session = Session;
                    // A positive expire_timeout closes the notice for the application when it runs out, as the
                    // specification asks: "notify-send --wait -t 5000" waits for that. -1 (the server decides) and 0
                    // (never) keep it open until the user acts on it; a replacement's own timeout counts from now.
                    if (call.Body[7] is int timeout && timeout > 0) _expires[id] = (now ?? DateTimeOffset.UtcNow).AddMilliseconds(timeout);
                    else _expires.Remove(id);
                    forward.Add(message);
                    reply = call.ReturnFor("u", id);
                    break;
                }
                case (FreedesktopNotifications.Interface or null, "CloseNotification") when call.Signature == "u":
                {
                    uint id = (uint)call.Body[0];
                    if (Forget(id))
                    {
                        bus.Add(Closed(id, FreedesktopNotifications.ClosedByCall));
                        forward.Add(new NoticeMessage { Type = NoticeTypes.Close, Origin = "dbus", Session = Session, NativeId = Id(id), Reason = (int)FreedesktopNotifications.ClosedByCall });
                    }
                    reply = call.ReturnFor();
                    break;
                }
                case ("org.freedesktop.DBus.Introspectable", "Introspect"):
                    reply = call.ReturnFor("s", FreedesktopNotifications.Introspection); break;
                case ("org.freedesktop.DBus.Peer", "Ping"):
                    reply = call.ReturnFor(); break;
                case ("org.freedesktop.DBus.Peer", "GetMachineId"):
                    reply = call.ReturnFor("s", machineId?.Invoke() ?? ""); break;
                default:
                    reply = call.ErrorFor("org.freedesktop.DBus.Error.UnknownMethod", $"No method {call.Interface}.{call.Member} with signature {call.Signature}.");
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or IndexOutOfRangeException)
        {
            reply = call.ErrorFor("org.freedesktop.DBus.Error.InvalidArgs", "Invalid arguments.");
        }
        if (wantsReply && reply is not null) bus.Insert(0, reply);
        return new(bus, forward);
    }

    // The answer to a call to the agent's own bus name while it is not the notification service (it watches another, or has
    // no name): an error at once, instead of leaving the caller to the bus's 25-second timeout (W10). Nothing for a call that
    // expects no reply.
    public static ServiceOutput NotServing(DBusMessage call)
        => call.Type != DBusMessageType.MethodCall || (call.Flags & DBusMessage.NoReplyExpected) != 0 ? ServiceOutput.None
            : new([call.ErrorFor(UnknownMethod, "BrimDeck's agent is not the notification service now.")], []);

    public const string UnknownMethod = "org.freedesktop.DBus.Error.UnknownMethod";

    // Whether BrimDeck's action or dismissal is about this run's notices. One for another session is about an earlier run's
    // notice, whose id this run may have handed out again (C-L5); one without a session comes from a BrimDeck that does not
    // send it yet, and is taken as before.
    public bool Addresses(NoticeMessage request) => request.Session is null || request.Session == Session;

    // The user chose an action in BrimDeck: the application learns which, and the notice closes as dismissed.
    public IReadOnlyList<DBusMessage> ActionInvoked(uint id, string key)
    {
        if (!Forget(id)) return [];
        return [DBusMessage.Signal(FreedesktopNotifications.Path, FreedesktopNotifications.Interface, "ActionInvoked", "us", id, key), Closed(id, FreedesktopNotifications.DismissedByUser)];
    }

    // The user dismissed the notice in BrimDeck (its close button, or cleared it from the recent notices). A notice BrimDeck
    // merely stopped showing is not dismissed: it stays open for the application.
    public IReadOnlyList<DBusMessage> Dismissed(uint id) => Forget(id) ? [Closed(id, FreedesktopNotifications.DismissedByUser)] : [];

    // The notices whose expire_timeout ran out by now: closed for their applications as expired (W12), and for BrimDeck as
    // the CloseNotification branch closes them, so that it drops their buttons, which would no longer reach the application
    // (review-batch1 item 2). BrimDeck keeps them in its recent notices, marked as closed at the source.
    public ServiceOutput Expire(DateTimeOffset now)
    {
        var due = _expires.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList();
        foreach (var id in due) Forget(id);
        return new(due.Select(id => Closed(id, FreedesktopNotifications.Expired)).ToList(),
            due.Select(id => new NoticeMessage { Type = NoticeTypes.Close, Origin = "dbus", Session = Session, NativeId = Id(id), Reason = (int)FreedesktopNotifications.Expired }).ToList());
    }

    private bool Forget(uint id) { _expires.Remove(id); return _open.Remove(id); }

    private uint NextId()
    {
        do { _next = _next == uint.MaxValue ? 1 : _next + 1; } while (_open.Contains(_next));
        _open.Add(_next);
        // Applications that never close their notices must not grow the set without bound; the oldest ids are forgotten.
        if (_open.Count > MaxOpen) Forget(_open.Min());
        return _next;
    }

    private static DBusMessage Closed(uint id, uint reason)
        => DBusMessage.Signal(FreedesktopNotifications.Path, FreedesktopNotifications.Interface, "NotificationClosed", "uu", id, reason);
    public static string Id(uint id) => id.ToString(CultureInfo.InvariantCulture);
}

// Watches another notification service. Notify calls are held until the service's reply gives their id (a replacement
// already names it); a call that is never answered is passed on after Patience with no id.
public sealed class NotificationMonitor
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);
    private const int MaxPending = 256;
    private readonly Dictionary<(string Sender, uint Serial), (NoticeMessage Message, DateTimeOffset At)> _pending = [];
    // The notification service's unique bus name; replies from anyone else are ignored.
    public string Owner { get; set; } = "";
    // Names the watched service in the notices passed on, since its ids start again when it restarts; the agent gives each
    // service it watches a new one (core-H2).
    public string Session { get; set; } = "monitor";
    // When the oldest held call is to be passed on without an id; null while none is held, so nothing needs to wake (perf-12).
    public DateTimeOffset? NextDue => _pending.Count == 0 ? null : _pending.Values.Min(pending => pending.At) + Patience;

    public IReadOnlyList<NoticeMessage> Observe(DBusMessage message, DateTimeOffset now)
    {
        var result = Flush(now);
        // Only the header of a message too large to read; the watched service answers it itself.
        if (message.Oversized) return result;
        if (message.Type == DBusMessageType.MethodCall && message.Interface == FreedesktopNotifications.Interface && message.Member == "Notify"
            && message.Signature == FreedesktopNotifications.NotifySignature)
        {
            uint replaces = message.Body[1] is uint r ? r : 0;
            var notice = FreedesktopNotifications.ToNotice(message.Body, replaces);
            notice.Session = Session;
            // Actions belong to the other service; BrimDeck cannot invoke them.
            notice.Actions = null;
            if (replaces != 0) { result.Add(notice); return result; }
            if (message.Sender is { } sender)
            {
                if (_pending.Count >= MaxPending) { result.AddRange(_pending.Values.Select(p => Unnumbered(p.Message))); _pending.Clear(); }
                _pending[(sender, message.Serial)] = (notice, now);
            }
            else result.Add(Unnumbered(notice));
            return result;
        }
        if (message.Type is DBusMessageType.MethodReturn or DBusMessageType.Error && message.Sender == Owner && message.Destination is { } destination
            && message.ReplySerial is { } serial && _pending.Remove((destination, serial), out var pending))
        {
            if (message.Type == DBusMessageType.MethodReturn && message.Body is [uint id, ..])
            {
                pending.Message.NativeId = NotificationServer.Id(id);
                result.Add(pending.Message);
            }
            return result;
        }
        if (message.Type == DBusMessageType.Signal && message.Interface == FreedesktopNotifications.Interface && message.Member == "NotificationClosed"
            && message.Sender == Owner && message.Body is [uint closed, uint reason])
            result.Add(new NoticeMessage { Type = NoticeTypes.Close, Origin = "dbus", Session = Session, NativeId = NotificationServer.Id(closed), Reason = (int)reason });
        return result;
    }

    // Passes on calls whose reply did not come in time.
    public List<NoticeMessage> Flush(DateTimeOffset now)
    {
        var late = _pending.Where(pair => now - pair.Value.At >= Patience).ToList();
        foreach (var pair in late) _pending.Remove(pair.Key);
        return late.Select(pair => Unnumbered(pair.Value.Message)).ToList();
    }

    private static NoticeMessage Unnumbered(NoticeMessage message) { message.NativeId = null; return message; }

    public static readonly string[] Rules =
    [
        "type='method_call',interface='org.freedesktop.Notifications',member='Notify'",
        "type='signal',interface='org.freedesktop.Notifications',member='NotificationClosed'"
    ];
    public static string ReplyRule(string owner) => $"type='method_return',sender='{owner}'";
}
