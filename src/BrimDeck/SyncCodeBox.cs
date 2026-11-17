using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace BrimDeck;

// A six-digit pairing code, one digit to a cell. As an input it takes digits typed or pasted (spaces and dashes in a pasted code
// are dropped) and reports the code once it is complete; read-only, it shows a code to be typed on another device.
internal sealed class SyncCodeBox : FrameworkElement
{
    public const int Digits = 6;
    private const double CellWidth = 42, CellHeight = 50, Gap = 8, GroupGap = 18, Radius = 8;
    private string _text = "";

    public bool ReadOnly { get; init; }
    public string Label { get; init; } = "";
    public event Action<string>? Completed;
    public event Action? Edited;

    public string Text
    {
        get => _text;
        set
        {
            string digits = new(value.Where(char.IsAsciiDigit).Take(Digits).ToArray());
            if (digits == _text) return;
            _text = digits; InvalidateVisual();
        }
    }

    public SyncCodeBox()
    {
        Width = Digits * CellWidth + (Digits - 2) * Gap + GroupGap; Height = CellHeight;
        HorizontalAlignment = HorizontalAlignment.Center; SnapsToDevicePixels = true;
        FocusVisualStyle = null;
        InputMethod.SetIsInputMethodEnabled(this, false);
        Loaded += (_, _) => { Focusable = !ReadOnly; Cursor = ReadOnly ? null : Cursors.IBeam; };
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
        IsEnabledChanged += (_, _) => { Opacity = IsEnabled ? 1 : 0.5; InvalidateVisual(); };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { if (!ReadOnly) Focus(); e.Handled = true; }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (ReadOnly) return;
        // Full-width digits, as a Chinese input method types them, count as the same digits.
        Add(e.Text.Select(c => c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ReadOnly) return;
        if (e.Key == Key.Back) { if (_text.Length > 0) { _text = _text[..^1]; Changed(); } e.Handled = true; }
        else if (e.Key == Key.Delete) { if (_text.Length > 0) { _text = ""; Changed(); } e.Handled = true; }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control || e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            try
            {
                string pasted = Clipboard.GetText();
                // Only a text that is a code: six digits, with nothing but spaces or dashes between them.
                if (pasted.Length <= 32 && pasted.All(c => char.IsAsciiDigit(c) || c is ' ' or '-' or '\r' or '\n' or '\t') && pasted.Count(char.IsAsciiDigit) == Digits)
                { _text = ""; Add(pasted); }
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _text.Length == Digits) { Completed?.Invoke(_text); e.Handled = true; }
    }

    private void Add(IEnumerable<char> typed)
    {
        string before = _text;
        foreach (char c in typed) if (char.IsAsciiDigit(c) && _text.Length < Digits) _text += c;
        if (_text == before) return;
        Changed();
        if (_text.Length == Digits) Completed?.Invoke(_text);
    }
    private void Changed() { InvalidateVisual(); Edited?.Invoke(); }

    protected override void OnRender(DrawingContext drawing)
    {
        Brush Resource(string key, string fallback) => TryFindResource(key) as Brush ?? UI.Brush(fallback);
        // A code to read is a row of plain tiles; one to type has the frame of an input.
        var fill = ReadOnly ? Resource("SettingsFillHover", "#14FFFFFF") : Resource("SettingsInput", "#1FFFFFFF");
        var border = new Pen(Resource("SettingsInputBorder", "#33FFFFFF"), 1);
        var active = new Pen(Resource("SettingsAccent", "#0A84FF"), 1.5);
        var ink = Resource("SettingsTextPrimary", "#F2F2F4");
        var typeface = new Typeface(TextElement_FontFamily(), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // The whole control takes the click, between the cells too.
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        for (int i = 0; i < Digits; i++)
        {
            double x = i * (CellWidth + Gap) + (i >= Digits / 2 ? GroupGap - Gap : 0);
            bool current = !ReadOnly && IsKeyboardFocused && i == Math.Min(_text.Length, Digits - 1);
            double inset = current ? 0.75 : 0.5;
            drawing.DrawRoundedRectangle(fill, ReadOnly ? null : current ? active : border, new Rect(x + inset, inset, CellWidth - 2 * inset, CellHeight - 2 * inset), Radius, Radius);
            if (i >= _text.Length) continue;
            var digit = new FormattedText(_text[i].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 24, ink, dpi);
            // Centred on the digit's own ink: its width across, and the height of a capital above the baseline.
            double capital = typeface.CapsHeight * 24;
            drawing.DrawText(digit, new Point(x + (CellWidth - digit.Width) / 2, (CellHeight - capital) / 2 - (digit.Baseline - capital)));
        }
    }
    private FontFamily TextElement_FontFamily() => (FontFamily)GetValue(System.Windows.Documents.TextElement.FontFamilyProperty);

    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, ReadOnly ? AutomationControlType.Text : AutomationControlType.Edit,
        () => _text.Length > 0 ? Label + " " + string.Join(" ", _text.ToCharArray()) : Label);
}
