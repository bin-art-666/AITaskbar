using System.IO.Compression;
using System.Text.Json;

namespace Halo;
internal sealed partial class StationStore
{
    internal async Task ExportDesktop(string destination) {
        await desktopGate.WaitAsync(); string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try {
            if (ManagedPath(destination)) throw new IOException("备份文件不能放在副本内容目录内。");
            var snapshot = data;
            await Task.Run(() => {
                using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true)) {
                    StationItem Pack(StationItem item) {
                        if (!item.Owned || item.Kind is not (StationKind.File or StationKind.Folder)) return item;
                        if (!ManagedPath(item.Value)) throw new IOException("副本路径无效。");
                        string relative = "files/" + item.Id.ToString("N") + "/" + Path.GetFileName(item.Value);
                        void Add(string source, string entry) {
                            NoLink(source);
                            if (Directory.Exists(source)) { zip.CreateEntry(entry + "/"); foreach (var child in Directory.EnumerateFileSystemEntries(source)) Add(child, entry + "/" + Path.GetFileName(child)); }
                            else { var record = zip.CreateEntry(entry, CompressionLevel.Fastest); using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read); using var output = record.Open(); input.CopyTo(output); }
                        }
                        Add(item.Value, relative); return item with { Value = relative };
                    }
                    var manifest = snapshot with { Version = 4, StorageDirectory = null, LastStorageOperation = null, Items = snapshot.Items.Select(Pack).ToArray(), Trash = (snapshot.Trash ?? []).Select(Pack).ToArray() };
                    var packedPaths = snapshot.Items.Concat(snapshot.Trash ?? []).Zip(manifest.Items.Concat(manifest.Trash!));
                    manifest = manifest with { FolderColors = MoveFolderColors(snapshot.FolderColors, packedPaths
                        .Where(pair => IsOwnedFile(pair.First)).Select(pair => new FlatMove(pair.First.Value,
                            pair.Second.Value.Replace('/', Path.DirectorySeparatorChar), pair.First.Kind))) };
                    using var manifestStream = zip.CreateEntry("halo-desktop.json").Open(); JsonSerializer.Serialize(manifestStream, manifest);
                }
                file.Flush(true);
            });
            File.Move(temporary, destination, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); desktopGate.Release(); }
    }
    internal async Task RestoreDesktop(string source) {
        if (ReadOnly) throw new IOException(LoadWarning);
        await desktopGate.WaitAsync(); string? staging = null; bool committed = false;
        try {
            RequireAvailableLibrary();
            staging = IsFlatLibrary ? CreateFlatWork() : Path.Combine(DesktopDirectory, "restore-" + Guid.NewGuid().ToString("N"));
            StationDocument restored = await Task.Run(() => {
                using var zip = ZipFile.OpenRead(source); var manifestEntry = zip.GetEntry("halo-desktop.json") ?? throw new IOException("不是 Halo 桌面备份。");
                if (manifestEntry.Length > MaximumDocumentBytes || zip.Entries.Count > 100000 || zip.Entries.Sum(e => e.Length) > 20L * 1024 * 1024 * 1024) throw new IOException("备份超过恢复限制（20 GB／十万文件）。");
                using var input = manifestEntry.Open(); var manifest = JsonSerializer.Deserialize<StationDocument>(input) ?? throw new IOException("备份记录无效。");
                if (manifest.Version is not (3 or 4)) throw new IOException("不支持此备份版本。");
                if (!ManagedPath(staging)) throw new IOException("恢复目录无效。");
                Directory.CreateDirectory(staging); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries.Where(e => e.FullName != "halo-desktop.json")) {
                    string name = entry.FullName;
                    if (!name.StartsWith("files/", StringComparison.Ordinal) || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(p => p is "." or "..")) throw new IOException("备份包含不安全路径。");
                    string target = Path.GetFullPath(Path.Combine(staging, name)); if (!ManagedPath(target) || !target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !paths.Add(target)) throw new IOException("备份路径重复或无效。");
                    if (name.EndsWith('/')) Directory.CreateDirectory(target);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); using var from = entry.Open(); using var to = new FileStream(target, FileMode.CreateNew); from.CopyTo(to); to.Flush(true); }
                }
                StationItem Unpack(StationItem item) {
                    if (!item.Owned || item.Kind is not (StationKind.File or StationKind.Folder)) return item;
                    if (!item.Value.StartsWith("files/" + item.Id.ToString("N") + "/", StringComparison.Ordinal) || item.Value.Contains('\\') || item.Value.Split('/').Any(p => p is "." or "..")) throw new IOException("备份副本位置无效。");
                    string value = Path.GetFullPath(Path.Combine(staging, item.Value)); if (!ManagedPath(value) || !File.Exists(value) && !Directory.Exists(value)) throw new IOException("备份缺少文件。");
                    return item with { Value = value };
                }
                var result = manifest with { Version = data.Version, StorageDirectory = data.StorageDirectory, LastStorageOperation = data.LastStorageOperation, Items = manifest.Items.Select(Unpack).ToArray(), Trash = (manifest.Trash ?? []).Select(Unpack).ToArray() };
                var unpackedPaths = manifest.Items.Concat(manifest.Trash ?? []).Zip(result.Items.Concat(result.Trash!));
                result = result with { FolderColors = MoveFolderColors(manifest.FolderColors, unpackedPaths
                    .Where(pair => IsOwnedFile(pair.First)).Select(pair => new FlatMove(pair.First.Value.Replace('/', Path.DirectorySeparatorChar),
                        pair.Second.Value, pair.First.Kind))) };
                Validate(result); return result;
            });
            if (IsFlatLibrary) await RestoreFlatBackup(restored, staging);
            else await Change(_ => restored);
            committed = true;
        } finally {
            try {
                if (staging != null) {
                    if (IsFlatLibrary) DiscardFlatWork(staging);
                    else if (!committed && Directory.Exists(staging)) await Task.Run(() => CleanupOwned(staging));
                }
            } finally { desktopGate.Release(); }
        }
    }

    async Task RestoreFlatBackup(StationDocument restored, string staging)
    {
        var previous = data;
        var moves = new List<FlatMove>();
        var names = FlatNames();
        foreach (var item in previous.Items.Where(IsOwnedFile)) names.Remove(Path.GetFileName(item.Value));
        foreach (var item in previous.Items.Concat(previous.Trash ?? []).Where(IsOwnedFile)) {
            if (!ManagedPath(item.Value)) throw new IOException("副本路径无效。");
            if (EntryExists(item.Value))
                moves.Add(new(item.Value, Path.Combine(staging, "previous", item.Id.ToString("N"), Path.GetFileName(item.Value)), item.Kind));
        }
        StationItem Place(StationItem item, bool trash) {
            if (!IsOwnedFile(item)) return item;
            string target = trash ? FlatTrashPath(DesktopDirectory, item)
                : Path.Combine(DesktopDirectory, AllocateFlatName(names, Path.GetFileName(item.Value), item.Kind));
            moves.Add(new(item.Value, target, item.Kind));
            return item with { Value = target, Title = Path.GetFileName(target) == Path.GetFileName(item.Value) ? item.Title : "" };
        }
        var result = restored with {
            Items = restored.Items.Select(i => Place(i, false)).ToArray(),
            Trash = (restored.Trash ?? []).Select(i => Place(i, true)).ToArray(),
        };
        await ApplyFlatMoves(moves.ToArray(), current => {
            if (!current.Items.SequenceEqual(previous.Items) || !(current.Trash ?? []).SequenceEqual(previous.Trash ?? []))
                throw new IOException("恢复期间文件记录已变化，请重试。");
            return result;
        }, staging);
    }
}
