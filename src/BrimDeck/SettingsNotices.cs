using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// The "Dynamic notices" page: switching them on, the sources and their state, the look, the behavior, rules per application,
// and the recent notices kept on this PC. Source states change while the page is open (access granted, an agent started);
// their lines follow NoticeCenter.Changed without rebuilding the page.
public sealed partial class SettingsWindow
{
    private const int NoticesSettingsPage = 10;
    private readonly List<Action> _noticeStatusLines = [];

    private void NoticesPage()
    {
        var center = _app.Notices;
        _noticeStatusLines.Clear();
        Group(Toggle(Loc.T("启用灵动提示", "Turn on dynamic notices"), S.NoticesEnabled, value => { Change(s => s.NoticesEnabled = value); ShowPage(NoticesSettingsPage); },
            help: Loc.T("收到通知时，刘海、胶囊或指示条短暂展开，显示应用图标、标题和摘要；悬停查看详情。Windows 自己的横幅和通知中心保持不变。全部在本机处理，不使用云端服务。",
                "When a notice arrives, the notch, capsule or indicator briefly opens to show the application's icon, title and summary; hover for details. Windows' own banners and notification center stay as they are. Everything is processed on this PC, without any cloud service.")));
        if (!S.NoticesEnabled)
        {
            var note = TextLine(Loc.T("开启后可选择接收 Windows 应用通知、WSL 中的通知以及 brim-notify 命令发送的提示。", "Once on, you can receive Windows app notifications, notices from WSL and those sent with the brim-notify command."), 12, TextSecondary);
            note.TextWrapping = TextWrapping.Wrap; Target.Children.Add(note);
            return;
        }
        NoticeSources(center);
        NoticeAppearanceSection(center);
        NoticeBehavior(center);
        NoticeRules(center);
        NoticeHistorySection(center);
        // The status lines follow the sources while the page is shown.
        var watcher = new Border();
        Action refresh = () => { foreach (var line in _noticeStatusLines.ToList()) line(); };
        watcher.Loaded += (_, _) => center.Changed += refresh;
        watcher.Unloaded += (_, _) => center.Changed -= refresh;
        Target.Children.Add(watcher);
    }

    private TextBlock StatusLine(Func<(string Text, bool Problem)> read)
    {
        var line = TextLine("", 11.5, TextSecondary);
        line.TextWrapping = TextWrapping.Wrap; line.TextTrimming = TextTrimming.None; line.Margin = new Thickness(0, 2, 0, 0);
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);
        // An empty line takes no room, so a line whose state may come later (the local channel failing) can always be on the page.
        void Update() { var (text, problem) = read(); line.Text = text; line.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; line.Foreground = UI.Brush(problem ? _palette.Dark ? "#F0A6AA" : "#B12E3A" : TextSecondary); }
        Update();
        _noticeStatusLines.Add(Update);
        return line;
    }

    private void NoticeSources(NoticeCenter center)
    {
        SectionTitle(Loc.T("通知来源", "Sources"));
        // Windows: access, then events (with package identity) or the timed reading.
        var access = ActionButton(Loc.T("允许访问", "Allow access"), async () => { await center.RequestWindowsAccessAsync(); });
        var privacy = ActionButton(Loc.T("打开系统设置", "Open Windows settings"), () => UI.OpenUrl("ms-settings:privacy-notifications"));
        var windowsToggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = S.NoticeWindowsSource, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        AutomationProperties.SetName(windowsToggle, Loc.T("Windows 应用通知", "Windows app notifications"));
        windowsToggle.Checked += (_, _) => Change(s => s.NoticeWindowsSource = true);
        windowsToggle.Unchecked += (_, _) => Change(s => s.NoticeWindowsSource = false);
        var windowsControls = new StackPanel { Orientation = Orientation.Horizontal };
        windowsControls.Children.Add(access); windowsControls.Children.Add(privacy); windowsControls.Children.Add(windowsToggle);
        var windowsStatus = StatusLine(() =>
        {
            var state = center.WindowsState;
            access.Visibility = state == NoticeSourceState.NeedsAccess ? Visibility.Visible : Visibility.Collapsed;
            privacy.Visibility = state == NoticeSourceState.Denied ? Visibility.Visible : Visibility.Collapsed;
            return state switch
            {
                NoticeSourceState.Off => (Loc.T("已关闭", "Off"), false),
                NoticeSourceState.NeedsAccess => (Loc.T("需要授权：点击“允许访问”，Windows 会询问是否允许 BrimDeck 读取通知。", "Needs access: click \"Allow access\" and Windows asks whether BrimDeck may read notifications."), true),
                NoticeSourceState.Denied => (Loc.T("Windows 拒绝了访问。可在“设置 › 隐私和安全性 › 通知”中允许应用访问通知。", "Windows denies access. Allow apps to access notifications in Settings › Privacy & security › Notifications."), true),
                NoticeSourceState.Events => (Loc.T("正在接收（事件模式）", "Receiving (event mode)"), false),
                NoticeSourceState.Polling => (Loc.T("正在接收（轮询模式：每 2.5 秒检查新通知，每 15 秒完整读取一次）", "Receiving (polling mode: checks for new notices every 2.5 s and reads the whole list every 15 s)"), false),
                _ => (Loc.T("读取失败，稍后重试。", "Reading failed; trying again shortly."), true)
            };
        });
        Group(Row(Loc.T("Windows 应用通知", "Windows app notifications"), windowsControls, windowsStatus,
            () => Loc.T("读取 Windows 通知中心里当前用户的应用通知（来源、图标、时间和文字），不读取应用内部消息，也不会清除或修改通知中心。BrimDeck 作为普通桌面程序运行时，Windows 不提供变化事件，因此使用轮询；注册通知身份包后（见 docs/zh-CN/notices.md）自动改用事件模式。",
                "Reads the current user's app notifications in Windows' notification center (source, icon, time and text). It does not read messages inside apps, and never clears or changes the notification center. As an ordinary desktop program BrimDeck gets no change events from Windows, so it polls; once the notice identity package is registered (see docs/en-US/notices.md) it switches to event mode.")));

        // WSL: one switch per distribution; none on by default.
        var distributions = WslLocations.Distributions();
        var rows = new List<FrameworkElement>();
        foreach (var name in distributions)
        {
            bool on = S.NoticeWslDistros.Contains(name, StringComparer.OrdinalIgnoreCase);
            var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, "WSL · " + name);
            toggle.Checked += (_, _) => Change(s => { if (!s.NoticeWslDistros.Contains(name, StringComparer.OrdinalIgnoreCase)) s.NoticeWslDistros.Add(name); });
            toggle.Unchecked += (_, _) => Change(s => s.NoticeWslDistros.RemoveAll(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase)));
            var status = StatusLine(() => WslStatus(center, name));
            rows.Add(Row("WSL · " + name, toggle, status));
        }
        if (rows.Count == 0)
        {
            var none = TextLine(Loc.T("未检测到 WSL 发行版。", "No WSL distribution was found."), 12, TextSecondary);
            rows.Add(new Border { Child = none, MinHeight = 44, Padding = new Thickness(0, 13, 0, 13) });
        }
        SectionTitle(Loc.T("WSL（默认关闭）", "WSL (off by default)"));
        Note(Loc.T("开启某个发行版后，BrimDeck 会把通知代理复制到它的 ~/.local/share/brimdeck，并添加 ~/.local/bin/brim-notify。代理只在发行版已运行时启动，不会为接收通知而启动发行版。代理运行时发行版和 WSL 虚拟机不会自动停止，所以发行版中你的程序（终端、后台任务等）全部结束 5 分钟后，代理会自行退出，让 WSL 照常停止发行版并释放内存，发行版下次运行时再自动接入。没有通知服务时，代理作为该会话的标准桌面通知服务（notify-send 等可直接使用）；已有通知服务时改为旁路观察，若总线不允许监视则只支持 brim-notify。",
            "Once a distribution is on, BrimDeck copies its notice agent to ~/.local/share/brimdeck there and adds ~/.local/bin/brim-notify. The agent starts only while the distribution runs; a distribution is never started for notices. While the agent runs, WSL does not stop the distribution or its virtual machine, so the agent ends by itself 5 minutes after the last of your programs there (terminals, background jobs) ended; WSL then stops the distribution and frees its memory as usual, and the agent connects again the next time it runs. Without a notification service, the agent becomes the session's standard desktop notification service (notify-send and others work as usual); with one, it watches it, and where the bus does not allow watching only brim-notify works."));
        Group([.. rows]);

        // The command line, for Windows and for scripts.
        var helper = WslNoticeBridge.HelperPath;
        var copy = ActionButton(Loc.T("复制路径", "Copy path"), () => { try { Clipboard.SetText(helper); } catch (System.Runtime.InteropServices.ExternalException) { } });
        var example = TextLine("BrimDeck.Notify.exe \"" + Loc.T("构建完成", "Build finished") + "\" --body \"" + Loc.T("测试已通过", "Tests passed") + "\"  ·  BrimDeck.Notify.exe run -- dotnet test", 11.5, TextSecondary);
        example.TextWrapping = TextWrapping.Wrap; example.TextTrimming = TextTrimming.None; example.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        Group(Row(Loc.T("命令行 brim-notify", "Command line: brim-notify"), copy, example,
            () => Loc.T("Windows 上使用 BrimDeck 安装目录中的 BrimDeck.Notify.exe；WSL 中开启发行版后使用 brim-notify。run 会运行命令并在结束后提示结果；同一 --replace 键会更新同一条提示。",
                "On Windows use BrimDeck.Notify.exe in BrimDeck's folder; in WSL use brim-notify once the distribution is on. run runs a command and shows its result when it ends; the same --replace key updates the same notice.")));
        var pipe = StatusLine(() => center.PipeError is { } error ? (Loc.T("本地通道不可用：", "The local channel is unavailable: ") + error, true) : ("", false));
        Target.Children.Add(pipe);
        NoticeTimeSection(center);
    }

    // The time-kind applications this PC has (read only): their alarms, mail and reminders are recognized automatically.
    private void NoticeTimeSection(NoticeCenter center)
    {
        SectionTitle(Loc.T("时间类通知", "Time notices"));
        Note(Loc.T("闹钟、计时器、邮件和日程提醒会自动识别：倒计时实时显示剩余时间，闹钟和日程提醒优先显示。下列应用的提醒来自 Windows 通知，需要开启“Windows 应用通知”；BrimDeck 不读取邮箱或日历内容。",
            "Alarms, timers, mail and event reminders are recognized automatically: countdowns show the time left as it runs, and alarms and event reminders come first. The apps below remind through Windows notifications, so \"Windows app notifications\" needs to be on; BrimDeck does not read mailboxes or calendars."));
        var rows = new List<FrameworkElement>();
        var seen = center.Hub.KnownApps.Select(app => app.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var detected = TimeSourceDetection.InstalledAsync();
        foreach (var source in NoticeTimeSources.All)
        {
            bool received = source.AppIds.Any(id => seen.Contains(NoticeKeys.Windows(id)));
            var status = TextLine(Loc.T("正在检测…", "Checking…"), 12, TextSecondary);
            void Show(bool installed) => status.Text = installed
                ? received ? Loc.T("已安装 · 收到过通知", "Installed · notices received") : Loc.T("已安装", "Installed")
                : received ? Loc.T("收到过通知", "Notices received") : Loc.T("未安装", "Not installed");
            if (detected.IsCompleted) Show(detected.Result.GetValueOrDefault(source.Id));
            else _ = detected.ContinueWith(task => Dispatcher.BeginInvoke(() => Show(task.Result.GetValueOrDefault(source.Id))), TaskContinuationOptions.OnlyOnRanToCompletion);
            var kinds = TextLine(source.Role switch
            {
                NoticeTimeRole.Clock => Loc.T("闹钟、计时器结束（时钟内正在运行的计时 Windows 不对外提供）", "Alarms and finished timers (Windows does not share the Clock's running timers)"),
                NoticeTimeRole.Calendar => Loc.T("日程提醒", "Event reminders"),
                NoticeTimeRole.Reminders => Loc.T("任务提醒，到点时像闹钟一样弹出", "Task reminders, shown like an alarm when due"),
                NoticeTimeRole.Meetings => Loc.T("会议提醒和开始；其他消息照常", "Meeting reminders and starts; other messages as usual"),
                _ => Loc.T("新邮件、日程提醒", "New mail, event reminders")
            }, 11.5, TextSecondary);
            kinds.TextWrapping = TextWrapping.Wrap; kinds.TextTrimming = TextTrimming.None;
            rows.Add(Row(source.DisplayName, status, kinds));
        }
        var native = TextLine("brim-notify timer 25m · stopwatch · event --at 15:00", 11.5, TextSecondary);
        native.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        rows.Add(Row(Loc.T("BrimDeck 计时器", "BrimDeck timers"), TextLine(Loc.T("始终可用", "Always available"), 12, TextSecondary), native,
            () => Loc.T("用 brim-notify 设倒计时、秒表或日程提醒（Windows 和 WSL 都可用），由 BrimDeck 自己计时：倒计时到点和日程开始前会提醒，可取消、停止或 5 分钟后再提醒。",
                "Set a countdown, stopwatch or event reminder with brim-notify (on Windows and in WSL); BrimDeck keeps the time itself: it reminds when a countdown ends and before an event starts, and they can be cancelled, stopped or snoozed for 5 minutes.")));
        Group([.. rows]);
    }

    private static (string, bool) WslStatus(NoticeCenter center, string name)
    {
        if (!center.WslStates.TryGetValue(name, out var state)) return (Loc.T("未开启", "Off"), false);
        // The agent ended by itself (W2); said apart from the states below, which are the agent's own.
        if (state.State == WslAgentState.Idle)
            return (Loc.T("发行版中已没有你的程序在运行，代理已退出，让 WSL 可以停止它；发行版下次运行时自动接入。", "Nothing of yours runs in the distribution any more, so the agent ended to let WSL stop it; it connects again the next time the distribution runs."), false);
        var (text, warning) = state.State switch
        {
            WslAgentState.NotRunning => (Loc.T("发行版未运行；它运行后自动接入。", "The distribution is not running; it connects once it runs."), false),
            WslAgentState.NotPackaged => (state.Detail.Length > 0
                ? Loc.T($"此版本没有附带适用于该发行版（{state.Detail}）的 WSL 代理。", $"This build does not include the WSL agent for this distribution ({state.Detail}).")
                : Loc.T("此版本没有附带 WSL 代理。", "This build does not include the WSL agent."), true),
            WslAgentState.Unsupported => (Loc.T($"该发行版的处理器架构或 C 库（{state.Detail}）没有可用的 WSL 代理；brim-notify 在 Windows 上仍可用。",
                $"No WSL agent runs on this distribution's processor or C library ({state.Detail}); brim-notify still works on Windows."), true),
            WslAgentState.Installing => (Loc.T("正在安装代理…", "Installing the agent…"), false),
            WslAgentState.Starting => (Loc.T("正在启动…", "Starting…"), false),
            WslAgentState.Server => (Loc.T("正在接收：BrimDeck 是该会话的通知服务。", "Receiving: BrimDeck is this session's notification service."), false),
            WslAgentState.Monitor => (Loc.T("正在接收：旁路观察已有的通知服务（不提供其按钮）。", "Receiving: watching the existing notification service (its buttons are not offered)."), false),
            WslAgentState.MonitorDenied => (Loc.T("已有通知服务，且总线不允许监视；brim-notify 仍可用。", "Another notification service runs and the bus does not allow watching it; brim-notify still works."), true),
            WslAgentState.NoBus => (Loc.T("没有可用的会话总线；brim-notify 仍可用。", "No session bus is available; brim-notify still works."), true),
            WslAgentState.Failed => (Loc.T("连接失败，稍后重试", "Connection failed; trying again later") + (state.Detail.Length > 0 ? Loc.T("（", " (") + state.Detail + Loc.T("）", ")") : ""), true),
            _ => (Loc.T("未开启", "Off"), false)
        };
        if (!state.Conflict) return (text, warning);
        return (text + Loc.T(" ~/.local/bin/brim-notify 是你自己的文件，BrimDeck 未改动它；可改用 ~/.local/share/brimdeck/brimdeck-agent notify。",
            " ~/.local/bin/brim-notify is your own file and was left as it is; use ~/.local/share/brimdeck/brimdeck-agent notify instead."), true);
    }

    // An explanation set between a section's title and its rows.
    private void Note(string text)
    {
        var note = TextLine(text, 11.5, TextSecondary);
        note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None; note.Margin = new Thickness(0, -2, 0, 10);
        Target.Children.Add(note);
    }

    // Notices take the panel theme: the island, the panel and the notices are drawn in one material. The section says where the
    // theme is chosen and lets the user try it.
    private void NoticeAppearanceSection(NoticeCenter center)
    {
        SectionTitle(Loc.T("外观", "Look"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var theme = ActionButton(Loc.T("更改主题", "Change theme"), () => ShowPage(ThemePage));
        var test = ActionButton(Loc.T("发送测试通知", "Send a test notice"), center.SendTest); test.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(theme); buttons.Children.Add(test);
        var note = TextLine(Loc.T($"跟随面板主题“{PanelTheme.ShownTheme.DisplayName}”：通知与刘海、胶囊、指示条和展开面板使用同一套底色、边缘、辉光与文字颜色。",
            $"Follows the panel theme \"{PanelTheme.ShownTheme.DisplayName}\": notices share the surface, rim, glow and text colors of the notch, capsule, indicator and open panel."), 11.5, TextSecondary);
        note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None;
        Group(Row(Loc.T("通知外观", "Notice look"), buttons, note));
    }

    private void NoticeBehavior(NoticeCenter center)
    {
        SectionTitle(Loc.T("提醒方式", "Behavior"));
        static string PauseLabel(NoticeCenter center) => center.Paused ? Loc.T("恢复提醒", "Resume") : Loc.T("暂停 1 小时", "Pause for 1 hour");
        var pause = ActionButton(PauseLabel(center), () => { center.Pause(center.Paused ? null : TimeSpan.FromHours(1)); ShowPage(NoticesSettingsPage); });
        // A pause that runs out while the page is open turns the button back as the status line changes.
        _noticeStatusLines.Add(() => pause.Content = PauseLabel(center));
        var pauseStatus = StatusLine(() => center.Paused && S.NoticesPausedUntil is { } until
            ? (Loc.T($"已暂停至 {until.ToLocalTime():HH:mm}，期间的通知只进入最近通知。", $"Paused until {until.ToLocalTime():HH:mm}; notices meanwhile only go to the recent notices."), false)
            : center.Quiet ? (Loc.T("Windows 正处于专注、演示或锁定状态，提示暂时不显示。", "Windows is in focus, presentation or locked state; notices are held back for now."), false) : ("", false));
        Group(SliderRow(Loc.T("停留时间", "Shown for"), S.NoticeDuration, 2, 30, 1, Loc.T(" 秒", " s"), value => Change(s => s.NoticeDuration = (int)Math.Round(value))),
            SliderRow(Loc.T("高优先级停留", "High priority shown for"), S.NoticeHighDuration, 2, 60, 1, Loc.T(" 秒", " s"), value => Change(s => s.NoticeHighDuration = (int)Math.Round(value))),
            Toggle(Loc.T("悬停显示详情", "Hover shows details"), S.NoticeHoverDetails, value => Change(s => s.NoticeHoverDetails = value),
                help: Loc.T("鼠标在提示上停留约 0.25 秒即展开详情；关闭后需单击。提示到达时鼠标已在岛上，不会自动展开。", "Resting the pointer on a notice for about 0.25 s opens its details; when off, click instead. A notice arriving under a resting pointer does not open by itself.")),
            MusicChoice(Loc.T("显示在", "Show on"), S.NoticeScreen, [(NoticeScreen.Foreground, Loc.T("前台窗口所在屏幕", "Foreground window's screen")), (NoticeScreen.Main, Loc.T("主面板所在屏幕", "Main panel's screen"))], value => Change(s => s.NoticeScreen = value)),
            MusicChoice(Loc.T("到达时显示", "On arrival show"), S.NoticePrivacy, [(NoticePrivacy.Full, Loc.T("标题和摘要", "Title and summary")), (NoticePrivacy.TitleOnly, Loc.T("仅标题", "Title only")), (NoticePrivacy.AppOnly, Loc.T("仅应用", "App only"))], value => Change(s => s.NoticePrivacy = value)),
            Toggle(Loc.T("专注与演示时不打扰", "Quiet during focus and presentations"), S.NoticeRespectFocus, value => Change(s => s.NoticeRespectFocus = value),
                help: (center.FocusSupported ? Loc.T("Windows 专注会话或演示模式期间不弹出提示，结束后显示一次数量汇总。", "No notices pop up during a Windows focus session or presentation mode; afterwards one summary shows how many arrived.")
                    : Loc.T("此 Windows 版本不提供专注会话接口，仅识别演示模式。", "This version of Windows does not report focus sessions; only presentation mode is recognized."))
                    + Loc.T("Windows 单独的“请勿打扰”开关没有公开接口，可使用 BrimDeck 自己的暂停。锁屏和切换用户时始终不显示内容。", " Windows' separate \"Do not disturb\" switch has no public interface; use BrimDeck's own pause instead. Nothing is shown while locked or switched to another user.")),
            Row(Loc.T("暂停提醒", "Pause notices"), pause, pauseStatus),
            HotkeyRow(HotkeyKind.Notices));
    }

    private void NoticeRules(NoticeCenter center)
    {
        SectionTitle(Loc.T("应用", "Apps"));
        var apps = center.Hub.KnownApps.Select(app => (app.Key, app.Name, Origin: NoticeText.OriginName(app.Origin, null)))
            .Concat(S.NoticeApps.Where(rule => center.Hub.KnownApps.All(app => app.Key != rule.Key)).Select(rule => (rule.Key, rule.Name, Origin: "")))
            .Where(app => app.Key != NoticeCenter.TestAppKey).ToList();
        if (apps.Count == 0)
        {
            var none = TextLine(Loc.T("收到通知后，来源应用会列在这里，可分别设置静音、优先级和隐私。", "Apps appear here once their notices arrive; each can be muted, raised or kept private."), 12, TextSecondary);
            none.TextWrapping = TextWrapping.Wrap;
            Group(new Border { Child = none, Padding = new Thickness(0, 13, 0, 13) });
            return;
        }
        var rows = new List<FrameworkElement>();
        foreach (var (key, name, origin) in apps.Take(60))
        {
            var rule = S.NoticeRule(key);
            void Edit(Action<NoticeAppRule> edit) => Change(s =>
            {
                var target = s.NoticeRule(key);
                if (target is null) { target = new NoticeAppRule { Key = key, Name = name }; s.NoticeApps.Add(target); }
                target.Name = name; edit(target);
            });
            var mode = (rule?.Enabled ?? true) == false ? 0 : rule?.Muted == true ? 1 : rule?.Priority == NoticePriority.High ? 3 : 2;
            var choices = new UniformGrid { Columns = 4, Width = 280 };
            string[] labels = [Loc.T("关闭", "Off"), Loc.T("静音", "Muted"), Loc.T("正常", "Normal"), Loc.T("优先", "High")];
            for (int i = 0; i < labels.Length; i++)
            {
                int value = i;
                var option = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = labels[i], GroupName = "NoticeApp" + key, IsChecked = mode == i };
                AutomationProperties.SetName(option, name + " · " + labels[i]);
                option.Checked += (_, _) => Edit(r => { r.Enabled = value != 0; r.Muted = value == 1; r.Priority = value == 3 ? NoticePriority.High : NoticePriority.Normal; });
                choices.Children.Add(option);
            }
            var privacy = new ComboBox { Width = 112, Style = (Style)FindResource("SettingsCombo"), Margin = new Thickness(10, 0, 0, 0),
                ItemsSource = new[] { Loc.T("默认显示", "Default"), Loc.T("标题和摘要", "Full"), Loc.T("仅标题", "Title only"), Loc.T("仅应用", "App only") },
                SelectedIndex = rule?.Privacy is { } p ? (int)p + 1 : 0 };
            AutomationProperties.SetName(privacy, name + " · " + Loc.T("到达时显示", "On arrival show"));
            privacy.SelectionChanged += (_, _) => Edit(r => r.Privacy = privacy.SelectedIndex <= 0 ? null : (NoticePrivacy)(privacy.SelectedIndex - 1));
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(SegmentBorder(choices)); controls.Children.Add(privacy);
            var subtitle = TextLine(origin.Length > 0 ? origin : key, 11, TextTertiary);
            rows.Add(Row(name, controls, subtitle));
        }
        Group([.. rows]);
    }

    private void NoticeHistorySection(NoticeCenter center)
    {
        SectionTitle(Loc.T("最近通知与隐私", "Recent notices and privacy"));
        int[] hours = [1, 6, 12, 24, 72, 168];
        var retention = new ComboBox { Width = 120, Style = (Style)FindResource("SettingsCombo"), IsEnabled = S.NoticeHistory,
            ItemsSource = hours.Select(h => h < 24 ? Loc.T($"{h} 小时", $"{h} h") : Loc.T($"{h / 24} 天", Loc.Count(h / 24, "day", "days"))).ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(hours, S.NoticeHistoryHours) is var index && index >= 0 ? index : 3) };
        AutomationProperties.SetName(retention, Loc.T("保留时间", "Keep for"));
        retention.SelectionChanged += (_, _) => { if (retention.SelectedIndex >= 0) Change(s => s.NoticeHistoryHours = hours[retention.SelectedIndex]); };
        string folder = NoticeCenter.HistoryFolder(S);
        var autoClear = new ComboBox { Width = 120, Style = (Style)FindResource("SettingsCombo"),
            ItemsSource = new[] { Loc.T("不自动清空", "Never"), Loc.T("每次退出时", "On exit"), Loc.T("每天", "Daily"), Loc.T("每周", "Weekly") },
            SelectedIndex = (int)S.NoticeAutoClear };
        AutomationProperties.SetName(autoClear, Loc.T("定期清空", "Clear automatically"));
        // Choosing a schedule starts its count now, so switching it on never clears at once.
        autoClear.SelectionChanged += (_, _) => { if (autoClear.SelectedIndex >= 0) Change(s => { s.NoticeAutoClear = (NoticeAutoClear)autoClear.SelectedIndex; s.NoticeLastCleared = DateTimeOffset.UtcNow; }); };
        var where = TextLine(folder, 11, TextTertiary); where.TextTrimming = TextTrimming.CharacterEllipsis; where.ToolTip = folder;
        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal };
        folderButtons.Children.Add(ActionButton(Loc.T("更改…", "Change…"), () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("选择保存最近通知的文件夹", "Choose the folder for the recent notices"), InitialDirectory = Directory.Exists(folder) ? folder : "" };
            if (dialog.ShowDialog(this) == true) { Change(s => s.NoticeHistoryFolder = dialog.FolderName); ShowPage(NoticesSettingsPage); }
        }));
        if (S.NoticeHistoryFolder.Length > 0)
        {
            var reset = ActionButton(Loc.T("默认位置", "Default"), () => { Change(s => s.NoticeHistoryFolder = ""); ShowPage(NoticesSettingsPage); });
            reset.Margin = new Thickness(8, 0, 0, 0); folderButtons.Children.Add(reset);
        }
        var clear = ActionButton(Loc.T("清空最近通知", "Clear recent notices"), () => { center.Clear(); ShowPage(NoticesSettingsPage); });
        var rows = new List<FrameworkElement>
        {
            Toggle(Loc.T("在本机保存最近通知", "Keep recent notices on this PC"), S.NoticeHistory, value => { Change(s => s.NoticeHistory = value); ShowPage(NoticesSettingsPage); },
                help: Loc.T("保存时使用 Windows 为当前用户提供的加密，最多 500 条；关闭后只保留在内存中，重启即清空。不保存原始通知内容、附件或头像，诊断日志不记录标题和正文。",
                    "Saved with the encryption Windows provides for the current user, at most 500 notices; when off they are kept in memory only and are gone after a restart. Raw notification content, attachments and pictures are never saved, and diagnostic logs never contain titles or bodies.")),
            Row(Loc.T("保留时间", "Keep for"), retention),
            Row(Loc.T("定期清空", "Clear automatically"), autoClear, TextLine(Loc.T("在保留时间之外，按所选频率清空整个最近通知列表。", "Besides the retention, empties the whole recent list on the chosen schedule."), 11, TextTertiary)),
            Row(Loc.T("保存位置", "Location"), folderButtons, where),
            Row(Loc.T("最近通知", "Recent notices"), clear, TextLine(Loc.T($"{center.Hub.Notices.Count} 条 · 只清除 BrimDeck 中的记录，Windows 通知中心不受影响。", $"{center.Hub.Notices.Count} kept · clears BrimDeck's list only; Windows' notification center is not affected."), 11, TextTertiary))
        };
        Button Rebuild() => ActionButton(Loc.T("清空并重建", "Clear and start again"), () => { center.ResetHistory(); ShowPage(NoticesSettingsPage); });
        // The folder's own refusal (a link, another account's, open to others) says which; otherwise the file's state does.
        string Reason() => center.HistoryState == NoticeHistoryState.Refused && center.HistoryRefusal is { } refusal ? NoticeStorage.Describe(refusal, center.HistoryClosable) : NoticeHistory.Describe(center.HistoryState);
        if (center.HistoryState == NoticeHistoryState.Unreadable
            // A notices file that is a link, in a folder BrimDeck may use: clearing removes the link itself.
            || center.HistoryState == NoticeHistoryState.Refused && center.HistoryRefusal is null)
            rows.Add(Row(Loc.T("无法读取已保存的通知", "Saved notices unreadable"), Rebuild(), TextLine(Reason(), 11, _palette.Dark ? "#F0A6AA" : "#B12E3A")));
        // Not reachable for now, not readable by this account, or a folder BrimDeck will not use: said, and only a file this
        // account cannot read may be cleared from here (C-L15); an unreachable one may be fine.
        else if (center.HistoryState is NoticeHistoryState.Unavailable or NoticeHistoryState.Refused or NoticeHistoryState.Denied)
        {
            // Follows a retry while the page shows.
            var reason = StatusLine(() => center.HistoryState is NoticeHistoryState.Unavailable or NoticeHistoryState.Refused or NoticeHistoryState.Denied
                ? (Reason(), true) : (Loc.T("已读取。", "Read."), false));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            void Add(Button button) { if (buttons.Children.Count > 0) button.Margin = new Thickness(8, 0, 0, 0); buttons.Children.Add(button); }
            string title;
            if (center.HistoryState == NoticeHistoryState.Refused)
            {
                title = Loc.T("无法使用该文件夹", "Folder not used");
                // A folder of the user's own that others may write in can be closed to them, as BrimDeck's own folder is; only
                // one this can take effect on and that holds nothing but BrimDeck's files (M3).
                if (center.HistoryRefusal == NoticeStorage.FolderTrust.Shared && center.HistoryClosable)
                    Add(ActionButton(Loc.T("仅限本人访问", "Make it mine only"), async () => { await center.CloseHistoryFolderAsync(); ShowPage(NoticesSettingsPage); }));
                Add(ActionButton(Loc.T("默认位置", "Default"), () => { Change(s => s.NoticeHistoryFolder = ""); ShowPage(NoticesSettingsPage); }));
            }
            else
            {
                title = center.HistoryState == NoticeHistoryState.Denied ? Loc.T("没有权限读取已保存的通知", "Saved notices not readable") : Loc.T("暂时无法读取已保存的通知", "Saved notices not reachable");
                Add(ActionButton(Loc.T("重试", "Retry"), center.RetryHistory));
                if (center.HistoryState == NoticeHistoryState.Denied) Add(Rebuild());
            }
            rows.Add(Row(title, buttons, reason));
        }
        Group([.. rows]);
    }

    private void NoticeSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.NoticesWidth, 360, 1200, value => Resize(s => s.NoticesWidth = value, DeckPage.Notices)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.NoticesHeight, 140, 400, value => Resize(s => s.NoticesHeight = value, DeckPage.Notices)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
    }
}
