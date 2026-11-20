namespace Halo;

internal sealed partial class StationStore
{
    sealed record FileGroup(Guid Source, Guid Target, Guid Folder);

    internal static bool CanGroupFile(StationItem? item) => item is { Owned: true, Kind: StationKind.File };

    internal async Task GroupFiles(Guid source, Guid target, bool expanded, int columns)
    {
        await desktopGate.WaitAsync();
        string? work = null;
        try {
            if (ReadOnly) throw new IOException(LoadWarning);
            var from = Items.FirstOrDefault(i => i.Id == source);
            var onto = Items.FirstOrDefault(i => i.Id == target);
            if (source == target || !CanGroupFile(from) || !CanGroupFile(onto))
                throw new IOException("只能将两个 Halo 文件副本合并为文件夹。");
            if (!File.Exists(from!.Value) || !File.Exists(onto!.Value))
                throw new IOException("要合并的文件副本不存在。");

            var names = FlatNames();
            if (!IsFlatLibrary)
                names.UnionWith(Items.Where(i => i.Kind is StationKind.File or StationKind.Folder).Select(i => Path.GetFileName(i.Value)));
            string name = AllocateFlatName(names, "新建文件夹", StationKind.Folder);
            Guid id = Guid.NewGuid();
            string destination = IsFlatLibrary ? Path.Combine(DesktopDirectory, name)
                : Path.Combine(DesktopDirectory, id.ToString("N"), name);
            var folder = new StationItem(id, StationKind.Folder, destination, "", DateTimeOffset.Now, true);
            work = CreateFlatWork();
            string staged = Directory.CreateDirectory(Path.Combine(work, "group")).FullName;
            var childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string Child(StationItem file) => Path.Combine(staged, AllocateFlatName(childNames, Path.GetFileName(file.Value), StationKind.File));
            await ApplyFlatMoves([
                new(onto.Value, Child(onto), StationKind.File),
                new(from.Value, Child(from), StationKind.File),
                new(staged, destination, StationKind.Folder),
            ], document => {
                if (!document.Items.Contains(from) || !document.Items.Contains(onto))
                    throw new IOException("拖动项目已变化，请重试。");
                StationLayout? Replace(StationLayout? layout) => layout == null ? null : layout with {
                    Cells = layout.Cells.Where(c => c.Id != source).Select(c => c.Id == target ? c with { Id = id } : c).ToArray(),
                };
                var compact = document.CompactLayout;
                var standalone = document.ExpandedLayout;
                if (document.Sort == StationSort.Manual) {
                    var basis = SyncLayout((expanded ? standalone : compact) ?? (expanded ? compact : standalone), document.Items)
                        ?? Packed(ManualItems(document).Select(i => i.Id), Math.Clamp(columns, 1, 64));
                    if (expanded) standalone ??= basis;
                    else compact ??= basis;
                }
                return document with {
                    Items = document.Items.Where(i => i.Id != source).Select(i => i.Id == target ? folder : i).ToArray(),
                    ManualOrder = document.ManualOrder?.Where(item => item != source).Select(item => item == target ? id : item).ToArray(),
                    CompactLayout = Replace(compact), ExpandedLayout = Replace(standalone),
                };
            }, work, new(source, target, id));
        } finally {
            try { if (work != null) DiscardFlatWork(work); }
            finally { desktopGate.Release(); }
        }
    }

    void ValidateFileGroup(FlatTransaction transaction, FileGroup group)
    {
        if (group.Source == Guid.Empty || group.Target == Guid.Empty || group.Folder == Guid.Empty
            || group.Source == group.Target || group.Folder == group.Target || group.Folder == group.Source
            || transaction.Work == null || transaction.Moves is not { Length: 3 } moves || moves.Any(m => m == null))
            throw new IOException("合并文件操作记录无效。");
        string staging = Path.Combine(transaction.Work, "group");
        var final = moves[2];
        if (final.Kind != StationKind.Folder || !SamePath(final.From, staging) || !ManagedPath(final.To)
            || !SamePath(Path.GetDirectoryName(final.To), IsFlatLibrary ? DesktopDirectory : Path.Combine(DesktopDirectory, group.Folder.ToString("N")))
            || ReservedFlatNames().Contains(Path.GetFileName(final.To))
            || SamePath(moves[0].From, moves[1].From) || SamePath(moves[0].To, moves[1].To))
            throw new IOException("合并文件路径无效。");
        Guid[] ids = [group.Target, group.Source];
        for (int i = 0; i < ids.Length; i++) {
            var move = moves[i];
            if (move.Kind != StationKind.File || !ManagedPath(move.From) || !ManagedPath(move.To)
                || !SamePath(Path.GetDirectoryName(move.To), staging))
                throw new IOException("合并文件路径无效。");
            if (data.LastStorageOperation == transaction.Id) continue;
            var file = Items.FirstOrDefault(item => item.Id == ids[i]);
            if (!CanGroupFile(file) || !SamePath(file!.Value, move.From))
                throw new IOException("合并文件记录已变化。");
        }
    }
}
