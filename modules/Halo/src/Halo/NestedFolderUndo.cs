namespace Halo;

internal sealed record FolderUndo(string Label, Func<Task> Apply, Func<bool>? IsCurrent = null);

internal sealed partial class StationStore
{
    sealed record UndoStamp(string Path, bool Directory, long Length, DateTime Written, DateTime Created, string? ContentHash);

    static UndoStamp[] SnapshotUndoPaths(IEnumerable<string> paths)
    {
        var result = new List<UndoStamp>();
        var remaining = new Stack<string>(paths.Reverse());
        while (remaining.TryPop(out string? path)) {
            NoLink(path);
            if (Directory.Exists(path)) {
                var folder = new DirectoryInfo(path);
                result.Add(new(path, true, 0, folder.LastWriteTimeUtc, folder.CreationTimeUtc, null));
                foreach (var child in Directory.EnumerateFileSystemEntries(path).OrderDescending(StringComparer.Ordinal))
                    remaining.Push(child);
            } else {
                var file = new FileInfo(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                result.Add(new(path, false, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc, hash));
            }
        }
        return result.ToArray();
    }

    async Task<FolderUndo?> CreateFolderUndo(StationItem root, string directory, FolderGridLayout? before,
        string label, string[] paths, Func<Func<StationDocument, StationDocument>, Task> reverse)
    {
        var after = SavedFolderGrid(root, directory);
        UndoStamp[] snapshot;
        try { snapshot = await Task.Run(() => SnapshotUndoPaths(paths)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { App.Log(error); return null; }
        return new(label, async () => {
            await desktopGate.WaitAsync();
            try {
                ValidateFolderPath(root, directory, write: true);
                if (SavedFolderGrid(root, directory) != after) throw new IOException("格子布局已变化，无法撤销。");
                UndoStamp[] now;
                try { now = await Task.Run(() => SnapshotUndoPaths(paths)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                    throw new IOException("文件夹或文件已移动，或者无法读取。", error);
                }
                if (!snapshot.SequenceEqual(now)) throw new IOException("整理后的文件已被修改，无法撤销。");
                await reverse(document => WithFolderLayout(document, root, directory, current => {
                    if (after != null && after != current) throw new IOException("格子布局已变化，无法撤销。");
                    return current with { Grid = before?.Grid, Sort = before?.Sort ?? FolderSort.Manual };
                }));
            } finally { desktopGate.Release(); }
        });
    }

    static FlatMove[] ReverseFolderMoves(IEnumerable<FlatMove> moves) =>
        moves.Reverse().Select(move => new FlatMove(move.To, move.From, move.Kind)).ToArray();

    static void CheckUndoDestinations(IEnumerable<FlatMove> moves)
    {
        var conflict = moves.FirstOrDefault(move => EntryExists(move.To));
        if (conflict != null) throw new IOException($"「{Path.GetFileName(conflict.To)}」已占用原位置，无法撤销。");
    }
}
