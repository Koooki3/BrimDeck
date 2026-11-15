using System.Xml;
using System.Xml.Linq;

namespace BrimDeck.Core.Notices;

public enum ToastActivationKind
{
    // Start or bring forward the application only, as before.
    Fallback,
    // Open the toast's own protocol link (Uri).
    Protocol,
    // Start the packaged application with the toast's launch string as its arguments (Arguments).
    PackageLaunch
}

public readonly record struct ToastActivationPlan(ToastActivationKind Kind, string? Uri = null, string? Arguments = null)
{
    public static ToastActivationPlan Fallback => new(ToastActivationKind.Fallback);
}

// The two attributes of a toast's root element that say what clicking it does.
public readonly record struct ToastLaunch(string Launch, string ActivationType);

// What opening an application from a Windows notice may do beyond starting it: replay the toast's own activation, so that the
// conversation the toast was about opens. Everything here is a pure decision on text; reading the toast (the notification
// database, one row, only when the user opens the notice) and starting things are the Windows side's job.
//
// Without a launch string nothing changes. A protocol toast opens its link only when the scheme is http or https, or is the
// application's own custom scheme (tg:, discord:, ...) and is not one that Windows or a browser gives a meaning of its own
// (file:, javascript:, ms-*:, search-ms:, ...). A packaged application gets the launch string as the arguments of its launch,
// the way Windows hands it to the application for a click on the toast. A desktop (unpackaged) application would need its COM
// activator (INotificationActivationCallback), which means creating an object from a registry class id the toast's sender
// registered; that is left out, and such an application is started as before.
public static class ToastActivation
{
    public const int MaxLaunch = 2048;
    private const int MaxToastXml = 1 << 20;

    // Schemes that are never opened from a toast: they run code, open files, or reach system handlers.
    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "javascript", "vbscript", "data", "blob", "about", "view-source", "search", "search-ms", "shell", "ie", "res", "mhtml", "mk",
        "jar", "wsh", "cmd", "powershell", "ldap", "ldaps", "ftp", "gopher", "telnet", "microsoft-edge", "chrome", "calculator"
    };

    // Windows' own handlers (ms-settings:, ms-msdt:, ms-cxh:, ms-officecmd:, ...) are all named ms-*.
    private static bool IsDenied(string scheme) => Denied.Contains(scheme) || scheme.StartsWith("ms-", StringComparison.OrdinalIgnoreCase);

    // The root element's launch and activationType, or null when the text is not a toast. DTDs are refused.
    public static ToastLaunch? ParseToast(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxToastXml) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var root = XDocument.Load(reader).Root;
            if (root is null || !string.Equals(root.Name.LocalName, "toast", StringComparison.OrdinalIgnoreCase)) return null;
            return new((string?)root.Attribute("launch") ?? "", (string?)root.Attribute("activationType") ?? "");
        }
        catch (XmlException) { return null; }
    }

    // The link to open for this text, or null when it must not be opened. The scheme is read as text up to the first colon
    // (a path such as C:\x or \\host\share has none that qualifies), compared without regard to case, and the rest may hold
    // no space, control character, quote, bracket, pipe, caret, backtick or backslash.
    public static string? ProtocolUri(string? text)
    {
        var uri = (text ?? "").Trim();
        if (uri.Length is 0 or > MaxLaunch) return null;
        int colon = uri.IndexOf(':');
        if (colon is < 2 or > 32 || colon == uri.Length - 1) return null;
        var scheme = uri[..colon];
        if (!char.IsAsciiLetter(scheme[0]) || !scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')) return null;
        if (IsDenied(scheme)) return null;
        if (uri.Any(c => c <= ' ' || c == 0x7f || c is '"' or '<' or '>' or '|' or '^' or '`' or '\\' or '{' or '}')) return null;
        if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return uri.Length > colon + 3 && uri.AsSpan(colon).StartsWith("://") ? uri : null;
        return uri;
    }

    // The plan for a click on the notice. The toast is null when it could not be read (or the switch is off).
    public static ToastActivationPlan Decide(bool enabled, ToastLaunch? toast, string appId, bool packaged)
    {
        if (!enabled || toast is not { } t) return ToastActivationPlan.Fallback;
        var launch = t.Launch.Trim();
        if (launch.Length is 0 or > MaxLaunch || launch.Any(char.IsControl)) return ToastActivationPlan.Fallback;
        var type = t.ActivationType.Trim();
        if (type.Equals("protocol", StringComparison.OrdinalIgnoreCase))
            return ProtocolUri(launch) is { } uri ? new(ToastActivationKind.Protocol, Uri: uri) : ToastActivationPlan.Fallback;
        // Foreground activation is the default. Background and system activations run no application window for a click: not replayed.
        if ((type.Length == 0 || type.Equals("foreground", StringComparison.OrdinalIgnoreCase)) && packaged && WindowsNoticeList.IsAppId(appId))
            return new(ToastActivationKind.PackageLaunch, Arguments: launch);
        return ToastActivationPlan.Fallback;
    }

    // The notification id inside a Windows notice's InstanceKey ("windows:<appId>:<id>:<ticks>"), when it is for this application.
    public static uint? NotificationId(string instanceKey, string appId)
    {
        var prefix = "windows:" + appId + ":";
        if (!instanceKey.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = instanceKey[prefix.Length..].Split(':');
        return parts.Length == 2 && uint.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && long.TryParse(parts[1], out _) ? id : null;
    }
}
