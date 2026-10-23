using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Native;
using Microsoft.Win32;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    // The apps that keep the panel's style while they cover the screen. Adding from the running apps is the shortest path:
    // the app that causes the problem is usually in front of the user, and its executable name is not obvious.
    private void FullscreenExceptions()
    {
        SectionTitle(Loc.T("全屏例外", "Full-screen exceptions"));
        var panel = new StackPanel();
        var help = TextLine(Loc.T("名单内的应用处于全屏或最大化状态时，面板保持当前样式：不隐藏，也不切换为指示条。",
            "While an app in this list is full screen or maximized, the panel keeps its style: it neither hides nor turns into the indicator."), 11.5, TextTertiary);
        help.TextWrapping = TextWrapping.Wrap;
        help.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(help);
        if (S.FullscreenExceptions.Count == 0)
        {
            var empty = TextLine(Loc.T("尚未添加例外应用。", "No exceptions yet."), 12.5, TextTertiary);
            empty.Margin = new Thickness(0, 4, 0, 8);
            panel.Children.Add(empty);
        }
        else
        {
            foreach (var exception in S.FullscreenExceptions)
            {
                if (panel.Children[^1] is Grid) panel.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
                panel.Children.Add(ExceptionRow(exception));
            }
        }
        var add = new Button { Content = Loc.T("添加应用…", "Add app…"), Style = (Style)FindResource("SettingsButton"), HorizontalAlignment = HorizontalAlignment.Left };
        add.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetName(add, Loc.T("添加全屏例外应用", "Add a full-screen exception"));
        add.Click += (_, _) => ExceptionMenu(add);
        panel.Children.Add(add);
        Group(panel);
    }

    private FrameworkElement ExceptionRow(FullscreenException exception)
    {
        var row = new Grid { MinHeight = 44, Margin = new Thickness(0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 12, 8) };
        labels.Children.Add(TextLine(exception.Name, 13, TextPrimary, FontWeights.SemiBold));
        if (exception.Path.Length > 0)
        {
            var path = TextLine(exception.Path, 11.5, TextTertiary);
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.ToolTip = exception.Path;
            labels.Children.Add(path);
        }
        row.Children.Add(labels);
        var remove = new Button { Style = (Style)FindResource("SettingsGlyphButton"), Width = 24, Height = 26, Padding = new Thickness(0) };
        var cross = Stroke("M 0,0 L 8,8 M 8,0 L 0,8", 8, 8, TextSecondary);
        cross.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        remove.Content = cross;
        AutomationProperties.SetName(remove, Loc.T("移除 ", "Remove ") + exception.Name);
        remove.Click += (_, _) =>
        {
            Change(s => s.FullscreenExceptions.RemoveAll(entry => string.Equals(ScreenPolicy.Key(entry), ScreenPolicy.Key(exception), StringComparison.OrdinalIgnoreCase)));
            ShowPage(0);
        };
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);
        return row;
    }

    // Runs as the button's menu, so the menu is anchored to the button that opened it.
    private void ExceptionMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var apps = RunningApps.WithWindows()
            .Where(app => !S.FullscreenExceptions.Any(exception => ScreenPolicy.Matches(exception, app.Name, app.Path)))
            .ToList();
        foreach (var app in apps)
        {
            var item = new MenuItem { Header = app.Name, ToolTip = app.Path.Length > 0 ? app.Path : null };
            item.Click += (_, _) => AddException(new FullscreenException(app.Name, app.Path));
            menu.Items.Add(item);
        }
        if (apps.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("没有可添加的应用", "No apps to add"), IsEnabled = false });
        menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = Loc.T("浏览可执行文件…", "Browse for an executable…") };
        browse.Click += (_, _) => BrowseException();
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }

    private void BrowseException()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("选择要豁免的应用", "Choose an app to exempt"),
            Filter = Loc.T("应用程序 (*.exe)|*.exe", "Applications (*.exe)|*.exe"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        AddException(new FullscreenException(System.IO.Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName));
    }

    private void AddException(FullscreenException exception)
    {
        Change(s => s.FullscreenExceptions.Add(exception));
        ShowPage(0);
    }
}
