using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;
using QRCoder;

namespace BrimDeck;

// Sends files to a linked device through WeType: the files at the top, then the devices to choose from (or a QR code for any
// device), what happens while the device connects, the progress, and how it ended.
internal sealed class WeTypeShareWindow : FileTransferDialog
{
    private readonly ClipboardSyncService _service;
    private readonly WeTypeIdentity _identity;
    private readonly IReadOnlyList<string> _paths;
    private readonly CancellationTokenSource _lifetime;
    private bool _busy;
    private bool _closed;
    private string? _deviceName;

    public WeTypeShareWindow(SettingsTheme theme, ClipboardSyncService service, IReadOnlyList<string> paths, CancellationToken shutdown)
        : base(theme, Loc.T("通过微信输入法发送", "Send with WeType"))
    {
        _service = service;
        _identity = service.Accounts.WeType;
        _paths = paths;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        ShowLoading();
        Loaded += async (_, _) => await LoadDevicesAsync();
        service.Changed += CheckPairing;
        Closed += (_, _) =>
        {
            _closed = true;
            service.Changed -= CheckPairing;
            _lifetime.Cancel();
            if (!_busy) _lifetime.Dispose();
        };
    }

    private void CheckPairing()
    {
        if (!ReferenceEquals(_identity, _service.Accounts.WeType) || !_service.Connected(false)) Close();
    }

    // ---- States ----

    private static string DeviceGlyph(WeTypeFileDevice device) => device.Platform == 6 ? LaptopGlyph : PhoneGlyph;

    // The files as the subject: one file by name, several by count; the badge once the transfer has ended.
    private void ShowSubject(Badge badge = Badge.None)
    {
        long total = _paths.Sum(path => ShelfFiles.FileSize(path) ?? 0);
        string name = _paths.Count == 1 ? Path.GetFileName(_paths[0]) : Loc.T($"{_paths.Count} 个文件", $"{_paths.Count} files");
        Hero(FilePicture(_paths[0], _paths.Count), name, Shelf.Size(total), badge);
    }

    internal void ShowLoading()
    {
        ShowSubject(); Clear();
        Say(Loc.T("正在读取关联的设备…", "Loading linked devices…"), Palette.Tertiary);
    }

    internal void ShowDevices(IReadOnlyList<WeTypeFileDevice> devices)
    {
        ShowSubject(); Clear();
        var tiles = devices.Select(device => (DeviceGlyph(device), device.Name, (Action)(() => { _deviceName = device.Name; Start(device.Id); }))).ToList();
        tiles.Add((QrGlyph, Loc.T("二维码", "QR code"), () => { _deviceName = null; Start(null); }));
        Tiles(devices.Count == 0 ? Loc.T("没有关联设备能接收文件，可以用二维码发送", "No linked device can receive files; send with a QR code")
            : Loc.T("发送到", "Send to"), tiles);
    }

    internal void ShowLoadFailed()
    {
        ShowSubject(); Clear();
        Say(Loc.T("无法读取关联的设备", "Could not load the linked devices"), Palette.Negative, 13);
        Say(Loc.T("请检查网络连接后重试。", "Check the connection and try again."));
        Buttons((Loc.T("重试", "Try again"), true, () => _ = LoadDevicesAsync()));
    }

    internal void ShowConnecting(string? device)
    {
        ShowSubject(); Clear();
        Say(device == null ? Loc.T("正在连接接收设备…", "Connecting to the receiving device…")
            : Loc.T($"正在等待「{device}」接收…", $"Waiting for “{device}” to accept…"));
    }

    internal void ShowQr(string url)
    {
        byte[] png = PngByteQRCodeHelper.GetQRCode(url, QRCodeGenerator.ECCLevel.M, 6);
        using var stream = new MemoryStream(png);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        ShowSubject(); Clear();
        QrCard(bitmap);
        var hint = Say(Loc.T("用接收设备上的微信输入法扫码", "Scan with WeType on the receiving device"));
        hint.Margin = new Thickness(0, 12, 0, 0);
    }

    // A single file is the subject already, so its progress says only where it goes; with several, which one is going.
    internal void ShowSending(WeTypeSendProgress value)
    {
        ShowSubject(); Clear();
        int index = Math.Max(0, _paths.Select(Path.GetFileName).ToList().IndexOf(value.FileName));
        string label = _paths.Count > 1 ? $"{index + 1}/{_paths.Count} · {value.FileName}"
            : _deviceName == null ? Loc.T("正在发送", "Sending") : Loc.T($"发送到 {_deviceName}", $"To {_deviceName}");
        Progress(label, value.Total == 0 ? 1 : (double)value.Sent / value.Total);
    }

    internal void ShowSent()
    {
        ShowSubject(Badge.Done); Clear();
        Say(_deviceName == null ? Loc.T("已发送，对方已收到全部文件", "Sent. The device received all the files")
            : Loc.T($"已发送到 {_deviceName}", $"Sent to {_deviceName}"), Palette.Primary, 13);
    }

    internal void ShowFailed(string text, string detail)
    {
        ShowSubject(Badge.Failed); Clear();
        Say(text, Palette.Primary, 13);
        Say(detail);
        Buttons((Loc.T("重新发送", "Send again"), true, () => _ = LoadDevicesAsync()));
    }

    // ---- Work ----

    private async Task LoadDevicesAsync()
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        _busy = true;
        ShowLoading();
        try
        {
            using var client = new WeTypeSyncClient(_identity, _service.Save);
            var devices = await client.FileDevicesAsync(_lifetime.Token);
            if (!_lifetime.IsCancellationRequested) ShowDevices(devices);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!_closed) ShowLoadFailed(); }
        finally { EndOperation(); }
    }

    private async void Start(ulong? device)
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        _busy = true;
        ShowConnecting(_deviceName);
        var progress = new Progress<WeTypeSendProgress>(value =>
        {
            if (_busy && !_lifetime.IsCancellationRequested) ShowSending(value);
        });
        try
        {
            // A connection kept from an earlier send would compete with this one.
            _service.CloseRelay();
            using var client = new WeTypeSyncClient(_identity, _service.Save) { FileTrace = _service.Log };
            var link = await client.SendFilesAsync(device, _paths, ShowQr, progress, _lifetime.Token);
            if (link != null) _service.KeepRelay(link, _deviceName ?? Loc.T("手机", "Phone"));
            if (!_closed) ShowSent();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (WeTypeRelayUnavailableException)
        {
            ShowFailed(Loc.T("无法连接接收设备", "Could not reach the device"), Loc.T("两台设备无法直连，服务器也没有提供中转。", "The devices cannot connect directly, and no relay was offered."));
        }
        catch (OperationCanceledException)
        {
            ShowFailed(Loc.T("接收设备没有响应", "The device did not answer"), Loc.T("对方没有接收邀请。", "The invitation was not accepted."));
        }
        catch (Exception ex)
        {
            _service.Log("WeType file send failed: " + ex.GetType().Name + ": " + ex.Message);
            if (ex is FileNotFoundException) ShowFailed(Loc.T("找不到所选文件", "A file is missing"), Loc.T("文件可能已被移动或删除。", "It may have been moved or deleted."));
            else ShowFailed(Loc.T("发送未完成", "Not sent"), Loc.T("连接中断了。可以重新发送。", "The connection was lost. You can send again."));
        }
        finally { EndOperation(); }
    }

    private void EndOperation()
    {
        _busy = false;
        if (_closed) _lifetime.Dispose();
    }
}
