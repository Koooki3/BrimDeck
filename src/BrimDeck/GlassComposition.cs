using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace BrimDeck;

// Shared UI-thread Composition target for the DWM glass. A failed interop activation leaves the opaque theme available.
internal static class GlassComposition
{
    private static Compositor? _compositor;
    private static IntPtr _queue;
    private static bool _failed;
    public static bool Ready => !_failed && (_compositor is not null || Start());
    public static Compositor? Compositor => Ready ? _compositor : null;
    public static void Fail() => _failed = true;

    private static bool Start()
    {
        try
        {
            var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), Type = 2, Apartment = 2 }; // current thread, STA
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is null && CreateDispatcherQueueController(options, out _queue) != 0) { _failed = true; return false; }
            _compositor = new Compositor();
            return true;
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException or TypeLoadException or InvalidOperationException)
        {
            _failed = true; return false;
        }
    }

    // ICompositorDesktopInterop.CreateDesktopWindowTarget, the first method after IUnknown's, called through its vtable.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDesktopWindowTarget(IntPtr self, IntPtr window, int topmost, out IntPtr target);
    public static DesktopWindowTarget Target(IntPtr window)
    {
        var unknown = ((IWinRTObject)_compositor!).NativeObject.ThisPtr;
        var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var interop));
        try
        {
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(interop), 3 * IntPtr.Size);
            var create = Marshal.GetDelegateForFunctionPointer<CreateDesktopWindowTarget>(method);
            Marshal.ThrowExceptionForHR(create(interop, window, 0, out var target));
            try { return DesktopWindowTarget.FromAbi(target); }
            finally { Marshal.Release(target); }
        }
        finally { Marshal.Release(interop); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int Size, Type, Apartment; }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
}
