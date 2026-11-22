using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;

namespace ReadmeMedia;

// The windows that send and receive files with WeType (WeTypeShareWindow, WeTypeReceiveWindow), each state drawn from invented
// files and devices, in the dark theme and a few in the light one; for checking, not for the README.
internal sealed partial class Renderer
{
    private async Task WeTypeFileScene(App app, string output, string folder)
    {
        var service = Activator.CreateInstance(Type.GetType("BrimDeck.ClipboardSyncService, BrimDeck")!, Program.Any, null, [app, folder], null)!;
        Program.SetProperty(app, "ClipboardSync", service);
        var accounts = Program.GetProperty(service, "Accounts")!;
        Program.SetProperty(accounts, "DeviceName", _english ? "BrimDeck STUDIO-PC" : "BrimDeck 书房电脑");
        var wetype = (WeTypeIdentity)Program.GetProperty(accounts, "WeType")!;
        wetype.Paired = true; wetype.DisplayName = _english ? "Study desktop" : "BrimDeck 书房电脑";

        string File_(string name, long size)
        {
            string path = Path.Combine(folder, name);
            using (var stream = new FileStream(path, FileMode.Create)) stream.SetLength(size);
            return path;
        }
        var single = new[] { File_(_english ? "Quarterly review 2026-11.pptx" : "2026 年 11 月季度汇报.pptx", 5_280_022) };
        var many = new[]
        {
            File_(_english ? "Site photos from the renovation of the second floor, north wing.jpg" : "二楼北侧装修现场照片（第三批，含吊顶与灯带细节）.jpg", 3_912_334),
            File_("budget.xlsx", 48_221), File_("contract-signed.pdf", 1_204_551), File_("notes.txt", 2_310), File_("floor-plan.dwg", 12_884_102),
        };
        var devices = new List<WeTypeFileDevice> { new(1, "OPPO Find X9 Pro", 1), new(2, "DESKTOP-OFFICE-WORKSTATION-2026-LONG-NAME", 6) };

        async Task Draw(string name, Window window)
        {
            var frame = (FrameworkElement)window.Content;
            window.Content = null;
            const double scale = 1.5, width = 360;
            var host = new System.Windows.Controls.Border { Child = frame, Resources = window.Resources, Width = width };
            System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
            TextOptions.SetTextFormattingMode(host, TextFormattingMode.Ideal);
            for (int i = 0; i < 2; i++)
            {
                host.Measure(new Size(width, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize));
                await Settle();
                // The Explorer's icons arrive on their own thread.
                for (int wait = 0; wait < 50 && Pending() > 0; wait++) await Task.Delay(40);
                await Settle();
            }
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)Math.Ceiling(host.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(host); bitmap.Freeze();
            Write(bitmap, Path.Combine(output, name + ".png"));
            Console.WriteLine(name + " " + host.ActualHeight.ToString("0"));
            host.Child = null;
            window.Content = frame;
        }
        void Call(object target, string name, params object?[] args) =>
            Program.Call(target, name, args.Select(a => a switch
            {
                List<WeTypeFileDevice> => typeof(IReadOnlyList<WeTypeFileDevice>),
                List<string> => typeof(IReadOnlyList<string>),
                _ => a!.GetType()
            }).ToArray(), args);
        var shareType = Type.GetType("BrimDeck.WeTypeShareWindow, BrimDeck")!;
        var receiveType = Type.GetType("BrimDeck.WeTypeReceiveWindow, BrimDeck")!;
        var shelfFiles = Type.GetType("BrimDeck.ShelfFiles, BrimDeck")!;
        int Pending() => (int)shelfFiles.GetProperty("Pending", Program.Any)!.GetValue(null)!;

        foreach (var theme in new[] { SettingsTheme.Dark, SettingsTheme.Light })
        {
            string t = theme == SettingsTheme.Dark ? "dark" : "light";
            bool all = theme == SettingsTheme.Dark;
            _settings.Theme = theme;
            Window Share(string[] paths) => (Window)Activator.CreateInstance(shareType, Program.Any, null, [theme, service, (IReadOnlyList<string>)paths, CancellationToken.None], null)!;

            var share = Share(single);
            Call(share, "ShowDevices", devices);
            await Draw($"wetype-send-devices-{t}", share);
            share = Share(single);
            Program.SetField(share, "_deviceName", "OPPO Find X9 Pro");
            Call(share, "ShowSending", new WeTypeSendProgress(Path.GetFileName(single[0]), 2_217_609, 5_280_022));
            await Draw($"wetype-send-progress-{t}", share);
            share = Share(single);
            Program.SetField(share, "_deviceName", "OPPO Find X9 Pro");
            Call(share, "ShowSent");
            await Draw($"wetype-send-sent-{t}", share);
            if (all)
            {
                share = Share(single); Call(share, "ShowLoading"); await Draw("wetype-send-loading", share);
                share = Share(many); Call(share, "ShowDevices", devices); await Draw("wetype-send-devices-many", share);
                share = Share(single); Call(share, "ShowDevices", new List<WeTypeFileDevice>()); await Draw("wetype-send-no-devices", share);
                share = Share(single); Call(share, "ShowLoadFailed"); await Draw("wetype-send-load-failed", share);
                share = Share(single); Program.Call(share, "ShowConnecting", [typeof(string)], "OPPO Find X9 Pro"); await Draw("wetype-send-connecting", share);
                share = Share(single); Call(share, "ShowQr", "https://example.com/wetype-transfer/0123456789abcdef0123456789abcdef"); await Draw("wetype-send-qr", share);
                share = Share(many);
                Call(share, "ShowSending", new WeTypeSendProgress(Path.GetFileName(many[0]), 3_100_000, 3_912_334));
                await Draw("wetype-send-progress-many", share);
                share = Share(single);
                Call(share, "ShowFailed", Loc.T("发送未完成", "Not sent"), Loc.T("连接中断了。可以重新发送。", "The connection was lost. You can send again."));
                await Draw("wetype-send-failed", share);
            }

            Window Receive(bool invited = true, string? destination = null)
            {
                var window = (Window)Activator.CreateInstance(receiveType, Program.Any, null, [app, wetype, service, CancellationToken.None], null)!;
                if (invited) Program.SetField(window, "_invite", new WeTypeFileInvite("code", 8, "OPPO Find X9 Pro"));
                if (destination != null) ((TaskCompletionSource<string>)Program.Field(window, "_destination")!).TrySetResult(destination);
                return window;
            }
            string downloads = _english ? @"C:\Users\Alex\Downloads" : @"D:\资料\从手机接收的文件\2026 年 11 月\项目汇报与现场照片";

            var receive = Receive();
            Call(receive, "ShowOffer");
            await Draw($"wetype-receive-offer-{t}", receive);
            receive = Receive(destination: Path.GetDirectoryName(many[0]));
            Call(receive, "ShowReceiving", new WeTypeSendProgress(Path.GetFileName(many[0]), 1_600_000, 3_912_334, 2, 5));
            await Draw($"wetype-receive-progress-{t}", receive);
            receive = Receive(destination: folder);
            Call(receive, "ShowSaved", single.ToList());
            await Draw($"wetype-receive-saved-{t}", receive);
            if (all)
            {
                receive = Receive(invited: false); await Draw("wetype-receive-waiting", receive);
                receive = Receive(destination: downloads); Call(receive, "ShowOffer"); await Draw("wetype-receive-connecting", receive);
                receive = Receive(destination: folder); Call(receive, "ShowSaved", many.ToList()); await Draw("wetype-receive-saved-many", receive);
                receive = Receive();
                Program.Call(receive, "ShowFailed", [typeof(string), typeof(string), typeof(bool)],
                    Loc.T("没有连上手机", "Could not connect to the phone"), Loc.T("可以重试，或在手机上重新发送。", "Retry, or send again from the phone."), true);
                await Draw("wetype-receive-failed", receive);
                receive = Receive();
                Program.Call(receive, "ShowFailed", [typeof(string), typeof(string), typeof(bool)],
                    Loc.T("这次传输已中断", "This transfer was interrupted"), Loc.T("已通知手机结束，请在手机上重新发送。", "The phone was told to stop. Send the files again from the phone."), false);
                await Draw("wetype-receive-stale", receive);
            }
        }

        // The file transfer page with WeType linked: the receive button beside search in the header.
        _settings.Theme = SettingsTheme.Dark;
        _settings.ClipboardWidth = 760; _settings.ClipboardHeight = 240;
        _settings.Animations = false;
        SelectPage(DeckPage.Clipboard);
        _deck.SetExpanded(true, true);
        await Settle();
        await Save("wetype-shelf-header", 3);
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(false, true);
        _settings.Animations = true;
        await Settle();
    }
}
