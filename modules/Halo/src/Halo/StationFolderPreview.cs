using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal sealed partial class StationWindow
{
    const int FolderPreviewLimit = 6;

    async Task PreviewFolder(StationItem item, StationItem folderRoot, GridView grid, Func<Task> open)
    {
        string name = Path.GetFileName(item.Value);
        if (name.Length == 0) name = item.Value;
        var heading = PanelTheme.Title(name);
        heading.TextWrapping = TextWrapping.Wrap;
        heading.TextTrimming = TextTrimming.None;
        var icon = new Image { Width = 24, Height = 24 };
        var fallback = PanelTheme.Glyph("\uE8B7", 18);
        var sample = new Grid { VerticalAlignment = VerticalAlignment.Top, Children = { fallback, icon } };
        var header = new Grid { ColumnSpacing = 8, Children = { sample, heading } };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(heading, 1);

        var entries = new StackPanel { Spacing = 4 };
        try {
            var children = await Task.Run(() => App.Station.EnumerateFolderContents(folderRoot, item.Value, default)
                .Take(FolderPreviewLimit + 1).ToArray());
            foreach (var child in children.Take(FolderPreviewLimit)) {
                var glyph = PanelTheme.Glyph(child is DirectoryInfo ? "\uE8B7" : "\uE8A5", 14);
                var text = new TextBlock { Text = child.Name, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTipService.SetToolTip(text, child.Name);
                var row = new Grid { ColumnSpacing = 8, Children = { glyph, text } };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(text, 1);
                entries.Children.Add(row);
            }
            if (children.Length == 0) entries.Children.Add(PanelTheme.Hint("此文件夹为空"));
            if (children.Length > FolderPreviewLimit) entries.Children.Add(PanelTheme.Hint("还有更多项目"));
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            entries.Children.Add(PanelTheme.Hint("无法读取文件夹内容：" + error.Message));
        }

        var path = PanelTheme.Hint(item.Value);
        path.TextWrapping = TextWrapping.NoWrap;
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        path.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(path, item.Value);
        var copyPath = new Button { Content = "复制路径", MinWidth = 80 };
        PanelTheme.StyleTextAction(copyPath);
        copyPath.Click += (_, _) => {
            try { CopyItemPaths([item]); copyPath.Content = "已复制"; }
            catch (Exception error) { App.Log(error); copyPath.Content = "复制失败"; }
        };
        var pathRow = new Grid { ColumnSpacing = 8, Children = { path, copyPath } };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(copyPath, 1);
        var body = new StackPanel { Spacing = 8, Children = { entries, pathRow } };
        var dialog = new ContentDialog {
            Title = header, Content = new ScrollViewer { Content = body, MaxHeight = 280, IsTabStop = false, HorizontalScrollMode = ScrollMode.Disabled },
            CloseButtonText = "关闭", PrimaryButtonText = "打开文件夹",
        };
        AutomationProperties.SetName(dialog, $"快速预览 · {name}");
        PanelTheme.CompactDialog(dialog);
        await FolderIcon.Apply(icon, App.Station.FolderColorOverride(item) ?? App.Appearance.FolderColor, fallback);
        if (await ShowStationDialog(dialog, grid) == ContentDialogResult.Primary) await open();
    }
}
