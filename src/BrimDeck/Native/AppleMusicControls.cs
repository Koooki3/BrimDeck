using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Shuffle, repeat and seeking for Apple Music, whose media session reports all three as unavailable. They go through the
// shuffle button, the repeat button and the progress bar of its main window, found by their automation ids, so they work
// only while that window exists and may stop working when Apple Music changes its interface. The bar counts whole
// seconds; writing it moves playback (measured: 157 to 167). UI Automation calls into another process and can take a
// while, so every call runs on the thread pool, one at a time.
internal sealed class AppleMusicControls
{
    public sealed record State(bool Shuffle, MediaRepeat Repeat, bool CanSeek);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppleMusicRepeat _repeatNames = new();
    private AutomationElement? _shuffle, _repeat, _scrubber;
    private DateTimeOffset _readAt;
    private int _reading;
    // The window's last reading; null while no Apple Music window with these controls was found.
    public State? Latest { get; private set; }
    // Raised on a pool thread when a reading differs from the previous one.
    public event Action? Changed;

    // Reads the window again, at most once a second unless now is set.
    public void Refresh(bool now = false)
    {
        var at = DateTimeOffset.UtcNow;
        if (!now && at - _readAt < TimeSpan.FromSeconds(1) || Interlocked.Exchange(ref _reading, 1) == 1) return;
        _readAt = at;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                var state = Read();
                if (state != Latest) { Latest = state; Changed?.Invoke(); }
            }
            finally { _gate.Release(); Volatile.Write(ref _reading, 0); }
        });
    }

    public Task<bool> ToggleShuffleAsync() => RunAsync(() => ((TogglePattern)_shuffle!.GetCurrentPattern(TogglePattern.Pattern)).Toggle());
    public Task<bool> CycleRepeatAsync() => RunAsync(() => ((InvokePattern)_repeat!.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
    public Task<bool> SeekAsync(TimeSpan position) => RunAsync(() =>
    {
        if (_scrubber is null) throw new InvalidOperationException();
        var range = (RangeValuePattern)_scrubber.GetCurrentPattern(RangeValuePattern.Pattern);
        range.SetValue(Math.Clamp(Math.Round(position.TotalSeconds), range.Current.Minimum, range.Current.Maximum));
    });

    private async Task<bool> RunAsync(Action action)
    {
        await _gate.WaitAsync();
        try
        {
            // A control that is gone (its window was rebuilt) is looked up again and the action tried once more.
            return await Task.Run(() =>
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try { if (!Find()) return false; action(); return true; }
                    catch (Exception ex) when (Lost(ex)) { Forget(); }
                }
                return false;
            });
        }
        finally
        {
            _gate.Release();
            // Apple Music redraws the button a moment after the click (about half a second in testing).
            _ = Task.Delay(400).ContinueWith(_ => Refresh(now: true), TaskScheduler.Default);
        }
    }
    private State? Read()
    {
        try
        {
            if (!Find()) return null;
            bool shuffle = ((TogglePattern)_shuffle!.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
            string glyph = TreeWalker.RawViewWalker.GetFirstChild(_repeat!)?.Current.Name ?? "";
            var repeat = _repeatNames.Observe(glyph, _repeat!.Current.Name) ?? MediaRepeat.None;
            return new(shuffle, repeat, _scrubber is not null);
        }
        catch (Exception ex) when (Lost(ex)) { Forget(); return null; }
    }
    // The controls of the window that has them; elements of a closed window throw on use and are looked up again. A progress
    // bar not found yet is looked for again: the window may have none while no song is loaded.
    private bool Find()
    {
        if (_shuffle is not null && _repeat is not null && _scrubber is not null) return true;
        foreach (var process in Process.GetProcessesByName("AppleMusic"))
            using (process)
                foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)))
                {
                    if (ById(window, "ShuffleButton") is not { } shuffle || ById(window, "RepeatButton") is not { } repeat) continue;
                    _shuffle = shuffle; _repeat = repeat; _scrubber = ById(window, "LCDScrubber");
                    return true;
                }
        return false;
    }
    private static AutomationElement? ById(AutomationElement window, string id) =>
        window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    private void Forget() => _shuffle = _repeat = _scrubber = null;
    private static bool Lost(Exception ex) => ex is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or COMException;
}
