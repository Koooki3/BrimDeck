using System.IO;
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
//   Hover, Press, Morph: 0..1, eased by the caller. Hover lights the glass from the pointer (the source's .lit-glow), Press
//     gives the source's pressed shadows and thicker ring (strength 1.2), Morph thickens the ring as the shape changes.
//   Dark: 1 for the dark look.
//   BandLow, BandHigh: the band of brightness the theme's text was fitted to (PanelGlass.Low/High; 0, 1 for none): only a
//     background outside it is moved, just far enough (legibility, see Liquid.hlsl).
//   Scale: device pixels per DIP (the source's CSS pixel).
//   Open: 0 collapsed .. 1 the open panel.
//   Ref, Edge, Rad: the source's refraction, ring thickness and inner corner (its sliders; 1, 1, .6 by default).
//   Contrast, Brightness, Saturate: the source's backdrop-filter (1.2, 1.05, 1.1).
//   InsetY, InsetBlur, InsetAlpha: the source's inset shadow (0 -8px 20px rgba(0,0,0,.14)), shrunk for low shapes.
//   Faithful: the source's own uv field (the proof sheets), not the island's.
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
    float InsetY = -8, float InsetBlur = 20, float InsetAlpha = .14f, bool Faithful = false)
{
    // The source's height the inset shadow was made for (its buttons, about 44 CSS pixels): lower shapes (the capsule, the bar)
    // take it in proportion, and very low ones (under Thin) fainter still, so a 20-pixel bar is not one full-height gradient.
    private const float SourceHeight = 44, Thin = 28;

    // The two looks, at a display scale, for a shape and size: the source's glass in both, white-tinted; the theme's band for its
    // text (liquid-glass-light: .85..1 for dark text; liquid-glass-dark: 0...28 for light text).
    public static LiquidFrame Preset(bool dark, int width, int height, float cornerTop, float cornerBottom, float shoulder, float scale)
    {
        float css = height / MathF.Max(.5f, scale);
        float fit = MathF.Min(1, css / SourceHeight);
        float thin = MathF.Min(1, css / Thin);
        return new(width, height, cornerTop, cornerBottom, shoulder, 1, 1, 1, .08f,
            PointerX: float.NaN, PointerY: float.NaN, Hover: 0, Press: 0, Morph: 0, Dark: dark ? 1 : 0,
            BandLow: dark ? 0 : .85f, BandHigh: dark ? .28f : 1, Scale: scale,
            InsetY: -8 * fit, InsetBlur: 20 * fit, InsetAlpha: .14f * thin * thin);
    }

    // Between two looks (light glass to dark as automatic liquid glass turns), t from a to b: every look's value in between,
    // the shape and the pointer a's.
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
        var light = Preset(false, 1, 1, 0, 0, 0, 1);
        var deep = Preset(true, 1, 1, 0, 0, 0, 1);
        double t = Math.Clamp(dark, 0, 1);
        double low = light.BandLow + (deep.BandLow - light.BandLow) * t, high = light.BandHigh + (deep.BandHigh - light.BandHigh) * t;
        double shown = Math.Clamp(luma, low, Math.Max(low, high));
        return toDark ? shown <= TextTurn : shown >= TextTurn;
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
    // for the source exactly; for the island every step-th inside and every texel along the edges, where the largest lie, which
    // costs little as the shape animates.
    public static double MaxHalf(double w, double h, double edge, double strength, double rad, bool faithful)
    {
        int W = Math.Max(2, (int)Math.Round(w)), H = Math.Max(2, (int)Math.Round(h));
        int step = faithful ? 1 : Math.Max(1, (int)Math.Sqrt(W * (double)H / 20000));
        double max = 0;
        void At(int x, int y)
        {
            var (dx, dy) = Shift(x, y, W, H, edge, strength, rad, faithful);
            max = Math.Max(max, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        }
        for (int y = 0; y < H; y += step)
            for (int x = 0; x < W; x += step) At(x, y);
        for (int x = 0; x < W; x++) { At(x, 0); At(x, H - 1); }
        for (int y = 0; y < H; y++) { At(0, y); At(W - 1, y); }
        return max * .5;
    }
}

// Liquid glass: the desktop under the island (captured by the caller, padded around it, BrimDeck's own windows left out) drawn
// through a slab of thick glass by one pixel shader (Liquid.hlsl, compiled from the embedded source on first use) into a
// swap chain for composition, which a Composition brush shows in place of the blur window's blur. Rendering happens only when
// the caller asks (the background, the pointer, the shape or an animation changed); a frame copies the background into a
// texture of its own with mips (the frost reads them), updates one constant buffer and draws one triangle. Nothing is
// allocated per frame, and the whole pipeline state is set on every frame (the capture side clears the context's state). The
// background is read only through that copy, so its texture needs no view of ours. All calls on one thread, the capture side's
// (DesktopSource, on the UI thread): the device is used from no other, so it needs no multithread protection.
internal sealed class LiquidRenderer : IDisposable
{
    private const Format Pixels = Format.B8G8R8A8_UNorm;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertex;
    private readonly ID3D11PixelShader _pixel;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private IDXGISwapChain2? _swapChain;
    private ID3D11RenderTargetView? _target;
    // The frame's size, shown from the swap chain's buffers (SetSourceSize), and the buffers' size.
    private (int Width, int Height) _size, _capacity;
    // The background with mips, remade when the background's size or format changes.
    private ID3D11Texture2D? _mips;
    private ID3D11ShaderResourceView? _mipsView;
    private (int Width, int Height, Format Format) _mipsFor;
    // The pointer last seen over the island, which the light keeps turning from while Hover eases out.
    private float _pointerX = float.NaN, _pointerY = float.NaN;
    private bool _disposed;

    public LiquidRenderer(ID3D11Device device)
    {
        _device = device;
        _context = device.ImmediateContext;
        var source = Source();
        using (var code = Compile(source, "VS", "vs_5_0")) _vertex = device.CreateVertexShader(code.AsSpan());
        using (var code = Compile(source, "PS", "ps_5_0")) _pixel = device.CreatePixelShader(code.AsSpan());
        _constants = device.CreateBuffer(new BufferDescription((uint)Unsafe.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None));
        _sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
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
        var view = Mips(background);
        Sample(pad, frame.Width, frame.Height);
        Write(frame, background.Description, pad);
        var context = _context;
        context.OMSetRenderTargets(target);
        context.OMSetBlendState(null);
        context.OMSetDepthStencilState(null);
        context.RSSetState(null);
        context.RSSetViewport(new Viewport(frame.Width, frame.Height));
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertex);
        context.PSSetShader(_pixel);
        context.PSSetConstantBuffer(0, _constants);
        context.PSSetShaderResource(0, view);
        context.PSSetSampler(0, _sampler);
        context.Draw(3, 0);
        // The background is copied into the mips next frame: it must not stay bound as a shader input meanwhile.
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    // The background copied into the mipped texture, and its mips made.
    private ID3D11ShaderResourceView Mips(ID3D11Texture2D background)
    {
        var description = background.Description;
        var key = ((int)description.Width, (int)description.Height, description.Format);
        if (_mips is null || key != _mipsFor)
        {
            _mipsView?.Dispose(); _mips?.Dispose();
            _mips = _device.CreateTexture2D(new Texture2DDescription(description.Format, description.Width, description.Height, 1, 0,
                BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
            _mipsView = _device.CreateShaderResourceView(_mips);
            _mipsFor = key;
        }
        _context.CopySubresourceRegion(_mips, 0, 0, 0, 0, background, 0);
        _context.GenerateMips(_mipsView!);
        return _mipsView!;
    }

    // ----- What lies behind -----
    // The mean brightness of the background under the island, for automatic liquid glass (GlassAppearance): a small mip (at
    // most MeasureTexels across) is copied to a staging texture after the mips are made, and read on a later frame, never
    // waited for. One copy is out at a time.
    private const int MeasureTexels = 64;
    private ID3D11Texture2D? _measure;
    private (int Width, int Height, int Level) _measureFor;
    private bool _measuring;
    private (int Pad, int Width, int Height) _measuredRegion;
    private readonly byte[] _measureRow = new byte[MeasureTexels * 4 * 2];

    private void Sample(int pad, int width, int height)
    {
        if (_measuring || _mips is null) return;
        var d = _mips.Description;
        int level = 0, w = (int)d.Width, h = (int)d.Height;
        while (w > MeasureTexels && level + 1 < (int)d.MipLevels) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); level++; }
        if (_measure is null || _measureFor != (w, h, level))
        {
            _measure?.Dispose();
            _measure = _device.CreateTexture2D(new Texture2DDescription(d.Format, (uint)w, (uint)h, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _measureFor = (w, h, level);
        }
        _context.CopySubresourceRegion(_measure, 0, 0, 0, 0, _mips, (uint)level);
        _measuredRegion = (pad, width, height);
        _measuring = true;
    }

    // The mean luma (0..1, sRGB) of the background under the island, from the last copy whose answer is in; false while none
    // is (the next frame's copy goes out once this one is read).
    // Whether a measure is out, to be read on a later frame.
    public bool Measuring => _measuring;
    public bool TryBackdrop(out double luma)
    {
        luma = 0;
        if (!_measuring || _measure is null) return false;
        if (_context.Map(_measure, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var mapped).Failure) return false;
        try
        {
            var (w, h, level) = _measureFor;
            double step = 1 << level, sum = 0;
            int count = 0;
            var (pad, width, height) = _measuredRegion;
            for (int y = 0; y < h; y++)
            {
                double cy = (y + .5) * step - pad;
                if (cy < 0 || cy > height) continue;
                Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), _measureRow, 0, w * 4);
                for (int x = 0; x < w; x++)
                {
                    double cx = (x + .5) * step - pad;
                    if (cx < 0 || cx > width) continue;
                    int o = x * 4;
                    sum += (.0722 * _measureRow[o] + .7152 * _measureRow[o + 1] + .2126 * _measureRow[o + 2]) / 255;
                    count++;
                }
            }
            if (count == 0) return false;
            luma = sum / count;
            return true;
        }
        finally
        {
            _context.Unmap(_measure, 0);
            _measuring = false;
        }
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
        float strength = 1 + .2f * Math.Clamp(f.Press, 0, 1) + .2f * Math.Clamp(f.Morph, 0, 1);
        // The source's ring is a fifth of the glass's shorter side, fit for its buttons and lenses; on a larger island (the open
        // panel) it is kept to about RingCss CSS pixels.
        float edge = f.Faithful ? f.Edge : f.Edge * MathF.Max(1, MathF.Min(w, h + extra) / scale / RingCss);
        var key = (w, h + extra, scale, edge, strength, f.Rad, f.Faithful);
        if (key != _maxFor) { _maxHalf = (float)LiquidFrame.MaxHalf(w / scale, (h + extra) / scale, edge, strength, f.Rad, f.Faithful); _maxFor = key; }

        if (float.IsFinite(f.PointerX) && float.IsFinite(f.PointerY)) { _pointerX = f.PointerX; _pointerY = f.PointerY; }
        bool known = float.IsFinite(_pointerX);
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
            PointerX = known ? _pointerX : -1e4f, PointerY = known ? _pointerY : -1e4f,
            Lit = known ? LitMax * Math.Clamp(f.Hover, 0, 1) : 0, Press = Math.Clamp(f.Press, 0, 1),
            Dark = f.Dark, Band = 24 * scale
        };
        _context.UpdateSubresource(in c, _constants);
    }

    // The pointer's light at full hover: the source lights a glass this much (.lit-glow at a press, 1) from a source of light
    // touching it; the pointer over the island is a gentler one.
    private const float LitMax = .35f;
    private const float RingCss = 90;
    private float _maxHalf;
    private (float, float, float, float, float, float, bool) _maxFor;

    // The swap chain, its buffers at least the frame's size: they grow (in steps of 64 pixels, so an opening panel does not
    // reallocate every frame) and never shrink, and the source size shows only the frame's part of them, from the top-left.
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
    private static ICompositionSurface CreateSurface(Compositor compositor, IntPtr swapChain)
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

    private static string Source()
    {
        using var stream = typeof(LiquidRenderer).Assembly.GetManifestResourceStream("BrimDeck.LiquidGlass.Liquid.hlsl")
            ?? throw new InvalidOperationException("Liquid.hlsl is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Blob Compile(string source, string entry, string profile)
    {
        var result = Compiler.Compile(source, null!, null!, entry, "Liquid.hlsl", profile, ShaderFlags.OptimizationLevel3, EffectFlags.None, out var code, out var errors);
        using (errors)
            if (result.Failure || code is null) throw new InvalidOperationException("Liquid.hlsl: " + (errors?.AsString() ?? result.ToString()));
        return code;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _target?.Dispose(); _swapChain?.Dispose();
        _mipsView?.Dispose(); _mips?.Dispose(); _measure?.Dispose();
        // The immediate context is not let go: Vortice keeps one wrapper for the device, which the capture side uses too.
        _sampler.Dispose(); _constants.Dispose(); _pixel.Dispose(); _vertex.Dispose();
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
        public float PointerX, PointerY, Lit, Press;
        public float Dark, Band, Spare0, Spare1;
    }
}
