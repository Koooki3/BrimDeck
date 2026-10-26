using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BrimDeck.LiquidGlass;

// The desktop under the island, on the GPU, without BrimDeck's own windows in it: what the liquid glass refracts.
//
// It comes from DXGI Desktop Duplication, with our windows marked WCA_EXCLUDED_FROM_DDA (Exclude, CaptureExclusion: only while
// liquid glass is in use, so a recording through Desktop Duplication misses the island only then). Measured on Windows 11
// (26300) with a WPF layered window and a plain one: Desktop Duplication then shows exactly the desktop behind them (no pixel of
// theirs, no difference from a capture with them hidden), on an 8-bit output and on one composed in 16-bit float alike, while
// screenshots, BitBlt and Windows.Graphics.Capture still show them. WDA_EXCLUDEFROMCAPTURE would also hide them, but from every
// screenshot and recording too; Windows.Graphics.Capture ignores the duplication attribute.
//
// Desktop Duplication hands out frames of a whole output, only when something on it changed. Each new frame's changed parts (its
// dirty and moved rectangles) are copied into a copy of the output kept here, so the region can move or resize without waiting
// for the next change; the region texture is then filled from that copy (a plain copy, or a small shader where the region
// reaches past the output's edge, the desktop is float, or the texture is larger than the region). The region texture grows in
// steps of 64 pixels and never shrinks (an opening panel would otherwise make three textures anew on every frame): the region
// lies at its top-left (Size), and the shader repeats the region's last column and row across the rest, so whatever reads past
// the region (the renderer's mips, its reach) sees the region's edge, as a texture of exactly its size clamped would show. Windows still reports our own windows' redraws as changes over the
// region, though their pixels are not in the frame: the region is compared with what the glass last showed (an occlusion query
// counting the pixels that differ), so the island's own motion, and the refraction drawn from this, never count as a change of
// the desktop. Those rectangles cannot simply be skipped: our windows span the region, mostly transparent, so a change under
// them (a video behind the island) is reported in the same place. The query is never waited for: its answer is read on a later
// Update (Comparing says one is out), and until then the region counts as unchanged.
//
// Update returns false, and the caller shows the plain blur instead, while there is nothing to show: protected content masked out
// on the output, access lost (a UAC prompt or the lock screen on the secure desktop, a mode change, a full-screen exclusive
// game) until duplication can start again, an output turned to portrait, or a device lost until a new one is made.
//
// The first frame of a new duplication may hold no image yet (nothing presented since it started: seen all black): the copy of
// the output is taken from the first frame that was presented, whole, and the region only after that.
//
// Not thread-safe: Update and everything drawn with Device's immediate context belong to one thread at a time. A source is made,
// and its duplication started, off the UI thread (SourceStart: making the device can take hundreds of milliseconds, more on a
// hybrid GPU), then handed over whole to BrimDeck's UI thread, where GlassBackdrop draws; no two threads use it at once, so
// it needs no multithread protection.
internal sealed class DesktopSource : IDisposable
{
    // How long after a failure to start duplicating, or a lost access, before trying again; how long Update may wait for the
    // first frame of a new duplication (later frames are only taken when there already is one).
    private const int RetryMilliseconds = 500, FirstFrameMilliseconds = 20;
    // Dirty and moved rectangles copied one by one before the whole output is copied instead.
    private const int MaxRects = 48;
    // How long a comparison may stay unanswered before the reported change is taken as a change.
    private const long CompareMilliseconds = 250;
    // The region texture's size grows in steps of this many pixels.
    private const int Bucket = 64;

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
    // The region texture, the region it was last filled for, and what the glass last showed of it (the comparison's baseline;
    // not valid after a move, when it is taken from the region texture before the next fill).
    private ID3D11Texture2D? _texture, _before, _scratch;
    private bool _beforeValid;
    // A comparison out on the GPU (and since when), and one owed because the region was filled again meanwhile; whether the
    // region holds a fill not yet shown, and whether the last comparison answered found a change (the desktop is changing).
    private bool _comparing, _compareOwed, _unshown, _hot;
    private long _comparingSince;
    private ID3D11ShaderResourceView? _textureView, _beforeView;
    private ID3D11RenderTargetView? _textureTarget, _scratchTarget;
    private RECT? _filled;
    // The region texture's size, and the region's size in it (at its top-left).
    private (int Width, int Height) _textureSize, _size;
    // Whether the duplication's first frame was waited for; whether a device was made during this Update (DeviceChanged is
    // raised as Update returns, never inside it).
    private bool _waitedFirst, _deviceMade;
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
        Interlocked.Increment(ref _made);
    }

    // Sources made in this process (the tests: a source made off the UI thread is the one opened, not made twice).
    public static int Made => Volatile.Read(ref _made);
    private static int _made;

    // A DesktopSource, or null where this machine cannot make one (no hardware adapter with an output, no Direct3D 11).
    public static DesktopSource? TryCreate()
    {
        try { return new DesktopSource(); }
        catch (Exception ex) when (ex is NotSupportedException or SharpGen.Runtime.SharpGenException or COMException) { return null; }
    }

    // The capture for an island's window and its glass window (GlassBackdrop), made now, on this thread (the tests): see the
    // other Open.
    public static DesktopSource? Open(IntPtr island, IntPtr glass, out bool refused)
    {
        refused = false;
        var source = TryCreate();
        if (source is null) return null;
        if (!source.Hold(island, glass)) { source.Dispose(); refused = true; return null; }
        return source;
    }

    public enum Opening { Opened, Pending, Failed, Refused }

    // The capture for an island's window and its glass window (GlassBackdrop), both left out of it with every other window of
    // ours that can lie over the island (CaptureExclusion), held until the source is disposed. Never made on the UI thread:
    // the one made off it (SourceStart: for the first look, or asked for here, region being where the island's glass is) is
    // taken; while it is being made, Pending (ready is called on this thread once it is there) and the blur stands in; Failed
    // when it could not be made or took longer than SourceStart.BoundMs (a hybrid GPU slow to make its device); Refused where
    // Windows will not leave the island's windows out: the glass would then refract the island itself, and the blur stands in
    // for the rest of the run.
    public static DesktopSource? Open(IntPtr island, IntPtr glass, RECT region, Action ready, out Opening state)
    {
        var source = SourceStart.Take();
        if (source is null)
        {
            if (SourceStart.TakeFailure()) state = Opening.Failed;
            else if (SourceStart.Pending) state = SourceStart.Late ? Opening.Failed : Opening.Pending;
            else { SourceStart.Prepare(region, measure: false); state = Opening.Pending; }
            if (state == Opening.Pending) SourceStart.WhenDone(ready);
            return null;
        }
        if (!source.Hold(island, glass)) { source.Dispose(); state = Opening.Refused; return null; }
        state = Opening.Opened;
        return source;
    }

    // Sources opened for a glass and not yet disposed: while there is one, the live glass measures, and no first look is taken.
    public static int OpenCount { get; private set; }
    private bool _holds;
    private bool Hold(IntPtr island, IntPtr glass)
    {
        if (island == IntPtr.Zero || !CaptureExclusion.Acquire(glass == IntPtr.Zero ? [island] : [island, glass])) return false;
        _holds = true; OpenCount++;
        return true;
    }

    // Device on the adapter that drives the monitor containing the region's centre (device pixels). Recreated when that changes.
    public ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(DesktopSource));

    // The desktop under the region of the last successful Update: BGRA8, at least the region's size (in steps of 64 pixels), the
    // region at its top-left (Size) and its last column and row repeated over the rest; exactly the region's size where the
    // shaders are missing. Kept while the region shrinks or moves, made anew only when it grows past it.
    public ID3D11Texture2D? Texture => _texture;

    // The region's size in Texture (device pixels), as the last successful Update captured it.
    public (int Width, int Height) Size => _size;

    // Counts the devices made: changes when Device was replaced. What was made on the old device (a renderer) must be made again
    // before it draws anything Update captured on the new one.
    public int Generation { get; private set; }

    // Whether a comparison is out: the next Update, soon, tells whether the region changed.
    public bool Comparing => _comparing || _compareOwed;
    // The comparisons answered so far, and the share of the region's pixels the last one found changed (0..1): how much of
    // the backdrop a video or a scroll moves (automatic liquid glass holds its look over a video, GlassAppearance.Changed).
    public int Compared { get; private set; }
    public double ChangedShare { get; private set; }

    // Raised as Update returns when Device was replaced during it (adapter or monitor change, device lost): the renderer must be
    // made again. Never raised inside Update, where a caller's frame still holds the old renderer; the caller checks Generation
    // before drawing what Update captured.
    public event Action? DeviceChanged;

    // Hides a window of ours from the capture (CaptureExclusion.Exclude): from Desktop Duplication only, so screenshots and
    // most recordings still show it (not those that record through Desktop Duplication). False where Windows refused it or
    // does not keep it: the window would then be in the capture, refracted under itself, and the caller shows the blur instead.
    public static bool Exclude(IntPtr hwnd) => CaptureExclusion.Exclude(hwnd);

    // Copies the desktop under the region (device pixels, screen coordinates, already padded by the caller for refraction
    // reach) into Texture, at its top-left (Size), the texture made anew only when the region grows past it. A region over two
    // monitors takes the one under its centre; the part beyond that output's edge repeats the edge. False when nothing could be
    // captured. Changed is true when the region moved or resized, or when a comparison answered since the last call found the
    // pixels differ from what the glass last showed (the caller then draws them, and they become the baseline); a change found
    // by a comparison shows on a later call than the one that filled it (Comparing), and while the desktop keeps changing (a
    // video) each fill shows at once, as its comparison only decides when that stops. Leaves the immediate context's pipeline
    // state cleared (where it draws, it sets what it needs and clears it after).
    public bool Update(RECT region, out bool changed)
    {
        changed = false;
        if (_disposed || region.IsEmpty) return false;
        try { return Capture(region, out changed); }
        finally
        {
            if (_deviceMade) { _deviceMade = false; DeviceChanged?.Invoke(); }
        }
    }

    private bool Capture(RECT region, out bool changed)
    {
        changed = false;
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
            bool shaders = EnsureShaders();
            if ((partial || linear) && !shaders) return false;

            bool resized = EnsureTexture(region.Width, region.Height, shaders);
            _size = (region.Width, region.Height);
            bool exact = _textureSize == _size;
            bool moved = resized || _filled != region;
            var local = region.Offset(-output.Bounds.Left, -output.Bounds.Top);
            bool touched = moved || Touches(local);
            _changes.Clear(); _changedAll = false;

            if (touched)
            {
                var context = _context!;
                // Moved, the caller draws it anyway: a comparison still out is of another region, and the baseline is taken
                // from what is drawn now when the region is next filled.
                if (moved) { _beforeValid = false; _comparing = false; _compareOwed = false; _unshown = false; }
                else if (!_beforeValid) { context.CopyResource(_before, _texture); _beforeValid = true; }
                if (!partial && !linear && exact)
                    context.CopySubresourceRegion(_texture, 0, 0, 0, 0, _desktop, 0, new Box(local.Left, local.Top, 0, local.Right, local.Bottom, 1));
                else Fill(local, output, linear);
                _filled = region;
            }
            changed = moved || Differs(touched && !moved);
            return true;
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            // A device lost makes a new one; anything else (out of memory, a call refused) starts duplicating again later.
            if (IsDeviceLoss(ex.HResult)) Recover();
            else AccessLost();
            return false;
        }
    }

    // Access to the output lost (a UAC prompt or the lock screen on the secure desktop, a mode change, a full-screen exclusive
    // game), or a call refused: duplication starts again after RetryMilliseconds, and its first presented frame is copied whole.
    private void AccessLost()
    {
        StopDuplicating();
        _retryAt = Environment.TickCount64 + RetryMilliseconds;
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
        _desktopValid = false; _masked = false; _waitedFirst = false;
        _filled = null;
        return true;
    }

    // The frame waiting, if any: its changed parts go into the copy of the output. False when access was lost.
    private bool Take()
    {
        var duplication = _duplication!;
        // The first frame of a new duplication is waited for once, briefly; while none was presented (a desktop holding still)
        // the wait is not repeated on every Update.
        uint wait = _desktopValid || _waitedFirst ? 0u : FirstFrameMilliseconds;
        _waitedFirst = true;
        var result = duplication.AcquireNextFrame(wait, out var info, out var resource);
        if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return true;
        if (result.Failure)
        {
            if (IsDeviceLoss(result.Code)) Lose();
            else AccessLost();
            return false;
        }
        try
        {
            // A frame nothing was presented for (LastPresentTime 0: only the pointer moved) changes nothing here, as the pointer
            // is never in the image. Before the copy of the output holds a frame, such a frame may hold no image at all (all
            // black, and the parts of it that never change would then stay black): the copy waits for a presented one.
            if (info.LastPresentTime == 0) return true;
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

    // The region texture (and its companions for the comparison) for a region of this size; true when they were made anew. With
    // the fill shader (bucket) in steps of Bucket pixels, never shrunk, and grown by at least a quarter at a time (a region
    // growing on every frame of an opening panel makes it anew a few times, the first time only); without the shader exactly
    // the region's size.
    private bool EnsureTexture(int width, int height, bool bucket)
    {
        if (_texture is not null && (bucket ? _textureSize.Width >= width && _textureSize.Height >= height : _textureSize == (width, height))) return false;
        if (bucket) { width = Grow(width, _textureSize.Width); height = Grow(height, _textureSize.Height); }
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

    private static int Step(int size) => (Math.Max(1, size) + Bucket - 1) / Bucket * Bucket;
    private static int Grow(int needed, int had) => needed <= had ? had : Step(Math.Max(needed, had + had / 4));

    // The region from the copy of the output through the fill shader, over the whole texture: the part beyond the output's edge
    // repeats the edge, the part of the texture beyond the region repeats the region's last column and row, and a float desktop
    // becomes 8-bit sRGB.
    private void Fill(RECT local, Output output, bool linear)
    {
        var context = _context!;
        var parameters = new DesktopShaders.FillParameters
        {
            OffsetX = local.Left, OffsetY = local.Top,
            LastX = Math.Min(output.Bounds.Width, local.Right) - 1, LastY = Math.Min(output.Bounds.Height, local.Bottom) - 1,
            White = _white, Linear = linear ? 1u : 0u
        };
        context.UpdateSubresource(in parameters, _fillParameters!);
        Draw(_textureTarget!, _textureSize.Width, _textureSize.Height, _fillShader!, _desktopView!, null);
    }

    // Whether the region is to be shown again (it then becomes the baseline). Each fill (filled: this call's) is compared with
    // the baseline by the occlusion query over the differ shader, sent now and read on a later call, never waited for; one fill
    // meanwhile is compared once that answer is in. A comparison that finds a change shows what is not shown yet, and while
    // the last answer found one each fill shows at once (the GPU runs the comparison before the baseline is replaced), so a
    // video keeps its pace; the first answer that finds nothing ends that. Where the shaders are missing, every fill shows;
    // a comparison unanswered for CompareMilliseconds counts as a change.
    private bool Differs(bool filled)
    {
        if (!EnsureShaders()) return filled;
        var context = _context!;
        bool show = false;
        if (_comparing)
        {
            var result = context.GetData(_differQuery!, _queryResult, sizeof(ulong), AsyncGetDataFlags.DoNotFlush);
            bool answered = result.Code == 0, late = Environment.TickCount64 - _comparingSince > CompareMilliseconds;
            if (answered || result.Failure || late)
            {
                _comparing = false;
                _hot = !answered || result.Failure || Marshal.ReadInt64(_queryResult) != 0;
                if (answered && result.Success) { ChangedShare = Math.Clamp(Marshal.ReadInt64(_queryResult) / (double)Math.Max(1, _size.Width * _size.Height), 0, 1); Compared++; }
                show = _hot && _unshown;
            }
        }
        if (filled)
        {
            _unshown = true; _compareOwed = true;
            show |= _hot;
        }
        if (_compareOwed && !_comparing)
        {
            _compareOwed = false;
            context.Begin(_differQuery!);
            // Only the region: the rest of the texture repeats its edge, so it differs only where the region does.
            Draw(_scratchTarget!, _size.Width, _size.Height, _differShader!, _textureView!, _beforeView);
            context.End(_differQuery!);
            // Sent to the GPU now, so the answer is there by the next Update.
            context.Flush();
            _comparing = true;
            _comparingSince = Environment.TickCount64;
        }
        if (show)
        {
            context.CopyResource(_before, _texture);
            _unshown = false;
        }
        return show;
    }

    private readonly IntPtr _queryResult = Marshal.AllocHGlobal(sizeof(ulong));
    private readonly ID3D11ShaderResourceView[] _oneView = new ID3D11ShaderResourceView[1], _twoViews = new ID3D11ShaderResourceView[2];

    // One full-target triangle: the state it needs is set here and cleared after, so the renderer's own state is not assumed.
    private void Draw(ID3D11RenderTargetView target, int width, int height, ID3D11PixelShader shader, ID3D11ShaderResourceView first, ID3D11ShaderResourceView? second)
    {
        var context = _context!;
        context.ClearState();
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(shader);
        // The views go through arrays kept for it, not made per draw; emptied after, so they hold nothing past the draw.
        if (second is null) { _oneView[0] = first; context.PSSetShaderResources(0, _oneView); }
        else { _twoViews[0] = first; _twoViews[1] = second; context.PSSetShaderResources(0, _twoViews); }
        _oneView[0] = null!; _twoViews[0] = null!; _twoViews[1] = null!;
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
                    // The constructor's device is the first; every later one replaces one (raised as Update returns).
                    if (Generation++ > 0) _deviceMade = true;
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
        _filled = null; _textureSize = default;
        _beforeValid = false; _comparing = false; _compareOwed = false; _unshown = false; _hot = false;
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
        if (_holds) { _holds = false; OpenCount--; CaptureExclusion.Release(); }
    }
}
