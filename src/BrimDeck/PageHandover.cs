using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace BrimDeck;

// How a rebuilt page takes over from the one it replaces.
internal enum Handover
{
    // At once, as when nothing about the page's form changed (a reading, the clock).
    None,
    // The old content fades away above the new, which is in place at once: a quota page or a filter changed. What did not
    // change stays still.
    Dissolve,
    // The page changed its form (a drag crossed a size step, an app was added): the old layout drops away quickly while the new
    // one fades in, so two layouts of different type sizes overlap only briefly.
    Morph,
    // Deeper and back: the old content leaves toward the side it is left for while the new comes in from the other side, as
    // pages of the page switch do (model details, application pages).
    Forward, Back,
    // Another song: the old titles drift up and fade while the new ones rise into place.
    Swap
}

// The usage and media pages are rebuilt rather than updated, so a change of form used to replace everything in one frame. Hold
// takes the page's elements before a rebuild; Release then lets them leave on a layer above the page (LeavingContent, laid out with
// the margin the page had) while the rebuilt content arrives, or drops them at once when the rebuild changed nothing that shows.
// A rebuild during a hand-over that is still under way keeps that hand-over going and drops only its own old elements, so a
// panel dragged across a size step does not cut its hand-over short 25 times a second. The page switch hands over a picture
// of the page it leaves (Capture, Send): the pages keep their own elements, and the monitor page keeps its view across switches.
internal sealed class PageHandover
{
    private const double Travel = 16, Rise = 8;
    private readonly Grid _layer;
    private readonly TranslateTransform _layerShift = new();
    private readonly List<UIElement> _held = [];
    private Thickness _margin;
    private int _version;

    public PageHandover(Grid layer) { _layer = layer; _layer.RenderTransform = _layerShift; _layer.IsHitTestVisible = false; }

    public void Hold(Panel content, UIElement? keep = null)
    {
        _held.Clear();
        _margin = content.Margin;
        for (int i = content.Children.Count - 1; i >= 0; i--)
        {
            var child = content.Children[i];
            if (child == keep) continue;
            content.Children.RemoveAt(i);
            _held.Insert(0, child);
        }
    }

    public void Release(FrameworkElement content, Handover motion)
    {
        if (_held.Count == 0 || motion == Handover.None || !Soft.Enabled) { _held.Clear(); return; }
        var leaving = _held.ToList();
        _held.Clear();
        Begin(leaving, _margin, content, motion);
    }

    // A picture of a page as it is on screen, to leave in its place; null for a page that is not laid out.
    public static Image? Capture(FrameworkElement page)
    {
        double width = page.ActualWidth, height = page.ActualHeight;
        if (!page.IsVisible || width < 1 || height < 1 || PresentationSource.FromVisual(page) is null) return null;
        var dpi = VisualTreeHelper.GetDpi(page);
        var picture = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Drawn through a brush, so the page's own offset in its parent does not move it within the picture.
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(page), null, new Rect(0, 0, width, height));
        picture.Render(drawing);
        picture.Freeze();
        return new Image { Source = picture, Width = width, Height = height, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    }

    // Lets a captured page leave while content arrives. The picture keeps the page's top margin and is centred as the page was.
    public void Send(UIElement? picture, Thickness margin, FrameworkElement content, Handover motion)
    {
        if (picture is null || motion == Handover.None || !Soft.Enabled) return;
        Begin([picture], new Thickness(0, margin.Top, 0, 0), content, motion);
    }

    private void Begin(List<UIElement> leaving, Thickness margin, FrameworkElement content, Handover motion)
    {
        Drop();
        int version = _version;
        _layer.Margin = margin;
        foreach (var child in leaving) _layer.Children.Add(child);
        var shift = content.RenderTransform as TranslateTransform;
        if (shift is null || shift.IsFrozen) content.RenderTransform = shift = new TranslateTransform();
        int leave = motion switch { Handover.Dissolve => Soft.Quick + 20, Handover.Morph => 120, Handover.Swap => 200, _ => 150 };
        int arrive = motion switch { Handover.Morph => 200, Handover.Swap => 320, _ => 300 };
        var away = Soft.Fade(1, 0, leave, motion == Handover.Morph ? Soft.Out : Soft.InOut);
        away.Completed += (_, _) => { if (_version == version) Drop(); };
        _layer.BeginAnimation(UIElement.OpacityProperty, away);
        if (motion == Handover.Dissolve) return;
        if (motion == Handover.Morph) { content.BeginAnimation(UIElement.OpacityProperty, Arrival(0, 1, arrive)); return; }
        bool sideways = motion is Handover.Forward or Handover.Back;
        double direction = motion == Handover.Back ? -1 : 1;
        var axis = sideways ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        _layerShift.BeginAnimation(axis, Soft.Fade(0, sideways ? -direction * Travel : -Rise * .75, leave, Soft.InOut));
        content.BeginAnimation(UIElement.OpacityProperty, Arrival(0, 1, arrive));
        shift.BeginAnimation(axis, Arrival(sideways ? direction * Travel : Rise, 0, arrive));
    }

    // Ends a hand-over at once: the old elements go and the page is in place.
    public void Drop()
    {
        _version++;
        _layer.BeginAnimation(UIElement.OpacityProperty, null); _layer.Opacity = 1;
        _layerShift.BeginAnimation(TranslateTransform.XProperty, null); _layerShift.BeginAnimation(TranslateTransform.YProperty, null);
        _layerShift.X = _layerShift.Y = 0;
        _layer.Children.Clear();
    }

    // Brings a toolbar part that the rebuild showed in with the content.
    public static void Arrive(UIElement element, Handover motion)
    {
        if (motion is not (Handover.Forward or Handover.Back) || element.Visibility != Visibility.Visible || !Soft.Enabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, Arrival(0, 1, Soft.Short));
    }

    // An arrival returns the property to its value when it ends, so nothing is left animated.
    private static DoubleAnimation Arrival(double from, double to, int milliseconds)
    {
        var animation = Soft.Fade(from, to, milliseconds);
        animation.FillBehavior = FillBehavior.Stop;
        return animation;
    }

    // Moves an element that the rebuild keeps (a transport button) to its new place: it glides from where it is now, or is set
    // at once. The property keeps the new value; the glide only covers the way there.
    public static void Glide(UIElement element, DependencyProperty property, double to, bool animate, int milliseconds = Soft.Short)
    {
        double from = (double)element.GetValue(property);
        element.BeginAnimation(property, null);
        element.SetValue(property, to);
        if (!animate || !double.IsFinite(from) || Math.Abs(from - to) < .5) return;
        element.BeginAnimation(property, Arrival(from, to, milliseconds));
    }
}
