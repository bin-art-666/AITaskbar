namespace Halo;

internal sealed partial class StationStore
{
    sealed record NestedFolderSplit(Guid RootId, string Directory, Guid ArchiveId, bool Undo = false);

    internal async Task<(int Count, FolderUndo? Undo)> DissolveFolderIn(StationItem root, string directory, StationItem folder, int columns)
    {
        await desktopGate.WaitAsync();
        try {
            string parent = ValidateFolderPath(root, directory, write: true);
            var prior = SavedFolderGrid(root, parent);
            string source = ValidateFolderPath(root, folder.Value, write: true);
            if (!CanDissolveFolder(folder) || !SamePath(Path.GetDirectoryName(source), parent)
                || folder.Id != FolderEntryId(source))
                throw new IOException("只能解散当前目录的 Halo 子文件夹。");
            var entries = await Task.Run(() => ReadFolderContents(root, parent, default));
            if (!entries.Any(item => item.Id == folder.Id && CanDissolveFolder(item)))
                throw new IOException("文件夹已移动或移除。");
            var names = FolderNames(parent);
            var children = new List<StationItem>();
            var moves = new List<FlatMove>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(source).Take(MaximumCapacity + 1)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)) {
                if (!ManagedPath(entry)) throw new IOException("文件夹内有不可移动的项目。");
                if (children.Count == MaximumCapacity) throw new IOException("文件夹内的项目过多，无法一次解散。");
                var kind = File.GetAttributes(entry).HasFlag(FileAttributes.Directory) ? StationKind.Folder : StationKind.File;
                string target = Path.Combine(parent, AllocateFlatName(names, Path.GetFileName(entry), kind));
                children.Add(new(FolderEntryId(target), kind, target, "", DateTimeOffset.Now, true));
                moves.Add(new(entry, target, kind));
            }
            var cell = FolderGrid(root, parent, entries, columns).Cells.FirstOrDefault(value => value.Id == folder.Id);
            var archived = folder with { Id = Guid.NewGuid(), Origin = MakeTrashOrigin(root, parent, cell) };
            archived = archived with { Value = NestedSplitArchivePath(archived.Id, source) };
            moves.Add(new(source, archived.Value, StationKind.Folder));
            var promoted = children.ToArray();
            await ApplyFlatMoves(moves.ToArray(), document => {
                if (Directory.EnumerateFileSystemEntries(archived.Value).Any())
                    throw new IOException("文件夹内容已变化，请重试。");
                var updated = WithFolderLayout(document, root, parent, state => {
                    if (state.Grid == null && state.Sort != FolderSort.Manual) return state;
                    var grid = FolderGrid(root, parent, entries, columns);
                    var replacement = ReplaceDissolvedFolder(grid, folder.Id, promoted);
                    var remaining = entries.Where(item => item.Id != folder.Id).Concat(promoted).ToArray();
                    return state with { Grid = SyncLayout(replacement, remaining) };
                });
                return updated with { Trash = [.. updated.Trash ?? [], archived] };
            }, nestedSplit: new(root.Id, parent, archived.Id));
            var undo = await CreateFolderUndo(root, parent, prior, $"撤销解散 · {Path.GetFileName(folder.Value)}", [archived.Value, .. promoted.Select(item => item.Value)],
                async restoreLayout => {
                    if (!Trash.Contains(archived)) throw new IOException("空文件夹已被恢复或清理，无法撤销。");
                    var restore = ReverseFolderMoves(moves);
                    CheckUndoDestinations(restore);
                    await ApplyFlatMoves(restore, document => {
                        if (!(document.Trash ?? []).Contains(archived)) throw new IOException("回收站项目已变化。");
                        var restored = restoreLayout(document);
                        return restored with { Trash = (restored.Trash ?? []).Where(item => item.Id != archived.Id).ToArray() };
                    }, nestedSplit: new(root.Id, parent, archived.Id, Undo: true));
                });
            return (promoted.Length, undo);
        } finally { desktopGate.Release(); }
    }

    string NestedSplitArchivePath(Guid id, string source) => Path.Combine(
        IsFlatLibrary ? Path.Combine(DesktopDirectory, FlatTrash) : DesktopDirectory,
        id.ToString("N"), Path.GetFileName(source));

    void ValidateNestedFolderSplit(FlatTransaction transaction, NestedFolderSplit split)
    {
        var root = Items.FirstOrDefault(item => item.Id == split.RootId);
        if (!CanDissolveFolder(root) || split.Directory == null || split.ArchiveId == Guid.Empty
            || transaction.Work != null || transaction.Group != null || transaction.Split != null || transaction.NestedGroup != null
            || transaction.Moves.Length > MaximumCapacity + 1 || transaction.Moves.Any(move => move == null))
            throw new IOException("子文件夹解散记录无效。");
        string parent = ValidateFolderPath(root!, split.Directory);
        var moves = split.Undo ? ReverseFolderMoves(transaction.Moves) : transaction.Moves;
        var archive = moves[^1];
        bool DirectEntry(string value, string directory) => value != null
            && SamePath(Path.GetDirectoryName(value), directory) && ManagedPath(value);
        if (!SamePath(parent, split.Directory) || archive.Kind != StationKind.Folder
            || !DirectEntry(archive.From, parent) || !SamePath(archive.To, NestedSplitArchivePath(split.ArchiveId, archive.From))
            || !ManagedPath(archive.To))
            throw new IOException("子文件夹回收路径无效。");
        if ((data.LastStorageOperation == transaction.Id) != split.Undo && !Trash.Any(item => item.Id == split.ArchiveId
                && CanDissolveFolder(item) && SamePath(item.Value, archive.To)))
            throw new IOException("已解散的文件夹回收记录无效。");
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { archive.From };
        foreach (var move in moves.SkipLast(1))
            if (move.Kind is not (StationKind.File or StationKind.Folder) || !DirectEntry(move.From, archive.From)
                || !DirectEntry(move.To, parent) || !sources.Add(move.From) || !destinations.Add(move.To))
                throw new IOException("子文件夹解散路径无效。");
    }
}
