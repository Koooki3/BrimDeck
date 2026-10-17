using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace BrimDeck;

// Automatic update checks. The panel's minute clock asks whether a check is due; a returning network and waking from
// sleep ask too. AppUpdates decides when one is due, merges it with any check already running, and keeps failures silent.
public partial class App
{
    private bool _updateEvents;
    private string? _noticeVersion;

    private void StartBackgroundUpdates()
    {
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        Updates.Changed += UpdateNoticeChanged;
        _updateEvents = true;
        _noticeVersion = Updates.NoticeVersion; foreach (var deck in Decks) deck.UpdateNotice();
    }

    private void StopBackgroundUpdates()
    {
        if (!_updateEvents) return;
        _updateEvents = false;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        Updates.Changed -= UpdateNoticeChanged;
    }

    internal void CheckUpdatesIfDue()
    {
        if (Exiting || !_updateEvents) return;
        bool online;
        try { online = NetworkInterface.GetIsNetworkAvailable(); }
        catch (NetworkInformationException) { online = false; }
        _ = Updates.CheckInBackgroundAsync(online, Lifetime.Token);
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) Dispatcher.BeginInvoke(CheckUpdatesIfDue);
    }

    private void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(CheckUpdatesIfDue);
    }

    // Download progress also raises Changed; the panel is touched only when the marked version changes.
    private void UpdateNoticeChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateNoticeChanged); return; }
        if (Updates.NoticeVersion == _noticeVersion) return;
        _noticeVersion = Updates.NoticeVersion; foreach (var deck in Decks) deck.UpdateNotice();
    }
}
