using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BrimDeck.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace BrimDeck.Native;

// Windows' own share sheet, the one Explorer's "共享" opens: the files go to the app the user picks there (an app registered to
// take shares, nearby sharing, mail). Only files are given: the callers leave folders and shortcuts out (MainWindow.Shareable).
//
// The sheet asks for its data (DataRequested) only when its window is the foreground window, and the panel never is
// (WS_EX_NOACTIVATE). It is placed over the window it is shown for and under any window that is always on top, the panel among
// them. So the sheet is shown for a window of its own: a dot where the sheet should stand (it opens centred on it; Place keeps it
// off the capsule), brought to the foreground at the click (the click lets this process do so), and gone when the share ends,
// when it gets the foreground back after the sheet closes, or at the next share.
internal static class ShareSheet
{
    private static HwndSource? _host;
    private static DataTransferManager? _manager;
    private static DispatcherTimer? _wait;
    private static IReadOnlyList<string> _paths = [];
    private static string _title = "";
    private static Action? _opened;
    private static bool _left;

    // capsule: where the collapsed panel stays on top, in device pixels; the sheet is put where it does not cover it.
    // opened: the sheet asked for the files, so it is opening. failed: it could not be opened, or did not ask within three seconds.
    public static void Show(IntPtr monitor, Rect? capsule, IReadOnlyList<string> paths, string title, Action opened, Action failed)
    {
        Close();
        if (paths.Count == 0 || WindowsHost.MonitorScreen(monitor) is not { } screen) { failed(); return; }
        _paths = paths; _title = title; _opened = opened; _left = false;
        var middle = Place(screen.Work, capsule, new Size(SheetSize.Width * screen.Scale, SheetSize.Height * screen.Scale));
        try
        {
            _host = new HwndSource(new HwndSourceParameters("BrimDeck")
            {
                WindowStyle = unchecked((int)(WsPopup | WsVisible)), Width = 1, Height = 1, PositionX = (int)middle.X, PositionY = (int)middle.Y
            });
            _host.AddHook(HostMessages);
            var handle = _host.Handle;
            SetForegroundWindow(handle);
            _manager = DataTransferManagerInterop.GetForWindow(handle);
            _manager.DataRequested += Requested;
            DataTransferManagerInterop.ShowShareUIForWindow(handle);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            Close(); failed(); return;
        }
        _wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _wait.Tick += (_, _) => { if (_opened is null) return; Close(); failed(); };
        _wait.Start();
    }

    // The files are read as the sheet's own storage items; one moved or deleted since it was chosen is left out.
    private static async void Requested(DataTransferManager sender, DataRequestedEventArgs e)
    {
        var opened = _opened; _opened = null;
        _wait?.Stop(); _wait = null;
        opened?.Invoke();
        var request = e.Request;
        var deferral = request.GetDeferral();
        try
        {
            var items = new List<IStorageItem>();
            foreach (var path in _paths)
            {
                try { items.Add(await StorageFile.GetFileFromPathAsync(path)); }
                catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException or ArgumentException or COMException) { }
            }
            if (items.Count == 0) { request.FailWithDisplayText(Loc.T("无法分享这些文件", "These files cannot be shared")); return; }
            request.Data.Properties.Title = _title;
            request.Data.SetStorageItems(items);
            request.Data.ShareCompleted += (_, _) => Later(Close);
            request.Data.ShareCanceled += (_, _) => Later(Close);
        }
        finally { deferral.Complete(); }
    }

    // The sheet takes the foreground from the dot; the dot getting it back means the sheet has closed.
    private static IntPtr HostMessages(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0006) // WM_ACTIVATE
        {
            if ((wParam.ToInt64() & 0xFFFF) == 0) _left = true;
            else if (_left) Later(Close);
        }
        return IntPtr.Zero;
    }

    private static void Later(Action act) => Dispatcher.CurrentDispatcher.BeginInvoke(act);

    // The sheet's size in DIPs, as measured on Windows 11 at 100% (528 × 526), with a little room.
    private static readonly Size SheetSize = new(540, 540);

    // The middle of the work area when a sheet there leaves the capsule clear; otherwise the middle of a free band above, below,
    // left or right of the capsule: of those the sheet fits in, the nearest to the middle (a capsule at the top puts the sheet
    // below it, not beside it); if it fits in none, the one that holds it best.
    internal static Point Place(Rect work, Rect? capsule, Size sheet)
    {
        var middle = new Point(work.Left + work.Width / 2, work.Top + work.Height / 2);
        if (capsule is not { IsEmpty: false } avoid) return middle;
        var centred = new Rect(middle.X - sheet.Width / 2, middle.Y - sheet.Height / 2, sheet.Width, sheet.Height);
        if (!centred.IntersectsWith(avoid)) return middle;
        Rect[] bands =
        [
            new(work.Left, work.Top, work.Width, Math.Max(0, avoid.Top - work.Top)),
            new(work.Left, avoid.Bottom, work.Width, Math.Max(0, work.Bottom - avoid.Bottom)),
            new(work.Left, work.Top, Math.Max(0, avoid.Left - work.Left), work.Height),
            new(avoid.Right, work.Top, Math.Max(0, work.Right - avoid.Right), work.Height)
        ];
        static Point Centre(Rect band) => new(band.Left + band.Width / 2, band.Top + band.Height / 2);
        double Fit(Rect band) => Math.Min(band.Width / sheet.Width, band.Height / sheet.Height);
        var fitting = bands.Where(band => Fit(band) >= 1).ToList();
        return fitting.Count > 0 ? fitting.Select(Centre).MinBy(point => (point - middle).Length) : Centre(bands.MaxBy(Fit));
    }

    private static void Close()
    {
        _wait?.Stop(); _wait = null;
        _opened = null;
        if (_manager is not null) { _manager.DataRequested -= Requested; _manager = null; }
        _host?.Dispose(); _host = null;
    }

    private const uint WsPopup = 0x80000000, WsVisible = 0x10000000;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
