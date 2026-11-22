using BrimDeck.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BrimDeck.Native;

// A volume a music gesture adjusts (MainWindow.MusicGestures.cs): the system's (SystemVolume) or the current player's (PlayerVolume).
internal interface IVolumeLevel : IDisposable
{
    // 0 to 1. A failed write leaves the level as it was.
    float Level { get; set; }
}

// The current player's own volume, as the system's volume mixer shows it: the volume of every audio session its processes have, on
// every playback device, matched to the player as the level bars match it (MediaAppTarget.Plays). The system volume is left alone.
// A player that has not opened an audio session yet, as one that has not played since it started, has none to adjust.
internal sealed class PlayerVolume : IVolumeLevel
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly List<MMDevice> _devices;
    private readonly List<AudioSessionControl> _sessions;
    private PlayerVolume(MMDeviceEnumerator enumerator, List<MMDevice> devices, List<AudioSessionControl> sessions) { _enumerator = enumerator; _devices = devices; _sessions = sessions; }

    // Null when the player has no audio session or the sessions cannot be read.
    public static PlayerVolume? Open(MediaAppOwner owner)
    {
        var enumerator = new MMDeviceEnumerator();
        List<MMDevice> devices = []; List<AudioSessionControl> sessions = [];
        try
        {
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int d = 0; d < endpoints.Count; d++)
            {
                var device = endpoints[d]; devices.Add(device);
                var listed = device.AudioSessionManager.Sessions;
                for (int i = 0; i < listed.Count; i++)
                {
                    var session = listed[i]; bool keep = false;
                    try { keep = !session.IsSystemSoundsSession && MediaAppTarget.Plays(owner, AppIdentity.Describe((int)session.GetProcessID)); }
                    catch { }
                    if (keep) sessions.Add(session); else session.Dispose();
                }
            }
            if (sessions.Count > 0) { var volume = new PlayerVolume(enumerator, devices, sessions); _ = volume.Level; return volume; }
        }
        catch { }
        Release(enumerator, devices, sessions);
        return null;
    }

    // The level of the session that is playing, or of the first one when none is.
    public float Level
    {
        get => (_sessions.FirstOrDefault(s => s.State == AudioSessionState.AudioSessionStateActive) ?? _sessions[0]).SimpleAudioVolume.Volume;
        set
        {
            float level = Math.Clamp(value, 0f, 1f);
            foreach (var session in _sessions) { try { session.SimpleAudioVolume.Volume = level; } catch { } }
        }
    }

    public void Dispose() => Release(_enumerator, _devices, _sessions);
    private static void Release(MMDeviceEnumerator enumerator, List<MMDevice> devices, List<AudioSessionControl> sessions)
    {
        foreach (var session in sessions) { try { session.Dispose(); } catch { } }
        sessions.Clear();
        foreach (var device in devices) { try { device.AudioSessionManager.Dispose(); device.Dispose(); } catch { } }
        devices.Clear();
        enumerator.Dispose();
    }
}
