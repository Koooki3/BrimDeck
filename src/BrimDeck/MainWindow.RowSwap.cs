using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using static BrimDeck.Tracks;

namespace BrimDeck;

// The collapsed row giving way to a quota alert, the alert giving way to the next one, and the row coming back when the alert
// ends. The island does not change its shape for an alert, only its content row, so the swap is the content's own move, the one
// a notice makes when it follows another in place: the row on show leaves first (RowSwap.Leave, shrinking
// slightly as it goes), the row that follows lands after it (RowSwap.Land, from the same small size), and the two are never
// on show together. The new row is built at once (the state is already the new one when RenderCompact returns); only its opacity
// waits for its track. The one on its way out is kept in a layer of its own until it is gone. Without animations, or whenever the
// island is not drawing the row (the panel open or opening, the indicator line, a notice over the island, the boot), the swap is
// as direct as before.
public partial class MainWindow
{
    // The alert the row was last built for; a different one (or none after one) swaps the row.
    private CompactAlertInfo? _rowAlert;
    // The rows on their way out, drawn over the compact content until their track ends.
    private readonly List<Panel> _leavingRows = [];
    // The swap as it was before this file, for the offscreen comparison (tests/BrimDeck.FxPreview --alert-return).
    internal static bool RowSwapOff;

    // The length of a swap now, or 0 when the row changes at once.
    private int RowSwapLength()
    {
        bool drawn = !RowSwapOff && !_expanded && !_poweringOff && !_islandCovered && !BootHoldsPointer && Island.IsVisible
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
        // A row that was still waiting for its track (a swap that follows another within the first third of the move) has
        // nothing to show; the one leaving it leaves from where it is.
        double from = old.Opacity;
        var parts = old.Children.OfType<UIElement>().ToList();
        old.Children.Clear();
        if (from < .02 || parts.Count == 0) return length;
        var scale = new ScaleTransform(1, 1);
        var ghost = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = old.HorizontalAlignment, VerticalAlignment = old.VerticalAlignment,
            IsHitTestVisible = false, Opacity = from, RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale,
        };
        foreach (var part in parts) ghost.Children.Add(part);
        Compact.Children.Add(ghost);
        _leavingRows.Add(ghost);
        // Over the leaving track the row is fully gone; its layer is dropped when that ends.
        Animate(ghost, OpacityProperty, 0, length, RowSwap.Leave, Soft.Out, () => DropRow(ghost));
        Animate(scale, ScaleTransform.ScaleXProperty, Soft.Shrunk, length, RowSwap.Leave, Soft.Out);
        Animate(scale, ScaleTransform.ScaleYProperty, Soft.Shrunk, length, RowSwap.Leave, Soft.Out);
        return length;
    }

    // After RenderCompact has built the new row: it is out of sight until its track begins, then lands.
    private void LandRow(int length)
    {
        var row = _alert is not null ? CompactAlert : CompactRings;
        var scale = new ScaleTransform(Soft.Shrunk, Soft.Shrunk);
        row.RenderTransformOrigin = new Point(.5, .5);
        row.RenderTransform = scale;
        SetImmediate(row, OpacityProperty, 0d);
        Animate(row, OpacityProperty, 1, length, RowSwap.Land, Soft.Out, () => { if (ReferenceEquals(row.RenderTransform, scale)) row.RenderTransform = null; });
        Animate(scale, ScaleTransform.ScaleXProperty, 1, length, RowSwap.Land, Soft.Out);
        Animate(scale, ScaleTransform.ScaleYProperty, 1, length, RowSwap.Land, Soft.Out);
    }

    private void DropRow(Panel ghost)
    {
        if (!_leavingRows.Remove(ghost)) return;
        Compact.Children.Remove(ghost);
        ghost.Children.Clear();
    }

    // A swap that cannot play (animations off, the island not drawing the row) leaves both rows as they were before this file:
    // fully shown, with no layer of a swap that was under way.
    private void SettleRows()
    {
        foreach (var row in (Panel[])[CompactRings, CompactAlert]) { SetImmediate(row, OpacityProperty, 1d); row.RenderTransform = null; }
        foreach (var ghost in _leavingRows.ToList()) DropRow(ghost);
    }
}
