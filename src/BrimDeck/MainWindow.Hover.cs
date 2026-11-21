using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The collapsed surface's pointer area (see CollapsedHover), in the two forms the window needs: a screen rectangle in device
// pixels for the cursor checks, and the box of the nearly transparent HoverExtension layer that receives the mouse there.
public partial class MainWindow
{
    // The sensing area in device pixels, comparable with GetCursorPos. Empty while the window has no presentation source.
    private Rect CompactHoverScreenRect()
    {
        if (PresentationSource.FromVisual(Stage) is null) return Rect.Empty;
        var zone = HoverZoneOnStage();
        return new Rect(Stage.PointToScreen(new Point(zone.Left, zone.Top)), Stage.PointToScreen(new Point(zone.Right, zone.Bottom)));
    }

    // The HoverExtension box in DIPs relative to Stage. Collapsed, it is the sensing area: from the screen edge down to the
    // bottom of the surface while bridged, otherwise the surface alone, so the gap above a lowered capsule lets clicks through.
    // Open, the panel uses its own outline as before: no height, and a NaN Left and Width that keep the stretched layout.
    private (double Top, double Height, double Left, double Width) HoverExtensionBox(bool expanded)
    {
        if (expanded) return (0, 0, double.NaN, double.NaN);
        var zone = HoverZoneOnStage();
        return (zone.Top, zone.Height, zone.Left, zone.Width);
    }

    // The collapsed shape is relative to the monitor's top-left corner. Stage starts at the window's corner, which Place puts
    // at the top of the monitor and _windowOffset DIPs to the right of its left edge; the island is drawn with the same offset.
    private Rect HoverZoneOnStage()
    {
        // A capsule docked with the bottom bridges to the line it is docked with (MainWindow.Edge.cs), the frame's edge.
        var zone = CollapsedHover.Zone(Collapsed.Style, Collapsed.Surface, Collapsed.Style == CompactStyle.Capsule && _frame.Edge != ScreenEdge.Top ? 0 : WorkDip.Y);
        return new Rect(zone.X - _windowOffset, zone.Y, Math.Max(0, zone.Width), Math.Max(0, zone.Height));
    }

    // ---- The part under the pointer (DefaultDeckPage.Hover) ----
    // Each part of the collapsed content carries, as its Tag, what it opens the panel on (HoverPartOf, set where the row and the
    // column are built): its page, or none for the clock. The file transfer's count opens the clipboard page with the file transfer
    // in front (Files). Tagging the parts themselves keeps the mapping right however the row is ordered, sized or left short.
    private sealed record HoverPart(DeckPage? Page, bool Files = false);

    private static HoverPart HoverPartOf(object part) => part switch
    {
        "cover" or CompactPart.Equalizer or CompactPart.Music => new(DeckPage.Music),
        CompactPart.Monitor => new(DeckPage.Monitor),
        CompactPart.Devices => new(DeckPage.Devices),
        CompactPart.Rings => new(DeckPage.Usage),
        CompactPart.Transfer => new(DeckPage.Clipboard, Files: true),
        _ => new(null),
    };

    // The part the panel opens on, taken as it opens (OpenAtPointer) and spent by the page choice that opening makes
    // (ApplyDefaultMusicPage). Null when the opening page is chosen otherwise or nothing is shown.
    private HoverPart? _hoverPart;
    private bool OpensOnHoverPart => Settings.DefaultPage == DefaultDeckPage.Hover;
    // The shadow's reach in the default themes (blur 14 and depth 4).
    private const double MinApproach = 18;

    // The surface in Stage's coordinates, as HoverZoneOnStage gives the sensing area.
    private Rect SurfaceOnStage()
    {
        var surface = Collapsed.Surface;
        return new Rect(surface.X - _windowOffset, surface.Y, Math.Max(0, surface.Width), Math.Max(0, surface.Height));
    }

    // Where the pointer counts as near the surface: the surface with its shadow around it on every side, and the sensing area
    // (the bridge to the screen edge). The shadow's reach follows the effect the theme gives it, but a theme with a short shadow or
    // none still leaves the pointer that much room to be seen coming.
    private Rect HoverApproachOnStage()
    {
        double reach = Math.Max(MinApproach, IslandShadow.Effect is DropShadowEffect shadow ? shadow.BlurRadius + shadow.ShadowDepth : 0);
        var area = SurfaceOnStage();
        area.Inflate(reach, reach);
        area.Union(HoverZoneOnStage());
        return area;
    }

    // The part that answers for a place along the surface. Stage's X runs along the edge for every form (EdgeFrame): the row of
    // the notch and the capsule, and the column of a notch on a side edge. Only that coordinate counts, and the parts share the
    // whole length between them, meeting halfway across each gap; the first and the last reach past the surface's ends. So a
    // pointer in a gap, above or below the parts, in the bridge or in the shadow beside the surface is never on "no part", and
    // only the place along the edge decides, however fast the pointer came in. Neighbours that open the same page (a side edge's
    // reading lines) are one part. A part that glides partly out of a notch (ColumnCarousel) answers only for what is still inside.
    // Same: every part that opens the same as the one found, itself among them, in order along the surface.
    private (List<List<FrameworkElement>> Same, HoverPart Part)? HoverPartAt(double along)
    {
        var surface = SurfaceOnStage();
        var found = new List<(FrameworkElement Element, HoverPart Part, double Start, double End)>();
        // A quota notice takes the row's place (LayoutCompactAlert): it is about the usage, and is one part.
        IEnumerable<FrameworkElement> elements = CompactRings.IsVisible ? HoverPartElements(CompactRings) : CompactAlert.IsVisible ? [CompactAlert] : [];
        foreach (var element in elements)
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            Rect box;
            try { box = element.TransformToAncestor(Stage).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)); }
            catch (InvalidOperationException) { continue; }
            double start = Math.Max(box.Left, surface.Left), end = Math.Min(box.Right, surface.Right);
            if (end > start) found.Add((element, element == CompactAlert ? new HoverPart(DeckPage.Usage) : (HoverPart)element.Tag, start, end));
        }
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        var parts = new List<(List<FrameworkElement> Elements, HoverPart Part, double Start, double End)>();
        foreach (var (element, part, start, end) in found)
        {
            if (parts.Count > 0 && parts[^1].Part == part) { parts[^1].Elements.Add(element); parts[^1] = parts[^1] with { End = Math.Max(parts[^1].End, end) }; }
            else parts.Add(([element], part, start, end));
        }
        int at = CollapsedHover.PartAt([.. parts.Select(part => (part.Start, part.End))], along);
        if (at < 0) return null;
        var chosen = parts[at].Part;
        return ([.. parts.Where(part => part.Part == chosen).Select(part => part.Elements)], chosen);
    }

    // The tagged parts of the collapsed content, however deep; a tagged part's own children are not parts.
    private static IEnumerable<FrameworkElement> HoverPartElements(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Tag: HoverPart } element) yield return element;
            else foreach (var inner in HoverPartElements(child)) yield return inner;
        }
    }

    // The part under the pointer as the panel opens; null unless the opening page follows it.
    private HoverPart? HoverPartUnderPointer()
    {
        if (!OpensOnHoverPart || PresentationSource.FromVisual(Stage) is null) return null;
        return HoverPartAt(Stage.PointFromScreen(WindowsHost.Cursor()).X)?.Part;
    }

    // ---- The frames around the part ----
    // While the panel is collapsed and the pointer is near (HoverApproachOnStage), frames mark what the panel would open on, so it
    // is known before the pointer reaches the surface, which with no open delay opens it at once. Every part of that page is
    // framed, not only the one under the pointer: the cover, the title and the equalizer all stand for the music, wherever they
    // are in the row. A frame is a light fill of the theme's text color and a hairline edge of it, so it reads on every theme's
    // surface, light or dark, plain or patterned; its corners are small (square in a pixel-art theme) and it keeps clear of the
    // surface's edges. The frames lie over the content: beneath them, the opaque fades at the ends of a gliding title or readings
    // would cut dark bands into the fill. They fade in and out together, and glide to the next part as the pointer moves along.
    // The clock, which opens no page, and a page that is switched off get none.
    private Grid? _hoverFrames;
    private bool _hoverFrameShown;
    private readonly List<Rect> _hoverFrameAt = [];
    private Color _hoverFrameInk;
    private Brush? _hoverFill, _hoverStroke;
    private const int HoverFrameFade = 140, HoverFrameGlide = 160;

    // Called by TrackPointer on each check. Returns whether the pointer is near, where it is then checked more often. covered:
    // whether another window lies over the pointer, asked only once it is near.
    private bool TrackHoverFrame(Point cursor, bool allowed, Func<bool> covered)
    {
        if (!allowed || !OpensOnHoverPart || _expanded || PresentationSource.FromVisual(Stage) is null) { HideHoverFrame(); return false; }
        var point = Stage.PointFromScreen(cursor);
        if (!HoverApproachOnStage().Contains(point) || covered()) { HideHoverFrame(); return false; }
        if (HoverPartAt(point.X) is { Part.Page: { } page } found && DeckPages.Shown(Settings, page)) ShowHoverFrame(found.Same);
        else HideHoverFrame();
        return true;
    }

    // One frame per group of parts, in the order along the surface.
    private void ShowHoverFrame(List<List<FrameworkElement>> groups)
    {
        double scale = CompactMusicLayout.SafeScale(Collapsed.Scale);
        // IslandContent stands upright: the row runs along its width, a side edge's column along its height. A frame spans the
        // surface's depth less an inset on either side, and its parts' length with half a gap on either side.
        double width = IslandContent.ActualWidth, height = IslandContent.ActualHeight;
        double inset = Math.Round(4 * scale), pad = CompactMusicLayout.Gap(scale) / 2;
        var inside = new Rect(inset, inset, Math.Max(0, width - 2 * inset), Math.Max(0, height - 2 * inset));
        var dpi = VisualTreeHelper.GetDpi(this);
        var frames = new List<Rect>();
        foreach (var group in groups)
        {
            var box = Rect.Empty;
            try { foreach (var element in group) box.Union(element.TransformToAncestor(IslandContent).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight))); }
            catch (InvalidOperationException) { continue; }
            if (box.IsEmpty) continue;
            var frame = CompactColumnMode
                ? new Rect(inside.Left, box.Top - pad, inside.Width, box.Height + 2 * pad)
                : new Rect(box.Left - pad, inside.Top, box.Width + 2 * pad, inside.Height);
            frame.Intersect(inside);
            if (frame.IsEmpty || frame.Width < 1 || frame.Height < 1) continue;
            frames.Add(new Rect(Snap(frame.Left, dpi.DpiScaleX), Snap(frame.Top, dpi.DpiScaleY), Snap(frame.Width, dpi.DpiScaleX), Snap(frame.Height, dpi.DpiScaleY)));
        }
        if (frames.Count == 0) { HideHoverFrame(); return; }

        if (_hoverFrames is null)
        {
            _hoverFrames = new Grid { IsHitTestVisible = false, Opacity = 0, Visibility = Visibility.Hidden };
            IslandContent.Children.Insert(IslandContent.Children.IndexOf(Compact) + 1, _hoverFrames);
        }
        var ink = Look.ColorOf("textPrimary");
        bool recolor = ink != _hoverFrameInk || _hoverFill is null;
        if (recolor)
        {
            _hoverFrameInk = ink;
            _hoverFill = Frozen(Color.FromArgb(0x1C, ink.R, ink.G, ink.B)); _hoverStroke = Frozen(Color.FromArgb(0x4A, ink.R, ink.G, ink.B));
        }
        Brush fill = _hoverFill!, edge = _hoverStroke!;
        while (_hoverFrames.Children.Count < frames.Count)
            _hoverFrames.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true, Background = fill, BorderBrush = edge,
            });
        // Square in a pixel-art theme, as every corner there is (PanelLook.Corner).
        double corner = Look.Corner(5 * scale);
        bool glide = _hoverFrameShown && Animates;
        for (int i = 0; i < _hoverFrames.Children.Count; i++)
        {
            var border = (Border)_hoverFrames.Children[i];
            if (i >= frames.Count) { border.Visibility = Visibility.Collapsed; continue; }
            if (recolor) { border.Background = fill; border.BorderBrush = edge; }
            var frame = frames[i];
            border.CornerRadius = new CornerRadius(Math.Min(corner, Math.Min(frame.Width, frame.Height) / 2));
            bool was = i < _hoverFrameAt.Count && border.Visibility == Visibility.Visible;
            border.Visibility = Visibility.Visible;
            if (was && _hoverFrameAt[i] == frame) continue;
            var margin = new Thickness(frame.Left, frame.Top, 0, 0);
            if (glide && was)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var span = TimeSpan.FromMilliseconds(HoverFrameGlide);
                border.BeginAnimation(MarginProperty, new ThicknessAnimation(margin, span) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
                border.BeginAnimation(WidthProperty, new DoubleAnimation(frame.Width, span) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
                border.BeginAnimation(HeightProperty, new DoubleAnimation(frame.Height, span) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                SetImmediate(border, MarginProperty, margin);
                SetImmediate(border, WidthProperty, frame.Width);
                SetImmediate(border, HeightProperty, frame.Height);
            }
        }
        _hoverFrameAt.Clear(); _hoverFrameAt.AddRange(frames);
        if (_hoverFrameShown) return;
        _hoverFrameShown = true;
        _hoverFrames.Visibility = Visibility.Visible;
        if (Animates) _hoverFrames.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(HoverFrameFade)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
        else SetImmediate(_hoverFrames, OpacityProperty, 1d);
    }

    // The frames fade out; shown again, they appear at their parts instead of gliding from where they were.
    private void HideHoverFrame()
    {
        if (!_hoverFrameShown || _hoverFrames is not { } frames) return;
        _hoverFrameShown = false; _hoverFrameAt.Clear();
        void Gone() { foreach (UIElement border in frames.Children) border.Visibility = Visibility.Collapsed; frames.Visibility = Visibility.Hidden; }
        if (!Animates) { SetImmediate(frames, OpacityProperty, 0d); Gone(); return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(HoverFrameFade)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => { if (!_hoverFrameShown) Gone(); };
        frames.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
