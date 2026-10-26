using BrimDeck.Core;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Track = BrimDeck.Core.Notices.NoticeMotion.Track;

namespace BrimDeck;

// The island's shadow under liquid glass (round 14): out while the panel opens or closes and back at rest (ShadowAtRest), and an
// ambient and a contact shadow by size and by what lies behind (ShadowLook). The cached picture itself is MainWindow.Shadow.
public partial class MainWindow
{
    partial void ShadowSizeChanged() { if (_shadowLook) ApplyShadowLook(); }
    partial void ShadowScaleSeen(double scale) => _shadowScale = scale;
    partial void ShadowClosed() => _shadowToneTimer?.Stop();
    // Coming back at rest after the panel opened over liquid glass (ShadowAtRest), the island has stopped changing: the picture
    // is kept from the fade's first frame instead of after the settle wait. The island's last change of size may come in the
    // same frame (a frame late, dropped frames), so the picture is kept once that frame is laid out, whatever it changed.
    partial void ShadowShowing()
    {
        if (_shadowRestAt is not { } rest || Eased.Now < rest) return;
        _shadowRestAt = null;
        // Long past it (another move took over), it no longer says anything.
        if (Eased.Now < rest + (ShadowSettleMs + Soft.Quick) / 1000d)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _shadowSettle?.Stop();
                if (ShadowHost.Opacity > 0) ShadowHost.CacheMode = _shadowCache;
            });
    }

    // Over liquid glass the shadow stays under the open panel (MainWindow.LiquidShadow), but its blur is drawn again on every
    // frame the island changes size: kept through the opening and closing it added about half to the CPU time of an open and
    // close (FxPreview --shadow-cost, drawn in software; about a tenth without liquid glass). So while the panel opens or closes it is out, as without liquid glass:
    // opening, it leaves on its opening track, and fades back in over Soft.Quick as soon as the move has ended (R3 NEW-8: it
    // waited ShadowSettleMs and then a third of the move, which read as late), at most a frame (ShadowRestFrameMs) after it,
    // with the picture kept from the fade's first frame (the cache: the island no longer changes), so the fade only composes
    // it; closing, it leaves over the first ShadowLeaveShare of the move and comes back on its closing track, as without
    // liquid glass.
    private const double ShadowLeaveShare = .2, ShadowRestFrameMs = 1000 / 60d;
    // When the panel that opened over liquid glass comes to rest (Eased.Now, seconds); null when no such fade is under way.
    private double? _shadowRestAt;
    private void ShadowAtRest(bool opening, double duration, Track track, IEasingFunction easing)
    {
        static KeyTime At(double ms) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(ShadowHost.Opacity, At(0)));
        _shadowRestAt = opening ? Eased.Now + duration / 1000 : null;
        if (opening)
        {
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, At(duration * track.End), easing));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(duration + ShadowRestFrameMs)));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, At(duration + ShadowRestFrameMs + Soft.Quick), Soft.Out));
        }
        else
        {
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, At(duration * Math.Min(ShadowLeaveShare, track.Start)), easing));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(duration * track.Start)));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, At(duration * track.End), easing));
        }
        ShadowHost.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    // ----- Under liquid glass: an ambient shadow and a contact shadow, by size and by what lies behind -----
    // Apple's glass casts two: a soft, wide, faint ambient shadow and a tight contact shadow close under its edge. The
    // collapsed capsule's are small and crisp; the open panel's larger, softer and a little deeper, as a larger piece of glass
    // stands further off the page. Both are interpolated with the opening (GlassOpenness), so nothing jumps where the panel
    // comes to rest (the shadow is out while it opens or closes and fades in at rest, ShadowAtRest). The ambient shadow is
    // IslandShadow's effect (the theme's own, 12 / 4 / .22 in the liquid themes, comes back when liquid glass is not shown);
    // the contact shadow is IslandContact, collapsed otherwise. Their opacity follows what lies behind (the glass's measure,
    // GlassBackdrop.Measured, low-passed): deeper over a busy backdrop (text, a photo, a video), lighter over a plain light
    // one, where the same shadow reads heavier (Apple: shadows deepen over text and lighten over plain light surfaces).
    private readonly record struct ShadowLook(double Blur, double Depth, double Opacity);
    private static readonly ShadowLook AmbientCapsule = new(12, 4, .28), AmbientOpen = new(30, 8, .14),
        ContactCapsule = new(3, 1, .26), ContactOpen = new(6, 2, .10);
    // The tone: 1 + ShadowBusy over a busy backdrop, 1 - ShadowPlain over a plain light one; low-passed over ShadowToneMs.
    private const double ShadowBusy = .25, ShadowPlain = .3, ShadowToneMs = 1200, ShadowToneStep = .04;
    private bool _shadowLook;
    private double _shadowScale = 1, _shadowTone = 1, _shadowToneTarget = 1, _shadowToneShown = 1;
    private long _shadowToneAt;
    private DispatcherTimer? _shadowToneTimer;

    // How deep the shadow is over a backdrop whose luma lies between low and high (its 2nd and 98th percentiles): its spread
    // says how busy it is, its dark end how plain and light.
    internal static double ShadowTone(double low, double high)
    {
        static double Smooth(double a, double b, double x) { double t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
        double busy = Smooth(.15, .6, high - low), plain = Smooth(.55, .9, low) * (1 - busy);
        return 1 + ShadowBusy * busy - ShadowPlain * plain;
    }

    // Liquid glass shown or not: the two shadows by size and backdrop, or the theme's one shadow.
    // For the offscreen comparisons only (FxPreview, LIQUID_SHADOW_R13=1): the theme's one shadow under liquid glass, as before.
    internal static bool ShadowLookOff { get; set; }
    private void LiquidShadowLook(bool on)
    {
        on &= !ShadowLookOff;
        if (on == _shadowLook) { if (on) ApplyShadowLook(); return; }
        _shadowLook = on;
        IslandContact.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) ApplyShadowLook();
        else
        {
            _shadowToneTimer?.Stop();
            if (IslandShadow.Effect is DropShadowEffect shadow) PanelTheme.Dress(shadow);
            FitShadowCache(_shadowScale);
        }
        ShadowChanging();
    }

    private void ApplyShadowLook()
    {
        if (!_shadowLook || IslandShadow.Effect is not DropShadowEffect ambient || IslandContact.Effect is not DropShadowEffect contact) return;
        double open = GlassOpenness();
        Set(ambient, AmbientCapsule, AmbientOpen, open, _shadowTone);
        Set(contact, ContactCapsule, ContactOpen, open, _shadowTone);
        if (contact.Color != ambient.Color) contact.Color = ambient.Color;
        _shadowToneShown = _shadowTone;
        FitShadowCache(_shadowScale);

        static void Set(DropShadowEffect effect, ShadowLook closed, ShadowLook opened, double t, double tone)
        {
            double blur = closed.Blur + (opened.Blur - closed.Blur) * t, depth = closed.Depth + (opened.Depth - closed.Depth) * t;
            double opacity = Math.Clamp((closed.Opacity + (opened.Opacity - closed.Opacity) * t) * tone, 0, 1);
            if (effect.BlurRadius != blur) effect.BlurRadius = blur;
            if (effect.ShadowDepth != depth) effect.ShadowDepth = depth;
            if (effect.Opacity != opacity) effect.Opacity = opacity;
        }
    }

    // A new measure of what lies behind (GlassBackdrop.Measured). The first is taken at once; later ones are eased toward over
    // ShadowToneMs by a timer that runs only until it is there, and the shadow is drawn again only in steps of ShadowToneStep
    // and once it has arrived (each step blurs its cached picture once).
    private void ShadowBackdrop(float low, float high)
    {
        _shadowToneTarget = ShadowTone(low, high);
        long now = Environment.TickCount64;
        if (_shadowToneAt == 0) { _shadowToneAt = now; _shadowTone = _shadowToneTarget; ApplyShadowLook(); return; }
        if (Math.Abs(_shadowToneTarget - _shadowTone) < .005 || _shadowToneTimer?.IsEnabled == true) return;
        if (_shadowToneTimer is null)
        {
            _shadowToneTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _shadowToneTimer.Tick += (_, _) => EaseShadowTone();
        }
        _shadowToneAt = now;
        _shadowToneTimer.Start();
    }

    private void EaseShadowTone()
    {
        long now = Environment.TickCount64;
        double dt = now - _shadowToneAt;
        _shadowToneAt = now;
        _shadowTone += (_shadowToneTarget - _shadowTone) * (1 - Math.Exp(-dt / ShadowToneMs));
        bool there = Math.Abs(_shadowToneTarget - _shadowTone) < .005;
        if (there) { _shadowTone = _shadowToneTarget; _shadowToneTimer!.Stop(); }
        if (there || Math.Abs(_shadowTone - _shadowToneShown) >= ShadowToneStep) ApplyShadowLook();
    }

}
