namespace Halo;

internal sealed record LibraryCopyPlacement(Guid Anchor, int Column, int Row, bool Expanded = false, bool NewestFirst = false);

internal sealed partial class StationStore
{
    sealed record LibraryCopy(LibraryLocation Destination, Guid[] DesktopItems);

    internal static bool CanCopyTo(LibraryLocation destination, StationItem[] selected) =>
        CanMoveLibraryItems(selected) && !selected.Any(item => item.Kind == StationKind.Folder &&
            (SamePath(item.Value, destination.Directory) || Below(destination.Directory, item.Value)));

    internal static StationCell[]? CopyCells(StationLayout layout, Guid[] ids, LibraryCopyPlacement placement)
    {
        var selected = ids.ToHashSet();
        var originals = layout.Cells.Where(cell => selected.Contains(cell.Id)).ToArray();
        if (originals.Length != ids.Length || originals.FirstOrDefault(cell => cell.Id == placement.Anchor) is not { } anchor) return null;
        var occupied = layout.Cells.Select(cell => (cell.Column, cell.Row)).ToHashSet();
        var targets = originals.Select(cell => cell with {
            Column = placement.Column + cell.Column - anchor.Column,
            Row = placement.Row + cell.Row - anchor.Row,
        }).ToArray();
        return targets.Any(cell => cell.Column < 0 || cell.Column >= layout.Columns || cell.Row is < 0 or > 4095
            || occupied.Contains((cell.Column, cell.Row))) ? null : targets;
    }

    internal async Task<int> CopyLibraryItems(LibraryLocation source, LibraryLocation destination, StationItem[] selected,
        int columns, LibraryCopyPlacement? placement)
    {
        await desktopGate.WaitAsync();
        string? work = null;
        try {
            if (ReadOnly || !CanCopyTo(destination, selected) || selected.Select(item => item.Id).Distinct().Count() != selected.Length)
                throw new IOException("请选择可复制的 Halo 文件或文件夹，以及有效的目标目录。");
            var sourceRoot = LibraryLocationRoot(source);
            var targetRoot = LibraryLocationRoot(destination);
            var before = data;
            if (targetRoot == null && before.Items.Length + selected.Length > before.Capacity)
                throw new IOException("中转站容量不足，本批未复制。");
            var sources = sourceRoot == null ? before.Items : ReadFolderContents(sourceRoot, source.Directory, default);
            foreach (var item in selected) {
                if (!sources.Contains(item) || !ManagedPath(item.Value)) throw new IOException("源项目已变化，请重新选择。");
                if (sourceRoot != null) CheckNestedEntry(source.Directory, item);
            }
            var entries = targetRoot == null ? before.Items : ReadFolderContents(targetRoot, destination.Directory, default);
            if (targetRoot != null && entries.Length + selected.Length > MaximumCapacity)
                throw new IOException("当前目录的项目过多，本批未复制。");
            StationLayout? grid = null;
            StationCell[]? cells = null;
            if (placement != null) {
                bool manual = targetRoot == null ? Sort(placement.NewestFirst) == StationSort.Manual
                    : SortFolder(targetRoot, destination.Directory) == FolderSort.Manual;
                if (!manual || !SamePath(source.Directory, destination.Directory)) throw new IOException("请选择手动布局中的空位。");
                grid = targetRoot == null ? Layout(placement.Expanded, columns, placement.NewestFirst)
                    : FolderGrid(targetRoot, destination.Directory, entries, columns);
                cells = CopyCells(grid, selected.Select(item => item.Id).ToArray(), placement)
                    ?? throw new IOException("目标位置放不下副本，请选择空位。");
            }
            var names = targetRoot == null ? FlatNames() : FolderNames(destination.Directory);
            if (targetRoot == null && !IsFlatLibrary)
                names.UnionWith(before.Items.Where(IsOwnedFile).Select(item => Path.GetFileName(item.Value)));
            var copies = selected.Select(item => {
                Guid id = Guid.NewGuid();
                var target = targetRoot == null && !IsFlatLibrary ? Path.Combine(DesktopDirectory, id.ToString("N")) : destination.Directory;
                string value = Path.Combine(target, AllocateFlatName(names, Path.GetFileName(item.Value), item.Kind));
                return item with { Id = targetRoot == null ? id : FolderEntryId(value), Value = value, Title = "", Created = DateTimeOffset.Now };
            }).ToArray();
            if (cells != null) {
                var newIds = selected.Zip(copies).ToDictionary(pair => pair.First.Id, pair => pair.Second.Id);
                grid = grid! with { Cells = [.. grid!.Cells, .. cells.Select(cell => cell with { Id = newIds[cell.Id] })] };
            }
            work = CreateFlatWork();
            var staging = work;
            var moves = await Task.Run(() => selected.Select((item, index) => {
                string slot = Directory.CreateDirectory(Path.Combine(staging, index.ToString())).FullName;
                string staged = Path.Combine(slot, Path.GetFileName(item.Value));
                CopyTree(item.Value, staged, default);
                VerifyCopiedTree(item.Value, staged, default);
                return new FlatMove(staged, copies[index].Value, item.Kind);
            }).ToArray());
            await ApplyFlatMoves(moves, document => {
                if (!ReferenceEquals(document, before)) throw new IOException("文件库已变化，请重试。");
                if (targetRoot != null)
                    return grid == null ? document : WithFolderLayout(document, targetRoot, destination.Directory, state => state with { Grid = grid });
                var next = document with { Items = [.. document.Items, .. copies] };
                return grid == null ? next : placement!.Expanded ? next with { ExpandedLayout = grid } : next with { CompactLayout = grid };
            }, work, copy: new(destination, targetRoot == null ? copies.Select(item => item.Id).ToArray() : []));
            return copies.Length;
        } finally {
            try { if (work != null) DiscardFlatWork(work); }
            finally { desktopGate.Release(); }
        }
    }

    void ValidateLibraryCopy(FlatTransaction transaction, LibraryCopy copy)
    {
        var moves = transaction.Moves;
        if (copy.Destination == null || transaction.Work is not { } work || transaction.Group != null || transaction.Split != null
            || transaction.NestedGroup != null || transaction.NestedSplit != null || transaction.Edit != null || transaction.Transfer != null
            || moves.Length > MaximumCapacity || copy.DesktopItems == null)
            throw new IOException("文件库复制记录无效。");
        bool desktop = LibraryLocationRoot(copy.Destination) == null;
        if (copy.DesktopItems.Length != (desktop ? moves.Length : 0) || copy.DesktopItems.Contains(Guid.Empty)
            || copy.DesktopItems.Distinct().Count() != copy.DesktopItems.Length)
            throw new IOException("文件库复制项目记录无效。");
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < moves.Length; i++) {
            var move = moves[i];
            Guid id = desktop ? copy.DesktopItems[i] : Guid.Empty;
            string parent = desktop && !IsFlatLibrary ? Path.Combine(DesktopDirectory, id.ToString("N")) : copy.Destination.Directory;
            if (move == null || move.Kind is not (StationKind.File or StationKind.Folder)
                || !ManagedPath(move.From) || !ManagedPath(move.To)
                || !SamePath(Path.GetDirectoryName(move.From), Path.Combine(work, i.ToString()))
                || !SamePath(Path.GetDirectoryName(move.To), parent) || !destinations.Add(move.To)
                || desktop && ReservedFlatNames().Contains(Path.GetFileName(move.To)))
                throw new IOException("文件库复制路径无效。");
            if (desktop) {
                var item = Items.FirstOrDefault(item => item.Id == id);
                if (data.LastStorageOperation == transaction.Id
                    ? item == null || !item.Owned || item.Kind != move.Kind || !SamePath(item.Value, move.To) : item != null)
                    throw new IOException("文件库复制记录与中转站项目不一致。");
            }
        }
    }
}
