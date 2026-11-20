using System.Security.Cryptography;

namespace Halo;

internal sealed record LibraryMigrationEntry(Guid Id, string Source, string Relative, StationKind Kind, bool InTrash);
internal sealed record LibraryMigrationPlan(string Destination, LibraryMigrationEntry[] Entries);

internal sealed partial class StationStore
{
    string MigrationJournal => Path.Combine(Path.GetDirectoryName(path)!, "storage-migration.json");
    sealed record PendingLibraryMigration(Guid Id, string Destination);

    internal static string ProposeStorageDirectory(string parent) =>
        Path.Combine(CanonicalLibraryPath(parent), "Halo 文件库（平铺）-" + Guid.NewGuid().ToString("N")[..8]);

    internal LibraryMigrationPlan PreviewStorageMigration(string destination) => PlanStorageMigration(data, CanonicalLibraryPath(destination));

    static LibraryMigrationPlan PlanStorageMigration(StationDocument document, string destination)
    {
        var used = ReservedFlatNames();
        var unavailable = ReservedFlatNames();
        unavailable.UnionWith(document.Items.Where(IsOwnedFile).Select(i => Path.GetFileName(i.Value)));
        var entries = new List<LibraryMigrationEntry>();
        foreach (var item in document.Items.Where(IsOwnedFile)) {
            string sourceName = Path.GetFileName(item.Value);
            string name = used.Add(sourceName) ? sourceName : AllocateFlatName(unavailable, sourceName, item.Kind);
            entries.Add(new(item.Id, item.Value, name, item.Kind, false));
        }
        foreach (var item in (document.Trash ?? []).Where(IsOwnedFile))
            entries.Add(new(item.Id, item.Value, Path.Combine(FlatTrash, item.Id.ToString("N"), Path.GetFileName(item.Value)), item.Kind, true));
        return new(destination, entries.ToArray());
    }

    internal Task MoveStorage(string destination, CancellationToken token) => MoveStorage(PreviewStorageMigration(destination), token);

    internal async Task MoveStorage(LibraryMigrationPlan plan, CancellationToken token)
    {
        string destination = CanonicalLibraryPath(plan.Destination);
        await desktopGate.WaitAsync(token);
        PendingLibraryMigration? pending = null;
        try {
            if (ReadOnly) throw new IOException(LoadWarning);
            RequireAvailableLibrary();
            string source = DesktopDirectory;
            string parent = Path.GetDirectoryName(destination) ?? throw new IOException("请选择文件库的上级目录。");
            CheckLibraryParents(parent);
            if (!Directory.Exists(parent) || new DriveInfo(Path.GetPathRoot(parent)!).DriveType != DriveType.Fixed)
                throw new IOException("请选择本机固定磁盘上已存在的文件夹。");
            if (Below(destination, source) || Below(source, destination) || SamePath(source, destination))
                throw new IOException("请选择现有文件库以外的位置。");
            if (EntryExists(destination)) throw new IOException("目标文件库已存在。请重新选择位置。");
            string journals = Path.Combine(Path.GetDirectoryName(path)!, "rename-journals");
            if (File.Exists(MigrationJournal) || File.Exists(FlatJournal) || Directory.Exists(journals) && Directory.EnumerateFiles(journals, "*.json").Any())
                throw new IOException("有未完成的文件操作，请先重启 Halo。");
            if (!PlanStorageMigration(data, destination).Entries.SequenceEqual(plan.Entries))
                throw new IOException("文件记录已变化，请重新预览迁移。");

            var transaction = new PendingLibraryMigration(Guid.NewGuid(), destination);
            Directory.CreateDirectory(Path.GetDirectoryName(MigrationJournal)!);
            WriteStorageJournal(MigrationJournal, transaction);
            pending = transaction;
            string temporary = MigrationStaging(transaction);
            await Task.Run(() => {
                token.ThrowIfCancellationRequested();
                CheckLibraryParents(parent);
                if (EntryExists(temporary)) throw new IOException("迁移临时目录已存在。");
                Directory.CreateDirectory(temporary);
                File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.Hidden);
                string marker = Path.Combine(temporary, MigrationOwner);
                using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    stream.Write(System.Text.Encoding.ASCII.GetBytes(transaction.Id.ToString("N")));
                    stream.Flush(true);
                }
                File.SetAttributes(marker, File.GetAttributes(marker) | FileAttributes.Hidden);
                foreach (var entry in plan.Entries) {
                    if (!ManagedPath(entry.Source)) throw new IOException("副本路径超出 Halo 文件库。");
                    string copy = Path.Combine(temporary, entry.Relative);
                    CheckLibraryParents(copy);
                    if (entry.InTrash) EnsurePrivateFolder(temporary, FlatTrash);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    CopyTree(entry.Source, copy, token);
                    VerifyCopiedTree(entry.Source, copy, token);
                }
                token.ThrowIfCancellationRequested();
                CheckLibraryParents(temporary);
                Directory.Move(temporary, destination);
            }, token);

            var entries = plan.Entries.ToDictionary(i => i.Id);
            await Change(document => {
                token.ThrowIfCancellationRequested();
                if (!PlanStorageMigration(document, destination).Entries.SequenceEqual(plan.Entries))
                    throw new IOException("复制期间文件记录已变化，请重新迁移。");
                StationItem Remap(StationItem item) => entries.TryGetValue(item.Id, out var entry)
                    ? item with { Value = Path.Combine(destination, entry.Relative),
                        Title = Path.GetFileName(item.Value) == Path.GetFileName(entry.Relative) ? item.Title : "" } : item;
                return document with {
                    Version = 6, StorageDirectory = destination, LastStorageOperation = null,
                    Items = document.Items.Select(Remap).ToArray(),
                    Trash = document.Trash?.Select(Remap).ToArray(),
                    FolderColors = MoveFolderColors(document.FolderColors, plan.Entries.Select(entry =>
                        new FlatMove(entry.Source, Path.Combine(destination, entry.Relative), entry.Kind))),
                };
            });
        }
        finally {
            try { if (pending != null) CompleteLibraryMigration(pending); }
            catch (Exception error) { App.Log(error); }
            finally { desktopGate.Release(); }
        }
    }

    static string MigrationStaging(PendingLibraryMigration transaction) =>
        Path.Combine(Path.GetDirectoryName(transaction.Destination)!, ".Halo-migration-" + transaction.Id.ToString("N"));

    void RecoverLibraryMigration()
    {
        if (!File.Exists(MigrationJournal)) return;
        var transaction = System.Text.Json.JsonSerializer.Deserialize<PendingLibraryMigration>(File.ReadAllText(MigrationJournal))
            ?? throw new IOException("文件库迁移记录无效。");
        CompleteLibraryMigration(transaction);
    }

    void CompleteLibraryMigration(PendingLibraryMigration transaction)
    {
        string destination = CanonicalLibraryPath(transaction.Destination);
        if (transaction.Id == Guid.Empty || !SamePath(destination, transaction.Destination) || SamePath(destination, Path.GetPathRoot(destination))
            || !SamePath(DesktopDirectory, destination) && (Below(destination, DesktopDirectory) || Below(DesktopDirectory, destination)))
            throw new IOException("文件库迁移记录无效。");
        foreach (string candidate in new[] { MigrationStaging(transaction), destination }) {
            string marker = Path.Combine(candidate, MigrationOwner);
            CheckLibraryParents(marker);
            if (!File.Exists(marker) || File.ReadAllText(marker) != transaction.Id.ToString("N")) continue;
            if (SamePath(candidate, DesktopDirectory)) {
                File.SetAttributes(candidate, File.GetAttributes(candidate) & ~FileAttributes.Hidden);
                File.Delete(marker);
            }
            else DeleteOwnedTree(candidate);
        }
        File.Delete(MigrationJournal);
    }

    static string CanonicalLibraryPath(string value)
    {
        if (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\'
            || value.Length > 32767 || value.Any(char.IsControl) || value.AsSpan(2).Contains(':'))
            throw new IOException("文件库必须使用本机磁盘上的完整路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    static bool Below(string candidate, string root) =>
        candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    static void CheckLibraryParents(string value)
    {
        // Check from the drive down so an existing junction is never traversed.
        var ancestors = new Stack<string>();
        for (string? part = value; part != null; part = Path.GetDirectoryName(part)) ancestors.Push(part);
        foreach (string part in ancestors) {
            try { NoLink(part); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
    }

    static void VerifyCopiedTree(string source, string copy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NoLink(source);
        NoLink(copy);
        if (Directory.Exists(source)) {
            var originals = Directory.GetFileSystemEntries(source).ToDictionary(p => Path.GetFileName(p)!, StringComparer.Ordinal);
            var copies = Directory.GetFileSystemEntries(copy);
            if (originals.Count != copies.Length) throw new IOException("迁移后的目录内容与原目录不一致。");
            foreach (string entry in copies) {
                if (!originals.TryGetValue(Path.GetFileName(entry), out var original)) throw new IOException("迁移后的文件名校验失败。");
                VerifyCopiedTree(original, entry, token);
            }
            return;
        }
        using var originalFile = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var copiedFile = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (originalFile.Length != copiedFile.Length
            || !SHA256.HashDataAsync(originalFile, token).AsTask().GetAwaiter().GetResult().AsSpan()
                .SequenceEqual(SHA256.HashDataAsync(copiedFile, token).AsTask().GetAwaiter().GetResult()))
            throw new IOException("迁移后的文件校验失败。");
    }
}
