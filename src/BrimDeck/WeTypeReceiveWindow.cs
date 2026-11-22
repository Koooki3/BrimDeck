using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;
using Microsoft.Win32;

namespace BrimDeck;

// Receives files a linked device sends through WeType: who wants to send, accept (choosing the folder) or decline, the file
// coming in with its progress, and what was saved. The phone either invites this device, or sends through the connection kept
// after this device sent to it.
internal sealed class WeTypeReceiveWindow : FileTransferDialog
{
    private readonly App _app;
    private readonly ClipboardSyncService _service;
    private readonly WeTypeIdentity _identity;
    private WeTypeFileInvite? _invite;
    // Files the phone sends through the connection kept after this device sent to it; there is no invitation.
    private WeTypeRelayLink? _link;
    private string? _linkSender;
    private string? SenderName => _invite?.Name ?? _linkSender;
    private readonly CancellationTokenSource _lifetime;
    private readonly TaskCompletionSource<string> _destination = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _attempt;
    private readonly List<WeTypeFileInvite> _retired = [];
    private readonly List<string> _saved = [];
    private volatile bool _fileOffered;
    private bool _connected, _closed, _finished, _declining;
    private string? _incoming;
    // How many files the phone has announced (less any it canceled); the window shows what was saved only once they all are.
    private int _expected;

    public WeTypeReceiveWindow(App app, WeTypeIdentity identity, ClipboardSyncService service, CancellationToken stop)
        : base(app.Settings.Theme, Loc.T("接收文件", "Receive files"))
    {
        _app = app; _service = service; _identity = identity;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        ShowWaiting();
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _destination.TrySetCanceled();
            _lifetime.Dispose();
        };
    }

    internal bool Finished => _finished;
    internal bool CanContinue(WeTypeFileInvite next) => !_closed && !_declining && !_fileOffered &&
        _invite?.Sender == next.Sender && !_retired.Any(next.SameOffer) && !next.SameOffer(_invite);

    // ---- States ----

    internal void ShowWaiting()
    {
        var name = string.IsNullOrWhiteSpace(_identity.DisplayName) ? _identity.DeviceName : _identity.DisplayName;
        Hero(Avatar(DownloadGlyph), Loc.T("等待手机发送", "Waiting for the phone"), null);
        Clear();
        Say(Loc.T("在手机的微信输入法中发送文件时，选择这台电脑：", "When sending files from WeType on the phone, choose this PC:"));
        var device = Say(name, Palette.Primary, 13); device.FontWeight = FontWeights.SemiBold;
    }

    // Who wants to send, with Decline and Accept (which asks for the folder); once a folder is chosen, the connection.
    internal void ShowOffer()
    {
        if (_finished || _closed || _declining || SenderName is not { } sender) return;
        Clear();
        if (!_destination.Task.IsCompletedSuccessfully)
        {
            Hero(Avatar(PhoneGlyph), sender, Loc.T("想发送文件给你", "wants to send you files"));
            Buttons((_link != null ? Loc.T("忽略", "Ignore") : Loc.T("拒绝", "Decline"), false, Decline),
                (Loc.T("接收…", "Accept…"), true, ChooseFolder));
            return;
        }
        Hero(Avatar(PhoneGlyph), sender, _connected ? Loc.T("已连接，等待发送…", "Connected. Waiting for files…") : Loc.T("正在连接…", "Connecting…"));
        Where(_destination.Task.Result);
    }

    // The file coming in is the subject; several files are one subject, with how many arrived and the progress of them all.
    // The progress under it says where they come from.
    internal void ShowReceiving(WeTypeSendProgress value)
    {
        if (_finished || _closed || !_destination.Task.IsCompletedSuccessfully) return;
        string folder = _destination.Task.Result;
        string picture = Path.Combine(folder, value.FileName);
        string key = value.Count > 1 ? $"{value.Count}|{value.Index}|{value.Total}" : $"{value.FileName}|{value.Total}";
        if (_incoming != key)
        {
            _incoming = key;
            if (value.Count > 1)
                Hero(FilePicture(picture, value.Count), Loc.T($"{value.Count} 个文件", $"{value.Count} files"),
                    Loc.T($"已收到 {value.Index - 1} 个 · 共 {Shelf.Size(value.Total)}", $"{value.Index - 1} received · {Shelf.Size(value.Total)}"));
            else
                Hero(FilePicture(picture), value.FileName, Shelf.Size(value.Total));
        }
        Clear();
        Progress(SenderName is { } sender ? Loc.T($"来自 {sender}", $"From {sender}") : Loc.T("正在接收", "Receiving"),
            value.Total == 0 ? 1 : (double)value.Sent / value.Total);
        Where(folder);
    }

    internal void ShowSaved(IReadOnlyList<string> paths)
    {
        _incoming = null;
        string last = paths[^1];
        if (paths.Count == 1)
            Hero(FilePicture(last), Path.GetFileName(last), Loc.T("已保存", "Saved") + " · " + Shelf.Size(ShelfFiles.FileSize(last) ?? 0), Badge.Done);
        else
            Hero(FilePicture(last, paths.Count), Loc.T($"已保存 {paths.Count} 个文件", $"Saved {paths.Count} files"),
                string.Join(Loc.T("、", ", "), paths.Select(Path.GetFileName)), Badge.Done);
        Clear();
        Where(Path.GetDirectoryName(last) ?? "");
        Buttons((Loc.T("在文件夹中显示", "Show in folder"), false, () => Reveal(last)));
    }

    internal void ShowFailed(string text, string detail, bool retry)
    {
        _incoming = null;
        Hero(Avatar(PhoneGlyph), text, null, Badge.Failed);
        Clear();
        Say(detail);
        if (retry && _invite is { } invite) Buttons((Loc.T("重试连接", "Retry"), true, () => StartAttempt(invite)));
    }

    private static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ---- Offers ----

    internal bool Offer(WeTypeFileInvite invite)
    {
        if (_closed || _link != null || _invite != null && !CanContinue(invite)) return false;
        if (_invite != null)
        {
            _retired.Add(_invite);
            _attempt?.Cancel();
            _service.Log("WeType switched to the same sender's new invitation before receiving a file.");
        }
        StartAttempt(invite);
        return true;
    }

    internal bool OfferLink(WeTypeRelayLink link, string sender)
    {
        if (_closed || _invite != null || _link != null) return false;
        _link = link;
        _linkSender = sender;
        _connected = true;
        _finished = false;
        var attempt = _attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ShowOffer();
        _ = ReceiveAsync(null, attempt);
        return true;
    }

    private void StartAttempt(WeTypeFileInvite invite)
    {
        _invite = invite;
        _connected = _finished = false;
        _incoming = null;
        _expected = 0;
        _attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ShowOffer();
        _ = ReceiveAsync(invite, _attempt);
    }

    private void ChooseFolder()
    {
        if (_closed || _finished || _destination.Task.IsCompleted || SenderName == null) return;
        var picker = new OpenFolderDialog { Title = Loc.T("选择文件保存位置", "Choose where to save the files") };
        if (picker.ShowDialog(this) != true || _closed || _finished) return;
        _destination.TrySetResult(picker.FolderName);
        _service.Log("WeType destination selected for incoming files.");
        ShowOffer();
    }

    private async void Decline()
    {
        if (_invite == null || _link != null || _destination.Task.IsCompleted || _finished) { Close(); return; }
        _declining = true;
        IsEnabled = false;
        _lifetime.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new WeTypeSyncClient(_identity, _service.Save);
            await client.DeclineFileAsync(_invite, timeout.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _service.Log("WeType decline: " + ex.GetType().Name); }
        finally { if (!_closed) Close(); }
    }

    // ---- Work ----

    private async Task ReceiveAsync(WeTypeFileInvite? invite, CancellationTokenSource attempt)
    {
        int saved = 0;
        var progress = new Progress<WeTypeSendProgress>(p =>
        {
            if (_closed || _finished || attempt != _attempt) return;
            _expected = p.Count;
            // The last file the phone canceled may leave every other one saved.
            if (_saved.Count > 0 && _saved.Count >= _expected) ShowSaved(_saved);
            else if (p.Sent < p.Total) ShowReceiving(p);
        });
        try
        {
            void Saved(string path)
            {
                Interlocked.Increment(ref saved);
                _service.Log("WeType incoming file verified and saved.");
                Dispatcher.InvokeAsync(() =>
                {
                    _app.Shelf.AddFiles([path], null);
                    _saved.Add(path);
                    // While other files are still coming, the progress stays; the next file's progress replaces this one's.
                    if (!_closed && _saved.Count >= _expected) ShowSaved(_saved);
                });
            }
            void Offered() { if (_attempt == attempt) _fileOffered = true; }
            if (invite == null)
                await _link!.ReceiveAsync(_destination.Task, progress, Saved, Offered, attempt.Token);
            else
            {
                using var client = new WeTypeSyncClient(_identity, _service.Save) { FileTrace = _service.Log };
                await client.ReceiveFilesAsync(invite, _destination.Task, progress, Saved,
                    () => Dispatcher.InvokeAsync(() => { if (_attempt == attempt) { _connected = true; ShowOffer(); } }),
                    Offered, attempt.Token);
            }
            if (saved == 0 && _attempt == attempt && !_closed)
                ShowFailed(Loc.T("对方已断开", "The sender left"), Loc.T("没有收到文件。", "No files were received."), false);
        }
        catch (Exception) when (attempt.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _service.Log("WeType file receive failed: " + ex.GetType().Name + ": " + ex.Message);
            bool retry = ex is not WeTypeStaleTransferException && !_fileOffered && saved == 0;
            if (_closed) { }
            else if (saved > 0)
                ShowFailed(Loc.T($"连接已结束，已保存 {saved} 个文件", $"Connection ended after {saved} file(s)"), Loc.T("其余文件没有收到。", "The rest were not received."), false);
            else
            {
                var (text, detail) = ex switch
                {
                    WeTypeRelayUnavailableException => (Loc.T("无法连接手机", "Could not reach the phone"), Loc.T("两台设备无法直连，邀请中也没有可用的中转。", "The devices cannot connect directly, and the invitation offered no relay.")),
                    WeTypeStaleTransferException => (Loc.T("这次传输已中断", "This transfer was interrupted"), Loc.T("已通知手机结束，请在手机上重新发送。", "The phone was told to stop. Send the files again from the phone.")),
                    TimeoutException => (Loc.T("没有连上手机", "Could not connect to the phone"), Loc.T("可以重试，或在手机上重新发送。", "Retry, or send again from the phone.")),
                    _ => (Loc.T("接收未完成", "Not received"), Loc.T("没有保存文件。", "No file was saved.")),
                };
                ShowFailed(text, detail, retry);
            }
        }
        finally
        {
            if (_attempt == attempt)
            {
                _attempt = null;
                _finished = true;
            }
            attempt.Dispose();
        }
    }
}
