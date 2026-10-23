using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BrimDeck.Core;
using BrimDeck.Protocol;

namespace BrimDeck.Native;

public enum WslAgentState { Off, NotRunning, NotPackaged, Installing, Starting, Server, Monitor, MonitorDenied, NoBus, Failed }

// The agents of the WSL distributions the user chose. A distribution is never started for notices: its agent starts once the
// distribution runs (checked every RunningCheck while it does not) and stops with BrimDeck. Each agent is installed into
// ~/.local/share/brimdeck of the distribution's default user the first time, and again when BrimDeck brings a new one.
// Events are raised on worker threads.
internal sealed class WslNoticeBridge : IDisposable
{
    public static readonly TimeSpan RunningCheck = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, WslNoticeAgent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task _loop = Task.CompletedTask;
    // Whether a chosen distribution runs under WSL 1; null until asked after the choice changed (a stale answer only costs
    // one more question).
    private bool? _wsl1;

    public event Action<string, NoticeMessage>? Received;
    public event Action? StateChanged;

    public static string AgentPath => Path.Combine(AppContext.BaseDirectory, "wsl", "brimdeck-agent");
    // By its full path: a bare name is also looked for in BrimDeck's folder and the current directory before System32.
    public static string WslExe => Path.Combine(Environment.SystemDirectory, "wsl.exe");
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "BrimDeck.Notify.exe");
    public static bool AgentPackaged => File.Exists(AgentPath);

    public IReadOnlyDictionary<string, (WslAgentState State, string Detail, bool Conflict)> States
    {
        get { lock (_agents) return _agents.ToDictionary(pair => pair.Key, pair => (pair.Value.State, pair.Value.Detail, pair.Value.Conflict), StringComparer.OrdinalIgnoreCase); }
    }

    // Applies the chosen distributions: agents of distributions no longer chosen stop; new ones start when their distribution runs.
    public void Apply(IReadOnlyCollection<string> distributions)
    {
        lock (_agents)
        {
            foreach (var name in _agents.Keys.Where(name => !distributions.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList())
            { _agents[name].Dispose(); _agents.Remove(name); }
            foreach (var name in distributions.Where(name => !_agents.ContainsKey(name)))
            {
                var agent = new WslNoticeAgent(name);
                agent.Received += message => Received?.Invoke(name, message);
                agent.StateChanged += () => StateChanged?.Invoke();
                _agents[name] = agent;
            }
            _wsl1 = null;
            if (_agents.Count > 0 && _loop.IsCompleted) _loop = Task.Run(LoopAsync);
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
        while (!_stop.IsCancellationRequested)
        {
            List<WslNoticeAgent> idle;
            lock (_agents) { if (_agents.Count == 0) return; idle = _agents.Values.Where(agent => agent.NeedsStart).ToList(); }
            if (idle.Count > 0)
            {
                // Asked once per set of chosen distributions: WSL 1 ones run without the virtual machine RunningDistributionsAsync looks for.
                if (_wsl1 is null)
                {
                    var versions = await VersionsAsync(_stop.Token);
                    _wsl1 = idle.Any(agent => versions.TryGetValue(agent.Distribution, out int version) && version == 1);
                }
                var running = await RunningDistributionsAsync(_stop.Token, anyWsl1: _wsl1 == true);
                foreach (var agent in idle)
                {
                    if (running.Contains(agent.Distribution)) _ = agent.StartAsync(_stop.Token);
                    else agent.MarkNotRunning();
                }
            }
            try { await _wake.WaitAsync(RunningCheck, _stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    // The distributions running now. WSL 2 runs them all in one virtual machine process; without it no WSL 2 distribution runs,
    // and unless a chosen distribution is a WSL 1 one (anyWsl1), wsl.exe is not asked at all.
    public static async Task<HashSet<string>> RunningDistributionsAsync(CancellationToken token, bool anyWsl1 = false)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!anyWsl1 && !VirtualMachineRuns()) return names;
        var output = await RunWslAsync(["--list", "--running", "--quiet"], token, Encoding.Unicode);
        if (output is null) return names;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.Trim('\0').Trim() is { Length: > 0 } name) names.Add(name);
        return names;
    }

    // Whether WSL 2's virtual machine process runs. Every Process object found is released, not only the first.
    private static bool VirtualMachineRuns()
    {
        var found = Process.GetProcessesByName("vmmemWSL").Concat(Process.GetProcessesByName("vmmem")).ToList();
        foreach (var process in found) process.Dispose();
        return found.Count > 0;
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
    {
        var start = new ProcessStartInfo(WslExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = encoding ?? Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
            var read = process.StandardOutput.ReadToEndAsync(limit.Token);
            _ = process.StandardError.ReadToEndAsync(limit.Token);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } return null; }
            var text = await read;
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    // The version stamp of the packaged agent: BrimDeck's version and the start of the file's hash, so a rebuilt agent is
    // installed again even without a version change.
    public static string AgentStamp()
    {
        try
        {
            using var file = File.OpenRead(AgentPath);
            return ApplicationVersion.Current + "-" + Convert.ToHexString(SHA256.HashData(file))[..12].ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ApplicationVersion.Current; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_agents) { foreach (var agent in _agents.Values) agent.Dispose(); _agents.Clear(); }
        _stop.Dispose();
    }
}

// One distribution's agent: installs it when needed and runs it through wsl.exe as the distribution's default user.
internal sealed class WslNoticeAgent(string distribution) : IDisposable
{
    // brim-notify in ~/.local/bin. BrimDeck treats the file as its own only while it is exactly this text: a file of that name
    // with anything else in it, or a link, belongs to the user and is left alone (reported as a conflict).
    private const string WrapperLines = "'#!/bin/sh' '# brim-notify, installed by BrimDeck: shows a notice in BrimDeck on Windows. BrimDeck replaces this file only while it is unchanged.' 'exec \"$HOME/.local/share/brimdeck/brimdeck-agent\" notify \"$@\"'";
    // What earlier test versions wrote, exactly: still BrimDeck's own, and replaced by the current text.
    private const string EarlierWrapperLines = "'#!/bin/sh' '# Installed by BrimDeck: shows a notice in BrimDeck on Windows.' 'exec \"$HOME/.local/share/brimdeck/brimdeck-agent\" notify \"$@\"'";
    // Copies the agent from Windows (path as $1, converted by wslpath), records how brim-notify reaches Windows without the
    // agent ($2), adds brim-notify (see WrapperLines) and writes the stamp ($3) last. BrimDeck's folder must be the user's own
    // and not a link; every file is written to a new temporary file (mktemp creates it, so nothing is followed) and renamed
    // into place, which replaces a link instead of writing through it. Prints "conflict" when brim-notify was left alone.
    // sh needs plain line feeds; a raw string takes the source file's line endings, which Git on Windows checks out as CRLF.
    private static readonly string InstallScript = $$"""
        set -eu
        umask 022
        dir="$HOME/.local/share/brimdeck"
        bin="$HOME/.local/bin"
        mkdir -p "$dir"
        if [ -L "$dir" ] || [ ! -O "$dir" ]; then echo "refused: $dir is a link or not owned by $(id -un)" >&2; exit 3; fi
        # A step that fails leaves no temporary file of BrimDeck's behind.
        trap 'rm -f "$dir"/.new.?????? "$bin"/.brim-notify.??????' EXIT
        put() { t=$(mktemp "$dir/.new.XXXXXX"); cat > "$t"; chmod "$2" "$t"; mv -f "$t" "$1"; }
        src=$(wslpath -u "$1")
        put "$dir/brimdeck-agent" 755 < "$src"
        helper=$(wslpath -u "$2" 2>/dev/null || true)
        printf '{"helper":"%s","version":"%s"}\n' "$helper" "$3" | put "$dir/bridge.json" 644
        mkdir -p "$bin"
        b="$bin/brim-notify"
        ours() { printf '%s\n' {{WrapperLines}} | cmp -s - "$1" || printf '%s\n' {{EarlierWrapperLines}} | cmp -s - "$1"; }
        if [ -L "$b" ] || { [ -e "$b" ] && ! ours "$b"; }; then
          echo conflict
        else
          t=$(mktemp "$bin/.brim-notify.XXXXXX")
          printf '%s\n' {{WrapperLines}} > "$t"
          chmod 755 "$t"
          mv -f "$t" "$b"
          echo ok
        fi
        printf '%s' "$3" | put "$dir/agent.version" 644
        """.ReplaceLineEndings("\n");
    // The installed stamp on the first line; then whether brim-notify is BrimDeck's current one ("ok"), an earlier one of
    // BrimDeck's ("stale"), absent ("missing") or the user's own ("conflict").
    private static readonly string CheckScript = $$"""
        cat "$HOME/.local/share/brimdeck/agent.version" 2>/dev/null; echo
        b="$HOME/.local/bin/brim-notify"
        if [ -L "$b" ]; then echo conflict
        elif [ ! -e "$b" ]; then echo missing
        elif printf '%s\n' {{WrapperLines}} | cmp -s - "$b"; then echo ok
        elif printf '%s\n' {{EarlierWrapperLines}} | cmp -s - "$b"; then echo stale
        else echo conflict; fi
        """.ReplaceLineEndings("\n");
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private bool _installed, _disposed;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private TimeSpan _backoff = TimeSpan.FromSeconds(5);

    public string Distribution { get; } = distribution;
    public WslAgentState State { get; private set; } = WslAgentState.Off;
    public string Detail { get; private set; } = "";
    // ~/.local/bin/brim-notify is the user's own file, which BrimDeck left alone; the agent still works.
    public bool Conflict { get; private set; }
    public event Action<NoticeMessage>? Received;
    public event Action? StateChanged;

    public bool NeedsStart { get { lock (_gate) return !_disposed && _process is null && State != WslAgentState.Installing && State != WslAgentState.Starting && DateTimeOffset.UtcNow >= _retryAt; } }
    public bool IsServer => State == WslAgentState.Server;

    public void MarkNotRunning() { if (State is WslAgentState.Off or WslAgentState.NotRunning or WslAgentState.Failed) SetState(WslAgentState.NotRunning, ""); }

    public async Task StartAsync(CancellationToken token)
    {
        if (!WslNoticeBridge.AgentPackaged) { SetState(WslAgentState.NotPackaged, ""); return; }
        lock (_gate) { if (_disposed || _process is not null) return; }
        try
        {
            if (!_installed)
            {
                SetState(WslAgentState.Installing, "");
                string stamp = WslNoticeBridge.AgentStamp();
                var check = (await WslNoticeBridge.RunWslAsync(["-d", Distribution, "--exec", "/bin/sh", "-c", CheckScript], token) ?? "").Split('\n', StringSplitOptions.TrimEntries);
                string wrapper = check.Length > 1 ? check[1] : "";
                if (check[0] != stamp || wrapper is "missing" or "stale")
                {
                    var result = await WslNoticeBridge.RunWslAsync(["-d", Distribution, "--exec", "/bin/sh", "-c", InstallScript, "sh", WslNoticeBridge.AgentPath, WslNoticeBridge.HelperPath, stamp], token, timeout: TimeSpan.FromSeconds(60));
                    if (result is null) { Retry("install"); return; }
                    wrapper = result.Contains("conflict", StringComparison.Ordinal) ? "conflict" : "ok";
                }
                Conflict = wrapper == "conflict";
                _installed = true;
            }
            SetState(WslAgentState.Starting, "");
            var start = new ProcessStartInfo(WslNoticeBridge.WslExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-d", Distribution, "--cd", "~", "--exec", "/bin/sh", "-c", "exec \"$HOME/.local/share/brimdeck/brimdeck-agent\" serve" }) start.ArgumentList.Add(argument);
            var process = Process.Start(start);
            if (process is null) { Retry("start"); return; }
            lock (_gate)
            {
                if (_disposed) { try { process.Kill(); } catch (InvalidOperationException) { } process.Dispose(); return; }
                _process = process;
            }
            _ = process.StandardError.ReadToEndAsync(token);
            _ = ReadAsync(process);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { Retry(ex.Message); }
        catch (OperationCanceledException) { }
    }

    private async Task ReadAsync(Process process)
    {
        var stream = process.StandardOutput.BaseStream;
        try
        {
            while (await NoticeFrames.ReadMessageAsync(stream) is { } message)
            {
                if (NoticeProtocol.Validate(message) is not null) continue;
                if (message.Type == NoticeTypes.Status)
                {
                    SetState(message.State switch
                    {
                        AgentStates.Server => WslAgentState.Server, AgentStates.Monitor => WslAgentState.Monitor,
                        AgentStates.MonitorDenied => WslAgentState.MonitorDenied, AgentStates.NoBus => WslAgentState.NoBus, _ => WslAgentState.Failed
                    }, message.Detail ?? "");
                    lock (_gate) _backoff = TimeSpan.FromSeconds(5);
                    continue;
                }
                Received?.Invoke(message);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { }
        lock (_gate) { if (_process == process) _process = null; }
        try { process.Dispose(); } catch (InvalidOperationException) { }
        if (!_disposed) Retry("exited");
    }

    public void Send(NoticeMessage message)
    {
        Process? process;
        lock (_gate) process = _process;
        if (process is null) return;
        _ = Task.Run(async () =>
        {
            await _writeLock.WaitAsync();
            try { await NoticeFrames.WriteAsync(process.StandardInput.BaseStream, message); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
            finally { _writeLock.Release(); }
        });
    }

    // A failed start or an agent that ended is tried again after a growing pause, up to two minutes; it also has to wait for
    // the distribution to run, so a distribution shut down meanwhile is not started again.
    private void Retry(string detail)
    {
        // Under the lock NeedsStart reads them with: retries come from the loop and from an agent's reading task.
        lock (_gate)
        {
            _retryAt = DateTimeOffset.UtcNow + _backoff;
            _backoff = TimeSpan.FromSeconds(Math.Min(120, _backoff.TotalSeconds * 2));
        }
        SetState(WslAgentState.Failed, detail);
    }

    private void SetState(WslAgentState state, string detail)
    {
        if (State == state && Detail == detail) return;
        State = state; Detail = detail;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        Process? process;
        lock (_gate) { _disposed = true; process = _process; _process = null; }
        if (process is null) return;
        // Closing standard input asks the agent to end; it gives up the notification service name as it exits.
        try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        try { if (!process.WaitForExit(1500)) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }
}
