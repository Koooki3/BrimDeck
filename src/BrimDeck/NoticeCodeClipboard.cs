using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// Puts a verification code on the clipboard when the user clicks "copy code" on a notice, and nowhere else: nothing here runs
// unless that click happened. The copy asks Windows to keep it out of the clipboard history and the cloud clipboard
// (CanIncludeInClipboardHistory and CanUploadToCloudClipboard 0, ExcludeClipboardContentFromMonitorProcessing), which BrimDeck's own
// clipboard page honours as well (ClipboardWatch); and the clipboard is emptied after CodeClipboardGuard.ClearAfter if it still
// holds that code. Used from the interface thread.
internal static class NoticeCodeClipboard
{
    private sealed class Board : ICodeClipboard
    {
        public bool TryRead(out string? text)
        {
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; return true; }
            catch (ExternalException) { text = null; return false; }
        }

        public bool TryClear()
        {
            try { Clipboard.Clear(); return true; }
            catch (ExternalException) { return false; }
        }
    }

    private static readonly CodeClipboardGuard Guard = new(() => DateTimeOffset.UtcNow, new Board());
    private static DispatcherTimer? _timer;

    // True when the code is on the clipboard. The clipboard may be held by another application for a moment.
    public static bool Copy(string code)
    {
        if (code.Length == 0) return false;
        var data = new DataObject();
        data.SetText(code, TextDataFormat.UnicodeText);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(1)));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                Guard.Arm(code);
                Schedule(CodeClipboardGuard.ClearAfter);
                return true;
            }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        return false;
    }

    private static void Schedule(TimeSpan wait)
    {
        _timer ??= CreateTimer();
        _timer.Stop();
        _timer.Interval = wait;
        _timer.Start();
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            switch (Guard.Tick())
            {
                case CodeClearResult.Retry: Schedule(TimeSpan.FromSeconds(2)); break;
                case CodeClearResult.Waiting when Guard.Due is { } due: Schedule(due - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50)); break;
            }
        };
        return timer;
    }
}
