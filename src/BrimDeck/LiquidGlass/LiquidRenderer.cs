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

// One frame of liquid glass. Sizes are device pixels; the shape is the island's (IslandBorder.BuildOutline) at this size.
//   Width, Height: the island; the swap chain is resized to it.
//   CornerTop, CornerBottom, Shoulder: as the island draws them; CornerTop 0 is a square top (the open panel, the notch).
//   Tint: a thin tint over the glass, straight color and alpha (the preset's: legibility comes from the band, not the tint, so
//     the WPF surface over the glass stays transparent).
//   Thickness: the bevel's width, how far in from the edge the glass bends the background.
//   Refraction: how far the rim shifts the background, in bevel widths (about the displacement at the rim / Thickness).
//   Dispersion: 0..1, red and blue spread apart at the rim.
//   PointerX, PointerY: the pointer in island pixels, NaN while it is away; the light then stays where it was and turns back
//     to top-left as Hover eases to 0, so the caller's easing of Hover is all the easing there is.
//   Hover, Press, Morph: 0..1, eased by the caller; Morph pulses the refraction while the shape changes.
//   Dark: 1 for a dark glass theme (a cooler, dimmer light).
// Added:
//   BandLow, BandHigh: the band of brightness the theme's text was fitted to (PanelGlass.Low/High; 0, 1 for none). The middle
//     is held to it adaptively: only a background outside it is moved, just far enough (see Liquid.hlsl).
//   Saturation: the colors behind the glass, this much more saturated (vibrancy).
//   EdgeBand: 0..1, how far that band relaxes at the rim, where the refraction shows and no text sits.
//   Frost: the middle's frost as a mip level of the background (1 is a 2 px blur, 2 a 4 px one); a busy background and the
//     open panel take a little more.
//   Scale: the display scale (device pixels per DIP), which sizes the rim line.
//   Light: overall strength of the highlights and rim.
//   Open: 0 collapsed .. 1 the open panel.
//   Shade: how much the rim darkens opposite the light, in the background's own hue. Sheen: the soft highlight inside the lit bevel.
internal readonly record struct LiquidFrame(
    int Width, int Height,
    float CornerTop, float CornerBottom, float Shoulder,
    float Tint_R, float Tint_G, float Tint_B, float TintAlpha,
    float Thickness,
    float Refraction,
    float Dispersion,
    float PointerX, float PointerY,
    float Hover, float Press,
    float Morph,
    float Dark,
    float BandLow = 0, float BandHigh = 1, float Saturation = 1, float EdgeBand = 0,
    float Frost = 1.5f, float Scale = 1, float Light = 1, float Open = 0, float Shade = 0, float Sheen = 0)
{
    // The defaults for BrimDeck's two glass themes at a display scale, for a shape and size. Clear glass in both: the band is
    // the theme's (liquid-glass-light: .85..1 for its dark text; liquid-glass-dark: 0...28 for its light text), held only where
    // the background lies outside it; a thin tint, a light frost, a little vibrancy.
    public static LiquidFrame Preset(bool dark, int width, int height, float cornerTop, float cornerBottom, float shoulder, float scale) => dark
        ? new(width, height, cornerTop, cornerBottom, shoulder, 0x12 / 255f, 0x14 / 255f, 0x19 / 255f, .08f,
            Thickness: Bevel(height, scale), Refraction: 1.45f, Dispersion: .35f, PointerX: float.NaN, PointerY: float.NaN, Hover: 0, Press: 0, Morph: 0, Dark: 1,
            BandLow: 0, BandHigh: .28f, Saturation: 1.35f, EdgeBand: .12f, Frost: 1.5f + MathF.Log2(scale), Scale: scale, Light: 1, Shade: .25f, Sheen: .3f)
        : new(width, height, cornerTop, cornerBottom, shoulder, 1, 1, 1, .05f,
            Thickness: Bevel(height, scale), Refraction: 1.45f, Dispersion: .35f, PointerX: float.NaN, PointerY: float.NaN, Hover: 0, Press: 0, Morph: 0, Dark: 0,
            BandLow: .85f, BandHigh: 1, Saturation: 1.3f, EdgeBand: .25f, Frost: 1.5f + MathF.Log2(scale), Scale: scale, Light: 1, Shade: .4f, Sheen: .2f);

    // The bevel: 14 DIP on the open panel, and under half the height on the small collapsed island, so the rim stays a rim.
    private static float Bevel(int height, float scale) => MathF.Min(14 * scale, height * .4f);
}

// Liquid glass: the desktop under the island (captured by the caller, padded around it, BrimDeck's own windows left out) drawn
// through a slab of thick glass by one pixel shader (Liquid.hlsl, compiled from the embedded source on first use) into a
// swap chain for composition, which a Composition brush shows in place of the blur window's blur. Rendering happens only when
// the caller asks (the background, the pointer, the shape or an animation changed); a frame copies the background into a
// texture of its own with mips (the frost reads them), updates one constant buffer and draws one triangle. Nothing is
// allocated per frame, and the whole pipeline state is set on every frame (the capture side clears the context's state). The
// background is read only through that copy, so its texture needs no view of ours. All calls on one thread, with the device's immediate context not used elsewhere at the same time (the
// constructor turns on the device's multithread protection, so the capture side may use it from another thread).
internal sealed class LiquidRenderer : IDisposable
{
    private const Format Pixels = Format.B8G8R8A8_UNorm;
    // The light's default direction in the island's plane, toward the top-left (y down).
    private static readonly float DefaultAngle = MathF.Atan2(-.85f, -.55f);

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
        using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>()) multithread?.SetMultithreadProtected(true);
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
        // The bevel never reaches past the middle of the shape, where the normals of opposite edges meet.
        float inner = top > 0 ? MathF.Min(w, h) / 2 : MathF.Min(h, side);
        float thickness = Math.Clamp(f.Thickness, 1, MathF.Max(1, inner * .65f));

        if (float.IsFinite(f.PointerX) && float.IsFinite(f.PointerY)) { _pointerX = f.PointerX; _pointerY = f.PointerY; }
        bool known = float.IsFinite(_pointerX);
        float hover = Math.Clamp(f.Hover, 0, 1);
        // The light comes from the pointer's side of the island (from above when it is over the middle), turned toward from
        // the default angle by Hover.
        float angle = DefaultAngle;
        if (known && hover > 0)
        {
            float vx = (_pointerX - w / 2) / MathF.Max(1, w / 2 - h / 2 + 1), vy = (_pointerY - h / 2) / MathF.Max(1, h / 2) - .6f;
            float toward = MathF.Atan2(vy, vx);
            float turn = MathF.IEEERemainder(toward - angle, MathF.Tau);
            angle += turn * hover;
        }
        var c = new Constants
        {
            Width = w, Height = h,
            InverseX = 1f / background.Width, InverseY = 1f / background.Height,
            Pad = pad, HalfWidth = w / 2, Side = side, Shoulder = shoulder, Depth = depth,
            CornerTop = top, CornerBottom = bottom, SideTop = shoulder > 0 ? depth : top > 0 ? top : -1e4f,
            TintR = f.Tint_R, TintG = f.Tint_G, TintB = f.Tint_B, TintA = Math.Clamp(f.TintAlpha, 0, 1),
            Thickness = thickness, Refraction = MathF.Max(0, f.Refraction), Dispersion = Math.Clamp(f.Dispersion, 0, 1), Frost = MathF.Max(0, f.Frost),
            PointerX = known ? _pointerX : -1e4f, PointerY = known ? _pointerY : -1e4f, Near = known ? hover : 0, Hover = hover,
            LightX = MathF.Cos(angle), LightY = MathF.Sin(angle),
            Press = Math.Clamp(f.Press, 0, 1), Morph = Math.Clamp(f.Morph, 0, 1), Dark = f.Dark,
            Low = Math.Clamp(f.BandLow, 0, 1), High = Math.Clamp(f.BandHigh, 0, 1), Saturation = MathF.Max(0, f.Saturation), EdgeBand = Math.Clamp(f.EdgeBand, 0, 1),
            Scale = MathF.Max(.5f, f.Scale), Gain = MathF.Max(0, f.Light), Glow = MathF.Max(12 * f.Scale, h * .9f),
            Open = Math.Clamp(f.Open, 0, 1), Shade = Math.Clamp(f.Shade, 0, 1), Sheen = MathF.Max(0, f.Sheen)
        };
        _context.UpdateSubresource(in c, _constants);
    }

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
        _mipsView?.Dispose(); _mips?.Dispose();
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
        public float Thickness, Refraction, Dispersion, Frost;
        public float PointerX, PointerY, Near, Hover;
        public float LightX, LightY, Press, Morph;
        public float Dark, Low, High, Saturation;
        public float EdgeBand, Scale, Gain, Glow;
        public float Open, Shade, Sheen, Spare;
    }
}
