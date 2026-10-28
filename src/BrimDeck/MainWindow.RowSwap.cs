using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;
using static BrimDeck.Tracks;

namespace BrimDeck;

// The collapsed row giving way to a quota alert, the alert giving way to the next one, and the row coming back when the alert
// ends. The island does not change its shape for an alert, only its content row, so the swap is the content's own move: the row on
// show leaves first (shrinking slightly as it goes), the row that follows lands after it from the same small size, and the two are
// never on show together (the model is BrimDeck.Core.RowSwap). The new row is built at once (the state is already the new one when
// RenderCompact returns); only its opacity waits for its track. The row on its way out is a picture of it taken just before it is
// built again, so the old row's parts (the readings, the equalizer, the title) end exactly as they did before this file: replaced,
// never moved. These are WPF animations: nothing runs on the display's frames, and nothing runs once the swap has ended. Without
// animations, or whenever the island is not drawing the row (the panel open or opening, the indicator line, the boot, the
// power-off, the panel hidden, the display off), the row changes at once, as before.
public partial class MainWindow
{
    // The alert the row was last built for; a different one (or none after one) swaps the row.
    private CompactAlertInfo? _rowAlert;
    // The pictures of rows on their way out, drawn over the compact content until their track ends.
    private readonly List<Image> _leavingRows = [];
    // The swap as it was before this file, for the offscreen comparison (tests/BrimDeck.FxPreview --alert-return).
    internal static bool RowSwapOff;

    // The length of a swap now, or 0 when the row changes at once.
    private int RowSwapLength()
    {
        bool drawn = !RowSwapOff && !_expanded && !_poweringOff && !_islandCovered && !BootHoldsPointer && !IsSuppressed && !DisplayPower.Off && Island.IsVisible
            && Compact.Visibility == Visibility.Visible && Compact.Opacity >= .02 && Collapsed.Style != CompactStyle.Line;
        return RowSwap.Length(Settings.AnimationDuration, Soft.Enabled, drawn);
    }

    // First thing RenderCompact does. Returns the length of the swap to land the new row with, or 0.
    private int LeaveRow()
    {
        if (!RowSwap.Changes(_rowAlert, _alert)) return 0;
        _rowAlert = _alert;
        int length = RowSwapLength();
        if (length <= 0) { SettleRows(); return 0; }
        var old = CompactAlert.Visibility == Visibility.Visible ? CompactAlert : CompactRings;
        // A row that was still landing (a swap that follows another within the first part of the move) leaves from where it is:
        // its opacity and size are taken, and the picture is made of it at full opacity and size.
        double from = old.Opacity, size = old.RenderTransform is ScaleTransform landing ? landing.ScaleX : 1;
        if (!(from >= .02) || old.Children.Count == 0 || Picture(old) is not { } picture) return length;
        var scale = new ScaleTransform(size, size);
        var ghost = new Image
        {
            Source = picture, Width = old.ActualWidth, Height = old.ActualHeight, Stretch = Stretch.Fill, SnapsToDevicePixels = true,
            HorizontalAlignment = old.HorizontalAlignment, VerticalAlignment = old.VerticalAlignment,
            IsHitTestVisible = false, Opacity = from, RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale,
        };
        Compact.Children.Add(ghost);
        _leavingRows.Add(ghost);
        // Over the leaving track the row is fully gone; its layer is dropped when that ends.
        Animate(ghost, OpacityProperty, 0, length, RowSwap.Leave, Soft.Out, () => DropRow(ghost));
        Animate(scale, ScaleTransform.ScaleXProperty, Soft.Shrunk * size, length, RowSwap.Leave, Soft.Out);
        Animate(scale, ScaleTransform.ScaleYProperty, Soft.Shrunk * size, length, RowSwap.Leave, Soft.Out);
        return length;
    }

    // The row as it is drawn now, at its own opacity and size, in device pixels; null when it has no size yet.
    private static BitmapSource? Picture(Panel row)
    {
        double width = row.ActualWidth, height = row.ActualHeight;
        if (!(width >= 1) || !(height >= 1)) return null;
        // What lands animates the row's own opacity and size, which a picture of it must not carry twice.
        SetImmediate(row, OpacityProperty, 1d);
        row.RenderTransform = null;
        var dpi = VisualTreeHelper.GetDpi(row);
        var picture = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Through a brush: rendering the row itself would keep its place in the compact content and cut the picture off.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(row) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
        picture.Render(visual);
        picture.Freeze();
        return picture;
    }

    // After RenderCompact has built the new row: it is out of sight until its track begins, then lands.
    private void LandRow(int length)
    {
        var row = _alert is not null ? CompactAlert : CompactRings;
        var scale = new ScaleTransform(Soft.Shrunk, Soft.Shrunk);
        row.RenderTransformOrigin = new Point(.5, .5);
        row.RenderTransform = scale;
        SetImmediate(row, OpacityProperty, 0d);
        // Landed, the row goes back to the plain element it was: no transform and no animation held on it, so the readings'
        // own frames are not drawn through an opacity layer.
        Animate(row, OpacityProperty, 1, length, RowSwap.Land, Soft.Out, () =>
        {
            if (!ReferenceEquals(row.RenderTransform, scale)) return;
            row.RenderTransform = null;
            SetImmediate(row, OpacityProperty, 1d);
        });
        Animate(scale, ScaleTransform.ScaleXProperty, 1, length, RowSwap.Land, Soft.Out);
        Animate(scale, ScaleTransform.ScaleYProperty, 1, length, RowSwap.Land, Soft.Out);
    }

    private void DropRow(Image ghost)
    {
        if (!_leavingRows.Remove(ghost)) return;
        Compact.Children.Remove(ghost);
        ghost.Source = null;
    }

    // A swap that cannot play (animations off, the island not drawing the row) leaves both rows as they were before this file:
    // fully shown, with no picture of a swap that was under way.
    private void SettleRows()
    {
        foreach (var row in (Panel[])[CompactRings, CompactAlert]) { SetImmediate(row, OpacityProperty, 1d); row.RenderTransform = null; }
        foreach (var ghost in _leavingRows.ToList()) DropRow(ghost);
    }
}
