using System.IO;
using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private sealed record FileShareTarget(string Name, int Count, Action Open);
    private WeTypeShareWindow? _weTypeShare;

    private List<FileShareTarget> FileShareTargets(IReadOnlyList<TransferFile> files)
    {
        List<FileShareTarget> targets = [];
        if (_app.ClipboardSync.Connected(false))
        {
            var paths = files.Select(file => file.Path).Where(File.Exists).ToArray();
            if (paths.Length > 0)
                targets.Add(new(Loc.T("通过微信输入法发送", "Send with WeType"), paths.Length, () =>
                {
                    if (_weTypeShare is { } existing)
                    {
                        existing.Activate();
                        ShowShelfNotice(Loc.T("请先关闭当前的文件发送窗口", "Close the current file transfer first"));
                        return;
                    }
                    if (!_app.ClipboardSync.Connected(false)) return;
                    var dialog = new WeTypeShareWindow(Settings.Theme, _app.ClipboardSync, paths, _app.Lifetime.Token);
                    _weTypeShare = dialog;
                    dialog.Owner = this;
                    dialog.Closed += (_, _) => _weTypeShare = null;
                    SetExpanded(false);
                    dialog.Show();
                    dialog.Activate();
                }));
        }
        var windows = Shareable(files);
        if (windows.Count > 0)
            targets.Add(new(Loc.T("分享到更多应用", "Share to more apps"), windows.Count, () => ShareFiles(windows)));
        return targets;
    }

    private List<MenuItem> ShareChoices(List<FileShareTarget> targets, int count) => targets.Select(target =>
        Item(count == target.Count ? target.Name : Loc.T($"{target.Name}（{target.Count} 项）", $"{target.Name} ({target.Count} items)"),
            target.Open, MenuIcons.Share)).ToList();

    private void AddShareMenu(ContextMenu menu, IReadOnlyList<TransferFile> files)
    {
        var targets = FileShareTargets(files);
        if (targets.Count == 0) return;
        int count = targets.Max(target => target.Count);
        var label = files.Count > 1 ? Loc.T($"分享所选 {count} 项", $"Share {count} items") : Loc.T("分享", "Share");
        menu.Items.Add(targets.Count == 1 ? Item(label + "…", targets[0].Open, MenuIcons.Share)
            : Submenu(label, MenuIcons.Share, ShareChoices(targets, files.Count)));
    }

    private void ShowShareChoices(TransferFile file, FrameworkElement anchor)
    {
        var targets = FileShareTargets([file]);
        if (targets.Count == 1) targets[0].Open();
        else if (targets.Count > 1)
        {
            var menu = NewMenu();
            foreach (var choice in ShareChoices(targets, 1)) menu.Items.Add(choice);
            OpenMenu(menu, anchor);
        }
    }
}
