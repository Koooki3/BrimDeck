using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.UI.Composition;

namespace BrimDeck.LiquidGlass;

// Round 16, step 1: the acrylic body's noise, as Windows' Acrylic lays it over (microsoft-ui-xaml, MIT: AcrylicBrush's noise
// texture at 2 %): a fine grey grain that keeps a wide, smooth blur from banding and gives it the frosted surface's texture.
// One tile of Tile x Tile device pixels, made once for the run in a small composition swap chain of its own (no Win2D in a WPF
// app; the swap chain is the one way the desktop compositor takes pixels of ours), drawn once and never again. GlassBackdrop
// lays it over the body in a grid of sprites sharing its brush, inside the body's clip. Seeded, so the offscreen sheets draw
// the same grain (Pixels).
internal static class AcrylicNoise
{
    public const int Tile = 256;
    public const double Opacity = .02;
    private static ID3D11Device? _device;
    private static IDXGISwapChain1? _swapChain;
    private static bool _failed;

    // The tile's premultiplied BGRA pixels: a grey of random lightness at Opacity, from the middle half of the range (the
    // whole range at 200 % weighed on the page checks' reading comfort as detail behind the text, 1.10 to 1.12).
    public static byte[] Pixels()
    {
        var random = new Random(1983);
        var pixels = new byte[Tile * Tile * 4];
        byte alpha = (byte)Math.Round(Opacity * 255);
        for (int i = 0; i < Tile * Tile; i++)
        {
            byte v = (byte)Math.Round((64 + random.Next(129)) * Opacity);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = v;
            pixels[i * 4 + 3] = alpha;
        }
        return pixels;
    }

    // The tile's surface for this compositor; null where it cannot be made (no Direct3D, a failure: the body goes without).
    public static ICompositionSurface? Surface(Compositor compositor)
    {
        if (_failed) return null;
        try
        {
            if (_swapChain is null)
            {
                var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out ID3D11Device device, out ID3D11DeviceContext context);
                if (result.Failure || device is null) { _failed = true; return null; }
                _device = device;
                using var dxgi = device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgi.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory2>();
                var description = new SwapChainDescription1(Tile, Tile, Format.B8G8R8A8_UNorm, false, Usage.RenderTargetOutput, 2, Scaling.Stretch,
                    SwapEffect.FlipSequential, Vortice.DXGI.AlphaMode.Premultiplied);
                _swapChain = factory.CreateSwapChainForComposition(device, description);
                using (var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
                    context.UpdateSubresource(Pixels(), buffer, 0, Tile * 4, 0);
                _swapChain.Present(0, PresentFlags.None);
                context.Dispose();
            }
            return LiquidRenderer.CreateSurface(compositor, _swapChain.NativePointer);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException or InvalidOperationException or DllNotFoundException)
        {
            _failed = true;
            _swapChain?.Dispose(); _swapChain = null; _device?.Dispose(); _device = null;
            return null;
        }
    }
}
