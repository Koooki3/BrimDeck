namespace BrimDeck.Core.Notices;

public enum NoticeClickAction { OpenPage, OpenApp }

// What a click (or the notices hotkey) on a notice shown on the collapsed island does. Only a Windows notice that carries an
// application id and can be opened is taken to its application; a WSL or brim-notify notice has no application BrimDeck can
// start, so it always opens the notices page, whatever the setting says.
public static class NoticeClick
{
    public static NoticeClickAction Decide(Notice? notice, NoticeClickTarget target)
        => target == NoticeClickTarget.SourceApp && notice is { Origin: NoticeOrigin.Windows, AppId.Length: > 0 } && (notice.Capabilities & NoticeCapabilities.OpenApp) != 0
            ? NoticeClickAction.OpenApp : NoticeClickAction.OpenPage;
}
