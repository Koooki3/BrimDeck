using System.Collections;
using System.Reflection;
using BrimDeck.Core;
using BrimDeck.Native;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

// What the audio level meter does with a player's session whose device has just gone: the session answers with an error, and
// nothing of it may leave the meter's thread, where it would end the process. The render devices are only listed; no application
// matches the target, so nothing is captured.
internal static class AudioMeterChecks
{
    private const int DeviceInvalidated = unchecked((int)0x88890004); // AUDCLNT_E_DEVICE_INVALIDATED
    public static void Run(Action<string, bool> check)
    {
        var gone = new GoneSession(); int left = -1; Exception? escaped = null;
        // The meter reads the audio on an MTA thread.
        var thread = new Thread(() =>
        {
            using var meter = new AudioLevelMeter();
            var sessions = (IList)Member<FieldInfo>("_sessions").GetValue(meter)!;
            sessions.Add(new AudioSessionControl(gone));
            try { Member<MethodInfo>("Enumerate").Invoke(meter, [new MediaAppOwner(null, "\u0001", "\u0001")]); }
            catch (TargetInvocationException ex) { escaped = ex.InnerException; }
            left = sessions.Count;
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        check("audio meter: listing the sessions ends", thread.Join(TimeSpan.FromSeconds(20)));
        if (gone.Asked == 0) { Console.WriteLine("SKIP: audio meter: the render devices could not be listed here"); return; }
        check("audio meter: a session whose device has gone does not throw out of the meter", escaped is null);
        check("audio meter: nothing of it is kept, so the meter looks again", left == 0);
    }
    private static T Member<T>(string name) where T : MemberInfo =>
        (T)typeof(AudioLevelMeter).GetMember(name, BindingFlags.NonPublic | BindingFlags.Instance).Single();

    private sealed class GoneSession : IAudioSessionControl
    {
        public int Asked;
        public int GetState(out AudioSessionState state) { Asked++; state = AudioSessionState.AudioSessionStateInactive; return DeviceInvalidated; }
        public int GetDisplayName(out string displayName) { displayName = ""; return 0; }
        public int SetDisplayName(string displayName, Guid eventContext) => 0;
        public int GetIconPath(out string iconPath) { iconPath = ""; return 0; }
        public int SetIconPath(string iconPath, Guid eventContext) => 0;
        public int GetGroupingParam(out Guid groupingId) { groupingId = default; return 0; }
        public int SetGroupingParam(Guid groupingId, Guid eventContext) => 0;
        public int RegisterAudioSessionNotification(IAudioSessionEvents client) => 0;
        public int UnregisterAudioSessionNotification(IAudioSessionEvents client) => 0;
    }
}
