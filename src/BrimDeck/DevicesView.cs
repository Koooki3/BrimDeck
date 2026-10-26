using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The devices page: a card per connected wireless device, in the steady order of the roster (by kind, then name), laid out by
// DevicesLayout. Each card carries the device's icon, its name, a line under it (the earbuds' parts, charging, low, no battery
// reported, or where the reading comes from) and, at the right, a battery whose fill is the charge, with the figure after it.
// A low battery takes the collapsed readings' warning or critical color, an exclamation mark in the battery and the words, so
// color is never the only sign; a charging one a bolt. With more devices than the grid holds, the last card counts the rest.
// As the monitor page does, it draws itself in one element: a new reading changes what it draws, never the layout. Within a
// layout step a device that arrives fades in where it belongs while the others glide aside, one that leaves fades while they
// close up, and a battery's fill eases to each reading (a first reading grows from empty); figures change in place, never
// cross-faded. The empty page and the search before the first listing fade in and out. The page is built again (and
// crossfades, MonitorCrossfade) only when its layout step changes.
internal sealed class DevicesView : FrameworkElement
{
    private readonly DevicesLayout _layout;
    private readonly MonitorColors _colors;
    private readonly int _low;
    private sealed class Card
    {
        public required string Key;
        public DeviceBattery? Device;
        // The "+N" card: the devices it stands for.
        public IReadOnlyList<DeviceBattery> More = [];
        public Eased Presence, X, Y, Fill;
        public bool Placed, Leaving;
        // The texts as last laid out, kept while their text, width and color stay the same.
        public (string Text, double Width, string Color, double Size) NameKey, DetailKey;
        public FormattedText? Name, Detail;
    }
    private const string MoreKey = "\u0001more";
    private readonly List<Card> _cards = [];
    private bool _settled, _updated;
    private Eased _empty, _looking;
    private string? _hovered;
    // One tooltip, whose content follows the card under the pointer and its readings while it is open.
    private readonly System.Windows.Controls.ToolTip _tip = new();

    public DevicesView(DevicesLayout layout, MonitorColors colors, int low)
    {
        _layout = layout; _colors = colors; _low = low;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        MouseMove += (_, e) => Hover(e.GetPosition(this));
        MouseLeave += (_, _) => { _hovered = null; RefreshToolTip(); };
        SizeChanged += (_, _) => Place(motion: false);
    }

    private IEnumerable<Card> Shown => _cards.Where(card => !card.Leaving);

    // The devices to show (the roster without hidden ones); settled: the sources have listed what is connected, so an empty list
    // means there are none. The first call (the page being built) shows them in place; later ones move.
    public void Update(IReadOnlyList<DeviceBattery> devices, bool settled, bool motion)
    {
        int room = _layout.Room;
        var shown = devices.Count > room ? devices.Take(room - 1).ToList() : devices.ToList();
        var more = devices.Count > room ? devices.Skip(room - 1).ToList() : [];
        var current = Shown.ToList();
        // Asked again with what it shows (a resize within the step, a settings change elsewhere): nothing to do.
        if (_updated && settled == _settled && current.Count == shown.Count + (more.Count > 0 ? 1 : 0)
            && shown.All(device => current.Any(card => card.Device?.Same(device) == true))
            && (more.Count == 0 || current.Any(card => card.Key == MoreKey && card.More.Count == more.Count && card.More.Zip(more).All(pair => pair.First.Same(pair.Second)))))
            return;
        bool animate = motion && _updated;
        _updated = true; _settled = settled;
        double now = Eased.Now;
        var keys = shown.Select(device => device.Key).ToHashSet();
        if (more.Count > 0) keys.Add(MoreKey);
        foreach (var card in _cards) if (!card.Leaving && !keys.Contains(card.Key)) { card.Leaving = true; card.Presence.Set(0, animate, now); }
        foreach (var device in shown) Take(device.Key, now, animate).Device = device;
        if (more.Count > 0) Take(MoreKey, now, animate).More = more;
        foreach (var card in Shown) if (card.Device is { } device) card.Fill.Set((device.Lowest ?? 0) / 100.0, animate, now);
        // The search and the empty page never show together: the search goes at once when the list settles, and the empty page
        // fades in (OnRender holds it back while the last card fades out).
        _looking.Set(!settled && shown.Count == 0 ? 1 : 0, animate && !settled, now);
        _empty.Set(settled && shown.Count == 0 ? 1 : 0, animate, now);
        // Without motion a leaving card is gone at once.
        if (!animate) _cards.RemoveAll(card => card.Leaving);
        // Leaving cards first, so they are painted under the cards gliding into their places.
        _cards.Sort((a, b) => a.Leaving != b.Leaving ? b.Leaving.CompareTo(a.Leaving) : Order(a, b));
        Place(animate);
        if (_hovered is not null) RefreshToolTip();
    }

    // The card for a key: the one shown (or still leaving, which comes back), or a new one that fades in.
    private Card Take(string key, double now, bool animate)
    {
        var card = _cards.FirstOrDefault(card => card.Key == key);
        if (card is null)
        {
            card = new Card { Key = key, Presence = new Eased(animate ? 0 : 1, Eased.Seconds * .8), Fill = new Eased(0) };
            _cards.Add(card);
        }
        card.Leaving = false;
        card.Presence.Set(1, animate, now);
        return card;
    }
    // Shown cards in the roster's order, the "+N" card last.
    private static int Order(Card a, Card b) => a.Device is null ? 1 : b.Device is null ? -1 : Devices.Order.Compare(a.Device, b.Device);

    // The shown cards' places; a card that arrives takes its place at once (it fades in there), the others glide to theirs. A
    // card leaving keeps its place while it fades.
    private void Place(bool motion)
    {
        double w = ActualWidth, h = ActualHeight, now = Eased.Now;
        if (w > 0 && h > 0)
        {
            var shown = Shown.ToList();
            for (int i = 0; i < shown.Count; i++)
            {
                var card = shown[i];
                var (x, y, _, _) = _layout.Card(i, w, h, shown.Count);
                if (!card.Placed) { card.X = new Eased(x, Eased.Seconds); card.Y = new Eased(y, Eased.Seconds); card.Placed = true; }
                else { card.X.Set(x, motion, now); card.Y.Set(y, motion, now); }
            }
        }
        if (Moving(now)) Motion.Start(this, Step);
        InvalidateVisual();
    }

    private bool Moving(double now) => _empty.Moving(now) || _looking.Moving(now)
        || _cards.Any(card => card.Presence.Moving(now) || card.X.Moving(now) || card.Y.Moving(now) || card.Fill.Moving(now));

    private bool Step(double now)
    {
        InvalidateVisual();
        if (Moving(now)) return true;
        _cards.RemoveAll(card => card.Leaving);
        return false;
    }

    // The card drawn under the pointer, where it is drawn now (cards glide for a moment after a device comes or goes); the last
    // drawn is on top.
    private void Hover(Point at)
    {
        var (cw, ch) = _layout.CardSize(ActualWidth, ActualHeight);
        double now = Eased.Now;
        string? found = null;
        for (int i = _cards.Count - 1; i >= 0; i--)
        {
            var card = _cards[i];
            if (card.Leaving || !card.Placed) continue;
            if (new Rect(card.X.At(now), card.Y.At(now), cw, ch).Contains(at)) { found = card.Key; break; }
        }
        if (found == _hovered) return;
        _hovered = found;
        RefreshToolTip();
    }
    // The hovered card's tooltip, by its device's key, so it always names the device under the pointer and follows its readings.
    private void RefreshToolTip()
    {
        var card = _cards.FirstOrDefault(card => card.Key == _hovered && !card.Leaving);
        string? text = card is null ? null : card.Device is { } device ? Devices.Describe(device) : string.Join("\n\n", card.More.Select(Devices.Describe));
        if (text is null) { ToolTip = null; return; }
        if (!Equals(_tip.Content, text)) _tip.Content = text;
        if (!ReferenceEquals(ToolTip, _tip)) ToolTip = _tip;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, h = ActualHeight, now = Eased.Now, ppd = MonitorDraw.PixelsPerDip(this);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double cards = _cards.Count == 0 ? 0 : _cards.Max(card => Math.Clamp(card.Presence.At(now), 0, 1));
        Message(drawing, w, h, ppd, Math.Clamp(_empty.At(now), 0, 1) * (1 - cards), Loc.T("没有已连接的无线设备", "No wireless devices connected"),
            Loc.T("连接蓝牙耳机、键鼠、手柄，或插上罗技等接收器后会显示在这里", "Bluetooth headphones, keyboards, mice and controllers, and receivers such as Logitech's, show here once connected"));
        Message(drawing, w, h, ppd, Math.Clamp(_looking.At(now), 0, 1), Loc.T("正在查找设备…", "Looking for devices…"), null);
        var (cardWidth, cardHeight) = _layout.CardSize(w, h);
        foreach (var card in _cards)
        {
            double presence = Math.Clamp(card.Presence.At(now), 0, 1);
            if (presence <= .001 || !card.Placed) continue;
            double x = Math.Round(card.X.At(now)), y = Math.Round(card.Y.At(now) + (1 - presence) * 6);
            bool partial = presence < .999;
            if (partial) drawing.PushOpacity(presence * presence);
            DrawCard(drawing, card, new Rect(x, y, cardWidth, cardHeight), now, ppd);
            if (partial) drawing.Pop();
        }
    }

    private ((string, double, double, string) Key, FormattedText Text)? _hint;

    // A centred title and, under it, a hint of up to two lines, at the given opacity.
    private void Message(DrawingContext drawing, double w, double h, double ppd, double opacity, string title, string? hint)
    {
        if (opacity <= .001) return;
        var heading = MonitorDraw.Text(title, _layout.Body + 1, UI.Secondary, ppd);
        FormattedText? lines = null;
        if (hint is not null)
        {
            // Laid out once per text, width and scale: it is drawn on every frame of a fade.
            var key = (hint, Math.Round(Math.Max(40, Math.Min(w - 24, 420))), ppd, UI.Tertiary);
            if (_hint is not { } known || known.Key != key)
                _hint = (key, new FormattedText(hint, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(UI.PanelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), _layout.Caption, UI.Brush(UI.Tertiary), ppd)
                { MaxTextWidth = key.Item2, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center });
            lines = _hint is { } made ? made.Text : null;
        }
        double total = heading.Height + (lines is null ? 0 : 4 + lines.Height), top = Math.Round((h - total) / 2);
        if (opacity < .999) drawing.PushOpacity(opacity);
        drawing.DrawText(heading, new Point(Math.Round((w - heading.WidthIncludingTrailingWhitespace) / 2), top));
        if (lines is not null) drawing.DrawText(lines, new Point(Math.Round((w - lines.MaxTextWidth) / 2), top + heading.Height + 4));
        if (opacity < .999) drawing.Pop();
    }

    private void DrawCard(DrawingContext drawing, Card card, Rect r, double now, double ppd)
    {
        bool high = SystemParameters.HighContrast;
        double radius = PanelTheme.Corner(8);
        drawing.DrawRoundedRectangle(high ? null : UI.Brush(UI.Fill), high ? DeviceGlyphs.Stroke(UI.Outline, 1) : null, r, radius, radius);
        double pad = Math.Round(Math.Clamp(r.Height * .22, 8, 14)), icon = Math.Round(Math.Clamp(r.Height * .4, 16, 22)), middle = r.Y + r.Height / 2;
        double figureSize = _layout.Figure, right = r.Right - pad, baseline = Math.Round(middle + figureSize * .36);
        // Figures as the monitor page sets them (FigureGauge): the number in the display face, the unit small after it.
        var unit = MonitorDraw.Text("%", _layout.Caption, UI.Secondary, ppd);
        double widest = MonitorDraw.Text("100", figureSize, UI.Primary, ppd, display: true).WidthIncludingTrailingWhitespace + 2 + unit.WidthIncludingTrailingWhitespace;
        double textX = r.X + pad + icon + Math.Round(pad * .8);

        if (card.Device is not { } device)
        {
            // The "+N" card: an ellipsis, what it is, and the count where a figure stands.
            DeviceGlyphs.More(drawing, new Rect(r.X + pad, Math.Round(middle - icon / 2), icon, icon), UI.Secondary);
            var count = MonitorDraw.Text("+" + card.More.Count, figureSize, UI.Primary, ppd, display: true);
            MonitorDraw.Baseline(drawing, count, right - count.WidthIncludingTrailingWhitespace, baseline);
            var (label, _) = Texts(card, Loc.T("更多设备", "More devices"), null, Math.Max(10, right - widest - 8 - textX), UI.Secondary, UI.Tertiary, ppd);
            drawing.DrawText(label, new Point(textX, Math.Round(middle - label.Height / 2)));
            return;
        }

        var level = Devices.Level(device.Lowest, device.Charging, _low);
        // As text the level's color is moved to read at 4.5:1 on the theme (the warning amber is too light on light themes); in high
        // contrast the system's text color.
        string accent = high ? SystemColors.WindowTextColor.ToString() : TextColor(_colors.Level(level, UI.Secondary));
        DeviceGlyphs.Draw(drawing, device.Kind, new Rect(r.X + pad, Math.Round(middle - icon / 2), icon, icon), UI.Secondary);

        // The figure at the right, in a column as wide as "100%" so names line up from card to card, the battery before it.
        if (device.Lowest is { } percent)
        {
            var number = MonitorDraw.Text(Devices.Clamp(percent).ToString(System.Globalization.CultureInfo.InvariantCulture), figureSize,
                level == MemoryLevel.Normal ? UI.Primary : accent, ppd, display: true);
            double unitX = right - unit.WidthIncludingTrailingWhitespace;
            MonitorDraw.Baseline(drawing, unit, unitX, baseline);
            MonitorDraw.Baseline(drawing, number, unitX - 2 - number.WidthIncludingTrailingWhitespace, baseline);
        }
        else
        {
            var dash = MonitorDraw.Text("—", figureSize, UI.Tertiary, ppd, display: true);
            MonitorDraw.Baseline(drawing, dash, right - dash.WidthIncludingTrailingWhitespace, baseline);
        }
        double batteryHeight = Math.Round(Math.Clamp(figureSize * .68, 11, 14)), batteryWidth = Math.Round(batteryHeight * 1.9);
        double batteryX = Math.Round(right - widest - 6 - batteryWidth);
        Battery(drawing, new Rect(batteryX, Math.Round(middle - batteryHeight / 2), batteryWidth, batteryHeight), device.Lowest is null ? null : Math.Clamp(card.Fill.At(now), 0, 1),
            level == MemoryLevel.Normal ? _colors.Performance : _colors.Level(level, _colors.Performance), device.Charging, level != MemoryLevel.Normal, ppd);

        // The name and the line under it: on two lines where the card has room, else the state after the name on one line.
        double textWidth = Math.Max(10, batteryX - 8 - textX);
        // The detail is in the critical color only for a low battery; getting low says so in the text color, the figure carrying
        // the warning color (that color reads poorly as small text on light themes).
        string detailColor = level == MemoryLevel.Critical ? accent : UI.Tertiary;
        if (!_layout.Dense && r.Height >= 44)
        {
            var (name, under) = Texts(card, device.Name, Detail(device, level), textWidth, UI.Primary, detailColor, ppd, strong: true);
            double block = name.Height + 1 + (under?.Height ?? 0), top = Math.Round(middle - block / 2);
            drawing.DrawText(name, new Point(textX, top));
            if (under is not null) drawing.DrawText(under, new Point(textX, top + name.Height + 1));
        }
        else
        {
            // One line: the name alone, whole where it fits. The state is in the battery (a bolt, an exclamation mark, empty with a
            // dash) and the figure's color on every card alike, and in the tooltip in words.
            var (name, _) = Texts(card, device.Name, null, textWidth, UI.Primary, detailColor, ppd);
            drawing.DrawText(name, new Point(textX, Math.Round(middle - name.Height / 2)));
        }
    }

    // By theme (a color readable on a light theme is not on a dark one). The target is above 4.5:1 because it is measured on the
    // panel's surface and the cards' fill is a shade darker.
    private static readonly Dictionary<(int Theme, string Color), string> TextColors = [];
    private static string TextColor(string color)
    {
        var key = (PanelTheme.Generation, color);
        if (TextColors.TryGetValue(key, out var readable)) return readable;
        if (TextColors.Count > 32) TextColors.Clear();
        return TextColors[key] = PanelTheme.Readable(color, 5.2);
    }

    // The card's name and detail lines, cut to the width, laid out again only when their text, width or color changes.
    private (FormattedText Name, FormattedText? Detail) Texts(Card card, string name, string? detail, double width, string nameColor, string detailColor, double ppd, bool strong = false)
    {
        double size = strong ? _layout.Body + 1 : _layout.Body;
        var nameKey = (name, Math.Round(width), nameColor + (strong ? "!" : ""), size * ppd);
        if (card.Name is null || card.NameKey != nameKey) { card.Name = MonitorDraw.Trimmed(name, size, nameColor, width, ppd, strong); card.NameKey = nameKey; }
        if (detail is null) return (card.Name, null);
        var detailKey = (detail, Math.Round(width), detailColor, ppd);
        if (card.Detail is null || card.DetailKey != detailKey) { card.Detail = MonitorDraw.Trimmed(detail, _layout.Caption, detailColor, width, ppd); card.DetailKey = detailKey; }
        return (card.Name, card.Detail);
    }

    // The line under the name: the earbuds' parts, then charging, low or getting low, an estimate, or that the device reports no
    // battery; otherwise where the reading comes from.
    private static string Detail(DeviceBattery device, MemoryLevel level)
    {
        var parts = new List<string>();
        if (device.Parts.Count > 0) parts.Add(string.Join("  ", device.Parts.Select(part => Devices.PartName(part.Part) + " " + Devices.Figure(part.Percent))));
        if (device.Charging) parts.Add(Loc.T("充电中", "Charging"));
        else if (device.Lowest is null) parts.Add(Loc.T("未报告电量", "No battery reported"));
        else if (level == MemoryLevel.Critical) parts.Add(Loc.T("电量低", "Battery low"));
        else if (level == MemoryLevel.Elevated) parts.Add(Loc.T("电量偏低", "Getting low"));
        else if (device.Coarse) parts.Add(Loc.T("估算", "Estimated"));
        if (parts.Count == 0) parts.Add(Devices.SourceName(device.Source));
        return string.Join(" · ", parts);
    }

    // A battery on its side, on whole device pixels: the outline, its tip, the charge as a fill from the left, and an
    // exclamation mark when low. Charging, a bolt as tall as the battery takes the tip's place (its body that much shorter), as
    // in the collapsed row: over the fill a bolt this small does not read on every theme's colors. share null: no reading, the
    // outline alone in the faint text color. In high contrast the fill takes the system's highlight and the marks its text color.
    private static void Battery(DrawingContext drawing, Rect r, double? share, string color, bool charging, bool low, double ppd)
    {
        bool high = SystemParameters.HighContrast;
        double Snap(double value) => Math.Round(value * ppd) / ppd;
        double stroke = Math.Max(1, Math.Round(1.2 * ppd)) / ppd, half = stroke / 2;
        double tip = charging ? Snap(r.Height * 15 / 23) : Snap(Math.Max(1.5, r.Height * .18)), radius = PanelTheme.Corner(Math.Max(1.5, r.Height * .22));
        var body = new Rect(Snap(r.X) + half, Snap(r.Y) + half, Snap(r.Width - tip - 1) - stroke, Snap(r.Height) - stroke);
        string ink = share is null ? UI.Tertiary : UI.Secondary, mark = high ? SystemColors.WindowTextColor.ToString() : UI.Primary;
        drawing.DrawRoundedRectangle(null, DeviceGlyphs.Stroke(ink, stroke), body, radius, radius);
        double tipX = Snap(body.Right + half + 1 / ppd);
        if (charging) DeviceGlyphs.Bolt(drawing, new Rect(tipX, Snap(r.Y), tip, Snap(r.Height)), share is null ? ink : mark);
        else drawing.DrawRectangle(UI.Brush(ink), null, new Rect(tipX, Snap(r.Y + r.Height * .3), tip, Snap(r.Height * .4)));
        if (share is not { } s) return;
        var inner = new Rect(body.X + half + 1 / ppd, body.Y + half + 1 / ppd, Math.Max(0, body.Width - stroke - 2 / ppd), Math.Max(0, body.Height - stroke - 2 / ppd));
        double width = Snap(inner.Width * s);
        if (width > 0) drawing.DrawRoundedRectangle(high ? SystemColors.HighlightBrush : UI.Brush(color), null, new Rect(inner.X, inner.Y, Math.Max(inner.Height * .5, width), inner.Height), radius * .5, radius * .5);
        if (charging || !low) return;
        // The mark sits inside the outline, set off from the fill by a pixel of the card's own fill (the window's in high contrast,
        // where cards have none), so it never cuts the outline.
        var inside = new Rect(body.X + half, body.Y + half, Math.Max(0, body.Width - stroke), Math.Max(0, body.Height - stroke));
        drawing.PushClip(new RectangleGeometry(inside));
        DeviceGlyphs.Exclamation(drawing, inside, mark, high ? SystemColors.WindowColor.ToString() : UI.Fill, ppd);
        drawing.Pop();
    }
}

// The devices' icons on a 24 unit grid, stroked as the page switch's icons are, and the marks drawn inside the battery.
internal static class DeviceGlyphs
{
    private static readonly Dictionary<DeviceKind, Geometry> Icons = new()
    {
        [DeviceKind.Mouse] = Frozen("M12,3 C8.1,3 6,5.6 6,9.5 V14.5 C6,18.4 8.6,21 12,21 C15.4,21 18,18.4 18,14.5 V9.5 C18,5.6 15.9,3 12,3 Z M12,3 V10 M6,10 H18"),
        [DeviceKind.Keyboard] = Frozen("M3.5,6.5 H20.5 A1.5,1.5 0 0 1 22,8 V16 A1.5,1.5 0 0 1 20.5,17.5 H3.5 A1.5,1.5 0 0 1 2,16 V8 A1.5,1.5 0 0 1 3.5,6.5 Z M6,10 H6.01 M9.3,10 H9.31 M12.6,10 H12.61 M15.9,10 H15.91 M18.2,10 H18.21 M7.5,14 H16.5"),
        [DeviceKind.Headphones] = Frozen("M4,15 V12 A8,8 0 0 1 20,12 V15 M4,14.5 H6.5 A1,1 0 0 1 7.5,15.5 V19 A1,1 0 0 1 6.5,20 H5 A1,1 0 0 1 4,19 Z M20,14.5 H17.5 A1,1 0 0 0 16.5,15.5 V19 A1,1 0 0 0 17.5,20 H19 A1,1 0 0 0 20,19 Z"),
        [DeviceKind.Earbuds] = Frozen("M7.5,4.5 A3,3 0 1 1 7.5,10.5 A3,3 0 1 1 7.5,4.5 Z M9.2,9.9 L10,19.5 M16.5,4.5 A3,3 0 1 0 16.5,10.5 A3,3 0 1 0 16.5,4.5 Z M14.8,9.9 L14,19.5"),
        [DeviceKind.Controller] = Frozen("M7.5,7 H16.5 C19.5,7 21.5,9.5 21.8,13 L22.2,16.5 C22.4,18.5 20,19.6 18.6,18 L16.5,15.6 H7.5 L5.4,18 C4,19.6 1.6,18.5 1.8,16.5 L2.2,13 C2.5,9.5 4.5,7 7.5,7 Z M7,10 V13 M5.5,11.5 H8.5 M16,10.6 H16.01 M17.6,12.4 H17.61"),
        [DeviceKind.Pen] = Frozen("M16.5,3.5 L20.5,7.5 L8.5,19.5 L3.5,20.5 L4.5,15.5 Z M14.5,5.5 L18.5,9.5"),
        [DeviceKind.Speaker] = Frozen("M7,3 H17 A1.5,1.5 0 0 1 18.5,4.5 V19.5 A1.5,1.5 0 0 1 17,21 H7 A1.5,1.5 0 0 1 5.5,19.5 V4.5 A1.5,1.5 0 0 1 7,3 Z M12,10.5 A3.5,3.5 0 1 1 12,17.5 A3.5,3.5 0 1 1 12,10.5 Z M12,6.5 H12.01"),
        [DeviceKind.Other] = Frozen("M12,3 L20,7.5 V16.5 L12,21 L4,16.5 V7.5 Z M12,12 L20,7.5 M12,12 L4,7.5 M12,12 V21"),
    };
    private static readonly Geometry BoltShape = Frozen("M13.5,0.5 L4.5,13.5 H11 L10,23.5 L19.5,9.5 H13 Z");
    private static readonly Geometry MoreShape = Frozen("M5.5,12 H5.51 M12,12 H12.01 M18.5,12 H18.51");
    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    // Round-capped pens by color and thickness, made once: a card redraws on every frame of a move.
    private static readonly Dictionary<(string Color, double Thickness), Pen> Pens = [];
    public static Pen Stroke(string color, double thickness)
    {
        if (Pens.TryGetValue((color, thickness), out var pen)) return pen;
        pen = new Pen(UI.Brush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        if (Pens.Count > 64) Pens.Clear();
        return Pens[(color, thickness)] = pen;
    }

    // Transforms that place a glyph, by their matrix, made once.
    private static readonly Dictionary<Matrix, Transform> Places = [];
    private static Transform Placed(double scale, double x, double y)
    {
        var matrix = new Matrix(scale, 0, 0, scale, x, y);
        if (Places.TryGetValue(matrix, out var known)) return known;
        var transform = new MatrixTransform(matrix); transform.Freeze();
        if (Places.Count > 128) Places.Clear();
        return Places[matrix] = transform;
    }

    public static Geometry Icon(DeviceKind kind) => Icons.TryGetValue(kind, out var icon) ? icon : Icons[DeviceKind.Other];

    public static void Draw(DrawingContext drawing, DeviceKind kind, Rect at, string color)
    {
        drawing.PushTransform(Placed(at.Width / 24, at.X, at.Y));
        drawing.DrawGeometry(null, Stroke(color, 1.65), Icon(kind));
        drawing.Pop();
    }

    // The "+N" card's mark: three dots where a device's icon stands.
    public static void More(DrawingContext drawing, Rect at, string color)
    {
        drawing.PushTransform(Placed(at.Width / 24, at.X, at.Y));
        // Thicker than the icons' strokes, so three dots carry the weight of an outlined icon.
        drawing.DrawGeometry(null, Stroke(color, 4), MoreShape);
        drawing.Pop();
    }

    // A bolt as tall as the area, centred in it.
    public static void Bolt(DrawingContext drawing, Rect area, string color)
    {
        double scale = area.Height / 23;
        if (scale <= 0) return;
        drawing.PushTransform(Placed(scale, area.X + (area.Width - 15 * scale) / 2 - 4.5 * scale, area.Y - .5 * scale));
        drawing.DrawGeometry(UI.Brush(color), null, BoltShape);
        drawing.Pop();
    }

    // An exclamation mark centred in the area: a bar and a square dot of the same thickness, at least 1.5 DIP, all on whole
    // device pixels so the dot is as dark as the bar. halo: a device pixel of this color around them, setting them off the fill.
    public static void Exclamation(DrawingContext drawing, Rect area, string color, string? halo, double ppd)
    {
        double Snap(double value) => Math.Round(value * ppd) / ppd;
        double h = area.Height, thickness = Math.Max(Math.Ceiling(1.5 * ppd), Math.Round(h * .2 * ppd)) / ppd;
        double left = Snap(area.X + area.Width / 2 - thickness / 2), dotBottom = Snap(area.Y + h * .85), dotTop = dotBottom - thickness;
        double barBottom = dotTop - Math.Max(1 / ppd, Snap(h * .1)), barTop = Math.Min(Snap(area.Y + h * .15), barBottom - thickness);
        var bar = new Rect(left, barTop, thickness, barBottom - barTop);
        var dot = new Rect(left, dotTop, thickness, thickness);
        if (halo is not null)
        {
            var around = UI.Brush(halo);
            drawing.DrawRectangle(around, null, Rect.Inflate(bar, 1 / ppd, 1 / ppd));
            drawing.DrawRectangle(around, null, Rect.Inflate(dot, 1 / ppd, 1 / ppd));
        }
        var ink = UI.Brush(color);
        drawing.DrawRectangle(ink, null, bar);
        drawing.DrawRectangle(ink, null, dot);
    }
}
