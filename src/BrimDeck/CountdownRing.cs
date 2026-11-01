using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// A countdown's ring on the island: a track and, over it, the part of the circle the countdown has run (RingArc), from the top
// counterclockwise. It is made once for a notice; every second the shared tick (NoticeCenter.LiveTick) moves the arc's end point
// in place, so nothing is laid out or allocated again and nothing ticks once the countdown ends (IslandNotice.WatchLive).
internal sealed class CountdownRing : Grid
{
    private readonly double _size, _thickness;
    private bool _drawn;
    private readonly Ellipse _full;
    private readonly Path _arc;
    private readonly PathFigure _figure = new() { IsClosed = false };
    private readonly ArcSegment _segment = new() { SweepDirection = SweepDirection.Counterclockwise, IsStroked = true };

    // The arc as last drawn.
    public RingArc Shown { get; private set; }

    public CountdownRing(double fraction, double size, double thickness, Color color, Color track)
    {
        _size = size; _thickness = thickness;
        Width = size; Height = size; IsHitTestVisible = false;
        var ink = Freeze(new SolidColorBrush(color));
        Children.Add(new Ellipse { Stroke = Freeze(new SolidColorBrush(track)), StrokeThickness = thickness });
        Children.Add(_full = new Ellipse { Stroke = ink, StrokeThickness = thickness });
        _figure.Segments.Add(_segment);
        var geometry = new PathGeometry(); geometry.Figures.Add(_figure);
        Children.Add(_arc = new Path { Data = geometry, Stroke = ink, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        Set(fraction);
    }

    public void Set(double fraction)
    {
        var arc = RingArc.Of(fraction, _size, _thickness);
        if (_drawn && arc == Shown) return;
        _drawn = true; Shown = arc;
        _full.Visibility = arc.Full ? Visibility.Visible : Visibility.Collapsed;
        _arc.Visibility = arc.Full || arc.None ? Visibility.Collapsed : Visibility.Visible;
        if (arc.Full || arc.None) return;
        _figure.StartPoint = new Point(arc.Center, arc.Center - arc.Radius);
        _segment.Point = new Point(arc.EndX, arc.EndY);
        _segment.Size = new Size(arc.Radius, arc.Radius);
        _segment.IsLargeArc = arc.Large;
    }

    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}
