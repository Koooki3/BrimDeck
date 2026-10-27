using System.Windows.Media.Animation;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// The indicator's motion. The surface that is the line grows into the notice's card and comes back, and that move is all of the
// indicator's motion: it adds no layer of its own (no droplets, no glow, no sweep). It follows the shape the expert analysis of the
// indicator asks for: the line is the notice's lead and its remainder, so before the card grows it draws back a little against
// the way it is about to go, as a loaded spring does, and when the card has come back to it the surface carries on a little past
// the line's size and settles back to it. Both ride on the panel's own curves (the opening spring's decay and frequency, the closing
// curve), with the same speed through the joint into the release and the same speed and acceleration all through the return
// (Curves.LineArrive, Curves.LineReturn). Only the amplitude follows the notice's weight (NoticeFx.LineScale). The length of
// the surface along the line, and its place with it, is all that the two curves change (its height and corners keep the panel's);
// nothing is drawn per frame, and without animations nothing moves.
internal sealed partial class NoticeWindow
{
    // The plan of the notice shown last (the notice itself is gone by the time the surface returns).
    private NoticeFxPlan _linePlan = NoticeFxPlan.Standard;

    private static NoticeFxPlan PlanOf(AttentionItem item)
    {
        var notice = item.Payload as Notice;
        return NoticeFx.For(item.Kind, item.Priority, item.Urgent, notice?.Call ?? false, notice?.Category);
    }

    // The curve of the surface's size and place as the line grows into the card, or null when it is not the line.
    private IEasingFunction? LineArrival()
    {
        if (!Indicator || _item is not { } item) return null;
        _linePlan = PlanOf(item);
        return new LineArriveEase(NoticeFx.LineWindUp(_collapsed.Rect.Width, _toastFrame.Rect.Width, _linePlan));
    }

    // The curve as the surface returns to the line, from the size it has now, carrying on from the panel's closing curve (Soft.Settle).
    private IEasingFunction? LineReturning()
        => Indicator ? new LineReturnEase(NoticeFx.LineCarry(_collapsed.Rect.Width, _surface.Width, _linePlan), .45, 0, .12, 1) : null;
}
