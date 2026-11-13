using NAudio.CoreAudioApi;

namespace BrimDeck.Native;

// The default playback device's master volume, held open while a drag adjusts it (MainWindow.MusicGestures.cs). Setting the level
// directly changes it in any amount, unlike the volume keys' fixed steps, and shows no system flyout.
internal sealed class SystemVolume : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private SystemVolume(MMDeviceEnumerator enumerator, MMDevice device) { _enumerator = enumerator; _device = device; }

    // Null when there is no playback device or it cannot be read.
    public static SystemVolume? Open()
    {
        var enumerator = new MMDeviceEnumerator();
        try { var volume = new SystemVolume(enumerator, enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)); _ = volume.Level; return volume; }
        catch { enumerator.Dispose(); return null; }
    }

    // 0 to 1. A failed write leaves the level as it was.
    public float Level
    {
        get => _device.AudioEndpointVolume.MasterVolumeLevelScalar;
        set { try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f); } catch { } }
    }

    public void Dispose() { try { _device.Dispose(); } catch { } _enumerator.Dispose(); }
}
