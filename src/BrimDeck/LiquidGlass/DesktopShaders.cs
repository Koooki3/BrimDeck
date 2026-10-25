using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace BrimDeck.LiquidGlass;

// The two small shaders DesktopSource draws with, compiled at run time by the d3dcompiler_47.dll every Windows 10 and 11 ships
// (no compiler package, no build step). Compiled once per run; the shader objects are made per device.
//
// Fill writes the region texture from the copy of the desktop where a plain copy cannot: a region reaching past its output's
// edge (the pixels beyond repeat the edge) or a desktop composed in 16-bit float (advanced colour, HDR), which it turns into
// 8-bit sRGB with the output's SDR white as white. Differ counts, through an occlusion query, the pixels that differ between the
// region now and before: our own windows over the region (excluded from the capture, but still reported as changed by Windows)
// then cost no redraw.
internal static class DesktopShaders
{
    private const string Source = """
        Texture2D<float4> Desktop : register(t0);
        Texture2D<float4> Before : register(t1);
        cbuffer Fill : register(b0)
        {
            int2 Offset;  // the region's corner in the desktop copy
            int2 Last;    // the desktop copy's last pixel the region may read
            float White;  // linear value of SDR white in a float desktop
            uint Linear;  // 1 when the desktop copy is linear float (scRGB)
            float2 Unused;
        };

        float4 Vertex(uint id : SV_VertexID) : SV_Position
        {
            float2 at = float2((id << 1) & 2, id & 2);
            return float4(at * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        float3 Encode(float3 c)
        {
            return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1 / 2.4) - 0.055;
        }

        float4 FillPixel(float4 position : SV_Position) : SV_Target
        {
            int2 at = clamp(int2(position.xy) + Offset, int2(0, 0), Last);
            float4 c = Desktop.Load(int3(at, 0));
            if (Linear != 0) c.rgb = Encode(saturate(c.rgb / White));
            return float4(c.rgb, 1);
        }

        float DifferPixel(float4 position : SV_Position) : SV_Target
        {
            int3 at = int3(position.xy, 0);
            if (all(abs(Desktop.Load(at) - Before.Load(at)) < 0.5 / 255)) discard;
            return 1;
        }
        """;

    private static byte[]? _vertex, _fill, _differ;
    private static bool _failed;

    // The compiled bytecode, or null where the compiler is missing or refused (the caller then cannot fill a clamped or float
    // region, and treats every reported change as a change).
    public static (byte[] Vertex, byte[] Fill, byte[] Differ)? Bytecode
    {
        get
        {
            if (_failed) return null;
            try
            {
                _vertex ??= Compile("Vertex", "vs_4_0");
                _fill ??= Compile("FillPixel", "ps_4_0");
                _differ ??= Compile("DifferPixel", "ps_4_0");
                return (_vertex, _fill, _differ);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or COMException)
            {
                _failed = true;
                return null;
            }
        }
    }

    // The parameters Fill reads, laid out as its constant buffer (16-byte rows).
    [StructLayout(LayoutKind.Sequential)]
    public struct FillParameters
    {
        public int OffsetX, OffsetY, LastX, LastY;
        public float White;
        public uint Linear;
        public float Unused0, Unused1;
    }

    private static byte[] Compile(string entry, string target)
    {
        var text = Encoding.UTF8.GetBytes(Source);
        const uint OptimizationLevel3 = 1 << 15;
        int hr = D3DCompile(text, text.Length, "DesktopShaders", IntPtr.Zero, IntPtr.Zero, entry, target, OptimizationLevel3, 0, out var code, out var errors);
        using var errorBlob = errors != IntPtr.Zero ? new Blob(errors) : null;
        if (hr < 0 || code == IntPtr.Zero)
            throw new InvalidOperationException($"{entry}: {(errorBlob is null ? hr.ToString("x8") : errorBlob.AsString())}");
        using var blob = new Blob(code);
        return blob.AsBytes();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] source, nint size, string name, IntPtr defines, IntPtr include,
        string entry, string target, uint flags, uint effectFlags, out IntPtr code, out IntPtr errors);
}
