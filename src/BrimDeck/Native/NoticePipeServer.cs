using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

namespace BrimDeck.Native;

// The local end of brim-notify: a named pipe that only the current user can open, never over the network, named for this
// user and logon session (NoticeProtocol.PipeName). Each connection may send several frames; every frame gets one reply.
// Messages are only data: nothing in them is run or opened.
internal sealed class NoticePipeServer : IDisposable
{
    private const int Instances = 4;
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);
    // A sender that does not read its reply lets go of the instance after this (life-L11, sec-L2).
    private static readonly TimeSpan ReplyLimit = TimeSpan.FromSeconds(10);
    // How long a new server waits for the previous one to close its instances before taking the name anyway.
    private static readonly TimeSpan ReleaseLimit = TimeSpan.FromSeconds(2);
    // The previous server's listeners. Switching notices off and on replaces the server; until the old instances are closed,
    // FirstPipeInstance would fail and the channel would be down for a retry (life-L2). Only touched on the UI thread.
    private static Task s_released = Task.CompletedTask;
    private readonly Func<NoticeMessage, Task<NoticeMessage>> _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _listeners = [];
    private readonly object _gate = new();
    // Messages of every kind over all connections, counted here so that a flood is answered without reaching the UI thread
    // (sec-L2). It is above the notices' own limit (NoticeCenter.Admit), which still decides for notices.
    private readonly IntakeGate _intake = new(300, TimeSpan.FromSeconds(10));
    private int _live;
    public string Name { get; }
    // Set when the pipe could not be created, for example because another BrimDeck of this user already serves the name.
    public string? Error { get; private set; }
    // A message's handling failed; the sender got "internal-error".
    public Action<Exception>? Failed { get; set; }

    public NoticePipeServer(Func<NoticeMessage, Task<NoticeMessage>> handle)
    {
        _handle = handle;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Name = NoticeProtocol.PipeName(process.SessionId, WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
    }

    public void Start()
    {
        var previous = s_released;
        for (int i = 0; i < Instances; i++) _listeners.Add(Task.Run(() => ListenAsync(previous)));
    }

    private PipeSecurity Security()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private async Task ListenAsync(Task previous)
    {
        try { await previous.WaitAsync(ReleaseLimit, _stop.Token); }
        catch (Exception) when (!_stop.IsCancellationRequested) { }
        catch (OperationCanceledException) { return; }
        var retry = TimeSpan.FromMilliseconds(200);
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            bool first = false;
            try
            {
                lock (_gate)
                {
                    // While none of BrimDeck's instances exists, the name must be free: FirstPipeInstance fails if anyone holds it.
                    first = _live == 0;
                    var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                    pipe = NamedPipeServerStreamAcl.Create(Name, PipeDirection.InOut, Instances, PipeTransmissionMode.Byte, options, 0, 0, Security());
                    _live++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = first && HeldByAnotherUser()
                    ? Loc.T("通道名称被另一个用户占用。", "Another user holds the channel's name.")
                    : ex.Message;
                try { await Task.Delay(retry, _stop.Token); } catch (OperationCanceledException) { return; }
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, TimeSpan.FromSeconds(5).Ticks));
                continue;
            }
            Error = null;
            retry = TimeSpan.FromMilliseconds(200);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                await ServeAsync(pipe);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
            finally
            {
                await pipe.DisposeAsync();
                lock (_gate) _live--;
            }
        }
    }

    // Whether the name is taken by a pipe another user created (the name can be worked out, so another local user could take
    // it first): the client side refuses a pipe its own user does not own, or may not open. Another BrimDeck of this user,
    // or one that is busy, does not count.
    private bool HeldByAnotherUser()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(200);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { return false; }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        while (!_stop.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            idle.CancelAfter(Idle);
            NoticeMessage? message;
            try { message = await NoticeFrames.ReadMessageAsync(pipe, idle.Token); }
            catch (InvalidDataException)
            {
                // A malformed frame cannot be skipped reliably; the sender is told once and the connection ends.
                idle.CancelAfter(ReplyLimit);
                await NoticeFrames.WriteAsync(pipe, NoticeProtocol.Reply(NoticeResults.Rejected, "bad-frame"), idle.Token);
                return;
            }
            if (message is null) return;
            NoticeMessage reply;
            if (!_intake.Admit(Environment.TickCount64)) reply = NoticeProtocol.Reply(NoticeResults.Rejected, "rate-limited", message.EventId);
            else
            {
                try { reply = await _handle(message); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Failed?.Invoke(ex); reply = NoticeProtocol.Reply(NoticeResults.Rejected, "internal-error"); }
            }
            idle.CancelAfter(ReplyLimit);
            await NoticeFrames.WriteAsync(pipe, reply, idle.Token);
        }
    }

    // Not waited for: Dispose runs on the UI thread, which a listener serving a message may be waiting for. Each listener
    // closes its pipe as it sees the cancellation; the token source goes once they all have, and the next server waits for
    // the same moment before it takes the name.
    public void Dispose()
    {
        _stop.Cancel();
        var all = Task.WhenAll(_listeners);
        s_released = all;
        _ = all.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
