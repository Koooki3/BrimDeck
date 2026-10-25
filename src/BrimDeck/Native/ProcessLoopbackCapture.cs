using System.Runtime.InteropServices;
using BrimDeck.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace BrimDeck.Native;

// Captures what one player process renders (Windows 10 build 20348 and later) and feeds
// it to the level-bar analysis. Samples stay in memory and are discarded after analysis.
// Create, read and dispose on the same MTA thread.
internal sealed class ProcessLoopbackCapture : IDisposable
{
    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly WaveFormat Format = new(44100, 16, 2);
    private readonly AudioClient _client;
    private readonly AudioCaptureClient _capture;
    private float[] _buffer = new float[4096];
    private short[] _pcm = new short[4096];
    private double _analysed = double.NegativeInfinity;
    // Seconds between analyses and between drains, and the audio the stream's buffer holds; the buffer outlasts a drain that
    // comes late several times over.
    public const double AnalysisInterval = 1 / 30d, BufferDuration = .2;
    // The thread waits DrainWait between drains: Windows' default timer wakes it on 15.6 ms steps, so a wait of 33 ms came back
    // after 47 and the bars moved at 21 Hz; 30 ms comes back after 31.25 (or on time with a finer timer), and an analysis is due
    // a little before its interval for the same reason.
    public static readonly TimeSpan DrainWait = TimeSpan.FromMilliseconds(30);
    public AutoResetEvent Ready { get; } = new(false);
    public EqualizerSignal Signal { get; } = new(Format.SampleRate);
    public uint ProcessId { get; }

    private ProcessLoopbackCapture(uint processId, AudioClient client)
    {
        ProcessId = processId; _client = client;
        // The documented process loopback flags; the engine converts to the requested PCM format.
        // The buffer holds BufferDuration of audio, so packets drained every AnalysisInterval (AudioLevelMeter) are never lost.
        _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
            (long)(BufferDuration * 10_000_000), 0, Format, Guid.Empty);
        _client.SetEventHandle(Ready.SafeWaitHandle.DangerousGetHandle());
        _capture = _client.AudioCaptureClient;
        _client.Start();
    }

    public static ProcessLoopbackCapture? TryStart(uint processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return null;
        IntPtr parameters = IntPtr.Zero, variant = IntPtr.Zero;
        AudioClient? client = null;
        try
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS: activation type, then AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS.
            parameters = Marshal.AllocHGlobal(12);
            Marshal.WriteInt32(parameters, 0, 1);                 // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(parameters, 4, (int)processId);    // TargetProcessId
            Marshal.WriteInt32(parameters, 8, 0);                 // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            // PROPVARIANT holding a VT_BLOB that points at the parameters.
            variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
            Marshal.StructureToPtr(new BlobVariant { Type = 65, Size = 12, Data = parameters }, variant, false);
            var completion = new ActivationCompletion();
            ActivateAudioInterfaceAsync(ProcessLoopbackDevice, AudioClientId, variant, completion, out _);
            if (!completion.Done.Wait(TimeSpan.FromSeconds(2)) || completion.Result < 0 || completion.Interface is not IAudioClient audio) return null;
            client = new AudioClient(audio);
            return new ProcessLoopbackCapture(processId, client);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            client?.Dispose();
            return null;
        }
        finally
        {
            if (variant != IntPtr.Zero) Marshal.FreeHGlobal(variant);
            if (parameters != IntPtr.Zero) Marshal.FreeHGlobal(parameters);
        }
    }

    // Moves every waiting packet into the analysis. Returns false when the stream failed.
    public bool Drain(double now)
    {
        try
        {
            for (int frames = _capture.GetNextPacketSize(); frames > 0; frames = _capture.GetNextPacketSize())
            {
                IntPtr data = _capture.GetBuffer(out frames, out var flags);
                int count = frames * Format.Channels;
                if (_buffer.Length < count) { _buffer = new float[count]; _pcm = new short[count]; }
                if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(_buffer, 0, count);
                else
                {
                    // One block copy per packet instead of a marshalled read per sample.
                    Marshal.Copy(data, _pcm, 0, count);
                    for (int i = 0; i < count; i++) _buffer[i] = _pcm[i] / 32768f;
                }
                _capture.ReleaseBuffer(frames);
                Signal.Push(_buffer.AsSpan(0, count), Format.Channels, now);
            }
            // The bars move 30 times a second (MainWindow.TickMusic) and read the analyses in between, so 30 analyses serve them.
            if (now - _analysed >= AnalysisInterval - .005) { _analysed = now; Signal.Advance(now); }
            return true;
        }
        catch (COMException) { return false; }
    }

    public void Dispose()
    {
        try { _client.Stop(); } catch (COMException) { }
        _capture.Dispose(); _client.Dispose(); Ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlobVariant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public uint Size;
        public IntPtr Data;
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    // The activation completes on a system worker thread, so the handler must be agile.
    [ComVisible(true)]
    private sealed class ActivationCompletion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Done { get; } = new(false);
        public int Result { get; private set; } = -1;
        public object? Interface { get; private set; }
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try { operation.GetActivateResult(out int result, out object activated); Result = result; Interface = activated; }
            catch (COMException ex) { Result = ex.HResult; }
            finally { Done.Set(); }
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation activationOperation);
}
