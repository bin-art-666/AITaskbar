using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal sealed partial class StationWindow
{
    async Task VerifyFileMenuSettings(SettingsPage page, Action<string, bool> check, Action<Button> click)
    {
        var initial = App.Settings.Data.EffectiveFileMenu;
        var folders = App.Settings.Data.EffectiveFolderMenu;
        var (primary, more, sample) = page.FileMenuControlsForVerification;
        page.ShowFolderMenuSettingsForVerification(showFiles: true);
        await Task.Delay(100);
        var preview = page.FindButton("预览文件菜单");
        var file = new StationItem(Guid.NewGuid(), StationKind.File, "图片.jpg", "", DateTimeOffset.Now, true);
        static string[] Labels(IEnumerable<MenuFlyoutItemBase> rows) => rows.Select(row => row switch {
            MenuFlyoutItem item => item.Text, MenuFlyoutSeparator => "|", MenuFlyoutSubItem submenu => submenu.Text, _ => "",
        }).ToArray();
        string[] Primary(MenuFlyout menu) => Labels(menu.Items.Skip(2));
        string[] Overflow(MenuFlyout menu) => Labels(menu.Items.OfType<MenuFlyoutSubItem>().Single().Items);
        MenuFlyout Preview() => (MenuFlyout)preview.Flyout;
        void Select(ListView list, FileMenuAction action) =>
            list.SelectedItem = list.Items.Cast<MenuLayoutRow>().First(row => Equals(row.Action, action));
        void Press(string name) => click(page.FolderMenuButtonForVerification("文件 " + name));
        check("LegacySettingsUseDefaultFileMenu", System.Text.Json.JsonSerializer.Deserialize<HaloSettings>("{\"Version\":1}")!.EffectiveFileMenu
            == FileMenuLayout.Default);
        check("FileMenuDefaultGroupsAndOverflow", Primary(Preview()).SequenceEqual(
            ["打开文件", "快速预览  空格", "复制文件", "|", "重命名…", MoreFolderActions])
            && Overflow(Preview()).SequenceEqual(["在资源管理器中定位", "复制路径", "|", "移到 Halo 回收站", "移动到…"]));
        sample.SelectedIndex = 1;
        check("FileMenuSettingsPreviewMatchesShortcut", Primary(Preview()).Contains("打开快捷方式")
            && Primary(Preview()).Contains("查看快捷方式信息  空格"));
        sample.SelectedIndex = 2;
        check("FileMenuSettingsPreviewMatchesReference", Primary(Preview()).Contains("打开引用…")
            && !Primary(Preview()).Contains("重命名…") && Overflow(Preview()).Contains("保存为 Halo 副本（保留位置）"));
        sample.SelectedIndex = 3;
        check("FileMenuSettingsPreviewMatchesMultiSelect", Primary(Preview()).SequenceEqual(["复制所选 2 项", MoreFolderActions]));
        sample.SelectedIndex = 4;
        check("FileMenuSettingsPreviewMatchesUnknownFormat", Primary(Preview()).Contains("查看文件信息  空格"));
        sample.SelectedIndex = 0;
        void Edit()
        {
            Select(more, FileMenuAction.CopyPath);
            Press("移到一级菜单");
            Press("上移");
            Select(primary, FileMenuAction.CopyPath);
            Press("添加分隔线");
        }
        Edit();
        string[] changed = ["打开文件", "快速预览  空格", "复制文件", "|", "复制路径", "|", "重命名…", MoreFolderActions];
        check("FileMenuDraftUpdatesPreviewOnly", Primary(Preview()).SequenceEqual(changed)
            && !Primary(BuildItemMenu([file])).Contains("复制路径") && App.Settings.Data.EffectiveFileMenu == initial);
        click(page.FindButton("取消"));
        check("FileMenuCancelRestoresLayout", !Primary(Preview()).Contains("复制路径") && App.Settings.Data.EffectiveFileMenu == initial);
        Edit();
        click(page.FindButton("保存"));
        await page.PendingSave;
        await App.Settings.Flush();
        var reloaded = new SettingsStore(Path.Combine(App.DataDirectory, "settings.json")).Data;
        check("FileMenuSavePersistsWithoutChangingFolderMenu", reloaded.EffectiveFileMenu != initial
            && reloaded.EffectiveFileMenu == App.Settings.Data.EffectiveFileMenu
            && reloaded.EffectiveFolderMenu == folders
            && Primary(BuildItemMenu([file])).SequenceEqual(changed));
        click(page.FindButton("恢复默认文件菜单"));
        click(page.FindButton("保存"));
        await page.PendingSave;
        check("FileMenuResetRestoresDefaults", App.Settings.Data.EffectiveFileMenu == FileMenuLayout.Default);
        page.ShowFolderMenuSettingsForVerification();
    }
}
