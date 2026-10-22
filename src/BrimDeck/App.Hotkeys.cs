using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The two global hotkeys and the hidden state the hide hotkey toggles. Both hotkeys are unset by default and are taken from
// the settings (HideHotkey, ExpandHotkey). The registrations live on a message-only window of the app (GlobalHotkeys), so
// panels closed and opened again for display changes do not lose them.
public partial class App
{
    private GlobalHotkeys? _hotkeys;
    private readonly Dictionary<HotkeyKind, HotkeyStatus> _hotkeyStatus = new() { [HotkeyKind.Hide] = HotkeyStatus.Unset, [HotkeyKind.Expand] = HotkeyStatus.Unset, [HotkeyKind.Notices] = HotkeyStatus.Unset };
    private string _hideHotkey = "", _expandHotkey = "", _noticesHotkey = "";
    private int _hotkeySuspensions;

    // Raised on the UI thread after the hotkeys are registered again, with any status change.
    public event EventHandler? HotkeysChanged;
    // Raised when the panels are hidden or shown again by the hide hotkey or the tray menu.
    public event EventHandler? UserHiddenChanged;

    // Every panel is hidden: nothing is drawn, hovering does nothing and no click is caught. Not saved; a restart shows them.
    public bool UserHidden { get; private set; }

    // The status from the last registration. While SuspendHotkeys is in effect nothing is registered and this keeps the
    // status the combinations will have again.
    public HotkeyStatus StatusOf(HotkeyKind kind) => _hotkeyStatus[kind];
    public bool IsHotkeyRegistered(HotkeyKind kind) => StatusOf(kind) == HotkeyStatus.Active;

    // Creates the registrar and registers the saved hotkeys. Call once the main panel exists.
    internal void InitializeHotkeys()
    {
        if (_hotkeys is not null) return;
        _hotkeys = new GlobalHotkeys();
        _hotkeys.Pressed += HotkeyPressed;
        ApplyHotkeys();
    }

    // Registers the hotkeys saved in the settings. Unchanged texts that are already active are left alone; any other one is
    // tried again, so a combination another program has since released becomes active on the next save.
    // The notices hotkey is registered only while dynamic notices are on.
    public void ApplyHotkeys() => ApplyHotkeys(Settings.HideHotkey, Settings.ExpandHotkey, Settings.NoticesEnabled ? Settings.NoticeHotkey : "");
    internal void ApplyHotkeys(string? hide, string? expand, string? notices = "")
    {
        hide ??= ""; expand ??= ""; notices ??= "";
        bool unchanged = hide == _hideHotkey && expand == _expandHotkey && notices == _noticesHotkey
            && _hotkeyStatus.Values.All(status => status is HotkeyStatus.Active or HotkeyStatus.Unset);
        _hideHotkey = hide; _expandHotkey = expand; _noticesHotkey = notices;
        if (unchanged && _hotkeySuspensions == 0 && _hotkeys is not null) return;
        RegisterHotkeys();
    }

    // Releases both hotkeys, for example while the settings window records a new combination. Calls nest; each must be
    // matched by ResumeHotkeys, and the hotkeys come back with the last one.
    public void SuspendHotkeys()
    {
        if (_hotkeySuspensions++ > 0 || _hotkeys is null) return;
        foreach (var kind in Enum.GetValues<HotkeyKind>()) _hotkeys.Unregister(kind);
    }

    public void ResumeHotkeys()
    {
        if (_hotkeySuspensions == 0) return;
        if (--_hotkeySuspensions == 0) RegisterHotkeys();
    }

    internal void DisposeHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.Pressed -= HotkeyPressed; _hotkeys.Dispose(); _hotkeys = null;
    }

    // Hides every panel, or shows them again. An open panel closes first.
    public void ToggleHidden() => SetUserHidden(!UserHidden);
    public void SetUserHidden(bool hidden)
    {
        if (UserHidden == hidden) return;
        UserHidden = hidden;
        foreach (var deck in Decks.ToList()) deck.UserHiddenChanged();
        UserHiddenChanged?.Invoke(this, EventArgs.Empty);
    }

    // Opens the panel on the monitor under the pointer, or closes the one that is open. A monitor without a panel uses the
    // main panel.
    public void ToggleExpandFromHotkey()
    {
        if (Decks.FirstOrDefault(deck => deck.IsOpenForHotkey) is { } open) { open.CollapseFromHotkey(); return; }
        var monitor = GlobalHotkeys.CursorMonitor();
        (Decks.FirstOrDefault(deck => deck.HotkeyMonitor == monitor) ?? Deck).OpenFromHotkey();
    }

    // A notice on screen opens its details for the keyboard; otherwise the panel under the pointer opens on the recent notices.
    private void NoticesFromHotkey()
    {
        if (!Settings.NoticesEnabled) return;
        foreach (var deck in Decks) if (deck.NoticeKeyboard()) return;
        var monitor = GlobalHotkeys.CursorMonitor();
        (Decks.FirstOrDefault(deck => deck.HotkeyMonitor == monitor) ?? Deck).OpenNoticesPage();
    }

    private void RegisterHotkeys()
    {
        foreach (var kind in Enum.GetValues<HotkeyKind>())
        {
            var (status, text) = HotkeyText.Plan(kind, _hideHotkey, _expandHotkey, _noticesHotkey, HotkeyGesture.IsKeyName);
            _hotkeys?.Unregister(kind);
            if (status is null)
            {
                HotkeyGesture.TryParse(text, out var modifiers, out var key);
                status = !HotkeyGesture.ToNative(modifiers, key, out uint fs, out uint vk) ? HotkeyStatus.Invalid
                    : _hotkeys is null || _hotkeySuspensions > 0 ? HotkeyStatus.Active : _hotkeys.Register(kind, fs, vk);
            }
            _hotkeyStatus[kind] = status.Value;
        }
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HotkeyPressed(HotkeyKind kind)
    {
        if (Exiting) return;
        if (kind == HotkeyKind.Hide) ToggleHidden();
        else if (kind == HotkeyKind.Notices) NoticesFromHotkey();
        else ToggleExpandFromHotkey();
    }
}
