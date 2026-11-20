using System.Security.Cryptography;
using System.Text;

namespace Halo;

internal enum FolderSort { Manual, Name, Newest, Oldest }
internal sealed record FolderGridLayout(Guid FolderId, string DirectoryKey, StationLayout? Grid, FolderSort Sort = FolderSort.Manual);

internal sealed partial class StationStore
{
    static byte[] FolderGridHash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    static string FolderDirectoryKey(StationItem folder, string directory) =>
        Convert.ToHexString(FolderGridHash(Path.GetRelativePath(folder.Value, directory).ToUpperInvariant()));

    internal static Guid FolderEntryId(string path) => new(FolderGridHash(Path.GetFileName(path)).AsSpan(0, 16));

    FolderGridLayout? SavedFolderGrid(StationItem folder, string directory) =>
        FindFolderGrid(data, folder.Id, FolderDirectoryKey(folder, directory));

    static FolderGridLayout? FindFolderGrid(StationDocument document, Guid folder, string key) =>
        document.FolderLayouts?.FirstOrDefault(layout => layout.FolderId == folder && layout.DirectoryKey == key);

    internal FolderSort SortFolder(StationItem folder, string directory) => SavedFolderGrid(folder, directory)?.Sort ?? FolderSort.Manual;

    internal static IEnumerable<StationItem> FolderDisplayOrder(IEnumerable<StationItem> entries, FolderSort sort) => sort switch {
        FolderSort.Name => entries.OrderByDescending(item => item.Kind == StationKind.Folder)
            .ThenBy(item => Path.GetFileName(item.Value), StringComparer.CurrentCultureIgnoreCase),
        FolderSort.Newest => entries.OrderByDescending(item => item.Created),
        FolderSort.Oldest => entries.OrderBy(item => item.Created),
        _ => entries,
    };

    internal StationLayout FolderDisplayGrid(StationItem folder, string directory, StationItem[] entries, int columns)
    {
        var sort = SortFolder(folder, directory);
        return sort == FolderSort.Manual ? FolderGrid(folder, directory, entries, columns)
            : Packed(FolderDisplayOrder(entries, sort).Select(item => item.Id), Math.Clamp(columns, 1, 64));
    }

    internal StationLayout FolderGrid(StationItem folder, string directory, StationItem[] entries, int columns)
    {
        var saved = SavedFolderGrid(folder, directory)?.Grid;
        return SyncLayout(saved == null ? null : Widen(saved, columns), entries)
            ?? Packed(entries.Select(item => item.Id), Math.Clamp(columns, 1, 64));
    }

    internal async Task SaveFolderMove(StationItem folder, string directory, Guid[] moving, Guid anchor,
        int column, int row, int columns)
    {
        await desktopGate.WaitAsync();
        try {
            var entries = await Task.Run(() => ReadFolderContents(folder, directory, CancellationToken.None));
            await UpdateFolderLayout(folder, directory, state => {
                if (state.Sort != FolderSort.Manual) throw new IOException("切回手动排序后才能调整位置。");
                var original = FolderGrid(folder, directory, entries, columns);
                var updated = MoveLayout(original, moving, anchor, column, row);
                return updated == original ? state : state with { Grid = updated };
            });
        } finally { desktopGate.Release(); }
    }

    internal Task SetFolderSort(StationItem folder, string directory, FolderSort sort) =>
        UpdateFolderLayout(folder, directory, state => state with { Sort = sort });

    internal async Task ArrangeFolderGrid(StationItem folder, string directory, int columns)
    {
        await desktopGate.WaitAsync();
        try {
            var entries = await Task.Run(() => ReadFolderContents(folder, directory, CancellationToken.None));
            await UpdateFolderLayout(folder, directory, state => {
                if (state.Sort != FolderSort.Manual) throw new IOException("切回手动排序后才能整理空位。");
                var grid = FolderGrid(folder, directory, entries, columns);
                var ids = grid.Cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).Select(cell => cell.Id);
                return state with { Grid = Packed(ids, Math.Clamp(columns, 1, 64)) };
            });
        } finally { desktopGate.Release(); }
    }

    Task UpdateFolderLayout(StationItem folder, string directory, Func<FolderGridLayout, FolderGridLayout> update)
    {
        ValidateFolderPath(folder, directory);
        return Change(document => WithFolderLayout(document, folder, directory, update));
    }

    static StationDocument WithFolderLayout(StationDocument document, StationItem folder, string directory,
        Func<FolderGridLayout, FolderGridLayout> update)
    {
        string key = FolderDirectoryKey(folder, directory);
        if (!document.Items.Any(item => item.Id == folder.Id && item.Kind == StationKind.Folder && SamePath(item.Value, folder.Value)))
            throw new IOException("文件夹已移动或移除。");
        var current = FindFolderGrid(document, folder.Id, key) ?? new(folder.Id, key, null);
        var updated = update(current);
        if (updated == current) return document;
        return document with {
            FolderLayouts = [.. (document.FolderLayouts ?? []).Where(layout => layout.FolderId != folder.Id || layout.DirectoryKey != key), updated],
        };
    }

    static void ValidateFolderLayouts(FolderGridLayout[]? layouts)
    {
        if (layouts == null) return;
        var keys = new HashSet<(Guid, string)>();
        foreach (var layout in layouts) {
            if (layout == null || layout.FolderId == Guid.Empty || layout.DirectoryKey is not { Length: 64 }
                || !layout.DirectoryKey.All(Uri.IsHexDigit) || !Enum.IsDefined(layout.Sort)
                || !keys.Add((layout.FolderId, layout.DirectoryKey)))
                throw new InvalidDataException("文件夹网格位置记录无效。");
            ValidateLayout(layout.Grid);
        }
    }
}
