namespace BrimDeck.Core.Notices;

// Starts a program through a URL scheme the user's Windows has registered for it (ShellExecute), and reads whether a scheme is registered.
// Behind interfaces so the decision and its calls are checked without starting anything.
public interface IProtocolLauncher { bool Launch(string uri); }
public interface IProtocolRegistry { bool IsRegistered(string scheme); }

public enum ChatRestoreKind { Fallback, Restore }

public readonly record struct ChatRestoreDecision(ChatRestoreKind Kind, string? Uri)
{
    public static readonly ChatRestoreDecision Fallback = new(ChatRestoreKind.Fallback, null);
}

// What a click on a notice from the taskbar flash source (ChatFlash) may do besides opening the notices page: ask the chat
// application, through its own registered URL scheme, to bring its main window forward. The address is one fixed constant;
// nothing of the notice, the message or the user is put into it, and BrimDeck never touches the application's windows. Anything
// else (another source, another application, the setting off, the scheme not registered, a launch that failed) is the
// ordinary click.
public static class ChatRestore
{
    // The only address ever launched, and its scheme. A row of ChatFlashApps may name this exact string and nothing else.
    public const string WeChatUri = "weixin://";
    public const string WeChatScheme = "weixin";

    public static bool IsAllowed(string? uri) => string.Equals(uri, WeChatUri, StringComparison.Ordinal);

    // The chat application row behind a flash notice, or null when the notice is not one.
    public static ChatFlashApp? AppOf(Notice? notice, IReadOnlyList<ChatFlashApp>? apps = null)
    {
        const string prefix = "flash:";
        if (notice is not { Origin: NoticeOrigin.Windows } || notice.AppKey is not { } key || !key.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string id = key[prefix.Length..];
        return (apps ?? ChatFlashApps.All).FirstOrDefault(app => app.Enabled && string.Equals(app.Id, id, StringComparison.Ordinal));
    }

    public static ChatRestoreDecision Decide(Notice? notice, bool setting, bool protocolRegistered, IReadOnlyList<ChatFlashApp>? apps = null)
        => setting && protocolRegistered && AppOf(notice, apps) is { RestoreUri: { } uri } && IsAllowed(uri)
            ? new ChatRestoreDecision(ChatRestoreKind.Restore, uri) : ChatRestoreDecision.Fallback;

    // True when the application was asked to come forward. The registry is read only for a notice that qualifies, and the
    // launcher is called at most once, with the constant.
    public static bool TryRestore(Notice? notice, bool setting, IProtocolRegistry registry, IProtocolLauncher launcher, IReadOnlyList<ChatFlashApp>? apps = null)
    {
        if (Decide(notice, setting, true, apps).Kind != ChatRestoreKind.Restore) return false;
        bool registered;
        try { registered = registry.IsRegistered(WeChatScheme); } catch (Exception) { return false; }
        if (Decide(notice, setting, registered, apps) is not { Kind: ChatRestoreKind.Restore, Uri: { } uri }) return false;
        try { return launcher.Launch(uri); } catch (Exception) { return false; }
    }
}
