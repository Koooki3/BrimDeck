using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace BrimDeck;

// The island's shadow is drawn from a cached picture (ShadowHost's BitmapCache). WPF does not keep an effect's output: whenever
// anything drawn over the shadow changes (the scrolling title, the equalizer, a carousel, the readings), it draws the shadow's
// subtree again and blurs it again, on every frame of that motion. The cache keeps the blurred picture, so those frames only
// compose it. While the island changes size the picture would be made again on every frame, which costs more than blurring
// directly, so the cache is dropped then and comes back once the size has held still for a moment.
public partial class MainWindow
{
    private BitmapCache? _shadowCache;
    private DispatcherTimer? _shadowSettle;

    private void InitializeShadow()
    {
        _shadowCache = ShadowHost.CacheMode as BitmapCache;
        _shadowSettle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _shadowSettle.Tick += (_, _) => { _shadowSettle.Stop(); ShadowHost.CacheMode = _shadowCache; };
        // The host's size follows the island's margin as well (the island sinks into place as the panel closes).
        void Moved(object sender, SizeChangedEventArgs e) { ShadowHost.CacheMode = null; _shadowSettle.Stop(); _shadowSettle.Start(); }
        IslandShadow.SizeChanged += Moved; ShadowHost.SizeChanged += Moved;
        SourceInitialized += (_, _) => FitShadowCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
        Closed += (_, _) => _shadowSettle.Stop();
    }

    // The picture is drawn at the display's scale and reaches past the blur by whole device pixels (ShadowBounds): a picture
    // whose edge fell between pixels was resampled when drawn, and differed from the shadow drawn directly by up to 44 levels
    // along the island's edge at 125 %. Laid out on whole device pixels (UseLayoutRounding), it matches the uncached shadow
    // exactly at every scale (measured at 100 to 200 %). Asked again when the theme or the scale changes.
    private void FitShadowCache(double scale)
    {
        if (_shadowCache is not { } cache || IslandShadow.Effect is not DropShadowEffect effect || !(scale > 0)) return;
        if (cache.RenderAtScale != scale) cache.RenderAtScale = scale;
        double reach = Math.Ceiling((effect.BlurRadius + effect.ShadowDepth + 2) * scale) / scale;
        var margin = new Thickness(-reach);
        if (ShadowBounds.Margin != margin) ShadowBounds.Margin = margin;
    }
}
