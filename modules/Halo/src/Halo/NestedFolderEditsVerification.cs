namespace Halo;

internal sealed partial class FolderBrowser
{
    async Task VerifyNestedEdits(Action<string, bool> check, string parent, string catalog)
    {
        string path = await store.CreateFolderIn(folder, parent, "子项编辑验证", default);
        string original = Path.Combine(path, "示例.txt");
        string other = Path.Combine(path, "其余.txt");
        await File.WriteAllTextAsync(original, "原内容");
        await File.WriteAllTextAsync(other, "另一个文件");
        await Navigate(path);
        var item = Entries().Single(entry => entry.Value == original);
        await SavePosition([item.Id], item.Id, 0, 3);
        string renamed = await store.RenameFolderEntry(folder, path, item, "改名.txt", VisibleColumns);
        await Navigate(path, refresh: true);
        item = Entries().Single(entry => entry.Value == renamed);
        check("NestedRenamePreservesCellAndFileContent", !File.Exists(original) && File.ReadAllText(renamed) == "原内容"
            && GridLayout!.Cells.Contains(new(item.Id, 0, 3)));
        var known = store.Trash.Select(entry => entry.Id).ToHashSet();
        await TrashEntries(Entries());
        var archived = store.Trash.Where(entry => !known.Contains(entry.Id)).ToArray();
        var reopened = new StationStore(catalog);
        check("NestedTrashBatchStoresRelativeParentAndCell", archived.Length == 2 && !File.Exists(renamed) && !File.Exists(other)
            && reopened.Trash.Single(entry => entry.Id == archived.Single(value => value.Origin?.Row == 3).Id).Origin?.RootId == folder.Id);
        await store.Restore(archived);
        await Navigate(path, refresh: true);
        check("NestedTrashRestoresOriginalDirectoryAndCell", File.ReadAllText(other) == "另一个文件"
            && File.ReadAllText(renamed) == "原内容" && GridLayout!.Cells.Contains(new(item.Id, 0, 3)));

        await TrashEntries([Entries().Single(entry => entry.Id == item.Id)]);
        var collision = store.Trash.Single(entry => !known.Contains(entry.Id));
        await File.WriteAllTextAsync(renamed, "后来添加");
        await store.Restore([collision]);
        string numbered = Path.Combine(path, "改名 (2).txt");
        check("NestedTrashRestoreNumbersNameConflicts", File.ReadAllText(renamed) == "后来添加" && File.ReadAllText(numbered) == "原内容");

        await Navigate(path, refresh: true);
        await TrashEntries([Entries().Single(entry => entry.Value == numbered)]);
        var missing = store.Trash.Single(entry => !known.Contains(entry.Id));
        string moved = Path.Combine(parent, "外部已改名");
        Directory.Move(path, moved);
        check("NestedTrashDetectsMissingParent", store.OriginalTrashLocation(missing) == null);
        await store.RestoreInFolder(missing, folder, moved);
        check("NestedTrashRestoresToChosenManagedFolder", File.ReadAllText(Path.Combine(moved, Path.GetFileName(numbered))) == "原内容"
            && store.Trash.All(entry => entry.Id != missing.Id));
        await Navigate(parent);
    }
}
