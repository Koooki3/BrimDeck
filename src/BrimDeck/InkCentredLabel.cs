using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace BrimDeck;

// The text of a settings button, centred top to bottom in the whole button by its letters as drawn (InkCentredText.InkSpan),
// whatever the font and display scale. A button's text is centred by its line box, whose Chinese sits low; a fixed lift of
// 1 DIP, measured once at 100%, put "检查更新" 1 to 2.5 device pixels high at 150% and 175%. The button's own padding and border
// are rounded to device pixels apart from the text, so the letters are placed against the button's box, measured after
// layout, not against the slot the padding leaves.
//
// The settings buttons' templates show text content through it (Selector); other content keeps the usual presenter. The text
// takes the button's font, size, weight and colour.
internal sealed class InkCentredLabel : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(InkCentredLabel),
        new FrameworkPropertyMetadata("", (d, e) => { var label = (InkCentredLabel)d; label._text.Text = (string?)e.NewValue ?? ""; label.InvalidateMeasure(); }));

    // For a ContentPresenter's ContentTemplateSelector: text content is shown through this label, anything else as usual.
    public static DataTemplateSelector Selector { get; } = new TextSelector();

    private readonly TextBlock _text = new();
    private double _shift, _pad;
    // The button's height in pixels before the label asked for one more, until the next layout shows whether it grew.
    private int? _padFrom;
    private bool _noPad;
    private (string Text, string Font, double Size, FontWeight Weight, TextFormattingMode Mode, double Pixels)? _inkKey;
    private (double Top, double Bottom) _ink;

    public InkCentredLabel()
    {
        AddVisualChild(_text);
        AddLogicalChild(_text);
        Loaded += (_, _) => { LayoutUpdated -= Recentre; LayoutUpdated += Recentre; };
        Unloaded += (_, _) => LayoutUpdated -= Recentre;
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _text;
    protected override IEnumerator LogicalChildren => new object[] { _text }.GetEnumerator();

    protected override Size MeasureOverride(Size available)
    {
        _text.Measure(available);
        return new Size(_text.DesiredSize.Width, _text.DesiredSize.Height + _pad);
    }

    protected override Size ArrangeOverride(Size size)
    {
        _text.Arrange(new Rect(0, _shift, size.Width, _text.DesiredSize.Height));
        return size;
    }

    // After each layout: where the letters would sit in the button, and the whole device pixels that move them to its middle.
    // Moving them asks for one more arrange, after which the shift comes out the same.
    //
    // Where the room the letters leave in the button is an odd number of pixels, no whole-pixel place centres them: at 100%,
    // "检查更新" left 8 pixels above and 9 below in a 28-pixel button. Then the label asks for one pixel more (_pad), so the
    // button grows to 29 and the letters get 9 and 9. A button whose height does not follow its text (a fixed Height, or
    // stretched by a taller neighbour) does not grow; the label then stops asking until its text, font or scale changes.
    private void Recentre(object? sender, EventArgs e)
    {
        if (!IsVisible || FindButton() is not { ActualHeight: > 0 } button) return;
        double pixels = UI.PixelsPerDip(this);
        if (pixels <= 0) return;
        var (inkTop, inkBottom) = Ink(pixels);
        int height = (int)Math.Round(button.ActualHeight * pixels), pad = (int)Math.Round(_pad * pixels);
        if (_padFrom is int from)
        {
            _padFrom = null;
            if (height <= from) { _noPad = true; SetPad(0, pixels); return; }
        }
        double ink = (inkBottom - inkTop) * pixels;
        double Off(int box) { double side = (box - ink) / 2; return Math.Abs(side - Math.Round(side)); }
        int natural = height - pad, want = !_noPad && Off(natural + 1) < Off(natural) - .05 ? 1 : 0;
        if (want != pad) { _padFrom = want > 0 ? natural : null; SetPad(want, pixels); return; }
        double top = TransformToAncestor(button).Transform(new Point(0, 0)).Y;
        double shift = Math.Round(((button.ActualHeight - (inkBottom - inkTop)) / 2 - inkTop - top) * pixels) / pixels;
        if (Math.Abs(shift - _shift) < 1e-6) return;
        _shift = shift;
        InvalidateArrange();
    }

    private void SetPad(int pixelsMore, double pixels) { _pad = pixelsMore / pixels; InvalidateMeasure(); }

    private ButtonBase? FindButton()
    {
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase button) return button;
        return null;
    }

    // The letters' span is measured once for each text, font and scale, not at every layout of the window.
    private (double Top, double Bottom) Ink(double pixels)
    {
        var key = (_text.Text, _text.FontFamily.Source, _text.FontSize, _text.FontWeight, TextOptions.GetTextFormattingMode(_text), pixels);
        if (_inkKey != key) { _ink = InkCentredText.InkSpan(_text, pixels); _inkKey = key; _noPad = false; _padFrom = null; }
        return _ink;
    }

    private sealed class TextSelector : DataTemplateSelector
    {
        private static readonly DataTemplate Label = Make();

        private static DataTemplate Make()
        {
            var label = new FrameworkElementFactory(typeof(InkCentredLabel));
            label.SetBinding(TextProperty, new Binding());
            var template = new DataTemplate { VisualTree = label };
            template.Seal();
            return template;
        }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is string ? Label : null;
    }
}
