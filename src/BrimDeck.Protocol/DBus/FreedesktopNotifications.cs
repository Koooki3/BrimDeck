using System.Net;
using System.Text.RegularExpressions;

namespace BrimDeck.Protocol.DBus;

// The org.freedesktop.Notifications interface: the call a desktop application makes, and the replies and signals a
// notification service sends. See the Desktop Notifications Specification 1.2.
public static partial class FreedesktopNotifications
{
    public const string Name = "org.freedesktop.Notifications", Path = "/org/freedesktop/Notifications", Interface = "org.freedesktop.Notifications";
    public const string NotifySignature = "susssasa{sv}i";
    // NotificationClosed reasons.
    public const uint Expired = 1, DismissedByUser = 2, ClosedByCall = 3, Undefined = 4;
    // Only what the agent implements: body text, actions (shown as buttons in the notice's details), and body markup, which
    // BrimDeck shows as plain text (PlainText). Saying "body-markup" makes clients that check escape a literal "<" or "&" in
    // the body, which the decoding then turns back into what they meant (W11).
    public static readonly string[] Capabilities = ["body", "body-markup", "actions"];
    // Hints that name a notice a client sends again and again in its place: a volume or brightness OSD sends a new notice
    // (replaces_id 0) at each step with the same tag, which then updates the one shown instead of adding another (I1).
    public static readonly string[] StackHints = ["x-canonical-private-synchronous", "synchronous", "x-dunst-stack-tag"];
    // The ReplaceId given for such a tag; within the application's own notices only (NoticeHub matches it by AppKey).
    public const string StackPrefix = "stack:";

    public const string Introspection = """
        <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN" "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
        <node>
          <interface name="org.freedesktop.Notifications">
            <method name="GetCapabilities"><arg direction="out" type="as"/></method>
            <method name="Notify">
              <arg direction="in" type="s"/><arg direction="in" type="u"/><arg direction="in" type="s"/><arg direction="in" type="s"/>
              <arg direction="in" type="s"/><arg direction="in" type="as"/><arg direction="in" type="a{sv}"/><arg direction="in" type="i"/>
              <arg direction="out" type="u"/>
            </method>
            <method name="CloseNotification"><arg direction="in" type="u"/></method>
            <method name="GetServerInformation">
              <arg direction="out" type="s"/><arg direction="out" type="s"/><arg direction="out" type="s"/><arg direction="out" type="s"/>
            </method>
            <signal name="NotificationClosed"><arg type="u"/><arg type="u"/></signal>
            <signal name="ActionInvoked"><arg type="u"/><arg type="s"/></signal>
          </interface>
          <interface name="org.freedesktop.DBus.Introspectable"><method name="Introspect"><arg direction="out" type="s"/></method></interface>
          <interface name="org.freedesktop.DBus.Peer"><method name="Ping"/><method name="GetMachineId"><arg direction="out" type="s"/></method></interface>
        </node>
        """;

    // A Notify call's arguments as a notice. The id is the one the service gives (or gave, for a replacement); the sender is
    // the caller's unique bus name, which with the id identifies the notice for later updates. Texts are cut to the protocol's
    // limits here, as BrimDeck would cut them: a long body must not make the notice too large for a frame (sec-L3). Only the
    // body may carry markup; the summary is plain text by the specification and is kept as it is, "<" and "&" included (W11).
    public static NoticeMessage ToNotice(object[] body, uint id)
    {
        string appName = body[0] as string ?? "", icon = body[2] as string ?? "", summary = body[3] as string ?? "", text = body[4] as string ?? "";
        var actions = body[5] as IList<object> ?? [];
        var hints = body[6] as IDictionary<object, object> ?? new Dictionary<object, object>();
        var message = new NoticeMessage
        {
            Origin = "dbus", NativeId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AppName = appName.Length > 0 ? NoticeProtocol.Clip(appName, NoticeProtocol.MaxField) : null,
            Title = NoticeProtocol.Clip(summary, NoticeProtocol.MaxTitle), Body = NoticeProtocol.Clip(PlainText(text), NoticeProtocol.MaxBody),
            Icon = NoticeProtocol.Clip(icon.Length > 0 ? icon : Hint<string>(hints, "image-path") ?? Hint<string>(hints, "image_path"), 1024),
            AppId = NoticeProtocol.Clip(Hint<string>(hints, "desktop-entry"), NoticeProtocol.MaxField),
            CreatedAt = DateTimeOffset.UtcNow,
            Category = CategoryOf(Hint<string>(hints, "category")),
            // Mail clients (Thunderbird, Evolution) mark new mail with this category; it is shown as a mail notice.
            Kind = Hint<string>(hints, "category") is "email" or "email.arrived" ? NoticeTimes.Mail : null,
            Priority = Hint<byte?>(hints, "urgency") switch { 0 => "low", 2 => "high", _ => null }
        };
        var pairs = new List<NoticeActionInfo>();
        for (int i = 0; i + 1 < actions.Count && pairs.Count < NoticeProtocol.MaxActions; i += 2)
            if (actions[i] is string key && actions[i + 1] is string label && key.Length > 0)
                pairs.Add(new() { Key = NoticeProtocol.Clip(key, 64)!, Label = NoticeProtocol.Clip(label.Length > 0 ? label : key, 64)! });
        if (pairs.Count > 0) message.Actions = pairs;
        if (Hint<int?>(hints, "value") is { } value && value is >= 0 and <= 100) message.Progress = value / 100d;
        if (StackHints.Select(name => Hint<string>(hints, name)).FirstOrDefault(tag => !string.IsNullOrWhiteSpace(tag)) is { } stack)
            message.ReplaceId = NoticeProtocol.Clip(StackPrefix + stack, NoticeProtocol.MaxField);
        // Not to be kept once it went: BrimDeck shows it but does not save it with the recent notices (I1).
        if (Hint<bool?>(hints, "transient") == true || Hint<int?>(hints, "transient") is { } flag && flag != 0) message.Transient = true;
        return message;
    }

    private static T? Hint<T>(IDictionary<object, object> hints, string name)
    {
        if (!hints.TryGetValue(name, out var raw) || raw is not DBusVariant variant) return default;
        object value = variant.Value;
        if (typeof(T) == typeof(int?) && value is IConvertible && value is not string and not bool)
            try { return (T)(object)Convert.ToInt32(value); } catch (OverflowException) { return default; }
        if (typeof(T) == typeof(byte?) && value is IConvertible && value is not string and not bool)
            try { return (T)(object)Convert.ToByte(value); } catch (OverflowException) { return default; }
        return value is T typed ? typed : default;
    }

    // The specification's categories ("im.received", "transfer.complete", ...) mapped to the notice categories.
    public static string? CategoryOf(string? category)
    {
        if (string.IsNullOrEmpty(category)) return null;
        if (category.EndsWith(".error", StringComparison.Ordinal) || category == "network.error") return "error";
        if (category.EndsWith(".complete", StringComparison.Ordinal)) return "success";
        if (category.StartsWith("im", StringComparison.Ordinal) || category.StartsWith("email", StringComparison.Ordinal)) return "message";
        if (category.StartsWith("transfer", StringComparison.Ordinal)) return "task";
        return "info";
    }

    // A body in the specification's small markup subset ("body-markup"), as plain text: tags go, line breaks stay, entities
    // are decoded. A watched service may not say "body-markup"; its bodies are treated the same.
    public static string PlainText(string text)
    {
        if (text.IndexOf('<') < 0 && text.IndexOf('&') < 0) return text;
        var plain = Tags().Replace(text.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase).Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase), "");
        return WebUtility.HtmlDecode(plain);
    }

    [GeneratedRegex("<[^>]{0,200}>")]
    private static partial Regex Tags();
}
