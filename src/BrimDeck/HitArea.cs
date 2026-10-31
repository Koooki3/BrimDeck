using System.Windows;

namespace BrimDeck;

// A hit area larger than what is drawn, so a small control is easy to point at (WCAG 2.5.8 asks for 24 DIP; 32 where the room
// allows) while its look and the layout stay as they were. The control grows by Inset and a negative margin of the same size
// takes the growth back out of the layout; its template draws inside the inset (a "Face" part whose margin is Inset), so only
// the transparent edge around it is new. Its keyboard focus outline keeps to the face as well. The growth replaces the
// control's vertical margin, so it is used on controls that have none of their own.
internal static class HitArea
{
    public const double Target = 32, Minimum = 24;

    public static readonly DependencyProperty InsetProperty = DependencyProperty.RegisterAttached(
        "Inset", typeof(Thickness), typeof(HitArea), new FrameworkPropertyMetadata(default(Thickness)));
    public static Thickness GetInset(DependencyObject element) => (Thickness)element.GetValue(InsetProperty);
    public static void SetInset(DependencyObject element, Thickness value) => element.SetValue(InsetProperty, value);
    // The focus outline the control had before it grew.
    private static readonly DependencyProperty FocusProperty = DependencyProperty.RegisterAttached(
        "Focus", typeof(Style), typeof(HitArea), new FrameworkPropertyMetadata(null));
    private static readonly Dictionary<(Style, Thickness), Style> FocusStyles = [];

    // A control drawn height tall, centred in a band room tall: it takes up to Target of the band, never past it.
    public static void Fit(FrameworkElement element, double height, double room)
    {
        double grow = Math.Max(0, Math.Min(Target, room) - height), top = Math.Floor(grow / 2);
        Grow(element, top, grow - top, height);
    }

    // Two rows drawn row tall, one under the other, each given Minimum tall for the pointer: the lower grows down over the room below
    // it, the upper up over roomAbove (the space between it and whatever lies over it, not its neighbour's), so neither takes the
    // other's room. Up is less than Minimum - row only where roomAbove is.
    public static (double Up, double Down) Stacked(double row, double roomAbove)
    {
        double grow = Math.Max(0, Minimum - row);
        return (Math.Min(grow, Math.Max(0, roomAbove)), grow);
    }

    // Grows a control by top above and bottom below what it draws; look: its drawn height, for a control of fixed height.
    public static void Grow(FrameworkElement element, double top, double bottom, double look = double.NaN)
    {
        top = Math.Max(0, top); bottom = Math.Max(0, bottom);
        var inset = new Thickness(0, top, 0, bottom);
        if (double.IsFinite(look)) element.Height = look + top + bottom;
        var margin = element.Margin;
        element.Margin = new Thickness(margin.Left, -top, margin.Right, -bottom);
        SetInset(element, inset);
        // The focus outline is the control's own, kept to the face.
        if (element.GetValue(FocusProperty) is not Style focus)
        {
            if (element.FocusVisualStyle is not { } own) return;
            element.SetValue(FocusProperty, focus = own);
        }
        element.FocusVisualStyle = top == 0 && bottom == 0 ? focus : FocusFor(focus, inset);
    }

    private static Style FocusFor(Style focus, Thickness inset)
    {
        if (FocusStyles.TryGetValue((focus, inset), out var style)) return style;
        style = new Style { BasedOn = focus };
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, inset));
        style.Seal();
        return FocusStyles[(focus, inset)] = style;
    }

    // The drawn height of a control that has grown.
    public static double Look(FrameworkElement element) { var inset = GetInset(element); return element.ActualHeight - inset.Top - inset.Bottom; }
}
