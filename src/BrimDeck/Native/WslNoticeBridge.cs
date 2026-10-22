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

    public event Action<string, NoticeMessage>? Received;
    public event Action? StateChanged;

    public static string AgentPath => Path.Combine(AppContext.BaseDirectory, "wsl", "brimdeck-agent");
    // By its full path: a bare name is also looked for in BrimDeck's folder and the current directory before System32.
    public static string WslExe => Path.Combine(Environment.SystemDirectory, "wsl.exe");
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "BrimDeck.Notify.exe");
    public static bool AgentPackaged => File.Exists(AgentPath);

    public IReadOnlyDictionary<string, (WslAgentState State, string Detail)> States
    {
        get { lock (_agents) return _agents.ToDictionary(pair => pair.Key, pair => (pair.Value.State, pair.Value.Detail), StringComparer.OrdinalIgnoreCase); }
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
                var running = await RunningDistributionsAsync(_stop.Token);
                foreach (var agent in idle)
                {
                    if (running.Contains(agent.Distribution)) _ = agent.StartAsync(_stop.Token);
                    else agent.MarkNotRunning();
                }
            }
            try { await _wake.WaitAsync(RunningCheck, _stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    // The distributions running now. WSL 2 runs them all in one virtual machine process; without it nothing runs and wsl.exe
    // is not asked at all.
    public static async Task<HashSet<string>> RunningDistributionsAsync(CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Process.GetProcessesByName("vmmemWSL").Concat(Process.GetProcessesByName("vmmem")).Any(DisposeAndTrue)) return names;
        var output = await RunWslAsync(["--list", "--running", "--quiet"], token, Encoding.Unicode);
        if (output is null) return names;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.Trim('\0').Trim() is { Length: > 0 } name) names.Add(name);
        return names;
    }

    private static bool DisposeAndTrue(Process process) { process.Dispose(); return true; }

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
    // Copies the agent from Windows (path as $1, converted by wslpath), records how brim-notify reaches Windows without the
    // agent ($2), adds ~/.local/bin/brim-notify unless the user has a different file there, and writes the stamp ($3) last.
    // sh needs plain line feeds; a raw string takes the source file's line endings, which Git on Windows checks out as CRLF.
    private static readonly string InstallScript = """
        set -e
        dir="$HOME/.local/share/brimdeck"
        mkdir -p "$dir"
        src=$(wslpath -u "$1")
        cp "$src" "$dir/brimdeck-agent.new"
        chmod 755 "$dir/brimdeck-agent.new"
        mv -f "$dir/brimdeck-agent.new" "$dir/brimdeck-agent"
        helper=$(wslpath -u "$2" 2>/dev/null || true)
        printf '{"helper":"%s","version":"%s"}\n' "$helper" "$3" > "$dir/bridge.json"
        mkdir -p "$HOME/.local/bin"
        b="$HOME/.local/bin/brim-notify"
        if [ ! -e "$b" ] || grep -q "brimdeck-agent" "$b"; then
          printf '#!/bin/sh\n# Installed by BrimDeck: shows a notice in BrimDeck on Windows.\nexec "$HOME/.local/share/brimdeck/brimdeck-agent" notify "$@"\n' > "$b"
          chmod 755 "$b"
        fi
        printf '%s' "$3" > "$dir/agent.version"
        """.ReplaceLineEndings("\n");
    private const string StampScript = "cat \"$HOME/.local/share/brimdeck/agent.version\" 2>/dev/null || true";
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private bool _installed, _disposed;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private TimeSpan _backoff = TimeSpan.FromSeconds(5);

    public string Distribution { get; } = distribution;
    public WslAgentState State { get; private set; } = WslAgentState.Off;
    public string Detail { get; private set; } = "";
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
                var current = await WslNoticeBridge.RunWslAsync(["-d", Distribution, "--exec", "/bin/sh", "-c", StampScript], token);
                if (current?.Trim() != stamp)
                {
                    var result = await WslNoticeBridge.RunWslAsync(["-d", Distribution, "--exec", "/bin/sh", "-c", InstallScript, "sh", WslNoticeBridge.AgentPath, WslNoticeBridge.HelperPath, stamp], token, timeout: TimeSpan.FromSeconds(60));
                    if (result is null) { Retry("install"); return; }
                }
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
                    _backoff = TimeSpan.FromSeconds(5);
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
        _retryAt = DateTimeOffset.UtcNow + _backoff;
        _backoff = TimeSpan.FromSeconds(Math.Min(120, _backoff.TotalSeconds * 2));
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
