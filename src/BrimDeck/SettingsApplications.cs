using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private Border AppNameField(AppEntry? entry)
    {
        var input = new TextBox { Style = (Style)FindResource("SettingsNumber"), TextAlignment = TextAlignment.Left,
            FontSize = 12.5, MaxLength = 64, VerticalAlignment = VerticalAlignment.Center, IsEnabled = entry is not null };
        AutomationProperties.SetAutomationId(input, "display-name-" + entry?.InstanceId.ToString("N"));
        string help = Loc.T("显示在看板上的名称；浅色文字为默认名称，点击后可直接输入，留空恢复配额来源名称",
            "The name shown on the panel. Light text is the default name; click to type a new one, or leave it empty to use the quota source's name");
        AutomationProperties.SetHelpText(input, help);
        var field = new Border { Child = input, Height = 34, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Background = UI.Brush(_palette.Input),
            BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1) };
        bool showingDefault = false;
        void Refresh()
        {
            var current = entry is null ? null : S.Entry(entry.InstanceId);
            showingDefault = !input.IsKeyboardFocused && string.IsNullOrWhiteSpace(current?.DisplayName);
            input.Text = input.IsKeyboardFocused ? current?.DisplayName ?? "" : current?.Name ?? "";
            input.Foreground = UI.Brush(showingDefault ? TextTertiary : TextPrimary);
            field.BorderBrush = UI.Brush(input.IsKeyboardFocused ? _palette.Accent : _palette.Border);
            AutomationProperties.SetName(input, Loc.T("显示名称 ", "Display name ") + (current?.Name ?? Loc.T("未配置应用", "unconfigured app")));
        }
        void Commit()
        {
            if (entry is not null && S.Entry(entry.InstanceId) is { } current)
            {
                var value = showingDefault ? "" : input.Text.Trim();
                if (value != current.DisplayName) Change(s => s.Entry(entry.InstanceId)!.DisplayName = value);
            }
            Refresh();
        }
        input.GotKeyboardFocus += (_, _) => Refresh();
        input.LostKeyboardFocus += (_, _) => Commit();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Refresh(); e.Handled = true; }
        };
        // Padding belongs to the input's click target as well.
        field.MouseLeftButtonDown += (_, e) => { input.Focus(); e.Handled = true; };
        Refresh();
        return field;
    }

    private const double AppRowHeight = 69;
    private AppDrag? _appDrag;
    private readonly Dictionary<Border, long> _appRowMotionVersions = [];
    private long _appRowMotionVersion;
    private sealed class AppDrag(Border[] rows, Border row, Border handle, double grabOffset)
    {
        public Border[] Rows { get; } = rows;
        public Border Row { get; } = row;
        public Border Handle { get; } = handle;
        public double GrabOffset { get; } = grabOffset;
        public int From { get; } = Array.IndexOf(rows, row);
        public int Target { get; set; } = Array.IndexOf(rows, row);
    }

    private static TranslateTransform RowTransform(Border row) => (TranslateTransform)row.RenderTransform;
    private void SetRowOffset(Border row, double offset)
    {
        _appRowMotionVersions[row] = ++_appRowMotionVersion;
        var transform = RowTransform(row);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = offset;
    }

    private void AnimateRow(Border row, double target, Action? completed = null, bool animate = true)
    {
        var transform = RowTransform(row);
        double start = transform.Y;
        if (!animate || !Soft.Enabled || Math.Abs(start - target) < .1)
        {
            SetRowOffset(row, target);
            completed?.Invoke();
            return;
        }
        // A new WPF animation clock starts on the next render tick. Keep the
        // current position as the base value so neither that tick nor a rapid
        // direction change can briefly expose the destination position.
        SetRowOffset(row, start);
        long version = _appRowMotionVersions[row];
        var motion = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(S.AnimationDuration))
        {
            EasingFunction = Soft.Out
        };
        motion.Completed += (_, _) =>
        {
            // Replaced clocks can still complete; they must not move a row
            // that has since been dragged, reversed or returned on cancellation.
            if (_appRowMotionVersions.GetValueOrDefault(row) != version) return;
            SetRowOffset(row, target); completed?.Invoke();
        };
        transform.BeginAnimation(TranslateTransform.YProperty, motion);
    }

    private static void ResetDragAppearance(Border row)
    {
        row.Background = Brushes.Transparent;
        Panel.SetZIndex(row, 0);
    }

    private void AttachDrag(Border handle)
    {
        AutomationProperties.SetName(handle, Loc.T("拖动调整顺序", "Drag to reorder"));
        // Focusable so that pressing it commits a name being edited; reordering is by mouse only, so Tab skips it.
        handle.Focusable = true; KeyboardNavigation.SetIsTabStop(handle, false); handle.FocusVisualStyle = null;
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (_appRows is null || handle.Parent is not Grid { Parent: Border row }) return;
            handle.Focus(); // Commits any name being edited before beginning the drag.
            StartAppDrag(row, handle, e.GetPosition(_appRows).Y);
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (_appDrag?.Handle != handle || !handle.IsMouseCaptured || _appRows is null) return;
            UpdateAppDrag(e.GetPosition(_appRows).Y);
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (_appDrag?.Handle != handle) return;
            EndAppDrag(true); e.Handled = true;
        };
        handle.LostMouseCapture += (_, _) => { if (_appDrag?.Handle == handle) EndAppDrag(false); };
    }

    private void StartAppDrag(Border row, Border handle, double pointerY)
    {
        EndAppDrag(false, false);
        CollapseSourceRows();
        if (_appRows is null) return;
        var rows = _appRows.Children.OfType<Border>().Where(b => b.Tag is AppEntry).ToArray();
        foreach (var item in rows) ResetDragAppearance(item);
        int from = Array.IndexOf(rows, row);
        _appDrag = new AppDrag(rows, row, handle, pointerY - from * AppRowHeight - RowTransform(row).Y);
        // Keep the visual tree and mouse capture intact until release. Only transforms move during a drag.
        SetRowOffset(row, RowTransform(row).Y);
        row.Background = UI.Brush(Inset); row.CornerRadius = new CornerRadius(6); Panel.SetZIndex(row, 2);
        if (!handle.CaptureMouse()) EndAppDrag(false, false);
    }

    internal void UpdateAppDrag(double pointerY)
    {
        if (_appDrag is not { } drag) return;
        double top = Math.Clamp(pointerY - drag.GrabOffset, 0, (drag.Rows.Length - 1) * AppRowHeight);
        SetRowOffset(drag.Row, top - drag.From * AppRowHeight);
        int target = Math.Clamp((int)Math.Floor(top / AppRowHeight + .5), 0, drag.Rows.Length - 1);
        if (target == drag.Target) return;
        drag.Target = target;
        for (int i = 0; i < drag.Rows.Length; i++)
        {
            if (i == drag.From) continue;
            int slot = i;
            if (i > drag.From && i <= target) slot--;
            else if (i < drag.From && i >= target) slot++;
            AnimateRow(drag.Rows[i], (slot - i) * AppRowHeight);
        }
    }

    internal void EndAppDrag(bool commit, bool animate = true)
    {
        if (_appDrag is not { } drag || _appRows is null) return;
        _appDrag = null;
        if (drag.Handle.IsMouseCaptured) drag.Handle.ReleaseMouseCapture();
        var positions = drag.Rows.Select((row, i) => (row, y: i * AppRowHeight + RowTransform(row).Y)).ToArray();
        bool changed = commit && drag.From != drag.Target;
        if (changed)
        {
            _appRows.Children.Remove(drag.Row);
            _appRows.Children.Insert(drag.Target, drag.Row);
            UpdateDividers();
        }
        foreach (var (row, y) in positions)
        {
            SetRowOffset(row, y - _appRows.Children.IndexOf(row) * AppRowHeight);
            AnimateRow(row, 0, () => { if (_appDrag?.Row != row) ResetDragAppearance(row); }, animate);
        }
        if (changed)
        {
            var order = _appRows.Children.OfType<Border>().Where(b => b.Tag is AppEntry).Select(b => ((AppEntry)b.Tag).InstanceId).ToArray();
            Change(s => s.Apps = order.Select(id => s.Entry(id)!).ToList());
        }
    }
}
