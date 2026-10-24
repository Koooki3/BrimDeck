using System.Windows;
using System.Windows.Media.Animation;
using Track = BrimDeck.Core.Notices.NoticeMotion.Track;

namespace BrimDeck;

// One move of the panel or a notice played on tracks: each animation starts and ends at fractions of the move's duration
// (NoticeMotion.Track), and replaces what ran on its property before. The panel (MainWindow) and its notices (NoticeWindow)
// share these, so the two keep the same clock.
internal static class Tracks
{
    public static T Timed<T>(T animation, double duration, Track track) where T : AnimationTimeline
    {
        animation.BeginTime = TimeSpan.FromMilliseconds(duration * track.Start);
        animation.Duration = TimeSpan.FromMilliseconds(duration * track.Length);
        return animation;
    }

    // Without a duration (no animations) the value is set at once and completed runs straight away.
    public static void Animate(IAnimatable target, DependencyProperty property, double value, double duration, Track track, IEasingFunction easing, Action? completed = null)
    {
        if (duration <= 0 || !double.IsFinite(value)) { SetImmediate(target, property, double.IsFinite(value) ? value : 0d); completed?.Invoke(); return; }
        var animation = Timed(new DoubleAnimation { To = value, EasingFunction = easing }, duration, track);
        if (completed is not null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    // Drops whatever animates the property and sets its value.
    public static void SetImmediate(IAnimatable target, DependencyProperty property, object value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }
}
