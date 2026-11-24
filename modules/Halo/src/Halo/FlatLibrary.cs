using System.Text.Json;

namespace Halo;

internal sealed partial class StationStore
{
    const string FlatTrash = ".halo-trash", FlatWork = ".halo-work", MigrationOwner = ".halo-migration-owner";
    bool IsFlatLibrary => data.Version >= 6;
    string FlatJournal => Path.Combine(Path.GetDirectoryName(path)!, "flat-library-moves.json");

    sealed record FlatMove(string From, string To, StationKind Kind);
    sealed record FlatTransaction(Guid Id, FlatMove[] Moves, string? Work, FileGroup? Group = null, FolderSplit? Split = null,
        NestedFileGroup? NestedGroup = null, NestedFolderSplit? NestedSplit = null, NestedEdit? Edit = null, LibraryMove? Transfer = null,
        LibraryCopy? Copy = null);

    static bool IsOwnedFile(StationItem item) => item.Owned && item.Kind is StationKind.File or StationKind.Folder;
    static bool SamePath(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    static bool EntryExists(string path) {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    static HashSet<string> ReservedFlatNames() => new(StringComparer.OrdinalIgnoreCase) { FlatTrash, FlatWork, MigrationOwner };

    HashSet<string> FlatNames(string? except = null)
    {
        var names = ReservedFlatNames();
        if (Directory.Exists(DesktopDirectory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(DesktopDirectory))
                if (!SamePath(entry, except)) names.Add(Path.GetFileName(entry));
        foreach (var item in data.Items.Where(i => i.Kind is StationKind.File or StationKind.Folder))
            if (!SamePath(item.Value, except) && SamePath(Path.GetDirectoryName(item.Value), DesktopDirectory))
                names.Add(Path.GetFileName(item.Value));
        return names;
    }

    static string AllocateFlatName(HashSet<string> names, string name, StationKind kind)
    {
        if (names.Add(name)) return name;
        string extension = kind == StationKind.File ? Path.GetExtension(name) : "";
        string stem = name[..(name.Length - extension.Length)];
        for (int index = 2; ; index++) {
            string suffix = $" ({index})";
            string ending = suffix + extension;
            // A long extension also contributes to the Windows component limit.
            if (ending.Length >= 255) {
                ending = suffix + extension[..(254 - suffix.Length)];
                if (char.IsHighSurrogate(ending[^1])) ending = ending[..^1];
            }
            int cutoff = Math.Min(stem.Length, 255 - ending.Length);
            if (cutoff > 0 && char.IsHighSurrogate(stem[cutoff - 1])) cutoff--;
            string candidate = stem[..cutoff] + ending.TrimEnd(' ', '.');
            if (names.Add(candidate)) return candidate;
        }
    }

    static string FlatTrashPath(string library, StationItem item) =>
        Path.Combine(library, FlatTrash, item.Id.ToString("N"), Path.GetFileName(item.Value));

    static void EnsurePrivateFolder(string library, string name)
    {
        string folder = Path.Combine(library, name);
        CheckLibraryParents(folder);
        Directory.CreateDirectory(folder);
        File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Hidden);
    }

    string CreateFlatWork()
    {
        RequireAvailableLibrary();
        if (File.Exists(FlatJournal)) throw new IOException("有未完成的文件操作，请先重启 Halo。");
        EnsurePrivateFolder(DesktopDirectory, FlatWork);
        return Directory.CreateDirectory(Path.Combine(DesktopDirectory, FlatWork, Guid.NewGuid().ToString("N"))).FullName;
    }

    static void PruneFlatTrashContainer(string value)
    {
        string parent = Path.GetDirectoryName(value)!;
        try {
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        } catch (IOException error) { App.Log(error); }
        catch (UnauthorizedAccessException error) { App.Log(error); }
    }

    async Task<string> CopyToFlatLibrary(string source, StationKind kind, CancellationToken token)
    {
        string work = CreateFlatWork();
        try {
            string staged = Path.Combine(work, Path.GetFileName(source));
            await Task.Run(() => CopyTree(source, staged, token), token);
            token.ThrowIfCancellationRequested();
            return PublishFlatEntry(staged, kind);
        } finally { DiscardFlatWork(work); }
    }

    string PublishFlatEntry(string staged, StationKind kind)
    {
        var names = FlatNames();
        while (true) {
            string target = Path.Combine(DesktopDirectory, AllocateFlatName(names, Path.GetFileName(staged), kind));
            try { MoveEntry(staged, target, kind); return target; }
            catch (IOException) when (EntryExists(staged) && EntryExists(target)) { }
        }
    }

    static void MoveEntry(string source, string target, StationKind kind)
    {
        CheckLibraryParents(source);
        CheckLibraryParents(target);
        if (kind == StationKind.Folder) Directory.Move(source, target);
        else File.Move(source, target);
    }

    void DeleteCreatedCopy(string value)
    {
        if (IsFlatLibrary) {
            if (!ManagedPath(value) || !SamePath(Path.GetDirectoryName(value), DesktopDirectory)) throw new IOException("副本路径无效。");
            if (Directory.Exists(value)) DeleteOwnedTree(value);
            else File.Delete(value);
        } else CleanupOwned(value);
    }

    void DiscardFlatWork(string work)
    {
        // A pending journal owns the work area until rollback or catalog commit completes.
        if (File.Exists(FlatJournal)) return;
        try { CleanupOwned(work); }
        catch (IOException error) { App.Log(error); }
        catch (UnauthorizedAccessException error) { App.Log(error); }
    }

    Task MoveFlatRecords(StationItem[] from, StationItem[] to, Func<StationDocument, StationDocument> update) =>
        ApplyFlatMoves(from.Zip(to).Where(pair => IsOwnedFile(pair.First) && pair.First.Value != pair.Second.Value)
            .Select(pair => new FlatMove(pair.First.Value, pair.Second.Value, pair.First.Kind)).ToArray(), update);

    async Task RenameFlatItem(StationItem item, string name)
    {
        string target = Path.Combine(DesktopDirectory, AllocateFlatName(FlatNames(item.Value), name, item.Kind));
        if (target == item.Value) return;
        StationDocument Update(StationDocument d) {
            if (!d.Items.Contains(item)) throw new IOException("项目已变化，请重试。");
            return d with { Items = d.Items.Select(i => i.Id == item.Id ? i with { Value = target, Title = "" } : i).ToArray() };
        }
        if (!SamePath(item.Value, target)) {
            await ApplyFlatMoves([new(item.Value, target, item.Kind)], Update);
        } else {
            string work = CreateFlatWork();
            try {
                string transit = Path.Combine(work, "rename");
                await ApplyFlatMoves([new(item.Value, transit, item.Kind), new(transit, target, item.Kind)], Update, work);
            } finally { DiscardFlatWork(work); }
        }
    }

    async Task<StationDocument> ApplyFlatMoves(FlatMove[] moves, Func<StationDocument, StationDocument> update, string? work = null,
        FileGroup? group = null, FolderSplit? split = null, NestedFileGroup? nestedGroup = null, NestedFolderSplit? nestedSplit = null,
        NestedEdit? edit = null, LibraryMove? transfer = null, LibraryCopy? copy = null)
    {
        if (moves.Length == 0) return await Change(update);
        RequireAvailableLibrary();
        var transaction = new FlatTransaction(Guid.NewGuid(), moves, work, group, split, nestedGroup, nestedSplit, edit, transfer, copy);
        ValidateFlatTransaction(transaction);
        var prepared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var movedDirectories = new List<FlatMove>();
        foreach (var move in moves) {
            if (!EntryExists(move.From) && !prepared.Contains(move.From)
                && !movedDirectories.Any(directory => Below(move.From, directory.To)
                    && EntryExists(Path.Combine(directory.From, Path.GetRelativePath(directory.To, move.From)))))
                throw new IOException("要移动的副本不存在。");
            prepared.Add(move.To);
            if (move.Kind == StationKind.Folder) movedDirectories.Add(move);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FlatJournal)!);
        WriteStorageJournal(FlatJournal, transaction);
        int moved = 0;
        try {
            foreach (var move in moves) {
                if (Below(move.To, Path.Combine(DesktopDirectory, FlatTrash))) EnsurePrivateFolder(DesktopDirectory, FlatTrash);
                Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
                MoveEntry(move.From, move.To, move.Kind);
                moved++;
            }
            return await Change(d => {
                var next = update(d);
                return next with { LastStorageOperation = transaction.Id, FolderColors = MoveFolderColors(next.FolderColors, moves) };
            });
        } finally {
            try { CompleteFlatTransaction(transaction, moved); }
            catch (Exception error) when (data.LastStorageOperation == transaction.Id) { App.Log(error); }
        }
    }

    void ValidateFlatTransaction(FlatTransaction transaction)
    {
        string root = DesktopDirectory;
        if (transaction.Id == Guid.Empty || transaction.Moves is not { Length: > 0 }
            || transaction.Work is { } work && (!ManagedPath(work) || !SamePath(Path.GetDirectoryName(work), Path.Combine(root, FlatWork)) || !Guid.TryParseExact(Path.GetFileName(work), "N", out _)))
            throw new IOException("平铺文件库操作记录无效。");
        if (transaction.Copy != null) { ValidateLibraryCopy(transaction, transaction.Copy); return; }
        if (transaction.Transfer != null) { ValidateLibraryMove(transaction, transaction.Transfer); return; }
        if (transaction.Edit != null) { ValidateNestedEdit(transaction, transaction.Edit); return; }
        if (transaction.NestedSplit != null) { ValidateNestedFolderSplit(transaction, transaction.NestedSplit); return; }
        if (transaction.NestedGroup != null) { ValidateNestedFileGroup(transaction, transaction.NestedGroup); return; }
        if (transaction.Split != null) { ValidateFolderSplit(transaction, transaction.Split); return; }
        if (transaction.Group != null) { ValidateFileGroup(transaction, transaction.Group); return; }
        if (!IsFlatLibrary) throw new IOException("平铺文件库操作记录无效。");
        bool Valid(string value) {
            if (value == null || !ManagedPath(value)) return false;
            var parent = Path.GetDirectoryName(value);
            return SamePath(parent, root) && !ReservedFlatNames().Contains(Path.GetFileName(value))
                || SamePath(Path.GetDirectoryName(parent), Path.Combine(root, FlatTrash)) && Guid.TryParseExact(Path.GetFileName(parent), "N", out _)
                || transaction.Work is { } location && Below(value, location);
        }
        if (transaction.Moves.Any(move => move == null || move.Kind is not (StationKind.File or StationKind.Folder) || !Valid(move.From) || !Valid(move.To)))
            throw new IOException("平铺文件库操作路径无效。");
    }

    void CompleteFlatTransaction(FlatTransaction transaction, int? completedMoves = null)
    {
        if (data.LastStorageOperation != transaction.Id)
            foreach (var move in transaction.Moves.Take(completedMoves ?? transaction.Moves.Length).Reverse()) {
                bool origin = EntryExists(move.From), destination = EntryExists(move.To);
                if ((transaction.Transfer != null || transaction.Copy != null) && (origin == destination || (completedMoves.HasValue && origin)))
                    throw new IOException("移动的源位置或目标位置发生变化，已保留操作记录。");
                if (!origin && destination) MoveEntry(move.To, move.From, move.Kind);
            }
        File.Delete(FlatJournal);
        if (transaction.Work is { } work) DiscardFlatWork(work);
        if (!IsFlatLibrary && transaction.Group != null && data.LastStorageOperation != transaction.Id)
            PruneFlatTrashContainer(transaction.Moves[^1].To);
        if (transaction.Split != null && data.LastStorageOperation != transaction.Id)
            foreach (var move in IsFlatLibrary ? transaction.Moves.TakeLast(1) : transaction.Moves)
                PruneFlatTrashContainer(move.To);
        if (transaction.NestedSplit is { } nestedSplit && (data.LastStorageOperation == transaction.Id) == nestedSplit.Undo)
            PruneFlatTrashContainer(nestedSplit.Undo ? transaction.Moves[0].From : transaction.Moves[^1].To);
        if (transaction.Edit is { } edit) {
            bool committed = data.LastStorageOperation == transaction.Id;
            if (edit.Kind == NestedEditKind.Archive && !committed)
                foreach (var move in transaction.Moves) PruneFlatTrashContainer(move.To);
            else if (edit.Kind == NestedEditKind.Restore && committed) PruneFlatTrashContainer(transaction.Moves[0].From);
        }
    }

    static void WriteStorageJournal<T>(string journal, T transaction, bool replace = false)
    {
        string temporary = journal + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, transaction);
                stream.Flush(true);
            }
            File.Move(temporary, journal, replace);
        } finally { File.Delete(temporary); }
    }

    void RecoverStorage()
    {
        RecoverLibraryMigration();
        RecoverRootMigration();
        if (File.Exists(FlatJournal)) {
            var transaction = JsonSerializer.Deserialize<FlatTransaction>(File.ReadAllText(FlatJournal))
                ?? throw new IOException("平铺文件库操作记录无效。");
            ValidateFlatTransaction(transaction);
            CompleteFlatTransaction(transaction);
        }
        RecoverRenames();
        if (!IsFlatLibrary) return;
        if (data.Items.Where(IsOwnedFile).Any(i => !SamePath(Path.GetDirectoryName(i.Value), DesktopDirectory)
                || ReservedFlatNames().Contains(Path.GetFileName(i.Value)) || IsSystemRootEntry(i.Value, DesktopDirectory))
            || Trash.Where(IsOwnedFile).Any(i => !SamePath(i.Value, FlatTrashPath(DesktopDirectory, i))))
            throw new InvalidDataException("平铺文件库的副本位置无效。");
    }
}
