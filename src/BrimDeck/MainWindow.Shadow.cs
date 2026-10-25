using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BrimDeck.Core;
using Track = BrimDeck.Core.Notices.NoticeMotion.Track;

namespace BrimDeck;

// The island's shadow is drawn from a cached picture (ShadowHost's BitmapCache). WPF does not keep an effect's output: whenever
// anything drawn over the shadow changes (the scrolling title, the equalizer, a carousel, the readings), it draws the shadow's
// subtree again and blurs it again, on every frame of that motion. The cache keeps the blurred picture, so those frames only
// compose it. While the island changes size the picture would be made again on every frame, which costs more than blurring
// directly, so the cache is dropped then and comes back once the size has held still for a moment. The same holds while what
// the picture shows changes in place (the surface color, the corners), and while the shadow is not shown at all there is
// nothing to keep. The shadow fades on ShadowHost, outside the cache, so a fade only composes the kept picture.
public partial class MainWindow
{
    private BitmapCache? _shadowCache;
    private DispatcherTimer? _shadowSettle;

    private void InitializeShadow()
    {
        _shadowCache = ShadowHost.CacheMode as BitmapCache;
        _shadowSettle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ShadowSettleMs) };
        _shadowSettle.Tick += (_, _) => { _shadowSettle.Stop(); if (ShadowHost.Opacity > 0) ShadowHost.CacheMode = _shadowCache; };
        // The host's size follows the island's margin as well (the island sinks into place as the panel closes).
        IslandShadow.SizeChanged += (_, _) => ShadowChanging(); ShadowHost.SizeChanged += (_, _) => ShadowChanging();
        // The color, corners and shoulders the shadow takes from the island; Changed is raised on each frame of a color animation.
        _surface.Changed += (_, _) => ShadowChanging();
        // A descriptor's listener is held by a static table: each is removed when the window closes, or a panel closed with its
        // monitor (or its panel turned off) would never be freed.
        var listeners = new List<(DependencyPropertyDescriptor Property, object Element, EventHandler Handler)>();
        void Listen(DependencyProperty property, Type owner, object element, EventHandler handler)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, owner);
            descriptor.AddValueChanged(element, handler);
            listeners.Add((descriptor, element, handler));
        }
        Listen(Border.CornerRadiusProperty, typeof(IslandBorder), Island, (_, _) => ShadowChanging());
        Listen(IslandBorder.ShoulderRadiusProperty, typeof(IslandBorder), Island, (_, _) => ShadowChanging());
        // Hidden (the open panel, the line), the picture is let go; shown again, it is kept at once unless a change is under way.
        // Coming back at rest after the panel opened over liquid glass (ShadowAtRest), the island has stopped changing: the picture
        // is kept from the fade's first frame instead of after the settle wait. The island's last change of size may come in the
        // same frame (a frame late, dropped frames), so the picture is kept once that frame is laid out, whatever it changed.
        Listen(OpacityProperty, typeof(Grid), ShadowHost, (_, _) =>
        {
            if (ShadowHost.Opacity <= 0) { ShadowHost.CacheMode = null; return; }
            if (_shadowRestAt is { } rest && Eased.Now >= rest)
            {
                _shadowRestAt = null;
                // Long past it (another move took over), it no longer says anything.
                if (Eased.Now < rest + (ShadowSettleMs + Soft.Quick) / 1000d)
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                    {
                        _shadowSettle.Stop();
                        if (ShadowHost.Opacity > 0) ShadowHost.CacheMode = _shadowCache;
                    });
            }
            if (ShadowHost.CacheMode is null && !_shadowSettle.IsEnabled) ShadowHost.CacheMode = _shadowCache;
        });
        if (ShadowHost.Opacity <= 0) ShadowHost.CacheMode = null;
        SourceInitialized += (_, _) => FitShadowCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
        Closed += (_, _) =>
        {
            _shadowSettle.Stop();
            foreach (var (property, element, handler) in listeners) property.RemoveValueChanged(element, handler);
            listeners.Clear();
        };
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
    private const int ShadowSettleMs = 250;
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

    private void ShadowChanging()
    {
        if (_shadowSettle is null) return;
        ShadowHost.CacheMode = null; _shadowSettle.Stop(); _shadowSettle.Start();
    }

    // The picture is drawn at the display's scale and reaches past the blur by whole device pixels (ShadowBounds): a picture
    // whose edge fell between pixels was resampled when drawn, and differed from the shadow drawn directly by up to 44 levels
    // along the island's edge at 125 %. Laid out on whole device pixels (UseLayoutRounding), it matches the uncached shadow
    // exactly at every scale (measured at 100 to 200 %). WPF already draws a cached picture at the system's DPI, so the cache
    // is asked only for what this monitor's scale adds to it. Asked again when the theme or the scale changes.
    private void FitShadowCache(double scale)
    {
        if (_shadowCache is not { } cache || IslandShadow.Effect is not DropShadowEffect effect || !(scale > 0)) return;
        double render = scale / SystemScale;
        if (cache.RenderAtScale != render) cache.RenderAtScale = render;
        double reach = Math.Ceiling((effect.BlurRadius + effect.ShadowDepth + 2) * scale) / scale;
        var margin = new Thickness(-reach);
        if (ShadowBounds.Margin != margin) ShadowBounds.Margin = margin;
    }

    private static readonly double SystemScale = GetDpiForSystem() is var dpi and > 0 ? dpi / 96d : 1;
    [DllImport("user32")] private static extern uint GetDpiForSystem();
}
