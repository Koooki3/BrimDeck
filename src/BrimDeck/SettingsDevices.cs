using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The devices page's settings: which devices it shows (a switch per device, read while this page is open), and the level at
// which a battery shows as low. Whether the page shows, and its size, are set with the other pages under 展开外观.
public partial class SettingsWindow
{
    private const int DevicesSettingsPage = 10;

    private void DevicesSettings()
    {
        // The devices are read while this page is open, so the list is current even with the panel collapsed.
        var holder = new Border();
        holder.Loaded += (_, _) => _app.WatchDevicesForSettings(true);
        holder.Unloaded += (_, _) => _app.WatchDevicesForSettings(false);
        // The window listens once, and lets go when it closes, unloaded or not; the rows refresh only while this page shows.
        if (!_devicesClosing)
        {
            _devicesClosing = true;
            _app.DevicesChanged += RefreshDeviceRows;
            Closed += (_, _) => { _app.DevicesChanged -= RefreshDeviceRows; _app.WatchDevicesForSettings(false); };
        }
        Target.Children.Add(holder);

        var threshold = new ComboBox { Width = 110, Style = (Style)FindResource("SettingsCombo"), ItemsSource = Devices.LowChoices.Select(percent => $"{percent}%").ToList(),
            SelectedIndex = Math.Max(0, Devices.LowChoices.ToList().IndexOf(S.DeviceLowPercent)) };
        AutomationProperties.SetName(threshold, Loc.T("低电量阈值", "Low battery threshold")); PopupMotion.Attach(threshold);
        threshold.SelectionChanged += (_, _) => { if (threshold.SelectedIndex >= 0) Change(s => s.DeviceLowPercent = Devices.LowChoices[threshold.SelectedIndex]); };
        Group(Row(Loc.T("低电量阈值", "Low battery threshold"), threshold, help: () => Loc.T("电量不高于此值时显示为低电量，高出 10 个百分点以内显示为偏低，分别使用读数的严重与警告颜色。设备页和收起行的“设备最低电量”都按它着色。",
            "At or under this level a battery shows as low; within 10 points above it, as getting low, in the readings' critical and warning colors. The devices page and the collapsed \"Lowest device battery\" reading both color by it.")));

        SectionTitle(Loc.T("设备", "Devices"), help: () => Loc.T(
            "支持：Windows 读取的蓝牙电量（蓝牙耳机、低功耗蓝牙键鼠与笔，与 Windows 设置中显示的一致）；罗技 Unifying、Bolt、Lightspeed 接收器与有线设备；雷蛇无线鼠标；赛睿 Arctis 与 HyperX Cloud 无线耳机；ATK、VXE、VGN、迈从、Endgame Gear、VAXEE、英菲克的 2.4G 鼠标与键盘；Xbox 与 PlayStation 手柄；AirPods 与 Beats（左右耳与充电盒）；以及带标准 HID 电量的设备。只读取，不会配对、断开或改动设备与系统设置。其他品牌的 2.4G 接收器协议未公开，切到蓝牙模式即可显示。",
            "Supported: the Bluetooth battery Windows reads (Bluetooth headphones and Bluetooth LE keyboards, mice and pens, as Windows Settings shows it); Logitech Unifying, Bolt and Lightspeed receivers and cabled devices; Razer wireless mice; SteelSeries Arctis and HyperX Cloud wireless headsets; 2.4 GHz mice and keyboards by ATK, VXE, VGN, MCHOSE, Endgame Gear, VAXEE and Inphic; Xbox and PlayStation controllers; AirPods and Beats (each bud and the case); and devices with the standard HID battery usages. Read only: nothing is paired, disconnected or changed on the devices or in Windows. Other brands' 2.4 GHz receivers do not publish their protocol; in Bluetooth mode they show."));
        Note(Loc.T("关闭开关的设备不在设备页和收起行中出现。设备只在设备页、收起行的电量读数或本设置页显示时读取。",
            "Devices switched off do not show on the page or in the collapsed row. Devices are read only while the devices page, the collapsed battery reading or this settings page shows."), 11.5, TextSecondary, new Thickness(0, -2, 0, 10));
        _deviceRows = new StackPanel();
        Target.Children.Add(new Border { Child = _deviceRows, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 0, 0, 28) });
        FillDeviceRows(_deviceRows);
        var strip = TextLine(Loc.T("提示：在“显示与读数”的收起读数中可加入“设备最低电量”，在刘海或胶囊里显示电量最低的设备。",
            "Tip: add \"Lowest device battery\" to the collapsed readings under Display and readings to show the device with the lowest charge in the notch or capsule."), 11.5, TextSecondary);
        strip.TextWrapping = TextWrapping.Wrap; Target.Children.Add(strip);
    }
    private StackPanel? _deviceRows;
    private bool _devicesClosing;
    private void RefreshDeviceRows() { if (_page == DevicesSettingsPage && _deviceRows is not null) FillDeviceRows(_deviceRows); }
    // What the rows were built for (each device's key and name, and the hidden ones), and each row's detail line by key, so a new
    // reading only changes the text and the switch keeps the keyboard's focus.
    private string _deviceRowsKey = "";
    private readonly Dictionary<string, TextBlock> _deviceDetails = [];

    // A row per device read now, and one per device switched off that is not connected, so it can be switched on again.
    private void FillDeviceRows(StackPanel rows)
    {
        var devices = _app.Devices?.All ?? [];
        string key = string.Join("|", devices.Select(device => device.Key + "=" + device.Name)) + "#" + string.Join("|", S.HiddenDevices.Select(hidden => hidden.Key))
            + "#" + (_app.Devices?.Settled == true) + "#" + rows.Children.Count;
        if (key == _deviceRowsKey && rows.Children.Count > 0)
        {
            foreach (var device in devices) if (_deviceDetails.TryGetValue(device.Key, out var line)) line.Text = DeviceDetail(device);
            return;
        }
        rows.Children.Clear(); _deviceDetails.Clear();
        var keys = devices.Select(device => device.Key).ToHashSet();
        var missing = S.HiddenDevices.Where(hidden => !keys.Contains(hidden.Key)).ToList();
        if (devices.Count == 0 && missing.Count == 0)
        {
            var empty = TextLine(_app.Devices?.Settled == true ? Loc.T("没有已连接的无线设备。", "No wireless devices connected.") : Loc.T("正在查找设备…", "Looking for devices…"), 12.5, TextSecondary);
            empty.Margin = new Thickness(0, 12, 0, 12); rows.Children.Add(empty);
            return;
        }
        void Add(string key, string title, string detail)
        {
            if (rows.Children.Count > 0) rows.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = !S.Hides(key), Padding = new Thickness(0) };
            AutomationProperties.SetName(toggle, Loc.T("显示 ", "Show ") + title);
            toggle.Checked += (_, _) => Change(s => s.HiddenDevices = [.. s.HiddenDevices.Where(hidden => hidden.Key != key)]);
            toggle.Unchecked += (_, _) => Change(s => s.HiddenDevices = [.. s.HiddenDevices.Where(hidden => hidden.Key != key).Append(new HiddenDevice(key, title))]);
            var line = TextLine(detail, 11.5, TextSecondary); line.TextWrapping = TextWrapping.Wrap;
            _deviceDetails[key] = line;
            rows.Children.Add(Row(title, toggle, line));
        }
        foreach (var device in devices) Add(device.Key, device.Name, DeviceDetail(device));
        foreach (var hidden in missing) Add(hidden.Key, hidden.Name.Length > 0 ? hidden.Name : hidden.Key, Loc.T("未连接", "Not connected"));
        _deviceRowsKey = string.Join("|", devices.Select(device => device.Key + "=" + device.Name)) + "#" + string.Join("|", S.HiddenDevices.Select(hidden => hidden.Key))
            + "#" + (_app.Devices?.Settled == true) + "#" + rows.Children.Count;
    }
    private static string DeviceDetail(DeviceBattery device) => string.Join(" · ", new[] { Devices.KindName(device.Kind),
        device.Lowest is null ? Loc.T("未报告电量", "No battery reported") : Devices.Figure(device.Lowest) + (device.Charging ? Loc.T("（充电中）", " (charging)") : ""), Devices.SourceName(device.Source) });

    private void DeviceSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.DevicesWidth, 440, 1200, value => Resize(s => s.DevicesWidth = value, DeckPage.Devices)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.DevicesHeight, 140, 400, value => Resize(s => s.DevicesHeight = value, DeckPage.Devices)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
    }
}
