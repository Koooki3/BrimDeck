using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;
using BrimDeck.Native;

namespace BrimDeck;

internal sealed class ClipboardSyncAccounts
{
    public string DeviceName { get; set; } = "BrimDeck " + Environment.MachineName;
    public WeTypeIdentity WeType { get; set; } = new();
    public DoubaoIdentity? Doubao { get; set; }
    public LanClipboardPeer? Lan { get; set; }
    public string DoubaoDeviceId { get; set; } = (1_000_000_000_000_000L + RandomNumberGenerator.GetInt32(1_000_000_000) * 1_000_000L + RandomNumberGenerator.GetInt32(1_000_000)).ToString();
}

// How a connection stands, for the colour of its status: not syncing, syncing, or needing the user's attention.
internal enum SyncLevel { Idle, Active, Attention }

internal sealed partial class ClipboardSyncService : IDisposable
{
    private readonly App _app;
    private readonly string _file;
    private ClipboardWatch? _watch;
    private readonly Dictionary<bool, CancellationTokenSource> _workers = [];
    private readonly Dictionary<bool, ClipCopy> _pending = [];
    private readonly Queue<(string Hash, DateTime Time)> _received = [];
    private readonly Dictionary<bool, (string Text, SyncLevel Level)> _status = [];
    // What was last sent to each service, as a hash: its own application on this computer may hand the same thing back.
    private readonly Dictionary<bool, string> _lastSent = [];
    private bool _disposed;
    // Cancelled when this computer's network addresses change: a connection waiting to retry tries again at once.
    private CancellationTokenSource _wake = new();
    public string Folder { get; }
    public ClipboardSyncAccounts Accounts { get; private set; } = new();
    // Whether Doubao lets this device sync, as last read; null before the first reading.
    public DoubaoDeviceState? DoubaoCapability { get; private set; }
    public event Action? Changed;

    public ClipboardSyncService(App app, string folder)
    {
        _app = app; Folder = Path.Combine(folder, "clipboard-sync"); _file = Path.Combine(Folder, "accounts.dat");
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += NetworkChanged;
        try
        {
            if (File.Exists(_file))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_file), null, DataProtectionScope.CurrentUser);
                try { Accounts = JsonSerializer.Deserialize<ClipboardSyncAccounts>(plain) ?? new(); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
        }
        // Unreadable connection details leave every service as not connected. The file is set aside rather than written
        // over by the next save, so that it is not lost to a fault that may pass.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            Log("connection details could not be read: " + Describe(ex));
            try { File.Copy(_file, _file + ".unreadable", true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var plain = JsonSerializer.SerializeToUtf8Bytes(Accounts);
        try
        {
            File.WriteAllBytes(_file + ".tmp", ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            File.Move(_file + ".tmp", _file, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    // The names WeType shows: another device may have renamed this one, or joined or left. The worker reads them itself while it runs.
    public async Task RefreshWeTypeNamesAsync()
    {
        if (!Connected(false) || _workers.ContainsKey(false)) return;
        using var client = new WeTypeSyncClient(Accounts.WeType, Save);
        await client.RefreshNamesAsync(_app.Lifetime.Token);
    }
    public bool CanRename => !Connected(false) && !Connected(true);
    public void RenameDevice(string name)
    {
        name = name.Trim();
        if (!CanRename || name.Length is 0 or > 48 || name.Any(char.IsControl)) throw new ArgumentException("Invalid device name.");
        Accounts.DeviceName = name; Accounts.WeType.DeviceName = name;
        Save(); Changed?.Invoke();
    }
    // A laptop back on a network, or awake again: the wait between retries, up to a minute by then, is cut short.
    private void NetworkChanged(object? sender, EventArgs e) => _app.Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        var waiting = _wake;
        _wake = new();
        waiting.Cancel();
    });
    public bool Connected(bool doubao) => doubao ? Accounts.Doubao is not null : Accounts.WeType.Paired;
    public string Status(bool doubao) => _status.TryGetValue(doubao, out var status) ? status.Text : "";
    public SyncLevel Level(bool doubao) => _status.TryGetValue(doubao, out var status) ? status.Level : SyncLevel.Idle;
    private void Report(bool doubao, string message, SyncLevel level)
    {
        if (_disposed) return;
        if (Status(doubao) != message) Log((doubao ? "doubao" : "wetype") + " status [" + level + "] " + message);
        _status[doubao] = (message, level);
        Changed?.Invoke();
    }
    // What happened and when, without any copied content: sizes, names of devices and applications, and the services' error
    // codes. Kept in the sync folder to tell afterwards why something was not synced; the older half goes at 256 KB.
    internal void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, "events.log");
            if (new FileInfo(file) is { Exists: true, Length: > 256 * 1024 })
            {
                var lines = File.ReadAllLines(file);
                File.WriteAllLines(file, lines[(lines.Length / 2)..]);
            }
            File.AppendAllText(file, DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + " " + line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message + (ex.InnerException is { } inner ? " <- " + inner.GetType().Name + ": " + inner.Message : "");
    public async Task PairWeTypeAsync(string code, CancellationToken ct)
    {
        if (_workers.ContainsKey(false)) throw new InvalidOperationException("Disconnect before pairing again.");
        Accounts.WeType.DeviceName = Accounts.DeviceName;
        using var client = new WeTypeSyncClient(Accounts.WeType, Save) { Trace = Log };
        await client.PairAsync(code, ct);
        ApplyFileOffers();
        Changed?.Invoke();
    }
    // Signing in and being allowed to sync are separate: the account's devices each have a switch of their own.
    public async Task ConnectDoubaoAsync(DoubaoIdentity identity, CancellationToken ct)
    {
        using var client = new DoubaoSyncClient(identity);
        client.Trace = Log;
        // As the official client does on signing in; after an earlier sign-out it lists this device again.
        try { await client.ReportSignInAsync(true, ct); }
        catch (Exception ex) when (ex is DoubaoRequestException or InvalidDataException or System.Net.Http.HttpRequestException) { Log("doubao sign-in notice failed: " + Describe(ex)); }
        var capability = await client.ReadCurrentDeviceAsync(ct);
        Log("doubao signed in: sync_enabled=" + capability.SyncState + " is_support_copy=" + capability.SupportsCopy);
        // Signing in here is asking to sync: this device's switch, off for a new device, is turned on once. Turned off
        // later on another device, it stays off.
        if (!capability.CanSync && capability.SupportsCopy)
        {
            try
            {
                capability = await client.EnableAsync(ct);
                Log("doubao switch on: sync_enabled=" + capability.SyncState);
            }
            catch (Exception ex) when (ex is DoubaoRequestException or InvalidDataException or System.Net.Http.HttpRequestException) { Log("doubao switch on failed: " + Describe(ex)); }
        }
        ct.ThrowIfCancellationRequested();
        Accounts.Doubao = identity; Save();
        DoubaoCapability = capability;
        Changed?.Invoke();
    }
    // The account's other devices, for taking one off its list: Doubao's own applications have no way to, and a device whose
    // sign-in was lost (BrimDeck before a reinstall) cannot sign out itself. They are not told apart by name, which the user
    // chooses: all are listed, and the user picks.
    public async Task<IReadOnlyList<DoubaoDevice>> OtherDoubaoDevicesAsync(CancellationToken ct)
    {
        if (Accounts.Doubao is not { } identity) return [];
        using var client = new DoubaoSyncClient(identity) { Trace = Log };
        return [.. (await client.ListDevicesAsync(ct)).Where(device => !device.Current && device.Id != identity.DeviceId)];
    }
    // True once the account no longer lists the device.
    public async Task<bool> RemoveDoubaoDeviceAsync(DoubaoDevice device, CancellationToken ct)
    {
        if (Accounts.Doubao is not { } identity) return false;
        using var client = new DoubaoSyncClient(identity) { Trace = Log };
        try { await client.RemoveDeviceAsync(device, ct); }
        catch (Exception ex) when (ex is DoubaoRequestException or InvalidDataException or System.Net.Http.HttpRequestException)
        { Log("doubao device not removed: \"" + device.Name + "\" " + Describe(ex)); return false; }
        bool gone = (await client.ListDevicesAsync(ct)).All(listed => listed.Id != device.Id);
        Log("doubao device \"" + device.Name + "\" " + (gone ? "removed" : "still listed after the removal"));
        return gone;
    }
    // Signs out of Doubao. The service is told first, as the official client tells it when its account is cleared, so that
    // the account's other devices stop listing this one. The sign-in kept here goes whether or not that reached the service.
    public async Task SignOutDoubaoAsync()
    {
        if (Accounts.Doubao is { } identity)
        {
            Stop(true);
            try
            {
                using var client = new DoubaoSyncClient(identity) { Trace = Log };
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.ReportSignInAsync(false, limit.Token);
                // Nothing is asked of the service after this: asking for the account's devices names this one again.
                Log("doubao told of the sign-out");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Log("doubao sign-out notice failed: " + Describe(ex)); }
        }
        Disconnect(true);
    }
    // Asks WeType to take this PC off the other devices' lists; false when that did not succeed. The link itself stays until
    // Disconnect.
    public async Task<bool> LeaveWeTypeGroupAsync()
    {
        if (!Accounts.WeType.Paired) return true;
        try
        {
            using var client = new WeTypeSyncClient(Accounts.WeType, Save);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.LeaveGroupAsync(limit.Token);
            Log("wetype left the group");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log("wetype leaving the group failed: " + Describe(ex)); return false; }
    }
    public void Disconnect(bool doubao)
    {
        Stop(doubao);
        if (doubao)
        {
            Accounts.Doubao = null;
            DoubaoCapability = null;
        }
        else Accounts.WeType = new() { DeviceName = Accounts.DeviceName };
        ApplyFileOffers();
        _status.Remove(doubao);
        Save(); Changed?.Invoke();
    }
    public void Apply()
    {
        if (_disposed) return;
        ApplyLan();
        foreach (bool doubao in new[] { false, true })
        {
            bool enabled = doubao ? _app.Settings.DoubaoClipboardSync : _app.Settings.WeTypeClipboardSync;
            if (enabled && Connected(doubao) && !_workers.ContainsKey(doubao))
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
                _workers[doubao] = stop; _ = RunAsync(doubao, stop);
            }
            else if ((!enabled || !Connected(doubao)) && _workers.ContainsKey(doubao)) Stop(doubao);
        }
        ApplyFileOffers();
        if ((_workers.Count > 0 || _lanStop is not null) && _watch is null)
        {
            try { _watch = new ClipboardWatch(Copied, name => ClipboardExclusions.Excludes(_app.Settings.ClipboardExcluded, name)); }
            catch (System.ComponentModel.Win32Exception)
            {
                foreach (bool doubao in _workers.Keys) Report(doubao, Loc.T("无法读取剪贴板", "Cannot read the clipboard"), SyncLevel.Attention);
            }
        }
        else ReleaseIdleWatcher();
    }
    private void ReleaseIdleWatcher()
    {
        if (_workers.Count != 0 || _lanStop is not null) return;
        _watch?.Dispose();
        _watch = null;
    }
    private void Stop(bool doubao)
    {
        if (_workers.Remove(doubao, out var source)) source.Cancel();
        _pending.Remove(doubao);
        _status.Remove(doubao);
        _lastSent.Remove(doubao);
    }
    // Which services a copy goes to. Not to the one whose own application put it on the clipboard: an input method installed
    // here and syncing by itself writes there what it received, and sends by itself what is copied here. And not what was
    // last sent to a service, which its application would hand back in the same way. Either would go round without end.
    private void Queue(ClipCopy copy, string hash)
    {
        foreach (bool doubao in _workers.Keys)
        {
            bool own = copy.SourcePath.Contains(doubao ? "\\DoubaoIME\\" : "\\WeType\\", StringComparison.OrdinalIgnoreCase);
            if (own || _lastSent.TryGetValue(doubao, out var sent) && sent == hash)
            {
                Log((doubao ? "doubao" : "wetype") + " not sent: " + (own ? "written by its own application" : "the same as last sent"));
                continue;
            }
            _pending[doubao] = copy;
        }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private void Copied(ClipCopy copy)
    {
        while (_received.TryPeek(out var item) && item.Time < DateTime.UtcNow.AddMinutes(-2)) _received.Dequeue();
        Log("copy " + (copy.Png is { } picture ? "image " + copy.PixelWidth + "x" + copy.PixelHeight + " " + picture.Length + " bytes" : "text " + copy.Text.Length + " chars")
            + " from \"" + copy.Source + "\" -> " + string.Join(",", _workers.Keys.Select(doubao => doubao ? "doubao" : "wetype").Append(_lanOutgoing is null ? "" : "lan").Where(name => name.Length > 0)));
        if (copy.Png is { } png)
        {
            string hash = HashImage(png);
            if (_received.Any(item => item.Hash == hash)) return;
            Queue(copy, hash);
            if (_lanImages && _lanOutgoing is { } images && LanPng.Fits(copy.PixelWidth, copy.PixelHeight, png.Length)) images.Writer.TryWrite(copy);
            return;
        }
        if (copy.Text.Length == 0 || LanClipboardLink.ContainsInvitation(copy.Text)) return;
        string textHash = Hash(copy.Text);
        if (_received.Any(item => item.Hash == textHash)) return;
        if (Encoding.UTF8.GetByteCount(copy.Text) > SyncTransport.MaxTextBytes) return;
        Queue(copy, textHash);
        _lanOutgoing?.Writer.TryWrite(copy);
    }
    private void AcceptText(string text, string source, string html = "")
    {
        _app.Shelf.AcceptRemoteText(text, source, html.Length > 0 ? ClipboardHtml.Wrap(html) : "");
        _watch?.IgnoreCurrent();
        RememberReceived(Hash(text));
    }
    private bool AcceptImage(LanPng png, BitmapSource image, string source)
    {
        if (!_app.Shelf.AcceptRemoteImage(png, image, source)) return false;
        _watch?.IgnoreCurrent();
        RememberReceived(HashImage(png.Bytes));
        return true;
    }
    private void RememberReceived(string hash)
    {
        _received.Enqueue((hash, DateTime.UtcNow));
        while (_received.Count > 64) _received.Dequeue();
    }
    private static string HashImage(byte[] png) => "PNG:" + Convert.ToHexString(SHA256.HashData(png));
    private static string At(string what) => what + " · " + DateTime.Now.ToString("HH:mm:ss");

    private async Task RunAsync(bool doubao, CancellationTokenSource stop)
    {
        using IClipboardSyncClient client = doubao ? new DoubaoSyncClient(Accounts.Doubao!) { Trace = Log } :
            new WeTypeSyncClient(Accounts.WeType, Save) { Trace = Log, FileInviteChanged = ObserveFileOffer };
        string source = doubao ? Loc.T("豆包输入法", "Doubao IME") : Loc.T("微信输入法", "WeType");
        bool initialized = false, sending = false; string? lastId = null, lastSignature = null; int failures = 0;
        static string Signature(RemoteClip clip) => clip.Image is { } bytes ? HashImage(bytes) : Hash(clip.Text);
        Report(doubao, Loc.T("正在连接…", "Connecting…"), SyncLevel.Idle);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int wait = 3;
                try
                {
                    if (!initialized)
                    {
                        if (client is DoubaoSyncClient account)
                        {
                            var capability = await account.ReadCurrentDeviceAsync(stop.Token);
                            stop.Token.ThrowIfCancellationRequested();
                            if (DoubaoCapability != capability) Log("doubao device: sync_enabled=" + capability.SyncState + " is_support_copy=" + capability.SupportsCopy);
                            DoubaoCapability = capability;
                            if (!capability.CanSync) throw new DoubaoDeviceUnavailableException(capability.SyncState, capability.SupportsCopy);
                        }
                        else await client.ConnectAsync(stop.Token);
                        // What was on the service before connecting is not pasted here: only later copies are.
                        lastId = await client.PeekAsync(stop.Token);
                        stop.Token.ThrowIfCancellationRequested();
                        initialized = true; failures = 0;
                        Report(doubao, Loc.T("已连接", "Connected"), SyncLevel.Active);
                    }
                    if (_pending.Remove(doubao, out var copy))
                    {
                        sending = true;
                        await SendAsync(client, doubao, copy, stop.Token);
                        sending = false;
                    }
                    uint sequence = ClipboardWatch.GetClipboardSequenceNumber();
                    var remote = await client.ReceiveAsync(lastId, stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    // The same thing again from the same service is already on the clipboard.
                    if (remote is not null && Signature(remote) is var signature && signature == lastSignature) { lastId = remote.Id; remote = null; }
                    else if (remote is not null) lastSignature = Signature(remote);
                    if (remote is not null)
                    {
                        Log((doubao ? "doubao" : "wetype") + " received " + (remote.Image is { } bytes ? "image " + bytes.Length + " bytes" : "text " + remote.Text.Length + " chars") + " id=" + remote.Id);
                        lastId = remote.Id;
                        // Another device's copy is now the latest there: what was sent before may be sent again.
                        _lastSent.Remove(doubao);
                        await AcceptAsync(remote, source, doubao, sequence, stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (DoubaoDeviceUnavailableException ex)
                {
                    // The switch is on the account: once another device turns this one on, syncing starts by itself.
                    DoubaoCapability = new(ex.SyncState, ex.SupportsCopy); initialized = false; wait = 15;
                    Report(true, Loc.T("此设备的互传开关未开启", "Sync is turned off for this device"), SyncLevel.Attention);
                }
                catch (WeTypeUnpairedException)
                {
                    initialized = false; wait = 30;
                    if (Accounts.WeType.PeerNames.Count > 0)
                    {
                        Accounts.WeType.PeerNames = [];
                        try { Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
                    }
                    Report(false, Loc.T("已被其他设备解除关联", "Removed by another device"), SyncLevel.Attention);
                }
                catch (DoubaoRequestException ex)
                {
                    initialized = false; wait = 30;
                    Report(true, Loc.T("豆包拒绝了请求（错误码 ", "Doubao refused the request (code ") + ex.Code + Loc.T("）", ")"), SyncLevel.Attention);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log((doubao ? "doubao" : "wetype") + (sending ? " send failed: " : " failed: ") + Describe(ex));
                    failures = Math.Min(failures + 1, 5); initialized = false; wait = Math.Min(60, 3 * (1 << failures));
                    Report(doubao, sending ? Loc.T("发送失败，请重新复制", "Not sent. Copy again to retry")
                        : Loc.T("连接中断，正在重试…", "Connection lost. Retrying…"), SyncLevel.Attention);
                }
                sending = false;
                using var pause = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, _wake.Token);
                try { await Task.Delay(TimeSpan.FromSeconds(wait), pause.Token); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    // The addresses change several times as a network comes up; the retry follows the last of them.
                    failures = 0;
                    await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_workers.TryGetValue(doubao, out var current) && ReferenceEquals(current, stop))
            {
                _workers.Remove(doubao);
                _pending.Remove(doubao);
                ReleaseIdleWatcher();
            }
            stop.Dispose();
        }
    }

    private async Task SendAsync(IClipboardSyncClient client, bool doubao, ClipCopy copy, CancellationToken ct)
    {
        if (copy.Png is not { } png)
        {
            // Doubao carries the formatting too; a formatted copy it refuses goes as plain text.
            bool rich = false;
            if (client is DoubaoSyncClient account && copy.Html.Length > 0 && ClipboardHtml.Fragment(copy.Html) is { Length: > 0 and <= DoubaoSyncClient.MaxHtmlChars } fragment)
            {
                try { await account.SendRichAsync(copy.Text, fragment, ct); rich = true; }
                catch (Exception ex) when (ex is DoubaoRequestException or InvalidDataException) { Log("doubao formatted text refused: " + Describe(ex)); }
            }
            if (!rich) await client.SendAsync(copy.Text, ct);
            _lastSent[doubao] = Hash(copy.Text);
            Report(doubao, At(Loc.T("已发送文字", "Text sent")), SyncLevel.Active);
            return;
        }
        if (!client.SupportsImages) return;
        try
        {
            int limit = doubao ? DoubaoSyncClient.MaxImageBytes : SyncTransport.MaxImageBytes;
            var picture = await Task.Run(() => ShelfService.EncodeForSync(png, limit), ct) ?? throw new SyncImageTooLargeException();
            await client.SendImageAsync(picture, copy.PixelWidth, copy.PixelHeight, ct);
            _lastSent[doubao] = HashImage(png);
            Report(doubao, At(Loc.T("已发送图片", "Image sent")), SyncLevel.Active);
        }
        catch (SyncImageTooLargeException ex)
        {
            // The service's own threshold, when it named one, tells which pictures would go through.
            string size = ex.Limit > 0 ? Loc.T($"（上限 {ex.Limit / 1024} KB）", $" (limit {ex.Limit / 1024} KB)") : "";
            Report(doubao, Loc.T("图片过大，未发送", "Image too large to send") + size, SyncLevel.Active);
        }
    }

    private async Task AcceptAsync(RemoteClip remote, string source, bool doubao, uint sequence, CancellationToken ct)
    {
        try
        {
            if (remote.Image is { } encoded)
            {
                var (png, image) = await Task.Run(() => ShelfService.DecodeRemotePicture(encoded), ct);
                ct.ThrowIfCancellationRequested();
                // A copy made here while the picture was on its way stays on the clipboard.
                if (sequence != ClipboardWatch.GetClipboardSequenceNumber() || _pending.ContainsKey(doubao) || !AcceptImage(png, image, source)) return;
                Report(doubao, At(Loc.T("已接收图片", "Image received")), SyncLevel.Active);
            }
            else
            {
                if (sequence != ClipboardWatch.GetClipboardSequenceNumber() || _pending.ContainsKey(doubao)) return;
                AcceptText(remote.Text, source, remote.Html);
                Report(doubao, At(Loc.T("已接收文字", "Text received")), SyncLevel.Active);
            }
        }
        // The clipboard is held by another application, or the picture cannot be read: this one copy is skipped.
        catch (Exception ex) when (ex is ExternalException or IOException or InvalidDataException or FormatException or NotSupportedException
            or ArgumentException or InvalidOperationException or OverflowException) { Log((doubao ? "doubao" : "wetype") + " not accepted: " + Describe(ex)); }
    }

    public void Dispose()
    {
        _disposed = true;
        CloseRelay();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkChanged;
        StopLan();
        _fileOffersStop?.Cancel();
        foreach (var source in _workers.Values) source.Cancel();
        _workers.Clear(); _pending.Clear(); _watch?.Dispose(); _watch = null;
    }
}
