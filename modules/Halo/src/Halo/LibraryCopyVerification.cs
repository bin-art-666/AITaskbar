namespace Halo;

internal sealed partial class StationStore
{
    internal static async Task VerifyLibraryCopies(string fixtures, Action<string, bool> check)
    {
        Directory.CreateDirectory(fixtures);
        string catalog = Path.Combine(fixtures, "station.json");
        var store = new StationStore(catalog);
        string incoming = Directory.CreateDirectory(Path.Combine(fixtures, "sources")).FullName;
        string first = Path.Combine(incoming, "A.txt"), second = Path.Combine(incoming, "B.txt");
        string folder = Directory.CreateDirectory(Path.Combine(incoming, "归档")).FullName;
        await File.WriteAllTextAsync(first, "A 内容");
        await File.WriteAllTextAsync(second, "B 内容");
        await File.WriteAllTextAsync(Path.Combine(folder, "A.txt"), "已有内容");
        await store.ImportCopies([new(StationKind.File, first), new(StationKind.File, second), new(StationKind.Folder, folder)]);
        await store.SetSort(StationSort.Manual, false);
        await store.EnsureLayout(false, 5, false);
        var a = store.Items.Single(item => Path.GetFileName(item.Value) == "A.txt");
        var b = store.Items.Single(item => Path.GetFileName(item.Value) == "B.txt");
        var target = store.Items.Single(item => item.Kind == StationKind.Folder);
        StationItem[] batch = [a, b];
        var before = store.Layout(false, 5, false);
        var anchor = before.Cells.Single(cell => cell.Id == a.Id);
        var other = before.Cells.Single(cell => cell.Id == b.Id);
        var placement = new LibraryCopyPlacement(a.Id, anchor.Column, anchor.Row + 2);
        check("CtrlDragCopyBlockedAtOccupiedCell", CopyCells(before, [a.Id, b.Id], placement with { Row = anchor.Row }) == null);
        await store.CopyLibraryItems(store.LibraryRoot, store.LibraryRoot, batch, 5, placement);
        var reopened = new StationStore(catalog);
        var after = reopened.Layout(false, 5, false);
        var copyA = reopened.Items.Single(item => Path.GetFileName(item.Value) == "A (2).txt");
        var copyB = reopened.Items.Single(item => Path.GetFileName(item.Value) == "B (2).txt");
        check("CtrlDragCopyKeepsSourcesAndPlacesWholeShapeInEmptyCells",
            before.Cells.All(after.Cells.Contains)
            && after.Cells.Contains(new(copyA.Id, anchor.Column, anchor.Row + 2))
            && after.Cells.Contains(new(copyB.Id, other.Column, other.Row + 2))
            && File.ReadAllText(a.Value) == File.ReadAllText(copyA.Value) && File.ReadAllText(b.Value) == File.ReadAllText(copyB.Value));
        var destination = new LibraryLocation(target.Id, target.Value);
        await store.CopyLibraryItems(store.LibraryRoot, destination, batch, 5, null);
        check("CtrlDragCopyIntoFolderNumbersConflictsWithoutOverwriting",
            File.ReadAllText(Path.Combine(target.Value, "A.txt")) == "已有内容"
            && File.ReadAllText(Path.Combine(target.Value, "A (2).txt")) == "A 内容"
            && File.ReadAllText(Path.Combine(target.Value, "B.txt")) == "B 内容" && File.Exists(a.Value) && File.Exists(b.Value));
        var nested = store.ReadFolderContents(target, target.Value, default).Single(item => Path.GetFileName(item.Value) == "B.txt");
        await store.CopyLibraryItems(destination, store.LibraryRoot, [nested], 5, null);
        check("CtrlDragCopyFromFolderToDesktopPreservesNestedSource", File.Exists(nested.Value)
            && store.Items.Any(item => Path.GetFileName(item.Value) == "B (3).txt"));
        check("CtrlDragCopyRejectsFolderIntoItself", !CanCopyTo(destination, [target]));
        int count = store.Items.Count;
        bool rejected = false;
        using (File.Open(b.Value, FileMode.Open, FileAccess.Read, FileShare.None)) {
            try { await store.CopyLibraryItems(store.LibraryRoot, store.LibraryRoot, batch, 5, null); }
            catch (IOException) { rejected = true; }
        }
        check("CtrlDragCopyBatchFailurePublishesNoCopies", rejected && count == store.Items.Count
            && !File.Exists(Path.Combine(store.DesktopDirectory, "A (3).txt")) && !File.Exists(store.FlatJournal));
        string work = store.CreateFlatWork();
        string staged = Path.Combine(Directory.CreateDirectory(Path.Combine(work, "0")).FullName, "A.txt");
        string unpublished = Path.Combine(store.DesktopDirectory, "A (3).txt");
        File.Copy(a.Value, staged);
        WriteStorageJournal(store.FlatJournal, new FlatTransaction(Guid.NewGuid(), [new(staged, unpublished, StationKind.File)], work,
            Copy: new(store.LibraryRoot, [Guid.NewGuid()])));
        File.Move(staged, unpublished);
        reopened = new StationStore(catalog);
        check("CtrlDragCopyRecoversUncommittedPublish", !reopened.ReadOnly && reopened.Items.Count == count
            && File.Exists(a.Value) && !File.Exists(unpublished) && !File.Exists(store.FlatJournal));
    }
}
