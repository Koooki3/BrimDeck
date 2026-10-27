using System.Windows;
using System.Windows.Media.Animation;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The system monitor page and the readings in the collapsed row. The sampler (App.Monitor) runs while any panel shows either;
// every panel draws the same readings. The page is built once per layout step (MonitorLayout), palette, sort, main gauge and
// theme; within a step it stretches, and each new reading only changes what its parts draw.
public partial class MainWindow
{
    private MonitorView? _monitorView;
    private string _monitorKey = "";
    private MonitorStrip? _compactStrip;
    // Which readings the collapsed row shows and how far each has come in or gone out, kept while the row is built again.
    private readonly StripPresence _stripPresence = new();
    // When the temperatures and the drives join the collapsed row: only once their first figures arrive (StripAdmission).
    private readonly StripAdmission _stripAdmission = new();
    private bool _monitorShowing, _monitorShown, _stripShowing, _monitorFade, _powerPage;

    private void InitializeMonitor()
    {
        IsVisibleChanged += (_, _) => UpdateMonitorDemand();
        _app.MonitorUpdated += MonitorUpdated;
        // Plugging in or unplugging changes what the power reading can come from (battery discharge on battery only): asked again then.
        Microsoft.Win32.PowerModeChangedEventHandler powerChanged = (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.StatusChange) Dispatcher.BeginInvoke(UpdateMonitorDemand); };
        Microsoft.Win32.SystemEvents.PowerModeChanged += powerChanged;
        Closed += (_, _) => { _app.MonitorUpdated -= MonitorUpdated; Microsoft.Win32.SystemEvents.PowerModeChanged -= powerChanged; _app.PresentMonitor(this, false, false); };
    }

    // Asked whenever the panel opens, closes, changes page or is hidden, and whenever the collapsed row is built.
    private void UpdateMonitorDemand()
    {
        // Nothing is read for a display that is off (ScreenOff), nor for a row whose readings have all gone.
        // A page left open while the display is off stays as it was (its card, the list held under the pointer); it only stops
        // being read, and takes the current readings when the display is back.
        bool shown = _expanded && IsMonitorPage && IsVisible && !IsSuppressed;
        bool page = shown && !ScreenOff;
        // A row whose only chosen reading is the battery reads the devices (UpdateDevicesDemand), not the system. A row whose
        // chosen readings all wait for their first figures shows nothing, yet must be read for them (StripAwaits).
        bool strip = !_expanded && (_compactStrip?.Showing(Eased.Now) == true && _compactStrip.NeedsSystem || StripAwaits()) && IsVisible && !IsSuppressed && !ScreenOff && _alert is null && EffectiveStyle != CompactStyle.Line;
        if (!shown)
        {
            // A list that is not on screen cannot be under the pointer, and a closed page keeps no card open.
            if (_monitorShown && _app.Monitor is { } monitor) monitor.HoldProcesses = false;
            _monitorView?.CloseCard();
        }
        _monitorShown = shown;
        // The power is read for a page that shows its line (a page too small for it, or with the setting off, reads none of it).
        bool power = page && Settings.MonitorPower && _monitorView?.ShowsPower == true;
        _powerPage = power;
        _app.PresentMonitor(this, page, strip, power);
        // Neither took readings while hidden: they show the current ones, or dashes once the sampler has paused, never the
        // figures they showed last time.
        if (page && !_monitorShowing) _monitorView?.Update(_app.Monitor?.Latest ?? WaitingSnapshot, motion: false);
        if (strip && !_stripShowing) _compactStrip?.Update(_app.Monitor?.Latest);
        _monitorShowing = page; _stripShowing = strip;
        // The devices page and the collapsed battery reading ask on the same occasions.
        UpdateDevicesDemand();
    }
    // The collapsed readings the row was last built without (StripAdmission.Hidden): the drives until their first figures, and for
    // good on a machine without them; and the temperatures and letters it was built with (StripAdmission.Layout).
    private CompactMonitorItems _stripUnavailable;
    private string _stripLayout = "";
    // A laptop on mains that gave no power reading may give one on battery (the discharge rate): its power is asked again then.
    private SensorStates StripSensors => _app.Monitor is { } monitor ? new(monitor.CpuSensor, monitor.GpuSensor, monitor.StorageState, monitor.DriveSensor)
    {
        Power = monitor.PowerSensor == SensorState.None && Native.BatteryProvider.SystemHasBattery && !Native.BatteryProvider.OnMains ? SensorState.Unknown : monitor.PowerSensor,
        Charge = monitor.BatterySensor
    } : SensorStates.Unknown;
    // Whether this surface shows the system readings and waits for the first figures of one it does not show yet.
    private bool StripAwaits()
    {
        if (!Settings.ShowsMonitor(EffectiveStyle)) return false;
        return _stripAdmission.Awaits(Settings.CompactReadings, _app.Monitor?.Latest, StripSensors);
    }
    private PowerOptions PowerBlockFor() => new(PowerStyle.For(MonitorThemeColors.For(Settings)), Settings.DeviceLowPercent, Native.BatteryProvider.SystemHasBattery);
    private static MonitorSnapshot WaitingSnapshot => MonitorSnapshot.Empty(Environment.ProcessorCount, Loc.T("正在读取", "Reading"));

    private static bool MonitorMotion => Soft.Enabled;

    private void MonitorUpdated(MonitorSnapshot snapshot)
    {
        // Worked out on every reading, open or not, so a row built when the panel collapses already has what arrived meanwhile.
        bool admitted = AdmitStrip(snapshot);
        if (!_expanded)
        {
            // The drives, a temperature, a drive letter or the battery got its first figure: the row is built again with it, and it comes
            // in with the row's own entrance while the others slide aside. Until then the row did not show it, so nothing else changes.
            if (admitted) return;
            _compactStrip?.Update(snapshot);
            // The last reading has faded out of a row that stays as it is: the sampler may rest.
            if (_stripShowing && _compactStrip?.Showing(Eased.Now) != true) UpdateMonitorDemand();
            return;
        }
        if (!IsMonitorPage || _monitorView is null) return;
        if (MonitorKey() != _monitorKey)
        {
            // Rebuilt because a kind of sensor turned out to have none (or turned up), the page fades in rather than cuts.
            if (BuiltSensors() != _monitorSensors) _monitorFade = true;
            RenderMonitor(); return;
        }
        _monitorView.Update(snapshot, MonitorMotion);
    }

    // Which later readings the collapsed row shows now (StripAdmission); builds the row again, and returns true, when one joins
    // or leaves. Asked on every system reading and every change of the devices.
    private bool AdmitStrip(MonitorSnapshot? snapshot)
    {
        var (hidden, arriving) = UpdateStripAdmission(snapshot);
        // Open, the row is not on screen; it is built with the new readings when the panel collapses.
        // A row not built yet (its chosen readings all waiting) is built once one of them joins.
        // A temperature or a letter that joins or leaves changes its reading's width, and the row is built again for it.
        if (_expanded || _compactStrip is null && !Settings.ShowsMonitor(EffectiveStyle) || hidden == _stripUnavailable && _stripAdmission.Layout == _stripLayout) return false;
        _stripPresence.Arrive(arriving); RenderCompact();
        return true;
    }
    private (CompactMonitorItems Hidden, CompactMonitorItems Arriving) UpdateStripAdmission(MonitorSnapshot? snapshot)
    {
        return _stripAdmission.Update(snapshot, Settings.CompactReadings, StripSensors, BatteryState());
    }
    // The battery reading's state: Found while a shown device reports a charge, None once the devices are listed and none
    // does, Unknown before (or while nothing reads them).
    private SensorState BatteryState() => LowestBattery() is not null ? SensorState.Found : DevicesSettled ? SensorState.None : SensorState.Unknown;

    // The page's bottom margin for a page of this height; its sides are the header's inset.
    private static double MonitorBottom(double height) => height < 170 ? 10 : 16;
    // The step the page is built for, kept while a resize stays within MonitorLayout.Hysteresis of it.
    private MonitorLayout? _monitorLayout;
    private MonitorLayout CurrentMonitorLayout()
    {
        double height = ContentCanvas.Height;
        return MonitorLayout.For(ContentCanvas.Width - 2 * _header.Side, height - _header.Row - MonitorBottom(height), Settings.MonitorProcesses, MonitorSensors().Drives,
            _monitorView is not null ? _monitorLayout : null, MonitorSensors().Power);
    }

    // What the view is built from: its layout step, not its exact size, so a page resized within a step is not built again.
    // The sort is not part of it: a new sort reorders the list in place. The page keeps lines for the drives (as many as were found,
    // or as many as fit while they are not read yet, within the layout's room: MonitorLayout.StorageLines) and room for a
    // temperature beside the small gauge (MonitorSensors), so it is built again only when the lines it shows or that room change:
    // when the drives are first counted, a sensor turns out to have none or first shows, or a drive comes or goes past the room.
    private string MonitorKey() => string.Join("|", CurrentMonitorLayout(), Settings.MonitorPalette, Settings.MonitorPalette == MonitorPalette.Custom ? Settings.MonitorCustomColors.Key : "", Settings.MonitorProcesses,
        Settings.MonitorMain, Settings.MonitorProcessView, Loc.Language, PanelTheme.Generation, Settings.MonitorTemperatures, MonitorSensors().Small, Settings.MonitorPower, Native.BatteryProvider.SystemHasBattery);
    // What the shown page was built for: its drives' lines, the small gauge's room and the lines for power and the battery.
    private (int Lines, bool Small, int Power) _monitorSensors;
    private (int Lines, bool Small, int Power) BuiltSensors() => (CurrentMonitorLayout().StorageLines, MonitorSensors().Small, CurrentMonitorLayout().PowerLines);
    // Drives: the drives to keep lines for, 0 for none; while they are not counted yet, as many as the page can hold.
    private (int Drives, bool Small, int Power) MonitorSensors()
    {
        var monitor = _app.Monitor;
        int lines = !Settings.MonitorStorage || monitor?.StorageState == SensorState.None ? 0 : monitor?.StorageCount is > 0 and var count ? count : 3;
        // The processor's zones are reserved for only once one has shown (a fixed zone never does); a graphics adapter is
        // reserved for until it is known to have no sensor.
        bool small = Settings.MonitorMain == MonitorMain.Cpu ? monitor?.GpuSensor != SensorState.None : monitor?.CpuSensor == SensorState.Found;
        // The power and the battery lines (PowerBlock) are asked for as many as the machine can show: both on a machine with a battery
        // (known at once from the system), power alone on one without, none once the sampler finds no source.
        int power = !Settings.MonitorPower ? 0 : Native.BatteryProvider.SystemHasBattery ? 2 : monitor?.PowerSensor == SensorState.None ? 0 : 1;
        return (lines, Settings.MonitorTemperatures && small, power);
    }

    private void RenderMonitor()
    {
        // A page on screen that is built again (a resize across a step, a sensor found or missing, a new main gauge) gives way to
        // the new one with a crossfade; a page opened from another one appears as it always has.
        bool shown = MonitorContent.Visibility == Visibility.Visible && _monitorView is not null && MonitorContent.Children.Contains(_monitorView);
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; DevicesContent.Visibility = Visibility.Collapsed; NoticesContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        // The header keeps the usage page's controls, as the music page does, so the switch does not move between pages. Fitting
        // the usage page for them is kept until its size, data or settings change: a drag resizes this page many times a second.
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        MonitorContent.Margin = new Thickness(_header.Side, 0, _header.Side, MonitorBottom(height));
        var key = MonitorKey();
        bool fade = _monitorFade; _monitorFade = false;
        if (_monitorView is not null && key == _monitorKey && MonitorContent.Children.Contains(_monitorView)) return;
        var layout = CurrentMonitorLayout();
        _monitorKey = key; _monitorLayout = layout; _monitorSensors = BuiltSensors();
        // The list being replaced may have been under the pointer; its leave event never comes.
        if (_app.Monitor is { } monitor) monitor.HoldProcesses = false;
        var old = shown ? _monitorView : null;
        old?.CloseCard();
        _monitorView = new MonitorView(layout, MonitorThemeColors.For(Settings), Settings.MonitorMain, Settings.MonitorSort,
            Settings.MonitorProcessView, SwitchMonitorMain, SortProcesses, hold => { if (_app.Monitor is { } sampler) sampler.HoldProcesses = hold; }, SwitchMonitorGpu,
            Settings.MonitorTemperatures, _monitorSensors.Small, PowerBlockFor());
        _monitorView.Update(_app.Monitor?.Latest ?? WaitingSnapshot, motion: false);
        // The new page may show the power line where the old one did not, or the other way round: what is read follows.
        if (_monitorShowing && _monitorView.ShowsPower != _powerPage) UpdateMonitorDemand();
        // A drag across a layout step (and the spring settling after it) swaps at once: the page follows the pointer, and a
        // crossfade there laid both layouts over each other for most of its length (live check X-02).
        if (old is not null) MonitorCrossfade.Swap(MonitorContent, _monitorView, old, MonitorMotion && !Resizing);
        else
        {
            MonitorContent.Children.Clear(); MonitorContent.Children.Add(_monitorView);
            // A new main gauge, asked for on the page, arrives with a short fade instead of a cut.
            if (fade && MonitorMotion) _monitorView.BeginAnimation(OpacityProperty, Soft.Fade(.15, 1, Soft.Short));
        }
    }

    private (double Width, double Height, IReadOnlyList<ProviderSnapshot> Snapshots, DeckSettings Settings, PanelHeader Header)? _usageHeader;
    private PanelHeader CachedUsageHeader()
    {
        double width = UsageLayoutWidth, height = UsagePageHeight;
        if (_usageHeader is { } known && known.Width == width && known.Height == height && ReferenceEquals(known.Snapshots, Snapshots) && ReferenceEquals(known.Settings, Settings))
            return known.Header;
        var header = UsageHeader(width, height);
        _usageHeader = (width, height, Snapshots, Settings, header);
        return header;
    }

    // The sort and the main gauge are the same for every panel and are kept with the settings. A new main gauge rebuilds the page,
    // which fades in; a new sort only reorders the list.
    private void SortProcesses(ProcessSort sort)
    {
        if (Settings.MonitorSort == sort) return;
        Settings.MonitorSort = sort; _app.QueueSave();
        // Every panel's page, shown or not: a page kept for later is not built again, so it must not keep the old sort.
        foreach (var deck in _app.Decks) deck._monitorView?.SetSort(sort);
    }
    private void SwitchMonitorMain(MonitorMain main)
    {
        if (Settings.MonitorMain == main) return;
        Settings.MonitorMain = main; _app.QueueSave();
        RefreshMonitorPages();
    }
    // The adapter shown on the page is the one the monitor shows everywhere, the collapsed readings too, so a click on the page
    // keeps it as the choice under the monitor's settings. Every page and strip shows it at once, without waiting for a reading.
    private void SwitchMonitorGpu(GpuSample gpu)
    {
        if (Settings.MonitorGpu == gpu.Key) return;
        Settings.MonitorGpu = gpu.Key; Settings.MonitorGpuName = gpu.Name; _app.QueueSave();
        _app.Monitor?.ShowGpuChoice();
    }
    private void RefreshMonitorPages()
    {
        foreach (var deck in _app.Decks) if (deck._expanded && deck.IsMonitorPage) { deck._monitorFade = true; deck.RenderMonitor(); }
    }
}
