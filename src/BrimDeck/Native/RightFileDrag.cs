using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace BrimDeck.Native;

// Shell targets remember which button began an OLE drag. Convert only this gesture
// to a left drag before entering OLE; consume its physical right release and release
// the synthetic left button instead. Ordinary right clicks never enter this path.
internal sealed class RightFileDrag : IDisposable
{
    private readonly HookProc _callback;
    private nint _hook;
    private volatile bool _leftDown, _released, _disposed;
    private DispatcherTimer? _cleanup;
    private Dispatcher? _inputDispatcher;
    private readonly ManualResetEventSlim _ready = new();
    private static readonly nuint Marker = 0x42524447;

    private RightFileDrag()
    {
        _callback = Hook;
        // Low-level hooks run on the installing thread. Never install this hook
        // on the WPF/OLE thread: SendInput and shell calls on that thread can wait
        // for input whose hook is waiting for the same thread to pump messages.
        var thread = new Thread(InputLoop) { IsBackground = true, Name = "BrimDeck right drag input" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _ready.Wait();
    }

    private void InputLoop()
    {
        try
        {
            _inputDispatcher = Dispatcher.CurrentDispatcher;
            _cleanup = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(30) };
            _cleanup.Tick += (_, _) => Unhook();
            _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
        }
        finally { _ready.Set(); }
        if (_hook == 0) return;
        try { Dispatcher.Run(); }
        finally
        {
            ReleaseLeft();
            if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
        }
    }

    public static RightFileDrag? Begin(UIElement source)
    {
        // Do not disturb a real left-button gesture or proceed without a hook.
        if (PasteInput.KeyDown(1)) return null;
        var drag = new RightFileDrag();
        if (drag._hook == 0) { drag.Dispose(); return null; }
        var frame = new DispatcherFrame();
        bool ready = false;
        MouseButtonEventHandler swallow = (_, e) =>
        {
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            {
                ready = true;
                frame.Continue = false;
            }
        };
        // A missing injected event must not leave a nested message loop running.
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(500) };
        timeout.Tick += (_, _) => frame.Continue = false;
        source.PreviewMouseDown += swallow;
        source.PreviewMouseUp += swallow;
        bool captured = source.CaptureMouse();
        try
        {
            if (!captured) { drag.Dispose(); return null; }
            drag._leftDown = true;
            if (Send([Input(0x10), Input(0x02)]) != 2) { drag.Dispose(); return null; }
            // Enter OLE as soon as the injected press arrives. A Background
            // callback can starve while the pointer continuously sends input.
            timeout.Start();
            Dispatcher.PushFrame(frame);
            if (!ready || drag._released) { drag.Dispose(); return null; }
            return drag;
        }
        finally
        {
            timeout.Stop();
            if (captured && source.IsMouseCaptured) source.ReleaseMouseCapture();
            source.PreviewMouseDown -= swallow;
            source.PreviewMouseUp -= swallow;
        }
    }

    private nint Hook(int code, nint message, nint data)
    {
        if (code >= 0 && message == 0x0205)
        {
            var mouse = Marshal.PtrToStructure<MouseHook>(data);
            if (mouse.ExtraInfo != Marker)
            {
                _released = true;
                // Never call SendInput inside a low-level mouse hook: Windows
                // is waiting for this callback before it can dispatch that input.
                _inputDispatcher!.BeginInvoke(DispatcherPriority.Send, () =>
                {
                    ReleaseLeft();
                    if (_disposed) Unhook();
                });
                return 1;
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private void ReleaseLeft()
    {
        if (!_leftDown) return;
        _leftDown = false;
        // Keep the hook pump free even while Windows delivers injected input.
        // Sending from the installing thread would reintroduce that dependency.
        ThreadPool.QueueUserWorkItem(_ => Send([Input(0x04)]));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook == 0) return;
        // Do not join the input thread from WPF: let it release injected input
        // and finish independently, even while OLE is unwinding on the UI thread.
        _inputDispatcher!.BeginInvoke(DispatcherPriority.Send, () =>
        {
            ReleaseLeft();
            // Esc may finish OLE before the physical right button is released.
            if (_released) Unhook();
            else _cleanup!.Start();
        });
    }

    private void Unhook()
    {
        _cleanup?.Stop();
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
        _inputDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
    }

    private static INPUT Input(uint flags) => new() { Mouse = new MOUSEINPUT { Flags = flags, ExtraInfo = Marker } };
    private static uint Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHook { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    private delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
