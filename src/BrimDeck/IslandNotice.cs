using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// One panel's arriving notices, shown on the island itself. The island keeps its size and place: it moves once like a viscous
// liquid (IslandLiquid: the capsule flings bubbles off its ends, the notch's edge ripples, the indicator beads and breathes) and
// shows the notice's icon and words inside its outline, scrolling them when they are longer than it. While the notice shows, the
// pointer resting on the island opens the panel on the notices page with this notice in front; there is no card of its own.
//
// The window lies over the island with room around it for the bubbles and the backlight, and is shown only while a notice is.
// While it shows, the island underneath is hidden (MainWindow.SetIslandCovered) and drawn here in the same outline and material.
// It never takes focus.
internal sealed class IslandNotice : Window
{
    private static readonly Color LineFlash = Color.FromRgb(0xF5, 0xF5, 0xF7), GlowColor = Color.FromRgb(0xF0, 0xF3, 0xF8);
    // The prototype's pace and intensity (the user's call, 2026-10-30: faster looked worse than the prototype).
    private const double Pace = 1, Fury = 5;
    // The words leave quickly and arrive a little slower: the island's own content and the notice's take turns, by opacity alone.
    internal const int FadeOut = 150, FadeIn = 220;
    private readonly MainWindow _deck;
    private readonly App _app;
    private readonly Canvas _stage = new() { Background = null };
    private readonly Layer _shadow = new(), _glowWide = new(), _glowCore = new(), _bloomWide = new(), _bloomMiddle = new(), _bloomSmall = new(), _body = new(), _parts = new();
    private readonly Border _hit = new() { Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Canvas _content = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly PromptTimer _hoverOpen;
    private AttentionItem? _item;
    private CollapsedShape _shape = null!;
    private NoticeLook _look = NoticeLook.Current();
    private Rect _stageRect, _island;
    private bool _leaving, _arrivedUnderPointer, _held, _closed;
    private int _generation;

    // The motion: its model, when it started, the factor from its design size to DIPs, and the shapes it draws each frame.
    private IslandLiquid? _liquid;
    private double _start, _t, _k;
    private readonly List<IslandLiquid.Circle> _circles = [];
    private readonly List<IslandLiquid.Neck> _necks = [];
    private Geometry? _notchGlowCore, _notchGlowWide;

    public IslandNotice(MainWindow deck, App app)
    {
        _deck = deck; _app = app;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Title = "BrimDeck notices"; UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Content = _stage;
        // Back to front: the shadow, the backlight (or the indicator's bloom) behind the island, the island, the beads that trail its
        // ripples, the content and the area that takes the pointer.
        foreach (var layer in new[] { _shadow, _glowWide, _glowCore, _bloomWide, _bloomMiddle, _bloomSmall, _body, _parts }) _stage.Children.Add(layer);
        _stage.Children.Add(_content); _stage.Children.Add(_hit);
        _shadow.Draw = DrawShadow; _glowWide.Draw = d => DrawGlow(d, wide: true); _glowCore.Draw = d => DrawGlow(d, wide: false);
        _bloomWide.Draw = d => DrawBloom(d, 3, 4, .55); _bloomMiddle.Draw = d => DrawBloom(d, 1.5, 1.5, .6); _bloomSmall.Draw = d => DrawBloom(d, .5, 0, .8);
        _body.Draw = DrawBody; _parts.Draw = DrawParts;
        RenderOptions.SetClearTypeHint(_content, ClearTypeHint.Enabled);
        AutomationProperties.SetName(_hit, Loc.T("灵动提示", "Live notice"));
        _hoverOpen = new(Dispatcher, () => { _hoverOpen!.Stop(); if (_item is not null && !_leaving && _hit.IsMouseOver) OpenPage(byKey: false); });
        _hit.MouseEnter += (_, _) => PointerEntered();
        _hit.MouseLeave += (_, _) => PointerLeft();
        _hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_item is not null && !_leaving) OpenPage(byKey: false); };
        _hit.MouseRightButtonUp += (_, e) => e.Handled = true;
        SourceInitialized += (_, _) => WindowsHost.ConfigureOverlay(this);
        _app.Notices.Icons.Loaded += IconLoaded;
        Closed += (_, _) => { _closed = true; StopMotion(); _hoverOpen.Stop(); _app.Notices.Icons.Loaded -= IconLoaded; _item = null; WatchLive(); };
        DpiChanged += (_, _) => { if (_item is not null && !_leaving) Dress(); };
    }

    // The window's handle, the theme's materials and the fonts, made beforehand while nothing moves; nothing is shown.
    internal void Prepare()
    {
        new WindowInteropHelper(this).EnsureHandle();
        Dress();
        if (IsVisible || _item is not null) return;
        double left = Left, top = Top, width = Width, height = Height, opacity = Opacity;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000; Width = 16; Height = 16; Opacity = 0;
        try { Show(); UpdateLayout(); Hide(); }
        finally { Left = left; Top = top; Width = width; Height = height; Opacity = opacity; }
    }

    // True while a notice covers the island: the panel then leaves hovering to this window.
    public bool Covering => _item is not null || IsVisible && !_leaving;
    public AttentionItem? Item => _item;
    private NoticeCenter Center => _app.Notices;
    private DeckSettings Settings => _app.Settings;
    private Notice? Notice => _item?.Payload as Notice;
    private IslandForm Form => _shape.Style switch { CompactStyle.Notch => IslandForm.Notch, CompactStyle.Capsule => IslandForm.Capsule, _ => IslandForm.Line };

    // ----- Showing, updating and ending -----

    public void Present(AttentionItem item)
    {
        bool continuing = IsVisible && !_leaving && _item is not null;
        _item = item; _leaving = false;
        int generation = ++_generation;
        // Another notice in place: the words that show leave first. A new one: the island's own words leave first.
        if (continuing) _content.BeginAnimation(OpacityProperty, Fade(0, Soft.Enabled ? FadeOut : 0, () => { if (generation == _generation && _item is not null) Begin(true, generation); }));
        else _deck.FadeIslandContent(() => { if (generation == _generation && _item is not null) Begin(false, generation); });
    }

    private void Begin(bool continuing, int generation)
    {
        if (!continuing)
        {
            Layout();
            Dress();
            _stage.Opacity = 1;
            Show();
            // Shown on a monitor with another scale, Windows has just given the window that scale; it is placed again in it.
            Place();
            Dress();
            NoticeNative.BringToTop(new WindowInteropHelper(this).Handle);
            // The two windows reach the screen on frames of their own: the island underneath is hidden only once this window has
            // drawn the same island over it, so no frame shows neither.
            AfterFrames(HandFrames, () => { if (generation == _generation && _item is not null) _deck.SetIslandCovered(true); });
        }
        _arrivedUnderPointer = _island.Contains(CursorInStage());
        BuildContent();
        _content.BeginAnimation(OpacityProperty, null);
        _content.Opacity = 0;
        _content.BeginAnimation(OpacityProperty, Fade(1, Soft.Enabled ? FadeIn : 0));
        // A window just shown draws its first frame late; started now, the motion would lose its first flash to that frame.
        if (continuing) StartMotion();
        else Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (generation == _generation && _item is not null) StartMotion(); });
        Announce();
    }

    // The shown notice changed in place (a task's progress, a burst from the same application): its words change, the island stays.
    public void Update(AttentionItem item)
    {
        if (_item is null) return;
        _item = item;
        BuildContent();
    }

    // The item ended: the words leave, the island's own content lands again and the window hides.
    public void End(AttentionItem item)
    {
        if (_item is null || !ReferenceEquals(_item, item) && _item.Key != item.Key) return;
        _item = null; _leaving = true;
        WatchLive();
        _hoverOpen.Stop(); Release();
        int generation = ++_generation;
        double fade = Soft.Enabled ? FadeOut : 0;
        if (fade <= 0) { Finish(reveal: true); return; }
        _content.BeginAnimation(OpacityProperty, Fade(0, fade, () => { if (generation == _generation && _item is null) Finish(reveal: true); }));
    }

    // The panel opens over the island: a notice on its way out goes at once.
    public void Abort()
    {
        if (_item is not null || !IsVisible) return;
        _generation++;
        Finish();
    }

    // The panel opens while a notice shows (the pointer rested on it, the hotkey): the notice goes in the same frame and the panel
    // grows from the island as usual, which has kept its outline.
    public void HandOver()
    {
        // Still waiting for the island's own words to fade (FadeIslandContent): they come back at once and the notice never shows.
        if (!IsVisible) { if (_item is not null) { _item = null; WatchLive(); _generation++; _deck.SetIslandCovered(false); } return; }
        _item = null;
        WatchLive();
        _hoverOpen.Stop(); Release();
        _generation++;
        Finish();
    }

    // The panel is hidden, or BrimDeck exits: a notice that still shows goes at once.
    public void Vanish() { if (IsVisible) HandOver(); }

    // The theme or the island changed while a notice shows: it is laid out and drawn again for the island as it is now.
    public void Refresh()
    {
        if (!IsVisible) return;
        if (_item is null) { Finish(reveal: true); return; }
        StopMotion();
        Layout();
        Dress();
        BuildContent();
        _body.InvalidateVisual(); _shadow.InvalidateVisual();
    }

    public void IslandChanged()
    {
        if (IsVisible && _deck.Collapsed != _shape) Refresh();
    }

    // The notices hotkey with a notice on the island: the panel opens on the notices page and takes the keyboard.
    public void EnterKeyboard() { if (_item is not null) OpenPage(byKey: true); }

    // reveal: the notice ran out and the island takes over in place. The island shows again first and this window, drawing the same
    // island, leaves only once the island is on screen, a few frames later: the two windows reach the screen on frames of their own,
    // and leaving at once showed a frame with neither, a flash. When the panel opens instead, this window leaves at once, as the
    // panel grows from the island.
    private void Finish(bool reveal = false)
    {
        StopMotion();
        _leaving = false;
        _deck.SetIslandCovered(false, reveal);
        _content.Children.Clear();
        if (!reveal) { Empty(); return; }
        int generation = _generation;
        AfterFrames(HandFrames, () => { if (generation == _generation && _item is null) Empty(); });
    }

    // A layered window keeps its last picture while hidden and shows it for a frame when shown again: emptied first, then hidden.
    private void Empty()
    {
        _stage.Opacity = 0;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () => { if (_item is null && !_leaving) Hide(); });
    }

    // How many display frames one window waits for the other to have drawn the island when they hand it over.
    private const int HandFrames = 3;

    private static void AfterFrames(int frames, Action done)
    {
        TimeSpan last = default;
        int seen = 0;
        EventHandler? step = null;
        step = (_, e) =>
        {
            // WPF may raise Rendering more than once for one frame.
            if (e is RenderingEventArgs args) { if (args.RenderingTime == last) return; last = args.RenderingTime; }
            if (++seen < frames) return;
            CompositionTarget.Rendering -= step;
            done();
        };
        CompositionTarget.Rendering += step;
    }

    private void OpenPage(bool byKey)
    {
        string? id = Notice?.Id;
        _deck.OpenNoticesPage(byKey, id);
    }

    // ----- Pointer -----

    private void PointerEntered()
    {
        if (_item is null || _leaving) return;
        Hold();
        // A notice that arrives under a resting pointer does not open the panel by itself: the pointer leaves and comes back, or clicks.
        if (_arrivedUnderPointer) return;
        _hoverOpen.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.OpenDelay));
        _hoverOpen.Start();
    }

    private void PointerLeft()
    {
        _hoverOpen.Stop();
        _arrivedUnderPointer = false;
        Release();
    }

    // While the pointer is on it, the notice does not run out.
    private void Hold() { if (!_held) { _held = true; Center.Hold(_deck, true); } }
    private void Release() { if (_held) { _held = false; Center.Hold(_deck, false); } }

    private Point CursorInStage()
    {
        var (bounds, _, scale) = WindowsHost.MonitorScreen(_deck.HotkeyMonitor) ?? WindowsHost.PrimaryScreen();
        var cursor = WindowsHost.Cursor();
        return new Point((cursor.X - bounds.Left) / scale - _stageRect.X, (cursor.Y - bounds.Top) / scale - _stageRect.Y);
    }

    // ----- Geometry -----

    // The window holds the island with room around it for the bubbles and the backlight, and covers the island exactly.
    private void Layout()
    {
        _shape = _deck.Collapsed;
        var surface = _shape.Surface;
        var island = new Rect(surface.X, surface.Y, Math.Max(1, surface.Width), Math.Max(1, surface.Height));
        _k = Form switch { IslandForm.Capsule => island.Height / IslandLiquid.DesignHeight, IslandForm.Notch => Math.Max(.5, _shape.BottomRadius / CollapsedLimits.NotchCorner), _ => Math.Max(.5, _deck.Scale) };
        double side = Form == IslandForm.Line ? 40 : 28, below = Form == IslandForm.Line ? 32 : 26;
        var stage = new Rect(island.X - side * _k, island.Y - 26 * _k, island.Width + 2 * side * _k, island.Height + (26 + below) * _k);
        var monitor = _deck.MonitorBoundsDip;
        stage.Intersect(new Rect(0, 0, Math.Max(1, monitor.Width), Math.Max(1, monitor.Height)));
        if (stage.IsEmpty) stage = island;
        _stageRect = stage;
        _island = new Rect(island.X - stage.X, island.Y - stage.Y, island.Width, island.Height);
        Place();
        foreach (var layer in new[] { _shadow, _glowWide, _glowCore, _bloomWide, _bloomMiddle, _bloomSmall, _body, _parts }) { layer.Width = stage.Width; layer.Height = stage.Height; }
        // The pointer's area: the island, and for the thin indicator the band round it the island's own hover area takes.
        var hit = Form == IslandForm.Line ? new Rect(_island.X - 12, 0, _island.Width + 24, _island.Bottom + 10) : _island;
        Canvas.SetLeft(_hit, hit.X); Canvas.SetTop(_hit, hit.Y); _hit.Width = hit.Width; _hit.Height = hit.Height;
        Canvas.SetLeft(_content, _island.X); Canvas.SetTop(_content, _island.Y); _content.Width = _island.Width; _content.Height = _island.Height;
        _content.Clip = IslandBorder.BuildOutline(_island.Width, _island.Height, _shape.Shoulder, new CornerRadius(_shape.TopRadius, _shape.TopRadius, _shape.BottomRadius, _shape.BottomRadius));
        _notchGlowCore = _notchGlowWide = null;
    }

    private void Place()
    {
        var (bounds, _, scale) = WindowsHost.MonitorScreen(_deck.HotkeyMonitor) ?? WindowsHost.PrimaryScreen();
        Width = _stageRect.Width; Height = _stageRect.Height;
        int x = (int)Math.Round(bounds.Left + _stageRect.X * scale), y = (int)Math.Round(bounds.Top + _stageRect.Y * scale);
        if (PresentationSource.FromVisual(this) is not null && VisualTreeHelper.GetDpi(this).DpiScaleX is var current && Math.Abs(current - scale) > .001)
            WindowsHost.MoveTo(this, x, y);
        double dpi = PresentationSource.FromVisual(this) is null ? scale : VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = x / dpi; Top = y / dpi;
    }

    private void Dress()
    {
        double dpi = PresentationSource.FromVisual(this) is null ? (WindowsHost.MonitorScreen(_deck.HotkeyMonitor) ?? WindowsHost.PrimaryScreen()).Scale : VisualTreeHelper.GetDpi(this).DpiScaleX;
        _look = NoticeLook.Current(dpi);
        _fill = null; _rim = null; _edge = null; _rest = null;
        _decor = _look.HighContrast ? null : IslandDecor.For(PanelTheme.Current);
        // As the island's: a pixel theme's text is not smoothed, and over glass ClearType cannot be used (the surface is a tint).
        RenderOptions.SetClearTypeHint(_content, _look.Glass ? ClearTypeHint.Auto : ClearTypeHint.Enabled);
        if (_look.Pixel > 0) TextOptions.SetTextRenderingMode(_content, TextRenderingMode.Aliased); else _content.ClearValue(TextOptions.TextRenderingModeProperty);
        // The content is clipped to the island as it is drawn now (a pixel theme's steps, a cut theme's slants).
        if (_shape is not null && Form != IslandForm.Line) _content.Clip = Rest().Outline;
        // The island's shadow, from a copy of its body behind it; a see-through surface would show that copy, so it has none.
        _shadow.Effect = _look.ShadowOpacity <= 0 || _look.HighContrast || _look.Surface.A < 255 ? null : new DropShadowEffect
        {
            Color = _look.Shadow, BlurRadius = Math.Max(0, _look.ShadowBlur), ShadowDepth = _look.ShadowDepth, Direction = 270,
            Opacity = _look.ShadowOpacity, RenderingBias = RenderingBias.Performance
        };
        // The backlight's wide layer and the indicator's bloom are the prototype's blurred copies (σ 3.2; 1.6, 4 and 9), as kernel radii of 3σ.
        _glowWide.Effect = new BlurEffect { Radius = 3.2 * 3 * _k, RenderingBias = RenderingBias.Performance };
        _bloomSmall.Effect = new BlurEffect { Radius = 1.6 * 3 * _k, RenderingBias = RenderingBias.Performance };
        _bloomMiddle.Effect = new BlurEffect { Radius = 4 * 3 * _k, RenderingBias = RenderingBias.Performance };
        _bloomWide.Effect = new BlurEffect { Radius = 9 * 3 * _k, RenderingBias = RenderingBias.Performance };
        _body.InvalidateVisual(); _shadow.InvalidateVisual();
    }

    // ----- Motion -----

    // With animations on and outside high contrast; without, the island keeps still and only shows the words.
    private bool MotionAllowed => Soft.Enabled && !_look.HighContrast;

    private void StartMotion()
    {
        StopMotion();
        if (!MotionAllowed) { RedrawAll(); return; }
        var form = Form;
        double width = _island.Width / _k, height = _island.Height / _k;
        _liquid = form switch
        {
            IslandForm.Notch => new IslandLiquid(form, width, height, fury: Fury, corner: _shape.BottomRadius / _k, shoulder: _shape.Shoulder / _k, cut: _look.Pixel > 0 ? 0 : _look.Cut / _k),
            _ => new IslandLiquid(form, width, height, fury: Fury)
        };
        _start = Eased.Now; _t = 0;
        Motion.Start(_body, Step);
    }

    // One display frame of the motion; false once it has played.
    private bool Step(double now)
    {
        if (_liquid is not { } liquid) return false;
        _t = Math.Max(0, now - _start) * Pace;
        if (liquid.Finished(_t)) { StopMotion(); return false; }
        RedrawAll();
        return true;
    }

    private void StopMotion()
    {
        if (_liquid is null) return;
        _liquid = null;
        RedrawAll();
    }

    private void RedrawAll()
    {
        foreach (var layer in new[] { _shadow, _glowWide, _glowCore, _bloomWide, _bloomMiddle, _bloomSmall, _body, _parts }) layer.InvalidateVisual();
    }

    // ----- Drawing -----

    private Brush? _fill, _rim;

    private Brush Fill => _fill ??= Freeze(new SolidColorBrush(_look.Surface));

    // The island at rest, exactly as IslandBorder draws it.
    private Geometry RestOutline()
    {
        if (Form == IslandForm.Line)
        {
            double r = _island.Height / 2;
            return new RectangleGeometry(_island, r, r);
        }
        var outline = Rest().Outline.Clone();
        outline.Transform = new TranslateTransform(_island.X, _island.Y);
        return outline;
    }

    // The island's outline at the origin as the island draws it (IslandBorder.Shape): stepped for a pixel theme, with its bevel, cut
    // for a cut theme, smooth otherwise. Made once for the layout and the look.
    private (Geometry Outline, (Geometry Light, Geometry Shade)? Bevel) Rest()
    {
        if (_rest is { } known) return known;
        double dpi = PresentationSource.FromVisual(this) is null ? (WindowsHost.MonitorScreen(_deck.HotkeyMonitor) ?? WindowsHost.PrimaryScreen()).Scale : VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double step = PixelStairs.Step(_look.Pixel, dpi);
        var shape = IslandBorder.Shape(_island.Width, _island.Height, _shape.Shoulder, new CornerRadius(_shape.TopRadius, _shape.TopRadius, _shape.BottomRadius, _shape.BottomRadius), 0, step, _look.RimWidth, dpi, _look.Cut);
        return (_rest = shape).Value;
    }

    private (Geometry Outline, (Geometry Light, Geometry Shade)? Bevel)? _rest;
    private IslandDecor? _decor;
    private Pen[]? _edge;

    // The island's body at this frame: its outline pushed out by the ripples (the notch's top edge stays against the screen).
    private Geometry BodyOutline()
    {
        // A pixel theme's island is drawn in steps; its outline stays on them, and its liquid is the glow and the words alone.
        if (_liquid is not { } liquid || Form == IslandForm.Line || _look.Pixel > 0) return RestOutline();
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            bool started = false;
            if (Form == IslandForm.Notch) { g.BeginFigure(new Point(_island.X, _island.Y), true, true); started = true; }
            foreach (var p in liquid.Outline)
            {
                double o = liquid.EdgeOut(p.S, _t);
                var point = new Point(_island.X + (p.X + p.Nx * o) * _k, _island.Y + (p.Y + p.Ny * o) * _k);
                if (!started) { g.BeginFigure(point, true, true); started = true; }
                else g.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawShadow(DrawingContext drawing)
    {
        if (_shadow.Effect is null || Form == IslandForm.Line) return;
        drawing.DrawGeometry(Fill, null, BodyOutline());
    }

    private void DrawBody(DrawingContext drawing)
    {
        if (Form == IslandForm.Line) { DrawLine(drawing); return; }
        var body = BodyOutline();
        drawing.DrawGeometry(Fill, null, body);
        if (_look.Gradient is { } gradient) drawing.DrawGeometry(gradient, null, body);
        if (_decor is { } decor)
        {
            // The theme's motifs at the island's size, over the island's own place and under the displaced outline.
            drawing.PushClip(body);
            drawing.PushTransform(new TranslateTransform(_island.X, _island.Y));
            decor.Draw(drawing, _island.Size, new CornerRadius(_shape.TopRadius, _shape.TopRadius, _shape.BottomRadius, _shape.BottomRadius), _shape.Shoulder, Rest().Outline);
            drawing.Pop(); drawing.Pop();
        }
        if (_look.Pattern is { } pattern) drawing.DrawGeometry(pattern, null, body);
        if (_look.Edge is { } edge)
        {
            // Glass: the light gathered along the inside of the outline; clipped to the body, the pens' outer halves go.
            drawing.PushClip(body);
            IslandBorder.DrawEdge(drawing, _edge ??= IslandBorder.EdgePens(edge), body);
            drawing.Pop();
        }
        if (_look.RimWidth > 0 && Rest().Bevel is { } bevel && _look.Bevel is { } shade)
        {
            // A pixel theme's rim is its bevel: the light side in the rim's brush, the shade side in the bevel's.
            drawing.PushTransform(new TranslateTransform(_island.X, _island.Y));
            drawing.DrawGeometry(_look.Rim, null, bevel.Light);
            drawing.DrawGeometry(shade, null, bevel.Shade);
            drawing.Pop();
        }
        else if (_look.RimWidth > 0)
        {
            // Clipped to the body, the outer half of the stroke goes, so the pen is twice the rim's width (as IslandBorder's).
            _rim ??= Freeze(_look.Rim.Clone());
            drawing.PushClip(body);
            drawing.DrawGeometry(null, new Pen(_rim, _look.RimWidth * 2), body);
            drawing.Pop();
        }
    }

    // The beads that trail some ripples, in the island's own color.
    private void DrawParts(DrawingContext drawing)
    {
        if (_liquid is not { } liquid || Form == IslandForm.Line || _look.Pixel > 0) return;
        liquid.Beads(_t, _circles);
        if (_circles.Count == 0) return;
        var parts = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var g = parts.Open())
            foreach (var c in _circles) Circle(g, _island.X + c.X * _k, _island.Y + c.Y * _k, c.R * _k);
        parts.Freeze();
        drawing.DrawGeometry(Fill, null, parts);
    }

    // The indicator: its thread with round ends, the droplets leaving them, in the indicator's color brightening as it breathes.
    private void DrawLine(DrawingContext drawing)
    {
        var color = PanelTheme.ColorOf("indicator");
        if (_liquid is not { } liquid) { drawing.DrawGeometry(Freeze(new SolidColorBrush(color)), null, RestOutline()); return; }
        double b = IslandLiquid.BreathAt(_t) * .85;
        var lit = Color.FromArgb((byte)Math.Round(color.A + (LineFlash.A - color.A) * b), (byte)Math.Round(color.R + (LineFlash.R - color.R) * b),
            (byte)Math.Round(color.G + (LineFlash.G - color.G) * b), (byte)Math.Round(color.B + (LineFlash.B - color.B) * b));
        double cx = _island.X + _island.Width / 2, cy = _island.Y + _island.Height / 2, h0 = liquid.Height / 2;
        double left = -liquid.LineEnd(_t, -1) + h0, right = liquid.LineEnd(_t, 1) - h0;
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var g = geometry.Open())
        {
            int n = Math.Max(4, (int)Math.Ceiling((right - left) / .5));
            // Bottom left to right, then top right to left: the same turning as the circles, so where they overlap the fill adds up.
            g.BeginFigure(new Point(cx + left * _k, cy + liquid.LineHalf(left, _t) * _k), true, true);
            for (int i = 1; i <= n; i++) { double x = left + (right - left) * i / n; g.LineTo(new Point(cx + x * _k, cy + liquid.LineHalf(x, _t) * _k), true, false); }
            for (int i = n; i >= 0; i--) { double x = left + (right - left) * i / n; g.LineTo(new Point(cx + x * _k, cy - liquid.LineHalf(x, _t) * _k), true, false); }
            Circle(g, cx + left * _k, cy, liquid.LineHalf(left, _t) * _k);
            Circle(g, cx + right * _k, cy, liquid.LineHalf(right, _t) * _k);
            liquid.LineDrops(_t, _circles, _necks);
            foreach (var c in _circles) Circle(g, cx + c.X * _k, cy + c.Y * _k, c.R * _k);
            foreach (var neck in _necks) NeckFigure(g, neck, cx, cy);
        }
        geometry.Freeze();
        drawing.DrawGeometry(Freeze(new SolidColorBrush(lit)), null, geometry);
    }

    // The capsule's and the notch's backlight: behind the island, so the liquid shows dark against it. wide: the blurred outer layer.
    private void DrawGlow(DrawingContext drawing, bool wide)
    {
        if (_liquid is not { } liquid || Form == IslandForm.Line) return;
        double g = IslandLiquid.GlowAt(_t);
        if (g <= .004) return;
        Geometry band;
        LinearGradientBrush brush;
        if (Form == IslandForm.Capsule)
        {
            double cx = _island.X + _island.Width / 2, cy = _island.Y + _island.Height / 2;
            double left = -liquid.Width / 2, right = liquid.Width / 2;
            var (outer, inner) = liquid.CapsuleRing(left, right, wide ? 10 : 3.4);
            var ring = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var c = ring.Open()) { Polygon(c, outer, cx, cy); Polygon(c, inner, cx, cy); }
            ring.Freeze();
            band = ring;
            brush = Fade(cx + left * _k, cx + right * _k, [(0, .6), (.12, .78), (.32, 1), (.68, 1), (.88, .78), (1, .6)]);
        }
        else
        {
            ref var cached = ref wide ? ref _notchGlowWide : ref _notchGlowCore;
            if (cached is null)
            {
                var shape = new StreamGeometry();
                using (var c = shape.Open()) Polygon(c, liquid.NotchBand(wide ? 12 : 3.8), _island.X, _island.Y);
                shape.Freeze();
                cached = shape;
            }
            band = cached;
            brush = Fade(_island.X, _island.Right, [(0, .6), (.06, .72), (.28, 1), (.72, 1), (.94, .72), (1, .6)]);
        }
        drawing.PushOpacity(g);
        drawing.DrawGeometry(brush, null, band);
        drawing.Pop();
    }

    // The indicator's bloom: its own shape grown by grow and moved down by drop, blurred (the layer's effect), breathing twice.
    private void DrawBloom(DrawingContext drawing, double grow, double drop, double opacity)
    {
        if (_liquid is not { } liquid || Form != IslandForm.Line) return;
        double b = IslandLiquid.BreathAt(_t);
        if (b <= .004) return;
        double cx = _island.X + _island.Width / 2, cy = _island.Y + _island.Height / 2;
        double half = (liquid.Height / 2 + grow) * _k, left = cx - (liquid.LineEnd(_t, -1) + grow) * _k, right = cx + (liquid.LineEnd(_t, 1) + grow) * _k;
        drawing.PushOpacity(b * opacity);
        drawing.DrawRoundedRectangle(Freeze(new SolidColorBrush(GlowColor)), null, new Rect(left, cy + drop * _k - half, Math.Max(0, right - left), 2 * half), half, half);
        drawing.Pop();
    }

    private LinearGradientBrush Fade(double from, double to, (double Offset, double Alpha)[] stops)
    {
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(from, 0), EndPoint = new Point(Math.Max(from + 1, to), 0) };
        foreach (var (offset, alpha) in stops) brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * 255), GlowColor.R, GlowColor.G, GlowColor.B), offset));
        brush.Freeze();
        return brush;
    }

    private void Polygon(StreamGeometryContext g, List<LiquidPoint> points, double ox, double oy)
    {
        if (points.Count < 3) return;
        g.BeginFigure(new Point(ox + points[0].X * _k, oy + points[0].Y * _k), true, true);
        for (int i = 1; i < points.Count; i++) g.LineTo(new Point(ox + points[i].X * _k, oy + points[i].Y * _k), true, false);
    }

    private static void Circle(StreamGeometryContext g, double x, double y, double r)
    {
        if (!(r > 0)) return;
        // Two half arcs, turning the same way as the bodies, so overlaps add up instead of cancelling.
        g.BeginFigure(new Point(x - r, y), true, true);
        g.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
        g.ArcTo(new Point(x - r, y), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
    }

    private void NeckFigure(StreamGeometryContext g, IslandLiquid.Neck n, double ox, double oy)
    {
        Point P(LiquidPoint p) => new(ox + p.X * _k, oy + p.Y * _k);
        g.BeginFigure(P(n.P1), true, true);
        g.BezierTo(P(n.H1), P(n.H3), P(n.P3), true, false);
        g.LineTo(P(n.P4), true, false);
        g.BezierTo(P(n.H4), P(n.H2), P(n.P2), true, false);
    }

    private static T Freeze<T>(T freezable) where T : Freezable { if (freezable.CanFreeze) freezable.Freeze(); return freezable; }

    // ----- Content -----

    private NoticePrivacy Privacy(Notice notice) => Settings.NoticeRule(notice.AppKey)?.Privacy ?? Settings.NoticePrivacy;

    private (string Title, string Summary) Texts()
    {
        if (_item is { Kind: AttentionKind.Summary, Payload: int count })
            return (NoticeText.Count(count), Loc.T("在 BrimDeck 安静期间收到", "Arrived while BrimDeck kept quiet"));
        if (Notice is not { } notice) return ("", "");
        var (title, summary) = NoticeText.Presented(notice, Privacy(notice));
        if (NoticeClock.Live(notice.Timing, DateTimeOffset.UtcNow, TimeZoneInfo.Local) is { } live) summary = live;
        if (title.Length == 0) title = notice.AppName;
        if (_item is { Count: > 1 } item) summary = (summary.Length > 0 ? summary + " · " : "") + NoticeText.Count(item.Count);
        return (title, summary);
    }

    private TextBlock? _announced;

    // ----- Live words -----
    //
    // A time notice's words about its time ("还剩 24:59", "14 分钟后开始") follow NoticeCenter's shared second tick while it shows, and
    // change only those words; nothing ticks once it ends.
    private Run? _liveRun;
    private bool _watchingLive;

    private void WatchLive()
    {
        bool live = _liveRun is not null && _item is not null && !_leaving && !_closed;
        if (live == _watchingLive) return;
        _watchingLive = live;
        if (live) Center.LiveTick += LiveStep; else Center.LiveTick -= LiveStep;
    }

    private void LiveStep()
    {
        if (_liveRun is not { } run || _item is null || _leaving) { WatchLive(); return; }
        string words = "  " + Texts().Summary;
        // Words that grew (a stopwatch passing an hour, an event that started) may no longer fit: laid out again. Shorter ones stay.
        if (words.Length > run.Text.Length) BuildContent(); else run.Text = words;
        // Ended (the countdown reached its end, the event started): the last words stay; the tick is let go.
        if (_item is not null && !NoticeClock.IsLive(Notice?.Timing, DateTimeOffset.UtcNow)) { _liveRun = null; WatchLive(); }
    }

    // The icon and the words, laid out inside the island's content area at the island's scale. Words longer than the area scroll
    // through once they have been read from the start, and start again; without animations they end in an ellipsis.
    private void BuildContent()
    {
        BuildContentCore();
        WatchLive();
    }

    private void BuildContentCore()
    {
        _content.Children.Clear();
        _announced = null; _liveRun = null;
        if (_item is null || Form == IslandForm.Line) return;
        double scale = _shape.Scale, inset = _shape.Inset, available = Math.Max(0, _island.Width - 2 * inset);
        double plate = Math.Round(20 * scale), gap = Math.Round(8 * scale);
        var (title, summary) = Texts();
        var icon = Plate(plate);
        Canvas.SetLeft(icon, inset); Canvas.SetTop(icon, (_island.Height - plate) / 2);
        _content.Children.Add(icon);
        var line = new TextBlock { FontFamily = UI.PanelFont, FontSize = 12.5 * scale, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        line.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(_look.Primary) });
        if (summary.Length > 0)
        {
            var words = new Run("  " + summary) { Foreground = UI.Brush(_look.Secondary), FontSize = 12 * scale };
            line.Inlines.Add(words);
            if (Notice is { } timed && NoticeClock.IsLive(timed.Timing, DateTimeOffset.UtcNow)) _liveRun = words;
        }
        Directed(line, title + summary);
        string spoken = (Notice?.AppName is { Length: > 0 } app ? app + ": " : "") + (summary.Length > 0 ? title + ", " + summary : title);
        AutomationProperties.SetName(line, spoken);
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);
        _announced = line;
        double viewport = Math.Max(0, available - plate - gap);
        var window = new Canvas { Width = viewport, Height = _island.Height, ClipToBounds = true };
        Canvas.SetLeft(window, inset + plate + gap);
        _content.Children.Add(window);
        line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = line.DesiredSize.Width;
        Canvas.SetTop(line, (_island.Height - line.DesiredSize.Height) / 2);
        window.Children.Add(line);
        if (width <= viewport) return;
        if (!Soft.Enabled) { line.Width = viewport; line.TextTrimming = TextTrimming.CharacterEllipsis; return; }
        // Read from the start, carried along at an even pace to its end, held there, and back to the start.
        double travel = width - viewport, seconds = travel / (32 * scale);
        var move = new TranslateTransform();
        line.RenderTransform = move;
        var scroll = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(1.2 + seconds + 1.2) };
        scroll.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scroll.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
        scroll.KeyFrames.Add(new LinearDoubleKeyFrame(-travel, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2 + seconds))));
        scroll.KeyFrames.Add(new LinearDoubleKeyFrame(-travel, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2 + seconds + 1.2))));
        move.BeginAnimation(TranslateTransform.XProperty, scroll);
    }

    // The notice's picture (a contact's avatar) round; else the application's own icon; without one, its first letter in its color
    // on a plate tinted with it (a bell for a summary).
    private FrameworkElement Plate(double size)
    {
        var notice = Notice;
        if (notice is not null && NoticeLetter.ShowsPicture(notice, Settings) && Center.Icons.Get(notice.Picture, out _) is { } avatar) return NoticeLetter.Round(avatar, size);
        var image = _item?.Kind == AttentionKind.Summary ? null : Center.Icons.Get(notice?.IconRef, out _);
        if (image is not null)
        {
            var picture = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            if (notice is null) return picture;
            // The state and source badges on the plate's corners (NoticeBadges).
            var framed = new Grid { Width = size, Height = size };
            framed.Children.Add(picture);
            NoticeBadges.Decorate(framed, notice, size, _look.Glow, null);
            return framed;
        }
        var accent = notice is null ? PanelTheme.ColorOf("accent") : _look.AppColor(Center.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey)));
        // Without an icon, BrimDeck's own glyph (a device's) or one for the kind of application, else the initial.
        var plate = NoticeLetter.Plate(notice?.AppName, size, accent, bell: notice is null, icon: notice is null ? null : NoticeBadges.AppGlyph(notice, size, accent));
        if (notice is not null && plate is Panel panel) NoticeBadges.Decorate(panel, notice, size, _look.Glow, NoticeGlyphs.Plate(notice));
        return plate;
    }

    private void IconLoaded(string iconRef)
    {
        if (_leaving || Notice?.IconRef != iconRef && Notice?.Picture != iconRef) return;
        BuildContent();
    }

    // A text in a right-to-left language reads and aligns from the right (NoticeText.RightToLeft).
    internal static TextBlock Directed(TextBlock block) => Directed(block, block.Text);

    private static TextBlock Directed(TextBlock block, string text)
    {
        block.FlowDirection = NoticeText.RightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return block;
    }

    // Screen readers hear the new notice once, politely, without the window taking focus.
    private void Announce()
    {
        if (_announced is null || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        (UIElementAutomationPeer.FromElement(_announced) ?? UIElementAutomationPeer.CreatePeerForElement(_announced))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static DoubleAnimation Fade(double to, double milliseconds, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(Math.Max(1, milliseconds))) { EasingFunction = Soft.Out };
        if (completed is not null) animation.Completed += (_, _) => completed();
        return animation;
    }

    // A layer of the drawing, drawn by its delegate whenever it is invalidated.
    private sealed class Layer : FrameworkElement
    {
        public Action<DrawingContext>? Draw;
        public Layer() { IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext drawingContext) => Draw?.Invoke(drawingContext);
    }
}
