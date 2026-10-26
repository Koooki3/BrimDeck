using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static BrimDeck.Tracks;

namespace BrimDeck;

// What a theme's material asks of the island beyond its colors (PanelTheme.Dress): glass lets the desktop through, blurred where
// Windows can (GlassBackdrop), so its text cannot use ClearType, and the copy that casts the shadow, lying under the island,
// would show through it; pixel art draws its text unsmoothed and casts a stepped shadow.
public partial class MainWindow
{
    private (double X, double Y, Size Size, double Shoulder, CornerRadius Corners)? _shadowCutKey;
    private bool _shadowCut;
    // The blur under glass (GlassBackdrop), made with the window; it stays out of the way for other themes.
    private GlassBackdrop? _glass;
    // The open panel's height the last time it opened (SetExpanded): how far the island is open is read from its height between
    // the collapsed shape's and this, so it follows the opening motion as it is drawn.
    private double _glassOpenHeight;
    internal double GlassOpenness()
    {
        double closed = Collapsed.Surface.Height, open = _glassOpenHeight, now = Island.ActualHeight;
        if (!(open > closed + 1) || !double.IsFinite(now)) return _expanded ? 1 : 0;
        return Math.Clamp((now - closed) / (open - closed), 0, 1);
    }

    // The collapsed island's content row for the glass (GlassBackdrop.ContentArea), which calms the backdrop behind it: the
    // parts of the compact row shown now (the cover, the title, the clock, the readings, the rings, an alert's text), in DIPs
    // from the island's top-left as drawn (the compact content's own scale included), inside the island. None while the panel
    // is open or opening, for the line (it shows no content), or while nothing is shown.
    // Asked on every frame the glass follows: the parts are walked again only after a layout pass (Island.LayoutUpdated, which
    // also follows the row's parts coming, going and fading, CompactRow.ArrangeOverride) or a scale of the compact content too
    // small to scale from; the compact content's own scale (CompactScale, about its middle) is applied to the area kept.
    private Rect _contentArea = Rect.Empty;
    private Point _contentAreaMiddle;
    private (double X, double Y) _contentAreaScale;
    private bool _contentAreaStale = true;
    internal Rect? GlassContentArea()
    {
        if (_expanded || Compact.Visibility != Visibility.Visible || Compact.Opacity < .01 || !Island.IsVisible) return null;
        double sx = CompactScale.ScaleX, sy = CompactScale.ScaleY;
        if (_contentAreaStale || !(Math.Abs(_contentAreaScale.X) > .01 && Math.Abs(_contentAreaScale.Y) > .01))
        {
            _contentArea = ContentAreaNow();
            _contentAreaScale = (sx, sy);
            _contentAreaStale = false;
        }
        var area = _contentArea;
        if (area.IsEmpty) return null;
        if (sx != _contentAreaScale.X || sy != _contentAreaScale.Y)
        {
            double kx = sx / _contentAreaScale.X, ky = sy / _contentAreaScale.Y, mx = _contentAreaMiddle.X, my = _contentAreaMiddle.Y;
            double left = mx + (area.Left - mx) * kx, right = mx + (area.Right - mx) * kx, top = my + (area.Top - my) * ky, bottom = my + (area.Bottom - my) * ky;
            area = new Rect(new Point(left, top), new Point(right, bottom));
        }
        area.Intersect(new Rect(Island.RenderSize));
        return area.IsEmpty || area.Width < 1 || area.Height < 1 ? null : area;
    }

    // The row's parts as drawn now, in the island's DIPs (not cut to it), and the compact content's middle there (the fixed
    // point of its scale).
    private Rect ContentAreaNow()
    {
        var row = CompactAlert.Visibility == Visibility.Visible ? CompactAlert : CompactRings;
        if (row.Visibility != Visibility.Visible) return Rect.Empty;
        var area = Rect.Empty;
        try
        {
            _contentAreaMiddle = Compact.TransformToAncestor(Island).Transform(new Point(Compact.ActualWidth / 2, Compact.ActualHeight / 2));
            // The row's parts (CompactRow and the alert's row are panels of them); anything else as a whole.
            var children = row.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is not FrameworkElement { IsVisible: true } child) continue;
                if (child is Panel { Children: { Count: > 0 } parts })
                    for (int j = 0; j < parts.Count; j++) { if (parts[j] is FrameworkElement part) Add(part, ref area); }
                else Add(child, ref area);
            }
        }
        catch (InvalidOperationException) { return Rect.Empty; }
        return area;

        void Add(FrameworkElement part, ref Rect into)
        {
            if (!part.IsVisible || part.Opacity < .01 || part.ActualWidth <= 0 || part.ActualHeight <= 0) return;
            into.Union(part.TransformToAncestor(Island).TransformBounds(new Rect(part.RenderSize)));
        }
    }

    // The open panel's text and content regions (round 12b: the glass covers behind them as far as the text needs, and stays
    // translucent between them), in the island's DIPs, grown by TextGrow: every leaf of the visual tree shown (a text, an icon,
    // a cover, a custom drawing), a text block as a whole. Walked again after a layout pass, at most every TextRegionsMs; a
    // new list only when the regions changed (the glass draws again only then).
    private const double TextGrow = 6, TextRegionsMs = 100;
    private readonly List<Rect> _textRegions = [];
    private Rect[]? _textShown;
    private bool _textStale = true;
    private long _textAt = long.MinValue;
    internal IReadOnlyList<Rect>? GlassTextRegions()
    {
        if (!_expanded || !Island.IsVisible || ExpandedContent.Visibility != Visibility.Visible) return null;
        long now = Environment.TickCount64;
        if (_textStale && (_textShown is null || _offscreen is not null || now - _textAt >= TextRegionsMs))
        {
            _textStale = false; _textAt = now;
            _textRegions.Clear();
            try { Leaves(ExpandedContent); }
            catch (InvalidOperationException) { }
            if (_textShown is null || !_textShown.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textRegions))) _textShown = [.. _textRegions];
        }
        return _textShown;

        void Leaves(DependencyObject node)
        {
            if (node is UIElement { IsVisible: false } or UIElement { Opacity: < .01 }) return;
            int count = VisualTreeHelper.GetChildrenCount(node);
            if (GlassRegionsWhole)
            {
                // Round 13's rule (kept for the comparisons): every leaf as a whole.
                bool drawn = count > 0;
                for (int i = 0; drawn && i < count; i++) drawn = VisualTreeHelper.GetChild(node, i) is not UIElement;
                if (node is FrameworkElement element && (count == 0 || drawn || element is TextBlock))
                {
                    if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
                    if (drawn && element.ActualWidth * element.ActualHeight > Island.ActualWidth * Island.ActualHeight / 3) return;
                    var box = element.TransformToAncestor(Island).TransformBounds(new Rect(element.RenderSize));
                    box.Inflate(TextGrow, TextGrow);
                    _textRegions.Add(box);
                    return;
                }
                for (int i = 0; i < count; i++) Leaves(VisualTreeHelper.GetChild(node, i));
                return;
            }
            // Round 13b: only the text and the glyphs themselves - every run of glyphs a visual draws (text blocks and the pages'
            // drawn text alike, read off the visual's own drawing) and the icons (paths) - so the glass between and around
            // them keeps its translucency. Covers, plates, tracks and bars are left to the glass.
            if (node is Visual visual)
            {
                if (node is System.Windows.Shapes.Path { ActualWidth: > 0, ActualHeight: > 0 } icon)
                    Add(icon.TransformToAncestor(Island).TransformBounds(new Rect(icon.RenderSize)));
                else if (VisualTreeHelper.GetDrawing(visual) is { } drawing)
                {
                    GeneralTransform? toIsland = null;
                    Glyphs(drawing, Matrix.Identity, ref toIsland, visual);
                }
            }
            for (int i = 0; i < count; i++) Leaves(VisualTreeHelper.GetChild(node, i));
        }

        void Glyphs(Drawing drawing, Matrix within, ref GeneralTransform? toIsland, Visual owner)
        {
            switch (drawing)
            {
                case GlyphRunDrawing run:
                    var bounds = run.Bounds;
                    if (bounds.IsEmpty) return;
                    bounds.Transform(within);
                    toIsland ??= owner.TransformToAncestor(Island);
                    Add(toIsland.TransformBounds(bounds));
                    return;
                case DrawingGroup group:
                    var inner = group.Transform is { } transform ? Matrix.Multiply(transform.Value, within) : within;
                    foreach (var child in group.Children) Glyphs(child, inner, ref toIsland, owner);
                    return;
            }
        }

        void Add(Rect box)
        {
            if (box.IsEmpty || box.Width <= 0 || box.Height <= 0) return;
            box.Inflate(TextGrow, TextGrow);
            _textRegions.Add(box);
        }
    }

    // Round 13's whole-leaf regions instead of round 13b's glyphs (the comparisons only, FxPreview).
    internal static bool GlassRegionsWhole { get; set; }

    // Round 13b: the text on the glass carries its own legibility, as Apple's vibrant text does: a soft halo about a pixel wide in
    // the glass's own tint behind every glyph (white on the light look, the dark tint on the dark), so the glass behind need not
    // be whitened to keep it legible. One effect over the panel's and the row's content, only while liquid glass shows.
    internal static bool GlassHaloOff { get; set; }
    private System.Windows.Media.Effects.DropShadowEffect? _halo;
    private void GlassHalo(bool shown)
    {
        if (!shown || GlassHaloOff)
        {
            if (ExpandedContent.Effect == _halo && _halo is not null) ExpandedContent.ClearValue(EffectProperty);
            if (Compact.Effect == _halo && _halo is not null) Compact.ClearValue(EffectProperty);
            return;
        }
        bool dark = PanelTheme.Current.IsDark;
        _halo ??= new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 3, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality };
        _halo.Color = dark ? Color.FromRgb(0x12, 0x14, 0x19) : Colors.White;
        _halo.Opacity = dark ? .45 : .7;
        ExpandedContent.Effect = _halo;
        Compact.Effect = _halo;
    }

    // The pointer over the island (DIP, island coordinates; null when it left) and whether its button is down: the glass's
    // hover and press (GlassBackdrop.Pointer) and the press's jelly.
    private void GlassPress(Point? at, bool down)
    {
        _glass?.Pointer(at, down);
        Jelly(down);
    }

    private void ApplyMaterial()
    {
        if (_glass is null)
        {
            _glass = new GlassBackdrop(this, Island);
            // Over a held blur the tint thins with it, frame by frame. Liquid glass draws its own tint: the island's surface
            // steps aside under it altogether (GlassBackdrop.SurfaceAside), whatever the blur's tint would be.
            _glass.Held += held => ApplyHeld(_glass.SurfaceAside, held);
            // The panel opening takes the island out of a press at once: a squash or spring left on the open panel would hold
            // it off the screen's top edge.
            Island.SizeChanged += (_, _) => { if (_expanded) ResetJelly(); };
            _glass.LiquidTint = () => _surface.Color.A / 255d;
            // Automatic liquid glass takes its look from what lies behind the island (App.BackdropMeasured).
            _glass.AutoLook = () => _app.AutoLookFrom(this);
            _glass.Look += (dark, instant) => _app.TurnGlass(this, dark, instant);
            _glass.Crossed += () => _app.GlassCrossed(this);
            _glass.Openness = GlassOpenness;
            _glass.ContentArea = GlassContentArea;
            _glass.TextRegions = GlassTextRegions;
            Island.LayoutUpdated += (_, _) => { _contentAreaStale = true; _textStale = true; };
            OffscreenGlass();
            // Liquid glass brightens and lights its rim from the pointer and presses with it: where the pointer is over the
            // island, and whether the button is down (seen even when a button under it handles the click).
            Island.MouseMove += (_, e) => _glass.Pointer(e.GetPosition(Island), e.LeftButton == MouseButtonState.Pressed);
            Island.MouseLeave += (_, _) => GlassPress(null, false);
            Island.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) => GlassPress(e.GetPosition(Island), true)), true);
            Island.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) => GlassPress(e.GetPosition(Island), false)), true);
        }
        _glass.Update();
        bool glass = PanelTheme.GlassShown;
        var palette = PanelTheme.Current;
        // ClearType needs an opaque background under the text; over glass it washes the text out (PanelTheme.ClearType).
        if (Island.Child is Grid content) RenderOptions.SetClearTypeHint(content, PanelTheme.ClearType);
        if (palette.Effects.Pixel > 0) TextOptions.SetTextRenderingMode(Island, TextRenderingMode.Aliased); else Island.ClearValue(TextOptions.TextRenderingModeProperty);
        if (IslandShadow.Children.OfType<IslandBorder>().FirstOrDefault() is { } caster) caster.Pixel = palette.Effects.Pixel;
        if (glass == _shadowCut) return;
        _shadowCut = glass;
        if (glass) { Island.LayoutUpdated += CutShadow; CutShadow(null, EventArgs.Empty); }
        else { Island.LayoutUpdated -= CutShadow; ShadowHost.Clip = null; _shadowCutKey = null; }
    }

    // The press of liquid-glass-web's glass (xiaofangstudio, MIT): pressed, it squashes to scale(1 + k, 1 - .45 k) about its
    // middle, going down as every press in BrimDeck does (Soft.Pressed: Soft.Press on the monitor's curve); released, it springs
    // back over JellyBackMs on the source's 1 - e^(-5t) cos 9t (SpringEase, which lands exactly on 1). k is the source's .1 for
    // a glass as wide as its buttons (JellyWidth), less on a wider island. Only the collapsed island (a press in the open panel
    // is a press of one of its controls), only over liquid glass, and not with animations off. The glass follows the island's
    // shape through its transform (GlassBackdrop.Measure), as it follows the boot's dot. Each animation only covers the way and
    // ends without holding the scale (Soft.Glide's pattern), so the scale left behind is exactly the one set: 1 once let go.
    private ScaleTransform? _jelly;
    private bool _jellyDown;
    private const double JellyK = .1, JellyWidth = 140, JellyBackMs = 500;
    private static readonly SpringEase JellySpring = Frozen(new SpringEase());
    private static SpringEase Frozen(SpringEase ease) { ease.Freeze(); return ease; }

    // Out of any press at once, no spring: the panel opening.
    private void ResetJelly()
    {
        _jellyDown = false;
        if (_jelly is null || _jelly.ScaleX == 1 && _jelly.ScaleY == 1 && !_jelly.HasAnimatedProperties) return;
        SetImmediate(_jelly, ScaleTransform.ScaleXProperty, 1d);
        SetImmediate(_jelly, ScaleTransform.ScaleYProperty, 1d);
    }

    private void Jelly(bool down)
    {
        if (down == _jellyDown) return;
        if (down && (_expanded || !LiquidGlassShown || !Soft.Enabled)) return;
        if (!down && _expanded) { ResetJelly(); return; }
        _jellyDown = down;
        if (_jelly is null)
        {
            if (_dotSquash is null || _dotLift is null)
            {
                _dotSquash = new ScaleTransform(); _dotLift = new TranslateTransform();
                Island.RenderTransformOrigin = new Point(.5, 1);
                Island.RenderTransform = new TransformGroup { Children = { _dotSquash, _dotLift } };
            }
            if (Island.RenderTransform is not TransformGroup group) return;
            _jelly = new ScaleTransform();
            group.Children.Insert(0, _jelly);
        }
        // About the middle: the group's origin is the bottom middle (the dot's floor).
        _jelly.CenterY = -Island.ActualHeight / 2;
        if (down)
        {
            double k = JellyK * Math.Min(1, JellyWidth / Math.Max(1, Island.ActualWidth));
            Soft.Glide(_jelly, ScaleTransform.ScaleXProperty, 1 + k, true, Soft.Press);
            Soft.Glide(_jelly, ScaleTransform.ScaleYProperty, 1 - .45 * k, true, Soft.Press);
            _glass?.Follow(Soft.Press + 50);
            return;
        }
        Back(_jelly, ScaleTransform.ScaleXProperty);
        Back(_jelly, ScaleTransform.ScaleYProperty);
        _glass?.Follow(JellyBackMs + 50);

        // From where the press has it now (perhaps still going down) back to 1, the value set before the spring starts.
        static void Back(ScaleTransform jelly, DependencyProperty property)
        {
            double from = (double)jelly.GetValue(property);
            jelly.BeginAnimation(property, null);
            jelly.SetValue(property, 1d);
            if (from != 1)
                jelly.BeginAnimation(property, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(JellyBackMs)) { EasingFunction = JellySpring, FillBehavior = FillBehavior.Stop });
        }
    }

    // What the glass reported for a frame (GlassBackdrop.Held): the surface's opacity over it, and the painted rim and edge.
    private void ApplyHeld(bool aside, double held)
    {
        _surface.Opacity = GlassBackdrop.SurfaceOpacity(aside, held, _surface.Color.A / 255d);
        LiquidEdge(aside);
    }

    // Automatic liquid glass turning: the glass eases toward the new look ahead of the theme (GlassBackdrop.Lead).
    internal void LeadGlass(bool? dark) => _glass?.Lead(dark);
    // Whether this island's liquid glass can be shown (GlassBackdrop.LiquidUsable); true before the glass is made.
    internal bool LiquidUsable => _glass?.LiquidUsable != false;

    // Liquid glass draws its own rim light and edge, following the pointer; the island's painted rim and edge light would lie
    // over it as a stroke (a white ring on dark glass, a 3D button's edge), so they step aside while it is shown and come back
    // with the blur. PanelTheme.Dress sets them again on a theme change, and the next frame takes them away again.
    private bool _liquidEdge;
    private void LiquidEdge(bool shown)
    {
        GlassHalo(shown);
        LiquidShadow(shown);
        if (shown)
        {
            if (Island.RimWidth == 0 && Island.EdgeBrush is null) return;
            Island.RimWidth = 0; Island.EdgeBrush = null; _liquidEdge = true;
            _ambient?.Update();
        }
        else if (_liquidEdge)
        {
            _liquidEdge = false;
            var palette = PanelTheme.Current;
            Island.RimWidth = palette.Effects.RimWidth;
            Island.EdgeBrush = PanelTheme.GlassShown ? PanelTheme.EdgeBrush(palette) : null;
            _ambient?.Update();
        }
    }

    // The shadow's caster (the copy of the island under it) takes the island's surface brush, which liquid glass clears (its
    // opacity 0: the glass draws the island): the shadow would be cast by nothing. While liquid glass is shown the caster is
    // opaque; its colour does not matter (the effect's colour is the shadow's), only that it casts the whole shape.
    private BindingBase? _casterBrush;
    private void LiquidShadow(bool shown)
    {
        if (IslandShadow.Children.OfType<IslandBorder>().FirstOrDefault() is not { } caster) return;
        if (shown)
        {
            if (ReferenceEquals(caster.Background, Brushes.Black)) return;
            _casterBrush ??= BindingOperations.GetBindingBase(caster, Border.BackgroundProperty);
            caster.Background = Brushes.Black;
            ShadowChanging();
        }
        else if (_casterBrush is not null && ReferenceEquals(caster.Background, Brushes.Black))
        {
            caster.SetBinding(Border.BackgroundProperty, _casterBrush);
            ShadowChanging();
        }
    }

    // Glass: the shadow is drawn only outside the island. Layout runs on every frame the island changes size or place, and
    // only then; the clip is made again only when the outline has moved.
    private void CutShadow(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(Island) is null || !Island.IsVisible) return;
        var at = Island.TranslatePoint(new Point(), ShadowHost);
        var key = (at.X, at.Y, Island.RenderSize, Island.ShoulderRadius, Island.CornerRadius);
        if (_shadowCutKey == key) return;
        _shadowCutKey = key;
        var outline = IslandBorder.BuildOutline(Island.RenderSize.Width, Island.RenderSize.Height, Island.ShoulderRadius, Island.CornerRadius, 0,
            IslandBorder.PixelStep(Island.Pixel, Island)).Clone();
        outline.Transform = new TranslateTransform(at.X, at.Y);
        // Everything the shadow can reach, less the island.
        var reach = new RectangleGeometry(new Rect(-4096, -4096, 8192, 8192));
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude, reach, outline);
        clip.Freeze();
        ShadowHost.Clip = clip;
    }
}
