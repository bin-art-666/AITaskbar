using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifyFolderBrowser(Action<string, bool> check)
    {
        using var retain = Retain();
        string fixtures = Directory.CreateDirectory(Path.Combine(App.DataDirectory, "folder-browser")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(fixtures, "项目资料")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(source, "设计图")).FullName;
        Directory.CreateDirectory(Path.Combine(child, "初稿"));
        await File.WriteAllTextAsync(Path.Combine(child, "示例.txt"), "已存在");
        string inbound = Path.Combine(fixtures, "示例.txt");
        await File.WriteAllTextAsync(inbound, "拖入副本");
        await App.Station.ImportCopies([new(StationKind.Folder, source)]);
        var folder = App.Station.Items.Single(item => item.Owned && Path.GetFileName(item.Value) == "项目资料");
        var originalMode = Detached;
        var oldMainSelection = Selected().Select(item => item.Id).ToArray();
        var panelBounds = AppWindow.Position;
        await OpenItem(folder);
        var window = folderBrowser!;
        try {
            check("FolderBrowserOpensInsideCurrentPanel", IsOpen && root.Children.Contains(window)
                && Detached == originalMode && AppWindow.Position.Equals(panelBounds)
                && window.CurrentDirectory == folder.Value && window.BreadcrumbCount == 1 && window.Files.Items.Count == 1);
            var childCard = (GridViewItem)window.Files.Items[0];
            window.Files.SelectedItem = childCard;
            string childPath = Path.Combine(folder.Value, "设计图");
            await window.OpenEntry((StationItem)childCard.Tag);
            check("FolderBrowserOpensSubfolderInPlace", window.CurrentDirectory == childPath && window.BreadcrumbCount == 2
                && folderBrowser == window && window.Files.Items.Count == 2);
            await window.OpenEntry(window.Files.Items.Cast<GridViewItem>().Select(card => (StationItem)card.Tag)
                .Single(item => item.Kind == StationKind.Folder));
            check("FolderBrowserSupportsDeeperDirectories", window.BreadcrumbCount == 3);
            InvokeBack(window.BackButtonForVerification);
            await Task.Delay(120);
            InvokeBack(window.BackButtonForVerification);
            await Task.Delay(120);
            check("FolderBrowserBackRestoresSelection", window.CurrentDirectory == folder.Value && window.BreadcrumbCount == 1
                && window.Files.SelectedItem is GridViewItem { Tag: StationItem selected } && selected.Value == childPath);
            window.ClearSelection();
            check("FolderBrowserCardBlankOnlyClearsSelection", window.Files.SelectedItems.Count == 0
                && folderBrowser == window && window.CurrentDirectory == folder.Value);
            await window.Navigate(childPath);
            var input = new DataPackage();
            input.SetStorageItems([await StorageFile.GetFileFromPathAsync(inbound)], true);
            int mainItems = App.Station.Items.Count;
            int copied = await window.CopyDrop(input.GetView());
            string destination = Path.Combine(childPath, "示例 (2).txt");
            check("FolderBrowserDropCopiesToCurrentDirectory", copied == 1 && window.Files.Items.Count == 3
                && File.ReadAllText(destination) == "拖入副本" && File.ReadAllText(inbound) == "拖入副本"
                && File.ReadAllText(Path.Combine(childPath, "示例.txt")) == "已存在" && App.Station.Items.Count == mainItems);
            await Task.Delay(150);
            window.VerifyMarqueeSelection(check);
            var output = new DataPackage();
            await window.FillCopy(output, [window.Files.Items.Cast<GridViewItem>().Select(card => (StationItem)card.Tag)
                .Single(item => item.Value == destination)]);
            check("FolderBrowserDragPayloadIsCopyOnly", output.RequestedOperation == DataPackageOperation.Copy
                && !StationTransfer.IsInternal(output.GetView()) && (await output.GetView().GetStorageItemsAsync()).Single().Path == destination
                && File.Exists(destination));
            await window.VerifyManualLayout(check, destination);
            await window.VerifyDirectoryCommands(check, inbound, destination);
            await window.VerifyLibraryDragTargets(check, childPath);
            await window.VerifyDragNavigation(check, childPath);
            await window.VerifyNestedGrouping(check, childPath);
            bool recursionBlocked = false, escapeBlocked = false;
            try { await App.Station.CopyIntoFolder(folder, childPath, [new(StationKind.Folder, folder.Value)], default); }
            catch (IOException) { recursionBlocked = true; }
            try { App.Station.ReadFolderContents(folder, fixtures, default); }
            catch (IOException) { escapeBlocked = true; }
            check("FolderBrowserRejectsSelfCopyAndOutsidePaths", recursionBlocked && escapeBlocked);
            await window.Back();
            InvokeBack(window.BackButtonForVerification);
            await Task.Delay(100);
            check("FolderBrowserRootBackButtonClosesOverlay", folderBrowser == null && IsOpen
                && Selected().Select(item => item.Id).SequenceEqual(oldMainSelection));
        } finally { CloseFolderBrowser(); }
        SetDetached(!originalMode);
        await Task.Delay(150);
        try {
            await OpenItem(folder);
            var otherMode = folderBrowser!;
            await otherMode.Navigate(Path.Combine(folder.Value, "设计图"));
            otherMode.Dismiss();
            check("FolderBrowserShadeClosesAllLevelsInOtherMode", folderBrowser == null && IsOpen
                && Detached != originalMode && App.Station.Items.Any(item => item.Id == folder.Id));
        } finally {
            CloseFolderBrowser();
            SetDetached(originalMode);
            await Task.Delay(150);
        }

        static void InvokeBack(Button button)
        {
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }
    }
}

internal sealed partial class FolderBrowser
{
    internal void VerifyMarqueeSelection(Action<string, bool> check) => selectionMarquee.Verify(check, "Folder");

    internal async Task VerifyDirectoryCommands(Action<string, bool> check, string source, string pasted)
    {
        var item = Entries().Single(entry => entry.Value == pasted);
        var itemLabels = Labels(BuildItemMenu([item])).ToArray();
        var mainLabels = Labels(owner.BuildItemMenu([item]));
        check("FolderItemCommandsMatchMainMenuOrder", itemLabels.SequenceEqual(mainLabels)
            && itemLabels.Contains("复制路径") && itemLabels.Contains("重命名…") && itemLabels.Contains("移到 Halo 回收站"));
        var blank = Labels(BuildDirectoryMenu());
        check("FolderBlankMenuIncludesOnlyDirectoryCommands", blank.Contains("文件夹…")
            && blank.Contains("从剪贴板粘贴  Ctrl+V") && blank.Contains("全选") && blank.Contains("名称 A–Z")
            && blank.Contains("整理空位（当前布局）") && !blank.Contains("桌面管理"));

        string current = directory!;
        int stationCount = store.Items.Count;
        var manual = GridLayout!;
        ShowFolderSearch();
        folderSearch.Text = "(2)";
        await Task.Delay(80);
        check("FolderShortcutSearchFiltersOnlyCurrentDirectory", directory == current && Entries() is [var match]
            && match.Value == pasted && !ContainsGridPoint(new(12, 12)));
        HideFolderSearch();
        check("FolderShortcutSearchRestoresManualLayout", GridLayout!.Cells.SequenceEqual(manual.Cells)
            && folderSearch.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed);
        File.SetLastWriteTimeUtc(pasted, new DateTime(2002, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await UseSort(FolderSort.Oldest);
        check("FolderDateSortUsesModificationTime", GridLayout!.Cells.First().Id == item.Id
            && !Labels(BuildDirectoryMenu()).Contains("整理空位（当前布局）"));
        await UseSort(FolderSort.Newest);
        check("FolderNewestSortReversesDateOrder", GridLayout!.Cells.Last().Id == item.Id);
        await UseSort(FolderSort.Name);
        var reopened = new StationStore(Path.Combine(App.DataDirectory, "station.json"));
        check("FolderSortPersistsSeparatelyFromParent", reopened.SortFolder(folder, current) == FolderSort.Name
            && reopened.SortFolder(folder, folder.Value) == FolderSort.Manual);
        await UseSort(FolderSort.Manual);
        check("FolderReturningToManualRestoresEveryCell", GridLayout!.Cells.SequenceEqual(manual.Cells));

        var cutItem = Entries().Single(entry => entry.Value == pasted);
        await owner.CutLibrarySelection(new(folder.Id, current), [cutItem]);
        var cut = Clipboard.GetContent();
        check("LibraryCutIsHaloOnlyAndLeavesSourceInPlace", File.Exists(pasted)
            && !cut.Contains(StandardDataFormats.StorageItems) && !cut.Contains(StandardDataFormats.Text));
        bool sameFolderRejected = false;
        try { await owner.PasteLibraryCut(cut, new(folder.Id, current)); }
        catch (IOException) { sameFolderRejected = true; }
        string otherFolder = Path.Combine(current, "初稿");
        bool cutMoved = await owner.PasteLibraryCut(cut, new(folder.Id, otherFolder));
        check("LibraryCutPasteMovesBetweenFoldersAfterRejectedPaste", sameFolderRejected && cutMoved && !File.Exists(pasted)
            && File.Exists(Path.Combine(otherFolder, Path.GetFileName(pasted))) && owner.LatestLibraryMove?.Label == "撤销移动 · 1 项");
        await owner.UndoLibraryMove();
        check("LibraryCutUndoRestoresSourceAndManualCells", File.Exists(pasted) && GridLayout!.Cells.SequenceEqual(manual.Cells)
            && !File.Exists(Path.Combine(otherFolder, Path.GetFileName(pasted))));

        var clipboard = new DataPackage();
        clipboard.SetStorageItems([await StorageFile.GetFileFromPathAsync(source)], true);
        Clipboard.SetContent(clipboard);
        await Task.Delay(120);
        check("LibraryCutAllowsOrdinaryClipboardReplacement", CanWriteFolder && Clipboard.GetContent().Contains(StandardDataFormats.StorageItems));
        await PasteFromClipboard();
        string third = Path.Combine(current, "示例 (3).txt");
        check("FolderPasteCreatesCopyInCurrentDirectory", File.Exists(third)
            && File.ReadAllText(source) == File.ReadAllText(third)
            && !File.Exists(Path.Combine(folder.Value, "示例 (3).txt")) && store.Items.Count == stationCount);
        string created = await store.CreateFolderIn(folder, current, "初稿", default);
        await Navigate(current, refresh: true);
        check("FolderCreateNamesWithinCurrentDirectory", created == Path.Combine(current, "初稿 (2)")
            && Directory.Exists(created) && store.Items.Count == stationCount);
        var subfolder = Entries().Single(entry => entry.Value == created);
        var nestedMenu = BuildItemMenu([subfolder]);
        check("FolderMenuMatchesMainAndUsesOneExplorerEntry", Labels(nestedMenu).SequenceEqual(Labels(owner.BuildItemMenu([subfolder])))
            && Labels(nestedMenu).Count(label => label == "在资源管理器中打开") == 1
            && !Labels(nestedMenu).Contains("在文件夹中显示")
            && nestedMenu.Items.Last() is MenuFlyoutSubItem { Text: StationWindow.MoreFolderActions });
        await owner.VerifyFolderColorDialog(check, subfolder, folder, files);

        var expected = GridLayout!.Cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).Select(cell => cell.Id).ToArray();
        await ArrangeCurrentFolder();
        check("FolderArrangeCompactsOnlyLocalManualLayout", GridLayout!.Cells.Select(cell => cell.Id).SequenceEqual(expected)
            && GridLayout.Cells.Select(cell => cell.Row * GridLayout.Columns + cell.Column).SequenceEqual(Enumerable.Range(0, expected.Length)));

        static IEnumerable<string> Labels(MenuFlyout menu) => menu.Items.SelectMany(LabelsFor);
        static IEnumerable<string> LabelsFor(MenuFlyoutItemBase item) => item switch {
            MenuFlyoutSubItem submenu => new[] { submenu.Text }.Concat(submenu.Items.SelectMany(LabelsFor)),
            MenuFlyoutItem command => [command.Text],
            _ => [],
        };
    }

    internal async Task VerifyManualLayout(Action<string, bool> check, string destination)
    {
        var card = files.Items.Cast<GridViewItem>().Single(item => ((StationItem)item.Tag).Value == destination);
        var moving = ((StationItem)card.Tag).Id;
        string catalog = Path.Combine(App.DataDirectory, "station.json");
        string before = await File.ReadAllTextAsync(catalog);
        files.SelectedItem = card;
        BeginLocalDrag(card, new(50, 70), new(.5, .5));
        MoveVisual(new(90, 120));
        UpdateTarget(new(files.ActualWidth - 24, Math.Min(140, files.ActualHeight - 5)));
        check("FolderDragPreviewFollowsMouseWithoutWriting", dragLayer.Children.Count == 1 && dropFrame.Visibility == Microsoft.UI.Xaml.Visibility.Visible
            && dragPosition.X + visualAnchor.X == 90 && dragPosition.Y + visualAnchor.Y == 120
            && await File.ReadAllTextAsync(catalog) == before);
        CancelLocalDrag();
        check("FolderDragCancelRestoresGridWithoutWriting", dragLayer.Children.Count == 0 && !localDrag
            && await File.ReadAllTextAsync(catalog) == before);
        await SavePosition([moving], moving, 0, 2);
        var reopened = new StationStore(catalog);
        var contents = reopened.ReadFolderContents(folder, directory!, default);
        var layout = reopened.FolderGrid(folder, directory!, contents, VisibleColumns);
        check("FolderGridPersistsPositionAndKeepsFilesInPlace", layout.Cells.Contains(new(moving, 0, 2))
            && GridLayout!.Cells.Contains(new(moving, 0, 2)) && File.Exists(destination));
        var target = layout.Cells.First(cell => cell.Id != moving);
        await SavePosition([moving], moving, target.Column, target.Row);
        check("FolderGridSwapsOccupiedCell", GridLayout!.Cells.Contains(new(moving, target.Column, target.Row))
            && GridLayout.Cells.Contains(new(target.Id, 0, 2)));
        var originalParent = reopened.ReadFolderContents(folder, folder.Value, default);
        check("FolderGridLayoutsAreIndependentByDirectory", reopened.FolderGrid(folder, folder.Value, originalParent, VisibleColumns)
            .Cells.Single().Row == 0);
    }
}
