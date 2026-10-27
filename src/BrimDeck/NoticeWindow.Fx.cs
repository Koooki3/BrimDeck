using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// The liquid motion of the arriving notice (NoticeLiquid, NoticeFxVisuals). It plays on the notice's surface once the surface has
// grown to size: the capsule's ends throw droplets and flow back, the notch swells at its lower corners and the bulges run
// round its outline, the indicator's card sheds a drop at each end, and an edge glow lights twice. The capsule's and the
// notch's motion is the maintainer's design (see NoticeFxVisuals); the rest is ours.
//
// It costs nothing while it does not play. While it does (a second and a half to two seconds), the display's own frames drive it
// (Motion, the clock the monitor page's readings use), which lets go of the display as soon as the motion has nothing left to
// draw. Without animations it does not play at all, and the notice is as it was.
internal sealed partial class NoticeWindow
{
    // The glow's margin all round the surface (GlowMargin) and, to the sides of a capsule or a card, the room the droplets take.
    private const double LiquidReach = 28;
    private readonly NoticeFxBody _fxBody = new() { IsHitTestVisible = false };
    private readonly NoticeFxGlow _fxGlow = new() { IsHitTestVisible = false };
    private NoticeFxState? _fx;
    private PromptTimer? _fxTimer;
    private (NoticeLook Look, NoticeFxTone Tone, NoticeFxLook Fx)? _fxLookCache;

    // Whether the motion may play: with animations on, in the theme's own colors (high contrast drops the glow and is left still).
    private bool FxAllowed => Soft.Enabled && !_look.HighContrast && Duration() > 0;

    // Extra room to the sides of the window for the droplets of a capsule or a card; the notch's bulges stay in the glow's margin.
    private double FxSideMargin => Soft.Enabled && !SystemParameters.HighContrast && _deck.Collapsed.Style != CompactStyle.Notch ? LiquidReach : 0;

    private void FxSized()
    {
        _fxBody.Width = _fxGlow.Width = _stageRect.Width; _fxBody.Height = _fxGlow.Height = _stageRect.Height;
    }

    // The notice has arrived (or followed another in place): the motion begins once the surface is most of the way to its size,
    // so the glow lights as the notice settles. A motion still playing from the notice before ends first, the droplets shrinking away.
    private void FxBegin(bool continuing)
    {
        if (continuing) FxAbort(); else FxStop();
        if (_item is null || !FxAllowed || _showingDetail || _arrivedUnderPointer) return;
        _fxTimer ??= new PromptTimer(Dispatcher, FxPlay);
        _fxTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Duration() * .5));
        _fxTimer.Start();
    }

    private void FxPlay()
    {
        _fxTimer?.Stop();
        if (_item is not { } item || _leaving || _showingDetail || !FxAllowed || !IsVisible) return;
        // Whatever still plays from before goes at once; the notice has its own motion now.
        FxStop();
        var notice = Notice;
        var plan = NoticeFx.For(item.Kind, item.Priority, item.Urgent, notice?.Call ?? false, notice?.Category);
        var frame = _toastFrame;
        var form = Indicator ? LiquidForm.Line : _deck.Collapsed.Style == CompactStyle.Notch ? LiquidForm.Notch : LiquidForm.Capsule;
        var liquid = new NoticeLiquid(new LiquidSpec(form, frame.Rect.Width, frame.Rect.Height, NoticeLiquid.DesignSeed, Intensity: plan.Intensity, Separation: plan.Separation,
            Shoulder: frame.Shoulder, CornerRadius: frame.BottomRadius, MaxInset: NoticeLiquid.MaxInset(frame.Rect.Height, ToastMetrics(frame.Rect.Height).Padding, frame.Rect.Height / 2 - 6)));
        if (_fxLookCache is not { } cached || !ReferenceEquals(cached.Look, _look) || cached.Tone != plan.Tone)
            _fxLookCache = cached = (_look, plan.Tone, NoticeFxLook.Create(_look, plan));
        _fx = new NoticeFxState { Liquid = liquid, Look = cached.Fx, Plan = plan, Form = form, Start = Eased.Now, Pace = NoticeFx.Pace(Settings.AnimationDuration) };
        _fxBody.State = _fx; _fxGlow.State = _fx;
        FxSized();
        Motion.Start(_fxBody, FxStep);
    }

    // One display frame of the motion: the surface as it stands now (it may still be settling, or growing for a longer text), the
    // motion at its time, the ends drawn in, and the two layers drawn. False once there is nothing more to show.
    private bool FxStep(double now)
    {
        if (_fx is not { } fx) return false;
        double t = (now - fx.Start) / fx.Pace;
        double left = Canvas.GetLeft(_surface), top = Canvas.GetTop(_surface);
        fx.Left = double.IsFinite(left) ? left : 0; fx.Top = double.IsFinite(top) ? top : 0; fx.Width = _surface.Width; fx.Height = _surface.Height;
        if (!(fx.Width > 0 && fx.Height > 0)) { FxStop(); return false; }
        fx.Liquid.Sample(t, fx.Frame, fx.Width, fx.Height);
        var frame = fx.Frame;
        // The rest at the end draws nothing: the motion lets go of the display as soon as there is nothing left to show.
        bool shows = frame.Alive && (frame.Glow > .004 || frame.SweepCount > 0 || frame.Discs.Count > 0 || frame.BumpRuns.Count > 0 || frame.InsetLeft > .02 || frame.InsetRight > .02);
        if (!shows && t > fx.Liquid.Separation) { FxStop(); return false; }
        SetInsets(frame.InsetLeft, frame.InsetRight);
        _fxBody.InvalidateVisual(); _fxGlow.InvalidateVisual();
        if (fx.Liquid.Finished(t)) { FxStop(); return false; }
        return true;
    }

    private void SetInsets(double left, double right)
    {
        if (_surface.InsetLeft != left) { _surface.InsetLeft = left; _glow.InsetLeft = left; }
        if (_surface.InsetRight != right) { _surface.InsetRight = right; _glow.InsetRight = right; }
    }

    // The notice ends or its details open: the droplets shrink away, the ends flow back and the glow goes out within a third of a
    // second, rather than the motion stopping short.
    private void FxAbort()
    {
        _fxTimer?.Stop();
        if (_fx is { } fx) fx.Liquid.Abort((Eased.Now - fx.Start) / fx.Pace);
    }

    // Everything of the motion is gone at once: the window hides, the panel opens over it, or the notice is dressed again.
    private void FxStop()
    {
        _fxTimer?.Stop();
        if (_fx is null) return;
        _fx = null;
        _fxBody.State = null; _fxGlow.State = null;
        _fxGlow.Forget();
        SetInsets(0, 0);
        _fxBody.InvalidateVisual(); _fxGlow.InvalidateVisual();
    }
}
