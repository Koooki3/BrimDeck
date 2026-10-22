using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using BrimDeck.Protocol;

namespace BrimDeck.Native;

// The local end of brim-notify: a named pipe that only the current user can open, never over the network, named for this
// user and logon session (NoticeProtocol.PipeName). Each connection may send several frames; every frame gets one reply.
// Messages are only data: nothing in them is run or opened.
internal sealed class NoticePipeServer : IDisposable
{
    private const int Instances = 4;
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);
    private readonly Func<NoticeMessage, Task<NoticeMessage>> _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _listeners = [];
    private readonly object _gate = new();
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
        for (int i = 0; i < Instances; i++) _listeners.Add(Task.Run(ListenAsync));
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

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                lock (_gate)
                {
                    // While none of BrimDeck's instances exists, the name must be free: FirstPipeInstance fails if anyone holds it.
                    var options = PipeOptions.Asynchronous | (_live == 0 ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                    pipe = NamedPipeServerStreamAcl.Create(Name, PipeDirection.InOut, Instances, PipeTransmissionMode.Byte, options, 0, 0, Security());
                    _live++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = ex.Message;
                try { await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token); } catch (OperationCanceledException) { return; }
                continue;
            }
            Error = null;
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
                await NoticeFrames.WriteAsync(pipe, NoticeProtocol.Reply(NoticeResults.Rejected, "bad-frame"), _stop.Token);
                return;
            }
            if (message is null) return;
            NoticeMessage reply;
            try { reply = await _handle(message); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Failed?.Invoke(ex); reply = NoticeProtocol.Reply(NoticeResults.Rejected, "internal-error"); }
            await NoticeFrames.WriteAsync(pipe, reply, _stop.Token);
        }
    }

    // Not waited for: Dispose runs on the UI thread, which a listener serving a message may be waiting for. Each listener
    // closes its pipe as it sees the cancellation; the token source goes once they all have.
    public void Dispose()
    {
        _stop.Cancel();
        _ = Task.WhenAll(_listeners).ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
