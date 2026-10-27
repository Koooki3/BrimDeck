using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using Microsoft.Win32;

namespace BrimDeck;

// The panels' windows as the composition wants them (MainWindow.Composition): composed by DWM for the plain themes, layered
// for liquid glass, and layered whenever WPF cannot draw with the graphics card. A window's composition is fixed when it is
// made, so the panels are made again when the wanted one changes.
// Never black: when drawing falls to software (a lost graphics device, too little video memory) or a remote session connects,
// DWM would show a composed window's transparent pixels black, so the panels are made again at once, open or not. A theme
// chosen or left waits until no panel is open or previewed (the settings' theme preview shows liquid glass in a composed
// window, which works, only less quickly), and is looked at again as a panel closes or a preview ends.
public partial class App
{
    private bool _surfaceQueued;

    private void WatchSurface()
    {
        RenderCapability.TierChanged += TierChanged;
        SystemEvents.SessionSwitch += SessionSwitched;
    }

    private void UnwatchSurface()
    {
        RenderCapability.TierChanged -= TierChanged;
        SystemEvents.SessionSwitch -= SessionSwitched;
    }

    private void TierChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => CheckSurface(urgent: true));
    private void SessionSwitched(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.RemoteConnect or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.ConsoleDisconnect)
            Dispatcher.BeginInvoke(() => CheckSurface(urgent: true));
    }

    // Makes the panels again if their windows are not composed as wanted now. urgent: at once, even open (software drawing,
    // a remote session); otherwise once no panel is open or previewed.
    internal void CheckSurface(bool urgent = false)
    {
        if (Exiting || _offscreen || Deck is null || !Deck.IsLoaded) return;
        bool composed = BrimDeck.MainWindow.WantedSurface == WindowSurface.Composed;
        if (Decks.All(deck => deck.DwmComposed == composed)) return;
        // A composed window that can no longer be shown as one is never left waiting.
        urgent |= !composed && !BrimDeck.MainWindow.DrawsWithCard || BrimDeck.MainWindow.RemoteSession;
        if (!urgent && Decks.Any(deck => deck.IsExpanded || deck.Previewing)) return;
        if (_surfaceQueued) return;
        _surfaceQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _surfaceQueued = false; RebuildPanels(); });
    }

    // Makes every panel's window again: the main panel takes over the old one's data (no new refresh, no boot), the other
    // monitors' panels are opened again from it, and the notification area icon moves to the new window.
    private void RebuildPanels()
    {
        if (Exiting || Deck is null) return;
        bool composed = BrimDeck.MainWindow.WantedSurface == WindowSurface.Composed;
        if (Decks.All(deck => deck.DwmComposed == composed)) return;
        SurfaceRebuilds++;
        var old = Deck;
        foreach (var id in _otherDecks.Keys.ToList()) { var other = _otherDecks[id]; _otherDecks.Remove(id); other.Retire(); }
        Deck = new MainWindow(this) { TakesOver = old };
        MainWindow = Deck;
        AssignDisplays();
        Deck.Show();
        _tray?.Dispose();
        MakeTray();
        old.Retire();
    }
    // How many times the panels were made again (for the checks).
    internal int SurfaceRebuilds { get; private set; }

    private void MakeTray()
        => _tray = new TrayIcon(Deck, OpenSettings, ExitApplication, ToggleHidden, () => UserHidden)
        {
            NoticeEntry = () => !Settings.NoticesEnabled ? null
                : Notices.Paused ? (Loc.T("恢复通知提醒", "Resume notices"), () => Notices.Pause(null))
                : (Loc.T("暂停通知提醒 1 小时", "Pause notices for 1 hour"), () => Notices.Pause(TimeSpan.FromHours(1)))
        };
}
