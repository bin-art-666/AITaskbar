namespace Halo;

internal sealed record FolderTrashOrigin(Guid RootId, string RelativeParent, int? Column, int? Row);

internal sealed partial class StationStore
{
    enum NestedEditKind { Rename, Archive, Restore }
    sealed record NestedEdit(Guid RootId, string Directory, NestedEditKind Kind);

    static bool ValidTrashOrigin(FolderTrashOrigin origin) => origin.RootId != Guid.Empty
        && origin.RelativeParent is { Length: > 0 and <= 32767 } path && !Path.IsPathRooted(path)
        && (path == "." || path.Split(Path.DirectorySeparatorChar).All(part => part.Length > 0 && part is not ("." or "..")
            && !part.Any(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c))))
        && (origin.Column == null && origin.Row == null || origin.Column is >= 0 and < 64 && origin.Row is >= 0 and < 4096);

    static FolderTrashOrigin MakeTrashOrigin(StationItem root, string parent, StationCell? cell) =>
        new(root.Id, Path.GetRelativePath(root.Value, parent), cell?.Column, cell?.Row);

    internal (StationItem Root, string Directory)? OriginalTrashLocation(StationItem item)
    {
        if (item.Origin is not { } origin || !ValidTrashOrigin(origin)) return null;
        var root = Items.FirstOrDefault(entry => entry.Id == origin.RootId && CanDissolveFolder(entry));
        if (root == null) return null;
        string parent = Path.GetFullPath(Path.Combine(root.Value, origin.RelativeParent));
        try { return Directory.Exists(parent) ? (root, ValidateFolderPath(root, parent, write: true)) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal StationItem? ManagedFolderContaining(string directory)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return Items.FirstOrDefault(item => CanDissolveFolder(item) && (SamePath(full, item.Value) || Below(full, item.Value)));
    }

    void CheckNestedEntry(string parent, StationItem item)
    {
        if (!IsOwnedFile(item) || !SamePath(parent, Path.GetDirectoryName(item.Value)) || item.Id != FolderEntryId(item.Value)
            || !ManagedPath(item.Value) || (item.Kind == StationKind.Folder ? !Directory.Exists(item.Value) : !File.Exists(item.Value)))
            throw new IOException("当前目录的项目已移动或移除。");
    }

    internal async Task<string> RenameFolderEntry(StationItem root, string directory, StationItem item, string name, int columns)
    {
        name = ValidDesktopName(name);
        await desktopGate.WaitAsync();
        string? work = null;
        try {
            string parent = ValidateFolderPath(root, directory, write: true);
            CheckNestedEntry(parent, item);
            string destination = Path.Combine(parent, name);
            if (destination == item.Value) return destination;
            bool caseOnly = SamePath(destination, item.Value);
            if (!caseOnly && EntryExists(destination)) throw new IOException("此名称已存在。");
            var entries = await Task.Run(() => ReadFolderContents(root, parent, default));
            var renamed = item with { Id = FolderEntryId(destination), Value = destination };
            FlatMove[] moves;
            if (caseOnly) {
                work = CreateFlatWork();
                string transit = Path.Combine(work, "rename");
                moves = [new(item.Value, transit, item.Kind), new(transit, destination, item.Kind)];
            } else moves = [new(item.Value, destination, item.Kind)];
            await ApplyFlatMoves(moves, document => {
                var updated = WithFolderLayout(document, root, parent, state => {
                    if (state.Grid == null && state.Sort != FolderSort.Manual) return state;
                    return state with { Grid = ReplaceDissolvedFolder(FolderGrid(root, parent, entries, columns), item.Id, [renamed]) };
                });
                if (item.Kind != StationKind.Folder) return updated;
                var trash = (updated.Trash ?? []).Select(entry => {
                    if (entry.Origin is not { } origin || origin.RootId != root.Id) return entry;
                    string original = Path.GetFullPath(Path.Combine(root.Value, origin.RelativeParent));
                    if (!SamePath(original, item.Value) && !Below(original, item.Value)) return entry;
                    string relocated = Path.Combine(destination, Path.GetRelativePath(item.Value, original));
                    return entry with { Origin = origin with { RelativeParent = Path.GetRelativePath(root.Value, relocated) } };
                }).ToArray();
                return updated with { Trash = trash };
            }, work, edit: new(root.Id, parent, NestedEditKind.Rename));
            return destination;
        } finally {
            try { if (work != null) DiscardFlatWork(work); }
            finally { desktopGate.Release(); }
        }
    }

    internal async Task MoveFolderEntriesToTrash(StationItem root, string directory, StationItem[] selected, int columns)
    {
        if (selected.Length == 0) return;
        await desktopGate.WaitAsync();
        try {
            string parent = ValidateFolderPath(root, directory, write: true);
            foreach (var item in selected) CheckNestedEntry(parent, item);
            var entries = await Task.Run(() => ReadFolderContents(root, parent, default));
            var cells = FolderGrid(root, parent, entries, columns).Cells.ToDictionary(cell => cell.Id);
            var archived = selected.Select(item => {
                Guid id = Guid.NewGuid();
                return item with { Id = id, Value = NestedSplitArchivePath(id, item.Value),
                    Origin = MakeTrashOrigin(root, parent, cells.GetValueOrDefault(item.Id)) };
            }).ToArray();
            var ids = selected.Select(item => item.Id).ToHashSet();
            await ApplyFlatMoves(selected.Zip(archived, (item, copy) => new FlatMove(item.Value, copy.Value, item.Kind)).ToArray(), document => {
                var updated = WithFolderLayout(document, root, parent, state => {
                    var grid = FolderGrid(root, parent, entries, columns);
                    return state with { Grid = grid with { Cells = grid.Cells.Where(cell => !ids.Contains(cell.Id)).ToArray() } };
                });
                return updated with { Trash = [.. updated.Trash ?? [], .. archived] };
            }, edit: new(root.Id, parent, NestedEditKind.Archive));
        } finally { desktopGate.Release(); }
    }

    internal async Task RestoreInFolder(StationItem item, StationItem root, string directory)
    {
        await desktopGate.WaitAsync();
        try {
            var archived = Trash.FirstOrDefault(entry => entry.Id == item.Id);
            if (archived == null || !IsOwnedFile(archived)) throw new IOException("回收站项目已变化。");
            string parent = ValidateFolderPath(root, directory, write: true);
            var entries = await Task.Run(() => ReadFolderContents(root, parent, default));
            string name = AllocateFlatName(FolderNames(parent), Path.GetFileName(archived.Value), archived.Kind);
            string path = Path.Combine(parent, name);
            var restored = archived with { Value = path, Id = FolderEntryId(path), Origin = null };
            var origin = archived.Origin;
            bool original = origin?.RootId == root.Id && SamePath(origin.RelativeParent, Path.GetRelativePath(root.Value, parent));
            await ApplyFlatMoves([new(archived.Value, path, archived.Kind)], document => {
                if (!(document.Trash ?? []).Contains(archived)) throw new IOException("回收站项目已变化。");
                var updated = WithFolderLayout(document, root, parent, state => {
                    if (state.Grid == null && (!original || origin?.Column == null)) return state;
                    int columns = state.Grid?.Columns ?? origin!.Column!.Value + 1;
                    var grid = FolderGrid(root, parent, entries, columns);
                    if (original && origin is { Column: int column, Row: int row } && column < grid.Columns
                        && !grid.Cells.Any(cell => cell.Column == column && cell.Row == row))
                        grid = grid with { Cells = [.. grid.Cells, new StationCell(restored.Id, column, row)] };
                    return state with { Grid = SyncLayout(grid, [.. entries, restored]) };
                });
                return updated with { Trash = (updated.Trash ?? []).Where(entry => entry.Id != archived.Id).ToArray() };
            }, edit: new(root.Id, parent, NestedEditKind.Restore));
        } finally { desktopGate.Release(); }
    }

    void ValidateNestedEdit(FlatTransaction transaction, NestedEdit edit)
    {
        var root = Items.FirstOrDefault(item => item.Id == edit.RootId && CanDissolveFolder(item));
        if (root == null || edit.Directory == null || transaction.Group != null || transaction.Split != null
            || transaction.NestedGroup != null || transaction.NestedSplit != null
            || transaction.Moves.Length > MaximumCapacity || transaction.Moves.Any(move => move == null
                || move.Kind is not (StationKind.File or StationKind.Folder)))
            throw new IOException("目录操作记录无效。");
        string parent = ValidateFolderPath(root, edit.Directory);
        bool Direct(string value) => value != null && SamePath(Path.GetDirectoryName(value), parent) && ManagedPath(value);
        bool Archived(string value) => value != null && Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(value)), "N", out var id)
            && SamePath(value, NestedSplitArchivePath(id, value)) && ManagedPath(value);
        var moves = transaction.Moves;
        var first = moves[0];
        bool valid = edit.Kind switch {
            NestedEditKind.Rename when transaction.Work is { } work => moves.Length == 2 && Direct(first.From)
                && SamePath(first.To, Path.Combine(work, "rename")) && SamePath(first.To, moves[1].From)
                && Direct(moves[1].To) && SamePath(first.From, moves[1].To) && first.Kind == moves[1].Kind,
            NestedEditKind.Rename => moves.Length == 1 && Direct(first.From) && Direct(first.To) && !SamePath(first.From, first.To),
            NestedEditKind.Archive => transaction.Work == null && moves.All(move => Direct(move.From) && Archived(move.To))
                && moves.Select(move => move.From).Distinct(StringComparer.OrdinalIgnoreCase).Count() == moves.Length
                && moves.Select(move => move.To).Distinct(StringComparer.OrdinalIgnoreCase).Count() == moves.Length,
            NestedEditKind.Restore => transaction.Work == null && moves.Length == 1 && Archived(first.From) && Direct(first.To),
            _ => false,
        };
        if (!SamePath(parent, edit.Directory) || !valid) throw new IOException("目录操作路径无效。");
    }
}
