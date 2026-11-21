using BrimDeck.Core.ClipboardSync;

namespace BrimDeck;

internal sealed partial class ClipboardSyncService
{
    private CancellationTokenSource? _fileOffersStop;
    private WeTypeIdentity? _fileOfferIdentity;
    private WeTypeReceiveWindow? _inbox;
    private WeTypeFileInvite? _lastOffer;
    // Invitations already asked about: the ones still open, and the ones that had ended when first seen.
    private readonly List<WeTypeFileInvite> _liveOffers = [], _endedOffers = [];
    private WeTypeFileInvite? _checkingOffer;
    private WeTypeRelayLink? _relay;

    // Keeps the connection from a finished send open for the phone's next files (it sends through it instead of
    // inviting again). One at a time; it closes after 10 idle minutes or when the phone leaves.
    internal void KeepRelay(WeTypeRelayLink link, string sender)
    {
        CloseRelay();
        if (_disposed) { _ = link.DisposeAsync(); return; }
        _relay = link;
        _ = WatchRelayAsync(link, sender);
    }

    internal void CloseRelay()
    {
        if (_relay is { } link) { _relay = null; _ = link.DisposeAsync(); }
    }

    private async Task WatchRelayAsync(WeTypeRelayLink link, string sender)
    {
        try
        {
            if (!await link.WaitForFilesAsync(TimeSpan.FromMinutes(10)) || !ReferenceEquals(_relay, link)) return;
            if (_inbox is { Finished: true }) _inbox.Close();
            if (_inbox == null) OpenFileInbox();
            if (_inbox?.OfferLink(link, sender) != true)
            {
                Log("WeType could not open a window for files sent through the kept connection.");
                return;
            }
            BringInboxToFront();
            Log("WeType receiving files through the kept connection.");
            var window = _inbox;
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            await closed.Task;
        }
        finally
        {
            if (ReferenceEquals(_relay, link)) _relay = null;
            await link.DisposeAsync();
        }
    }

    private void ApplyFileOffers()
    {
        var identity = Accounts.WeType;
        if (identity.Paired && ReferenceEquals(identity, _fileOfferIdentity)) return;
        _fileOffersStop?.Cancel();
        _fileOffersStop = null;
        _fileOfferIdentity = null;
        _lastOffer = null;
        _inbox?.Close();
        if (!identity.Paired || _disposed) return;
        _fileOfferIdentity = identity;
        _fileOffersStop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        _ = WatchFileOffersAsync(identity, _fileOffersStop);
    }

    internal void OpenFileInbox()
    {
        ApplyFileOffers();
        if (_fileOffersStop == null) return;
        if (_inbox != null) { _inbox.Activate(); return; }
        _inbox = new WeTypeReceiveWindow(_app, Accounts.WeType, this, _fileOffersStop.Token);
        _inbox.Closed += (_, _) => _inbox = null;
        _inbox.Show();
    }

    // The invitation comes from another device while BrimDeck is in the background: bring the window to the front.
    private void BringInboxToFront()
    {
        if (_inbox == null) return;
        // A send window stays above other windows with the panel and opens at the same centered spot, so it would
        // cover this window: put this one beside it.
        if (System.Windows.Application.Current.Windows.OfType<WeTypeShareWindow>().FirstOrDefault(w => w.IsVisible) is { } share)
        {
            var area = System.Windows.SystemParameters.WorkArea;
            double width = _inbox.ActualWidth > 0 ? _inbox.ActualWidth : _inbox.Width;
            double right = share.Left + share.ActualWidth + 12;
            _inbox.Left = right + width <= area.Right ? right : Math.Max(area.Left, share.Left - width - 12);
            _inbox.Top = share.Top;
        }
        _inbox.Topmost = true;
        _inbox.Activate();
        _inbox.Topmost = false;
    }

    private void ObserveFileOffer(WeTypeFileInvite? invite)
    {
        if (_disposed || _fileOffersStop == null || _fileOffersStop.IsCancellationRequested) return;
        if (invite == null) { _lastOffer = null; return; }
        if (invite.SameOffer(_lastOffer) || _endedOffers.Any(invite.SameOffer)) return;
        if (!_liveOffers.Any(invite.SameOffer))
        {
            if (!invite.SameOffer(_checkingOffer)) _ = CheckFileOfferAsync(invite);
            return;
        }
        if (_inbox?.Finished == true && !_inbox.CanContinue(invite)) _inbox.Close();
        if (_inbox == null) OpenFileInbox();
        if (_inbox?.Offer(invite) == true)
        {
            _lastOffer = invite;
            BringInboxToFront();
            Log("WeType received a paired file invitation.");
        }
    }

    // Opens nothing for an invitation that has already ended; a failed check is tried again at the next look at the list.
    private async Task CheckFileOfferAsync(WeTypeFileInvite invite)
    {
        _checkingOffer = invite;
        try
        {
            using var client = new WeTypeSyncClient(Accounts.WeType, Save);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool live = await client.FileInviteLiveAsync(invite, limit.Token);
            if (_liveOffers.Count + _endedOffers.Count > 64) { _liveOffers.Clear(); _endedOffers.Clear(); }
            if (!live)
            {
                _endedOffers.Add(invite);
                Log("WeType ignored an invitation that had already ended.");
                return;
            }
            _liveOffers.Add(invite);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("WeType invitation check failed: " + Describe(ex));
            return;
        }
        finally { if (ReferenceEquals(_checkingOffer, invite)) _checkingOffer = null; }
        ObserveFileOffer(invite);
    }

    private async Task WatchFileOffersAsync(WeTypeIdentity identity, CancellationTokenSource stop)
    {
        WeTypeSyncClient? client = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int seconds = 4;
                try
                {
                    if (!_workers.ContainsKey(false))
                    {
                        client ??= new WeTypeSyncClient(identity, Save);
                        var invite = await client.IncomingFileAsync(stop.Token);
                        stop.Token.ThrowIfCancellationRequested();
                        ObserveFileOffer(invite);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log("WeType file invitation check failed: " + Describe(ex));
                    client?.Dispose(); client = null;
                    seconds = 10;
                }
                await Task.Delay(TimeSpan.FromSeconds(seconds), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            client?.Dispose();
            if (ReferenceEquals(stop, _fileOffersStop)) { _fileOffersStop = null; _fileOfferIdentity = null; _inbox?.Close(); }
            stop.Dispose();
        }
    }
}
