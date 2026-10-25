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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Menus and tooltips on the panel take the theme's text rendering as they open (PanelTheme.Text).
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) => PanelTheme.Text((FrameworkElement)sender)));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler((sender, _) => PanelTheme.Text((FrameworkElement)sender)));
        Resources[SystemParameters.VerticalScrollBarWidthKey] = 7.0;
        var suffix = Environment.UserName;
        var eventName = "Local\\BrimDeck.Settings." + suffix;
        _mutex = new Mutex(true, "Local\\BrimDeck.Instance." + suffix, out var first);
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
        // Automatic liquid glass shows from the first paint the look what lies behind the island asks for (GlanceGlass).
        if (PanelThemes.NormalizeId(Settings.PanelTheme) == PanelThemes.GlassAuto)
        {
            if (GlanceGlass() != PanelTheme.Current.IsDark) PanelTheme.Use(Settings, systemChanged: true);
            MatchGlassGradient();
        }
        MotionEnvironment.Changed += GlassEnvironmentChanged;
        GlassBackdrop.LiquidFailed += GlassEnvironmentChanged;
        Deck = new MainWindow(this); MainWindow = Deck;
        AssignDisplays();
        Deck.Show();
        Notices.ApplySettings();
        InitializeHotkeys();
        StartBackgroundUpdates();
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
        // Statuses and notes are worded when the data is read, so a new language reads the data again.
        bool languageChanged = Loc.Normalize(settings.Language) != Loc.Language;
        if (languageChanged) UseLanguage();
        // Automatic liquid glass just chosen shows at once the look what lies behind the island asks for, not the last one it had.
        if (PanelThemes.NormalizeId(settings.PanelTheme) == PanelThemes.GlassAuto && PanelThemes.NormalizeId(previousTheme) != PanelThemes.GlassAuto) GlanceGlass();
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
            if (PanelThemes.NormalizeId(Settings.PanelTheme) == PanelThemes.SystemId) ApplyPanelTheme(systemChanged: true);
            // Automatic liquid glass with nothing to measure follows the Windows app theme too. Otherwise the theme page's
            // "System" card shows the Windows mode whichever theme is chosen.
            else if (!FollowWindowsGlass()) _settingsWindow?.PanelThemeChanged(onlyThemePage: true);
        });
    }
    // Makes the chosen panel theme current (reading the theme folder again when asked) and repaints every panel if it changed.
    // When Windows switches "system" between dark and light, a preset clock gradient carries over as it does when a theme is chosen.
    // A turn of automatic liquid glass (glassTurn) carries it too, so the clock stays readable on the other look, but saves
    // nothing (PanelThemes.CarryOnTurn: it can come every few seconds over a video, and the look is decided again at every start)
    // and only names the new look on the theme page instead of building the settings page again.
    internal void ApplyPanelTheme(bool reload = false, bool systemChanged = false, bool glassTurn = false)
    {
        var previous = PanelTheme.Current;
        bool changed = PanelTheme.Use(Settings, reload, systemChanged || glassTurn);
        // A turn of automatic liquid glass under way ends with any other theme chosen.
        if (changed && PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) { _turnBy?.Stop(); foreach (var each in Decks.ToList()) each.LeadGlass(null); }
        ScheduleDaylight();
        if (!changed) return;
        if ((systemChanged || glassTurn) && PanelThemes.CarryOnTurn(previous, PanelTheme.Current, Settings, glassTurn)) QueueSave();
        foreach (var deck in Decks.ToList()) deck.ApplySettings();
        if (reload) return;
        if (glassTurn) _settingsWindow?.GlassTurned(); else _settingsWindow?.PanelThemeChanged();
    }
    // Automatic liquid glass ("liquid-glass"): the main island's glass measures what lies behind it and decides the look
    // (GlassBackdrop, BackdropLook, GlassAppearance). The first decision, at start or when the theme is chosen, is shown at
    // once (PanelTheme.SnapGeneration). A later one turns the glass first (MainWindow.LeadGlass); the theme, and with it every
    // text and icon brush, turns in one step when the glass passes the brightness where both looks' text reads
    // (GlassBackdrop.Crossed), without the theme's crossfade (PanelTheme.QuietGeneration), so no glyph is ever drawn twice.
    // TurnByMs is the latest the theme follows, should the glass not report (no liquid glass shown).
    private const int TurnByMs = 600;
    private System.Windows.Threading.DispatcherTimer? _turnBy;
    internal bool AutoLookFrom(MainWindow deck) => !Exiting && deck == Deck && PanelThemes.NormalizeId(Settings.PanelTheme) == PanelThemes.GlassAuto;
    internal void TurnGlass(MainWindow deck, bool dark, bool instant)
    {
        // With nothing to measure, the Windows app theme decides (FollowWindowsGlass); a last measure still under way is let go.
        if (!AutoLookFrom(deck) || !GlassMeasures || PanelTheme.GlassDark == dark && PanelTheme.Current.IsDark == dark) return;
        PanelTheme.GlassDark = dark;
        if (instant || !Soft.Enabled)
        {
            _turnBy?.Stop();
            PanelTheme.SnapGeneration = PanelTheme.Generation + 1;
            ApplyPanelTheme(glassTurn: true);
            return;
        }
        foreach (var each in Decks.ToList()) each.LeadGlass(dark);
        if (_turnBy is null)
        {
            _turnBy = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(TurnByMs) };
            _turnBy.Tick += (_, _) => TurnTheme();
        }
        _turnBy.Stop(); _turnBy.Start();
    }
    internal void GlassCrossed(MainWindow deck)
    {
        if (deck == Deck) TurnTheme();
    }
    private void TurnTheme()
    {
        _turnBy?.Stop();
        if (Exiting) return;
        // Automatic liquid glass given up meanwhile: the glass, led toward a look the theme will not take, follows the theme again.
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) { foreach (var each in Decks.ToList()) each.LeadGlass(null); return; }
        if (PanelTheme.Current.IsDark == PanelTheme.GlassDark) return;
        PanelTheme.QuietGeneration = PanelTheme.Generation + 1;
        ApplyPanelTheme(glassTurn: true);
    }

    // Whether automatic liquid glass can measure what lies behind the island: liquid glass can be shown on the main island
    // (transparency effects on, no high contrast, no battery saver, a local session, and the capture has not failed there).
    // Otherwise it follows the Windows app theme, as "system" does, and the theme page says so.
    internal bool GlassMeasures => GlassBackdrop.Available && MainLiquidUsable;
    private bool MainLiquidUsable => Deck?.LiquidUsable != false;
    // Automatic liquid glass with nothing to measure takes the Windows app theme's look. True when that turned the theme.
    private bool FollowWindowsGlass()
    {
        if (Exiting || PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto || GlassMeasures) return false;
        bool dark = SettingsPalette.IsDark(SettingsTheme.System);
        if (PanelTheme.GlassDark == dark && PanelTheme.Current.IsDark == dark) return false;
        PanelTheme.GlassDark = dark;
        _turnBy?.Stop();
        foreach (var each in Decks.ToList()) each.LeadGlass(null);
        ApplyPanelTheme(glassTurn: true);
        return true;
    }
    // Something that decides whether liquid glass can be shown changed (battery saver, transparency effects, the capture
    // failing): automatic liquid glass may have to follow Windows now, and the theme page's note may change.
    private bool? _glassMeasured;
    private void GlassEnvironmentChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (Exiting || Deck is null || PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) return;
            bool measures = GlassMeasures;
            bool turned = FollowWindowsGlass();
            if (measures != _glassMeasured) { bool known = _glassMeasured is not null; _glassMeasured = measures; if (known && !turned) _settingsWindow?.PanelThemeChanged(onlyThemePage: true); }
        });
    }
    // Automatic liquid glass shown at start or just chosen: a clock gradient that is a preset of its other look (the look saved
    // last time, or the one the theme card was chosen in) becomes this look's, as it does when the look turns.
    private void MatchGlassGradient()
    {
        if (PanelThemes.NormalizeId(Settings.PanelTheme) != PanelThemes.GlassAuto) return;
        var other = PanelThemes.Resolve(PanelTheme.Find(PanelTheme.Current.IsDark ? PanelThemes.GlassLight : PanelThemes.GlassDark));
        PanelThemes.CarryOnTurn(other, PanelTheme.Current, Settings, glassTurn: true);
    }
    // The look automatic liquid glass takes before the island is shown (at start, and when the theme is chosen), set as
    // PanelTheme.GlassDark: from one measure of the desktop where the island will be (BackdropLook.Glance), or the Windows app
    // theme's when liquid glass cannot be shown there or the measure fails. The live measure then rarely has anything to turn.
    private bool GlanceGlass()
    {
        bool dark = SettingsPalette.IsDark(SettingsTheme.System);
        // As GlassBackdrop.Available, which asks whether the theme on screen is glass: the theme just chosen is not on screen yet.
        bool shown = MotionEnvironment.Transparency && !SystemParameters.HighContrast && !MotionEnvironment.BatterySaver && GetSystemMetrics(RemoteSession) == 0 && MainLiquidUsable
            && Environment.GetEnvironmentVariable("BRIMDECK_LIQUID") != "0";
        if (shown)
        {
            var plan = DisplayLayout.Arrange(Settings.Display, Settings.DisplayMonitors.Select(d => d.Id).ToList(), Displays.Select(d => (d.Id, d.Primary)).ToList());
            if ((Displays.FirstOrDefault(d => d.Id == plan.Main) ?? Displays.FirstOrDefault()) is { } display)
            {
                // Our panels already on screen (the theme chosen in the settings) are left out, as the live glass leaves them out.
                IntPtr[] ours = Deck is null ? [] : [.. Decks.Select(deck => new System.Windows.Interop.WindowInteropHelper(deck).Handle).Where(handle => handle != IntPtr.Zero)];
                if (LiquidGlass.BackdropLook.Glance(display.Bounds, display.Handle, ours) is { } luma) dark = GlassAppearance.FirstLook(luma);
            }
        }
        PanelTheme.GlassDark = dark;
        return dark;
    }
    // A theme with a night look changes look at its hours (PanelThemes.IsNight): the timer is set for the next change, and at
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
        if (Monitor is null) { Monitor = new SystemSampler(Dispatcher); Monitor.Updated += snapshot => MonitorUpdated?.Invoke(snapshot); }
        Monitor.SetDemand(wanted, Settings.MonitorInterval, _presentingMonitor.Count > 0);
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
        Exiting = true; Lifetime.Cancel(); Notices?.Dispose(); if (_save.IsEnabled) FlushSettings(); _save.Stop(); FinishWriting(); StopBackgroundUpdates(); Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged; MotionEnvironment.Changed -= GlassEnvironmentChanged; GlassBackdrop.LiquidFailed -= GlassEnvironmentChanged; InteractionFx.Uninstall(); _tray?.Dispose(); DisposeHotkeys(); _listener?.Unregister(null); _settingsEvent?.Dispose();
        Media?.Dispose(); Monitor?.Dispose(); Lyrics.Dispose(); Prices?.Dispose(); _mutex?.Dispose(); base.OnExit(e);
    }
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    private const int RemoteSession = 0x1000;
}
