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
    // Only what the agent implements: plain body text and actions (shown as buttons in the notice's details).
    public static readonly string[] Capabilities = ["body", "actions"];

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
    // the caller's unique bus name, which with the id identifies the notice for later updates.
    public static NoticeMessage ToNotice(object[] body, uint id)
    {
        string appName = body[0] as string ?? "", icon = body[2] as string ?? "", summary = body[3] as string ?? "", text = body[4] as string ?? "";
        var actions = body[5] as IList<object> ?? [];
        var hints = body[6] as IDictionary<object, object> ?? new Dictionary<object, object>();
        var message = new NoticeMessage
        {
            Origin = "dbus", NativeId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AppName = appName.Length > 0 ? appName : null, Title = PlainText(summary), Body = PlainText(text),
            Icon = icon.Length > 0 ? icon : Hint<string>(hints, "image-path") ?? Hint<string>(hints, "image_path"),
            AppId = Hint<string>(hints, "desktop-entry"),
            CreatedAt = DateTimeOffset.UtcNow,
            Category = CategoryOf(Hint<string>(hints, "category")),
            Priority = Hint<byte?>(hints, "urgency") switch { 0 => "low", 2 => "high", _ => null }
        };
        var pairs = new List<NoticeActionInfo>();
        for (int i = 0; i + 1 < actions.Count; i += 2)
            if (actions[i] is string key && actions[i + 1] is string label && key.Length > 0) pairs.Add(new() { Key = key, Label = label.Length > 0 ? label : key });
        if (pairs.Count > 0) message.Actions = pairs;
        if (Hint<int?>(hints, "value") is { } value && value is >= 0 and <= 100) message.Progress = value / 100d;
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

    // Bodies may carry a small markup subset even to a service without "body-markup"; BrimDeck shows plain text.
    public static string PlainText(string text)
    {
        if (text.IndexOf('<') < 0 && text.IndexOf('&') < 0) return text;
        var plain = Tags().Replace(text.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase).Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase), "");
        return WebUtility.HtmlDecode(plain);
    }

    [GeneratedRegex("<[^>]{0,200}>")]
    private static partial Regex Tags();
}
