using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The global shortcuts: hide/show (on the Collapsed view page), expand (on the Expanded view page) and notices (on the Dynamic notices page). Each is recorded
// by clicking its box and pressing the combination; leaving the box cancels, and only the cross inside the box clears it. While a box records, the app's
// registered shortcuts are suspended so pressing the current combination records it instead of acting on the panel.
public sealed partial class SettingsWindow
{
    // Stops the box that is recording, if any; only one records at a time.
    private Action? _stopHotkeyRecording;
    // Rows on screen that update when the app reports a new registration result.
    private readonly HashSet<Action> _hotkeyRows = [];
    private const double HotkeyBoxWidth = 180;
    // The record button draws nothing itself; the box around it does.
    private static readonly ControlTemplate HotkeyBoxTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
        "<Border Background='Transparent'><ContentPresenter VerticalAlignment='Center'/></Border></ControlTemplate>");

    private void HideHotkeySection()
    {
        SectionTitle(Loc.T("快捷键", "Shortcut"));
        Group(HotkeyRow(HotkeyKind.Hide));
    }

    private static string HotkeyName(HotkeyKind kind) => kind switch
    {
        HotkeyKind.Hide => Loc.T("“隐藏/显示快捷键”", "the hide/show shortcut"), HotkeyKind.Expand => Loc.T("“展开快捷键”", "the expand shortcut"),
        _ => Loc.T("“通知快捷键”", "the notices shortcut")
    };
    private static string SavedHotkey(DeckSettings settings, HotkeyKind kind) => (kind switch
    {
        HotkeyKind.Hide => settings.HideHotkey, HotkeyKind.Expand => settings.ExpandHotkey, _ => settings.NoticeHotkey
    }) ?? "";
    private static void SetHotkey(DeckSettings settings, HotkeyKind kind, string value)
    {
        switch (kind) { case HotkeyKind.Hide: settings.HideHotkey = value; break; case HotkeyKind.Expand: settings.ExpandHotkey = value; break; default: settings.NoticeHotkey = value; break; }
    }

    private FrameworkElement HotkeyRow(HotkeyKind kind)
    {
        string title = kind switch { HotkeyKind.Hide => Loc.T("隐藏/显示快捷键", "Hide/show shortcut"), HotkeyKind.Expand => Loc.T("展开快捷键", "Expand shortcut"), _ => Loc.T("通知快捷键", "Notices shortcut") };
        string help = kind switch
        {
            HotkeyKind.Hide => Loc.T("按下快捷键即可隐藏刘海、胶囊或指示条，再按一次恢复显示。", "Press the shortcut to hide the notch, capsule or indicator bar; press it again to show it."),
            HotkeyKind.Expand => Loc.T("按下快捷键即可展开面板，再按一次收起。", "Press the shortcut to expand the panel; press it again to collapse it."),
            _ => Loc.T("有提示时打开它的详情并可用键盘操作（Tab 切换、Enter 确认、Esc 关闭）；没有提示时打开最近通知。默认不设置，避免与其他程序冲突。",
                "With a notice on screen, opens its details for the keyboard (Tab to move, Enter to choose, Esc to close); otherwise opens the recent notices. Unset by default, to avoid clashing with other programs.")
        };
        string Saved() => SavedHotkey(S, kind);
        // The other shortcuts, with the names a clash message gives them.
        IEnumerable<(string Text, string Name)> Others() => Enum.GetValues<HotkeyKind>().Where(other => other != kind).Select(other => (SavedHotkey(S, other), HotkeyName(other)));

        // The box keeps one size and place in every state: the text on the left, trimmed before the clear button, which
        // always holds its slot on the right and is only dimmed while nothing is set. The record button spans the whole
        // box (so its focus ring rings the box); the clear button sits above it and takes its own clicks.
        var text = TextLine("", 12, TextPrimary);
        text.VerticalAlignment = VerticalAlignment.Center; text.TextTrimming = TextTrimming.CharacterEllipsis; text.Margin = new Thickness(10, 0, 32, 0);
        var box = new Button
        {
            Template = HotkeyBoxTemplate, Content = text, HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand,
            FocusVisualStyle = (Style)FindResource("SettingsFocus")
        };
        Grid.SetColumnSpan(box, 2);
        AutomationProperties.SetName(box, title);
        var cross = new Path { Data = System.Windows.Media.Geometry.Parse("M 1,1 L 9,9 M 9,1 L 1,9"), StrokeThickness = 1.3, Width = 10, Height = 10 };
        cross.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var clear = new Button { Style = (Style)FindResource("SettingsGlyphButton"), Content = cross, Width = 24, Height = 24, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(clear, 1);
        AutomationProperties.SetName(clear, Loc.T("清除" + title, "Clear " + title));
        clear.ToolTip = Loc.T("清除", "Clear");
        ToolTipService.SetShowOnDisabled(clear, false);
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition()); inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // The clear button's slot never reaches the record button, even while the clear button is disabled and takes no clicks.
        var slot = new Border { Background = System.Windows.Media.Brushes.Transparent, Width = 27 }; Grid.SetColumn(slot, 1);
        inner.Children.Add(box); inner.Children.Add(slot); inner.Children.Add(clear);
        var chrome = new Border
        {
            Child = inner, Width = HotkeyBoxWidth, Height = 30, CornerRadius = new CornerRadius(6), Background = UI.Brush(_palette.Input),
            BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center
        };
        // Messages fill the space between the label and the box (at most 300 DIP wide and two lines), so neither the box nor the row height moves.
        var note = TextLine("", 11.5, _palette.Dark ? "#F0A6AA" : "#B12E3A");
        note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.CharacterEllipsis; note.MaxWidth = 300; note.MaxHeight = 34; note.HorizontalAlignment = HorizontalAlignment.Right;
        note.TextAlignment = TextAlignment.Right; note.Margin = new Thickness(0, 0, 12, 0);
        note.VerticalAlignment = VerticalAlignment.Center; note.Visibility = Visibility.Hidden;
        AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Assertive);

        bool recording = false, swallowKeyUp = false;
        void Note(string message) { note.Text = message; note.ToolTip = message.Length > 0 ? message : null; note.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Hidden; }
        void Show()
        {
            string saved = Saved();
            text.Text = recording ? Loc.T("请按下组合键…", "Press a combination…") : saved.Length == 0 ? Loc.T("未设置", "Not set") : HotkeyDisplay(saved);
            text.Foreground = UI.Brush(recording ? TextSecondary : saved.Length == 0 ? TextTertiary : TextPrimary);
            // A long combination is trimmed in the box; hovering shows it whole.
            box.ToolTip = !recording && saved.Length > 0 ? HotkeyDisplay(saved) : null;
            chrome.BorderBrush = UI.Brush(recording ? _palette.Accent : _palette.Border);
            clear.IsEnabled = saved.Length > 0; clear.Opacity = clear.IsEnabled ? 1 : .35;
            if (!recording) Note(HotkeyProblem(kind, saved, Others()));
            AutomationProperties.SetHelpText(box, (saved.Length == 0 ? Loc.T("未设置。", "Not set. ") : HotkeyDisplay(saved) + Loc.T("。", ". ")) + help);
        }
        Action stop = null!;
        void Start()
        {
            if (recording) return;
            _stopHotkeyRecording?.Invoke();
            recording = true; _stopHotkeyRecording = stop;
            SuspendAppHotkeys();
            box.Focus(); Note(""); Show();
        }
        stop = () =>
        {
            if (!recording) return;
            recording = false;
            if (_stopHotkeyRecording == stop) _stopHotkeyRecording = null;
            // Resuming registers the shortcuts again from the settings, including one just recorded.
            ResumeAppHotkeys();
            Show();
        };
        box.Click += (_, _) => { if (!recording) Start(); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (!recording) return;
            var key = HotkeyGesture.KeyOf(e);
            if (key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed) return;
            // Every key goes to the recorder, so Alt opens no menu and Enter or Space does not press the box again.
            e.Handled = true;
            var modifiers = CurrentModifiers();
            if (IsModifierKey(key))
            {
                text.Text = ModifierDisplay(modifiers) + "+…"; text.Foreground = UI.Brush(TextPrimary);
                return;
            }
            if (IsCharacterKey(key) && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            {
                Note(Loc.T("字母、数字等按键需要搭配 Ctrl、Alt 或 Win。", "Letter, digit and other character keys need Ctrl, Alt or Win."));
                text.Text = Loc.T("请按下组合键…", "Press a combination…"); text.Foreground = UI.Brush(TextSecondary);
                return;
            }
            string gesture = FormatHotkey(modifiers, key);
            if (gesture.Length == 0)
            {
                Note(Loc.T("请同时按住 Ctrl、Alt、Shift 或 Win；F1–F24 可以单独使用。", "Hold Ctrl, Alt, Shift or Win as well; F1–F24 can be used alone."));
                text.Text = Loc.T("请按下组合键…", "Press a combination…"); text.Foreground = UI.Brush(TextSecondary);
                return;
            }
            if (Others().FirstOrDefault(other => SameHotkey(gesture, other.Text)) is { Name: { } clash })
            {
                Note(Loc.T("与" + clash + "重复，请换一个组合键。", "This is already " + clash + ". Choose another combination."));
                text.Text = Loc.T("请按下组合键…", "Press a combination…"); text.Foreground = UI.Brush(TextSecondary);
                return;
            }
            Change(s => SetHotkey(s, kind, gesture));
            swallowKeyUp = true; stop();
        };
        box.PreviewKeyUp += (_, e) =>
        {
            if (!recording && !swallowKeyUp) return;
            e.Handled = true;
            var key = HotkeyGesture.KeyOf(e);
            if (!recording && !IsModifierKey(key)) swallowKeyUp = false;
            if (recording && IsModifierKey(key))
            {
                var modifiers = CurrentModifiers();
                text.Text = modifiers == ModifierKeys.None ? Loc.T("请按下组合键…", "Press a combination…") : ModifierDisplay(modifiers) + "+…";
            }
        };
        // Leaving the box, the page or the window ends recording, so suspensions and resumptions stay paired.
        box.LostKeyboardFocus += (_, _) => { swallowKeyUp = false; stop(); };
        clear.Click += (_, _) => { stop(); Change(s => SetHotkey(s, kind, "")); Note(""); Show(); };

        // Like Row, but the message takes the middle column, so a narrow window shrinks the message instead of pushing into the label.
        var row = new Grid { MinHeight = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = SettingLabel(title, () => help); label.Margin = new Thickness(0, 0, 12, 0); row.Children.Add(label);
        Grid.SetColumn(note, 1); row.Children.Add(note);
        Grid.SetColumn(chrome, 2); row.Children.Add(chrome);
        Action refresh = () => { if (!recording) Show(); };
        row.Loaded += (_, _) => { if (_hotkeyRows.Add(refresh) && _hotkeyRows.Count == 1) WatchAppHotkeys(true); Show(); };
        row.Unloaded += (_, _) => { stop(); if (_hotkeyRows.Remove(refresh) && _hotkeyRows.Count == 0) WatchAppHotkeys(false); };
        Show();
        return row;
    }

    // The app raises HotkeysChanged on the UI thread after registering again.
    private void AppHotkeysChanged() { foreach (var refresh in _hotkeyRows.ToList()) refresh(); }

    // What is wrong with a saved shortcut, or "" when nothing is: the same combination as the other shortcut, or a
    // registration the app could not complete.
    private string HotkeyProblem(HotkeyKind kind, string saved, IEnumerable<(string Text, string Name)> others)
    {
        if (saved.Length == 0) return "";
        if (others.FirstOrDefault(other => SameHotkey(saved, other.Text)) is { Name: { } clash })
            return Loc.T("与" + clash + "重复，请换一个组合键。", "This is already " + clash + ". Choose another combination.");
        return AppHotkeyState(kind) switch
        {
            HotkeyStatus.InUse => Loc.T("这个组合键已被其他程序占用，未能生效。", "Another program already uses this combination, so it is not active."),
            HotkeyStatus.Invalid => Loc.T("无法识别这个组合键，请重新录制。", "This combination is not recognized. Record it again."),
            HotkeyStatus.Duplicate => Loc.T("与其他快捷键重复，请换一个组合键。", "This is already another shortcut. Choose another combination."),
            _ => ""
        };
    }

    private static ModifierKeys CurrentModifiers()
    {
        var modifiers = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string ModifierDisplay(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }

    // A saved shortcut as the user reads it: digits as "1" rather than "D1", punctuation as its character.
    private static string HotkeyDisplay(string saved)
    {
        if (!ParseHotkey(saved, out var modifiers, out var key)) return saved;
        string name = key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (char)('0' + (key - Key.NumPad0)),
            Key.Add => "Num +", Key.Subtract => "Num -", Key.Multiply => "Num *", Key.Divide => "Num /", Key.Decimal => "Num .",
            Key.OemPlus => "=", Key.OemMinus => "-", Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/", Key.OemSemicolon => ";",
            Key.OemQuotes => "'", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemPipe => "\\", Key.OemTilde => "`",
            Key.Space => Loc.T("空格", "Space"), Key.Return => "Enter", Key.Back => "Backspace", Key.Escape => "Esc",
            Key.CapsLock => "CapsLock", Key.PrintScreen => "PrintScreen",
            Key.PageUp => "PageUp", Key.PageDown => "PageDown", Key.Delete => "Delete", Key.Insert => "Insert",
            Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
            _ => key.ToString()
        };
        return modifiers == ModifierKeys.None ? name : ModifierDisplay(modifiers) + "+" + name;
    }

    private static bool ParseHotkey(string text, out ModifierKeys modifiers, out Key key) => HotkeyGesture.TryParse(text, out modifiers, out key);
    private static string FormatHotkey(ModifierKeys modifiers, Key key) => HotkeyGesture.Format(modifiers, key);
    private HotkeyStatus AppHotkeyState(HotkeyKind kind) => _app.StatusOf(kind);
    private void SuspendAppHotkeys() => _app.SuspendHotkeys();
    private void ResumeAppHotkeys() => _app.ResumeHotkeys();
    private EventHandler? _hotkeysChanged;
    // Subscribed while a shortcut row is on screen; the last row to leave (also when the window closes) unsubscribes.
    private void WatchAppHotkeys(bool watch)
    {
        if (watch && _hotkeysChanged is null) { _hotkeysChanged = (_, _) => AppHotkeysChanged(); _app.HotkeysChanged += _hotkeysChanged; }
        else if (!watch && _hotkeysChanged is not null) { _app.HotkeysChanged -= _hotkeysChanged; _hotkeysChanged = null; }
    }

    // Two saved shortcuts are the same when they name the same keys, whatever the spelling.
    private static bool SameHotkey(string first, string second)
        => ParseHotkey(first, out var firstModifiers, out var firstKey) && ParseHotkey(second, out var secondModifiers, out var secondKey)
           && firstModifiers == secondModifiers && firstKey == secondKey;

    // Keys that type a character: with only Shift (or nothing) they would take over ordinary typing.
    private static bool IsCharacterKey(Key key)
        => key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9
               or Key.Space or Key.Add or Key.Subtract or Key.Multiply or Key.Divide or Key.Decimal
           || key.ToString().StartsWith("Oem", StringComparison.Ordinal);
}
