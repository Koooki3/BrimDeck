using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BrimDeck.LiquidGlass;

// The desktop under the island, on the GPU, without BrimDeck's own windows in it: what the liquid glass refracts.
//
// It comes from DXGI Desktop Duplication, with our windows marked WCA_EXCLUDED_FROM_DDA (Exclude). Measured on Windows 11
// (26300) with a WPF layered window and a plain one: Desktop Duplication then shows exactly the desktop behind them (no pixel of
// theirs, no difference from a capture with them hidden), on an 8-bit output and on one composed in 16-bit float alike, while
// screenshots, BitBlt and Windows.Graphics.Capture still show them. WDA_EXCLUDEFROMCAPTURE would also hide them, but from every
// screenshot and recording too; Windows.Graphics.Capture ignores the duplication attribute.
//
// Desktop Duplication hands out frames of a whole output, only when something on it changed. Each new frame's changed parts (its
// dirty and moved rectangles) are copied into a copy of the output kept here, so the region can move or resize without waiting
// for the next change; the region texture is then filled from that copy (a plain copy, or a small shader where the region
// reaches past the output's edge or the desktop is float). Windows still reports our own windows' redraws as changes over the
// region, though their pixels are not in the frame: the region is compared with what it was (an occlusion query counting the
// pixels that differ), so the island's own motion, and the refraction drawn from this, never count as a change of the desktop.
//
// Update returns false, and the caller shows the plain blur instead, while there is nothing to show: protected content masked out
// on the output, access lost (a UAC prompt or the lock screen on the secure desktop, a mode change, a full-screen exclusive
// game) until duplication can start again, an output turned to portrait, or a device lost until a new one is made.
//
// Not thread-safe: Update and everything drawn with Device's immediate context belong to one thread (the renderer's).
internal sealed class DesktopSource : IDisposable
{
    // How long after a failure to start duplicating, or a lost access, before trying again; how long Update may wait for the
    // first frame of a new duplication (later frames are only taken when there already is one).
    private const int RetryMilliseconds = 500, FirstFrameMilliseconds = 20;
    // Dirty and moved rectangles copied one by one before the whole output is copied instead.
    private const int MaxRects = 48;
    // How long Update waits for the comparison's result before taking a reported change as a change.
    private const double CompareMilliseconds = 4;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private Luid _adapter;
    private Output? _output;
    private IDXGIOutputDuplication? _duplication;
    private long _retryAt;
    // The copy of the output's desktop (in the duplication's format), and whether it holds a whole frame yet.
    private ID3D11Texture2D? _desktop;
    private ID3D11ShaderResourceView? _desktopView;
    private bool _desktopValid, _masked;
    // The changed parts of the frames taken since the region was last looked at, in output coordinates (or all of it), and of
    // the frame being taken.
    private readonly List<RawRect> _changes = [], _frameChanges = [];
    private bool _changedAll;
    private readonly RawRect[] _rects = new RawRect[MaxRects];
    private readonly OutduplMoveRect[] _moves = new OutduplMoveRect[MaxRects];
    // The region texture, the region it was last filled for, and what it held before the last fill (for the comparison).
    private ID3D11Texture2D? _texture, _before, _scratch;
    private ID3D11ShaderResourceView? _textureView, _beforeView;
    private ID3D11RenderTargetView? _textureTarget, _scratchTarget;
    private RECT? _filled;
    private (int Width, int Height) _textureSize;
    // The shaders, made per device; null where the compiler is missing.
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _fillShader, _differShader;
    private ID3D11Buffer? _fillParameters;
    private ID3D11Query? _differQuery;
    private bool _shadersTried;
    private bool _disposed;

    // An output of the desktop as DXGI lists it: its adapter, its place, its monitor.
    private sealed record Output(Luid Adapter, uint AdapterIndex, uint OutputIndex, RECT Bounds, IntPtr Monitor, ModeRotation Rotation, string DeviceName);

    // Device on the adapter that drives the primary monitor, until a region elsewhere moves it.
    public DesktopSource()
    {
        var outputs = Outputs();
        var primary = outputs.FirstOrDefault(o => o.Bounds.Contains(0, 0)) ?? outputs.FirstOrDefault()
            ?? throw new NotSupportedException("No desktop output to duplicate.");
        if (!CreateDevice(primary.Adapter)) throw new NotSupportedException("No Direct3D 11 device on the desktop's adapter.");
    }

    // A DesktopSource, or null where this machine cannot make one (no hardware adapter with an output, no Direct3D 11).
    public static DesktopSource? TryCreate()
    {
        try { return new DesktopSource(); }
        catch (Exception ex) when (ex is NotSupportedException or SharpGen.Runtime.SharpGenException or COMException) { return null; }
    }

    // Device on the adapter that drives the monitor containing the region's centre (device pixels). Recreated when that changes.
    public ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(DesktopSource));

    // The desktop under the region of the last successful Update: BGRA8, the region's size.
    public ID3D11Texture2D? Texture => _texture;

    // Raised when Device was replaced (adapter or monitor change, device lost): the renderer must rebuild its resources.
    public event Action? DeviceChanged;

    // Hides a window of ours from the capture (the island window, notices, the blur window): from Desktop Duplication only, so
    // screenshots and recordings still show it.
    public static void Exclude(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var on = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(on, 1);
            var data = new CompositionAttribute { Attribute = ExcludedFromDda, Data = on, Size = sizeof(int) };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(on); }
    }

    // Copies the desktop under the region (device pixels, screen coordinates, already padded by the caller for refraction
    // reach) into Texture, a BGRA8 texture of exactly the region's size, re-created when the size changes. A region over two
    // monitors takes the one under its centre; the part beyond that output's edge repeats the edge. False when nothing could be
    // captured; Changed is true when the pixels differ from the last call's. Leaves the immediate context's pipeline state cleared
    // (where it draws, it sets what it needs and clears it after).
    public bool Update(RECT region, out bool changed)
    {
        changed = false;
        if (_disposed || region.IsEmpty) return false;
        try
        {
            if ((_device is null || _device.DeviceRemovedReason.Failure) && !Recover()) return false;
            if (!Duplicate(region)) return false;
            if (!Take()) return false;
            if (!_desktopValid || _masked) return false;
            var output = _output!;
            var inside = region.Intersect(output.Bounds);
            if (inside.IsEmpty) return false;
            bool partial = inside != region;
            bool linear = _desktopLinear;
            if ((partial || linear) && !EnsureShaders()) return false;

            bool resized = EnsureTexture(region.Width, region.Height);
            bool moved = resized || _filled != region;
            var local = region.Offset(-output.Bounds.Left, -output.Bounds.Top);
            if (!moved && !Touches(local)) { _changes.Clear(); _changedAll = false; return true; }
            _changes.Clear(); _changedAll = false;

            var context = _context!;
            if (!moved) context.CopyResource(_before, _texture);
            if (!partial && !linear)
                context.CopySubresourceRegion(_texture, 0, 0, 0, 0, _desktop, 0, new Box(local.Left, local.Top, 0, local.Right, local.Bottom, 1));
            else Fill(local, output, linear);
            _filled = region;
            changed = moved || Differs(region.Width, region.Height);
            return true;
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            // A device lost makes a new one; anything else (out of memory, a call refused) starts duplicating again later.
            if (IsDeviceLoss(ex.HResult)) Recover();
            else { StopDuplicating(); _retryAt = Environment.TickCount64 + RetryMilliseconds; }
            return false;
        }
    }

    // ---------------------------------------------------------------- duplication

    // Duplication of the output under the region's centre, started (or started again after a loss) where needed, on a device
    // on that output's adapter.
    private bool Duplicate(RECT region)
    {
        int x = region.Left + region.Width / 2, y = region.Top + region.Height / 2;
        if (_duplication is not null && _output!.Bounds.Contains(x, y)) return true;
        if (Environment.TickCount64 < _retryAt) return false;
        var outputs = Outputs();
        var output = outputs.FirstOrDefault(o => o.Bounds.Contains(x, y))
            ?? outputs.OrderByDescending(o => Area(o.Bounds.Intersect(region))).FirstOrDefault(o => o.Bounds.Overlaps(region));
        if (output is null) return false;
        if (_duplication is not null && output == _output) return true;
        StopDuplicating();
        if (output.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified)) { _output = output; _retryAt = Environment.TickCount64 + RetryMilliseconds; return false; }
        if (output.Adapter != _adapter)
        {
            ReleaseDevice();
            if (!CreateDevice(output.Adapter)) { _retryAt = Environment.TickCount64 + RetryMilliseconds; return false; }
            DeviceChanged?.Invoke();
        }
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1(output.AdapterIndex, out var adapter).CheckError();
            using (adapter)
            {
                adapter.EnumOutputs(output.OutputIndex, out var dxgiOutput).CheckError();
                using (dxgiOutput)
                {
                    // DuplicateOutput1 asks for BGRA8 (an output composed in float hands out float frames whatever is asked);
                    // it needs a per-monitor-v2 process, so DuplicateOutput stands in where it is refused.
                    using var output5 = dxgiOutput.QueryInterfaceOrNull<IDXGIOutput5>();
                    try { _duplication = output5?.DuplicateOutput1(_device!, [Format.B8G8R8A8_UNorm]); }
                    catch (SharpGen.Runtime.SharpGenException ex) when (!IsDeviceLoss(ex.HResult)) { }
                    if (_duplication is null) { using var output1 = dxgiOutput.QueryInterface<IDXGIOutput1>(); _duplication = output1.DuplicateOutput(_device!); }
                }
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            // Not now (the secure desktop, too many duplications, a full-screen exclusive game), or a device lost meanwhile.
            if (IsDeviceLoss(ex.HResult)) Lose();
            _retryAt = Environment.TickCount64 + RetryMilliseconds;
            return false;
        }
        _output = output;
        _desktopValid = false; _masked = false;
        _filled = null;
        return true;
    }

    // The frame waiting, if any: its changed parts go into the copy of the output. False when access was lost.
    private bool Take()
    {
        var duplication = _duplication!;
        var result = duplication.AcquireNextFrame(_desktopValid ? 0u : FirstFrameMilliseconds, out var info, out var resource);
        if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return true;
        if (result.Failure)
        {
            if (IsDeviceLoss(result.Code)) Lose();
            else { StopDuplicating(); _retryAt = Environment.TickCount64 + RetryMilliseconds; }
            return false;
        }
        try
        {
            // A frame with only the pointer moved (LastPresentTime 0) changes nothing here: the pointer is never in the image.
            if (info.LastPresentTime == 0 && _desktopValid) return true;
            _masked = info.ProtectedContentMaskedOut;
            using var frame = resource.QueryInterface<ID3D11Texture2D>();
            var context = _context!;
            if (!EnsureDesktop(frame.Description) || !_desktopValid || !Changes(info, frame.Description))
            {
                context.CopyResource(_desktop, frame);
                _changedAll = true;
            }
            else
                foreach (var rect in _frameChanges)
                {
                    context.CopySubresourceRegion(_desktop, 0, (uint)rect.Left, (uint)rect.Top, 0, frame, 0, new Box(rect.Left, rect.Top, 0, rect.Right, rect.Bottom, 1));
                    _changes.Add(rect);
                }
            _desktopValid = true;
        }
        finally
        {
            resource.Dispose();
            duplication.ReleaseFrame();
        }
        return true;
    }

    // The frame's dirty rectangles and moved rectangles' destinations (the frame holds the moved pixels where they went), in
    // _frameChanges; false when there are too many or Windows cannot say, and the whole output is copied instead.
    private bool Changes(OutduplFrameInfo info, Texture2DDescription frame)
    {
        _frameChanges.Clear();
        if (info.TotalMetadataBufferSize == 0) return true;
        var duplication = _duplication!;
        int rectSize = Marshal.SizeOf<RawRect>(), moveSize = Marshal.SizeOf<OutduplMoveRect>();
        if (duplication.GetFrameMoveRects((uint)(_moves.Length * moveSize), _moves, out uint moveBytes).Failure) return false;
        if (duplication.GetFrameDirtyRects((uint)(_rects.Length * rectSize), _rects, out uint dirtyBytes).Failure) return false;
        int moves = (int)moveBytes / moveSize, dirty = (int)dirtyBytes / rectSize;
        for (int i = 0; i < moves; i++) Add(_moves[i].DestinationRect);
        for (int i = 0; i < dirty; i++) Add(_rects[i]);
        return true;

        void Add(RawRect rect)
        {
            var clipped = new RawRect(Math.Max(rect.Left, 0), Math.Max(rect.Top, 0), Math.Min(rect.Right, (int)frame.Width), Math.Min(rect.Bottom, (int)frame.Height));
            if (clipped.Right > clipped.Left && clipped.Bottom > clipped.Top) _frameChanges.Add(clipped);
        }
    }

    // Whether anything changed under the region (output coordinates) since it was last filled.
    private bool Touches(RECT local)
    {
        if (_changedAll) return true;
        foreach (var rect in _changes)
            if (rect.Left < local.Right && rect.Right > local.Left && rect.Top < local.Bottom && rect.Bottom > local.Top) return true;
        return false;
    }

    private bool EnsureDesktop(Texture2DDescription frame)
    {
        if (_desktop is { } desktop && ReferenceEquals(_desktopFor, _output) && desktop.Description is var d && d.Width == frame.Width && d.Height == frame.Height && d.Format == frame.Format) return true;
        _desktopFor = _output;
        _desktopView?.Dispose(); _desktop?.Dispose();
        _desktop = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = frame.Width, Height = frame.Height, MipLevels = 1, ArraySize = 1, Format = frame.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
        });
        _desktopView = _device.CreateShaderResourceView(_desktop);
        _desktopValid = false;
        _filled = null;
        _desktopLinear = frame.Format != Format.B8G8R8A8_UNorm;
        _white = _output is { } output && _desktopLinear ? OutputWhite.For(output.DeviceName) : 1;
        return false;
    }

    // The output the copy is of, whether it is float (scRGB), and the linear value of SDR white in it.
    private Output? _desktopFor;
    private bool _desktopLinear;
    private float _white = 1;

    // ---------------------------------------------------------------- the region

    // The region texture (and its companions for the comparison) at this size; true when they were made anew.
    private bool EnsureTexture(int width, int height)
    {
        if (_texture is not null && _textureSize == (width, height)) return false;
        ReleaseTexture();
        _textureSize = (width, height);
        var device = _device!;
        var description = new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
        };
        _texture = device.CreateTexture2D(description);
        _textureView = device.CreateShaderResourceView(_texture);
        _textureTarget = device.CreateRenderTargetView(_texture);
        _before = device.CreateTexture2D(description with { BindFlags = BindFlags.ShaderResource });
        _beforeView = device.CreateShaderResourceView(_before);
        _scratch = device.CreateTexture2D(description with { Format = Format.R8_UNorm, BindFlags = BindFlags.RenderTarget });
        _scratchTarget = device.CreateRenderTargetView(_scratch);
        _filled = null;
        return true;
    }

    // The region from the copy of the output through the fill shader: the part beyond the output's edge repeats the edge, and a
    // float desktop becomes 8-bit sRGB.
    private void Fill(RECT local, Output output, bool linear)
    {
        var context = _context!;
        var parameters = new DesktopShaders.FillParameters
        {
            OffsetX = local.Left, OffsetY = local.Top, LastX = output.Bounds.Width - 1, LastY = output.Bounds.Height - 1,
            White = _white, Linear = linear ? 1u : 0u
        };
        context.UpdateSubresource(in parameters, _fillParameters!);
        Draw(_textureTarget!, local.Width, local.Height, _fillShader!, _desktopView!, null);
    }

    // Whether the region now differs from before the fill, by the occlusion query over the differ shader. Where the shaders
    // are missing, or the GPU does not answer in time, a reported change is taken as a change.
    private bool Differs(int width, int height)
    {
        if (!EnsureShaders()) return true;
        var context = _context!;
        context.Begin(_differQuery!);
        Draw(_scratchTarget!, width, height, _differShader!, _textureView!, _beforeView);
        context.End(_differQuery!);
        context.Flush();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            var result = context.GetData(_differQuery!, _queryResult, sizeof(ulong), AsyncGetDataFlags.DoNotFlush);
            if (result.Code == 0) return Marshal.ReadInt64(_queryResult) != 0;
            if (result.Failure || System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds > CompareMilliseconds) return true;
            Thread.SpinWait(64);
        }
    }

    private readonly IntPtr _queryResult = Marshal.AllocHGlobal(sizeof(ulong));

    // One full-target triangle: the state it needs is set here and cleared after, so the renderer's own state is not assumed.
    private void Draw(ID3D11RenderTargetView target, int width, int height, ID3D11PixelShader shader, ID3D11ShaderResourceView first, ID3D11ShaderResourceView? second)
    {
        var context = _context!;
        context.ClearState();
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(shader);
        context.PSSetShaderResources(0, second is null ? [first] : [first, second]);
        context.PSSetConstantBuffer(0, _fillParameters);
        context.RSSetViewport(new Viewport(0, 0, width, height));
        context.OMSetRenderTargets(target);
        context.Draw(3, 0);
        context.ClearState();
    }

    private bool EnsureShaders()
    {
        if (_fillShader is not null) return true;
        if (_shadersTried) return false;
        _shadersTried = true;
        if (DesktopShaders.Bytecode is not { } code) return false;
        var device = _device!;
        _vertexShader = device.CreateVertexShader(code.Vertex);
        _differShader = device.CreatePixelShader(code.Differ);
        _fillParameters = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<DesktopShaders.FillParameters>(), BindFlags.ConstantBuffer));
        _differQuery = device.CreateQuery(new QueryDescription(QueryType.Occlusion));
        _fillShader = device.CreatePixelShader(code.Fill);
        return true;
    }

    // ---------------------------------------------------------------- devices and outputs

    private bool CreateDevice(Luid adapterLuid)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
                using (adapter)
                {
                    if (adapter.Description1.Luid != adapterLuid) continue;
                    var result = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out ID3D11Device device, out ID3D11DeviceContext context);
                    if (result.Failure || device is null) return false;
                    _device = device; _context = context; _adapter = adapterLuid;
                    return true;
                }
        }
        catch (SharpGen.Runtime.SharpGenException) { }
        return false;
    }

    // The device was lost (driver update, GPU reset, adapter removed): everything made on it goes, and a new one is made on the
    // same adapter (or the first one with an output) right away, or on a later Update.
    private bool Recover()
    {
        if (_device is not null) Lose();
        if (Environment.TickCount64 < _retryAt) return false;
        var outputs = Outputs();
        var adapter = outputs.FirstOrDefault(o => o.Adapter == _adapter)?.Adapter ?? outputs.FirstOrDefault()?.Adapter;
        if (adapter is null || !CreateDevice(adapter.Value)) { _retryAt = Environment.TickCount64 + RetryMilliseconds; return false; }
        DeviceChanged?.Invoke();
        return true;
    }

    private void Lose()
    {
        StopDuplicating();
        ReleaseDevice();
        _output = null;
    }

    private static List<Output> Outputs()
    {
        var list = new List<Output>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
                using (adapter)
                {
                    var description = adapter.Description1;
                    if ((description.Flags & AdapterFlags.Software) != 0) continue;
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                        using (output)
                        {
                            var d = output.Description;
                            if (!d.AttachedToDesktop) continue;
                            var r = d.DesktopCoordinates;
                            list.Add(new Output(description.Luid, a, o, new RECT(r.Left, r.Top, r.Right, r.Bottom), d.Monitor, d.Rotation, d.DeviceName));
                        }
                }
        }
        catch (SharpGen.Runtime.SharpGenException) { }
        return list;
    }

    private static long Area(RECT rect) => rect.IsEmpty ? 0 : (long)rect.Width * rect.Height;

    private static bool IsDeviceLoss(int hresult) =>
        hresult == Vortice.DXGI.ResultCode.DeviceRemoved.Code || hresult == Vortice.DXGI.ResultCode.DeviceReset.Code || hresult == Vortice.DXGI.ResultCode.DeviceHung.Code;

    private void StopDuplicating()
    {
        _duplication?.Dispose(); _duplication = null;
        _desktopValid = false; _masked = false;
        _changes.Clear(); _changedAll = false;
    }

    private void ReleaseTexture()
    {
        _scratchTarget?.Dispose(); _scratch?.Dispose(); _beforeView?.Dispose(); _before?.Dispose();
        _textureTarget?.Dispose(); _textureView?.Dispose(); _texture?.Dispose();
        _scratchTarget = null; _scratch = null; _beforeView = null; _before = null; _textureTarget = null; _textureView = null; _texture = null;
        _filled = null;
    }

    private void ReleaseDevice()
    {
        StopDuplicating();
        ReleaseTexture();
        _desktopView?.Dispose(); _desktop?.Dispose(); _desktopView = null; _desktop = null;
        _differQuery?.Dispose(); _fillParameters?.Dispose(); _differShader?.Dispose(); _fillShader?.Dispose(); _vertexShader?.Dispose();
        _differQuery = null; _fillParameters = null; _differShader = null; _fillShader = null; _vertexShader = null; _shadersTried = false;
        _context?.ClearState(); _context?.Dispose(); _device?.Dispose();
        _context = null; _device = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseDevice();
        Marshal.FreeHGlobal(_queryResult);
    }

    private const int ExcludedFromDda = 24;
    [StructLayout(LayoutKind.Sequential)] private struct CompositionAttribute { public int Attribute; public IntPtr Data; public int Size; }
    [DllImport("user32")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttribute data);
}
