using BrimDeck.Core.Notices;

// The sync button of the notices page (NoticeHub.SyncWindows): notices read from Windows' notification center are made to match
// it, and notices of every other source stay as they are.
internal static class NoticeWindowsSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static NoticeInput Center(string app, uint id, int minutesAgo, string title = "t") => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = "windows:" + app, AppName = app, AppId = app,
        InstanceKey = $"windows:{app}:{id}:{Now.AddMinutes(-minutesAgo).UtcTicks}", Title = title, CreatedAt = Now.AddMinutes(-minutesAgo),
        Capabilities = NoticeCapabilities.OpenApp
    };

    public static void Run(Action<string, bool> check)
    {
        var hub = new NoticeHub(key => key == "windows:Muted.App" ? new NoticeAppRule { Key = key, Enabled = false } : null);
        var kept = Center("Mail", 1, 30, "kept");
        var gone = Center("Mail", 2, 20, "gone");
        hub.Receive(kept, Now.AddMinutes(-30));
        hub.Receive(gone, Now.AddMinutes(-20));
        // Other sources: a taskbar flash (also of Windows origin), brim-notify, WSL and a test notice.
        hub.Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "flash:wechat", AppName = "微信", InstanceKey = "flash:wechat:1", Title = "flash" }, Now.AddMinutes(-15));
        hub.Receive(new NoticeInput { Origin = NoticeOrigin.Cli, AppKey = "cli:build", AppName = "build", InstanceKey = "cli:1", Title = "cli" }, Now.AddMinutes(-14));
        hub.Receive(new NoticeInput { Origin = NoticeOrigin.Wsl, AppKey = "wsl:Ubuntu:x", AppName = "x", Distro = "Ubuntu", InstanceKey = "wsl:Ubuntu:s:1", Title = "wsl" }, Now.AddMinutes(-13));
        hub.Receive(new NoticeInput { Origin = NoticeOrigin.Test, AppKey = "test", AppName = "BrimDeck", InstanceKey = "test:1", Title = "test" }, Now.AddMinutes(-12));
        hub.SourceRemoved(kept.InstanceKey);
        int changes = 0;
        hub.Changed += () => changes++;

        var missing = Center("Chat", 7, 40, "missing");
        var muted = Center("Muted.App", 8, 5, "muted");
        var (removed, added) = hub.SyncWindows([kept, missing, muted, missing], Now);

        var titles = hub.Notices.Select(n => n.Title).ToList();
        check("Sync: a center notice no longer in Windows is deleted", removed.Count == 1 && !titles.Contains("gone") && hub.Find(removed[0]) is null);
        check("Sync: a center notice still in Windows stays and reads as present", hub.FindInstance(kept.InstanceKey) is { SourcePresent: true });
        check("Sync: a notice in Windows but not listed is added once", added == 1 && titles.Count(t => t == "missing") == 1);
        check("Sync: an application the user turned off is not added", !titles.Contains("muted"));
        check("Sync: taskbar flash, brim-notify, WSL and test notices are not touched", new[] { "flash", "cli", "wsl", "test" }.All(titles.Contains));
        check("Sync: an added notice is listed by when Windows received it", titles.IndexOf("missing") == titles.Count - 1 && hub.FindInstance(missing.InstanceKey)!.ReceivedAt == Now.AddMinutes(-40));
        check("Sync: an added notice is kept for the retention from now", hub.FindInstance(missing.InstanceKey)!.UpdatedAt == Now && !hub.Prune(Now.AddHours(23)));
        check("Sync: the list raises one change", changes == 1);

        changes = 0;
        var again = hub.SyncWindows([kept, missing], Now);
        check("Sync: a second sync with the same center changes nothing", again.Removed.Count == 0 && again.Added == 0 && changes == 0);

        var empty = hub.SyncWindows([], Now);
        check("Sync: an empty center deletes every center notice and nothing else",
            empty.Removed.Count == 2 && !hub.Notices.Any(NoticeHub.FromWindowsCenter) && hub.Notices.Count == 4);
    }
}
