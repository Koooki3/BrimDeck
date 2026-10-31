using System.Windows;
using System.Windows.Controls;
using BrimDeck;

// The settings window's keyboard focus rules that need real WPF elements: a radio group's one Tab stop, and the row whose focused
// control folds away. Nothing is shown on the screen (the window is off it, unactivated) and no keyboard input is sent.
internal static class SettingsFocusTests
{
    public static void Run(Action<string, bool> check)
    {
        // A group is one Tab stop: the chosen option, or the first when none is chosen.
        var group = new StackPanel();
        var one = new RadioButton { GroupName = "g" }; var two = new RadioButton { GroupName = "g" }; var three = new RadioButton { GroupName = "g" };
        foreach (var radio in new[] { one, two, three }) group.Children.Add(radio);
        RadioGroups.SetEnabled(group, true);
        bool Stops(params bool[] expected) => new[] { one, two, three }.Select(radio => System.Windows.Input.KeyboardNavigation.GetIsTabStop(radio)).SequenceEqual(expected);
        two.IsChecked = true;
        check("A radio group's Tab stop is its chosen option", Stops(false, true, false));
        two.IsChecked = false;
        check("When the chosen option is cleared (both presets off) the Tab stop goes back to the first option", Stops(true, false, false));
        three.IsChecked = true;
        check("A later choice takes the Tab stop again", Stops(false, false, true));

        var window = new Window { Width = 200, Height = 100, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
        var handle = new Border { Focusable = true }; var field = new TextBox(); var details = new StackPanel(); details.Children.Add(field);
        var line = new Grid(); line.Children.Add(handle);
        var inner = new StackPanel(); inner.Children.Add(line); inner.Children.Add(details);
        var row = new Border { Child = inner };
        var outside = new TextBox(); var host = new StackPanel(); host.Children.Add(row); host.Children.Add(outside);
        window.Content = host;
        try
        {
            window.Show();
            check("A focused control that is still shown keeps the focus where it is", !RowFocus.Orphaned(row, field));
            details.Visibility = Visibility.Collapsed;
            check("A focused control inside the row that folds away has lost the focus, and is for the handle to take", RowFocus.Orphaned(row, field));
            check("The handle, which stays shown, is not orphaned", !RowFocus.Orphaned(row, handle));
            outside.Visibility = Visibility.Collapsed;
            check("A control outside the row is not the row's to rescue", !RowFocus.Orphaned(row, outside));
            check("No focus at all is left alone", !RowFocus.Orphaned(row, null));
        }
        finally { window.Close(); }
    }
}
