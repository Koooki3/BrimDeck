namespace BrimDeck.Core;

// Controls the acrylic's base luminosity veil. The backdrop's readability requirement can
// raise this floor, and is independent of the user's preference.
public static class GlassMaterial
{
    public const int DefaultTransparency = 50;
    public const double DefaultCollapsedVeil = .30, DefaultExpandedVeil = .35;
    public const double MaximumVeil = .95;

    public static int NormalizeTransparency(int percent) => Math.Clamp(percent, 0, 100);

    public static double DarkBackdropLimit(int transparency) => .26 + .06 * Math.Max(0, NormalizeTransparency(transparency) - 50) / 50;

    // Quantize the grain so a whole slider drag uses a small set of frozen WPF brushes.
    public static double GrainStrength(int transparency)
    {
        double amount = NormalizeTransparency(transparency) / 100d;
        return Math.Round((1 + (.5 - amount)) * 10) / 10;
    }

    public static double RimStrength(int transparency, double openness)
    {
        double amount = NormalizeTransparency(transparency) / 100d;
        return (.85 + .30 * amount) * (1 - .20 * Math.Clamp(openness, 0, 1));
    }


    // A lone demanding sample gets a small response. When two readings agree, apply the
    // shared readability floor promptly. Release slowly after both readings require less.
    public static double FollowBackdrop(double shown, double required, double previous)
    {
        if (required > shown)
            return Math.Min(required, Math.Max(shown + .025, previous));
        return Math.Max(shown - .025, Math.Min(shown, Math.Max(required, previous)));
    }

    public static double LuminosityOpacity(double required, double openness, int transparency)
    {
        double amount = NormalizeTransparency(transparency) / 100d;
        double baseVeil = amount <= .5 ? DefaultCollapsedVeil + (.5 - amount) * .50
            : DefaultCollapsedVeil - (amount - .5) * .20;
        double openingVeil = (DefaultExpandedVeil - DefaultCollapsedVeil) * Math.Clamp(openness, 0, 1);
        return Math.Ceiling(Math.Clamp(Math.Max(required, baseVeil + openingVeil), 0, MaximumVeil) * 20 - 1e-9) / 20;
    }
}

// The opacity actually presented to the compositor, distinct from the sampler's readability target.
public struct GlassVeilTransition
{
    private double _shown, _from, _target;
    private long _start;

    public bool Active => _shown != _target;

    public double Update(double target, long now, bool animate, bool sampleChanged = false)
    {
        if (!animate || (!Active && (!sampleChanged || Math.Abs(target - _shown) < .075)))
            return _shown = _target = target;

        if (target != _target)
        {
            _from = _shown;
            _target = target;
            _start = now;
            return _shown;
        }

        double duration = _target > _from ? 180 : 260;
        double progress = Math.Clamp((now - _start) / duration, 0, 1);
        double weight = progress * progress * (3 - 2 * progress);
        // Use the same .05 opacity steps as the brush cache. A retarget starts at the last displayed step.
        _shown = progress >= 1 ? _target : Math.Round((_from + (_target - _from) * weight) * 20) / 20;
        return _shown;
    }
}
