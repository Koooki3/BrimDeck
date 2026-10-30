namespace BrimDeck.Core.Notices;

// Which panel shows an arriving notice. Panels are identified by their lane name; the monitor ids are opaque handles.
public static class NoticeRouting
{
    public readonly record struct Candidate(string Lane, long Monitor, bool Main, bool Available);

    // With NoticeScreen.Foreground, the panel on the foreground window's monitor if it can show the notice, otherwise the main
    // panel. Null when the chosen panel cannot show anything (hidden by a full-screen rule, or by the user): the notice then
    // waits in the recent notices; it does not jump to another monitor.
    public static string? Choose(IReadOnlyList<Candidate> panels, long? foregroundMonitor, NoticeScreen screen)
    {
        if (panels.Count == 0) return null;
        var main = panels.FirstOrDefault(p => p.Main);
        if (main.Lane is null) main = panels[0];
        if (screen == NoticeScreen.Foreground && foregroundMonitor is { } monitor && panels.FirstOrDefault(p => p.Monitor == monitor) is { Lane: not null } match)
            return match.Available ? match.Lane : null;
        return main.Available ? main.Lane : null;
    }
}
