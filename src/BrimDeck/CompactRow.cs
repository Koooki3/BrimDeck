using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The parts of the collapsed row, left to right.
internal enum CompactPart { Cover, Rings, Text, Monitor, Equalizer, Clock }

// The collapsed row as CompactMusicLayout chose it: every part whole, with one gap between neighbours. The title takes the
// space left over; without a title that space goes to the middle while media shows (cover at the left, equalizer and clock at
// the right, the rings centred between), and otherwise the parts stand together in the centre.
// Parts come and go as the system readings do (MonitorStrip): a part that no longer fits, or that the content no longer has,
// fades and shrinks out of its place while its neighbours close up, and one that arrives grows into its place the same way.
// Nothing is clipped on the way. The row is built again whenever its width or content changes; each new row takes over the
// previous one's parts that are still leaving and how far every part has come, so a drag on the surface's end eases parts in
// and out instead of switching them. A change of what the row shows (the settings, the style, the height) starts afresh.
internal sealed class CompactRow : Panel
{
    private static readonly CompactPart[] Order = Enum.GetValues<CompactPart>();
    private sealed class Slot(FrameworkElement element, double width, bool shown)
    {
        public readonly FrameworkElement Element = element;
        // The part's width in the row when fully present.
        public readonly double Width = width;
        public readonly bool Shown = shown;
        public Eased Presence;
        public ScaleTransform? Zoom;
    }
    private readonly Dictionary<CompactPart, Slot> _slots = [];
    private readonly double _gap, _dpi;
    private MonitorStrip? _strip;
    // What the row shows; rows with the same key carry their parts over.
    public object Key { get; }

    public CompactRow(object key, double gap, double dpi)
    {
        Key = key; _gap = Math.Max(0, gap); _dpi = dpi > 0 && double.IsFinite(dpi) ? dpi : 1;
        VerticalAlignment = VerticalAlignment.Center; HorizontalAlignment = HorizontalAlignment.Left;
    }

    // A part the layout shows, width DIPs wide when fully present (the title's and the readings' widths follow the row).
    // A part the previous row already had keeps its presence; a new one starts from nothing when motion is on.
    public void Show(CompactPart part, FrameworkElement element, double width, CompactRow? previous, bool motion)
    {
        var slot = new Slot(element, Math.Max(0, width), true);
        slot.Presence = previous is not null && previous._slots.TryGetValue(part, out var before) ? before.Presence : new Eased(previous is null || !motion ? 1 : 0);
        slot.Presence.Set(1, motion);
        if (part == CompactPart.Monitor) _strip = element as MonitorStrip;
        _slots[part] = slot; Children.Add(element);
    }

    // The previous row's parts that this one no longer shows leave from where they are: the same elements, easing out.
    public void Carry(CompactRow? previous, bool motion)
    {
        if (previous is null || !motion) return;
        double now = Eased.Now;
        foreach (var part in Order)
        {
            // The readings fade inside their own strip, which the new row has whenever any is still present.
            if (part == CompactPart.Monitor || _slots.ContainsKey(part) || !previous._slots.TryGetValue(part, out var old)) continue;
            if (old.Presence.At(now) <= 0) continue;
            previous.Children.Remove(old.Element);
            var slot = new Slot(old.Element, old.Width, false) { Presence = old.Presence, Zoom = old.Zoom };
            slot.Presence.Set(0, true);
            _slots[part] = slot; Children.Add(old.Element);
        }
    }

    // Starts the row's motion if anything is still coming or going.
    public void Begin()
    {
        if (Moving(Eased.Now)) Motion.Start(this, Step);
    }
    private bool Moving(double now) => _slots.Values.Any(slot => slot.Presence.Moving(now)) || _strip?.Moving(now) == true;
    private bool Step(double now)
    {
        // A row that was replaced stops; the new one carries on.
        if (VisualTreeHelper.GetParent(this) is null) return false;
        InvalidateArrange();
        if (Moving(now)) return true;
        // Parts that have left are dropped.
        foreach (var (part, slot) in _slots.Where(pair => !pair.Value.Shown).ToList()) { Children.Remove(slot.Element); _slots.Remove(part); }
        return false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double now = Eased.Now, width = finalSize.Width, height = finalSize.Height;
        int n = Order.Length;
        Span<double> room = stackalloc double[n], presence = stackalloc double[n];
        Span<bool> has = stackalloc bool[n];
        // Each part takes its width and the gap after it in proportion to how present it is, so the row closes up smoothly.
        double total = 0, last = 0;
        int text = (int)CompactPart.Text;
        for (int i = 0; i < n; i++)
        {
            if (!_slots.TryGetValue(Order[i], out var slot)) continue;
            double p, r;
            if (Order[i] == CompactPart.Monitor && _strip is { } strip) { p = strip.Presence(now); r = strip.DrawnWidth(now); }
            else { p = Math.Clamp(slot.Presence.At(now), 0, 1); r = i == text && slot.Shown ? 0 : slot.Width * p; }
            if (p <= 0 && r <= 0)
            {
                // Gone (or not yet arrived): arranged out of the way, unseen.
                if (Order[i] != CompactPart.Monitor) slot.Element.Opacity = 0;
                slot.Element.Arrange(new Rect(0, 0, slot.Element.DesiredSize.Width, slot.Element.DesiredSize.Height));
                continue;
            }
            has[i] = true; presence[i] = p; room[i] = r;
            total += r + _gap * p; last = p;
        }
        total -= _gap * last;
        double free = Math.Max(0, width - total);
        // A title that shows takes the space that is left.
        if (has[text] && _slots[CompactPart.Text].Shown) { room[text] = free; free = 0; }
        // While media shows, the free space lies in the middle, around the rings; otherwise the parts stand together, centred.
        // The two blend as the cover and the equalizer come and go, so nothing jumps.
        double media = Math.Max(presence[(int)CompactPart.Cover], presence[(int)CompactPart.Equalizer]);
        bool rings = has[(int)CompactPart.Rings];
        double x = (1 - media) * free / 2;
        bool middle = false;
        for (int i = 0; i < n; i++)
        {
            if (!has[i]) continue;
            var part = Order[i];
            if (part == CompactPart.Rings) x += media * free / 2;
            if (part > CompactPart.Rings && !middle) { x += media * free * (rings ? .5 : 1); middle = true; }
            var slot = _slots[part];
            var element = slot.Element;
            // The title and the readings fill their room; the other parts keep their size and stand in the middle of their room.
            bool fills = part is CompactPart.Text or CompactPart.Monitor;
            double logical = fills ? room[i] : slot.Width;
            double childWidth = fills ? room[i] : element.DesiredSize.Width, childHeight = element.DesiredSize.Height;
            double left = x + (room[i] - logical) / 2;
            element.Arrange(new Rect(Snap(left), Snap((height - childHeight) / 2), Math.Max(0, childWidth), Math.Max(0, childHeight)));
            // The readings fade inside their strip; every other part is drawn smaller and fainter while it comes or goes.
            if (part != CompactPart.Monitor) Fade(slot, presence[i], logical, childHeight);
            x += room[i] + _gap * presence[i];
        }
        // A reading that starts to come or go during this arrange moves the row with it.
        if (_strip?.Moving(now) == true) Motion.Start(this, Step);
        return finalSize;
    }

    private static void Fade(Slot slot, double p, double width, double height)
    {
        var element = slot.Element;
        if (p >= .999)
        {
            if (element.Opacity != 1) element.Opacity = 1;
            if (!ReferenceEquals(element.RenderTransform, Transform.Identity)) element.RenderTransform = Transform.Identity;
            return;
        }
        double zoom = .82 + .18 * p;
        element.Opacity = p * p;
        var scale = slot.Zoom ??= new ScaleTransform();
        scale.ScaleX = scale.ScaleY = zoom; scale.CenterX = width / 2; scale.CenterY = height / 2;
        if (!ReferenceEquals(element.RenderTransform, scale)) element.RenderTransform = scale;
    }

    private double Snap(double value) => double.IsFinite(value) ? Math.Round(value * _dpi, MidpointRounding.AwayFromZero) / _dpi : 0;
}
