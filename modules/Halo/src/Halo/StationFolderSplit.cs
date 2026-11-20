namespace Halo;

internal sealed partial class StationStore
{
    sealed record FolderSplit(Guid Folder, Guid[] Children);

    internal static bool CanDissolveFolder(StationItem? item) => item is { Owned: true, Kind: StationKind.Folder };

    static StationLayout? ReplaceDissolvedFolder(StationLayout? layout, Guid folderId, StationItem[] children) =>
        layout == null ? null : layout with {
            Cells = layout.Cells.SelectMany(cell => cell.Id != folderId ? [cell]
                : children.Length == 0 ? Array.Empty<StationCell>() : [cell with { Id = children[0].Id }]).ToArray(),
        };

    internal async Task<int> DissolveFolder(Guid id)
    {
        await desktopGate.WaitAsync();
        try {
            if (ReadOnly) throw new IOException(LoadWarning);
            if (File.Exists(FlatJournal)) throw new IOException("有未完成的文件操作，请先重启 Halo。");
            var folder = Items.FirstOrDefault(item => item.Id == id);
            if (folder == null || !CanDissolveFolder(folder) || !ManagedPath(folder.Value))
                throw new IOException("只能解散 Halo 文件夹副本。");
            int space = data.Capacity - Items.Count + 1;
            var entries = Directory.EnumerateFileSystemEntries(folder.Value).Take(Math.Max(1, space + 1))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
            if (entries.Length > Math.Max(0, space)) throw new IOException("中转站容量不足，无法解散文件夹。");

            var names = FlatNames();
            var children = new StationItem[entries.Length];
            var moves = new List<FlatMove>(entries.Length + 1);
            for (int i = 0; i < entries.Length; i++) {
                string entry = entries[i];
                if (!ManagedPath(entry)) throw new IOException("文件夹内有不可移动的项目。");
                var kind = File.GetAttributes(entry).HasFlag(FileAttributes.Directory) ? StationKind.Folder : StationKind.File;
                Guid childId = Guid.NewGuid();
                string name = Path.GetFileName(entry);
                string destination = IsFlatLibrary ? Path.Combine(DesktopDirectory, AllocateFlatName(names, name, kind))
                    : Path.Combine(DesktopDirectory, childId.ToString("N"), name);
                children[i] = new(childId, kind, destination, "", DateTimeOffset.Now, true);
                moves.Add(new(entry, destination, kind));
            }
            var archived = IsFlatLibrary ? folder with { Value = FlatTrashPath(DesktopDirectory, folder) } : folder;
            if (IsFlatLibrary) moves.Add(new(folder.Value, archived.Value, StationKind.Folder));
            await ApplyFlatMoves(moves.ToArray(), document => {
                if (!document.Items.Contains(folder)) throw new IOException("文件夹已变化，请重试。");
                if (children.Length > 0 && document.Items.Length - 1 + children.Length > document.Capacity)
                    throw new IOException("中转站容量不足，无法解散文件夹。");
                if (Directory.EnumerateFileSystemEntries(archived.Value).Any())
                    throw new IOException("文件夹内容已变化，请重试。");
                return document with {
                    Items = document.Items.SelectMany(item => item.Id == id ? children : [item]).ToArray(),
                    Trash = [.. document.Trash ?? [], archived],
                    ManualOrder = document.ManualOrder?.SelectMany(item => item == id ? children.Select(child => child.Id) : [item]).ToArray(),
                    CompactLayout = ReplaceDissolvedFolder(document.CompactLayout, id, children),
                    ExpandedLayout = ReplaceDissolvedFolder(document.ExpandedLayout, id, children),
                };
            }, split: new(id, children.Select(child => child.Id).ToArray()));
            return children.Length;
        } finally { desktopGate.Release(); }
    }

    void ValidateFolderSplit(FlatTransaction transaction, FolderSplit split)
    {
        if (transaction.Group != null || transaction.Work != null || split.Folder == Guid.Empty
            || split.Children == null || split.Children.Length > MaximumCapacity
            || split.Children.Contains(Guid.Empty) || split.Children.Contains(split.Folder)
            || split.Children.Distinct().Count() != split.Children.Length
            || transaction.Moves.Length != split.Children.Length + (IsFlatLibrary ? 1 : 0))
            throw new IOException("解散文件夹操作记录无效。");

        bool committed = data.LastStorageOperation == transaction.Id;
        var folder = (committed ? Trash : Items).FirstOrDefault(item => item.Id == split.Folder);
        if (folder == null || !CanDissolveFolder(folder)) throw new IOException("解散的文件夹记录已变化。");
        string root = DesktopDirectory;
        string sourceParent = IsFlatLibrary ? root : Path.Combine(root, folder.Id.ToString("N"));
        string source = Path.Combine(sourceParent, Path.GetFileName(folder.Value));
        var trash = FlatTrashPath(root, folder);
        if (!ManagedPath(source) || ReservedFlatNames().Contains(Path.GetFileName(source))
            || !SamePath(folder.Value, committed && IsFlatLibrary ? trash : source))
            throw new IOException("解散的文件夹路径无效。");

        if (IsFlatLibrary && (transaction.Moves[^1] is not { Kind: StationKind.Folder } archive
            || !SamePath(archive.From, source) || !SamePath(archive.To, trash) || !ManagedPath(trash)))
            throw new IOException("解散的文件夹回收路径无效。");

        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source };
        for (int i = 0; i < split.Children.Length; i++) {
            var move = transaction.Moves[i];
            string parent = IsFlatLibrary ? root : Path.Combine(root, split.Children[i].ToString("N"));
            if (move == null || move.Kind is not (StationKind.File or StationKind.Folder)
                || !SamePath(Path.GetDirectoryName(move.From), source) || !ManagedPath(move.From)
                || !SamePath(Path.GetDirectoryName(move.To), parent) || !ManagedPath(move.To)
                || IsFlatLibrary && ReservedFlatNames().Contains(Path.GetFileName(move.To))
                || !sources.Add(move.From) || !destinations.Add(move.To)
                || committed && !Items.Any(item => item.Id == split.Children[i] && item.Owned && item.Kind == move.Kind && SamePath(item.Value, move.To)))
                throw new IOException("解散文件夹的内容路径无效。");
        }
    }
}
