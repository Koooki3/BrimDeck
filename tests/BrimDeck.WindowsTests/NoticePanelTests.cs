using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;

// The notices page's own panels, laid out by WPF as the page lays them out (src/BrimDeck/NoticePanels.cs): the list's header keeps
// its count (and the pause time in it) at any width, a row keeps its title readable and places its time the same way in every
// row, and the fades at the foot of a column are masks rather than colors laid over it.
internal static class NoticePanelTests
{
    private static TextBlock Text(string text, double size, bool wrap = false)
        => new() { Text = text, FontSize = size, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };

    private static Rect Lay(Panel panel, double width, out Size desired)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        desired = panel.DesiredSize;
        panel.Arrange(new Rect(0, 0, width, desired.Height));
        return new Rect(0, 0, width, desired.Height);
    }

    private static Rect Box(UIElement element) => new(element.TranslatePoint(new Point(), (UIElement)System.Windows.Media.VisualTreeHelper.GetParent(element)), element.RenderSize);

    public static void Run(Action<string, bool> check)
    {
        // ----- The header: title, count, tools -----
        NoticeHead Head(string count)
        {
            var head = new NoticeHead();
            var title = Text("通知", 15); title.Height = 20;
            var tools = new Border { Width = 112, Height = 20 };
            var counter = Text(count, 11.5, wrap: true); counter.LineHeight = NoticeLines.CountLine; counter.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            head.Children.Add(title); head.Children.Add(counter); head.Children.Add(tools);
            return head;
        }
        const string paused = "今天 8 条 · 暂停至 17:46";
        var wide = Head(paused); Lay(wide, 400, out var wideSize);
        check("A header that fits is one line, the count beside the title and the tools at the right",
            wideSize.Height == NoticeLines.HeadLine && wide.Children[1].RenderSize.Width > 0 && Box(wide.Children[1]).Left >= Box(wide.Children[0]).Right
            && Math.Abs(Box(wide.Children[2]).Right - 400) < .5 && Box(wide.Children[1]).Right <= Box(wide.Children[2]).Left);
        var narrow = Head(paused); Lay(narrow, 170, out var narrowSize);
        var (nTitle, nCount, nTools) = (narrow.Children[0], narrow.Children[1], narrow.Children[2]);
        check("A header that does not fit puts the count, with the pause time whole, under the title and the tools",
            narrowSize.Height > NoticeLines.HeadLine && Box(nCount).Top >= NoticeLines.HeadLine && Box(nCount).Left == 0
            && ((TextBlock)nCount).Text.Contains("17:46") && Box(nCount).Width <= 170.5 && Box(nTitle).Right <= Box(nTools).Left
            && Math.Abs(Box(nTools).Right - 170) < .5 && Box(nTools).Top == 0 && nCount.RenderSize.Height <= NoticeLines.CountLine * NoticeLines.CountLines + .5);
        var english = Head("8 today · paused until 17:46"); Lay(english, 150, out var englishSize);
        check("An English count that is too long for one line wraps under the tools, at most two lines",
            englishSize.Height > NoticeLines.HeadLine && englishSize.Height <= NoticeLines.HeadLine + NoticeLines.HeadGap + NoticeLines.CountLine * NoticeLines.CountLines + .5);

        // ----- A row: title, time, preview -----
        NoticeRowTexts Row(string title, string time)
        {
            var row = new NoticeRowTexts();
            var t = Text(title, 12.5); t.Height = NoticeLines.RowTitleLine; t.FontWeight = FontWeights.SemiBold;
            var when = Text(time, 11); when.Height = NoticeLines.RowTitleLine;
            var preview = Text("E1-S23 两条训练队列运行中，预计 11-01 早上完成", 12); preview.LineHeight = NoticeLines.RowPreviewLine;
            row.Children.Add(t); row.Children.Add(when); row.Children.Add(preview);
            return row;
        }
        var roomy = Row("Claude Code · ocbench-gen", "15 分钟前"); Lay(roomy, 170, out var roomySize);
        var (rTitle, rTime, rPreview) = (roomy.Children[0], roomy.Children[1], roomy.Children[2]);
        check("A row wide enough has its time at the right of the title and the preview on its own line",
            Math.Abs(Box(rTime).Right - 170) < .5 && Box(rTime).Top == 0 && Box(rTitle).Right <= Box(rTime).Left && Box(rTitle).Width >= NoticeLines.RowTitleMinimum - .5
            && Box(rPreview).Top >= NoticeLines.RowTitleLine && Box(rPreview).Left == 0 && Math.Abs(Box(rPreview).Width - 170) < .5 && roomySize.Height == 36);
        var tight = Row("Claude Code · ocbench-gen", "15 分钟前"); Lay(tight, 120, out var tightSize);
        var (tTitle, tTime, tPreview) = (tight.Children[0], tight.Children[1], tight.Children[2]);
        check("A row too narrow for the title and the time moves the time to the front of the preview, and the title keeps the whole line",
            Math.Abs(Box(tTitle).Width - 120) < .5 && Box(tTime).Top >= NoticeLines.RowTitleLine && Box(tTime).Left == 0 && Box(tPreview).Left >= Box(tTime).Right
            && Box(tPreview).Top >= NoticeLines.RowTitleLine && Box(tPreview).Right <= 120.5 && tightSize.Height == 36);
        // The choice depends on the width alone: a short time and a long one, a short title and a long one, are laid out alike.
        bool consistent = true;
        foreach (double width in new[] { 110, 130, 146, 160, 200 })
        {
            var a = Row("短", "1 分钟前"); var b = Row("A very long title that has to be trimmed at the end of the line", "45 minutes ago");
            Lay(a, width, out _); Lay(b, width, out _);
            consistent &= (Box(a.Children[1]).Top == 0) == (Box(b.Children[1]).Top == 0);
        }
        check("Every row of a list puts its time in the same place, whatever its words", consistent);
        check("The list's own minimum width leaves a row room for its time beside a title of at least 88 DIP",
            NoticeLines.TimeBesideTitle(146) && !NoticeLines.TimeBesideTitle(145) && NoticeLines.RowTitleMinimum == 88);

        // ----- The focus's buttons: delete, two actions, the main one -----
        NoticeButtons Buttons(bool main = true)
        {
            var row = new NoticeButtons { MainLast = main };
            row.Children.Add(new Border { Width = 32, Height = 32, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new Border { Width = 60, Height = 32, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new Border { Width = 60, Height = 32, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new Border { Width = 100, Height = 32 });
            return row;
        }
        var oneLine = Buttons(); Lay(oneLine, 400, out var oneSize);
        check("Buttons that fit are one line, the main one at the right end",
            oneSize.Height == 32 && Math.Abs(Box(oneLine.Children[3]).Right - 400) < .5 && Box(oneLine.Children[0]).Left == 0 && Box(oneLine.Children[2]).Right <= Box(oneLine.Children[3]).Left);
        var twoLines = Buttons(); Lay(twoLines, 178, out var twoSize);
        check("Buttons that do not fit wrap to a second line instead of being cut off, the main one at the right end of its line",
            twoSize.Height == 32 + NoticeButtons.LineGap + 32 && Box(twoLines.Children[3]).Top == 32 + NoticeButtons.LineGap && Math.Abs(Box(twoLines.Children[3]).Right - 178) < .5
            && Enumerable.Range(0, 4).All(i => Box(twoLines.Children[i]).Right <= 178.5 && Box(twoLines.Children[i]).Left >= 0));
        var plain = Buttons(main: false); Lay(plain, 400, out _);
        check("Without a main button the last one stays with the others", Math.Abs(Box(plain.Children[3]).Right - 400) > 1 && Box(plain.Children[3]).Left == Box(plain.Children[2]).Right + 8);

        // ----- The fade at a column's foot: a mask on the viewer -----
        var viewer = new ScrollViewer { Content = new Border { Height = 600, Width = 100 }, Height = 200, Width = 100 };
        NoticeFade.Foot(viewer, 36);
        var window = new Window { Width = 300, Height = 300, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000, Content = viewer };
        try
        {
            window.Show();
            Pump();
            var mask = viewer.OpacityMask as LinearGradientBrush;
            bool shape = mask is { GradientStops.Count: 3 } && mask.GradientStops[0].Color.A == 255 && mask.GradientStops[1].Color.A == 255 && mask.GradientStops[2].Color.A == 0
                && mask.StartPoint == new Point(0, 0) && mask.EndPoint == new Point(0, 1);
            check("A column's foot fades by an opacity mask on the viewer, opaque down to the last 36 DIP and clear at the foot", shape
                && Math.Abs(mask!.GradientStops[1].Offset - (1 - 36 / viewer.ActualHeight)) < .001 && mask.GradientStops[2].Offset == 1);
            viewer.Height = 100; Pump();
            check("The fade follows the viewer's height", Math.Abs(((LinearGradientBrush)viewer.OpacityMask).GradientStops[1].Offset - (1 - 36 / viewer.ActualHeight)) < .001);
        }
        finally { window.Close(); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
