using System.Windows.Input;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Momentary mouse pass-through while the ClickThrough hotkey is held (DeckSettings.ClickThroughHold). Does not flip the sticky
// ClickThrough setting; tray and the Compatibility switch still do that.
//
// Bare non-function keys (Escape, CapsLock, …) are watched with GetAsyncKeyState instead of RegisterHotKey, so other programs
// still receive them. Combinations with modifiers, and bare F1–F24, keep using RegisterHotKey.
public partial class App
{
    private PromptTimer? _holdPassThroughPoll;
    private PromptTimer? _clickThroughObserve;
    private int _holdPassThroughVk;
    private int _observeVk;
    private bool _observeDown;

    // True while the hold hotkey is down. MainWindow.ClickThrough ORs this into its pass-through math.
    internal bool HoldPassThrough { get; private set; }

    // Bare ClickThrough keys that would steal Esc (and similar) system-wide if registered with RegisterHotKey.
    internal static bool ObservesClickThrough(ModifierKeys modifiers, Key key)
        => modifiers == ModifierKeys.None && !HotkeyText.IsFunctionKey(key.ToString());

    private void ClickThroughFromHotkey()
    {
        if (Settings.ClickThroughHold) BeginHoldPassThrough();
        else ToggleClickThrough();
    }

    // Starts the non-consuming watcher for a bare ClickThrough key. Called instead of RegisterHotKey.
    internal HotkeyStatus ObserveClickThrough(int vk)
    {
        if (vk == 0) return HotkeyStatus.Invalid;
        if (_hotkeySuspensions > 0) return HotkeyStatus.Active;
        _observeVk = vk;
        _observeDown = PasteInput.KeyDown(vk);
        _clickThroughObserve ??= new PromptTimer(Dispatcher, PollClickThroughObserve) { Interval = TimeSpan.FromMilliseconds(30) };
        _clickThroughObserve.Start();
        return HotkeyStatus.Active;
    }

    internal void StopClickThroughObserver()
    {
        _clickThroughObserve?.Stop();
        _observeVk = 0;
        _observeDown = false;
        EndHoldPassThrough();
    }

    private void PollClickThroughObserve()
    {
        if (_observeVk == 0 || Exiting) return;
        bool down = PasteInput.KeyDown(_observeVk);
        if (down == _observeDown) return;
        _observeDown = down;
        if (down)
        {
            // Panel dismiss / move mode already reserved Escape via RegisterHotKey; do not also treat Escape as pass-through.
            // CapsLock and other observed keys still start hold pass-through while a panel is open.
            if (_observeVk == KeyInterop.VirtualKeyFromKey(Key.Escape) && Decks.Any(deck => deck.ReservesEscape))
                return;
            if (Settings.ClickThroughHold) BeginHoldPassThrough();
            else ToggleClickThrough();
        }
        else EndHoldPassThrough();
    }

    private void BeginHoldPassThrough()
    {
        if (HoldPassThrough) return;
        if (!HotkeyGesture.TryParse(Settings.ClickThroughHotkey, out _, out var key, bareKeyOk: true))
        {
            ToggleClickThrough();
            return;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            ToggleClickThrough();
            return;
        }
        HoldPassThrough = true;
        _holdPassThroughVk = vk;
        ApplyHoldPassThrough();
        // Observer path already watches release; RegisterHotKey path needs a release poll.
        if (_observeVk == vk) return;
        _holdPassThroughPoll ??= new PromptTimer(Dispatcher, PollHoldPassThrough) { Interval = TimeSpan.FromMilliseconds(30) };
        _holdPassThroughPoll.Start();
    }

    private void PollHoldPassThrough()
    {
        if (PasteInput.KeyDown(_holdPassThroughVk)) return;
        EndHoldPassThrough();
    }

    private void EndHoldPassThrough()
    {
        _holdPassThroughPoll?.Stop();
        if (!HoldPassThrough) return;
        HoldPassThrough = false;
        ApplyHoldPassThrough();
    }

    private void ApplyHoldPassThrough()
    {
        foreach (var deck in Decks.ToList())
            if (deck is not null) deck.RefreshHoldPassThrough();
    }
}
