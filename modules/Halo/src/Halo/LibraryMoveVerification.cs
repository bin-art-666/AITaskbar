namespace Halo;

internal sealed partial class StationStore
{
    internal static async Task VerifyLibraryMoves(string fixtures, Action<string, bool> check)
    {
        string seeds = Directory.CreateDirectory(Path.Combine(fixtures, "sources")).FullName;
        string catalog = Path.Combine(fixtures, "profile", "station.json");
        var store = new StationStore(catalog);
        string folder = Directory.CreateDirectory(Path.Combine(seeds, "目标")).FullName;
        string branch = Directory.CreateDirectory(Path.Combine(seeds, "整理")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(branch, "子目录")).FullName;
        string first = Path.Combine(seeds, "重复.txt"), second = Path.Combine(seeds, "占用.txt");
        await File.WriteAllTextAsync(first, "移入");
        await File.WriteAllTextAsync(second, "占用");
        await File.WriteAllTextAsync(Path.Combine(folder, "重复.txt"), "已有");
        await File.WriteAllTextAsync(Path.Combine(child, "子目录文件.txt"), "内容");
        await store.ImportCopies([new(StationKind.Folder, folder), new(StationKind.Folder, branch),
            new(StationKind.File, first), new(StationKind.File, second)]);
        var target = store.Items.Single(item => Path.GetFileName(item.Value) == "目标");
        var source = store.Items.Single(item => Path.GetFileName(item.Value) == "整理");
        var file = store.Items.Single(item => Path.GetFileName(item.Value) == "重复.txt");
        var locked = store.Items.Single(item => Path.GetFileName(item.Value) == "占用.txt");
        var destination = new LibraryLocation(target.Id, target.Value);
        await store.EnsureLayout(false, 3, false);
        await store.EnsureLayout(true, 5, false);
        var compact = store.Layout(false, 3, false);
        var expanded = store.Layout(true, 5, false);

        string numbered = Path.Combine(target.Value, "重复 (2).txt");
        await using (var handle = File.Open(locked.Value, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            bool rejected = false;
            try { await store.MoveLibraryItems(store.LibraryRoot, destination, [file, locked], 3); }
            catch (IOException) { rejected = true; }
            check("LibraryMoveRollsBackWhenLaterFileIsLocked", rejected && File.Exists(file.Value) && !File.Exists(numbered)
                && store.Items.Contains(file) && store.Items.Contains(locked) && !File.Exists(store.FlatJournal));
        }

        var interrupted = new FlatTransaction(Guid.NewGuid(), [
            new(file.Value, numbered, file.Kind), new(locked.Value, Path.Combine(target.Value, "占用.txt"), locked.Kind),
        ], null, Transfer: new(store.LibraryRoot, destination, [file.Id, locked.Id]));
        WriteStorageJournal(store.FlatJournal, interrupted);
        File.Move(file.Value, numbered);
        store = new StationStore(catalog);
        check("LibraryMoveRestartsAndRollsBackPartialJournal", !store.ReadOnly && File.Exists(file.Value) && !File.Exists(numbered)
            && store.Items.Contains(file) && !File.Exists(store.FlatJournal));

        string originalChild = Path.Combine(source.Value, "子目录");
        await store.SetFolderColor(source, null, "#114477");
        await store.SetFolderSort(source, originalChild, FolderSort.Name);
        var descendant = store.ReadFolderContents(source, originalChild, default).Single();
        await store.MoveFolderEntriesToTrash(source, originalChild, [descendant], 3);
        var transfer = await store.MoveLibraryItems(store.LibraryRoot, destination, [file, source], 3);
        string movedFolder = Path.Combine(target.Value, "整理");
        var nested = store.ReadFolderContents(target, target.Value, default).Single(item => item.Value == movedFolder);
        string newChild = Path.Combine(movedFolder, "子目录");
        var reopened = new StationStore(catalog);
        check("LibraryMoveKeepsCollisionsColorsLayoutsAndTrashOrigins", transfer.Label == "撤销移动 · 2 项" && !reopened.ReadOnly
            && !File.Exists(file.Value) && !Directory.Exists(source.Value) && File.ReadAllText(numbered) == "移入"
            && File.ReadAllText(Path.Combine(target.Value, "重复.txt")) == "已有"
            && reopened.FolderColorOverride(nested) == "#114477" && reopened.SortFolder(target, newChild) == FolderSort.Name
            && reopened.OriginalTrashLocation(reopened.Trash.Single())?.Directory == newChild);
        await transfer.Apply();
        check("LibraryMoveUndoRestoresBothDesktopLayoutsAndFolderMetadata", store.Items.Contains(source) && store.Items.Contains(file)
            && store.Layout(false, 3, false).Cells.SequenceEqual(compact.Cells)
            && store.Layout(true, 5, false).Cells.SequenceEqual(expanded.Cells) && store.FolderColorOverride(source) == "#114477"
            && store.SortFolder(source, originalChild) == FolderSort.Name && !Directory.Exists(movedFolder) && !File.Exists(numbered)
            && store.OriginalTrashLocation(store.Trash.Single())?.Directory == originalChild);

        var fromTarget = store.ReadFolderContents(target, target.Value, default).Single();
        var rootMove = await store.MoveLibraryItems(destination, store.LibraryRoot, [fromTarget], 3);
        var promoted = store.Items.Single(item => Path.GetFileName(item.Value) == "重复 (2).txt");
        check("LibraryMoveFromFolderToDesktopNumbersDuplicates", File.Exists(promoted.Value) && !File.Exists(fromTarget.Value));
        File.WriteAllText(fromTarget.Value, "另外创建的文件");
        bool undoRejected = false;
        try { await rootMove.Apply(); }
        catch (IOException) { undoRejected = true; }
        check("LibraryMoveUndoProtectsAnOccupiedOriginalPath", undoRejected && File.Exists(promoted.Value)
            && File.ReadAllText(fromTarget.Value) == "另外创建的文件");
        check("LibraryMoveRejectsOwnDescendants", !CanMoveTo(store.LibraryRoot, new(source.Id, originalChild), [source]));
    }
}
