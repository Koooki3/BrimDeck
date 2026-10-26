using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _settingsEvent;
    private RegisteredWaitHandle? _listener;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public DeckSettings Settings { get; private set; } = new();
    public SettingsStore Store { get; private set; } = null!;
    public UsageService Usage { get; private set; } = null!;
    public ProviderSecrets Secrets { get; private set; } = null!;
    public Pricing Prices { get; private set; } = null!;
    public AppUpdates Updates { get; internal set; } = null!;
    // Dynamic notices (NoticeCenter): created before the panels, which take their turns on the island through it.
    internal NoticeCenter Notices { get; private set; } = null!;
    // The main panel. It owns the tray icon, reads the usage data and takes the settings preview; every other chosen
    // monitor gets a panel that shows the same data.
    public MainWindow Deck { get; private set; } = null!;
    private readonly Dictionary<string, MainWindow> _otherDecks = [];
    public IEnumerable<MainWindow> Decks => _otherDecks.Values.Prepend(Deck);
    public IReadOnlyList<DisplayInfo> Displays { get; private set; } = [];
    private bool _arrangePending;
    // Every panel shows the one set of players, and their lyrics are looked up once.
    internal MediaSessions? Media { get; private set; }
    internal event Action? MediaChanged;
    internal LyricsService Lyrics { get; } = new();
    private readonly HashSet<MainWindow> _presentingMedia = [];
    // The system monitor page's readings, shared by every panel; created when a panel first shows the page.
    internal SystemSampler? Monitor { get; private set; }
    internal event Action<MonitorSnapshot>? MonitorUpdated;
    private readonly HashSet<MainWindow> _presentingMonitor = [], _stripMonitor = [];
    // The wireless devices' batteries, shared by every panel; created when first wanted (UpdateDeviceDemand).
    internal DeviceBatteries? Devices { get; private set; }
    internal event Action? DevicesChanged;
    private readonly HashSet<MainWindow> _devicePages = [], _deviceStrips = [];
    private readonly LowBatteryWatch _lowBattery = new();
    public CancellationTokenSource Lifetime { get; } = new();
    public bool Exiting { get; private set; }

    // Tooltip delays are not inherited, so the defaults are replaced for every element before any window exists.
    // The system hover time on some machines is a full second; every tooltip here opens after the same short pause.
    static App()
    {
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(120));
        System.Windows.Controls.ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(400));
        System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(20000));
    }

    // The element whose tooltip opens next (OnStartup).
    private WeakReference<DependencyObject>? _toolTipOwner;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // WPF raises Startup from the first dispatcher pass after the App is made, run or not: an App made for the offscreen
        // page sheets (UseOffscreen) starts nothing (no instance check, no signal to a running BrimDeck, no window), nor does an
        // App in a test harness (AppInstance.ForTest), whose instance names are its own in any case.
        if (_offscreen || AppInstance.IsTest) return;
        // Menus and tooltips take the text rendering of what they open from as they open (PanelTheme.Text). A tooltip opened by
        // the service has no placement target, so its owner is noted as the service raises ToolTipOpening on it.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent,
            new System.Windows.Controls.ToolTipEventHandler((_, args) => _toolTipOwner = new WeakReference<DependencyObject>((DependencyObject)args.OriginalSource)), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler((sender, _) =>
        {
            var tip = (System.Windows.Controls.ToolTip)sender;
            PanelTheme.Text(tip, tip.PlacementTarget ?? (_toolTipOwner?.TryGetTarget(out var owner) == true ? owner : null));
        }));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => PanelTheme.Text((System.Windows.Controls.ContextMenu)sender, ((System.Windows.Controls.ContextMenu)sender).PlacementTarget)));
        Resources[SystemParameters.VerticalScrollBarWidthKey] = 7.0;
        var eventName = AppInstance.Settings;
        _mutex = new Mutex(true, AppInstance.Mutex, out var first);
        if (!first)
        {
            try { using var existing = EventWaitHandle.OpenExisting(eventName); existing.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        SetCurrentProcessExplicitAppUserModelID("BrimDeck.Desktop");
        RecordCrashes();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");
        Store = new SettingsStore(folder); Settings = Store.Load();
        UseLanguage();
        PanelTheme.Use(Settings);
        ScheduleDaylight();
        // "跟随系统" follows the Windows app theme while BrimDeck runs.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        // The language a first start takes from Windows is kept, even if the display language changes later.
        if (Store.IsFirstStart) FlushSettings();
        try
        {
            Settings.LaunchAtStartup = StartupRegistration.IsEnabled();
            // When moving from the old EXE to an installed copy, keep an enabled startup entry at the new stable path.
            if (Settings.LaunchAtStartup) StartupRegistration.SetEnabled(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        Prices = new Pricing(folder);
        Updates = new AppUpdates(BrimDeck.Updates.GitHubUpdates.Create(folder), folder);
        Secrets = new ProviderSecrets(folder);
        Usage = new UsageService(new DesktopSources(), secrets: Secrets, quotaCache: Path.Combine(folder, ClaudeQuotaCache.FileName),
            kimiQuotaCache: Path.Combine(folder, KimiQuotaCache.FileName));
        InteractionFx.Install(this);
        Notices = new NoticeCenter(this);
        Displays = Native.Displays.Current();
        // Automatic liquid glass shows first the Windows app theme's look, and the look what lies behind the island asks for as
        // soon as it is measured, off the UI thread (StartGlance). A fixed liquid glass theme has its capture made off it meanwhile.
        if (LiquidTheme.IsAuto(Settings.PanelTheme))
        {
            if (StartGlance() != PanelTheme.Current.IsDark) PanelTheme.Use(Settings, systemChanged: true);
            MatchGlassGradient();
        }
        else if (LiquidTheme.Renders && LiquidPossible && GlanceRegion() is { } region) LiquidGlass.SourceStart.Prepare(region, measure: false);
        MotionEnvironment.Changed += GlassEnvironmentChanged;
        GlassBackdrop.LiquidFailed += GlassEnvironmentChanged;
        Deck = new MainWindow(this); MainWindow = Deck;
        AssignDisplays();
        Deck.Show();
        Notices.ApplySettings();
        InitializeHotkeys();
        StartBackgroundUpdates();
        DisplayPower.Install(Dispatcher);
        DisplayPower.Changed += UpdateDeviceDemand;
        _save.Tick += (_, _) => FlushSettings();
        _tray = new TrayIcon(Deck, OpenSettings, ExitApplication, ToggleHidden, () => UserHidden)
        {
            NoticeEntry = () => !Settings.NoticesEnabled ? null
                : Notices.Paused ? (Loc.T("恢复通知提醒", "Resume notices"), () => Notices.Pause(null))
                : (Loc.T("暂停通知提醒 1 小时", "Pause notices for 1 hour"), () => Notices.Pause(TimeSpan.FromHours(1)))
        };
        _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _listener = ThreadPool.RegisterWaitForSingleObject(_settingsEvent, (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, Timeout.Infinite, false);
        if (e.Args.Contains("--settings")) OpenSettings();
        // The start reads and builds a lot; what it leaves behind is returned once the panel has been left alone (App.Memory).
        RequestTrim();
    }
    // A resident app must not vanish without a trace. Unhandled errors are appended to crash.log in the data folder;
    // errors on the UI thread are logged and the app keeps running, and unobserved task errors are only logged.
    private void RecordCrashes()
    {
        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "crash.log");
        void Write(object error)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                // The log keeps only recent entries.
                if (File.Exists(log) && new FileInfo(log).Length > 1_048_576) File.Delete(log);
                File.AppendAllText(log, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {ApplicationVersion.Current}\n{error}\n\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        DispatcherUnhandledException += (_, args) => { Write(args.Exception); args.Handled = !Exiting; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Write(args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { Write(args.Exception); args.SetObserved(); };
    }
    // For the offscreen page sheets only (tests/BrimDeck.FxPreview --liquid-pages, MainWindow.Offscreen.cs): an App that is never
    // run (its OnStartup returns at once, so nothing is read, saved, listened to or shown) takes these settings, a notice center whose
    // sources are never started, and its main panel, laid out for the given monitor and never shown.
    private bool _offscreen;
    internal MainWindow UseOffscreen(DeckSettings settings, Rect bounds, Rect work, double scale)
    {
        _offscreen = true;
        AppInstance.ForTest();
        // A folder of its own for what the notices keep next to the settings (their banner ledger): never the user's.
        Store = new SettingsStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BrimDeck-offscreen-" + Environment.ProcessId));
        Settings = settings;
        UseLanguage();
        Notices = new NoticeCenter(this);
        Deck = new MainWindow(this);
        Deck.UseOffscreen(bounds, work, scale);
        return Deck;
    }
    // Names used by XAML templates follow the language through these resources.
    private void UseLanguage()
    {
        Loc.Use(Settings.Language);
        Resources["PinSourceName"] = Loc.T("固定显示", "Pin");
        Resources["UnpinSourceName"] = Loc.T("取消固定", "Unpin");
    }
    public void OpenSettings() => ShowSettings(false);
    // The panel ends a collapsed preview when the pointer reaches it. The settings window clears its own preview state; the
    // panel is resumed either way, in case the two disagree (ResumeAfterPreview does nothing when no preview is on).
    internal void EndPreviewFromDeck()
    {
        _settingsWindow?.EndPreview();
        Deck.ResumeAfterPreview();
    }
    // The panel's update dot opens the About page with the update dialog.
    internal void OpenUpdateSettings() => ShowSettings(true);
    private void ShowSettings(bool update)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            if (Store.LoadWarning is { } warning) _settingsWindow.SaveStatus(warning, true);
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        _ = update ? _settingsWindow.ShowUpdateNoticeAsync() : _settingsWindow.CheckUpdatesOnOpenAsync();
    }
    public void UpdateSettings(DeckSettings settings)
    {
        foreach (var previous in Settings.Apps)
        {
            var next = settings.Entry(previous.InstanceId);
            if (previous.SecretRevision != Guid.Empty && (next is null || next.QuotaSource != previous.QuotaSource))
            {
                try { Secrets.Delete(previous.InstanceId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { _settingsWindow?.SaveStatus(Loc.T("密钥文件无法更新，请检查本机设置目录后重试。", "The key file could not be updated. Check the local settings folder and try again."), true); return; }
            }
        }
        bool dataChanged = settings.IncludeWsl != Settings.IncludeWsl || !settings.RequiredProviders.SetEquals(Settings.RequiredProviders) ||
            !settings.EnabledApps.Select(e => e.ConfigurationKey).ToHashSet().SetEquals(Settings.EnabledApps.Select(e => e.ConfigurationKey));
        bool syncEnabled = settings.UsageAutoSync && !Settings.UsageAutoSync;
        // A new collapsed style morphs every panel's surface into its form (MainWindow.MorphShape).
        bool styleChanged = settings.Style != Settings.Style;
        var previousTheme = Settings.PanelTheme;
        settings.Normalize(); Settings = settings;
        ApplyHotkeys();
        // A panel whose collapsed row changes asks again as it is built (PresentMonitor).
        UpdateMonitorExtras();
        UpdateDeviceDemand();
        // Statuses and notes are worded when the data is read, so a new language reads the data again.
        bool languageChanged = Loc.Normalize(settings.Language) != Loc.Language;
        if (languageChanged) UseLanguage();
        // Automatic liquid glass just chosen shows at once the look what lies behind the island asks for, not the last one it had.
        if (LiquidTheme.IsAuto(settings.PanelTheme) && !LiquidTheme.IsAuto(previousTheme)) StartGlance();
        PanelTheme.Use(settings);
        MatchGlassGradient();
        ScheduleDaylight();
        AssignDisplays();
        foreach (var deck in Decks.ToList()) { if (styleChanged) deck.MorphShape(); else deck.ApplySettings(); }
        Notices.ApplySettings();
        QueueSave();
        CollapsedChanged();
        if (dataChanged || syncEnabled || languageChanged) _ = Deck.RefreshAutomaticallyAsync(queueIfBusy: true);
    }
    // The settings window's size group follows changes made on the panel (a drag, or content that changed the auto width).
    internal void CollapsedChanged() => _settingsWindow?.CollapsedChanged();
    private void SystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Exiting) return;
            // Automatic liquid glass's prior follows the Windows app mode (GlassAppearance.Prior: it may turn at once).
            LiquidTheme.ReadAppMode();
            // A theme whose day and night looks follow Windows' mode changes over like the dusk does (PanelThemes.LookAt).
            if (PanelThemes.NormalizeId(Settings.PanelTheme) == PanelThemes.SystemId || PanelTheme.FollowsWindowsMode(Settings)) ApplyPanelTheme(systemChanged: true);
            // Automatic liquid glass with nothing to measure follows the Windows app theme too. Otherwise the theme page's
            // "System" card shows the Windows mode whichever theme is chosen.
            else if (!FollowWindowsGlass()) _settingsWindow?.PanelThemeChanged(onlyThemePage: true);
        });
    }
    // Makes the chosen panel theme current (reading the theme folder again when asked) and repaints every panel if it changed.
    // When Windows switches "system" between dark and light, a preset clock gradient carries over as it does when a theme is chosen.
    internal void ApplyPanelTheme(bool reload = false, bool systemChanged = false)
    {
        var previous = PanelTheme.Current;
        bool changed = PanelTheme.Use(Settings, reload, systemChanged);
        ScheduleDaylight();
        if (!changed) return;
        if (systemChanged && PanelThemes.CarryGradient(previous, PanelTheme.Current, Settings)) QueueSave();
        foreach (var deck in Decks.ToList()) deck.ApplySettings();
        if (!reload) _settingsWindow?.PanelThemeChanged();
    }
    // The look automatic liquid glass takes before the island is shown (at start, and when the theme is chosen), set as
    // LiquidTheme.Dark: the Windows app theme's at first, so nothing waits for the desktop to be measured. Where liquid glass
    // can be shown, the desktop where the island will be is measured off the UI thread (SourceStart, BackdropLook.Measure) and
    // the look it asks for taken at once, without a fade (Glanced), typically within some 300 ms; the capture made for it is
    // handed to the island's glass (DesktopSource.Open), not made twice. A measure not back within GlanceBoundMs is let go then
    // (SourceStart.Within): the exclusion is released and the live glass decides once it shows. Where a glass already captures
    // (another liquid theme was shown), its last measure decides.
    // While the first look is measured (Glancing) an island booting waits out of sight (MainWindow.Boot, AfterGlance), so the
    // look it then takes is never seen to cut from the Windows app theme's: it appears in the measured look at once.
    private const int GlanceBoundMs = LiquidGlass.SourceStart.BoundMs;
    private int _glance;
    private Action? _afterGlance;
    internal bool Glancing { get; private set; }
    // Calls then once the first look is decided (at once when it is not being measured).
    internal void AfterGlance(Action then)
    {
        if (Glancing) _afterGlance += then; else then();
    }
    private void GlanceEnded()
    {
        Glancing = false;
        var then = _afterGlance;
        _afterGlance = null;
        then?.Invoke();
    }
    private bool StartGlance()
    {
        bool dark = SettingsPalette.IsDark(SettingsTheme.System);
        int glance = ++_glance;
        // A glass already captures: what it last measured decides now, and it goes on deciding.
        if (LiquidPossible && LiquidGlass.DesktopSource.OpenCount > 0 && !double.IsNaN(GlassBackdrop.SeenLuma)) dark = GlassAppearance.FirstLook(GlassBackdrop.SeenLuma, LiquidTheme.AppDark);
        LiquidTheme.Dark = dark;
        if (!LiquidPossible || LiquidGlass.DesktopSource.OpenCount > 0 || GlanceRegion() is not { } region) { GlanceEnded(); return dark; }
        // Our panels already on screen (the theme chosen in the settings) are left out, as the live glass leaves them out; and
        // the island, shown meanwhile, too (CaptureExclusion's hook).
        IntPtr[] ours = Deck is null ? [] : [.. Decks.Select(deck => new System.Windows.Interop.WindowInteropHelper(deck).Handle).Where(handle => handle != IntPtr.Zero)];
        LiquidGlass.CaptureExclusion.Acquire([], ours);
        Glancing = true;
        // The look taken and the exclusion let go even when taking the look throws; and the island waiting for it (AfterGlance)
        // is shown in any case (Within's ended), never left out of sight - also when starting the measure throws before Within
        // has it (R4-2): the glance then ends here, and the start goes on.
        bool handed = false;
        try
        {
            LiquidGlass.SourceStart.Within(LiquidGlass.SourceStart.Prepare(region, measure: true), GlanceBoundMs, prepared =>
            {
                try { Glanced(glance, dark, prepared?.Luma); }
                finally
                {
                    // Let go once the island's glass has taken the capture (DesktopSource.Open, at Render priority) and holds the
                    // exclusion itself, so our windows do not flicker back into the capture in between. Too late, at once.
                    if (prepared is null) LiquidGlass.CaptureExclusion.Release();
                    else Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, LiquidGlass.CaptureExclusion.Release);
                }
            }, () => { if (glance == _glance) GlanceEnded(); });
            handed = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A measure that cannot start is not worth failing the start for: the look shown stays, as without liquid glass.
            System.Diagnostics.Trace.WriteLine($"BrimDeck: the first look's measure did not start: {ex.Message}");
        }
        finally
        {
            if (!handed) { LiquidGlass.CaptureExclusion.Release(); GlanceEnded(); }
        }
        return dark;
    }
    // The first look measured: taken at once when it differs from the one shown, unless the theme was chosen again meanwhile,
    // the look was decided otherwise (the live glass, Windows' theme followed), or the measure came too late (null).
    private void Glanced(int glance, bool shownDark, double? luma)
    {
        if (Exiting || glance != _glance || luma is not { } measured
            || !LiquidTheme.IsAuto(Settings.PanelTheme) || LiquidTheme.Dark != shownDark || !GlassMeasures) return;
        bool dark = GlassAppearance.FirstLook(measured, LiquidTheme.AppDark);
        if (dark == shownDark && PanelTheme.Current.IsDark == dark) return;
        LiquidTheme.Dark = dark;
        _turnBy?.Stop();
        ApplyGlassTurn(snap: true);
    }
    // As GlassBackdrop.Available, which asks whether the theme on screen is glass: the theme just chosen is not on screen yet.
    private bool LiquidPossible => MotionEnvironment.Transparency && !SystemParameters.HighContrast && !MotionEnvironment.BatterySaver
        && GetSystemMetrics(RemoteSession) == 0 && MainLiquidUsable && Environment.GetEnvironmentVariable("BRIMDECK_LIQUID") != "0";
    // Where the main island will be: the top middle of its monitor (BackdropLook.Region), in device pixels.
    private LiquidGlass.RECT? GlanceRegion()
    {
        var plan = DisplayLayout.Arrange(Settings.Display, Settings.DisplayMonitors.Select(d => d.Id).ToList(), Displays.Select(d => (d.Id, d.Primary)).ToList());
        if ((Displays.FirstOrDefault(d => d.Id == plan.Main) ?? Displays.FirstOrDefault()) is not { } display) return null;
        var region = LiquidGlass.BackdropLook.Region(display.Bounds, display.Handle);
        return region.IsEmpty ? null : region;
    }
    // A theme with a night look changes look at its hours (PanelThemes.LookAt): the timer is set for the next change, and at
    // most ten minutes ahead, so a clock changed by hand or a machine that slept past the hour catches up soon after. The switch
    // carries a preset clock gradient over, as Windows switching "system" between dark and light does.
    private System.Windows.Threading.DispatcherTimer? _daylight;
    internal void ScheduleDaylight()
    {
        if (PanelTheme.NextDaylightChange(Settings) is not { } next) { _daylight?.Stop(); return; }
        if (_daylight is null)
        {
            _daylight = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
            _daylight.Tick += (_, _) => { _daylight.Stop(); if (!Exiting) ApplyPanelTheme(systemChanged: true); };
        }
        var wait = next - DateTime.Now;
        _daylight.Interval = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds + 500, 1000, TimeSpan.FromMinutes(10).TotalMilliseconds));
        _daylight.Stop(); _daylight.Start();
    }
    // A panel reports display changes (monitors added or removed, the primary changed, a work area or scale changed).
    // Each panel receives the same messages, so they are handled once, after Windows has moved the windows to the new layout.
    internal void DisplaysChanged()
    {
        if (_arrangePending || Exiting) return;
        _arrangePending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _arrangePending = false;
            if (Exiting) return;
            Displays = Native.Displays.Current(); AssignDisplays();
            foreach (var deck in Decks.ToList()) deck.ApplySettings();
            _settingsWindow?.DisplaysChanged();
        });
    }
    // Gives the main panel its monitor and opens or closes the panels of the other monitors. A panel that keeps its
    // monitor is kept, with its state.
    internal void AssignDisplays()
    {
        var plan = DisplayLayout.Arrange(Settings.Display, Settings.DisplayMonitors.Select(d => d.Id).ToList(), Displays.Select(d => (d.Id, d.Primary)).ToList());
        Deck.Display = Displays.FirstOrDefault(d => d.Id == plan.Main);
        foreach (var id in _otherDecks.Keys.Except(plan.Others).ToList())
        { var deck = _otherDecks[id]; _otherDecks.Remove(id); deck.Retire(); }
        // Panels are opened only once the main panel runs, so that they can take its data.
        if (!Deck.IsLoaded) return;
        foreach (var id in plan.Others)
        {
            var display = Displays.First(d => d.Id == id);
            if (_otherDecks.TryGetValue(id, out var deck)) { deck.Display = display; continue; }
            deck = new MainWindow(this, Deck) { Display = display };
            _otherDecks[id] = deck; deck.Show();
        }
    }
    internal void StartMedia()
    {
        bool wanted = Settings.MediaWanted;
        if (wanted && Media is null) { Media = new MediaSessions(Dispatcher); Media.Changed += RaiseMediaChanged; _ = Media.StartAsync(); }
        else if (!wanted && Media is not null) { Media.Changed -= RaiseMediaChanged; Media.Dispose(); Media = null; RaiseMediaChanged(); }
    }
    private void RaiseMediaChanged() => MediaChanged?.Invoke();
    // Audio levels are read while any panel shows music.
    internal void PresentMedia(MainWindow deck, bool active)
    {
        if (active) _presentingMedia.Add(deck); else _presentingMedia.Remove(deck);
        Media?.SetPresentationActive(_presentingMedia.Count > 0);
    }
    // The system is read while any panel shows the monitor page open (and briefly after; see SystemSampler) or shows readings in
    // its collapsed row. Processes are listed only for an open page.
    internal void PresentMonitor(MainWindow deck, bool page, bool strip)
    {
        if (page) _presentingMonitor.Add(deck); else _presentingMonitor.Remove(deck);
        if (strip) _stripMonitor.Add(deck); else _stripMonitor.Remove(deck);
        bool wanted = _presentingMonitor.Count + _stripMonitor.Count > 0;
        if (Exiting || Monitor is null && !wanted) return;
        // The sampler reads the choice of graphics adapter on its own thread, where each part is read whole.
        if (Monitor is null)
        {
            Monitor = new SystemSampler(Dispatcher, () => (Settings.MonitorGpu, Settings.MonitorGpuName));
            Monitor.Updated += snapshot => MonitorUpdated?.Invoke(snapshot);
            // A choice saved under an older key is kept under the adapter's key from now on (GpuChoice.Resolve).
            Monitor.GpuKeyUpgraded += (old, key) => { if (Settings.MonitorGpu == old) { Settings.MonitorGpu = key; QueueSave(); } };
        }
        Monitor.SetDemand(wanted, Settings.MonitorInterval, _presentingMonitor.Count > 0);
        UpdateMonitorExtras();
    }
    // The devices are read in full while any panel shows the devices page or the collapsed battery reading; otherwise, while the
    // low battery notice can show (the dynamic notices are on), the sources only listen (DeviceDemand.Listen); else nothing runs.
    internal void PresentDevices(MainWindow deck, bool page, bool strip)
    {
        if (page) _devicePages.Add(deck); else _devicePages.Remove(deck);
        if (strip) _deviceStrips.Add(deck); else _deviceStrips.Remove(deck);
        UpdateDeviceDemand();
    }
    private void UpdateDeviceDemand()
    {
        // The settings page is not read for with the display off, as a panel is not (its own demand drops then); the notice
        // still listens.
        var demand = _devicePages.Count + _deviceStrips.Count > 0 || _deviceSettings && !DisplayPower.Off ? DeviceDemand.Watch
            : Settings.DeviceLowNotice && Settings.NoticesEnabled ? DeviceDemand.Listen : DeviceDemand.None;
        if (Exiting || Devices is null && demand == DeviceDemand.None) return;
        if (Devices is null) { Devices = new DeviceBatteries(Dispatcher); Devices.Changed += DeviceBatteriesChanged; }
        Devices.SetDemand(demand);
    }
    // The settings' devices page lists what is connected, so it reads the devices while it is open.
    private bool _deviceSettings;
    internal void WatchDevicesForSettings(bool open) { _deviceSettings = open; UpdateDeviceDemand(); }
    private void DeviceBatteriesChanged()
    {
        if (Devices is { } devices && Settings.DeviceLowNotice && Settings.NoticesEnabled)
            foreach (var low in _lowBattery.Update(devices.All.Where(device => !Settings.Hides(device.Key)), Settings.DeviceLowPercent))
                Notices.SendDeviceLow(low);
        DevicesChanged?.Invoke();
    }
    // Temperatures and drives are read for an open page with them on, the drives' temperatures only there; the processor's and the
    // graphics adapter's temperatures, and the drives, also for a collapsed row showing those readings.
    private void UpdateMonitorExtras()
    {
        bool page = _presentingMonitor.Count > 0;
        var shown = _stripMonitor.Count > 0 ? Core.CompactMonitor.Shown(Settings.CompactReadings) : CompactMonitorItems.None;
        Monitor?.SetExtras(page && Settings.MonitorTemperatures || (shown & CompactMonitorItems.Temperature) != 0,
            page && Settings.MonitorStorage || (shown & CompactMonitorItems.Storage) != 0, page && Settings.MonitorStorage && Settings.MonitorTemperatures);
    }
    // Settings changed outside UpdateSettings are written after the same short pause instead of immediately.
    public void QueueSave() { _save.Stop(); _save.Start(); }
    // The settings are turned into text here, where they live, and written on the thread pool: writing the file (and the virus
    // scan that follows a write) took 60 ms and more, which showed as a stall after every page switch. Writes run one after
    // another in the order they were asked for, so the newest settings always land last.
    private Task _writing = Task.CompletedTask;
    public void FlushSettings()
    {
        _save.Stop();
        string text = Store.Serialize(Settings);
        _writing = _writing.ContinueWith(_ =>
        {
            string? error = null;
            try { Store.Write(text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = Loc.T("保存失败，请检查设置目录权限。", "Saving failed. Check the permissions of the settings folder."); }
            Dispatcher.BeginInvoke(() => { if (error is null) _settingsWindow?.SaveStatus(""); else _settingsWindow?.SaveStatus(error, true); });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
    // Before the process ends, the last write must have reached the disk.
    private void FinishWriting() { try { _writing.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { } }
    // The settings window records its size before the final save; closing it during shutdown would be too late.
    // Every panel powers off first (MainWindow.Lifecycle.cs): the islands draw back into dots that sink down through the floor, then the process ends. The
    // settings window goes at once, so the exit answers immediately. A panel that never reports back cannot hold the exit up:
    // the process ends anyway once the longest power-off would have finished.
    public void ExitApplication()
    {
        if (Exiting) return;
        Exiting = true; Lifetime.Cancel(); _settingsWindow?.RememberSize(); FlushSettings(); Notices?.Flush();
        _settingsWindow?.Hide();
        var decks = Decks.ToList();
        int waiting = decks.Count;
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BrimDeck.MainWindow.PowerOffLength(Settings.AnimationDuration) + 500) };
        bool done = false;
        void End() { if (done) return; done = true; fallback.Stop(); FinishWriting(); Shutdown(); }
        fallback.Tick += (_, _) => End();
        fallback.Start();
        foreach (var deck in decks) deck.PowerOff(() => { if (--waiting <= 0) End(); });
        if (decks.Count == 0) End();
    }
    internal void InstallUpdate()
    {
        if (Updates.PrepareRestart(() => { FinishWriting(); Store.Save(Settings); })) ExitApplication();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        // A settings change still waiting for its timer (made during the exit animation) is written before the last wait (life-L13).
        Exiting = true; Lifetime.Cancel(); Notices?.Dispose(); if (_save.IsEnabled) FlushSettings(); _save.Stop(); FinishWriting(); StopBackgroundUpdates(); DisplayPower.Uninstall(); Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged; MotionEnvironment.Changed -= GlassEnvironmentChanged; GlassBackdrop.LiquidFailed -= GlassEnvironmentChanged; InteractionFx.Uninstall(); _tray?.Dispose(); DisposeHotkeys(); _listener?.Unregister(null); _settingsEvent?.Dispose();
        Media?.Dispose(); Monitor?.Dispose(); Devices?.Dispose(); Lyrics.Dispose(); Prices?.Dispose(); _mutex?.Dispose(); base.OnExit(e);
    }
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    private const int RemoteSession = 0x1000;
}
