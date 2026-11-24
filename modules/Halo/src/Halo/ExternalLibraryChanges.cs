namespace Halo;

internal sealed partial class StationStore
{
    readonly Dictionary<Guid, (string Path, long Observed)> missingExternalFiles = new();

    internal async Task<bool> SynchronizeExternalFiles(string root, IReadOnlyList<(string From, string To)> renames)
    {
        await desktopGate.WaitAsync();
        try {
            if (ReadOnly || !IsFlatLibrary || !SamePath(root, DesktopDirectory) || !LibraryAccessible()) {
                missingExternalFiles.Clear();
                return false;
            }
            var observed = await Task.Run(() => FindExternalChanges(root, renames));
            var missingIds = observed.Where(change => change.To == null).Select(change => change.Id).ToHashSet();
            foreach (var id in missingExternalFiles.Keys.Where(id => !missingIds.Contains(id)).ToArray())
                missingExternalFiles.Remove(id);
            long now = Environment.TickCount64;
            var candidates = new List<(Guid Id, string From, string? To)>();
            bool needsAnotherCheck = false;
            foreach (var change in observed) {
                if (change.To != null || missingExternalFiles.TryGetValue(change.Id, out var previous)
                    && SamePath(previous.Path, change.From) && now - previous.Observed >= 1000) {
                    candidates.Add(change);
                    continue;
                }
                if (!missingExternalFiles.TryGetValue(change.Id, out previous) || !SamePath(previous.Path, change.From))
                    missingExternalFiles[change.Id] = (change.From, now);
                needsAnotherCheck = true;
            }
            if (candidates.Count == 0 || !LibraryAccessible()) return needsAnotherCheck;
            await Change(document => {
                var items = document.Items.ToList();
                var changes = new List<FlatMove>();
                bool changed = false;
                foreach (var (id, from, to) in candidates) {
                    int index = items.FindIndex(item => item.Id == id && SamePath(item.Value, from));
                    if (index < 0) continue;
                    var item = items[index];
                    if (to != null && EntryMatchesKind(to, item.Kind)
                        && (SamePath(from, to) || ConfirmMissing(from))
                        && !items.Any(other => other.Id != id && SamePath(other.Value, to))) {
                        items[index] = item with { Value = to };
                        changes.Add(new(from, to, item.Kind));
                    }
                    else if (to == null && ConfirmMissing(from)) items.RemoveAt(index);
                    else continue;
                    changed = true;
                }
                return changed && LibraryAccessible()
                    ? document with { Items = items.ToArray(), FolderColors = MoveFolderColors(document.FolderColors, changes) }
                    : document;
            });
            return needsAnotherCheck;
        }
        finally { desktopGate.Release(); }
    }

    (Guid Id, string From, string? To)[] FindExternalChanges(string root, IReadOnlyList<(string From, string To)> renames)
    {
        var result = new List<(Guid, string, string?)>();
        foreach (var item in data.Items.Where(IsOwnedFile)) {
            if (!SamePath(Path.GetDirectoryName(item.Value), root) || !SafeManagedPath(item.Value)) continue;
            string renamed = item.Value;
            foreach (var pair in renames)
                if (SamePath(renamed, pair.From)) renamed = pair.To;
            if (renamed != item.Value && SamePath(Path.GetDirectoryName(renamed), root)
                && !ReservedFlatNames().Contains(Path.GetFileName(renamed)) && SafeManagedPath(renamed)
                && EntryMatchesKind(renamed, item.Kind) && (SamePath(renamed, item.Value) || ConfirmMissing(item.Value)))
                result.Add((item.Id, item.Value, renamed));
            else if (ConfirmMissing(item.Value)) result.Add((item.Id, item.Value, null));
        }
        return result.ToArray();
    }

    bool SafeManagedPath(string value)
    {
        try { return ManagedPath(value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    static bool ConfirmMissing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    static bool EntryMatchesKind(string path, StationKind kind)
    {
        try {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.ReparsePoint)
                && attributes.HasFlag(FileAttributes.Directory) == (kind == StationKind.Folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
