using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// How a panel follows the global hotkeys (see App.Hotkeys.cs).
// Opened by the expand hotkey, the panel stays open until the pointer has been inside it once; after that the usual leave
// rules close it. While the panels are hidden, the expand hotkey still opens this panel; once it closes, it hides again.
// Opened by a hotkey, the panel also takes the keyboard (Tab, arrows, Enter work its pages, the recent notices among them)
// and gives it back to the window that had it when it closes. The panel never activates otherwise (WS_EX_NOACTIVATE): opened
// by the pointer, it leaves the keyboard where it is.
public partial class MainWindow
{
    // Shown for the expand hotkey while the panels are hidden.
    private bool _hotkeyOpened;
    // Opened by the expand hotkey and not entered by the pointer yet.
    private bool _hotkeyPinned;
    private DispatcherTimer? _hotkeyRehide;
    // Holding the keyboard since a hotkey opened the panel, and the window that had it before.
    private bool _keyboardPanel;
    private IntPtr _keyboardReturn;

    // Hidden by the hide hotkey; part of IsSuppressed, so it combines with the full-screen rule the same way.
    internal bool HiddenByUser => _app.UserHidden && !_hotkeyOpened;
    internal IntPtr HotkeyMonitor => MonitorHandle;
    internal bool IsOpenForHotkey => _expanded && !_preview;
    internal bool IsHotkeyPinned => _hotkeyPinned;

    // byKey: the hotkey itself; false when a click asked for the same opening ("View all" on a notice), which leaves the
    // keyboard where it is.
    internal void OpenFromHotkey(bool byKey = true)
    {
        if (_expanded) return;
        // A collapsed preview of the settings ends as hovering would end it; a preview of the open panel is left alone.
        if (_preview) { if (_previewExpanded) return; _app.EndPreviewFromDeck(); }
        // The full-screen rule hides the panel; the hotkey does not override it.
        if (Settings.Behavior(_context, _host.App.Name, _host.App.Path) == WindowBehavior.Hide) return;
        _hotkeyRehide?.Stop();
        if (_app.UserHidden && !_hotkeyOpened) { _hotkeyOpened = true; ApplySettings(); }
        _hover.Stop(); _leave.Stop();
        _hotkeyPinned = true;
        SetExpanded(true);
        if (!_expanded) { _hotkeyPinned = false; if (_hotkeyOpened) { _hotkeyOpened = false; ApplySettings(); } }
        else if (byKey) TakeKeyboard();
    }

    // The panel becomes a window that can be active, takes the foreground and puts the focus on its content: the first
    // notice on the recent notices page, else the selected page's button.
    private void TakeKeyboard()
    {
        var own = new WindowInteropHelper(this).Handle;
        if (own == IntPtr.Zero) return;
        if (!_keyboardPanel && NoticeNative.Foreground() is var previous && previous != own) _keyboardReturn = previous;
        _keyboardPanel = true;
        WindowsHost.KeyboardPanel = own;
        NoticeNative.SetActivatable(own, true);
        Activate();
        Dispatcher.BeginInvoke(() => { if (_keyboardPanel && _expanded) FocusPanelStart(); }, DispatcherPriority.Input);
    }

    private void FocusPanelStart()
    {
        if (CurrentPage == DeckPage.Notices && _pageRows.Count > 0) { _pageRows[0].Row.Focus(); return; }
        var button = CurrentPage switch { DeckPage.Music => MusicPageButton, DeckPage.Monitor => MonitorPageButton, DeckPage.Notices => NoticesPageButton, _ => AIUsageButton };
        Keyboard.Focus(button);
    }

    // The panel closed (or the user went to another window): it cannot be active again until the next hotkey, and the window
    // that had the keyboard gets it back, unless the user has moved on to another one meanwhile.
    private void ReleaseKeyboard(bool giveBack)
    {
        if (!_keyboardPanel) return;
        _keyboardPanel = false;
        var own = new WindowInteropHelper(this).Handle;
        if (own == IntPtr.Zero) return;
        NoticeNative.SetActivatable(own, false);
        if (giveBack) NoticeNative.ReturnForeground(own, _keyboardReturn);
        _keyboardReturn = IntPtr.Zero;
        // Only once the foreground has moved on: until then the panel being in front must not read as a change of context.
        if (WindowsHost.KeyboardPanel == own) WindowsHost.KeyboardPanel = IntPtr.Zero;
    }

    internal void CollapseFromHotkey() => SetExpanded(false);

    // For TrackPointer while open: the pointer inside ends the pin. True while the pin holds the panel open.
    internal bool HotkeyPinned(bool inside)
    {
        if (inside) _hotkeyPinned = false;
        return _hotkeyPinned;
    }

    // For PointerChanged: any entry ends the pin.
    internal void ReleaseHotkeyPin() => _hotkeyPinned = false;

    // For SetExpanded, on the change from open to closed.
    internal void HotkeyPanelCollapsed()
    {
        ReleaseKeyboard(giveBack: true);
        _hotkeyPinned = false;
        if (!_hotkeyOpened) return;
        _hotkeyOpened = false;
        if (!_app.UserHidden) return;
        // Hide again once the closing animation has played. Never from inside SetExpanded: ApplySettings calls it.
        int duration = Settings.Animations && SystemParameters.ClientAreaAnimation ? Settings.AnimationDuration : 0;
        _hotkeyRehide ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _hotkeyRehide.Stop();
        _hotkeyRehide.Interval = TimeSpan.FromMilliseconds(Math.Max(1, duration));
        _hotkeyRehide.Tick -= Rehide; _hotkeyRehide.Tick += Rehide;
        _hotkeyRehide.Start();
    }

    // Called by App for every panel when the hidden state changes.
    internal void UserHiddenChanged()
    {
        _hotkeyRehide?.Stop();
        _hotkeyOpened = false; _hotkeyPinned = false;
        if (_app.UserHidden)
        {
            _hover.Stop(); _leave.Stop(); _pointerInside = false; _cursorInCompact = false;
            if (_alert is not null) ClearAlert();
        }
        // Hiding and showing again are immediate (MainWindow.ApplySettings).
        ApplySettings(true);
    }

    private void Rehide(object? sender, EventArgs e)
    {
        _hotkeyRehide?.Stop();
        if (HiddenByUser && !_expanded) ApplySettings();
    }
}
