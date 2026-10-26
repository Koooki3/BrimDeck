using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.UI.Composition;
using WinRT;

namespace BrimDeck.LiquidGlass;

// One frame of liquid glass (Liquid.hlsl, a port of xiaofangstudio/liquid-glass-web). Sizes are device pixels; the shape is the
// island's (IslandBorder.BuildOutline) at this size.
//   Width, Height: the island; the swap chain is resized to it.
//   CornerTop, CornerBottom, Shoulder: as the island draws them; CornerTop 0 is a square top (the open panel, the notch).
//   Tint: the glass's background, straight color and alpha (the source's rgba(255, 255, 255, .08)).
//   PointerX, PointerY: the pointer in island pixels, NaN while it is away.
//   Hover, Press: 0..1, eased by the caller (GlassFeedback: the monitor page's hover time; Soft.Press down, Soft.Quick up).
//     Hover brightens the glass a little and lights its rim toward the pointer; Press fades in the source's pressed shadows
//     and inner glow. Morph: unused (0).
//   Dark: 1 for the dark look.
//   BandLow, BandHigh: the band of brightness the theme's text was fitted to (PanelGlass.Low/High; 0, 1 for none): only a
//     background outside it is moved, just far enough (legibility, see Liquid.hlsl).
//   Scale: device pixels per DIP (the source's CSS pixel).
//   Open: 0 collapsed .. 1 the open panel.
//   Ref, Edge, Rad: the source's refraction, ring thickness and inner corner (its sliders; 1, 1, .6 by default).
//   Contrast, Brightness, Saturate: the source's backdrop-filter (1.2, 1.05, 1.1).
//   InsetY, InsetBlur, InsetAlpha: the source's inset shadow (0 -8px 20px rgba(0,0,0,.14)), shrunk for low shapes.
//   Faithful: the source's own uv field (the proof sheets), not the island's.
//   Sigma0: the material's masking constant (0: DefaultSigma0); only the calibration sets it.
//   TextRects: the open panel's text and content regions, device pixels from the island's top-left, 4 floats each (left, top,
//     right, bottom); null: the whole panel counts as text (the previews without a page). Behind them the material covers as
//     far as the text needs; between them it stays translucent (round 12b).
//   Zoned: round 12's zones (a clear rim, Acrylic inside) instead of round 13's one slab, for the comparisons.
//   BodyLight: the light look's luminosity opacity between the text regions (0: DefaultBodyLight); only the sweep sets it.
//   Presence (D13, round 15): how far the glass has come into being, 0..1 (the island's entrance and exit, its opacity): the
//     slab's refraction and its rim's light grow with it, so the glass condenses as it fades in rather than only fading.
//   Plain (round 15): how plain the backdrop is (nothing in it: the open panel's light look then grows denser evenly), eased by
//     the caller (GlassBackdrop) over a measure's changes; -1 has the shader take it from this frame's band at once (previews).
//   ContentLeft..ContentBottom: the collapsed island's content row (its text and glyphs), device pixels from the island's
//     top-left; all 0 for none. Behind it the backdrop's contrast is capped, with a light local frost (Liquid.hlsl).
//   LinearFilters: the backdrop filtered in linear light, as the browser draws the source (see Liquid.hlsl); off, in sRGB.
//   LinearMap: the map's bytes read as linear light, the browser's constant shift (only for the proof sheets: it makes the
//     island's ring lopsided); off, the default, as the page meant it (sRGB bytes, no shift).
//   CssWidth, CssHeight (Faithful, the proof sheets): the source's element in whole CSS pixels, its map's texels; an element
//     between device pixels is drawn over its snapped pixels, which need not be that size times Scale. 0: Width / Scale.
//   FilterWidth, FilterHeight (Faithful): where its backdrop-filter's region ends, device pixels from the box's corner (one
//     short of the box where the corner snapped back; see Liquid.hlsl). 0: the whole box.
internal readonly record struct LiquidFrame(
    int Width, int Height,
    float CornerTop, float CornerBottom, float Shoulder,
    float Tint_R, float Tint_G, float Tint_B, float TintAlpha,
    float PointerX, float PointerY,
    float Hover, float Press,
    float Morph,
    float Dark,
    float BandLow = 0, float BandHigh = 1, float Scale = 1, float Open = 0,
    float Ref = 1, float Edge = 1, float Rad = .6f,
    float Contrast = 1.2f, float Brightness = 1.05f, float Saturate = 1.1f,
    float InsetY = -8, float InsetBlur = 20, float InsetAlpha = .14f, bool Faithful = false,
    bool LinearMap = false, bool LinearFilters = true, float CssWidth = 0, float CssHeight = 0,
    float FilterWidth = 0, float FilterHeight = 0,
    float ContentLeft = 0, float ContentTop = 0, float ContentRight = 0, float ContentBottom = 0, float Sigma0 = 0,
    float[]? TextRects = null, float BodyLight = 0, bool Zoned = false, float ReachLight = 0, bool Probe = false, float Presence = 1, float Plain = -1)
{
    // The source's height the inset shadow was made for (its buttons, about 44 CSS pixels): lower shapes (the capsule, the bar)
    // take it in proportion, and very low ones (under Thin) fainter still, so a 20-pixel bar is not one full-height gradient.
    private const float SourceHeight = 44, Thin = 28;
    // The backdrop's brightness band where the liquid glass themes' text keeps 4.5:1 (LiquidTheme.TextLight, TextDark; the
    // values are the theme's, PanelThemes.LiquidTextLight and LiquidTextDark).
    public const float TextLight = BrimDeck.Core.PanelThemes.LiquidTextLight, TextDark = BrimDeck.Core.PanelThemes.LiquidTextDark;

    // The two looks, at a display scale, for a shape and size: the source's glass in both. Light: its white background
    // (rgba(255,255,255,.08)) and the backdrop lifted to at least TextLight; dark: as thin a dark base (#121419 at .08) and the
    // backdrop brought down to at most TextDark, so the theme's text keeps 4.5:1 (see Liquid.hlsl, Legibility). The dark base
    // is no thicker than the light one: on a dark page the dark glass is the page's own darkness, not darker.
    public static LiquidFrame Preset(bool dark, int width, int height, float cornerTop, float cornerBottom, float shoulder, float scale)
    {
        float css = height / MathF.Max(.5f, scale);
        float fit = MathF.Min(1, css / SourceHeight);
        float thin = MathF.Min(1, css / Thin);
        return new(width, height, cornerTop, cornerBottom, shoulder, dark ? 0x12 / 255f : 1, dark ? 0x14 / 255f : 1, dark ? 0x19 / 255f : 1, .08f,
            PointerX: float.NaN, PointerY: float.NaN, Hover: 0, Press: 0, Morph: 0, Dark: dark ? 1 : 0,
            BandLow: dark ? 0 : TextLight, BandHigh: dark ? TextDark : 1, Scale: scale,
            InsetY: -8 * fit, InsetBlur: 20 * fit, InsetAlpha: .14f * thin * thin);
    }

    // Between two looks (light glass to dark as automatic liquid glass turns), t from a to b: every look's value in between,
    // the shape and the pointer a's.
    // How plain a backdrop is from its band (the 2nd and 98th percentiles of its luma): 1 when they lie within 2 levels in a
    // hundred, 0 from 8 on (a page of text, frosted, still spans about 10). Liquid.hlsl takes the same when given none.
    public static float PlainOf(float low, float high) => Math.Clamp(1 - (high - low - .02f) / .06f, 0, 1);

    public static LiquidFrame Blend(in LiquidFrame a, in LiquidFrame b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t <= 0) return a;
        if (t >= 1) return b with { Width = a.Width, Height = a.Height, CornerTop = a.CornerTop, CornerBottom = a.CornerBottom, Shoulder = a.Shoulder };
        float L(float x, float y) => x + (y - x) * t;
        return a with
        {
            Tint_R = L(a.Tint_R, b.Tint_R), Tint_G = L(a.Tint_G, b.Tint_G), Tint_B = L(a.Tint_B, b.Tint_B), TintAlpha = L(a.TintAlpha, b.TintAlpha),
            Dark = L(a.Dark, b.Dark), BandLow = L(a.BandLow, b.BandLow), BandHigh = L(a.BandHigh, b.BandHigh)
        };
    }

    // The glass's brightness where the text of either look keeps 3:1 (dark text above about 0.38, light text below about
    // 0.58): automatic liquid glass turns its text there. TextTurns: whether glass blended dark (0 light .. 1 dark) over a
    // backdrop of this mean luma has reached it on its way toward the look toDark, or has arrived.
    public const double TextTurn = .48;
    public static bool TextTurns(double luma, double dark, bool toDark, bool arrived)
    {
        if (arrived) return true;
        // Round 13's slab: behind text the glass covers as far as the text needs, its look turning in the middle of the turn
        // (Liquid.hlsl, CoverDark), about .93 light and .18 dark whatever lies behind (luma no longer matters).
        double s = Math.Clamp((Math.Clamp(dark, 0, 1) - .4) / .2, 0, 1);
        double covered = .93 + (.18 - .93) * s * s * (3 - 2 * s);
        return toDark ? covered <= TextTurn : covered >= TextTurn;
    }

    // ----- The source's displacement map -----

    // The source's roundedRectSDF.
    private static double RoundedRect(double x, double y, double w, double h, double r)
    {
        double qx = Math.Abs(x) - w + r, qy = Math.Abs(y) - h + r;
        return Math.Min(Math.Max(qx, qy), 0) + Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) - r;
    }
    private static double SmoothStep(double a, double b, double t) { t = Math.Clamp((t - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    // The shift at a texel (CSS pixels, in a box w x h), as Liquid.hlsl's Shift works it out: the source's field or the island's.
    public static (double X, double Y) Shift(double x, double y, double w, double h, double edge, double strength, double rad, bool faithful)
    {
        double e = .15 / edge / strength;
        if (faithful)
        {
            double ix = x / w - .5, iy = y / h - .5;
            double s = SmoothStep(0, 1, SmoothStep(.8, 0, RoundedRect(ix, iy, .3, .2, rad) - e));
            return (ix * (s - 1) * w, iy * (s - 1) * h);
        }
        double m = Math.Min(w, h), ex = (w - m) / 2, ey = (h - m) / 2;
        double qx = x + .5 - w / 2, qy = y + .5 - h / 2;
        double sx = Math.Clamp(qx, -ex, ex), sy = Math.Clamp(qy, -ey, ey);
        double t = SmoothStep(0, 1, SmoothStep(.8, 0, RoundedRect(qx / m, qy / m, .3 + ex / m, .2 + ey / m, rad) - e));
        return ((qx - sx) * (t - 1), (qy - sy) * (t - 1));
    }

    // Half the largest shift over the map, as the source normalises it (maxScale * .5): over every texel of the box (CSS pixels)
    // for the source exactly; for the island about 3000 texels inside and every few along the edges, where the largest lie
    // (about .05 ms, once per size: the opening panel changes size every frame).
    public static double MaxHalf(double w, double h, double edge, double strength, double rad, bool faithful)
    {
        // Whole CSS pixels as the shader takes them (half way rounds up, as offsetWidth does).
        int W = Math.Max(2, (int)Math.Floor(w + .5)), H = Math.Max(2, (int)Math.Floor(h + .5));
        int step = faithful ? 1 : Math.Max(1, (int)Math.Sqrt(W * (double)H / 3000)), along = faithful ? 1 : Math.Max(1, step / 2);
        double max = 0;
        void At(int x, int y)
        {
            var (dx, dy) = Shift(x, y, W, H, edge, strength, rad, faithful);
            max = Math.Max(max, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        }
        for (int y = 0; y < H; y += step)
            for (int x = 0; x < W; x += step) At(x, y);
        for (int x = 0; x < W; x += along) { At(x, 0); At(x, H - 1); }
        for (int y = 0; y < H; y += along) { At(0, y); At(W - 1, y); }
        return max * .5;
    }
}

// Liquid glass: the desktop under the island (captured by the caller, padded around it, BrimDeck's own windows left out) drawn
// through a slab of thick glass by one pixel shader (Liquid.hlsl, compiled from the embedded source on first use) into a
// swap chain for composition, which a Composition brush shows in place of the blur window's blur. Rendering happens only when
// the caller asks (the background, the pointer, the shape or an animation changed); a frame copies the background into a
// texture of its own, builds the frost's pyramid and the backdrop's statistics from it (Frost.hlsl: a few small passes over
// the padded region only), updates the constant buffers and draws one triangle. Nothing is allocated per frame (the pyramid's
// textures are remade only when the background's size or the display scale changes), and the whole pipeline state is set on
// every frame (the capture side clears the context's state). The background is read only through that copy, so its texture
// needs no view of ours. All calls on one thread, the capture side's (DesktopSource, on the UI thread): the device is used
// from no other, so it needs no multithread protection.
internal sealed class LiquidRenderer : IDisposable
{
    private const Format Pixels = Format.B8G8R8A8_UNorm, Pyramid = Format.R16G16B16A16_Float;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertex;
    private readonly ID3D11PixelShader _pixel, _downPass, _upPass, _bandPass, _statsPass;
    // Round 12's zones and the lgw port: made only when a frame asks for them (the proof sheets, the comparisons).
    private ID3D11PixelShader? _zoned;
    private ID3D11PixelShader Zoned() => _zoned ??= _device.CreatePixelShader(Shaders.Value.Zoned);
    private readonly ID3D11Buffer _constants, _passConstants;
    private IDXGISwapChain2? _swapChain;
    private ID3D11RenderTargetView? _target;
    // The frame's size, shown from the swap chain's buffers (SetSourceSize), and the buffers' size.
    private (int Width, int Height) _size, _capacity;
    // The background's copy, remade when the background's size or format changes.
    private Surface? _copy;
    private (int Width, int Height, Format Format) _copyFor;
    private bool _disposed;

    public LiquidRenderer(ID3D11Device device)
    {
        _device = device;
        _context = device.ImmediateContext;
        var shaders = Shaders.Value;
        _vertex = device.CreateVertexShader(shaders.Vertex);
        _pixel = device.CreatePixelShader(shaders.Pixel);
        _downPass = device.CreatePixelShader(shaders.Down);
        _upPass = device.CreatePixelShader(shaders.Up);
        _bandPass = device.CreatePixelShader(shaders.Band);
        _statsPass = device.CreatePixelShader(shaders.Stats);
        _constants = device.CreateBuffer(new BufferDescription((uint)Unsafe.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None));
        _passConstants = device.CreateBuffer(new BufferDescription((uint)Unsafe.SizeOf<PassConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None));
        // The frost's passes and the frost itself read filtered (Frost.hlsl, Liquid.hlsl's Frost); everything else texel by texel.
        _smooth = device.CreateSamplerState(SamplerDescription.LinearClamp);
    }
    private readonly ID3D11SamplerState _smooth;

    // A texture with the views a pass needs.
    private sealed class Surface(ID3D11Device device, int width, int height, Format format) : IDisposable
    {
        public readonly int Width = width, Height = height;
        public readonly ID3D11Texture2D Texture = device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
            BindFlags.ShaderResource | BindFlags.RenderTarget));
        private ID3D11ShaderResourceView? _view;
        private ID3D11RenderTargetView? _target;
        public ID3D11ShaderResourceView View => _view ??= device.CreateShaderResourceView(Texture);
        public ID3D11RenderTargetView Target => _target ??= device.CreateRenderTargetView(Texture);
        public void Dispose() { _view?.Dispose(); _target?.Dispose(); Texture.Dispose(); }
    }

    // A Composition brush showing the rendered glass at its size from the brush's top-left (no stretch): the caller places the
    // sprite, or the brush's offset, at the island's top-left.
    public CompositionBrush CreateBrush(Compositor compositor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var swapChain = EnsureSwapChain(Math.Max(1, _size.Width), Math.Max(1, _size.Height));
        var surface = CreateSurface(compositor, swapChain.NativePointer);
        var brush = compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.None;
        brush.HorizontalAlignmentRatio = 0; brush.VerticalAlignmentRatio = 0;
        return brush;
    }

    // Renders one frame into the swap chain and presents it. background: the padded desktop region; pad: its padding in device
    // pixels on each side.
    public void Render(ID3D11Texture2D background, int pad, in LiquidFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Width < 1 || frame.Height < 1) return;
        var swapChain = EnsureSwapChain(frame.Width, frame.Height);
        Draw(background, pad, frame, _target!);
        swapChain.Present(0, PresentFlags.None);
    }

    // The frame drawn into any target of the frame's size (the swap chain's buffer, or an offscreen texture for the preview).
    internal void Draw(ID3D11Texture2D background, int pad, in LiquidFrame frame, ID3D11RenderTargetView target)
    {
        var description = background.Description;
        var copy = Copy(background, description);
        var context = _context;
        context.OMSetBlendState(null);
        context.OMSetDepthStencilState(null);
        context.RSSetState(null);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertex);
        context.PSSetSampler(0, _smooth);
        float scale = MathF.Max(.5f, frame.Scale);
        // The frost behind the island's text this frame (the open panel's, growing with the opening; the content row's), as a
        // deviation, and the stats' band below it: the pyramid is drawn only as deep as they reach.
        float opened = frame.Faithful ? 0 : MathF.Sqrt(Math.Clamp(frame.Open, 0, 1));
        bool row = !frame.Faithful && frame.ContentRight > frame.ContentLeft;
        float frost = MathF.Max(FrostDip * scale * opened, row ? ContentFrostDip * scale : 0) * FrostDeviation, band = MathF.Max(BandDip * scale, LevelSigma[0]);
        Build(copy, scale, MathF.Sqrt(frost * frost + 16 * band * band));
        Statistics(copy, pad, frame, scale, frost, band);
        // The open panel's band is fitted to its frosted backdrop: measured from a level as coarse as the frost.
        Sample(pad, frame.Width, frame.Height, frame.Open > .5f && !frame.Faithful ? (int)MathF.Log2(MathF.Max(1, FrostDip * scale)) : 0);
        Write(frame, description, pad);
        context.OMSetRenderTargets(target);
        context.RSSetViewport(new Viewport(frame.Width, frame.Height));
        context.PSSetShader(frame.Faithful || frame.Zoned ? Zoned() : _pixel);
        context.PSSetConstantBuffer(0, _constants);
        Bind(copy.View, _atlas!.View, _stats[_statsAt]!.View, TextMask(frame, scale));
        context.Draw(3, 0);
        // The background is copied again next frame and the pyramid redrawn: none may stay bound as a shader input meanwhile.
        Unbind();
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    // The inputs t0..t2 bound, from an array kept (nothing allocated per frame), and let go.
    private readonly ID3D11ShaderResourceView[] _none = new ID3D11ShaderResourceView[4], _bound = new ID3D11ShaderResourceView[4];
    private void Unbind() => _context.PSSetShaderResources(0, _none);
    private void Bind(ID3D11ShaderResourceView a, ID3D11ShaderResourceView? b, ID3D11ShaderResourceView? c, ID3D11ShaderResourceView? d)
    {
        _bound[0] = a; _bound[1] = b!; _bound[2] = c!; _bound[3] = d!;
        _context.PSSetShaderResources(0, _bound);
        _bound[0] = _bound[1] = _bound[2] = _bound[3] = null!;
    }

    // ----- The open panel's text regions (round 12b) -----
    // A coverage grid of TextTileDip cells over the island (R8: how much of each cell the regions cover), drawn again only when
    // the regions or the island's size change; the glass reads it filtered, so the cover fades over about a cell.
    private const float TextTileDip = 4;
    private ID3D11Texture2D? _textMask;
    private ID3D11ShaderResourceView? _textMaskView;
    private (int Cols, int Rows) _textMaskSize;
    private (float[]? Rects, int Width, int Height, float Tile) _textMaskFor;
    private byte[] _textCells = [], _textPair = [];
    private float[] _textField = [], _textSpare = [], _textNear = [], _textWeight = [];
    // Round 15: the wide field three box passes of 11 cells (about a Gaussian of 46 DIPs), the near one two of 3 (about 11).
    private const int TextBlurCells = 11, TextBlurPasses = 3, TextNearCells = 3;
    // A box blur of radius cells each side along rows (horizontal) or columns, from one grid into another.
    private static void Box(float[] from, float[] to, int cols, int rows, bool horizontal, int radius)
    {
        int n = horizontal ? cols : rows, lines = horizontal ? rows : cols, span = 2 * radius + 1;
        for (int line = 0; line < lines; line++)
        {
            float sum = 0;
            int At(int k) => horizontal ? line * cols + Math.Clamp(k, 0, n - 1) : Math.Clamp(k, 0, n - 1) * cols + line;
            // Nothing past the grid's edge (zeros): the field falls off toward the panel's edge, so its margins keep the glass's
            // own translucency (round 15 tried the edge cells repeated: the margins of a dark backdrop went white).
            for (int k = -radius; k <= radius; k++) sum += k < 0 || k >= n ? 0 : from[At(k)];
            for (int k = 0; k < n; k++)
            {
                to[At(k)] = sum / span;
                int add = k + radius + 1, drop = k - radius;
                if (add < n) sum += from[At(add)];
                if (drop >= 0) sum -= from[At(drop)];
            }
        }
    }
    private (float Tile, float InverseX, float InverseY, float On) _textInfo;
    private ID3D11ShaderResourceView? TextMask(in LiquidFrame f, float scale)
    {
        if (f.TextRects is not { } rects || f.Faithful) { _textInfo = default; return null; }
        float tile = MathF.Max(1, TextTileDip * scale);
        int cols = (int)MathF.Ceiling(f.Width / tile) + 1, rows = (int)MathF.Ceiling(f.Height / tile) + 1;
        // The texture in steps of 16 cells (the panel opening changes the grid on every frame; the texture is not made again
        // for each), the cells past the grid empty.
        int textureCols = (cols + 15) / 16 * 16, textureRows = (rows + 15) / 16 * 16;
        if (_textMask is null || _textMaskSize != (textureCols, textureRows))
        {
            _textMaskView?.Dispose(); _textMask?.Dispose();
            _textMask = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8_UNorm, (uint)textureCols, (uint)textureRows, 1, 1, BindFlags.ShaderResource));
            _textMaskView = _device.CreateShaderResourceView(_textMask);
            _textMaskSize = (textureCols, textureRows);
            _textMaskFor = default;
        }
        if (_textMaskFor != (rects, f.Width, f.Height, tile))
        {
            _textMaskFor = (rects, f.Width, f.Height, tile);
            // The grids grow with room to spare (half as much again), so an opening panel does not allocate them on every frame.
            if (_textCells.Length < cols * rows)
            {
                int room = Math.Max(cols * rows, _textCells.Length * 3 / 2);
                _textCells = new byte[room]; _textField = new float[room]; _textSpare = new float[room]; _textNear = new float[room]; _textWeight = new float[room];
            }
            if (_textPair.Length < textureCols * textureRows * 2) _textPair = new byte[Math.Max(textureCols * textureRows * 2, _textPair.Length * 3 / 2)];
            Array.Clear(_textCells, 0, cols * rows);
            for (int i = 0; i + 3 < rects.Length; i += 4)
            {
                float l = rects[i], t = rects[i + 1], r = rects[i + 2], b = rects[i + 3];
                int c0 = Math.Max(0, (int)(l / tile)), c1 = Math.Min(cols - 1, (int)(r / tile)), r0 = Math.Max(0, (int)(t / tile)), r1 = Math.Min(rows - 1, (int)(b / tile));
                for (int y = r0; y <= r1; y++)
                    for (int x = c0; x <= c1; x++)
                    {
                        float w = MathF.Max(0, MathF.Min(r, (x + 1) * tile) - MathF.Max(l, x * tile)), h = MathF.Max(0, MathF.Min(b, (y + 1) * tile) - MathF.Max(t, y * tile));
                        byte cover = (byte)Math.Clamp((int)MathF.Round(w * h / (tile * tile) * 255), 0, 255);
                        ref byte cell = ref _textCells[y * cols + x];
                        cell = (byte)Math.Min(255, cell + cover);
                    }
            }
            // The density field (round 14, widened in round 15): the cells' coverage blurred wide into a low-frequency field (red),
            // so the glass grows denser smoothly over a block of text and its edge is a long ramp, never a patch round a glyph;
            // and a nearer one (green, about 11 DIPs) for the little help a lone label still wants.
            // The wide field normalised by the grid's own share of each blur (normalised convolution): text near the panel's edge
            // gets the density text in the middle gets, and an empty margin stays empty (nothing is assumed past the edge).
            for (int i = 0; i < cols * rows; i++) { _textField[i] = _textCells[i] / 255f; _textWeight[i] = 1; }
            for (int pass = 0; pass < TextBlurPasses; pass++)
            {
                Box(_textField, _textSpare, cols, rows, true, TextBlurCells); Box(_textSpare, _textField, cols, rows, false, TextBlurCells);
                Box(_textWeight, _textSpare, cols, rows, true, TextBlurCells); Box(_textSpare, _textWeight, cols, rows, false, TextBlurCells);
            }
            for (int i = 0; i < cols * rows; i++) _textField[i] /= MathF.Max(_textWeight[i], 1e-3f);
            for (int i = 0; i < cols * rows; i++) _textNear[i] = _textCells[i] / 255f;
            for (int pass = 0; pass < 2; pass++) { Box(_textNear, _textSpare, cols, rows, true, TextNearCells); Box(_textSpare, _textNear, cols, rows, false, TextNearCells); }
            Array.Clear(_textPair, 0, textureCols * textureRows * 2);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    int i = y * cols + x, o = (y * textureCols + x) * 2;
                    _textPair[o] = (byte)Math.Clamp((int)MathF.Round(_textField[i] * 255), 0, 255);
                    _textPair[o + 1] = (byte)Math.Clamp((int)MathF.Round(_textNear[i] * 255), 0, 255);
                }
            _context.UpdateSubresource(_textPair, _textMask, 0, (uint)textureCols * 2, 0);
        }
        _textInfo = (tile, 1f / textureCols, 1f / textureRows, 1);
        return _textMaskView;
    }

    // The background copied into a texture of ours (the pyramid's first source; the glass reads its pixels).
    private Surface Copy(ID3D11Texture2D background, Texture2DDescription description)
    {
        var key = ((int)description.Width, (int)description.Height, description.Format);
        if (_copy is null || key != _copyFor)
        {
            _copy?.Dispose();
            _copy = new Surface(_device, key.Item1, key.Item2, description.Format);
            _copyFor = key;
        }
        _context.CopySubresourceRegion(_copy.Texture, 0, 0, 0, 0, background, 0);
        return _copy;
    }

    // ----- The frost's pyramid (Frost.hlsl) -----
    // Downsamples D_1 .. D_n, each half the last (D_0 the copy); level B_k is D_k drawn up UpSteps levels (or to full size), into
    // one atlas: the level-0 ones stacked at the left, the smaller ones in a column at their right. The pyramid's full size
    // (Extent) is the background's rounded up to whole texels of D_n; past the background the copy's edge is repeated (clamped).
    private const int UpSteps = 1, MostLevels = 8;
    // Each level's Gaussian deviation, device pixels, as measured (FxPreview --frost-calibrate: a step edge through B_k).
    internal static readonly float[] LevelSigma = [1.73f, 3.63f, 7.34f, 14.72f, 29.48f, 58.95f, 117.91f, 235.8f];
    private int _levels, _built;
    private (int Width, int Height) _extent, _atlasSize;
    private (int Width, int Height, int Levels) _pyramidFor;
    private readonly Surface?[] _down = new Surface?[MostLevels + 1], _scratch = new Surface?[MostLevels + 1];
    private Surface? _atlas, _band;
    private readonly (int X, int Y)[] _region = new (int, int)[MostLevels];
    private PassConstants _pass;

    // Levels enough for the widest frost at this scale (the open panel's, and the stats' band below it).
    private static int LevelsFor(float scale)
    {
        float widest = FrostDip * scale * FrostDeviation * 1.02f;
        int n = 1;
        while (n < MostLevels && LevelSigma[n - 1] < widest) n++;
        return Math.Max(n, 4);
    }

    private void Build(Surface copy, float scale, float deepest)
    {
        int levels = LevelsFor(scale);
        if (_atlas is null || _pyramidFor != (copy.Width, copy.Height, levels)) Allocate(copy.Width, copy.Height, levels);
        // Levels up to the first as wide as the deepest frost asked for (the glass and the stats blend no further).
        _built = 1;
        while (_built < _levels && LevelSigma[_built - 1] < deepest) _built++;
        _pass.LevelCount = _built;
        for (int k = 1; k <= _built; k++)
        {
            var source = k == 1 ? copy : _down[k - 1]!;
            Pass(_downPass, source, _down[k]!, 0, 0);
        }
        for (int k = 1; k <= _built; k++)
        {
            var source = _down[k]!;
            int to = Math.Max(k - UpSteps, 0);
            for (int l = k - 1; l >= to; l--)
            {
                if (l == to) Pass(_upPass, source, _atlas!, _region[k - 1].X, _region[k - 1].Y, _extent.Width >> l, _extent.Height >> l);
                else { Pass(_upPass, source, _scratch[l]!, 0, 0); source = _scratch[l]!; }
            }
        }
    }

    // One pass of the pyramid or the stats: source to the target's region (its whole size unless given).
    private void Pass(ID3D11PixelShader shader, Surface source, Surface target, int x, int y, int width = 0, int height = 0)
    {
        _pass.DstOriginX = x; _pass.DstOriginY = y;
        _pass.SrcInverseX = 1f / source.Width; _pass.SrcInverseY = 1f / source.Height;
        _context.UpdateSubresource(in _pass, _passConstants);
        _context.OMSetRenderTargets(target.Target);
        _context.RSSetViewport(new Viewport(x, y, width > 0 ? width : target.Width, height > 0 ? height : target.Height));
        _context.PSSetShader(shader);
        _context.PSSetConstantBuffer(0, _passConstants);
        _context.PSSetShaderResource(0, source.View);
        _context.Draw(3, 0);
        Unbind();
    }

    private void Allocate(int width, int height, int levels)
    {
        DisposePyramid();
        int unit = 1 << levels;
        _levels = levels;
        _extent = ((width + unit - 1) / unit * unit, (height + unit - 1) / unit * unit);
        for (int k = 1; k <= levels; k++) _down[k] = new Surface(_device, _extent.Width >> k, _extent.Height >> k, Pyramid);
        for (int l = 1; l < levels; l++)
            if (Enumerable.Range(1, levels).Any(k => l > Math.Max(k - UpSteps, 0) && l < k))
                _scratch[l] = new Surface(_device, _extent.Width >> l, _extent.Height >> l, Pyramid);
        // The atlas: full-size levels down the left, the rest down a column at their right.
        int left = 0, right = 0;
        bool full = false;
        for (int k = 1; k <= levels; k++)
        {
            int at = Math.Max(k - UpSteps, 0);
            if (at == 0) { _region[k - 1] = (0, left); left += _extent.Height; full = true; }
            else { _region[k - 1] = (-1, right); right += _extent.Height >> at; }
        }
        int column = full ? _extent.Width : 0;
        for (int k = 1; k <= levels; k++) if (_region[k - 1].X < 0) _region[k - 1] = (column, _region[k - 1].Y);
        _atlasSize = (column + (right > 0 ? _extent.Width >> 1 : 0), Math.Max(Math.Max(left, right), 1));
        _atlas = new Surface(_device, Math.Max(1, _atlasSize.Width), _atlasSize.Height, Pyramid);
        _band = new Surface(_device, _extent.Width >> 1, _extent.Height >> 1, Pyramid);
        _pyramidFor = (width, height, levels);
        _pass.ExtentX = _extent.Width; _pass.ExtentY = _extent.Height;
        _pass.AtlasInverseX = 1f / _atlas.Width; _pass.AtlasInverseY = 1f / _atlas.Height;
        for (int k = 0; k < MostLevels; k++)
            Unsafe.Add(ref _pass.Level0, k) = k < levels
                ? new System.Numerics.Vector4(_region[k].X, _region[k].Y, 1 << Math.Max(k + 1 - UpSteps, 0), LevelSigma[k])
                : default;
    }

    private void DisposePyramid()
    {
        for (int k = 0; k <= MostLevels; k++) { _down[k]?.Dispose(); _down[k] = null; _scratch[k]?.Dispose(); _scratch[k] = null; }
        _atlas?.Dispose(); _atlas = null; _band?.Dispose(); _band = null;
    }

    // ----- The backdrop's statistics (Frost.hlsl, Stats) -----
    // Per tile of StatsDip, anchored at the island's top middle; the grid's capacity grows with the background and never
    // shrinks, so a tile keeps its place on the screen (and its history) while the island opens. Two textures, last frame's and
    // this one's. Smoothed in time against the clock between frames: a value rising at UpHalf (its weight half per 16.7 ms),
    // falling with the time constant DownTau; the mean at UpHalf either way.
    private const float StatsDip = 8, BandDip = 1, UpHalfMs = 1000 / 60f, DownTauMs = 300;
    private readonly Surface?[] _stats = new Surface?[2];
    private int _statsAt;
    private (int Cols, int Rows, int Tile) _statsFor;
    private long _lastFrame;
    private int _stamp;
    private float _statsOriginX, _statsOriginY;
    // The previews draw unrelated backdrops one after another: each frame's stats its own, unsmoothed.
    internal bool InstantStats { get; set; }
    // Off: no dither and no grain (the frost's measures).
    internal bool GrainOn { get; set; } = true;

    private void Statistics(Surface copy, int pad, in LiquidFrame frame, float scale, float frost, float band)
    {
        int tile = Math.Max(4, (int)MathF.Round(StatsDip * scale));
        int cols = (copy.Width + tile - 1) / tile + 2, rows = (copy.Height + tile - 1) / tile + 1;
        cols = (cols + 15) / 16 * 16; rows = (rows + 7) / 8 * 8;
        bool reset = InstantStats;
        if (_stats[0] is null || tile != _statsFor.Tile || cols > _statsFor.Cols || rows > _statsFor.Rows)
        {
            _stats[0]?.Dispose(); _stats[1]?.Dispose();
            cols = Math.Max(cols, tile == _statsFor.Tile ? _statsFor.Cols : 0); rows = Math.Max(rows, tile == _statsFor.Tile ? _statsFor.Rows : 0);
            _stats[0] = new Surface(_device, cols, rows, Pyramid);
            _stats[1] = new Surface(_device, cols, rows, Pyramid);
            _statsFor = (cols, rows, tile);
            reset = true;
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _lastFrame == 0 ? 1000 : (float)Math.Min(1000, (now - _lastFrame) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        _lastFrame = now;
        var last = _stats[_statsAt]!;
        var next = _stats[_statsAt ^ 1]!;
        // Each frame's tiles carry its stamp: a tile not drawn last frame (never, or the island was elsewhere) starts afresh.
        _pass.StampLast = reset ? -1 : _stamp;
        _stamp = _stamp % 1000 + 1;
        _pass.StampNow = _stamp;
        _statsOriginX = MathF.Floor(frame.Width / 2f) - _statsFor.Cols / 2 * tile;
        _statsOriginY = 0;
        _pass.StatsTile = tile;
        _pass.SigmaFrost = frost;
        _pass.SigmaBand = band;
        _pass.StatsOriginX = _statsOriginX; _pass.StatsOriginY = _statsOriginY;
        _pass.AlphaUp = 1 - MathF.Pow(.5f, dt / UpHalfMs);
        _pass.AlphaDown = 1 - MathF.Exp(-dt / DownTauMs);
        _pass.BackdropPad = pad;
        // Only the tiles over the island, and one around it; the band image under them (half size, in its own pixels).
        int x0 = Math.Clamp((int)MathF.Floor(-_statsOriginX / tile) - 1, 0, next.Width), x1 = Math.Clamp((int)MathF.Ceiling((frame.Width - _statsOriginX) / tile) + 1, x0, next.Width);
        int y0 = Math.Clamp((int)MathF.Floor(-_statsOriginY / tile) - 1, 0, next.Height), y1 = Math.Clamp((int)MathF.Ceiling((frame.Height - _statsOriginY) / tile) + 1, y0, next.Height);
        var image = _band!;
        int bx0 = Math.Clamp((int)MathF.Floor((_statsOriginX + x0 * tile + pad) / 2) - 1, 0, image.Width), bx1 = Math.Clamp((int)MathF.Ceiling((_statsOriginX + x1 * tile + pad) / 2) + 1, bx0, image.Width);
        int by0 = Math.Clamp((int)MathF.Floor((_statsOriginY + y0 * tile + pad) / 2) - 1, 0, image.Height), by1 = Math.Clamp((int)MathF.Ceiling((_statsOriginY + y1 * tile + pad) / 2) + 1, by0, image.Height);
        _pass.DstOriginX = 0; _pass.DstOriginY = 0;
        _pass.SrcInverseX = 1f / copy.Width; _pass.SrcInverseY = 1f / copy.Height;
        _context.UpdateSubresource(in _pass, _passConstants);
        _context.OMSetRenderTargets(image.Target);
        _context.RSSetViewport(new Viewport(bx0, by0, Math.Max(1, bx1 - bx0), Math.Max(1, by1 - by0)));
        _context.PSSetShader(_bandPass);
        _context.PSSetConstantBuffer(0, _passConstants);
        Bind(copy.View, _atlas!.View, null, null);
        _context.Draw(3, 0);
        Unbind();
        _pass.SrcInverseX = 1f / image.Width; _pass.SrcInverseY = 1f / image.Height;
        _context.UpdateSubresource(in _pass, _passConstants);
        _context.OMSetRenderTargets(next.Target);
        _context.RSSetViewport(new Viewport(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)));
        _context.PSSetShader(_statsPass);
        Bind(image.View, null, last.View, null);
        _context.Draw(3, 0);
        Unbind();
        _statsAt ^= 1;
    }

    // The last frame's stats as drawn (RGBA, Cols x Rows: mean luma, frosted band RMS, unfrosted band RMS, a stamp), and where the
    // tiles lie (island pixels of tile (0, 0)'s corner, a tile's side): for calibration (FxPreview --frost-stats).
    internal (float[] Data, int Cols, int Rows, float OriginX, float OriginY, int Tile) ReadStats()
    {
        var surface = _stats[_statsAt] ?? throw new InvalidOperationException("Nothing drawn yet.");
        return (ReadHalf(surface.Texture, 0, 0, surface.Width, surface.Height), surface.Width, surface.Height, _statsOriginX, _statsOriginY, _statsFor.Tile);
    }

    // Level B_k (1-based) of the last frame's pyramid (RGBA, at its own size), and its texel in device pixels.
    internal (float[] Data, int Width, int Height, int Texel) ReadLevel(int k)
    {
        if (_atlas is null || k < 1 || k > _built) throw new ArgumentOutOfRangeException(nameof(k));
        int at = Math.Max(k - UpSteps, 0);
        int w = _extent.Width >> at, h = _extent.Height >> at;
        return (ReadHalf(_atlas.Texture, _region[k - 1].X, _region[k - 1].Y, w, h), w, h, 1 << at);
    }
    internal int Levels => _built;

    private float[] ReadHalf(ID3D11Texture2D texture, int x, int y, int width, int height)
    {
        using var staging = _device.CreateTexture2D(new Texture2DDescription(Pyramid, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, new Box(x, y, 0, x + width, y + height, 1));
        var mapped = _context.Map(staging, 0, MapMode.Read);
        var data = new float[width * height * 4];
        var row = new byte[width * 8];
        for (int j = 0; j < height; j++)
        {
            Marshal.Copy(mapped.DataPointer + (nint)(j * mapped.RowPitch), row, 0, row.Length);
            for (int i = 0; i < width * 4; i++) data[j * width * 4 + i] = (float)BitConverter.ToHalf(row, i * 2);
        }
        _context.Unmap(staging, 0);
        return data;
    }

    // ----- What lies behind -----
    // The mean brightness of the background under the island, for automatic liquid glass (GlassAppearance): the copy or a
    // downsample of it (at most MeasureTexels across: fine enough that a word on a page shows in the percentiles) is copied to
    // a staging texture after the pyramid is made, and read on a later frame, never waited for. E17: a ring of MeasureRing
    // staging textures, so a copy goes out on each drawing while earlier ones are still in flight, and a read takes the newest
    // that is in (not the oldest, as a single texture made it). The percentiles come from a histogram (no sort: a measure of
    // the open panel reads some 37,000 texels).
    private const int MeasureTexels = 512, Bins = 1024, MeasureRing = 3;
    // Each slot keeps its own size (the level grows while the pyramid does, as the panel opens): a copy in flight is read at
    // its size, never dropped for a newer one's.
    private readonly ID3D11Texture2D?[] _measures = new ID3D11Texture2D?[MeasureRing];
    private readonly (int Width, int Height, int Level, Format Format)[] _measureFor = new (int, int, int, Format)[MeasureRing];
    private int _measureHead, _measurePending;
    private readonly (int Pad, int Width, int Height)[] _measuredRegions = new (int, int, int)[MeasureRing];
    private readonly byte[] _measureRow = new byte[MeasureTexels * 4 * 2];
    private readonly int[] _histogram = new int[Bins];
    // The backdrop's brightness under the island, its 2nd and 98th percentiles (sRGB luma), from the last measure: the band
    // the glass moves it into (legibility) is scaled from these. Before any, the widest.
    private float _bandA, _bandB = 1, _bandMean = .5f;
    public (float Low, float High) Band => (_bandA, _bandB);
    // The last measure's histogram (Bins bins over luma 0..1): automatic liquid glass takes the mean of its middle
    // (GlassAppearance.RangeMean, BackdropLook).
    public ReadOnlySpan<int> Histogram => _histogram;

    private void Sample(int pad, int width, int height, int least)
    {
        if (_measurePending == MeasureRing || _copy is null) return;
        // The copy itself, or the first downsample (D_k, Frost.hlsl) at most MeasureTexels across and at least as coarse as asked.
        int level = 0, w = _copy.Width, h = _copy.Height;
        while ((w > MeasureTexels || level < least) && level < _built) { level++; w = _down[level]!.Width; h = _down[level]!.Height; }
        var source = level == 0 ? _copy : _down[level]!;
        var format = level == 0 ? _copyFor.Format : Pyramid;
        int slot = (_measureHead + _measurePending) % MeasureRing;
        if (_measures[slot] is null || _measureFor[slot] != (w, h, level, format))
        {
            // A free slot of another size is made again (it holds no copy). On a device just lost this throws; that frame then
            // goes without a measure, and the next look at the desktop finds the loss (DesktopSource.Generation).
            _measures[slot]?.Dispose(); _measures[slot] = null;
            try { _measures[slot] = _device.CreateTexture2D(new Texture2DDescription(format, (uint)w, (uint)h, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)); }
            catch (SharpGen.Runtime.SharpGenException) { return; }
            _measureFor[slot] = (w, h, level, format);
        }
        var target = _measures[slot]!;
        _context.CopySubresourceRegion(target, 0, 0, 0, 0, source.Texture, 0);
        _measuredRegions[slot] = (pad, width, height);
        _measurePending++;
    }

    // The mean luma (0..1, sRGB) of the background under the island, from the newest copy whose answer is in; false while
    // none is. A copy goes out on each drawing while the ring has room.
    // Whether a measure is out, to be read on a later frame.
    public bool Measuring => _measurePending > 0;
    public bool TryBackdrop(out double luma) => Read(wait: false, out luma);
    // The same, waiting for the copy (the previews, which draw once).
    internal bool MeasureNow(out double luma) => Read(wait: true, out luma);
    private bool Read(bool wait, out double luma)
    {
        // The newest copy that is in decides (tried newest first, so only one is read); it and those before it are done with.
        luma = 0;
        for (int k = _measurePending - 1; k >= 0; k--)
        {
            int slot = (_measureHead + k) % MeasureRing;
            if (!ReadOne(slot, wait && k == _measurePending - 1, out double one)) continue;
            _measureHead = (slot + 1) % MeasureRing; _measurePending -= k + 1;
            if (double.IsNaN(one)) return false;
            luma = one;
            return true;
        }
        return false;
    }
    // One copy: false while it is not in; NaN for a copy with nothing of the island in it.
    private bool ReadOne(int slot, bool wait, out double luma)
    {
        luma = double.NaN;
        var measure = _measures[slot]!;
        if (_context.Map(measure, 0, MapMode.Read, wait ? Vortice.Direct3D11.MapFlags.None : Vortice.Direct3D11.MapFlags.DoNotWait, out var mapped).Failure) return false;
        try
        {
            var (w, h, level, format) = _measureFor[slot];
            double step = 1 << level, sum = 0;
            int count = 0;
            var (pad, width, height) = _measuredRegions[slot];
            bool half = format == Pyramid;
            Array.Clear(_histogram);
            for (int y = 0; y < h; y++)
            {
                double cy = (y + .5) * step - pad;
                if (cy < 0 || cy > height) continue;
                Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), _measureRow, 0, w * (half ? 8 : 4));
                for (int x = 0; x < w; x++)
                {
                    double cx = (x + .5) * step - pad;
                    if (cx < 0 || cx > width) continue;
                    double l;
                    if (half)
                    {
                        // RGBA, half floats (sRGB values, filtered).
                        int o = x * 8;
                        l = Math.Clamp(.2126 * (double)BitConverter.ToHalf(_measureRow, o) + .7152 * (double)BitConverter.ToHalf(_measureRow, o + 2)
                            + .0722 * (double)BitConverter.ToHalf(_measureRow, o + 4), 0, 1);
                    }
                    else
                    {
                        int o = x * 4;
                        l = (.0722 * _measureRow[o] + .7152 * _measureRow[o + 1] + .2126 * _measureRow[o + 2]) / 255;
                    }
                    sum += l;
                    count++;
                    _histogram[Math.Min(Bins - 1, (int)(l * Bins))]++;
                }
            }
            if (count == 0) return true;
            luma = sum / count;
            _bandMean = (float)luma;
            _bandA = Percentile(.02); _bandB = Percentile(.98);
            return true;
        }
        finally
        {
            _context.Unmap(measure, 0);
        }
    }

    // The luma below which the given share of the last measure's texels lie (the middle of its bin).
    private float Percentile(double share)
    {
        int total = 0;
        foreach (int c in _histogram) total += c;
        int wanted = (int)(total * share), seen = 0;
        for (int i = 0; i < Bins; i++)
        {
            seen += _histogram[i];
            if (seen > wanted) return (i + .5f) / Bins;
        }
        return 1;
    }

    private void Write(in LiquidFrame f, Texture2DDescription background, int pad)
    {
        float w = f.Width, h = f.Height;
        // The shape's measures, clamped as IslandBorder.BuildOutline clamps them.
        float shoulder = Math.Clamp(f.Shoulder, 0, MathF.Min(w / 4, h / 2));
        float depth = shoulder * 7 / 16;
        float side = w / 2 - shoulder;
        float limit = MathF.Max(0, MathF.Min(shoulder > 0 ? h - depth : h / 2, side));
        float bottom = Math.Clamp(f.CornerBottom, 0, limit);
        float top = shoulder > 0 ? 0 : Math.Clamp(f.CornerTop, 0, MathF.Min(w, h) / 2);
        float scale = MathF.Max(.5f, f.Scale);
        // A square top (the panel, the notch) lies against the screen's edge: the source's box reaches above it, so its ring
        // and inset shadow show along the sides and the bottom only.
        float extra = top > 0 || f.Faithful ? 0 : h * .6f + 20 * scale;
        // The source's strength: 1 for a button, pressed or not (its dragged lenses' 1.2 does not apply to the island).
        const float strength = 1;
        // The source's ring is a fifth of the glass's shorter side, fit for its buttons and lenses; on a larger island (the open
        // panel) it is kept to about RingCss CSS pixels.
        float edge = f.Faithful ? f.Edge : f.Edge * MathF.Max(1, MathF.Min(w, h + extra) / scale / RingCss);
        // The map's box in CSS pixels: the source's element (whole pixels) when given, else the island's own.
        bool css = f.Faithful && f.CssWidth > 0 && f.CssHeight > 0;
        float boxW = css ? f.CssWidth : w / scale, boxH = css ? f.CssHeight : (h + extra) / scale;
        var key = (boxW, boxH, scale, edge, f.Rad, f.Faithful);
        if (key != _maxFor) { _maxHalf = (float)LiquidFrame.MaxHalf(boxW, boxH, edge, strength, f.Rad, f.Faithful); _maxFor = key; }
        var c = new Constants
        {
            Width = w, Height = h,
            InverseX = 1f / background.Width, InverseY = 1f / background.Height,
            Pad = pad, HalfWidth = w / 2, Side = side, Shoulder = shoulder, Depth = depth,
            CornerTop = top, CornerBottom = bottom, SideTop = shoulder > 0 ? depth : top > 0 ? top : -1e4f,
            TintR = f.Tint_R, TintG = f.Tint_G, TintB = f.Tint_B, TintA = Math.Clamp(f.TintAlpha, 0, 1),
            Scale = scale, Ref = MathF.Max(0, f.Ref), Edge = MathF.Max(.05f, edge), Rad = Math.Clamp(f.Rad, 0, 1),
            MaxHalf = _maxHalf, Faithful = f.Faithful ? 1 : 0, TopExtra = extra, Strength = strength,
            Contrast = f.Contrast, Brightness = f.Brightness, Saturate = f.Saturate, Low = Math.Clamp(f.BandLow, 0, 1),
            High = Math.Clamp(f.BandHigh, 0, 1), InsetY = f.InsetY, InsetBlur = MathF.Max(.1f, f.InsetBlur), InsetAlpha = Math.Clamp(f.InsetAlpha, 0, 1),
            BandA = _bandA, BandB = _bandB,
            // A square top's sides: the inset shadow there is a gray band down the whole panel; narrowed to about half.
            Narrow = top > 0 || f.Faithful ? 0 : 1.2f,
            Press = Math.Clamp(f.Press, 0, 1), Dark = f.Dark, Band = 24 * scale,
            LinearMap = f.LinearMap ? 1 : 0, LinearFilters = f.LinearFilters ? 1 : 0,
            BoxCssX = css ? boxW : 0, BoxCssY = css ? boxH : 0,
            FilterEndX = f.Faithful && f.FilterWidth > 0 && f.FilterHeight > 0 ? Math.Min(f.FilterWidth, w) : 0,
            FilterEndY = f.Faithful && f.FilterWidth > 0 && f.FilterHeight > 0 ? Math.Min(f.FilterHeight, h + extra) : 0,
            // The open panel's frost: about FrostDip across, faded in with the opening; never on the source's own glass.
            Open = f.Faithful ? 0 : Math.Clamp(f.Open, 0, 1), FrostRadius = FrostDip * scale, BandMean = _bandMean,
            // The collapsed island's content row: a light frost behind it (ContentFrostDip), feathered over FeatherDip.
            ContentFrost = ContentFrostDip * scale, Feather = FeatherDip * scale,
            ContentLeft = f.Faithful ? 0 : f.ContentLeft, ContentTop = f.ContentTop, ContentRight = f.Faithful ? 0 : f.ContentRight, ContentBottom = f.ContentBottom,
            Hover = f.Faithful || !float.IsFinite(f.PointerX) || !float.IsFinite(f.PointerY) ? 0 : Math.Clamp(f.Hover, 0, 1),
            PointerX = float.IsFinite(f.PointerX) ? f.PointerX : -1e4f, PointerY = float.IsFinite(f.PointerY) ? f.PointerY : -1e4f,
            // The frost's pyramid and the stats (Frost.hlsl).
            AtlasInverseX = _pass.AtlasInverseX, AtlasInverseY = _pass.AtlasInverseY, FrostLevels = _built, GrainOn = GrainOn ? 1 : 0,
            FrostExtentX = _extent.Width, FrostExtentY = _extent.Height, StatsOriginX = _statsOriginX, StatsOriginY = _statsOriginY,
            StatsInverseX = 1f / _statsFor.Cols, StatsInverseY = 1f / _statsFor.Rows, StatsTile = _statsFor.Tile,
            // The material's masking constant (Liquid.hlsl, Material; calibrated offscreen, round 12): the frame's, else the default.
            Sigma0 = f.Sigma0 > 0 ? f.Sigma0 : DefaultSigma0,
            TextTile = _textInfo.Tile, TextInverseX = _textInfo.InverseX, TextInverseY = _textInfo.InverseY, TextOn = _textInfo.On,
            BodyLight = f.BodyLight > 0 ? f.BodyLight : DefaultBodyLight, Zoned = f.Zoned ? 1 : 0, ReachLight = f.ReachLight, Probe = f.Probe ? 1 : 0,
            Presence = Math.Clamp(f.Presence, 0, 1), Plain = f.Plain < 0 ? -1 : Math.Clamp(f.Plain, 0, 1)
        };
        for (int k = 0; k < MostLevels; k++) Unsafe.Add(ref c.Level0, k) = Unsafe.Add(ref _pass.Level0, k);
        _context.UpdateSubresource(in c, _constants);
    }

    private const float RingCss = 90;
    // Calibrated in round 12 (pr-plan liquid-glass round-12): see Liquid.hlsl, Material.
    internal const float DefaultSigma0 = .02f;
    // The light look's luminosity opacity between the text regions of the open panel (round 12b sweep; Acrylic's is .85).
    internal const float DefaultBodyLight = .6f;
    // The open panel's frost, DIP (Liquid.hlsl, Frost: a Gaussian of deviation FrostDeviation of it).
    // The frosts are Apple's regular material, about: 32 DIP on the open panel, 12 behind the collapsed content row.
    private const float FrostDip = 32, ContentFrostDip = 12, FeatherDip = 4, FrostDeviation = .75f;
    private float _maxHalf;
    private (float, float, float, float, float, bool) _maxFor;

    // The swap chain, its buffers at least the frame's size: they grow (in steps of 64 pixels, so an opening panel does not
    // reallocate every frame) and never shrink, and the source size shows only the frame's part of them, from the top-left.
    // E17 (round 15): a frame latency waitable swap chain (latency 1, a bounded wait before drawing) was tried and left out: the
    // glass followed the island no closer (1.00 frames behind either way, PerfCost) and the process's GPU engine time while
    // opening and closing doubled (4.6 to 8.8 ms/s).
    private IDXGISwapChain2 EnsureSwapChain(int width, int height)
    {
        if (_swapChain is null)
        {
            _capacity = (Step(width), Step(height));
            using var dxgi = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var description = new SwapChainDescription1((uint)_capacity.Width, (uint)_capacity.Height, Pixels, false, Usage.RenderTargetOutput, 2, Scaling.Stretch,
                SwapEffect.FlipSequential, AlphaMode.Premultiplied);
            using var created = factory.CreateSwapChainForComposition(_device, description);
            _swapChain = created.QueryInterface<IDXGISwapChain2>();
        }
        else if (width > _capacity.Width || height > _capacity.Height)
        {
            _target?.Dispose(); _target = null;
            _capacity = (Math.Max(_capacity.Width, Step(width)), Math.Max(_capacity.Height, Step(height)));
            _swapChain.ResizeBuffers(2, (uint)_capacity.Width, (uint)_capacity.Height, Pixels, SwapChainFlags.None).CheckError();
            _size = default;
        }
        if (_size != (width, height))
        {
            _swapChain.SetSourceSize((uint)width, (uint)height);
            _size = (width, height);
        }
        if (_target is null)
        {
            using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _target = _device.CreateRenderTargetView(buffer);
        }
        return _swapChain;

        static int Step(int size) => (Math.Max(1, size) + 63) / 64 * 64;
    }

    // ICompositorInterop.CreateCompositionSurfaceForSwapChain, the second method after IUnknown's, called through its vtable.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSurfaceForSwapChain(IntPtr self, IntPtr swapChain, out IntPtr surface);
    internal static ICompositionSurface CreateSurface(Compositor compositor, IntPtr swapChain)
    {
        var unknown = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        var iid = new Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var interop));
        try
        {
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(interop), 4 * IntPtr.Size);
            var create = Marshal.GetDelegateForFunctionPointer<CreateSurfaceForSwapChain>(method);
            Marshal.ThrowExceptionForHR(create(interop, swapChain, out var surface));
            try { return MarshalInterface<ICompositionSurface>.FromAbi(surface); }
            finally { Marshal.Release(surface); }
        }
        finally { Marshal.Release(interop); }
    }

    // The shaders' bytecode, compiled ahead (Liquid.vs.cso, Liquid.ps.cso; Frost.*.cso; made by
    // `FxPreview --compile-liquid`: compiling the source at run time took about 4 s on the UI thread) and embedded with the hash
    // of the source they were made from. Should a hash not be its embedded source's (the source edited, the bytecode not made
    // again; a test catches it), that source is compiled here instead, once per run.
    internal readonly record struct Bytecode(byte[] Vertex, byte[] Pixel, byte[] Zoned, byte[] Down, byte[] Up, byte[] Band, byte[] Stats);
    private static readonly Lazy<Bytecode> Shaders = new(() =>
    {
        var liquid = Resource("BrimDeck.LiquidGlass.Liquid.hlsl") ?? throw new InvalidOperationException("Liquid.hlsl is not embedded.");
        var frost = Resource("BrimDeck.LiquidGlass.Frost.hlsl") ?? throw new InvalidOperationException("Frost.hlsl is not embedded.");
        var vertex = Resource("BrimDeck.LiquidGlass.Liquid.vs.cso");
        var pixel = Resource("BrimDeck.LiquidGlass.Liquid.ps.cso");
        var zoned = Resource("BrimDeck.LiquidGlass.Liquid.zoned.cso");
        var (v, p, z) = vertex is not null && pixel is not null && zoned is not null && EmbeddedHash() == SourceHash(liquid) ? (vertex, pixel, zoned) : Compiled(liquid);
        var down = Resource("BrimDeck.LiquidGlass.Frost.down.cso");
        var up = Resource("BrimDeck.LiquidGlass.Frost.up.cso");
        var band = Resource("BrimDeck.LiquidGlass.Frost.band.cso");
        var stats = Resource("BrimDeck.LiquidGlass.Frost.stats.cso");
        var (d, u, b, st) = down is not null && up is not null && band is not null && stats is not null && EmbeddedFrostHash() == SourceHash(frost)
            ? (down, up, band, stats) : CompiledFrost(frost);
        return new Bytecode(v, p, z, d, u, b, st);
    });

    // The hash the bytecode is made for: SHA-256 of the source's bytes, its line ends taken as LF (a checkout may give it CRLF),
    // lower-case hex.
    internal static string SourceHash(byte[] source) => Convert.ToHexStringLower(SHA256.HashData(source.Where(b => b != 13).ToArray()));
    internal static byte[] EmbeddedSource() => Resource("BrimDeck.LiquidGlass.Liquid.hlsl")!;
    internal static string? EmbeddedHash() => Resource("BrimDeck.LiquidGlass.Liquid.hlsl.sha256") is { } made ? Encoding.ASCII.GetString(made).Trim() : null;
    internal static byte[] EmbeddedFrostSource() => Resource("BrimDeck.LiquidGlass.Frost.hlsl")!;
    internal static string? EmbeddedFrostHash() => Resource("BrimDeck.LiquidGlass.Frost.hlsl.sha256") is { } made ? Encoding.ASCII.GetString(made).Trim() : null;

    private static byte[]? Resource(string name)
    {
        using var stream = typeof(LiquidRenderer).Assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // Both shaders compiled from the source (bytes, ASCII only: the compiler is given them as they are).
    internal static (byte[] Vertex, byte[] Pixel, byte[] Zoned) Compiled(byte[] source)
    {
        string text = Encoding.UTF8.GetString(source);
        return (Compile(text, "Liquid.hlsl", "VS", "vs_5_0"), Compile(text, "Liquid.hlsl", "PS", "ps_5_0"), Compile(text, "Liquid.hlsl", "PSZoned", "ps_5_0"));
    }

    // The frost's passes compiled from Frost.hlsl.
    internal static (byte[] Down, byte[] Up, byte[] Band, byte[] Stats) CompiledFrost(byte[] source)
    {
        string text = Encoding.UTF8.GetString(source);
        return (Compile(text, "Frost.hlsl", "Down", "ps_5_0"), Compile(text, "Frost.hlsl", "Up", "ps_5_0"), Compile(text, "Frost.hlsl", "Band", "ps_5_0"),
            Compile(text, "Frost.hlsl", "Stats", "ps_5_0"));
    }

    private static byte[] Compile(string source, string name, string entry, string profile)
    {
        var result = Compiler.Compile(source, null!, null!, entry, name, profile, ShaderFlags.OptimizationLevel3, EffectFlags.None, out var code, out var errors);
        using (errors)
        using (code)
        {
            if (result.Failure || code is null) throw new InvalidOperationException(name + ": " + (errors?.AsString() ?? result.ToString()));
            return code.AsSpan().ToArray();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _target?.Dispose(); _swapChain?.Dispose();
        _copy?.Dispose(); DisposePyramid(); _stats[0]?.Dispose(); _stats[1]?.Dispose();
        foreach (var measure in _measures) measure?.Dispose();
        // The immediate context is not let go: Vortice keeps one wrapper for the device, which the capture side uses too.
        _constants.Dispose(); _passConstants.Dispose(); _smooth.Dispose();
        _pixel.Dispose(); _zoned?.Dispose(); _downPass.Dispose(); _upPass.Dispose(); _bandPass.Dispose(); _statsPass.Dispose(); _vertex.Dispose();
    }

    // The shader's constant buffer (Liquid.hlsl, Frame), in its order: 16 floats a row, nothing straddling a row.
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public float Width, Height, InverseX, InverseY;
        public float Pad, HalfWidth, Side, Shoulder;
        public float Depth, CornerTop, CornerBottom, SideTop;
        public float TintR, TintG, TintB, TintA;
        public float Scale, Ref, Edge, Rad;
        public float MaxHalf, Faithful, TopExtra, Strength;
        public float Contrast, Brightness, Saturate, Low;
        public float High, InsetY, InsetBlur, InsetAlpha;
        public float BandA, BandB, Narrow, Press;
        public float Dark, Band, LinearMap, LinearFilters;
        public float BoxCssX, BoxCssY, FilterEndX, FilterEndY;
        public float Open, FrostRadius, BandMean, ContentFrost;
        public float ContentLeft, ContentTop, ContentRight, ContentBottom;
        public float Hover, PointerX, PointerY, Feather;
        public float AtlasInverseX, AtlasInverseY, FrostLevels, GrainOn;
        public float FrostExtentX, FrostExtentY, StatsOriginX, StatsOriginY;
        public float StatsInverseX, StatsInverseY, StatsTile, Sigma0;
        public System.Numerics.Vector4 Level0, Level1, Level2, Level3, Level4, Level5, Level6, Level7;
        public float TextTile, TextInverseX, TextInverseY, TextOn;
        public float BodyLight, Zoned, ReachLight, Probe;
        public float Presence, Plain, Spare1, Spare2;
    }

    // The frost passes' constant buffer (Frost.hlsl, FrostPass), in its order.
    [StructLayout(LayoutKind.Sequential)]
    private struct PassConstants
    {
        public float DstOriginX, DstOriginY, SrcInverseX, SrcInverseY;
        public float ExtentX, ExtentY, AtlasInverseX, AtlasInverseY;
        public float LevelCount, StatsTile, SigmaFrost, SigmaBand;
        public float StatsOriginX, StatsOriginY, AlphaUp, AlphaDown;
        public float BackdropPad, StampLast, StampNow, Spare0;
        public System.Numerics.Vector4 Level0, Level1, Level2, Level3, Level4, Level5, Level6, Level7;
    }
}
