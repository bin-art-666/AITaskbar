using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal sealed partial class SettingsPage
{
    readonly ListView primaryFileMenu = new();
    readonly ListView moreFileMenu = new();
    readonly Button fileMenuPreview = new() { Content = "预览文件菜单" };
    readonly RadioButtons menuTarget = new() { MaxColumns = 2 };
    readonly ComboBox fileMenuSample = new();

    FileMenuLayout DraftFileMenu => new(Actions<FileMenuAction>(primaryFileMenu), Actions<FileMenuAction>(moreFileMenu));

    void BuildItemMenuSettings(StackPanel body)
    {
        var files = new StackPanel { Spacing = 8 };
        var folders = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(menuTarget, "右键菜单类型");
        menuTarget.Items.Add("文件");
        menuTarget.Items.Add("文件夹");
        menuTarget.SelectionChanged += (_, _) => {
            files.Visibility = menuTarget.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            folders.Visibility = menuTarget.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        };
        menuTarget.SelectedIndex = 0;
        var menuHeading = Section("右键菜单");
        menuHeading.VerticalAlignment = VerticalAlignment.Center;
        body.Children.Add(AdaptivePair(menuHeading, menuTarget, 330, equalColumns: false));
        body.Children.Add(new TextBlock { Text = "拖动排序，或选中条目后编辑", Opacity = 0.72 });
        BuildFileMenuSettings(files);
        BuildFolderMenuSettings(folders);
        body.Children.Add(files);
        body.Children.Add(folders);
    }

    void BuildFileMenuSettings(StackPanel body)
    {
        foreach (string title in new[] { "普通文件", "快捷方式", "原文件引用", "多选文件", "未知格式" }) fileMenuSample.Items.Add(title);
        AutomationProperties.SetName(fileMenuSample, "预览文件类型");
        fileMenuSample.SelectedIndex = 0;
        fileMenuSample.SelectionChanged += (_, _) => FileMenuEdited();
        var restore = new Button { Content = "恢复默认文件菜单" };
        restore.Click += (_, _) => LoadFileMenu(FileMenuLayout.Default);
        RegisterButton(restore);
        RegisterButton(fileMenuPreview);
        BuildMenuEditor(body, primaryFileMenu, moreFileMenu, FileMenuAction.Separator, FileMenuEdited, "文件");
        body.Children.Add(AdaptivePair(ActionRow(fileMenuSample, fileMenuPreview), restore, 452));
    }

    void LoadFileMenu(FileMenuLayout layout)
    {
        LoadMenuLayer(primaryFileMenu, layout.Primary, FileMenuAction.Separator, StationWindow.FileMenuLabel);
        LoadMenuLayer(moreFileMenu, layout.More, FileMenuAction.Separator, StationWindow.FileMenuLabel);
        FileMenuEdited();
    }

    void FileMenuEdited()
    {
        static StationItem Sample(string name, bool owned = true) =>
            new(Guid.NewGuid(), StationKind.File, name, "", DateTimeOffset.Now, owned);
        StationItem[] items = fileMenuSample.SelectedIndex switch {
            1 => [Sample("示例.lnk")],
            2 => [Sample("原文件.pdf", owned: false)],
            3 => [Sample("图片.jpg"), Sample("文档.pdf")],
            4 => [Sample("文件.unknown")],
            _ => [Sample("图片.jpg")],
        };
        var preview = new MenuFlyout();
        // The settings preview uses the real menu rules with inert actions.
        Task Nothing() => Task.CompletedTask;
        Task Ignore(StationItem _) => Task.CompletedTask;
        StationWindow.PopulateFileMenu(preview, DraftFileMenu, items, _ => Task.CompletedTask,
            new(Ignore, Ignore, Nothing, Ignore, Ignore, Nothing, Nothing), Ignore, Nothing, writable: true, ownReference: Nothing);
        fileMenuPreview.Flyout = preview;
        UpdateSaveState();
    }

    internal (ListView Primary, ListView More, ComboBox Sample) FileMenuControlsForVerification =>
        (primaryFileMenu, moreFileMenu, fileMenuSample);
}
