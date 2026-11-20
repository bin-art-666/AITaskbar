using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Halo;

internal sealed partial class StationWindow
{
    void MenuAction(IList<MenuFlyoutItemBase> entries, string text, Func<Task> action, bool enabled = true, string? shortcut = null) =>
        AddMenuCommand(entries, text, Run, action, enabled, shortcut);

    internal static void AddMenuCommand(IList<MenuFlyoutItemBase> entries, string text, Func<Func<Task>, Task> run,
        Func<Task> action, bool enabled = true, string? shortcut = null) => entries.Add(MenuCommand(text, run, action, enabled, shortcut));

    static MenuFlyoutItem MenuCommand(string text, Func<Func<Task>, Task> run, Func<Task> action, bool enabled, string? shortcut = null)
    {
        var command = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (shortcut != null) command.KeyboardAcceleratorTextOverride = shortcut;
        command.Click += async (_, _) => await run(action);
        return command;
    }
    internal static void MenuHeading(MenuFlyout menu, string text)
    {
        menu.Items.Add(new MenuFlyoutItem { Text = text, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
    }
    void SelectContextCard(GridViewItem card) {
        if (!files.SelectedItems.Contains(card)) SelectCard(card, false, false);
    }
    void ShowItemMenu(FrameworkElement target, ContextRequestedEventArgs args)
    {
        var menu = BuildItemMenu(Selected());
        if (menu.Items.Count == 0) return;
        if (args.TryGetPosition(target, out var point)) menu.ShowAt(target, new FlyoutShowOptions { Position = point }); else menu.ShowAt(target);
    }
    internal MenuFlyout BuildItemMenu(StationItem[] items)
    {
        var menu = Menu(); if (items.Length == 0) return menu;
        bool writable = !App.Station.ReadOnly, single = items.Length == 1;
        var first = items[0];
        bool storage = items.All(item => item.Kind is StationKind.File or StationKind.Folder);
        bool references = storage && items.All(item => !item.Owned);
        var commands = new ItemMenuCommands(
            OpenItem, item => PreviewItem(item),
            () => CopyDesktopItems(items),
            RevealStorage, OpenInExplorer,
            () => {
                CopyItemPaths(items);
                Notify(storage ? "已复制路径" : "已复制路径、链接和便笺内容（每项一行）");
                return Task.CompletedTask;
            }, StationStore.CanMoveLibraryItems(items) ? () => PickLibraryDestination(App.Station.LibraryRoot, items, files) : null);
        async Task OwnReference() { await App.Station.OwnReference(first.Id); Notify("已保存独立副本，原文件不变"); }
        Task TrashItems() => TrashDesktopItems(items);
        if (items.All(item => item.Kind == StationKind.Folder)) {
            PopulateFolderMenu(menu, items, Run, commands, new(RenameItem,
                item => ChooseFolderColor(item, null, files),
                async item => {
                    int count = await App.Station.DissolveFolder(item.Id);
                    Notify(count == 0 ? "空文件夹已移到 Halo 回收站" : $"已解散文件夹，{count} 项已移到中转站，空文件夹已移到回收站");
                }, TrashItems, OwnReference), writable, writable, allowReferenceRemoval: true);
            return menu;
        }
        if (items.All(item => item.Kind == StationKind.File)) {
            PopulateFileMenu(menu, App.Settings.Data.EffectiveFileMenu, items, Run, commands, RenameItem, TrashItems, writable, OwnReference);
            return menu;
        }
        PopulateCommonItemMenu(menu, items, Run, commands, writable);
        if (commands.Move != null) MenuAction(menu.Items, "移动到…", commands.Move, writable);
        if (single && (references || first.Owned || first.Kind is StationKind.Text or StationKind.Link)) {
            menu.Items.Add(new MenuFlyoutSeparator());
            if (references) MenuAction(menu.Items, "保存为 Halo 副本（保留位置）", OwnReference, writable);
            else {
                MenuAction(menu.Items, "重命名…", () => RenameItem(first), writable, "F2");
                if (first.Kind == StationKind.Link) MenuAction(menu.Items, "编辑链接…", () => EditDesktopText(first), writable);
            }
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        string remove = references ? single ? "移除引用" : $"移除 {items.Length} 个引用" : single ? "移到 Halo 回收站" : $"将所选 {items.Length} 项移到回收站";
        MenuAction(menu.Items, remove, TrashItems, writable, "Delete");
        return menu;
    }
    internal sealed record ItemMenuCommands(Func<StationItem, Task> Open, Func<StationItem, Task> Preview,
        Func<Task> Copy, Func<StationItem, Task> Reveal, Func<StationItem, Task> OpenInExplorer, Func<Task> CopyPaths, Func<Task>? Move = null);

    async Task CopyDesktopItems(StationItem[] items)
    {
        var package = new DataPackage();
        await StationTransfer.FillMany(package, items, App.Station.AttachmentDirectory);
        Clipboard.SetContent(package);
        Notify($"已复制 {items.Length} 项");
    }

    async Task TrashDesktopItems(StationItem[] items)
    {
        await App.Station.RemoveArchived(items);
        undo = items;
        Notify(items.All(item => !item.Owned && item.Kind is StationKind.File or StationKind.Folder)
            ? "引用已移到 Halo 回收站，原文件不变" : "所选内容已移到 Halo 回收站，可恢复",
            undoLabel: $"撤销移除 · {items.Length} 项", undoAction: () => UndoDesktopRemoval(RecoverableDesktopUndo()));
    }

    StationItem[] RecoverableDesktopUndo() =>
        undo.Where(item => App.Station.Trash.Any(trashed => trashed.Id == item.Id)).ToArray();

    async Task UndoDesktopRemoval(StationItem[] recoverable)
    {
        if (recoverable.Length == 0) return;
        await App.Station.Restore(recoverable);
        undo = [];
        Notify($"已恢复 {recoverable.Length} 项");
    }

    internal static void PopulateCommonItemMenu(MenuFlyout menu, StationItem[] items, Func<Func<Task>, Task> run,
        ItemMenuCommands commands, bool writable)
    {
        if (items.Length == 0) return;
        bool single = items.Length == 1;
        var first = items[0];
        bool storage = items.All(item => item.Kind is StationKind.File or StationKind.Folder);
        bool references = storage && items.All(item => !item.Owned);
        string kind = first.Kind switch { StationKind.Folder => "文件夹", StationKind.Link => "链接", StationKind.Text => "便笺", _ => "文件" };
        MenuHeading(menu, single ? $"{(references ? "原文件引用" : kind)}操作" : $"已选 {items.Length} 项");
        if (single) {
            string open = first.Kind switch { StationKind.Text => "编辑便笺…", StationKind.Link => "打开链接", StationKind.Folder => "打开文件夹", _ => "打开文件" };
            AddMenuCommand(menu.Items, references ? "打开引用…" : open, run, () => commands.Open(first), first.Kind != StationKind.Text || writable, "Enter");
            AddMenuCommand(menu.Items, "快速预览  空格", run, () => commands.Preview(first));
        }
        string copy = !single ? $"复制所选 {items.Length} 项" : first.Kind switch { StationKind.Text => "复制内容", StationKind.Link => "复制链接", StationKind.Folder => "复制文件夹", _ => "复制文件" };
        AddMenuCommand(menu.Items, copy, run, commands.Copy, shortcut: "Ctrl+C");
        if (single && storage) AddMenuCommand(menu.Items, references ? "在原文件夹中显示" : "在文件夹中显示", run, () => commands.Reveal(first));
        if (storage || !single && items.Select(item => item.Kind).Distinct().Count() > 1)
            AddMenuCommand(menu.Items, storage ? references ? "复制原路径" : "复制路径" : "复制路径与文本", run, commands.CopyPaths);
    }

    internal static void CopyItemPaths(StationItem[] items)
    {
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, items.Select(item => item.Value)));
        Clipboard.SetContent(package);
    }

    internal static async Task RevealStorage(StationItem item)
    {
        var storage = await StationTransfer.Storage(item);
        var folder = storage is StorageFile file ? await file.GetParentAsync() : await ((StorageFolder)storage).GetParentAsync();
        if (folder == null) throw new IOException("该项目没有可显示的上级文件夹。");
        var options = new FolderLauncherOptions(); options.ItemsToSelect.Add(storage);
        if (!await Launcher.LaunchFolderAsync(folder, options)) throw new IOException("无法打开所在文件夹。");
    }
    void ShowBackgroundMenu(ContextRequestedEventArgs args)
    {
        ClearDesktopSelection();
        var menu = BuildBackgroundMenu();
        if (args.TryGetPosition(files, out var point)) menu.ShowAt(files, new FlyoutShowOptions { Position = point }); else menu.ShowAt(files);
    }
    internal MenuFlyout BuildBackgroundMenu()
    {
        var menu = Menu(); bool writable = !App.Station.ReadOnly, hasItems = files.Items.Count > 0;
        MenuHeading(menu, "Halo 全局操作");
        var create = new MenuFlyoutSubItem { Text = "新建" };
        MenuAction(create.Items, "文件夹…", NewDesktopFolder, writable, "Ctrl+Shift+N"); MenuAction(create.Items, "文字便笺…", NewDesktopNote, writable); menu.Items.Add(create);
        MenuAction(create.Items, "Markdown 便笺…", () => EditNote(null, StationTextStyle.Markdown), writable);
        MenuAction(create.Items, "代码片段…", () => EditNote(null, StationTextStyle.Code), writable);
        MenuAction(menu.Items, "从剪贴板粘贴  Ctrl+V", PasteContent, writable);
        MenuAction(menu.Items, "搜索…  Ctrl+F", () => { ShowSearch(); return Task.CompletedTask; });
        menu.Items.Add(new MenuFlyoutSeparator());
        var sorting = new MenuFlyoutSubItem { Text = "排序" };
        foreach (var (mode, label) in new[] { (StationSort.Manual, "手动排序（保留空位）"), (StationSort.Newest, "最新在前"), (StationSort.Oldest, "最早在前") }) {
            var option = new ToggleMenuFlyoutItem { Text = label, IsChecked = App.Station.Sort(App.Settings.Data.NewestFirst) == mode, IsEnabled = writable };
            option.Click += async (_, _) => await Run(() => App.Station.SetSort(mode, App.Settings.Data.NewestFirst)); sorting.Items.Add(option);
        }
        menu.Items.Add(sorting);
        if (!Searching && App.Station.Sort(App.Settings.Data.NewestFirst) == StationSort.Manual)
            MenuAction(menu.Items, "整理空位（当前布局）", () => App.Station.ArrangeGrid(detached, GridColumns(), App.Settings.Data.NewestFirst), hasItems && writable);
        MenuAction(menu.Items, "全选", () => { files.SelectAll(); return Task.CompletedTask; }, hasItems, "Ctrl+A");
        menu.Items.Add(new MenuFlyoutSeparator());
        MenuAction(menu.Items, "Halo 回收站…", ShowTrash);
        var recoverable = RecoverableDesktopUndo();
        if (LatestLibraryMove is { } move) MenuAction(menu.Items, move.Label, UndoLibraryMove, writable, "Ctrl+Z");
        else if (recoverable.Length > 0) MenuAction(menu.Items, "撤销上次移除", () => UndoDesktopRemoval(recoverable), writable, "Ctrl+Z");
        var management = new MenuFlyoutSubItem { Text = "桌面管理" };
        MenuAction(management.Items, "备份桌面…", BackupDesktop);
        MenuAction(management.Items, "恢复桌面备份…", RestoreDesktop, writable);
        management.Items.Add(new MenuFlyoutSeparator());
        MenuAction(management.Items, "重试未完成的拖入", () => App.Current.DrainDrops(retryFailed: true), writable);
        management.Items.Add(new MenuFlyoutSeparator());
        MenuAction(management.Items, "全部移到回收站…", MoveAllToTrash, App.Station.Items.Count > 0 && writable);
        menu.Items.Add(management);
        menu.Items.Add(new MenuFlyoutSeparator());
        MenuAction(menu.Items, "图标、间距与外观…", () => { App.OpenSettings(); return Task.CompletedTask; });
        var windowOptions = new MenuFlyoutSubItem { Text = "窗口选项" };
        var autoHide = new ToggleMenuFlyoutItem { Text = "独立窗口失焦自动收起", IsChecked = App.Settings.Data.DetachedHideOnBlur, IsEnabled = writable };
        autoHide.Click += (_, _) => App.Settings.SaveSoon(App.Settings.Data with { DetachedHideOnBlur = autoHide.IsChecked });
        windowOptions.Items.Add(autoHide); menu.Items.Add(windowOptions);
        return menu;
    }
    async Task MoveAllToTrash()
    {
        var snapshot = App.Station.Items.ToArray();
        if (await new ContentDialog { XamlRoot = root.XamlRoot, Title = $"将全部 {snapshot.Length} 项移到回收站？", Content = "Halo 内容可从回收站恢复，原文件不会删除。", PrimaryButtonText = "移到回收站", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowAsync() != ContentDialogResult.Primary) return;
        await App.Station.RemoveArchived(snapshot); undo = snapshot; Notify("全部内容已移到 Halo 回收站，可恢复");
    }
}
