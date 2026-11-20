namespace Halo;

internal sealed partial class StationStore
{
    sealed record NestedFileGroup(Guid RootId, string Directory, bool Undo = false);

    internal async Task<FolderUndo?> GroupFilesInFolder(StationItem folder, string directory, Guid source, Guid target, int columns)
    {
        await desktopGate.WaitAsync();
        string? work = null;
        try {
            string path = ValidateFolderPath(folder, directory, write: true);
            var prior = SavedFolderGrid(folder, path);
            var entries = await Task.Run(() => ReadFolderContents(folder, path, CancellationToken.None));
            var from = entries.FirstOrDefault(item => item.Id == source);
            var onto = entries.FirstOrDefault(item => item.Id == target);
            if (source == target || !CanGroupFile(from) || !CanGroupFile(onto))
                throw new IOException("只能合并当前文件夹里的两个 Halo 文件副本。");

            string name = AllocateFlatName(FolderNames(path), "新建文件夹", StationKind.Folder);
            string destination = Path.Combine(path, name);
            Guid groupId = FolderEntryId(destination);
            work = CreateFlatWork();
            string staged = Directory.CreateDirectory(Path.Combine(work, "group")).FullName;
            var childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string Child(StationItem file) => Path.Combine(staged, AllocateFlatName(childNames, Path.GetFileName(file.Value), StationKind.File));
            string ontoChild = Child(onto!), sourceChild = Child(from!);
            await ApplyFlatMoves([
                new(onto!.Value, ontoChild, StationKind.File),
                new(from!.Value, sourceChild, StationKind.File),
                new(staged, destination, StationKind.Folder),
            ], document => WithFolderLayout(document, folder, path, state => {
                if (state.Grid == null && state.Sort != FolderSort.Manual) return state;
                var original = FolderGrid(folder, path, entries, columns);
                return state with { Grid = original with {
                    Cells = original.Cells.Where(cell => cell.Id != source)
                        .Select(cell => cell.Id == target ? cell with { Id = groupId } : cell).ToArray(),
                } };
            }), work, nestedGroup: new(folder.Id, path));
            return await CreateFolderUndo(folder, path, prior, $"撤销合并 · {Path.GetFileName(destination)}", [destination], async restoreLayout => {
                string undoWork = CreateFlatWork();
                try {
                    string transit = Path.Combine(undoWork, "group");
                    FlatMove[] restore = [
                        new(destination, transit, StationKind.Folder),
                        new(Path.Combine(transit, Path.GetFileName(sourceChild)), from.Value, StationKind.File),
                        new(Path.Combine(transit, Path.GetFileName(ontoChild)), onto.Value, StationKind.File),
                    ];
                    CheckUndoDestinations(restore);
                    await ApplyFlatMoves(restore, document => {
                        if (Directory.EnumerateFileSystemEntries(transit).Any()) throw new IOException("文件夹内容已变化，无法撤销。");
                        return restoreLayout(document);
                    }, undoWork, nestedGroup: new(folder.Id, path, Undo: true));
                } finally { DiscardFlatWork(undoWork); }
            });
        } finally {
            try { if (work != null) DiscardFlatWork(work); }
            finally { desktopGate.Release(); }
        }
    }

    void ValidateNestedFileGroup(FlatTransaction transaction, NestedFileGroup group)
    {
        var root = Items.FirstOrDefault(item => item.Id == group.RootId);
        if (root is not { Owned: true, Kind: StationKind.Folder } || group.Directory == null
            || transaction.Group != null || transaction.Split != null || transaction.Work == null
            || transaction.Moves is not { Length: 3 } || transaction.Moves.Any(move => move == null))
            throw new IOException("子目录合并记录无效。");
        var moves = group.Undo ? ReverseFolderMoves(transaction.Moves) : transaction.Moves;
        string directory = ValidateFolderPath(root, group.Directory);
        string staged = Path.Combine(transaction.Work, "group");
        bool DirectEntry(string value, string parent) => value != null && SamePath(Path.GetDirectoryName(value), parent) && ManagedPath(value);
        if (!SamePath(group.Directory, directory)
            || moves[2].Kind != StationKind.Folder || !SamePath(moves[2].From, staged) || !DirectEntry(moves[2].To, directory)
            || SamePath(moves[0].From, moves[1].From) || SamePath(moves[0].To, moves[1].To)
            || moves.Take(2).Any(move => move.Kind != StationKind.File || !DirectEntry(move.From, directory)
                || !DirectEntry(move.To, staged) || SamePath(move.From, moves[2].To)))
            throw new IOException("子目录合并路径无效。");
    }
}
