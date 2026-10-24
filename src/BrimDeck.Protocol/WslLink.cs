namespace BrimDeck.Protocol;

// How BrimDeck, a distribution's agent and brim-notify inside it keep the WSL link going: the times each side waits, the
// bridge's pause between starts of an agent, and the environment wsl.exe runs in. Logic without I/O, so that it can be tested.
public static class WslLink
{
    // How long the agent waits for BrimDeck's reply to a brim-notify message, and how long brim-notify waits for the agent's.
    // brim-notify waits longer, so that it hears the agent's "no-reply" rather than giving up first and sending the message
    // again another way (W3).
    public static readonly TimeSpan AgentReplyWait = TimeSpan.FromSeconds(8), CliReplyWait = TimeSpan.FromSeconds(12);
    // How often the agent looks for its CLI socket's file, or for the runtime directory to put it in (W4).
    public static readonly TimeSpan SocketCheck = TimeSpan.FromSeconds(30);

    // Variables that change how wsl.exe writes its own output: WSL_UTF8 makes "wsl --list" write UTF-8 instead of UTF-16,
    // which BrimDeck would read as garbage, and every distribution then as not running (W1).
    public static readonly string[] OutputVariables = ["WSL_UTF8"];

    // Removes them, whatever their case, from the environment a wsl.exe is started with.
    public static void ClearOutputVariables(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(key => OutputVariables.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList())
            environment.Remove(name);
    }

    // Waits for the next connection while checking every check whether the socket's file is still there: removing it does
    // not end the listening, it only leaves nobody able to connect (W4). Returns the connection, or null once the file is gone.
    public static async Task<T?> AcceptWhileThereAsync<T>(Func<CancellationToken, Task<T>> accept, Func<bool> there, TimeSpan check, CancellationToken token) where T : class
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var next = accept(stop.Token);
        while (true)
        {
            Task done;
            // The pause's timer is released as soon as a connection comes, rather than left to run out.
            using (var pause = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                done = await Task.WhenAny(next, Task.Delay(check, pause.Token));
                pause.Cancel();
            }
            if (done == next) return await next;
            token.ThrowIfCancellationRequested();
            if (there()) continue;
            stop.Cancel();
            try { return await next; }
            catch (OperationCanceledException) { return null; }
        }
    }
}

// The bridge's pause before it starts a distribution's agent again: it grows from Initial to Longest with each failure and
// starts over only once an agent kept running for Stable. An agent that reports its state and then ends at once is
// restarted ever less often, not every few seconds (W14). Not thread-safe; the bridge holds a lock around it.
public sealed class RetryBackoff
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(5), Longest = TimeSpan.FromMinutes(2), Stable = TimeSpan.FromSeconds(60);
    private TimeSpan _next = Initial;

    // The pause before the next start; each call doubles the one after it, up to Longest.
    public TimeSpan Next()
    {
        var wait = _next;
        _next = TimeSpan.FromTicks(Math.Min(Longest.Ticks, _next.Ticks * 2));
        return wait;
    }

    // An agent ended after running this long: only a run of Stable or more starts the pauses over.
    public void Ran(TimeSpan uptime) { if (uptime >= Stable) _next = Initial; }
}
