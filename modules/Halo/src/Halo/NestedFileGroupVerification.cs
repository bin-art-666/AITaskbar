using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Halo;

internal sealed partial class FolderBrowser
{
    internal async Task VerifyNestedGrouping(Action<string, bool> check, string parent)
    {
        string path = await store.CreateFolderIn(folder, parent, "合并验证", default);
        string source = Path.Combine(path, "A.txt"), target = Path.Combine(path, "B.txt");
        string duplicate = Directory.CreateDirectory(Path.Combine(path, "新建文件夹")).FullName;
        await File.WriteAllTextAsync(source, "来源文件");
        await File.WriteAllTextAsync(target, "目标文件");
        await Navigate(path);
        // Let WinUI lay out the new cards and settle the scroll viewer after navigation.
        await Task.Delay(180);
        files.UpdateLayout();
        Guid sourceId = StationStore.FolderEntryId(source), targetId = StationStore.FolderEntryId(target);
        string catalog = Path.Combine(App.DataDirectory, "station.json");

        Point Begin()
        {
            var cards = files.Items.Cast<GridViewItem>().ToArray();
            var from = cards.Single(card => ((StationItem)card.Tag).Id == sourceId);
            var onto = cards.Single(card => ((StationItem)card.Tag).Id == targetId);
            files.SelectedItem = from;
            BeginLocalDrag(from, new(60, 65), new(.5, .5));
            double half = (metrics.IconSize + 2 * StationWindow.DragPreviewFramePadding) / 2;
            previewOffset = new(-half, -half);
            var frame = FolderDropIntent.CardFrame(files, onto);
            return new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        }

        var fromCell = GridLayout!.Cells.Single(cell => cell.Id == sourceId);
        var ontoCell = GridLayout.Cells.Single(cell => cell.Id == targetId);
        var center = Begin();
        string before = await File.ReadAllTextAsync(catalog);
        UpdateTarget(center);
        if (folderDrop.Candidate != targetId)
            throw new InvalidOperationException($"Nested group target was unavailable at {center}. Grid: {files.ActualWidth}×{files.ActualHeight}, source: {localLibraryDrag?.Anchor}, target: {targetId}.");
        check("NestedFolderDropRequires300msBeforeCue", folderDrop.Candidate == targetId && folderDrop.Armed == null
            && folderDrop.Timer.Interval == FolderDropIntent.Delay && FolderDropIntent.Delay.TotalMilliseconds == 300);
        await Task.Delay(420);
        check("NestedFolderDropShowsCueWithoutMovingFiles", folderDrop.Armed == targetId && folderDrop.Cue.Visibility == Visibility.Visible
            && File.Exists(source) && File.Exists(target) && await File.ReadAllTextAsync(catalog) == before);
        var targetCard = files.Items.Cast<GridViewItem>().Single(card => ((StationItem)card.Tag).Id == targetId);
        var edge = new Point(FolderDropIntent.CardFrame(files, targetCard).Right + 1, center.Y);
        UpdateTarget(edge);
        check("NestedFolderDropEdgeClearsGroupingCue", folderDrop.Candidate == null && folderDrop.Cue.Visibility == Visibility.Collapsed);
        await CommitLocalDrop(edge);
        check("NestedFolderDropEdgeSwapsWithoutGrouping", GridLayout!.Cells.Contains(ontoCell with { Id = sourceId })
            && GridLayout.Cells.Contains(fromCell with { Id = targetId }) && Directory.GetDirectories(path).Length == 1);

        files.UpdateLayout();
        center = Begin();
        UpdateTarget(center);
        await Task.Delay(420);
        before = await File.ReadAllTextAsync(catalog);
        CancelLocalDrag();
        check("NestedFolderDropCancelPreservesFilesAndGrid", folderDrop.Armed == null && File.Exists(source) && File.Exists(target)
            && await File.ReadAllTextAsync(catalog) == before);

        bool blocked = false;
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            try { await store.GroupFilesInFolder(folder, path, sourceId, targetId, VisibleColumns); }
            catch (IOException) { blocked = true; }
        }
        check("NestedFolderGroupRollsBackWhenSecondFileIsLocked", blocked
            && File.ReadAllText(source) == "来源文件" && File.ReadAllText(target) == "目标文件"
            && await File.ReadAllTextAsync(catalog) == before && Directory.GetDirectories(path).Length == 1
            && !File.Exists(Path.Combine(App.DataDirectory, "flat-library-moves.json")));

        center = Begin();
        UpdateTarget(center);
        await Task.Delay(420);
        await CommitLocalDrop(center);
        string grouped = Path.Combine(path, "新建文件夹 (2)");
        var id = StationStore.FolderEntryId(grouped);
        var reopened = new StationStore(catalog);
        var restored = reopened.FolderGrid(folder, path, reopened.ReadFolderContents(folder, path, default), VisibleColumns);
        check("NestedFolderDropCreatesNumberedGroupAtTargetAndLeavesSourceEmpty", Directory.Exists(duplicate)
            && File.ReadAllText(Path.Combine(grouped, "A.txt")) == "来源文件" && File.ReadAllText(Path.Combine(grouped, "B.txt")) == "目标文件"
            && !File.Exists(source) && !File.Exists(target) && restored.Cells.Contains(fromCell with { Id = id })
            && restored.Cells.All(cell => cell.Row != ontoCell.Row || cell.Column != ontoCell.Column));
        check("NestedFolderGroupOffersUndo", undoButton.Visibility == Visibility.Visible && lastUndo != null);
        await UndoLastOrganization();
        check("NestedFolderGroupUndoRestoresBothOriginalCells", !Directory.Exists(grouped) && File.Exists(source) && File.Exists(target)
            && GridLayout!.Cells.Contains(ontoCell with { Id = sourceId }) && GridLayout.Cells.Contains(fromCell with { Id = targetId }));
        var undoConflict = await store.GroupFilesInFolder(folder, path, sourceId, targetId, VisibleColumns);
        string changedFile = Path.Combine(grouped, "A.txt");
        await File.WriteAllTextAsync(changedFile, "已编辑");
        bool editRejected = false;
        try { await undoConflict!.Apply(); }
        catch (IOException) { editRejected = true; }
        check("NestedFolderGroupUndoRejectsEditedContent", editRejected && File.ReadAllText(changedFile) == "已编辑" && !File.Exists(source));
        await File.WriteAllTextAsync(changedFile, "来源文件");
        await Navigate(path, refresh: true);
        await VerifyNestedDissolve(check, path, grouped, catalog);
        await VerifyNestedEdits(check, path, catalog);
        await Navigate(parent);
    }

    async Task VerifyNestedDissolve(Action<string, bool> check, string parent, string grouped, string catalog)
    {
        string original = Path.Combine(parent, "A.txt");
        await File.WriteAllTextAsync(original, "现有文件");
        await Navigate(parent, refresh: true);
        var item = Entries().Single(entry => entry.Value == grouped);
        var firstCell = GridLayout!.Cells.Single(cell => cell.Id == item.Id);
        var menu = BuildItemMenu([item]).Items.OfType<MenuFlyoutSubItem>().Single(entry => entry.Text == StationWindow.MoreFolderActions)
            .Items.OfType<MenuFlyoutItem>().Single(entry => entry.Text == "解散文件夹");
        check("NestedDissolveMenuOnlyForSingleOwnedFolder", menu.IsEnabled
            && !BuildItemMenu([item with { Owned = false }]).Items.OfType<MenuFlyoutItem>().Any(entry => entry.Text == menu.Text)
            && !BuildItemMenu([item, Entries().Single(entry => entry.Value == original)]).Items.OfType<MenuFlyoutItem>().Any(entry => entry.Text == menu.Text));

        string before = await File.ReadAllTextAsync(catalog);
        bool blocked = false;
        using (var locked = new FileStream(Path.Combine(grouped, "B.txt"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
            try { await store.DissolveFolderIn(folder, parent, item, VisibleColumns); }
            catch (IOException) { blocked = true; }
        }
        string renamed = Path.Combine(parent, "A (2).txt");
        check("NestedDissolveRollsBackWhenSecondFileIsLocked", blocked && !File.Exists(renamed)
            && File.Exists(Path.Combine(grouped, "A.txt")) && File.Exists(Path.Combine(grouped, "B.txt"))
            && await File.ReadAllTextAsync(catalog) == before && !File.Exists(Path.Combine(App.DataDirectory, "flat-library-moves.json")));
        int count = store.Items.Count;
        var trashBefore = store.Trash.Select(entry => entry.Id).ToHashSet();
        await DissolveSubfolder(item);
        var reopened = new StationStore(catalog);
        var archive = reopened.Trash.Single(entry => !trashBefore.Contains(entry.Id));
        var grid = reopened.FolderGrid(folder, parent, reopened.ReadFolderContents(folder, parent, default), VisibleColumns);
        check("NestedDissolvePromotesIntoParentAndArchivesOnlyEmptyFolder", Directory.Exists(archive.Value)
            && !Directory.EnumerateFileSystemEntries(archive.Value).Any() && !Directory.Exists(grouped)
            && File.ReadAllText(original) == "现有文件" && File.ReadAllText(renamed) == "来源文件"
            && File.ReadAllText(Path.Combine(parent, "B.txt")) == "目标文件" && reopened.Items.Count == count
            && grid.Cells.Contains(firstCell with { Id = StationStore.FolderEntryId(renamed) }));
        await UndoLastOrganization();
        check("NestedDissolveUndoRestoresGroupWithFilesAndLayout", File.Exists(Path.Combine(grouped, "A.txt"))
            && File.Exists(Path.Combine(grouped, "B.txt")) && !File.Exists(renamed)
            && GridLayout!.Cells.Contains(firstCell) && !store.Trash.Any(entry => entry.Id == archive.Id));
    }
}
