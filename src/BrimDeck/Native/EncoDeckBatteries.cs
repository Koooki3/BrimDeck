using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The earbuds or headphones EncoDeck reads (EncoDeckBattery), asked over its pipe. Few people run EncoDeck, so nothing runs for
// it in the background: while it has not answered, the pipe is tried once at the moments the other sources read again anyway
// (the reading starts, the page opens, the computer wakes, the user comes back) and when a Bluetooth device connects, and a pipe
// that does not exist fails at once (one CreateFile, no waiting). Once EncoDeck answers, it is asked every 5 seconds, as Windows'
// own Bluetooth levels are (DevicePolling.Cached), until it stops answering.
internal sealed class EncoDeckBatteries(Dispatcher dispatcher, Action<DeviceSource, IReadOnlyList<DeviceBattery>> report) : IDeviceSource
{
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(2);
    private DispatcherTimer? _poll;
    private DeviceDemand _demand;
    private bool _found, _reading, _disposed;
    private DeviceBattery? _last;
    private HashSet<string> _bluetooth = new(StringComparer.Ordinal);

    public bool Listed { get; private set; }

    public void Start(DeviceDemand demand)
    {
        if (_disposed) return;
        _demand = demand;
        if (demand == DeviceDemand.Watch) Try(); else Stop();
    }

    public void Stop()
    {
        _demand = DeviceDemand.None;
        _poll?.Stop();
        _found = false; Listed = false; _bluetooth.Clear();
        Publish(null);
    }

    public void Refresh() => Try();
    public void ReadNow() => Try();

    // The Bluetooth source's list: a device that connects (EncoDeck's headset among them) is the moment to look for EncoDeck.
    public void Consider(IReadOnlyList<DeviceBattery> bluetooth)
    {
        var keys = bluetooth.Select(device => device.Key).ToHashSet(StringComparer.Ordinal);
        bool connected = keys.Any(key => !_bluetooth.Contains(key));
        _bluetooth = keys;
        if (connected && !_found) Try();
    }

    private async void Try()
    {
        if (_disposed || _demand != DeviceDemand.Watch || _reading) return;
        _reading = true;
        string? answer;
        try { answer = await Task.Run(ReadAsync); }
        catch { answer = null; }
        finally { _reading = false; }
        if (_disposed || _demand != DeviceDemand.Watch) return;
        Listed = true;
        _found = answer is not null;
        if (_found)
        {
            if (_poll is null) { _poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = DevicePolling.Cached }; _poll.Tick += (_, _) => Try(); }
            _poll.Start();
        }
        else _poll?.Stop();
        // An empty answer (see ReadAsync) keeps the reading there is until the next.
        if (answer is not "") Publish(answer is null ? null : EncoDeckBattery.Parse(answer));
    }

    private void Publish(DeviceBattery? device)
    {
        if (device is null ? _last is null : _last is not null && device.Same(_last)) return;
        _last = device;
        report(DeviceSource.EncoDeck, device is null ? [] : [device]);
    }

    // EncoDeck's answer; null when it is not running or does not answer, empty when it took the request and its answer was lost.
    // EncoDeck lets the client go as soon as it has written, and what the client had not read by then is dropped (about one ask
    // in ten when asked back to back), so an empty answer is asked once more.
    private static async Task<string?> ReadAsync()
    {
        var answer = await ReadOnceAsync();
        if (answer is not "") return answer;
        // EncoDeck opens its pipe again only after it has let the last client go.
        await Task.Delay(50);
        return await ReadOnceAsync() ?? "";
    }

    // One ask. A connect timeout of 0 makes one attempt: with no pipe it fails at once, and a pipe busy with another client
    // gets the pipe's own short default wait. CurrentUserOnly takes only a pipe of this user's (another user's program cannot
    // stand in for EncoDeck), and Identification does not let the other end act as BrimDeck.
    private static async Task<string?> ReadOnceAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", EncoDeckBattery.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            using var timeout = new CancellationTokenSource(Answer);
            await pipe.ConnectAsync(0, timeout.Token);
            await pipe.WriteAsync(EncoDeckBattery.Request, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var content = new MemoryStream();
            var chunk = new byte[4096];
            // EncoDeck ends its answer with a line break and then lets the client go; a pipe it has already let go of may fail
            // the next read instead of ending it, which leaves what was read whole.
            while (content.Length < EncoDeckBattery.MaximumLength)
            {
                int read;
                try { read = await pipe.ReadAsync(chunk, timeout.Token); }
                catch (IOException) when (content.Length > 0) { break; }
                if (read == 0) break;
                content.Write(chunk, 0, read);
                if (Array.IndexOf(chunk, (byte)'\n', 0, read) >= 0) break;
            }
            return System.Text.Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll?.Stop();
    }
}
