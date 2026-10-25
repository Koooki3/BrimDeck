using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BrimDeck.Native;

// Audio is a visualization input, never a discoverable media source. Every COM
// object is created, read, and released on this single worker thread.
// The level bars prefer the player's captured audio; when process capture is not
// available they fall back to the session peak meter.
internal sealed class AudioLevelMeter : IDisposable
{
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private readonly List<AudioSessionControl> _sessions = [];
    private readonly List<MMDevice> _devices = [];
    private Thread? _thread;
    private string? _requested;
    private int _revision;
    private bool _disposed;
    private float _peak;
    private volatile ProcessLoopbackCapture? _capture;
    public float Peak => Volatile.Read(ref _peak);
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public void SetSource(string? processName, bool enabled)
    {
        string? target = enabled ? processName : null;
        lock (_gate)
        {
            if (_disposed || _requested == target) return;
            _requested = target; _revision++;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "BrimDeck audio level" };
                _thread.SetApartmentState(ApartmentState.MTA); _thread.Start();
            }
            _wake.Set();
        }
    }
    // Fills the flowing bar values when captured audio drives them.
    public bool TryFill(Span<double> levels)
    {
        if (_capture is not { } capture) return false;
        capture.Signal.Fill(levels, Now);
        return true;
    }
    private void Run()
    {
        int revision = -1;
        string? target = null;
        bool retried = false;
        double nextPeak = 0;
        // A player may create its audio session after it reports playback; look again with growing pauses.
        int searchDelay = 1000;
        WaitHandle[]? waits = null;
        try
        {
            while (true)
            {
                bool changed;
                lock (_gate)
                {
                    if (_disposed) break;
                    changed = revision != _revision;
                    if (changed) { revision = _revision; target = _requested; }
                }
                // Starting a capture waits for the system; do it outside the lock SetSource takes on the UI thread.
                if (changed) { Clear(); if (target is not null) Enumerate(target); retried = false; searchDelay = 1000; }
                var capture = _capture;
                double now = Now;
                if (capture is not null && !capture.Drain(now)) { StopCapture(); capture = null; }
                // Captured audio drives the bars whenever a capture runs, so session peaks are only read without one.
                if (capture is null && now >= nextPeak)
                {
                    nextPeak = now + .033;
                    float peak = 0;
                    try { foreach (var session in _sessions) peak = Math.Max(peak, session.AudioMeterInformation.MasterPeakValue); }
                    catch
                    {
                        Clear();
                        if (!retried && target is not null) { Enumerate(target); retried = true; }
                    }
                    Volatile.Write(ref _peak, peak);
                }
                if (capture is not null)
                {
                    if (waits?[1] != capture.Ready) waits = [_wake, capture.Ready];
                    WaitHandle.WaitAny(waits, 20);
                }
                else if (_sessions.Count > 0) _wake.WaitOne(33);
                else if (target is null) _wake.WaitOne();
                else if (!_wake.WaitOne(searchDelay))
                {
                    searchDelay = Math.Min(searchDelay * 2, 30_000);
                    Clear(); Enumerate(target);
                }
            }
        }
        finally { Clear(); Volatile.Write(ref _peak, 0); _wake.Dispose(); }
    }
    private void Enumerate(string target)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int d = 0; d < devices.Count; d++)
            {
                var device = devices[d]; _devices.Add(device);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i]; bool keep = false;
                    try
                    {
                        using var process = Process.GetProcessById((int)session.GetProcessID);
                        if (string.Equals(process.ProcessName, target, StringComparison.OrdinalIgnoreCase)) { _sessions.Add(session); keep = true; }
                    }
                    catch { }
                    finally { if (!keep) session.Dispose(); }
                }
            }
        }
        catch { Clear(); return; }
        // Capture the process that owns the playing session; its child processes are included.
        var playing = _sessions.FirstOrDefault(s => s.State == AudioSessionState.AudioSessionStateActive) ?? _sessions.FirstOrDefault();
        if (playing is not null) _capture = ProcessLoopbackCapture.TryStart(playing.GetProcessID);
    }
    private void StopCapture()
    {
        var capture = _capture; _capture = null;
        capture?.Dispose();
    }
    private void Clear()
    {
        StopCapture();
        foreach (var session in _sessions) { try { session.Dispose(); } catch { } }
        _sessions.Clear();
        foreach (var device in _devices) { try { device.AudioSessionManager.Dispose(); device.Dispose(); } catch { } }
        _devices.Clear();
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; if (_thread is null) _wake.Dispose(); else _wake.Set(); }
    }
}
