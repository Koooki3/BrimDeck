using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;

namespace BrimDeck;

// The "剪贴板同步" page: this device's name, then one card for each way to sync. A card says where it stands with a coloured
// dot and a few words, has the one button that connects or disconnects it and, once connected, the switch that syncs.
public sealed partial class SettingsWindow
{
    internal const int ClipboardSyncSettingsPage = 18;
    private enum SyncKind { Lan, WeType, Doubao }
    // What a card shows that changes while the page is open.
    private sealed record SyncCard(Ellipse Dot, TextBlock Status, TextBlock Detail);
    private readonly Dictionary<SyncKind, SyncCard> _syncCards = [];
    private bool _syncWatching, _syncConnecting, _syncNamesReading;
    private string _syncShape = "";
    private System.Windows.Threading.DispatcherTimer? _syncNameTimer;

    private void ClipboardSyncSettings()
    {
        if (!_syncWatching)
        {
            _syncWatching = true; _app.ClipboardSync.Changed += RefreshSyncStatus;
            Closed += (_, _) => { _app.ClipboardSync.Changed -= RefreshSyncStatus; _syncNameTimer?.Stop(); };
        }
        _syncCards.Clear();
        Target.Children.Add(SyncDeviceName());
        AddSyncCard(SyncKind.Lan); AddSyncCard(SyncKind.WeType); AddSyncCard(SyncKind.Doubao);
        var note = TextLine(Loc.T("只同步开启之后新复制的内容。", "Only what is copied after syncing is turned on is synced.") + " ", 12, TextSecondary);
        note.Inlines.Add(InlineLink(Loc.T("使用说明 ↗", "How it works ↗"),
            Updates.GitHubUpdates.RepositoryUrl + Loc.T("/blob/main/docs/zh-CN/clipboard-sync.md", "/blob/main/docs/en-US/clipboard-sync.md")));
        note.Margin = new Thickness(0, 4, 0, 20); Target.Children.Add(note);
        _syncShape = SyncShape();
        RefreshSyncStatus();
        // The names the paired WeType devices show can change on those devices at any time.
        _syncNameTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(15), System.Windows.Threading.DispatcherPriority.Background, async (_, _) => await ReadSyncNamesAsync(), Dispatcher);
        _ = ReadSyncNamesAsync();
    }

    private Grid SyncDeviceName()
    {
        var service = _app.ClipboardSync;
        var box = new TextBox { Style = (Style)FindResource("SettingsField"), Height = 30, Padding = new Thickness(10, 0, 10, 0), FontSize = 12.5, Width = 240, MaxLength = 48,
            Text = service.Accounts.DeviceName, VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = service.CanRename, Opacity = service.CanRename ? 1 : 0.5 };
        AutomationProperties.SetName(box, Loc.T("本机名称", "This device's name"));
        void Commit()
        {
            string name = box.Text.Trim();
            if (!service.CanRename || name.Length == 0 || name.Any(char.IsControl)) { box.Text = service.Accounts.DeviceName; return; }
            if (name == service.Accounts.DeviceName) return;
            try { service.RenameDevice(name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException) { box.Text = service.Accounts.DeviceName; }
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); e.Handled = true; } };
        var row = Row(Loc.T("本机名称", "This device's name"), box, help: () => Loc.T(
            "其他设备上显示的名称。关联微信输入法或登录豆包之后不能修改，需要先解除关联或退出登录。",
            "The name other devices show. It cannot be changed while WeType is linked or Doubao is signed in; unlink or sign out first."));
        row.Margin = new Thickness(0, 0, 0, 10);
        return row;
    }

    private void AddSyncCard(SyncKind kind)
    {
        var service = _app.ClipboardSync;
        bool connected = SyncConnected(kind);
        var (title, connect, disconnect, description, help) = kind switch
        {
            SyncKind.Lan => (Loc.T("局域网", "Local network"), Loc.T("配对", "Pair"), Loc.T("解除配对", "Unpair"),
                Loc.T("同一网络中两台装有 BrimDeck 的电脑直接同步文字和图片，不经过互联网。", "Two PCs with BrimDeck on the same network sync text and images directly, without the internet."),
                Loc.T("新复制的文字和图片会发送到另一台电脑。两台电脑都要开启。", "Newly copied text and images are sent to the other PC. Turn this on on both PCs.")),
            SyncKind.WeType => (Loc.T("微信输入法", "WeType"), Loc.T("关联设备", "Link a device"), Loc.T("解除关联", "Unlink"),
                Loc.T("与手机或其他电脑上的微信输入法同步文字和图片。", "Syncs text and images with WeType on your phone or other PCs."),
                Loc.T("新复制的文字和图片会发送到已关联的设备，它们复制的内容会写入本机剪贴板。", "Newly copied text and images are sent to the linked devices, and what they copy is put on this PC's clipboard.")),
            _ => (Loc.T("豆包输入法", "Doubao IME"), Loc.T("登录", "Sign in"), Loc.T("退出登录", "Sign out"),
                Loc.T("与登录同一豆包账号的手机或电脑同步文字和图片。", "Syncs text and images with phones and PCs signed in to the same Doubao account."),
                Loc.T("新复制的文字和图片会发送到同一账号的设备，它们复制的内容会写入本机剪贴板。", "Newly copied text and images are sent to the account's devices, and what they copy is put on this PC's clipboard."))
        };

        var action = ActionButton(connected ? disconnect : connect, () => { });
        action.Style = (Style)FindResource("SettingsOutlineButton"); action.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(action, title + " · " + (connected ? disconnect : connect));
        action.Click += async (_, _) =>
        {
            if (!connected) { await ConnectSyncAsync(kind); return; }
            if (kind == SyncKind.WeType)
            {
                string name = service.Accounts.WeType.DisplayName.Length > 0 ? service.Accounts.WeType.DisplayName : service.Accounts.DeviceName;
                if (!Confirm(Loc.T("解除与微信输入法的关联？", "Unlink WeType?"),
                    Loc.T("其他设备的“我的设备”中也会删除本机。", "This PC is also removed from My devices on the other devices."),
                    Loc.T("解除关联", "Unlink"), danger: false)) return;
                action.IsEnabled = false;
                // When WeType could not be told, the other devices still list this PC; the user decides whether to unlink anyway.
                if (!await service.LeaveWeTypeGroupAsync() && !Confirm(Loc.T("无法从其他设备中删除本机", "Could not remove this PC from the other devices"),
                    Loc.T($"仍要解除关联吗？解除后，请在其他设备的微信输入法“跨设备 → 我的设备”中删除“{name}”，否则再次关联后会出现两个本机。",
                        $"Unlink anyway? Then delete “{name}” under My devices in WeType's cross-device settings on the other devices, or linking again will list this PC twice."),
                    Loc.T("解除关联", "Unlink"), danger: false))
                {
                    action.IsEnabled = true;
                    return;
                }
            }
            action.IsEnabled = false;
            try
            {
                switch (kind)
                {
                    case SyncKind.Lan: await service.UnpairLanAsync(); Change(s => s.LanClipboardSync = false); break;
                    case SyncKind.WeType: Change(s => s.WeTypeClipboardSync = false); service.Disconnect(false); break;
                    default: Change(s => s.DoubaoClipboardSync = false); await service.SignOutDoubaoAsync(); break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
            ShowPage(ClipboardSyncSettingsPage);
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        labels.Children.Add(TextLine(title, 14, TextPrimary, FontWeights.SemiBold));
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var status = TextLine("", 12, TextSecondary); status.TextTrimming = TextTrimming.CharacterEllipsis;
        var state = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        state.Children.Add(dot); state.Children.Add(status);
        labels.Children.Add(state);
        // Not connected: what this way of syncing is. Connected: what the other side shows, when there is something to say.
        var detail = TextLine(connected ? "" : description, 12, TextSecondary);
        detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 6, 0, 0);
        labels.Children.Add(detail);
        header.Children.Add(labels);
        Grid.SetColumn(action, 1); header.Children.Add(action);

        var content = new StackPanel();
        content.Children.Add(header);
        if (connected)
        {
            var divider = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 2) };
            divider.SetResourceReference(Border.BackgroundProperty, "SettingsBorder");
            content.Children.Add(divider);
            content.Children.Add(Toggle(Loc.T("自动同步", "Sync automatically"), SyncEnabled(kind), value =>
            {
                Change(s => { if (kind == SyncKind.Lan) s.LanClipboardSync = value; else if (kind == SyncKind.WeType) s.WeTypeClipboardSync = value; else s.DoubaoClipboardSync = value; });
                RefreshSyncStatus();
            }, help: help));
            if (kind == SyncKind.Doubao) content.Children.Add(DoubaoAdvanced().Root);
        }
        var card = new Border { Child = content, CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 14, 16, connected ? 4 : 14),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        Target.Children.Add(card);
        _syncCards[kind] = new(dot, status, detail);
    }

    private sealed record DoubaoAdvancedView(StackPanel Root, StackPanel List);

    // "高级", closed until asked for: the account's other devices, each with a button that takes it off the account's list.
    // A device leaves the list by signing out on it; this is for one that stayed, its sign-out not having reached the service
    // or its sign-in having been lost (a reinstall), which Doubao's own applications offer no way to remove.
    private DoubaoAdvancedView DoubaoAdvanced()
    {
        var root = new StackPanel();
        var divider = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 0) };
        divider.SetResourceReference(Border.BackgroundProperty, "SettingsBorder");
        root.Children.Add(divider);
        var arrow = Stroke("M 3.5,1.5 L 7,5 L 3.5,8.5", 10, 10, TextSecondary);
        arrow.RenderTransformOrigin = new Point(0.5, 0.5); arrow.Margin = new Thickness(6, 1, 0, 0);
        arrow.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new TextBlock { Text = Loc.T("高级", "Advanced"), FontSize = 12.5 });
        label.Children.Add(arrow);
        var open = ActionButton("", () => { }); open.Content = label;
        open.HorizontalAlignment = HorizontalAlignment.Left; open.Padding = new Thickness(6, 5, 6, 5); open.Margin = new Thickness(-6, 6, 0, 6);
        AutomationProperties.SetName(open, Loc.T("豆包输入法高级设置", "Doubao IME advanced settings"));
        root.Children.Add(open);
        var list = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(list);
        bool reading = false;
        open.Click += async (_, _) =>
        {
            bool show = list.Visibility != Visibility.Visible;
            list.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            arrow.RenderTransform = show ? new System.Windows.Media.RotateTransform(90) : null;
            if (!show || reading) return;
            reading = true;
            list.Children.Clear();
            list.Children.Add(TextLine(Loc.T("正在读取账号下的设备…", "Reading the account's devices…"), 12, TextSecondary));
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
                limit.CancelAfter(TimeSpan.FromSeconds(10));
                ShowDoubaoDevices(list, await _app.ClipboardSync.OtherDoubaoDevicesAsync(limit.Token));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                list.Children.Clear();
                list.Children.Add(TextLine(Loc.T("没有读取到设备，请检查网络后重新展开。", "The devices could not be read. Check the network and open this again."), 12, TextSecondary));
            }
            finally { reading = false; }
        };
        return new(root, list);
    }

    private void ShowDoubaoDevices(StackPanel list, IReadOnlyList<DoubaoDevice> devices)
    {
        list.Children.Clear();
        var note = TextLine(devices.Count == 0 ? Loc.T("账号下没有其他设备。", "The account has no other devices.")
            : Loc.T("账号下的其他设备。正常情况下，在那台设备上退出登录，它就会从这里消失；退出后仍留在这里的设备，可以在这里移除。",
                "The account's other devices. A device normally leaves this list when it signs out; one that stayed after signing out can be removed here."), 12, TextSecondary);
        note.TextWrapping = TextWrapping.Wrap;
        list.Children.Add(note);
        foreach (var device in devices)
        {
            string name = device.Name.Length > 0 ? device.Name : Loc.T("未命名的设备", "Unnamed device");
            var text = TextLine(name, 12.5, TextPrimary); text.TextTrimming = TextTrimming.CharacterEllipsis; text.VerticalAlignment = VerticalAlignment.Center; text.Margin = new Thickness(0, 0, 16, 0);
            text.ToolTip = name;
            var remove = ActionButton(Loc.T("移除", "Remove"), () => { });
            remove.Style = (Style)FindResource("SettingsOutlineButton"); remove.Padding = new Thickness(12, 4, 12, 4); remove.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(remove, Loc.T("移除 ", "Remove ") + name);
            var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(text); Grid.SetColumn(remove, 1); row.Children.Add(remove);
            list.Children.Add(row);
            remove.Click += async (_, _) =>
            {
                if (!Confirm(Loc.T($"从豆包账号中移除“{name}”？", $"Remove “{name}” from the Doubao account?"),
                    Loc.T("它不再出现在已关联设备中。那台设备如果还在使用，需要在它上面重新登录豆包。", "It no longer appears among the linked devices. If that device is still in use, Doubao needs to be signed in to on it again."),
                    Loc.T("移除", "Remove"), danger: false)) return;
                remove.IsEnabled = false;
                bool gone = false;
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(15));
                    gone = await _app.ClipboardSync.RemoveDoubaoDeviceAsync(device, limit.Token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
                if (gone) { list.Children.Remove(row); return; }
                remove.IsEnabled = true; remove.Content = Loc.T("重试", "Try again");
                text.Text = name + Loc.T(" · 没有移除成功", " · not removed");
            };
        }
    }

    private bool SyncConnected(SyncKind kind) => kind switch
    {
        SyncKind.Lan => _app.ClipboardSync.Accounts.Lan is not null,
        SyncKind.WeType => _app.ClipboardSync.Connected(false),
        _ => _app.ClipboardSync.Connected(true)
    };
    private bool SyncEnabled(SyncKind kind) => kind switch { SyncKind.Lan => S.LanClipboardSync, SyncKind.WeType => S.WeTypeClipboardSync, _ => S.DoubaoClipboardSync };

    private async Task ConnectSyncAsync(SyncKind kind)
    {
        if (_syncConnecting) return;
        _syncConnecting = true;
        try
        {
            if (kind == SyncKind.Lan) await OpenLanPairingAsync();
            else if (kind == SyncKind.WeType) await ShowWeTypePairingAsync();
            else await ShowDoubaoLoginAsync();
        }
        finally
        {
            _syncConnecting = false;
            if (IsVisible && _page == ClipboardSyncSettingsPage) ShowPage(_page);
        }
    }

    private async Task ReadSyncNamesAsync()
    {
        if (_page != ClipboardSyncSettingsPage || _syncNamesReading || !_app.ClipboardSync.Connected(false)) return;
        _syncNamesReading = true;
        try { await _app.ClipboardSync.RefreshWeTypeNamesAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { _syncNamesReading = false; }
        RefreshSyncStatus();
    }

    // Which cards are connected: when that changes under an open page, the page is built again.
    private string SyncShape() => string.Concat(Enum.GetValues<SyncKind>().Select(kind => SyncConnected(kind) ? '1' : '0'));

    private void RefreshSyncStatus()
    {
        if (_page != ClipboardSyncSettingsPage || _syncCards.Count == 0) return;
        if (!_syncConnecting && SyncShape() != _syncShape) { ShowPage(ClipboardSyncSettingsPage); return; }
        var service = _app.ClipboardSync;
        foreach (var (kind, card) in _syncCards)
        {
            bool connected = SyncConnected(kind), enabled = SyncEnabled(kind);
            var (text, level) = kind switch
            {
                SyncKind.Lan when !connected => (Loc.T("未配对", "Not paired"), SyncLevel.Idle),
                SyncKind.WeType when !connected => (Loc.T("未关联", "Not linked"), SyncLevel.Idle),
                SyncKind.Doubao when !connected => (Loc.T("未登录", "Not signed in"), SyncLevel.Idle),
                _ when !enabled => (Loc.T("已暂停", "Paused"), SyncLevel.Idle),
                SyncKind.Lan => (service.LanStatus, service.LanLevel),
                _ => (service.Status(kind == SyncKind.Doubao), service.Level(kind == SyncKind.Doubao))
            };
            string detail = "";
            if (kind == SyncKind.Lan && connected && service.Accounts.Lan?.DeviceName is { Length: > 0 } pc) text = pc + " · " + text;
            if (kind == SyncKind.WeType && connected)
            {
                var identity = service.Accounts.WeType;
                if (identity.PeerNames.Count > 0) detail = Loc.T("已关联：", "Linked: ") + string.Join(Loc.T("、", ", "), identity.PeerNames);
                // Another device renamed this one: the name it goes by there.
                if (identity.DisplayName.Length > 0 && identity.DisplayName != service.Accounts.DeviceName)
                    detail += (detail.Length > 0 ? "\n" : "") + Loc.T($"本机在微信输入法中显示为“{identity.DisplayName}”", $"WeType shows this PC as “{identity.DisplayName}”");
            }
            if (kind == SyncKind.Doubao && connected && service.DoubaoCapability is { CanSync: false })
            {
                if (!enabled) (text, level) = (Loc.T("此设备的互传开关未开启", "Sync is turned off for this device"), SyncLevel.Attention);
                detail = Loc.T($"在手机或另一台电脑的豆包输入法中打开“超级互传”，在已关联设备中开启“{service.Accounts.DeviceName}”。",
                    $"In Doubao IME on your phone or another PC, open its cross-device transfer and turn on “{service.Accounts.DeviceName}” among the linked devices.");
            }
            card.Status.Text = text;
            card.Dot.Fill = UI.Brush(level switch { SyncLevel.Active => "#30D158", SyncLevel.Attention => "#E5A23A", _ => TextTertiary });
            if (connected) { card.Detail.Text = detail; card.Detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }
    }
}
