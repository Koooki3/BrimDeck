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
    // How often the agent looks for a session bus while it has none: only a few file names, so a bus that appears (the user's
    // first terminal starting user@.service) is found within seconds instead of half a minute later (W6).
    public static readonly TimeSpan BusCheck = TimeSpan.FromSeconds(3);

    // The agent's exit code when it ended because the distribution was idle (W2). wsl.exe hands it on; the bridge then waits
    // for the distribution to stop and run again instead of retrying.
    public const int IdleExitCode = 75;
    // How often the agent looks whether the user still runs anything, and for how long nothing may run before it ends.
    public static readonly TimeSpan IdleCheck = TimeSpan.FromMinutes(1), IdleLimit = TimeSpan.FromMinutes(5);
    // An agent that ended as idle while its distribution kept running (something else holds it, or the user came back in
    // the seconds before WSL stopped it) is started again after this long, at most once per this long: never a loop that
    // keeps a distribution up, while a terminal opened just then still gets notices within this time.
    public static readonly TimeSpan IdleRestart = TimeSpan.FromMinutes(30);

    // Whether the bridge starts an agent that ended as idle: once the distribution is listed as running again after it was
    // not (the bridge then forgets the idle end, see WslNoticeAgent.MarkNotRunning), or after IdleRestart while it stayed up.
    public static bool StartAfterIdle(bool running, TimeSpan sinceIdle) => running && sinceIdle >= IdleRestart;

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

// One process as the agent reads it from /proc/<pid>/status.
public sealed record LinuxProcess(int Pid, int ParentPid, uint UserId, string Name);

// Whether the user still uses the distribution, for the agent's idle end (W2). The agent itself is a wsl.exe session, and
// WSL 2 stops a distribution, and then its virtual machine, only once no such session is left; an agent that never ends would
// keep both running for as long as BrimDeck runs. Busy is the user having any process that is not plumbing: the agent and
// what it started, WSL's own init and relay processes, and the session services (systemd --user, (sd-pam), the session bus).
// A background job the user left running counts as use, so the agent never lets WSL stop a distribution under it.
public static class WslIdle
{
    // Names as /proc gives them (at most 15 characters). systemd counts only as the user's own manager, never as pid 1.
    private static readonly string[] Plumbing = ["systemd", "(sd-pam)", "dbus-daemon", "dbus-broker", "dbus-broker-lau", "init", "SessionLeader", "plan9", "init-watcher"];

    public static bool Busy(IEnumerable<LinuxProcess> processes, int self, uint uid)
    {
        var all = processes.ToList();
        var parents = all.GroupBy(process => process.Pid).ToDictionary(group => group.Key, group => group.First().ParentPid);
        return all.Any(process => process.UserId == uid && process.Pid != 1 && !IsPlumbing(process.Name) && !From(process.Pid, self, parents));
    }

    private static bool IsPlumbing(string name)
        => Plumbing.Contains(name, StringComparer.Ordinal) || name.StartsWith("Relay(", StringComparison.Ordinal) || name.StartsWith("init-", StringComparison.Ordinal);

    // Whether pid is self or one of its descendants; a cycle or a chain longer than any real one ends the walk.
    private static bool From(int pid, int self, Dictionary<int, int> parents)
    {
        for (int hops = 0; hops < 64 && pid > 1; hops++)
        {
            if (pid == self) return true;
            if (!parents.TryGetValue(pid, out pid)) return false;
        }
        return false;
    }

    // The fields of /proc/<pid>/status the check needs; null for a text without them (a process that ended while read).
    public static LinuxProcess? Parse(int pid, string status)
    {
        string? name = null;
        int? parent = null;
        uint? uid = null;
        foreach (var line in status.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var value = line[(colon + 1)..].Trim();
            switch (line[..colon])
            {
                case "Name": name = value; break;
                case "PPid" when int.TryParse(value, out var p): parent = p; break;
                // The real user id, the first of four.
                case "Uid" when uint.TryParse(value.Split('\t', ' ')[0], out var u): uid = u; break;
            }
        }
        return name is not null && parent is not null && uid is not null ? new(pid, parent.Value, uid.Value, name) : null;
    }
}

// Counts how long the distribution has been idle from the agent's checks; Due once it was idle at every check for Limit.
// A limit of zero or less never ends the agent.
public sealed class IdleClock(TimeSpan limit)
{
    private DateTimeOffset? _since;

    public bool Due(bool busy, DateTimeOffset now)
    {
        if (busy || limit <= TimeSpan.Zero) { _since = null; return false; }
        _since ??= now;
        return now - _since.Value >= limit;
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
