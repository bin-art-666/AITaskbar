using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal sealed partial class StationWindow
{
    internal void VerifyContextMenus(Action<string, bool> check)
    {
        static string[] Labels(IEnumerable<MenuFlyoutItemBase> entries) => entries.SelectMany(entry => entry switch {
            MenuFlyoutSubItem submenu => new[] { submenu.Text }.Concat(Labels(submenu.Items)),
            MenuFlyoutItem command => new[] { command.Text }, _ => Array.Empty<string>()
        }).ToArray();
        StationItem Item(StationKind kind, bool owned = false) => new(Guid.NewGuid(), kind, kind == StationKind.File ? "菜单验证.jpg" : "菜单验证",
            "菜单验证", DateTimeOffset.Now, owned);
        var global = Labels(BuildBackgroundMenu().Items);
        check("ContextGlobalOperationsGrouped", global.Contains("新建") && global.Contains("文件夹…") && global.Contains("文字便笺…") && global.Contains("桌面管理") && global.Contains("窗口选项"));
        check("ContextGlobalHasNoFileOrWindowCommands", !global.Contains("重命名…") && !global.Contains("复制文件") && !global.Contains("置顶") && !global.Contains("切换独立窗口"));
        check("ContextGlobalDestructiveActionNested", !BuildBackgroundMenu().Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "全部移到回收站…") && global.Contains("全部移到回收站…"));
        var file = Labels(BuildItemMenu([Item(StationKind.File, true)]).Items);
        check("ContextOwnedFileActions", file.Contains("打开文件") && file.Contains("复制文件") && file.Contains("重命名…")
            && file.Contains("在资源管理器中定位") && !file.Contains("在文件夹中显示") && !file.Contains("编辑链接…") && !file.Contains("桌面管理"));
        var pdf = Item(StationKind.File, true) with { Value = "示例.PDF" };
        var shortcut = Item(StationKind.File, true) with { Value = "应用.lnk" };
        var url = shortcut with { Value = "网页.url" };
        var unknown = Item(StationKind.File, true) with { Value = "压缩包.zip" };
        check("FileMenuMatchesPreviewCapability", Labels(BuildItemMenu([pdf]).Items).Contains("快速预览  空格")
            && Labels(BuildItemMenu([unknown]).Items).Contains("查看文件信息  空格")
            && new[] { shortcut, url }.All(item => Labels(BuildItemMenu([item]).Items)
                .Intersect(["打开快捷方式", "查看快捷方式信息  空格", "复制文件"]).Count() == 3));
        var batch = Labels(BuildItemMenu([pdf, shortcut]).Items);
        check("FileMenuMultiSelectOnlyShowsBatchOperations", batch.SequenceEqual(
            ["已选 2 项", "复制所选 2 项", MoreFolderActions, "复制路径", "将所选 2 项移到回收站", "移动到…"]));
        var folder = Labels(BuildItemMenu([Item(StationKind.Folder, true)]).Items);
        check("ContextFolderOpensDirectlyInExplorer", folder.Contains("打开文件夹") && !folder.Contains("在文件夹中显示")
            && folder.Count(label => label == "在资源管理器中打开") == 1 && folder.Contains("设置文件夹颜色"));
        check("ContextSpaceHintAppliesToFilePreviewOnly", folder.Contains("快速预览") && !folder.Contains("快速预览  空格")
            && file.Contains("快速预览  空格"));
        check("ContextDissolveAppliesToSingleOwnedFolder", folder.Contains("解散文件夹") && !file.Contains("解散文件夹")
            && !Labels(BuildItemMenu([Item(StationKind.Folder)]).Items).Contains("解散文件夹")
            && !Labels(BuildItemMenu([Item(StationKind.Folder, true), Item(StationKind.Folder, true)]).Items).Contains("解散文件夹"));
        var reference = Labels(BuildItemMenu([Item(StationKind.File)]).Items);
        check("LibraryMoveMenuOnlyAppliesToOwnedFileAndFolderSelections", file.Contains("移动到…") && folder.Contains("移动到…")
            && !reference.Contains("移动到…") && Labels(BuildItemMenu([Item(StationKind.File, true), Item(StationKind.Folder, true)]).Items).Contains("移动到…"));
        check("ContextReferenceOwnershipExplicit", reference.Contains("打开引用…") && reference.Contains("复制原路径") && reference.Contains("移除引用") && reference.Contains("保存为 Halo 副本（保留位置）") && !reference.Contains("重命名…"));
        var text = Labels(BuildItemMenu([Item(StationKind.Text)]).Items);
        check("ContextNoteHasOneEditAndCopy", text.Count(label => label.Contains("编辑")) == 1 && text.Contains("复制内容") && !text.Contains("复制路径") && !text.Contains("打开文件"));
        var link = Labels(BuildItemMenu([Item(StationKind.Link)]).Items);
        check("ContextLinkActions", link.Contains("打开链接") && link.Contains("复制链接") && link.Contains("编辑链接…") && !link.Contains("复制路径"));
        var mixed = Labels(BuildItemMenu([Item(StationKind.File, true), Item(StationKind.Text)]).Items);
        check("ContextMixedSelectionIsBatchOnly", mixed.Contains("已选 2 项") && mixed.Contains("复制所选 2 项") && mixed.Contains("复制路径与文本") && mixed.Contains("将所选 2 项移到回收站") && !mixed.Contains("重命名…") && !mixed.Any(label => label.StartsWith("打开")));
        check("ContextEmptySelectionHasNoCommands", BuildItemMenu([]).Items.Count == 0);
        var cards = files.Items.Cast<GridViewItem>().ToArray(); var previous = Selected().Select(item => item.Id).ToHashSet();
        SelectCard(cards[0], false, false); SelectCard(cards[1], true, false); SelectContextCard(cards[0]);
        check("ContextSelectedCardKeepsGroup", files.SelectedItems.Count == 2 && files.SelectedItems.Contains(cards[1]));
        SelectContextCard(cards[2]);
        check("ContextUnselectedCardTargetsOnlyItself", files.SelectedItems.Count == 1 && files.SelectedItems.Contains(cards[2]));
        ClearDesktopSelection();
        check("ContextBlankClearsSelection", files.SelectedItems.Count == 0 && selectionAnchor == null);
        foreach (var card in cards) if (previous.Contains(((StationItem)card.Tag).Id)) files.SelectedItems.Add(card);
    }
}
