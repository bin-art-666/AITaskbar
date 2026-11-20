using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Halo;

public partial class App
{
    static async Task VerifyFileGrouping(Action<string, bool> check)
    {
        string fixture = Path.Combine(DataDirectory, "file-grouping");
        Directory.CreateDirectory(fixture);
        string source = Path.Combine(fixture, "示例.txt");
        await File.WriteAllTextAsync(source, "文件内容");
        foreach (bool legacy in new[] { false, true }) {
            string catalog = Path.Combine(fixture, legacy ? "legacy.json" : "flat.json");
            string library = Directory.CreateDirectory(Path.Combine(fixture, legacy ? "legacy-library" : "flat-library")).FullName;
            await File.WriteAllTextAsync(catalog, JsonSerializer.Serialize(new StationDocument(legacy ? 5 : 6, [], StorageDirectory: library)));
            var store = new StationStore(catalog);
            await store.NewFolder("新建文件夹");
            await store.ImportCopies([new(StationKind.File, source), new(StationKind.File, source)]);
            var files = store.Items.Where(StationStore.CanGroupFile).ToArray();
            await File.WriteAllTextAsync(files[0].Value, "目标");
            await File.WriteAllTextAsync(files[1].Value, "来源");
            await store.MoveGrid([files[0].Id], files[0].Id, 3, 1, false, 4, true);
            await store.MoveGrid([files[0].Id], files[0].Id, 2, 2, true, 4, true);
            var compact = store.Layout(false, 4, true).Cells;
            var expanded = store.Layout(true, 4, true).Cells;
            string before = await File.ReadAllTextAsync(catalog);
            bool rejected = false;
            using (var locked = new FileStream(files[1].Value, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                try { await store.GroupFiles(files[1].Id, files[0].Id, false, 4); }
                catch (IOException) { rejected = true; }
            }
            check($"FileGroupingRollbackAfterLockedSecondFile_{legacy}", rejected
                && await File.ReadAllTextAsync(catalog) == before
                && File.ReadAllText(files[0].Value) == "目标" && File.ReadAllText(files[1].Value) == "来源"
                && !File.Exists(Path.Combine(fixture, "flat-library-moves.json"))
                && !Directory.EnumerateFileSystemEntries(Path.Combine(store.DesktopDirectory, ".halo-work")).Any());
            await store.GroupFiles(files[1].Id, files[0].Id, false, 4);
            var reopened = new StationStore(catalog);
            var group = reopened.Items.Single(i => Path.GetFileName(i.Value) == "新建文件夹 (2)");
            check($"FileGroupingKeepsBothFilesAndNumbersNames_{legacy}", reopened.Items.Count == 2 && group.Kind == StationKind.Folder && group.Owned
                && File.ReadAllText(Path.Combine(group.Value, "示例.txt")) == "目标"
                && File.ReadAllText(Path.Combine(group.Value, "示例 (2).txt")) == "来源"
                && files.All(i => !File.Exists(i.Value)) && File.ReadAllText(source) == "文件内容");
            StationCell[] Expected(StationCell[] layout) => layout
                .Where(c => c.Id != files[1].Id).Select(c => c.Id == files[0].Id ? c with { Id = group.Id } : c).ToArray();
            check($"FileGroupingOccupiesTargetAndLeavesSourceEmpty_{legacy}",
                reopened.Layout(false, 4, true).Cells.SequenceEqual(Expected(compact))
                && reopened.Layout(true, 4, true).Cells.SequenceEqual(Expected(expanded)));
            var envelope = new DataPackage();
            await StationTransfer.FillMany(envelope, [group], reopened.AttachmentDirectory);
            check($"GroupedFolderAdvertisesCopyOnly_{legacy}", envelope.RequestedOperation == DataPackageOperation.Copy
                && (await envelope.GetView().GetStorageItemsAsync()).Single().Path == group.Value && Directory.Exists(group.Value));
            string nested = Directory.CreateDirectory(Path.Combine(group.Value, "子目录")).FullName;
            await File.WriteAllTextAsync(Path.Combine(nested, "内容.txt"), "子目录内容");
            await reopened.ImportCopies([new(StationKind.File, source)]);
            await reopened.UpdateOptions(reopened.Items.Count, "", true);
            bool full = false;
            try { await reopened.DissolveFolder(group.Id); }
            catch (IOException) { full = true; }
            check($"DissolveFolderRejectsCapacityBeforeMoving_{legacy}", full && Directory.EnumerateFileSystemEntries(group.Value).Count() == 3);
            await reopened.UpdateOptions(200, "", true);
            string beforeSplit = await File.ReadAllTextAsync(catalog);
            bool splitRejected = false;
            using (var locked = new FileStream(Path.Combine(group.Value, "示例.txt"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
                try { await reopened.DissolveFolder(group.Id); }
                catch (IOException) { splitRejected = true; }
            }
            check($"DissolveFolderRollsBackLockedFile_{legacy}", splitRejected
                && await File.ReadAllTextAsync(catalog) == beforeSplit && Directory.EnumerateFileSystemEntries(group.Value).Count() == 3);
            int moved = await reopened.DissolveFolder(group.Id);
            var dissolved = new StationStore(catalog);
            var subfolder = dissolved.Items.Single(item => Path.GetFileName(item.Value) == "子目录");
            check($"DissolveFolderPreservesChildrenAndArchivesShell_{legacy}", moved == 3 && !dissolved.ReadOnly
                && dissolved.Items.All(item => item.Id != group.Id)
                && !Directory.EnumerateFileSystemEntries(dissolved.Trash.Single(item => item.Id == group.Id).Value).Any()
                && File.ReadAllText(Path.Combine(subfolder.Value, "内容.txt")) == "子目录内容"
                && dissolved.Items.Where(StationStore.CanGroupFile).Select(item => File.ReadAllText(item.Value)).Order()
                    .SequenceEqual(new[] { "文件内容", "目标", "来源" }.Order())
                && (legacy || dissolved.Items.Select(item => Path.GetFileName(item.Value)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == dissolved.Items.Count));
            check($"DissolveFolderKeepsOriginalSlot_{legacy}", dissolved.Layout(false, 4, true).Cells.Contains(new(subfolder.Id, 3, 1))
                && dissolved.Layout(true, 4, true).Cells.Contains(new(subfolder.Id, 2, 2)));
        }
    }
}

internal sealed partial class StationWindow
{
    internal async Task VerifyFolderDropIntent(Action<string, bool> check)
    {
        var cards = files.Items.Cast<GridViewItem>().Where(c => StationStore.CanGroupFile(c.Tag as StationItem))
            .OrderBy(StationGridPanel.Cell).Take(2).ToArray();
        using var lease = KeepOpenForDrag();
        dragAnchor = ((StationItem)cards[0].Tag).Id;
        var target = ((StationItem)cards[1].Tag).Id;
        void Begin(GridViewItem source)
        {
            movingIds = [dragAnchor];
            SelectCard(source, false, false);
            ShowDragPreview((Preview)((ContentControl)source.Content).Tag, new(100, 100), 1, new(.95, .95));
            dragPreviewPlacement = new(DragPreviewMode.GrabPoint, 0);
        }
        Point LowerPartOfCard(GridViewItem card)
        {
            var frame = FolderCardFrame(card);
            return new Point(frame.X + frame.Width * .8, frame.Y + frame.Height * .8);
        }

        var square = new Rect(0, 0, 40, 40);
        check("FolderDropOverlapThresholdIsStrictlyMoreThanHalf",
            !OverlapsMostOfPreview(square, new Rect(20, 0, 50, 50))
            && !OverlapsMostOfPreview(square, new Rect(20.5, 0, 50, 50))
            && OverlapsMostOfPreview(square, new Rect(19.5, 0, 50, 50)));
        string catalog = Path.Combine(App.DataDirectory, "station.json");
        string before = await File.ReadAllTextAsync(catalog);
        try {
            Begin(cards[0]);
            var card = FolderCardFrame(cards[1]);
            dragPreviewPlacement = new(DragPreviewMode.Below, 24);
            var above = new Point(card.X + card.Width / 2, card.Y - 24);
            check("BelowPreviewCanOverlapWithPointerAboveTheCard", UpdateDropTarget(above)
                && folderDropCandidate == target);
            ClearDropTarget();
            dragPreviewPlacement = new(DragPreviewMode.LowerRight, 12);
            var left = new Point(card.X + 2 - 12, card.Y + 2 - 12);
            check("LowerRightPreviewCanOverlapWithPointerLeftOfTheCard", UpdateDropTarget(left)
                && folderDropCandidate == target);
            ClearDropTarget();
            dragPreviewPlacement = new(DragPreviewMode.GrabPoint, 0);
            var point = LowerPartOfCard(cards[1]);
            UpdateDropTarget(point);
            check("FolderDropBelowIconRequires300msDwell", folderDropTimer?.Interval == TimeSpan.FromMilliseconds(300)
                && folderDropCandidate == target && FolderDropOnRelease(point) == null);
            await Task.Delay(FolderDropDelay + TimeSpan.FromMilliseconds(100));
            check("FrameOverlapArmsWithoutMutatingFiles", FolderDropOnRelease(point) == target
                && folderDropCue.Visibility == Visibility.Visible && await File.ReadAllTextAsync(catalog) == before);

            var targetFrame = FolderCardFrame(cards[1]);
            var edge = new Point(targetFrame.X + 2, point.Y);
            UpdateDropTarget(edge);
            check("UnderHalfOverlapClearsArmedFolderCue", folderDropCandidate == null
                && folderDropCue.Visibility == Visibility.Collapsed && FolderDropOnRelease(edge) == null);
            movingIds.Add(target);
            UpdateDropTarget(point);
            check("FolderDropNeverArmsForMultiSelection", folderDropCandidate == null);
            movingIds.Remove(target);
            UpdateDropTarget(point);
            CancelPointerDrag();
            await Task.Delay(FolderDropDelay + TimeSpan.FromMilliseconds(50));
            check("FolderDropCancelClearsPendingDwell", armedFolderDrop == null && insertion.Visibility == Visibility.Collapsed
                && await File.ReadAllTextAsync(catalog) == before);

            int columns = GridColumns();
            bool newest = App.Settings.Data.NewestFirst;
            var layout = App.Station.Layout(detached, columns, newest);
            var sourceBefore = layout.Cells.Single(i => i.Id == dragAnchor);
            var targetBefore = layout.Cells.Single(i => i.Id == target);
            Begin(cards[0]);
            UpdateDropTarget(edge);
            await Task.Delay(FolderDropDelay + TimeSpan.FromMilliseconds(50));
            await CommitGridDrop([dragAnchor], GridTarget(edge, false), FolderDropOnRelease(edge));
            var swapped = App.Station.Layout(detached, columns, newest).Cells;
            check("UnderHalfOverlapActuallySwapsTheTwoFiles",
                swapped.Contains(sourceBefore with { Column = targetBefore.Column, Row = targetBefore.Row })
                && swapped.Contains(targetBefore with { Column = sourceBefore.Column, Row = sourceBefore.Row })
                && App.Station.Items.Count(i => i.Id == dragAnchor || i.Id == target) == 2);
            CancelPointerDrag();
            await Task.Delay(150);

            var currentTarget = files.Items.Cast<GridViewItem>().Single(c => ((StationItem)c.Tag).Id == target);
            var currentSource = files.Items.Cast<GridViewItem>().Single(c => ((StationItem)c.Tag).Id == dragAnchor);
            point = LowerPartOfCard(currentTarget);
            var originals = App.Station.Items.Where(i => i.Id == dragAnchor || i.Id == target).ToArray();
            var previousIds = App.Station.Items.Select(i => i.Id).ToHashSet();
            Begin(currentSource);
            UpdateDropTarget(point);
            await Task.Delay(FolderDropDelay + TimeSpan.FromMilliseconds(100));
            var releaseCell = GridTarget(point, false);
            await CommitGridDrop([dragAnchor], releaseCell, FolderDropOnRelease(point));
            var folder = App.Station.Items.FirstOrDefault(i => !previousIds.Contains(i.Id));
            var grouped = App.Station.Layout(detached, columns, newest).Cells;
            check("OverHalfOverlapActuallyCreatesFolder",
                folder is { Kind: StationKind.Folder }
                && grouped.Any(c => c.Id == folder.Id && (c.Column, c.Row) == releaseCell)
                && originals.All(i => !File.Exists(i.Value) && File.Exists(Path.Combine(folder.Value, Path.GetFileName(i.Value)))));
        } finally { CancelPointerDrag(); }
    }
}
