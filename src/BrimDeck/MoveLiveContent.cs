using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// A live view of the compact content, independent of the opacity of its parent window surface.
// The caller clips it to the ghost and draws the glowing border separately.
internal sealed class MoveLiveContent(FrameworkElement source)
{
    private readonly VisualBrush _brush = new(source)
    {
        Stretch = Stretch.Fill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center,
        AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute
    };

    public void Draw(DrawingContext drawing, ScreenEdge edge, Point centre)
    {
        double width = source.ActualWidth, height = source.ActualHeight;
        if (!(width > 0) || !(height > 0)) return;
        _brush.Viewbox = new Rect(0, 0, width, height);
        var upright = EdgeFrame.For(edge, 1, 1, new DipRect(0, 0, 1, 1), true).Upright;
        drawing.PushTransform(new MatrixTransform(upright.M11, upright.M12, upright.M21, upright.M22,
            centre.X - (centre.X * upright.M11 + centre.Y * upright.M21),
            centre.Y - (centre.X * upright.M12 + centre.Y * upright.M22)));
        drawing.DrawRectangle(_brush, null, new Rect(centre.X - width / 2, centre.Y - height / 2, width, height));
        drawing.Pop();
    }
}
