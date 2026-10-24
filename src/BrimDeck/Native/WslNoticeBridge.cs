using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

namespace BrimDeck.Native;

// Idle: the agent ended because the user ran nothing in the distribution for a while (W2), so that WSL can stop it.
// Unsupported: no build of the agent runs on the distribution's processor or C library (W15); the detail names them.
public enum WslAgentState { Off, NotRunning, NotPackaged, Installing, Starting, Server, Monitor, MonitorDenied, NoBus, Failed, Idle, Unsupported }

// The agents of the WSL distributions the user chose. A distribution is never started for notices: its agent starts once the
// distribution runs (checked every RunningCheck while it does not) and stops with BrimDeck, or ends by itself once the user
// ran nothing there for WslLink.IdleLimit, since an agent that never ends keeps the distribution and WSL's virtual machine
// running (W2); it starts again when the distribution next runs. Each agent is installed into ~/.local/share/brimdeck of the
// distribution's default user the first time, and again when BrimDeck brings a new one. Events are raised on worker threads.
internal sealed class WslNoticeBridge : IDisposable
{
    public static readonly TimeSpan RunningCheck = TimeSpan.FromSeconds(30);
    // Asking about a WSL 1 distribution starts wsl.exe; once it was found not running a few times, it is asked less often
    // (perf-12). A change of the chosen distributions asks at once again.
    public static readonly TimeSpan IdleWsl1Check = TimeSpan.FromMinutes(2);
    private const int QuickWsl1Checks = 3;
    // All agents stopped together get this long, in total, to end on their own before they are ended (life-L3).
    public static readonly TimeSpan StopWait = TimeSpan.FromMilliseconds(1500);
    private readonly Dictionary<string, WslNoticeAgent> _agents = new(StringComparer.OrdinalIgnoreCase);
    // The agents still ending, by distribution, under the lock of _agents: a distribution switched off and quickly on again
    // starts its new agent only once the old one is gone, which otherwise still holds the notification service (C-L12).
    private readonly Dictionary<string, Task> _stopping = new(StringComparer.OrdinalIgnoreCase);
    // Only cancelled, never disposed: the loop and starting agents may still read its token after Dispose (life-L6).
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    // Whether the loop runs, kept under the lock of _agents: the loop clears it in the same lock in which it finds no agent,
    // so Apply never sees a loop that is about to end as one still running (life-L4).
    private bool _looping;
    // Whether a chosen distribution runs under WSL 1; null until asked after the choice changed (a stale answer only costs
    // one more question).
    private bool? _wsl1;
    private int _idleChecks;

    public event Action<string, NoticeMessage>? Received;
    public event Action? StateChanged;

    // The agent build for a distribution's runtime identifier (see WslPlatform), or null when this copy does not include it.
    public static string? AgentPath(string rid) => WslPlatform.Find(AppContext.BaseDirectory, rid, File.Exists);
    // By its full path: a bare name is also looked for in BrimDeck's folder and the current directory before System32.
    public static string WslExe => Path.Combine(Environment.SystemDirectory, "wsl.exe");
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "BrimDeck.Notify.exe");
    public static bool AgentPackaged => WslPlatform.AnyPackaged(AppContext.BaseDirectory, File.Exists);

    public IReadOnlyDictionary<string, (WslAgentState State, string Detail, bool Conflict)> States
    {
        get { lock (_agents) return _agents.ToDictionary(pair => pair.Key, pair => (pair.Value.State, pair.Value.Detail, pair.Value.Conflict), StringComparer.OrdinalIgnoreCase); }
    }

    // Applies the chosen distributions: agents of distributions no longer chosen stop; new ones start when their distribution runs.
    public void Apply(IReadOnlyCollection<string> distributions)
    {
        var stopped = new List<WslNoticeAgent>();
        lock (_agents)
        {
            foreach (var name in _agents.Keys.Where(name => !distributions.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList())
            { stopped.Add(_agents[name]); _agents.Remove(name); }
            foreach (var name in _stopping.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToList()) _stopping.Remove(name);
            foreach (var name in distributions.Where(name => !_agents.ContainsKey(name)))
            {
                var agent = new WslNoticeAgent(name, _stopping.GetValueOrDefault(name));
                agent.Received += message => Received?.Invoke(name, message);
                agent.StateChanged += () => StateChanged?.Invoke();
                _agents[name] = agent;
            }
            _wsl1 = null;
            _idleChecks = 0;
            if (_agents.Count > 0 && !_looping) { _looping = true; _ = Task.Run(LoopAsync); }
        }
        // Outside the lock and off the calling (UI) thread: the agents end together, within StopWait in total (life-L3).
        if (stopped.Count > 0)
        {
            var ending = stopped.Select(agent => (agent.Distribution, Process: agent.BeginStop())).Where(pair => pair.Process is not null).ToList();
            if (ending.Count > 0)
            {
                var finish = Task.Run(() => WslNoticeAgent.FinishStop(ending.Select(pair => pair.Process!).ToList(), StopWait));
                lock (_agents) foreach (var (name, _) in ending) _stopping[name] = finish;
            }
        }
        Wake();
        StateChanged?.Invoke();
    }

    public void Send(string distribution, NoticeMessage message)
    {
        WslNoticeAgent? agent;
        lock (_agents) _agents.TryGetValue(distribution, out agent);
        agent?.Send(message);
    }

    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    private async Task LoopAsync()
    {
        while (true)
        {
            List<WslNoticeAgent> idle;
            lock (_agents)
            {
                if (_agents.Count == 0 || _stop.IsCancellationRequested) { _looping = false; return; }
                idle = _agents.Values.Where(agent => agent.NeedsStart).ToList();
            }
            var wait = RunningCheck;
            try { if (idle.Count > 0) wait = await StartRunningAsync(idle); }
            catch (Exception) when (_stop.IsCancellationRequested) { }
            // Whatever else goes wrong, the loop goes on and asks again at the next check; the agents say it failed (life-L5).
            catch (Exception ex)
            {
                NoticeCenter.Log(ex);
                foreach (var agent in idle) agent.Fail(ex.GetType().Name);
            }
            try { await _wake.WaitAsync(wait, _stop.Token); } catch (OperationCanceledException) { }
        }
    }

    // Starts the agents of the idle distributions that run; returns how long to wait before asking again.
    private async Task<TimeSpan> StartRunningAsync(List<WslNoticeAgent> idle)
    {
        // Asked once per set of chosen distributions: WSL 1 ones run without the virtual machine RunningDistributionsAsync looks for.
        if (_wsl1 is null)
        {
            var versions = await VersionsAsync(_stop.Token);
            _wsl1 = idle.Any(agent => versions.TryGetValue(agent.Distribution, out int version) && version == 1);
        }
        var running = await RunningDistributionsAsync(_stop.Token, anyWsl1: _wsl1 == true);
        bool waiting = false;
        foreach (var agent in idle)
        {
            // One that ended as idle waits for its distribution to stop and run again (see WslLink.StartAfterIdle).
            if (running.Contains(agent.Distribution)) { if (agent.MayStart()) _ = agent.StartAsync(_stop.Token); }
            else { agent.MarkNotRunning(); waiting = true; }
        }
        if (_wsl1 != true || !waiting) { _idleChecks = 0; return RunningCheck; }
        return ++_idleChecks > QuickWsl1Checks ? IdleWsl1Check : RunningCheck;
    }

    // The distributions running now. WSL 2 runs them all in one virtual machine process; without it no WSL 2 distribution runs,
    // and unless a chosen distribution is a WSL 1 one (anyWsl1), wsl.exe is not asked at all.
    public static async Task<HashSet<string>> RunningDistributionsAsync(CancellationToken token, bool anyWsl1 = false)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!anyWsl1 && !VirtualMachineRuns()) return names;
        return ParseRunning(await RunWslAsync(["--list", "--running", "--quiet"], token, Encoding.Unicode));
    }

    // "wsl --list --running --quiet": one name per line.
    internal static HashSet<string> ParseRunning(string? output)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.Trim('\0', '﻿').Trim() is { Length: > 0 } name) names.Add(name);
        return names;
    }

    // Whether WSL 2's virtual machine process runs: one look at the process list (perf-12), every Process object released.
    private static bool VirtualMachineRuns()
    {
        bool found = false;
        foreach (var process in Process.GetProcesses())
        {
            try { found |= process.ProcessName.Equals("vmmemWSL", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("vmmem", StringComparison.OrdinalIgnoreCase); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        return found;
    }

    // Each installed distribution's WSL version, from "wsl --list --verbose" (which starts no distribution): lines such as
    // "* Ubuntu-24.04    Running    2", the default one marked with an asterisk.
    public static async Task<Dictionary<string, int>> VersionsAsync(CancellationToken token)
    {
        var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var output = await RunWslAsync(["--list", "--verbose"], token, Encoding.Unicode);
        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
        {
            var parts = line.Trim('\0').TrimStart('*').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && int.TryParse(parts[^1], out int version)) versions[parts[0]] = version;
        }
        return versions;
    }

    internal static async Task<string?> RunWslAsync(IEnumerable<string> arguments, CancellationToken token, Encoding? encoding = null, TimeSpan? timeout = null)
        => await RunWslWithCodeAsync(arguments, token, encoding, timeout) is (0, var text) ? text : null;

    // wsl.exe's exit code and output; a null code when it could not start or ran out of time.
    internal static async Task<(int? Code, string Output)> RunWslWithCodeAsync(IEnumerable<string> arguments, CancellationToken token, Encoding? encoding = null, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(WslExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = encoding ?? Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // "wsl --list" writes UTF-16 only without the user's WSL_UTF8 (W1).
        WslLink.ClearOutputVariables(start.Environment);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return (null, "");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
            var read = process.StandardOutput.ReadToEndAsync(limit.Token);
            // Drained and dropped, a line at a time held to a bound (S8).
            _ = BoundedLines.ReadAsync(process.StandardError.BaseStream, 200, _ => { });
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } return (null, ""); }
            var text = await read;
            return (process.ExitCode, text);
        }
        // The time limit may also end the reading just after the process exited (life-L5).
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or OperationCanceledException) { return (null, ""); }
    }

    // The version stamp of a packaged agent (path), BrimDeck's version and the start of the file's hash, so a rebuilt agent is
    // installed again even without a version change, and another build (W15) has another stamp; and the whole hash, which the
    // install script checks its copy against. Hash is null when the file cannot be read.
    public static (string Stamp, string? Hash) AgentStamp(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            return (ApplicationVersion.Current + "-" + hash[..12], hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (ApplicationVersion.Current, null); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        List<Process> processes;
        lock (_agents) { processes = _agents.Values.Select(agent => agent.BeginStop()).OfType<Process>().ToList(); _agents.Clear(); }
        // On exit the agents still end together, within StopWait in total rather than each in turn.
        WslNoticeAgent.FinishStop(processes, StopWait);
    }
}

// One distribution's agent: installs it when needed and runs it through wsl.exe as the distribution's default user.
// previous is the ending of this distribution's earlier agent, if one was stopped a moment ago (C-L12).
internal sealed class WslNoticeAgent(string distribution, Task? previous = null)
{
    // The same install failure again for the same agent file: the distribution is not going to change by itself soon, so the
    // next try waits this long instead of copying the agent again every two minutes (P3).
    public static readonly TimeSpan RepeatedInstallFailure = TimeSpan.FromMinutes(30);
    // brim-notify in ~/.local/bin. BrimDeck treats the file as its own only while it is exactly this text: a file of that name
    // with anything else in it, or a link, belongs to the user and is left alone (reported as a conflict).
    private const string WrapperLines = "'#!/bin/sh' '# brim-notify, installed by BrimDeck: shows a notice in BrimDeck on Windows. BrimDeck replaces this file only while it is unchanged.' 'exec \"$HOME/.local/share/brimdeck/brimdeck-agent\" notify \"$@\"'";
    // What earlier test versions wrote, exactly: still BrimDeck's own, and replaced by the current text.
    private const string EarlierWrapperLines = "'#!/bin/sh' '# Installed by BrimDeck: shows a notice in BrimDeck on Windows.' 'exec \"$HOME/.local/share/brimdeck/brimdeck-agent\" notify \"$@\"'";
    // Copies the agent from Windows (path as $1, converted by wslpath), records how brim-notify reaches Windows without the
    // agent ($2), adds brim-notify (see WrapperLines) and writes the stamp ($3) last. BrimDeck's folder must be the user's own
    // and not a link, and is made private (0700) and checked to be so: nobody else may swap a checked file in it before it is
    // moved into place (S6). Every file is written to a new temporary file in it (mktemp creates it, so nothing is followed)
    // and renamed into place, which replaces a link instead of writing through it. The agent's copy must have the SHA-256
    // BrimDeck read ($4): a file replaced on Windows after BrimDeck read it is not installed (sec-M2). The hash is taken with
    // whichever of sha256sum, busybox, openssl and python3 the distribution has (C-L7). Everything that can refuse is checked
    // before the agent is copied, so a refusal costs no copy (P3). Prints "conflict" when brim-notify was left alone. Exit
    // codes: 3 the folder is refused, 4 the copy's hash differs, 5 no tool can take the hash (InstallFailure).
    // sh needs plain line feeds; a raw string takes the source file's line endings, which Git on Windows checks out as CRLF.
    private static readonly string InstallScript = $$"""
        set -eu
        umask 022
        dir="$HOME/.local/share/brimdeck"
        bin="$HOME/.local/bin"
        sha() {
          if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"
          elif command -v busybox >/dev/null 2>&1 && busybox sha256sum /dev/null >/dev/null 2>&1; then busybox sha256sum "$1"
          elif command -v openssl >/dev/null 2>&1; then openssl dgst -sha256 -r "$1"
          elif command -v python3 >/dev/null 2>&1; then python3 -c 'import hashlib, sys; print(hashlib.sha256(open(sys.argv[1], "rb").read()).hexdigest())' "$1"
          else return 1; fi
        }
        if ! sha /dev/null >/dev/null 2>&1; then echo "refused: no sha256sum, busybox, openssl or python3" >&2; exit 5; fi
        mkdir -p "$dir"
        if [ -L "$dir" ] || [ ! -O "$dir" ]; then echo "refused: $dir is a link or not owned by $(id -un)" >&2; exit 3; fi
        chmod 700 "$dir"
        case $(ls -ld "$dir") in d????-??-*) ;; *) echo "refused: others can write to $dir" >&2; exit 3;; esac
        # A step that fails leaves no temporary file of BrimDeck's behind.
        trap 'rm -f "$dir"/.new.??????' EXIT
        put() { t=$(mktemp "$dir/.new.XXXXXX"); cat > "$t"; chmod "$2" "$t"; mv -f "$t" "$1"; }
        src=$(wslpath -u "$1")
        t=$(mktemp "$dir/.new.XXXXXX")
        cat < "$src" > "$t"
        sum=$(sha "$t")
        if [ "${sum%% *}" != "$4" ]; then echo "refused: the agent changed while it was copied" >&2; exit 4; fi
        chmod 755 "$t"
        mv -f "$t" "$dir/brimdeck-agent"
        helper=$(wslpath -u "$2" 2>/dev/null || true)
        printf '{"helper":"%s","version":"%s"}\n' "$helper" "$3" | put "$dir/bridge.json" 644
        mkdir -p "$bin"
        b="$bin/brim-notify"
        ours() { printf '%s\n' {{WrapperLines}} | cmp -s - "$1" || printf '%s\n' {{EarlierWrapperLines}} | cmp -s - "$1"; }
        if [ -L "$b" ] || { [ -e "$b" ] && ! ours "$b"; }; then
          echo conflict
        else
          printf '%s\n' {{WrapperLines}} | put "$b" 755
          echo ok
        fi
        printf '%s' "$3" | put "$dir/agent.version" 644
        """.ReplaceLineEndings("\n");
    // The installed stamp on the first line; then whether brim-notify is BrimDeck's current one ("ok"), an earlier one of
    // BrimDeck's ("stale"), absent ("missing") or the user's own ("conflict"); then the machine and its program loaders, from
    // which WslPlatform.Select picks the agent build (W15).
    private static readonly string CheckScript = $$"""
        cat "$HOME/.local/share/brimdeck/agent.version" 2>/dev/null; echo
        b="$HOME/.local/bin/brim-notify"
        if [ -L "$b" ]; then echo conflict
        elif [ ! -e "$b" ]; then echo missing
        elif printf '%s\n' {{WrapperLines}} | cmp -s - "$b"; then echo ok
        elif printf '%s\n' {{EarlierWrapperLines}} | cmp -s - "$b"; then echo stale
        else echo conflict; fi
        {{WslPlatform.Probe}}
        """.ReplaceLineEndings("\n");
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private bool _installed, _disposed;
    // The agent's last line on standard error, for the failure's detail; nothing more of it is kept (sec-L3).
    private volatile string? _lastError;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    // Starts over only after an agent kept running a while, not at its first status report (W14).
    private readonly RetryBackoff _backoff = new();
    // This distribution's notices, counted before they are queued for the UI thread (W5). Above NoticeCenter.Admit's own
    // limit, which still decides which are shown.
    private readonly IntakeGate _intake = new(300, TimeSpan.FromSeconds(10));
    // The last failed install: the agent file's hash and the script's exit code (null: it did not finish).
    private (string Hash, int? Code)? _installFailure;
    // When the agent last ended as idle (WslLink.IdleExitCode), until the distribution is seen stopped; null otherwise.
    private DateTimeOffset? _idleSince;

    public string Distribution { get; } = distribution;
    public WslAgentState State { get; private set; } = WslAgentState.Off;
    public string Detail { get; private set; } = "";
    // ~/.local/bin/brim-notify is the user's own file, which BrimDeck left alone; the agent still works.
    public bool Conflict { get; private set; }
    public event Action<NoticeMessage>? Received;
    public event Action? StateChanged;

    public bool NeedsStart { get { lock (_gate) return !_disposed && _process is null && State != WslAgentState.Installing && State != WslAgentState.Starting && DateTimeOffset.UtcNow >= _retryAt; } }

    // The distribution is not running: an idle end is over, and the agent starts as soon as it runs again.
    public void MarkNotRunning()
    {
        lock (_gate) _idleSince = null;
        if (State is WslAgentState.Off or WslAgentState.NotRunning or WslAgentState.Failed or WslAgentState.Idle) SetState(WslAgentState.NotRunning, "");
    }

    // Whether to start the agent of a running distribution: always, unless it ended as idle and the distribution has kept
    // running since, for less than WslLink.IdleRestart.
    public bool MayStart()
    {
        lock (_gate) return _idleSince is not { } since || WslLink.StartAfterIdle(true, DateTimeOffset.UtcNow - since);
    }

    public void Fail(string detail) => Retry(detail);

    public async Task StartAsync(CancellationToken token)
    {
        if (!WslNoticeBridge.AgentPackaged) { SetState(WslAgentState.NotPackaged, ""); return; }
        bool installed;
        lock (_gate) { if (_disposed || _process is not null) return; installed = _installed; _idleSince = null; }
        try
        {
            // The earlier agent may still own the notification service and the CLI socket; this one would only watch it
            // (C-L12). Its stop ends within StopWait, killing it if need be.
            if (previous is { IsCompleted: false })
            {
                SetState(WslAgentState.Starting, "");
                try { await previous.WaitAsync(WslNoticeBridge.StopWait * 2, token); } catch (TimeoutException) { }
            }
            if (!installed)
            {
                SetState(WslAgentState.Installing, "");
                // Without the check's answer the build to install is unknown: tried again after the usual pause.
                if (await WslNoticeBridge.RunWslAsync(["-d", Distribution, "--exec", "/bin/sh", "-c", CheckScript], token) is not { } output) { Retry(InstallFailure(null)); return; }
                var check = output.Split('\n', StringSplitOptions.TrimEntries);
                // A distribution no build runs on, or whose build this copy does not include, will not change by itself: it is
                // asked again only after RepeatedInstallFailure (or when the user switches it off and on), never in a loop (W15).
                var (rid, machine, libc) = WslPlatform.Select(check.Length > 2 ? check[2] : null);
                if (rid is null) { Unavailable(WslAgentState.Unsupported, Unsupported(machine, libc)); return; }
                if (WslNoticeBridge.AgentPath(rid) is not { } path) { Unavailable(WslAgentState.NotPackaged, rid); return; }
                var (stamp, hash) = WslNoticeBridge.AgentStamp(path);
                if (hash is null) { Retry("install"); return; }
                string wrapper = check.Length > 1 ? check[1] : "";
                if (check[0] != stamp || wrapper is "missing" or "stale")
                {
                    var (code, result) = await WslNoticeBridge.RunWslWithCodeAsync(["-d", Distribution, "--exec", "/bin/sh", "-c", InstallScript, "sh", path, WslNoticeBridge.HelperPath, stamp, hash], token, timeout: TimeSpan.FromSeconds(60));
                    if (code != 0) { InstallFailed(hash, code); return; }
                    wrapper = result.Contains("conflict", StringComparison.Ordinal) ? "conflict" : "ok";
                }
                Conflict = wrapper == "conflict";
                lock (_gate) { _installed = true; _installFailure = null; }
            }
            SetState(WslAgentState.Starting, "");
            var start = new ProcessStartInfo(WslNoticeBridge.WslExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-d", Distribution, "--cd", "~", "--exec", "/bin/sh", "-c", "exec \"$HOME/.local/share/brimdeck/brimdeck-agent\" serve" }) start.ArgumentList.Add(argument);
            // As every wsl.exe BrimDeck starts: whatever wsl.exe itself says comes out the same way (W1).
            WslLink.ClearOutputVariables(start.Environment);
            var process = Process.Start(start);
            if (process is null) { Retry("start"); return; }
            lock (_gate)
            {
                if (_disposed) { try { process.Kill(); } catch (InvalidOperationException) { } process.Dispose(); return; }
                _process = process;
            }
            _lastError = null;
            // A line at a time, each held to 200 characters however long the agent makes it (S8).
            // Shown on the settings page: cleaned as other text from the distribution is (L9).
            _ = BoundedLines.ReadAsync(process.StandardError.BaseStream, 200, line => _lastError = NoticeProtocol.Clip(line, 200));
            _ = ReadAsync(process);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        // Whatever went wrong, the agent is tried again after the pause (life-L5).
        catch (Exception ex)
        {
            if (ex is not (System.ComponentModel.Win32Exception or IOException or InvalidOperationException)) NoticeCenter.Log(ex);
            Retry(ex.Message);
        }
    }

    private async Task ReadAsync(Process process)
    {
        var stream = process.StandardOutput.BaseStream;
        bool reported = false;
        long started = Environment.TickCount64;
        try
        {
            while (await NoticeFrames.ReadMessageAsync(stream) is { } message)
            {
                if (NoticeProtocol.Validate(message) is { } problem)
                {
                    // brim-notify waits for an answer: it hears why at once instead of "no-reply" after its wait (core-M2).
                    if (message.Type == NoticeTypes.Notify && message.Origin == "cli" && message.EventId is { } id) Send(NoticeProtocol.Reply(NoticeResults.Rejected, problem, id));
                    continue;
                }
                if (message.Type == NoticeTypes.Status)
                {
                    reported = true;
                    SetState(message.State switch
                    {
                        AgentStates.Server => WslAgentState.Server, AgentStates.Monitor => WslAgentState.Monitor,
                        AgentStates.MonitorDenied => WslAgentState.MonitorDenied, AgentStates.NoBus => WslAgentState.NoBus, _ => WslAgentState.Failed
                    }, message.Detail ?? "");
                    continue;
                }
                if (!NoticeIntake.AdmitFromAgent(_intake, message, Environment.TickCount64, out var refusal))
                {
                    if (refusal is not null) Send(refusal);
                    continue;
                }
                Received?.Invoke(message);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { }
        catch (Exception ex) { NoticeCenter.Log(ex); }
        // Its output ends as it exits; wsl.exe hands on the agent's exit code, which says whether it ended as idle (W2).
        int? code = null;
        try { if (process.WaitForExit(3000)) code = process.ExitCode; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        bool idle = reported && code == WslLink.IdleExitCode;
        lock (_gate)
        {
            if (_process == process) _process = null;
            // An agent that ended before it said anything did not start: the distribution may have been installed again or
            // the agent removed, so the next start checks the installation again (core-M3).
            if (!reported) _installed = false;
            _backoff.Ran(TimeSpan.FromMilliseconds(Environment.TickCount64 - started));
            if (idle) _idleSince = DateTimeOffset.UtcNow;
        }
        try { process.Dispose(); } catch (InvalidOperationException) { }
        if (_disposed) return;
        // Not a failure: no pause and no failed state; MayStart holds the next start until the distribution ran again.
        if (idle) SetState(WslAgentState.Idle, "");
        else Retry(_lastError is { } error && !reported ? "exited: " + error : "exited");
    }

    public void Send(NoticeMessage message)
    {
        Process? process;
        lock (_gate) process = _process;
        if (process is null) return;
        _ = Task.Run(async () =>
        {
            await _writeLock.WaitAsync();
            // An agent that stopped reading must not hold every later reply back (life-L15).
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await NoticeFrames.WriteAsync(process.StandardInput.BaseStream, message, limit.Token); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or InvalidDataException or OperationCanceledException) { }
            finally { _writeLock.Release(); }
        });
    }

    // A failed start or an agent that ended is tried again after a growing pause, up to two minutes (RetryBackoff); it also
    // has to wait for the distribution to run, so a distribution shut down meanwhile is not started again.
    private void Retry(string detail, TimeSpan? wait = null)
    {
        // Under the lock NeedsStart reads them with: retries come from the loop and from an agent's reading task.
        lock (_gate)
        {
            var next = _backoff.Next();
            _retryAt = DateTimeOffset.UtcNow + (wait ?? next);
        }
        SetState(WslAgentState.Failed, detail);
    }

    // Says why the install script failed (C-L7), and waits RepeatedInstallFailure when it failed the same way for the same
    // agent file last time (P3). Only a failure that will recur by itself counts (M4): the folder refused (3), the copy differing
    // (4), no hashing tool (5). A timeout (a cold start of WSL) or another code (a full disk, a failed read) is retried as usual.
    private void InstallFailed(string hash, int? code)
    {
        bool repeated;
        lock (_gate) { repeated = code is 3 or 4 or 5 && _installFailure == (hash, code); _installFailure = (hash, code); }
        Retry(InstallFailure(code), repeated ? RepeatedInstallFailure : null);
    }

    internal static string InstallFailure(int? code) => code switch
    {
        3 => Loc.T("无法安装：~/.local/share/brimdeck 是链接、不属于该用户，或其他用户可写入", "cannot install: ~/.local/share/brimdeck is a link, not the user's own, or writable by others"),
        4 => Loc.T("无法安装：复制后的代理与 BrimDeck 读到的不一致", "cannot install: the copied agent differs from the one BrimDeck read"),
        5 => Loc.T("无法安装：发行版中没有 sha256sum、busybox、openssl 或 python3", "cannot install: the distribution has no sha256sum, busybox, openssl or python3"),
        null => Loc.T("安装超时或无法运行 wsl.exe", "install timed out, or wsl.exe could not run"),
        _ => "install (" + code + ")"
    };

    // No agent build runs on the distribution: another processor (armv7l, riscv64...), or neither glibc's nor musl's loader.
    internal static string Unsupported(string machine, string libc)
    {
        return (machine.Length > 0 ? machine : "?") + (libc.Length > 0 ? ", " + libc : "");
    }

    // The distribution cannot get an agent from this copy of BrimDeck (W15): said once, and asked again only after
    // RepeatedInstallFailure, since neither the distribution nor BrimDeck's folder changes by itself.
    private void Unavailable(WslAgentState state, string detail)
    {
        lock (_gate) _retryAt = DateTimeOffset.UtcNow + RepeatedInstallFailure;
        SetState(state, detail);
    }

    private void SetState(WslAgentState state, string detail)
    {
        if (State == state && Detail == detail) return;
        State = state; Detail = detail;
        StateChanged?.Invoke();
    }

    // Stops the agent for good and asks it to end: closing its standard input makes it give up the notification service name
    // and exit. Returns its process for FinishStop, or null when none runs.
    public Process? BeginStop()
    {
        Process? process;
        lock (_gate) { _disposed = true; process = _process; _process = null; }
        if (process is null) return null;
        try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        return process;
    }

    // Waits for the stopped agents together, budget in total; those still running then are ended.
    public static void FinishStop(IReadOnlyList<Process> processes, TimeSpan budget)
    {
        long deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        foreach (var process in processes)
        {
            try { if (!process.WaitForExit((int)Math.Max(0, deadline - Environment.TickCount64))) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
    }
}
