using BrimDeck.Core;
using Windows.UI.ViewManagement;

namespace BrimDeck.Native;

// Windows' "Text size" (Settings > Accessibility > Text size) from UISettings.TextScaleFactor. TextScaleFactorChanged comes on a
// pool thread; the owner hands it to the UI thread, once per change: nothing polls.
internal sealed class WindowsTextSize : ITextSizeSource, IDisposable
{
    private UISettings? _settings;
    private Action? _changed;

    public double Read()
    {
        try { return (_settings ??= new UISettings()).TextScaleFactor; }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or PlatformNotSupportedException) { return 1; }
    }

    // Calls changed (on a pool thread) when the setting changes.
    public void Watch(Action changed)
    {
        _changed = changed;
        try { (_settings ??= new UISettings()).TextScaleFactorChanged += Changed; }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or PlatformNotSupportedException) { _changed = null; }
    }

    private void Changed(UISettings sender, object args) => _changed?.Invoke();

    public void Dispose()
    {
        _changed = null;
        if (_settings is { } settings) { try { settings.TextScaleFactorChanged -= Changed; } catch (System.Runtime.InteropServices.COMException) { } }
        _settings = null;
    }
}
