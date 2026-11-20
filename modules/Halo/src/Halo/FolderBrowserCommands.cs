using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Halo;

internal sealed partial class FolderBrowser
{
    bool CanChangeView => !closed && !busy && !dragging && !localDrag && directory != null;
    bool CanWriteFolder => CanChangeView && folder.Owned && !store.ReadOnly;

    void InitializeCommands()
    {
        files.ContextRequested += (_, e) => {
            if (e.Handled || selectionMarquee.IsActive || !StationWindow.ContentBlankTarget(e.OriginalSource as DependencyObject, files)) return;
            e.Handled = true;
            ClearSelection();
            ShowMenu(BuildDirectoryMenu(), files, e);
        };
    }

    internal void HandleShortcut(KeyRoutedEventArgs e, DependencyObject? source, VirtualKeyModifiers modifiers)
    {
        if (localDrag || dragging || closed) return;
        if (e.Key == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None) {
            e.Handled = true;
            if (e.KeyStatus.WasKeyDown || busy) return;
            if (folderSearch.Visibility == Visibility.Visible) HideFolderSearch();
            else _ = Back();
            return;
        }
        if (!CanChangeView) return;
        if (source != null && StationWindow.InsideElement(folderSearch, source) && e.Key == VirtualKey.Down && modifiers == VirtualKeyModifiers.None) {
            e.Handled = StationWindow.FocusSearchResult(files, ref selectionAnchor);
            return;
        }
        if (StationWindow.TextInputTarget(source)) return;
        bool inGrid = StationWindow.FromGrid(files, source) || source == root;
        if (inGrid && StationWindow.MoveGridFocus(files, source, e.Key, modifiers, ref selectionAnchor)) {
            e.Handled = true;
            return;
        }
        var entries = SelectedEntries();
        bool single = entries.Length == 1;
        var command = StationWindow.GridShortcut(e.Key, modifiers);
        Func<Task>? action = command switch {
            StationWindow.GridCommand.Open when inGrid && single => () => OpenEntry(entries[0]),
            StationWindow.GridCommand.Preview when inGrid && single => () => entries[0].Kind == StationKind.Folder ? OpenEntry(entries[0]) : QuickPreview(entries[0]),
            StationWindow.GridCommand.Rename when inGrid && single && CanWriteFolder => () => RenameEntry(entries[0]),
            StationWindow.GridCommand.Copy when entries.Length > 0 => () => CopyEntries(entries),
            StationWindow.GridCommand.Paste when CanWriteFolder => PasteFromClipboard,
            StationWindow.GridCommand.Cut when inGrid && CanWriteFolder && StationStore.CanMoveLibraryItems(entries)
                => () => owner.CutLibrarySelection(new(folder.Id, directory!), entries),
            StationWindow.GridCommand.Trash when inGrid && CanWriteFolder && entries.Length > 0 => () => TrashEntries(entries),
            StationWindow.GridCommand.NewFolder when CanWriteFolder => NewFolder,
            StationWindow.GridCommand.Undo when CanWriteFolder && (lastUndo is { IsCurrent: null } || owner.LatestLibraryMove != null) => UndoLastOrganization,
            _ => null,
        };
        if (action != null) {
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown) _ = Run(action);
            return;
        }
        switch (command) {
            case StationWindow.GridCommand.SelectAll:
                e.Handled = true;
                files.SelectAll();
                break;
            case StationWindow.GridCommand.Search:
                e.Handled = true;
                ShowFolderSearch();
                break;
            case StationWindow.GridCommand.ContextMenu when inGrid:
                e.Handled = true;
                if (!e.KeyStatus.WasKeyDown)
                    StationWindow.ShowKeyboardMenu(files, source, SelectContextCard, () => BuildItemMenu(SelectedEntries()), BuildDirectoryMenu);
                break;
        }
    }

    void SelectContextCard(GridViewItem card)
    {
        if (!card.IsSelected) StationWindow.SelectGridCard(files, card, false, false, ref selectionAnchor);
    }

    static void ShowMenu(MenuFlyout menu, FrameworkElement anchor, ContextRequestedEventArgs e)
    {
        if (menu.Items.Count == 0) return;
        if (e.TryGetPosition(anchor, out var point)) menu.ShowAt(anchor, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(anchor);
    }

    internal MenuFlyout BuildItemMenu(StationItem[] items)
    {
        var menu = owner.Menu();
        Task Inspect(StationItem item, Func<StationItem, Task> action)
        {
            store.ValidateFolderPath(folder, item.Value);
            return action(item);
        }
        var commands = new StationWindow.ItemMenuCommands(OpenEntry, QuickPreview,
            () => CopyEntries(items), item => Inspect(item, StationWindow.RevealStorage),
            item => Inspect(item, StationWindow.OpenInExplorer), () => {
                foreach (var item in items) store.ValidateFolderPath(folder, item.Value);
                StationWindow.CopyItemPaths(items);
                Notify("已复制路径");
                return Task.CompletedTask;
            }, CanWriteFolder && StationStore.CanMoveLibraryItems(items) && directory is { } target
                ? () => owner.PickLibraryDestination(new(folder.Id, target), items, files) : null);
        if (items.Length > 0 && items.All(item => item.Kind == StationKind.Folder)) {
            StationWindow.PopulateFolderMenu(menu, items, Run, commands,
                new(RenameEntry, item => owner.ChooseFolderColor(item, folder, files),
                    DissolveSubfolder, () => TrashEntries(items)), CanWriteFolder, CanChangeView && !store.ReadOnly);
            return menu;
        }
        if (items.Length > 0 && items.All(item => item.Kind == StationKind.File)) {
            StationWindow.PopulateFileMenu(menu, App.Settings.Data.EffectiveFileMenu, items, Run, commands, RenameEntry,
                () => TrashEntries(items), CanWriteFolder);
            return menu;
        }
        StationWindow.PopulateCommonItemMenu(menu, items, Run, commands, CanWriteFolder);
        if (commands.Move != null) StationWindow.AddMenuCommand(menu.Items, "移动到…", Run, commands.Move, CanWriteFolder);
        if (items.Length > 0 && items.All(item => item.Owned)) {
            menu.Items.Add(new MenuFlyoutSeparator());
            if (items is [var item]) {
                StationWindow.AddMenuCommand(menu.Items, "重命名…", Run, () => RenameEntry(item), CanWriteFolder, "F2");
            }
            StationWindow.AddMenuCommand(menu.Items, items.Length == 1 ? "移到 Halo 回收站" : $"将所选 {items.Length} 项移到回收站",
                Run, () => TrashEntries(items), CanWriteFolder, "Delete");
        }
        return menu;
    }

    internal MenuFlyout BuildDirectoryMenu()
    {
        var menu = owner.Menu();
        StationWindow.MenuHeading(menu, "当前文件夹操作");
        var create = new MenuFlyoutSubItem { Text = "新建", IsEnabled = CanWriteFolder };
        StationWindow.AddMenuCommand(create.Items, "文件夹…", Run, NewFolder, shortcut: "Ctrl+Shift+N");
        menu.Items.Add(create);
        StationWindow.AddMenuCommand(menu.Items, "从剪贴板粘贴  Ctrl+V", Run, PasteFromClipboard, CanWriteFolder);
        StationWindow.AddMenuCommand(menu.Items, "搜索当前文件夹", Run, () => { ShowFolderSearch(); return Task.CompletedTask; },
            CanChangeView, "Ctrl+F");
        menu.Items.Add(new MenuFlyoutSeparator());
        var current = directory == null ? FolderSort.Manual : store.SortFolder(folder, directory);
        var sorting = new MenuFlyoutSubItem { Text = "排序", IsEnabled = CanChangeView && !store.ReadOnly };
        foreach (var (mode, label) in new[] { (FolderSort.Manual, "手动排序（保留空位）"), (FolderSort.Name, "名称 A–Z"),
            (FolderSort.Newest, "最近修改在前"), (FolderSort.Oldest, "最早修改在前") }) {
            var option = new ToggleMenuFlyoutItem { Text = label, IsChecked = current == mode };
            option.Click += async (_, _) => await Run(() => UseSort(mode));
            sorting.Items.Add(option);
        }
        menu.Items.Add(sorting);
        if (current == FolderSort.Manual && !FilteringFolder)
            StationWindow.AddMenuCommand(menu.Items, "整理空位（当前布局）", Run,
                ArrangeCurrentFolder,
                CanChangeView && !store.ReadOnly && files.Items.Count > 0);
        StationWindow.AddMenuCommand(menu.Items, "全选", Run, () => {
            files.SelectAll(); return Task.CompletedTask;
        }, CanChangeView && files.Items.Count > 0, "Ctrl+A");
        menu.Items.Add(new MenuFlyoutSeparator());
        StationWindow.AddMenuCommand(menu.Items, "Halo 回收站…", Run, async () => {
            await owner.ShowTrash();
            if (directory is { } path) await Navigate(path, refresh: true);
        });
        return menu;
    }

    async Task CopyEntries(StationItem[] entries)
    {
        var package = new DataPackage();
        await FillCopy(package, entries);
        Clipboard.SetContent(package);
        Notify($"已复制 {entries.Length} 项", lastUndo);
    }

    async Task PasteFromClipboard()
    {
        if (!CanWriteFolder) { Notify("当前文件夹只读或正在处理内容"); return; }
        var view = Clipboard.GetContent();
        if (await owner.PasteLibraryCut(view, new(folder.Id, directory!))) return;
        if (!view.Contains(StandardDataFormats.StorageItems)) { Notify("剪贴板没有可粘贴的文件或文件夹"); return; }
        int count = await CopyDrop(view);
        if (!closed) Notify($"已粘贴 {count} 项到当前文件夹");
    }

    async Task NewFolder()
    {
        if (!CanWriteFolder) return;
        string target = directory!;
        string? name = await owner.AskText("新建文件夹", "新建文件夹");
        if (name == null || !CanWriteFolder || !string.Equals(directory, target, StringComparison.OrdinalIgnoreCase)) return;
        string created = "";
        await UpdateDirectory(async path => created = await store.CreateFolderIn(folder, path, name, lifetime.Token));
        if (!closed && files.Items.Cast<GridViewItem>().FirstOrDefault(item =>
                string.Equals(((StationItem)item.Tag).Value, created, StringComparison.OrdinalIgnoreCase)) is { } card) {
            files.SelectedItem = card;
            files.ScrollIntoView(card);
        }
    }

    Task UseSort(FolderSort sort) => UpdateDirectory(path => store.SetFolderSort(folder, path, sort));
    Task ArrangeCurrentFolder() => UpdateDirectory(path => store.ArrangeFolderGrid(folder, path, VisibleColumns));

    async Task RenameEntry(StationItem item)
    {
        if (!CanWriteFolder) return;
        string target = directory!;
        string? renamed = null;
        await owner.ShowRenameDialog(item, files, async name => {
            if (!CanWriteFolder || !string.Equals(target, directory, StringComparison.OrdinalIgnoreCase))
                throw new IOException("当前文件夹已变化，请重新打开重命名。");
            busy = true;
            try { renamed = await store.RenameFolderEntry(folder, target, item, name, VisibleColumns); }
            finally { busy = false; }
        });
        if (renamed == null || !CanChangeView || !string.Equals(target, directory, StringComparison.OrdinalIgnoreCase)) return;
        await Navigate(target, refresh: true);
        if (!closed && files.Items.Cast<GridViewItem>().FirstOrDefault(card => ((StationItem)card.Tag).Value == renamed) is { } selected)
            files.SelectedItem = selected;
    }

    async Task TrashEntries(StationItem[] items)
    {
        if (!CanWriteFolder) return;
        await UpdateDirectory(path => store.MoveFolderEntriesToTrash(folder, path, items, VisibleColumns));
        if (!closed) Notify($"已将 {items.Length} 项移到 Halo 回收站");
    }

    async Task DissolveSubfolder(StationItem item)
    {
        if (!CanWriteFolder) return;
        (int Count, FolderUndo? Undo) result = default;
        await UpdateDirectory(async path => result = await store.DissolveFolderIn(folder, path, item, VisibleColumns));
        if (!closed) Notify(result.Count == 0 ? "空文件夹已移到 Halo 回收站" : $"已解散文件夹，{result.Count} 项已移到当前目录", result.Undo);
    }

    async Task UpdateDirectory(Func<string, Task> update)
    {
        if (!CanChangeView || store.ReadOnly) return;
        string target = directory!;
        busy = true;
        try { await update(target); }
        finally { busy = false; }
        await Navigate(target, refresh: true);
    }
}
