using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The parts of the collapsed row, left to right.
internal enum CompactPart { Cover, Rings, Text, Monitor, Equalizer, Clock }

// The collapsed row as CompactMusicLayout chose it: every part whole, every two neighbours the same gap apart, the system
// readings included (CompactSpacing). Width the parts leave over goes to the title up to the width of its text, then to every
// gap alike up to twice the unit, then to a title still wider than its room; what is left stands equally at both ends.
// Parts come and go as the system readings do (MonitorStrip): a part that no longer fits, or that the content no longer has,
// fades and shrinks out of its place while its neighbours close up, and one that arrives grows into its place the same way.
// Nothing is clipped on the way. The row is built again whenever its width or content changes; each new row takes over the
// previous one's parts that are still leaving and how far every part has come, so a drag on the surface's end eases parts in
// and out instead of switching them. A change of what the row shows (the settings, the style, the height) starts afresh.
// A new line of the title eases its room to the new text's width, and a reading whose figure widens or narrows eases its own.
internal sealed class CompactRow : Panel
{
    private static readonly CompactPart[] Order = Enum.GetValues<CompactPart>();
    private sealed class Slot(FrameworkElement element, double width, bool shown)
    {
        public readonly FrameworkElement Element = element;
        // The part's width in the row when fully present.
        public readonly double Width = width;
        public readonly bool Shown = shown;
        // The room it last had; a title on its way out keeps it.
        public double Room = width;
        public Eased Presence;
        public ScaleTransform? Zoom;
    }
    private readonly Dictionary<CompactPart, Slot> _slots = [];
    private readonly double _gap, _dpi, _scale;
    private MonitorStrip? _strip;
    // The width the title's room eases to (its text's width, held a while when a shorter line follows a longer one, so lyrics do
    // not move the row at every line), carried from row to row.
    private Eased _title;
    private HeldWidth _titleHeld;
    private bool _titleKnown;
    // What the row shows; rows with the same key carry their parts over.
    public object Key { get; }

    public CompactRow(object key, double gap, double dpi, double scale)
    {
        Key = key; _gap = Math.Max(0, gap); _dpi = dpi > 0 && double.IsFinite(dpi) ? dpi : 1; _scale = CompactMusicLayout.SafeScale(scale);
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
        if (part == CompactPart.Text && previous is { _titleKnown: true }) { _title = previous._title; _titleHeld = previous._titleHeld; _titleKnown = true; }
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
            var slot = new Slot(old.Element, part == CompactPart.Text ? old.Room : old.Width, false) { Presence = old.Presence, Zoom = old.Zoom };
            slot.Presence.Set(0, true);
            _slots[part] = slot; Children.Add(old.Element);
        }
    }

    // Starts the row's motion if anything is still coming or going.
    public void Begin()
    {
        if (Moving(Eased.Now)) Motion.Start(this, Step);
    }
    private bool Moving(double now) => _slots.Values.Any(slot => slot.Presence.Moving(now)) || _strip?.Moving(now) == true || _titleKnown && _title.Moving(now);
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
        // Every part of the row, the readings one by one, at its full width and how present it is (CompactSpacing).
        var parts = new List<CompactSpacing.Part>(Order.Length + 6);
        var owners = new List<CompactPart>(Order.Length + 6);
        int title = -1;
        double text = 0;
        foreach (var part in Order)
        {
            if (!_slots.TryGetValue(part, out var slot)) continue;
            if (part == CompactPart.Monitor && _strip is { } strip)
            {
                foreach (var (w, p) in strip.Readings(now)) { parts.Add(new(w, p)); owners.Add(part); }
                continue;
            }
            double presence = Math.Clamp(slot.Presence.At(now), 0, 1);
            if (presence <= 0)
            {
                // Gone (or not yet arrived): arranged out of the way, unseen.
                slot.Element.Opacity = 0;
                slot.Element.Arrange(new Rect(0, 0, slot.Element.DesiredSize.Width, slot.Element.DesiredSize.Height));
                continue;
            }
            if (part == CompactPart.Text && slot.Shown) { title = parts.Count; text = TitleWidth(slot, now); }
            parts.Add(new(part == CompactPart.Text ? slot.Room : slot.Width, presence)); owners.Add(part);
        }
        var placed = CompactSpacing.Arrange(parts, title, text, width, _gap, _scale, _dpi);
        double stripLeft = double.NaN, stripRight = 0;
        var offsets = new List<double>();
        for (int i = 0; i < parts.Count; i++)
        {
            var part = owners[i];
            double room = placed.Room[i], x = placed.Left[i], p = Math.Clamp(parts[i].Presence, 0, 1);
            if (part == CompactPart.Monitor)
            {
                if (double.IsNaN(stripLeft)) stripLeft = x;
                offsets.Add(x - stripLeft); stripRight = x + room;
                continue;
            }
            var slot = _slots[part];
            var element = slot.Element;
            // The title fills its room; the other parts keep their size and stand in the middle of their room.
            bool fills = part == CompactPart.Text;
            if (fills && slot.Shown) slot.Room = room;
            double logical = fills ? room : slot.Width;
            double childWidth = fills ? room : element.DesiredSize.Width, childHeight = element.DesiredSize.Height;
            double left = x + (room - logical) / 2;
            element.Arrange(new Rect(Snap(left), Snap((height - childHeight) / 2), Math.Max(0, childWidth), Math.Max(0, childHeight)));
            // Every part is drawn smaller and fainter while it comes or goes; the readings do so inside their strip.
            Fade(slot, p, logical, childHeight);
        }
        if (_strip is { } readings)
        {
            if (double.IsNaN(stripLeft)) readings.Arrange(new Rect(0, 0, 0, readings.DesiredSize.Height));
            else
            {
                readings.Place([.. offsets]);
                double h = readings.DesiredSize.Height;
                readings.Arrange(new Rect(Snap(stripLeft), Snap((height - h) / 2), Math.Max(0, stripRight - stripLeft), Math.Max(0, h)));
            }
        }
        // A reading that starts to come or go, or to change its width, during this arrange moves the row with it.
        if (_strip?.Moving(now) == true || _titleKnown && _title.Moving(now)) Motion.Start(this, Step);
        return finalSize;
    }

    // The width the title's text takes, easing to a new line's width so its neighbours glide to their new places.
    private double TitleWidth(Slot slot, double now)
    {
        double natural = slot.Element is MusicMarquee marquee ? marquee.TextWidth : slot.Room;
        if (!double.IsFinite(natural) || natural < 0) natural = 0;
        natural = _titleHeld.Update(Math.Ceiling(natural), now);
        if (!_titleKnown) { _title = new Eased(natural); _titleKnown = true; }
        else if (Math.Abs(_title.Target - natural) > .25) _title.Set(natural, Motion.Enabled, now);
        return _title.At(now);
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
