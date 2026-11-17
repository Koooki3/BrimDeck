using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shell;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;

namespace BrimDeck;

// The windows that connect a clipboard sync service: each says what to do on the other device, in the words that device's
// own application uses, and takes or shows the code.
public sealed partial class SettingsWindow
{
    private sealed record SyncDialogShell(Window Window, StackPanel Body);

    // A code with the steps above it, a status under it and, when the next step is the user's, one button.
    private sealed record SyncCodeView(StackPanel Root, SyncCodeBox Code, TextBlock Status, Button Action);

    private SyncDialogShell SyncDialog(string title, double width = 420)
    {
        var dialog = new Window { Owner = this, Title = title, Width = width, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = SettingsFont(UiFontRole.Panel),
            Background = UI.Brush(Surface), Foreground = UI.Brush(TextPrimary) };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/SettingsResources.xaml", UriKind.Relative) });
        _palette.Apply(dialog.Resources); Native.SettingsChrome.Apply(dialog); dialog.ResizeMode = ResizeMode.NoResize;
        dialog.SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        var body = new StackPanel();
        dialog.Content = SyncDialogFrame(title, body, dialog.Close);
        return new(dialog, body);
    }

    // The window's content: the title with the close button at the top right (Esc closes too), and the body under them.
    private FrameworkElement SyncDialogFrame(string title, StackPanel body, Action close)
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = TextLine(title, 16, TextPrimary, FontWeights.SemiBold);
        heading.Margin = new Thickness(24, 18, 56, 0); heading.TextTrimming = TextTrimming.CharacterEllipsis;
        layout.Children.Add(heading);
        var button = ActionButton("", close); button.Content = CaptionIcon("close"); button.Style = (Style)FindResource("SettingsCloseButton");
        button.Width = 32; button.Height = 32; button.Padding = new Thickness(0); button.Margin = new Thickness(0, 10, 10, 0);
        button.HorizontalAlignment = HorizontalAlignment.Right; button.VerticalAlignment = VerticalAlignment.Top; button.IsCancel = true;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true); AutomationProperties.SetName(button, Loc.T("关闭", "Close"));
        layout.Children.Add(button);
        body.Margin = new Thickness(24, 16, 24, 24);
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var frame = new Border { BorderThickness = new Thickness(1), Child = layout };
        frame.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
        return frame;
    }

    // Numbered steps: what to do on the other device, then here.
    private FrameworkElement SyncSteps(params string[] steps)
    {
        var list = new Grid();
        list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        list.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < steps.Length; i++)
        {
            list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var number = TextLine(i + 1 + ".", 12.5, TextSecondary);
            var text = TextLine(steps[i], 12.5, TextPrimary); text.TextWrapping = TextWrapping.Wrap; text.LineHeight = 19;
            number.Margin = text.Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0); number.LineHeight = 19;
            Grid.SetRow(number, i); Grid.SetRow(text, i); Grid.SetColumn(text, 1);
            list.Children.Add(number); list.Children.Add(text);
        }
        return list;
    }

    private TextBlock SyncStatusLine()
    {
        // One line is kept whether or not there is anything to say, so the window keeps its height.
        var status = TextLine("", 12.5, TextSecondary);
        status.TextWrapping = TextWrapping.Wrap; status.TextAlignment = TextAlignment.Center; status.MinHeight = 18; status.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        return status;
    }
    private static void SetSyncStatus(TextBlock status, string text, bool error = false)
    {
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, error ? "SettingsError" : "SettingsTextSecondary");
    }

    private SyncCodeView SyncCode(bool readOnly, string label, params string[] steps)
    {
        var root = new StackPanel();
        root.Children.Add(SyncSteps(steps));
        var code = new SyncCodeBox { ReadOnly = readOnly, Label = label, Margin = new Thickness(0, 20, 0, 0) };
        root.Children.Add(code);
        var status = SyncStatusLine(); root.Children.Add(status);
        var action = ActionButton("", () => { }, true);
        action.HorizontalAlignment = HorizontalAlignment.Center; action.MinWidth = 96; action.Padding = new Thickness(16, 6, 16, 6);
        action.Margin = new Thickness(0, 12, 0, 0); action.Visibility = Visibility.Collapsed;
        root.Children.Add(action);
        return new(root, code, status, action);
    }

    // Two ways through the same window, as a segmented choice across its width.
    private FrameworkElement SyncModes(string first, string second, Action<bool> chosen)
    {
        var options = new UniformGrid { Columns = 2 };
        foreach (var (label, second_) in new[] { (first, false), (second, true) })
        {
            var option = new RadioButton { Content = label, Style = (Style)FindResource("SettingsSegment"), GroupName = "SyncMode", IsChecked = !second_ };
            option.Checked += (_, _) => chosen(second_);
            options.Children.Add(option);
        }
        var segment = SegmentBorder(options); segment.Margin = new Thickness(0, 0, 0, 18);
        return segment;
    }

    // The code typed into a box, once it is complete.
    private static async Task<string> NextCodeAsync(SyncCodeBox box, CancellationToken token)
    {
        var typed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Take(string code) => typed.TrySetResult(code);
        box.Completed += Take;
        try { using (token.Register(() => typed.TrySetCanceled(token))) return await typed.Task; }
        finally { box.Completed -= Take; }
    }
    private static async Task ClickAsync(Button button, CancellationToken token)
    {
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Take(object sender, RoutedEventArgs e) => clicked.TrySetResult();
        button.Click += Take;
        try { using (token.Register(() => clicked.TrySetCanceled(token))) await clicked.Task; }
        finally { button.Click -= Take; }
    }

    private SyncCodeView WeTypeEnterView() => SyncCode(false, Loc.T("匹配码", "Match code"),
        Loc.T("在手机或另一台电脑上打开微信输入法的设置，进入“跨设备”，点击“查看匹配码”。", "On your phone or another PC, open WeType's settings, go to its cross-device page (跨设备) and choose 查看匹配码 to see its match code."),
        Loc.T("在这里输入匹配码。那台设备如果弹出确认，选择允许。", "Enter the code here. If that device asks to confirm, allow it."));

    private SyncCodeView WeTypeShowView() => SyncCode(true, Loc.T("本机匹配码", "This device's match code"),
        Loc.T("在手机或另一台电脑上打开微信输入法的设置，进入“跨设备”，点击底部的“关联设备”。", "On your phone or another PC, open WeType's settings, go to its cross-device page (跨设备) and choose 关联设备 at the bottom."),
        Loc.T("在那台设备上输入下面的匹配码，再回到这里点击“允许”。", "Enter the code below on that device, then choose Allow here."));

    private TextBlock WeTypeFootnote()
    {
        var note = TextLine(Loc.T("那台设备要在“跨设备”中开启“跨设备复制粘贴”，它复制的内容才会同步过来。个人词库、常用语等其他开关与 BrimDeck 无关。",
            "That device syncs what it copies only with 跨设备复制粘贴 turned on in its cross-device page. Its other switches, such as dictionary and phrases, do not concern BrimDeck."), 11.5, TextTertiary);
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 16, 0, 0);
        return note;
    }

    private async Task ShowWeTypePairingAsync()
    {
        var service = _app.ClipboardSync;
        service.Accounts.WeType.DeviceName = service.Accounts.DeviceName;
        var shell = SyncDialog(Loc.T("关联微信输入法", "Link WeType"));
        var host = new Border();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        shell.Window.Closed += (_, _) => lifetime.Cancel();
        CancellationTokenSource? attempt = null;
        Task active = Task.CompletedTask;
        int turn = 0;

        async Task SwitchAsync(bool show)
        {
            int mine = ++turn;
            attempt?.Cancel();
            await active;
            if (mine != turn || lifetime.IsCancellationRequested) return;
            attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            active = show ? ShowAsync(attempt.Token) : EnterAsync(attempt.Token);
        }
        void Linked()
        {
            Change(s => s.WeTypeClipboardSync = true);
            shell.Window.Close();
        }

        async Task EnterAsync(CancellationToken token)
        {
            var view = WeTypeEnterView();
            host.Child = view.Root;
            view.Code.Loaded += (_, _) => view.Code.Focus();
            try
            {
                while (true)
                {
                    string code = await NextCodeAsync(view.Code, token);
                    view.Code.IsEnabled = false;
                    SetSyncStatus(view.Status, Loc.T("正在关联…那台设备如果弹出确认，选择允许。", "Linking… If that device asks to confirm, allow it."));
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(60));
                    try
                    {
                        await service.PairWeTypeAsync(code, deadline.Token);
                        Linked();
                        return;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                    { SetSyncStatus(view.Status, Loc.T("那台设备没有允许关联，请重新输入。", "That device did not allow the link. Enter the code again."), true); }
                    catch (HttpRequestException)
                    { SetSyncStatus(view.Status, Loc.T("无法连接微信输入法的服务器，请检查网络。", "Cannot reach WeType. Check your network."), true); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { SetSyncStatus(view.Status, Loc.T("匹配码不正确或已过期。", "The code is wrong or has expired."), true); }
                    view.Code.Text = ""; view.Code.IsEnabled = true; view.Code.Focus();
                }
            }
            catch (OperationCanceledException) { }
        }

        async Task ShowAsync(CancellationToken token)
        {
            var view = WeTypeShowView();
            host.Child = view.Root;
            using var client = new WeTypeSyncClient(service.Accounts.WeType, service.Save) { Trace = service.Log };
            string? code = null;
            try
            {
                while (true)
                {
                    try
                    {
                        view.Action.Visibility = Visibility.Collapsed; view.Code.Text = "";
                        SetSyncStatus(view.Status, Loc.T("正在获取匹配码…", "Getting a code…"));
                        using (var request = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            request.CancelAfter(TimeSpan.FromSeconds(30));
                            try { code = await client.CreatePairingCodeAsync(request.Token); }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(); }
                        }
                        view.Code.Text = code;
                        var until = DateTime.UtcNow.AddSeconds(60);
                        (string BindId, string Name)? asking = null;
                        while (asking is null && DateTime.UtcNow < until)
                        {
                            int left = Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds));
                            SetSyncStatus(view.Status, Loc.T($"{left} 秒内有效", $"Valid for {left} s"));
                            try { asking = await client.PollPairingCodeAsync(code, token); }
                            // The server no longer knows the code: it has expired.
                            catch (InvalidDataException) { break; }
                            if (asking is null) await Task.Delay(1500, token);
                        }
                        if (asking is { } peer)
                        {
                            SetSyncStatus(view.Status, Loc.T($"“{peer.Name}”请求关联", $"“{peer.Name}” asks to link"));
                            view.Action.Content = Loc.T("允许", "Allow"); view.Action.Visibility = Visibility.Visible;
                            using var answer = CancellationTokenSource.CreateLinkedTokenSource(token);
                            answer.CancelAfter(TimeSpan.FromSeconds(60));
                            try
                            {
                                await ClickAsync(view.Action, answer.Token);
                                view.Action.Visibility = Visibility.Collapsed;
                                SetSyncStatus(view.Status, Loc.T("正在关联…", "Linking…"));
                                await client.ConfirmPairingAsync(peer.BindId, peer.Name, answer.Token);
                                Linked();
                                return;
                            }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                        }
                        await ReleaseAsync();
                        view.Code.Text = "";
                        SetSyncStatus(view.Status, Loc.T("匹配码已过期。", "The code has expired."));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        await ReleaseAsync();
                        view.Code.Text = "";
                        SetSyncStatus(view.Status, ex is HttpRequestException or TimeoutException
                            ? Loc.T("无法连接微信输入法的服务器，请检查网络。", "Cannot reach WeType. Check your network.")
                            : Loc.T("关联没有完成。", "The link was not completed."), true);
                    }
                    view.Action.Content = Loc.T("重新获取", "Get a new code"); view.Action.Visibility = Visibility.Visible;
                    await ClickAsync(view.Action, token);
                }
            }
            catch (OperationCanceledException) { }
            finally { await ReleaseAsync(); }

            // A code no longer shown is given back, so the server stops waiting for it.
            async Task ReleaseAsync()
            {
                if (code is not { } issued || service.Connected(false)) return;
                code = null;
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await client.ReleasePairingCodeAsync(issued, cleanup.Token); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
        }

        shell.Body.Children.Add(SyncModes(Loc.T("输入匹配码", "Enter a code"), Loc.T("显示本机匹配码", "Show this device's code"), show => _ = SwitchAsync(show)));
        shell.Body.Children.Add(host);
        shell.Body.Children.Add(WeTypeFootnote());
        _ = SwitchAsync(false);
        shell.Window.ShowDialog();
        await active;
    }

    private StackPanel DoubaoLoginView(out TextBlock status, out Button retry)
    {
        var view = new StackPanel();
        view.Children.Add(SyncSteps(
            Loc.T("在打开的浏览器窗口中登录豆包账号。", "Sign in to your Doubao account in the browser window that opens."),
            Loc.T("在手机或另一台电脑的豆包输入法中登录同一账号，并开启“超级互传”。", "In Doubao IME on your phone or another PC, sign in to the same account and turn on its cross-device transfer.")));
        status = SyncStatusLine(); status.Margin = new Thickness(0, 20, 0, 0); view.Children.Add(status);
        retry = ActionButton(Loc.T("重新登录", "Sign in again"), () => { }, true);
        retry.HorizontalAlignment = HorizontalAlignment.Center; retry.MinWidth = 96; retry.Padding = new Thickness(16, 6, 16, 6);
        retry.Margin = new Thickness(0, 12, 0, 0); retry.Visibility = Visibility.Collapsed;
        view.Children.Add(retry);
        return view;
    }

    private async Task ShowDoubaoLoginAsync()
    {
        var shell = SyncDialog(Loc.T("登录豆包", "Sign in to Doubao"));
        shell.Body.Children.Add(DoubaoLoginView(out var status, out var retry));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        var token = stop.Token;
        Task active = Task.CompletedTask;
        shell.Window.Closed += (_, _) => stop.Cancel();

        async Task SignInAsync()
        {
            retry.Visibility = Visibility.Collapsed;
            SetSyncStatus(status, Loc.T("正在打开浏览器…", "Opening the browser…"));
            try
            {
                await Native.DoubaoBrowserLogin.SignInAsync(_app.ClipboardSync, message => { if (!token.IsCancellationRequested) SetSyncStatus(status, message); }, token);
                if (token.IsCancellationRequested) return;
                Change(s => s.DoubaoClipboardSync = true);
                shell.Window.Close();
            }
            catch (Exception ex) when (token.IsCancellationRequested && ex is not OutOfMemoryException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SetSyncStatus(status, ex is Native.DoubaoSignInWindowClosedException or InvalidDataException or TimeoutException or InvalidOperationException
                    ? ex.Message : Loc.T("登录没有完成，请检查网络。", "Sign-in was not completed. Check your network."), true);
                retry.Visibility = Visibility.Visible;
            }
        }
        void Start() { if (active.IsCompleted && !token.IsCancellationRequested) active = SignInAsync(); }
        shell.Window.ContentRendered += (_, _) => Start();
        retry.Click += (_, _) => Start();
        shell.Window.ShowDialog();
        await active;
    }

    // Both computers open this window: each shows its own code and takes the other's, so either one can do the typing.
    private sealed record LanPairingView(StackPanel Root, SyncCodeBox Own, TextBlock OwnNote, SyncCodeBox Entry, TextBlock Status, StackPanel Devices);

    private LanPairingView LanPairView()
    {
        var root = new StackPanel();
        root.Children.Add(SyncSteps(
            Loc.T("两台电脑连接同一网络，都在 BrimDeck 的“剪贴板同步”设置中点击局域网的“配对”。", "Connect both PCs to the same network and choose Pair under Local network in BrimDeck's clipboard sync settings on each."),
            Loc.T("在其中一台输入另一台显示的配对码。", "On either PC, enter the code the other one shows.")));
        TextBlock Caption(string text)
        {
            var caption = TextLine(text, 12, TextSecondary); caption.HorizontalAlignment = HorizontalAlignment.Center; caption.Margin = new Thickness(0, 22, 0, 8);
            return caption;
        }
        root.Children.Add(Caption(Loc.T("本机配对码", "This PC's code")));
        var own = new SyncCodeBox { ReadOnly = true, Label = Loc.T("本机配对码", "This PC's code") };
        root.Children.Add(own);
        var ownNote = TextLine("", 12, TextSecondary); ownNote.HorizontalAlignment = HorizontalAlignment.Center; ownNote.Margin = new Thickness(0, 8, 0, 0);
        ownNote.Visibility = Visibility.Collapsed; root.Children.Add(ownNote);
        root.Children.Add(Caption(Loc.T("输入另一台电脑的配对码", "Enter the other PC's code")));
        var entry = new SyncCodeBox { Label = Loc.T("另一台电脑的配对码", "The other PC's code") };
        root.Children.Add(entry);
        var status = SyncStatusLine(); root.Children.Add(status);
        var devices = new StackPanel { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(devices);
        return new(root, own, ownNote, entry, status, devices);
    }

    private async Task OpenLanPairingAsync()
    {
        var service = _app.ClipboardSync;
        string name = service.Accounts.DeviceName;
        var shell = SyncDialog(Loc.T("配对另一台电脑", "Pair another PC"));
        var view = LanPairView();
        shell.Body.Children.Add(view.Root);
        view.Entry.Loaded += (_, _) => view.Entry.Focus();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        shell.Window.Closed += (_, _) => lifetime.Cancel();
        CancellationTokenSource? hosting = null;
        Task hosted = Task.CompletedTask;
        // The code proves both computers were given the same six digits (a password-authenticated exchange bound to the
        // connection's certificate), so neither side is asked to compare anything further.
        static Task<bool> Approve(LanPairingConfirmation details, CancellationToken token) => Task.FromResult(true);

        bool Paired(LanClipboardPeer peer)
        {
            try { service.SetLanPeer(peer); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                SetSyncStatus(view.Status, Loc.T("无法保存配对信息，请重试。", "The pairing could not be saved. Try again."), true);
                return false;
            }
            Change(s => s.LanClipboardSync = true);
            shell.Window.Close();
            return true;
        }

        // Shows a code and waits for the other computer to enter it; a code not used in time is replaced by a new one.
        async Task HostAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var host = await Task.Run(() => new LanClipboardLink.PairingHost(name), token);
                    token.ThrowIfCancellationRequested();
                    view.Own.Text = host.Code; view.OwnNote.Visibility = Visibility.Collapsed;
                    var peer = await host.AcceptAsync(Approve, token);
                    if (!token.IsCancellationRequested && Paired(peer)) return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or AuthenticationException or CryptographicException
                    or UnauthorizedAccessException or NetworkInformationException)
                {
                    view.Own.Text = "";
                    if (ex is LanHostAddressUnavailableException or LanNetworkChangedException)
                    { view.OwnNote.Text = Loc.T("本机没有连接到局域网", "This PC is not on a local network"); view.OwnNote.Visibility = Visibility.Visible; }
                    try { await Task.Delay(2000, token); } catch (OperationCanceledException) { return; }
                }
            }
        }
        void StartHost()
        {
            hosting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            hosted = HostAsync(hosting.Token);
        }
        async Task<LanPairingDevice> ChooseAsync(IReadOnlyList<LanPairingDevice> found, CancellationToken token)
        {
            SetSyncStatus(view.Status, Loc.T("找到多台电脑，选择要配对的一台：", "Several PCs were found. Choose the one to pair:"));
            var chosen = new TaskCompletionSource<LanPairingDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Devices.Children.Clear(); view.Devices.Visibility = Visibility.Visible;
            foreach (var device in found)
            {
                var pick = ActionButton(device.Label, () => chosen.TrySetResult(device));
                pick.Style = (Style)FindResource("SettingsOutlineButton"); pick.HorizontalContentAlignment = HorizontalAlignment.Left; pick.Margin = new Thickness(0, 6, 0, 0);
                view.Devices.Children.Add(pick);
            }
            try { using (token.Register(() => chosen.TrySetCanceled(token))) return await chosen.Task; }
            finally { view.Devices.Visibility = Visibility.Collapsed; view.Devices.Children.Clear(); }
        }
        // Joins the computer showing the code just typed. This computer's own code is withdrawn meanwhile, so the two
        // cannot pair in both directions at once.
        async Task JoinAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    string code = await NextCodeAsync(view.Entry, token);
                    view.Entry.IsEnabled = false;
                    SetSyncStatus(view.Status, Loc.T("正在查找另一台电脑…", "Looking for the other PC…"));
                    hosting?.Cancel(); await hosted;
                    view.Own.Text = "";
                    try
                    {
                        var found = await LanClipboardLink.DiscoverPairingDevicesAsync(token);
                        token.ThrowIfCancellationRequested();
                        if (found.Count == 0)
                            SetSyncStatus(view.Status, Loc.T("没有找到另一台电脑。确认它也打开了这个窗口，并且两台电脑在同一网络。",
                                "The other PC was not found. Make sure it has this window open and both PCs are on the same network."), true);
                        else
                        {
                            var device = found.Count == 1 ? found[0] : await ChooseAsync(found, token);
                            SetSyncStatus(view.Status, Loc.T("正在配对…", "Pairing…"));
                            var peer = await LanClipboardLink.JoinPairingAsync(device, code, name, Approve, token);
                            if (Paired(peer)) return;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (LanPairingRateLimitedException)
                    { SetSyncStatus(view.Status, Loc.T("尝试次数过多，请 5 分钟后再试。", "Too many attempts. Try again in 5 minutes."), true); }
                    catch (AuthenticationException)
                    { SetSyncStatus(view.Status, Loc.T("配对码不正确。", "The code is wrong."), true); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or CryptographicException or UnauthorizedAccessException
                        or NetworkInformationException or OperationCanceledException)
                    { SetSyncStatus(view.Status, Loc.T("配对没有完成，请重新输入。", "Pairing was not completed. Enter the code again."), true); }
                    view.Entry.Text = ""; view.Entry.IsEnabled = true; view.Entry.Focus();
                    StartHost();
                }
            }
            catch (OperationCanceledException) { }
        }

        StartHost();
        var joining = JoinAsync(lifetime.Token);
        shell.Window.ShowDialog();
        await joining; await hosted;
    }
}
