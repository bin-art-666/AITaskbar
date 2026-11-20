namespace Halo;

internal sealed record LibraryLocation(Guid? RootId, string Directory);

internal sealed partial class StationStore
{
    sealed record LibraryMove(LibraryLocation Source, LibraryLocation Destination, Guid[] DesktopItems);

    internal LibraryLocation LibraryRoot => new(null, DesktopDirectory);

    internal static bool CanMoveLibraryItems(IReadOnlyList<StationItem> items) =>
        items.Count is > 0 and <= MaximumCapacity && items.All(IsOwnedFile);

    internal static bool CanMoveTo(LibraryLocation source, LibraryLocation destination, StationItem[] selected) =>
        !SamePath(source.Directory, destination.Directory) && !selected.Any(item =>
            item.Kind == StationKind.Folder && (SamePath(item.Value, destination.Directory) || Below(destination.Directory, item.Value)));

    StationItem? LibraryLocationRoot(LibraryLocation location)
    {
        if (location.RootId == null) {
            if (!SamePath(location.Directory, DesktopDirectory)) throw new IOException("中转站文件库路径已变化。");
            CheckLibraryParents(DesktopDirectory);
            return null;
        }
        var root = Items.FirstOrDefault(item => item.Id == location.RootId && CanDissolveFolder(item))
            ?? throw new IOException("Halo 文件夹已移动或移除。");
        if (!SamePath(ValidateFolderPath(root, location.Directory), location.Directory) || !Directory.Exists(location.Directory))
            throw new IOException("Halo 文件夹已移动或移除。");
        return root;
    }

    internal LibraryLocation[] LibrarySubfolders(LibraryLocation location)
    {
        var root = LibraryLocationRoot(location);
        var children = root == null ? Items.Where(CanDissolveFolder).Select(item => new LibraryLocation(item.Id, item.Value))
            : EnumerateFolderContents(root, location.Directory, default).OfType<DirectoryInfo>()
                .Select(item => new LibraryLocation(root.Id, item.FullName));
        return children.Where(item => Directory.Exists(item.Directory))
            .OrderBy(item => Path.GetFileName(item.Directory), StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal async Task<FolderUndo> MoveLibraryItems(LibraryLocation source, LibraryLocation destination, StationItem[] selected,
        int columns)
    {
        await desktopGate.WaitAsync();
        try {
            if (ReadOnly) throw new IOException(LoadWarning);
            if (!CanMoveLibraryItems(selected) || selected.Select(item => item.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length
                || !CanMoveTo(source, destination, selected))
                throw new IOException("请选择可移动的 Halo 副本，以及另一个目标文件夹。");
            var sourceRoot = LibraryLocationRoot(source);
            var destinationRoot = LibraryLocationRoot(destination);
            if (sourceRoot == null && destinationRoot == null) throw new IOException("请选择另一个目标文件夹。");
            var before = data;
            if (destinationRoot == null && before.Items.Length + selected.Length > before.Capacity)
                throw new IOException("中转站容量不足，本批未移动。");
            var sourceEntries = sourceRoot == null ? before.Items : ReadFolderContents(sourceRoot, source.Directory, default);
            var destinationEntries = destinationRoot == null ? before.Items : ReadFolderContents(destinationRoot, destination.Directory, default);
            foreach (var item in selected) {
                if (!sourceEntries.Contains(item)) throw new IOException("待移动的项目已变化，请重新选择。");
                if (sourceRoot != null) CheckNestedEntry(source.Directory, item);
            }
            var creationTimes = selected.Select(item => File.GetCreationTimeUtc(item.Value)).ToArray();
            var names = destinationRoot != null ? FolderNames(destination.Directory) : FlatNames();
            if (destinationRoot == null && !IsFlatLibrary)
                names.UnionWith(before.Items.Where(IsOwnedFile).Select(item => Path.GetFileName(item.Value)));
            var moved = selected.Select(item => {
                Guid id = destinationRoot == null ? Guid.NewGuid() : Guid.Empty;
                string parent = destinationRoot == null && !IsFlatLibrary ? Path.Combine(DesktopDirectory, id.ToString("N")) : destination.Directory;
                string value = Path.Combine(parent, AllocateFlatName(names, Path.GetFileName(item.Value), item.Kind));
                return item with { Id = destinationRoot == null ? id : FolderEntryId(value), Value = value, Title = "" };
            }).ToArray();
            var moves = selected.Zip(moved, (item, next) => new FlatMove(item.Value, next.Value, item.Kind)).ToArray();
            var topIds = (sourceRoot == null ? selected : destinationRoot == null ? moved : []).Select(item => item.Id).ToArray();
            var updated = WithMovedFolderMetadata(before, selected, moved, sourceRoot, destinationRoot);
            if (sourceRoot == null) {
                var ids = topIds.ToHashSet();
                updated = updated with { Items = before.Items.Where(item => !ids.Contains(item.Id)).ToArray() };
            } else {
                var ids = selected.Select(item => item.Id).ToHashSet();
                updated = WithFolderLayout(updated, sourceRoot, source.Directory, state => state with {
                    Grid = SyncLayout(FolderGrid(sourceRoot, source.Directory, sourceEntries, columns),
                        sourceEntries.Where(item => !ids.Contains(item.Id)).ToArray()),
                });
            }
            if (destinationRoot == null) updated = updated with { Items = [.. updated.Items, .. moved] };
            else updated = WithFolderLayout(updated, destinationRoot, destination.Directory, state => state with {
                Grid = SyncLayout(FolderGrid(destinationRoot, destination.Directory, destinationEntries, columns), [.. destinationEntries, .. moved]),
            });
            var after = await ApplyFlatMoves(moves, document => ReferenceEquals(document, before) ? updated
                : throw new IOException("文件库已变化，请重试。"), transfer: new(source, destination, topIds));
            return new($"撤销移动 · {selected.Length} 项", async () => {
                await desktopGate.WaitAsync();
                try {
                    if (!ReferenceEquals(data, after)) throw new IOException("文件库已发生其他更改，无法撤销这次移动。");
                    if (moved.Where((item, index) => !EntryExists(item.Value) ||
                            File.GetCreationTimeUtc(item.Value) != creationTimes[index]).Any())
                        throw new IOException("移动后的项目已替换或移除，无法撤销。");
                    var reverse = ReverseFolderMoves(moves);
                    CheckUndoDestinations(reverse);
                    await ApplyFlatMoves(reverse, current => ReferenceEquals(current, after)
                        ? before with { FolderColors = current.FolderColors }
                        : throw new IOException("文件库已变化，无法撤销。"),
                        transfer: new(destination, source, topIds.Reverse().ToArray()));
                } finally { desktopGate.Release(); }
            }, () => ReferenceEquals(data, after));
        } finally { desktopGate.Release(); }
    }

    static StationDocument WithMovedFolderMetadata(StationDocument document, StationItem[] from, StationItem[] to,
        StationItem? sourceRoot, StationItem? destinationRoot)
    {
        var layouts = (document.FolderLayouts ?? []).ToDictionary(layout => (layout.FolderId, layout.DirectoryKey));
        var trash = document.Trash;
        for (int i = 0; i < from.Length; i++) {
            var source = from[i];
            if (source.Kind != StationKind.Folder) continue;
            var destination = to[i];
            var oldRoot = sourceRoot ?? source;
            var newRoot = destinationRoot ?? destination;
            var remaining = new Stack<DirectoryInfo>();
            remaining.Push(new(source.Value));
            while (remaining.TryPop(out var directory)) {
                NoLink(directory.FullName);
                string newPath = Path.GetFullPath(Path.Combine(destination.Value, Path.GetRelativePath(source.Value, directory.FullName)));
                var newKey = (newRoot.Id, FolderDirectoryKey(newRoot, newPath));
                layouts.Remove(newKey);
                if (layouts.Remove((oldRoot.Id, FolderDirectoryKey(oldRoot, directory.FullName)), out var previous))
                    layouts.Add(newKey, previous with { FolderId = newRoot.Id, DirectoryKey = newKey.Item2 });
                foreach (var child in directory.EnumerateDirectories()) remaining.Push(child);
            }
            if (trash != null)
                trash = trash.Select(item => {
                    if (item.Origin is not { } origin || origin.RootId != oldRoot.Id) return item;
                    string parent = Path.GetFullPath(Path.Combine(oldRoot.Value, origin.RelativeParent));
                    if (!SamePath(parent, source.Value) && !Below(parent, source.Value)) return item;
                    string relocated = Path.GetFullPath(Path.Combine(destination.Value, Path.GetRelativePath(source.Value, parent)));
                    return item with { Origin = origin with { RootId = newRoot.Id, RelativeParent = Path.GetRelativePath(newRoot.Value, relocated) } };
                }).ToArray();
        }
        return document with { FolderLayouts = layouts.Count == 0 ? null : layouts.Values.ToArray(), Trash = trash };
    }

    void ValidateLibraryMove(FlatTransaction transaction, LibraryMove transfer)
    {
        var source = transfer.Source;
        var destination = transfer.Destination;
        var moves = transaction.Moves;
        if (source == null || destination == null || transaction.Work != null || transaction.Group != null || transaction.Split != null
            || transaction.NestedGroup != null || transaction.NestedSplit != null || transaction.Edit != null
            || moves.Length > MaximumCapacity || SamePath(source.Directory, destination.Directory)
            || (source.RootId == null && destination.RootId == null) || transfer.DesktopItems == null)
            throw new IOException("文件库移动记录无效。");
        _ = LibraryLocationRoot(source);
        _ = LibraryLocationRoot(destination);
        bool usesDesktop = source.RootId == null || destination.RootId == null;
        if (transfer.DesktopItems.Length != (usesDesktop ? moves.Length : 0) || transfer.DesktopItems.Contains(Guid.Empty)
            || transfer.DesktopItems.Distinct().Count() != transfer.DesktopItems.Length)
            throw new IOException("文件库移动项目记录无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Valid(string value, LibraryLocation location, Guid id) {
            if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value) || !SamePath(Path.GetFullPath(value), value)
                || !ManagedPath(value) || !paths.Add(value)) return false;
            string parent = location.RootId == null && !IsFlatLibrary ? Path.Combine(DesktopDirectory, id.ToString("N")) : location.Directory;
            return SamePath(Path.GetDirectoryName(value), parent)
                && (location.RootId != null || !ReservedFlatNames().Contains(Path.GetFileName(value)));
        }
        bool committed = data.LastStorageOperation == transaction.Id;
        for (int i = 0; i < moves.Length; i++) {
            var move = moves[i];
            Guid id = usesDesktop ? transfer.DesktopItems[i] : Guid.Empty;
            if (move == null || move.Kind is not (StationKind.File or StationKind.Folder)
                || !Valid(move.From, source, id) || !Valid(move.To, destination, id)
                || move.Kind == StationKind.Folder && Below(destination.Directory, move.From))
                throw new IOException("文件库移动路径无效。");
            if (usesDesktop) {
                bool onDesktop = committed ? destination.RootId == null : source.RootId == null;
                var item = Items.FirstOrDefault(item => item.Id == id);
                if (onDesktop ? item == null || !IsOwnedFile(item) || item.Kind != move.Kind
                        || !SamePath(item.Value, committed ? move.To : move.From) : item != null)
                    throw new IOException("文件库移动记录与中转站项目不一致。");
            }
        }
    }
}
