namespace Halo;

internal sealed record TrashDeleteResult(int Deleted, int Failed);

internal sealed partial class StationStore
{
    readonly SemaphoreSlim desktopGate = new(1, 1);
    internal string DesktopDirectory => data.StorageDirectory ?? Path.Combine(Path.GetDirectoryName(path)!, "desktop-files");
    internal string TrashDirectory => IsFlatLibrary ? Path.Combine(DesktopDirectory, FlatTrash) : DesktopDirectory;
    internal string RequireTrashDirectory()
    {
        RequireAvailableLibrary();
        string directory = TrashDirectory;
        CheckLibraryParents(directory);
        if (!Directory.Exists(directory)) throw new IOException("回收站存储目录不可用。");
        return directory;
    }
    internal bool HasImportReceipt(string receipt) => (data.ImportReceipts ?? []).Contains(receipt);
    internal IReadOnlyList<StationItem> Trash => data.Trash ?? [];
    static void NoLink(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("暂不复制符号链接或目录联接，请选择实际文件。"); }
    bool ManagedPath(string value) {
        string full = Path.GetFullPath(value);
        if (!Below(full, DesktopDirectory) || IsSystemRootEntry(full, DesktopDirectory)) return false;
        CheckLibraryParents(full);
        return true;
    }
    internal bool IsManagedSearchImage(string value) => !LibraryOffline && ManagedPath(value);
    static void CopyTree(string source, string destination, CancellationToken token) {
        token.ThrowIfCancellationRequested(); NoLink(source);
        if (!Directory.Exists(source)) {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920]; int count;
            while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            output.Flush(true); return;
        }
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source)) CopyTree(entry, Path.Combine(destination, Path.GetFileName(entry)), token);
    }
    void CleanupOwned(string directory) {
        if (!ManagedPath(directory) || !Directory.Exists(directory)) return;
        DeleteOwnedTree(directory);
    }
    // Never traverse a link that an external editor might have placed in a managed folder.
    static void DeleteOwnedTree(string directory)
    {
        NoLink(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) {
            var attributes = File.GetAttributes(entry);
            if (!attributes.HasFlag(FileAttributes.Directory)) File.Delete(entry);
            else if (attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(entry);
            else DeleteOwnedTree(entry);
        }
        Directory.Delete(directory);
    }
    internal async Task<StationAddResult> ImportCopies(IEnumerable<StationInput> inputs, CancellationToken token = default, string? receipt = null) {
        await desktopGate.WaitAsync(token); var created = new List<string>(); bool committed = false;
        try {
            if (ReadOnly) throw new IOException(LoadWarning);
            RequireAvailableLibrary();
            if (receipt != null && (data.ImportReceipts ?? []).Contains(receipt)) return new(0, 0);
            var values = inputs.Take(MaximumCapacity + 1).Select(Normalize).ToArray();
            if (values.Length > MaximumCapacity || data.Items.Length + values.Length > data.Capacity) throw new InvalidDataException("容量不足，本批未存入。");
            var additions = new List<StationItem>(); int duplicates = 0;
            foreach (var value in values) {
                token.ThrowIfCancellationRequested(); Guid id = Guid.NewGuid(); string payload = value.Value;
                if (value.Kind is StationKind.File or StationKind.Folder) {
                    string source = Path.GetFullPath(payload);
                    if (Path.GetFullPath(DesktopDirectory).StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || source.Equals(DesktopDirectory, StringComparison.OrdinalIgnoreCase)) throw new IOException("不能把包含 Halo 存储目录的文件夹复制到自身。");
                    if (Path.GetFileName(source).Length == 0) throw new IOException("请选择具体文件或文件夹，不能复制整个磁盘。");
                    if (IsFlatLibrary) {
                        payload = await CopyToFlatLibrary(source, value.Kind, token);
                        created.Add(payload);
                    } else {
                        string root = Path.Combine(DesktopDirectory, id.ToString("N")); if (!ManagedPath(root)) throw new IOException("副本目录无效。");
                        Directory.CreateDirectory(root); created.Add(root);
                        payload = Path.Combine(root, Path.GetFileName(source));
                        await Task.Run(() => CopyTree(source, payload, token), token);
                    }
                } else if (data.Items.Concat(additions).Any(i => i.Kind == value.Kind && i.Value == value.Value)) { duplicates++; continue; }
                string title = IsFlatLibrary && value.Kind is StationKind.File or StationKind.Folder ? "" : value.Title;
                additions.Add(new(id, value.Kind, payload, title, DateTimeOffset.Now, true, value.TextStyle, value.SourceUrl, value.Language));
            }
            await Change(d => {
                if (d.Items.Length + additions.Count > d.Capacity) throw new IOException("容量不足，本批未存入。");
                return d with { Items = [.. d.Items, .. additions], ImportReceipts = receipt == null ? d.ImportReceipts : [.. (d.ImportReceipts ?? []).TakeLast(9999), receipt] };
            });
            committed = true; return new(additions.Count, duplicates);
        } finally { if (!committed) foreach (var root in created) try { await Task.Run(() => DeleteCreatedCopy(root)); } catch (Exception error) { App.Log(error); } desktopGate.Release(); }
    }
    internal async Task<StationItem> OwnReference(Guid id) {
        await desktopGate.WaitAsync(); string? root = null; bool committed = false;
        try {
            RequireAvailableLibrary();
            var item = Items.Single(i => i.Id == id); if (item.Owned || item.Kind is not (StationKind.File or StationKind.Folder)) return item;
            if (Path.GetFullPath(DesktopDirectory).StartsWith(item.Value + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || item.Value.Equals(DesktopDirectory, StringComparison.OrdinalIgnoreCase)) throw new IOException("不能复制包含 Halo 存储的文件夹。");
            string target;
            if (IsFlatLibrary) {
                target = root = await CopyToFlatLibrary(item.Value, item.Kind, CancellationToken.None);
            } else {
                root = Path.Combine(DesktopDirectory, Guid.NewGuid().ToString("N")); if (!ManagedPath(root)) throw new IOException("副本目录无效。");
                Directory.CreateDirectory(root); target = Path.Combine(root, Path.GetFileName(item.Value));
                await Task.Run(() => CopyTree(item.Value, target, CancellationToken.None));
            }
            var owned = item with { Value = target, Owned = true, Title = IsFlatLibrary ? "" : item.Title };
            await Change(d => {
                if (!d.Items.Contains(item)) throw new IOException("项目已变化。");
                return d with { Items = d.Items.Select(i => i.Id == id ? owned : i).ToArray(),
                    FolderColors = MoveFolderColors(d.FolderColors, [new(item.Value, target, item.Kind)]) };
            }); committed = true; return owned;
        } finally { try { if (!committed && root != null) await Task.Run(() => DeleteCreatedCopy(root)); } finally { desktopGate.Release(); } }
    }
    internal const int MaximumDesktopNameLength = 120;
    internal static string ValidDesktopName(string name) {
        name = name.Trim(); string stem = name.Split('.')[0].ToUpperInvariant();
        if (name.Length == 0) throw new IOException("请输入名称。");
        if (name.Length > MaximumDesktopNameLength) throw new IOException($"名称最多可包含 {MaximumDesktopNameLength} 个字符。");
        if (name.EndsWith('.')) throw new IOException("名称末尾不能使用句点。");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(char.IsControl))
            throw new IOException("名称包含 Windows 文件名不允许的字符。");
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && stem[3] is >= '0' and <= '9') throw new IOException("此名称由 Windows 保留。");
        return name;
    }
    internal async Task RenameOwned(Guid id, string name) {
        name = ValidDesktopName(name);
        await desktopGate.WaitAsync();
        try {
            var item = Items.Single(i => i.Id == id);
            if (item.Kind is not (StationKind.File or StationKind.Folder)) { await Edit(id, item.Value, name); return; }
            if (!item.Owned || !ManagedPath(item.Value)) throw new IOException("请先复制到 Halo，再重命名。");
            if (IsFlatLibrary) {
                await RenameFlatItem(item, name);
                return;
            }
            string next = Path.Combine(Path.GetDirectoryName(item.Value)!, name); if (next == item.Value) return;
            if (File.Exists(next) || Directory.Exists(next)) throw new IOException("此名称已存在。");
            string journal = Path.Combine(Path.GetDirectoryName(path)!, "rename-journals", item.Id.ToString("N") + ".json"); Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new[] { item.Value, next }));
            void Move(string from, string to) { if (item.Kind == StationKind.Folder) Directory.Move(from, to); else File.Move(from, to); }
            Move(item.Value, next);
            try {
                await Change(d => {
                    if (!d.Items.Contains(item)) throw new IOException("项目已变化，请重试。");
                    return d with { Items = d.Items.Select(i => i.Id == id ? i with { Value = next, Title = "" } : i).ToArray(),
                        FolderColors = MoveFolderColors(d.FolderColors, [new(item.Value, next, item.Kind)]) };
                });
            }
            catch { Move(next, item.Value); throw; }
            File.Delete(journal);
        } finally { desktopGate.Release(); }
    }
    void RecoverRenames() {
        string journals = Path.Combine(Path.GetDirectoryName(path)!, "rename-journals");
        if (Directory.Exists(journals)) foreach (string journal in Directory.EnumerateFiles(journals, "*.delete.json")) {
            var names = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(journal));
            if (names is not { Length: 3 } || !Guid.TryParse(names[0], out var id) || !ManagedPath(names[1]) || !ManagedPath(names[2]) || !Guid.TryParse(Path.GetFileName(names[1]), out _) || !Guid.TryParse(Path.GetFileName(names[2]), out _)) throw new IOException("删除恢复记录无效。");
            if (IsFlatLibrary && (!SamePath(names[1], Path.Combine(DesktopDirectory, FlatTrash, id.ToString("N")))
                || !SamePath(Path.GetDirectoryName(names[2]), Path.Combine(DesktopDirectory, FlatTrash)))) throw new IOException("回收站恢复路径无效。");
            if (data.Items.Concat(data.Trash ?? []).Any(i => i.Id == id) && Directory.Exists(names[2]) && !Directory.Exists(names[1])) Directory.Move(names[2], names[1]);
            File.Delete(journal);
        }
        foreach (var item in data.Items.Concat(data.Trash ?? []).Where(i => i.Owned && i.Kind is StationKind.File or StationKind.Folder)) {
            if (!ManagedPath(item.Value)) throw new InvalidDataException("副本路径超出 Halo 存储目录。");
            if (IsFlatLibrary) continue;
            string journal = Path.Combine(Path.GetDirectoryName(path)!, "rename-journals", item.Id.ToString("N") + ".json"); if (!File.Exists(journal)) continue;
            var names = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(journal));
            if (names is not { Length: 2 } || !ManagedPath(names[0]) || !ManagedPath(names[1]) || Path.GetDirectoryName(names[0]) != Path.GetDirectoryName(names[1])) throw new IOException("重命名恢复记录无效。");
            if (item.Value == names[0] && !File.Exists(names[0]) && !Directory.Exists(names[0]) && (File.Exists(names[1]) || Directory.Exists(names[1]))) { if (item.Kind == StationKind.Folder) Directory.Move(names[1], names[0]); else File.Move(names[1], names[0]); }
            File.Delete(journal);
        }
    }
    internal Task UpdateNote(Guid id, string text, StationTextStyle style, string source, string language, string title) => Change(d => d with {
        Items = d.Items.Select(item => item.Id == id && item.Kind == StationKind.Text
            ? item with { Value = Normalize(new(StationKind.Text, text, title, TextStyle: style, SourceUrl: source, Language: language)).Value, Title = title.Length > 200 ? title[..200] : title, TextStyle = style, SourceUrl = source, Language = language } : item).ToArray()
    });
    internal Task UpdateContent(Guid id, string text) => Change(d => d with { Items = d.Items.Select(i => i.Id == id && i.Kind is StationKind.Text or StationKind.Link ? i with { Value = Normalize(new(i.Kind, text)).Value } : i).ToArray() });
    internal async Task NewFolder(string name) {
        name = ValidDesktopName(name); if (ReadOnly) throw new IOException(LoadWarning);
        await desktopGate.WaitAsync(); Guid id = Guid.NewGuid(); string? root = null; bool committed = false;
        try {
            RequireAvailableLibrary();
            string target;
            if (IsFlatLibrary) {
                string work = CreateFlatWork();
                try { root = target = PublishFlatEntry(Directory.CreateDirectory(Path.Combine(work, name)).FullName, StationKind.Folder); }
                finally { DiscardFlatWork(work); }
            } else {
                root = Path.Combine(DesktopDirectory, id.ToString("N")); if (!ManagedPath(root)) throw new IOException("副本目录无效。");
                target = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            }
            await Change(d => {
                if (d.Items.Length >= d.Capacity) throw new IOException("容量不足。");
                return d with { Items = [.. d.Items, new StationItem(id, StationKind.Folder, target, "", DateTimeOffset.Now, true)] };
            });
            committed = true;
        } finally { try { if (!committed && root != null) await Task.Run(() => DeleteCreatedCopy(root)); } finally { desktopGate.Release(); } }
    }
    internal async Task<TrashDeleteResult> DeleteTrash(StationItem[] selected) {
        await desktopGate.WaitAsync();
        try {
            if (ReadOnly) throw new IOException(LoadWarning);
            RequireAvailableLibrary();
            int deleted = 0, failed = 0;
            foreach (var item in Trash.Where(selected.Contains).ToArray()) {
                try {
                    await DeleteTrashEntry(item);
                    deleted++;
                } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                    App.Log(error);
                    failed++;
                }
            }
            return new(deleted, failed);
        } finally { desktopGate.Release(); }
    }

    async Task DeleteTrashEntry(StationItem item)
    {
        string? original = null, quarantine = null, journal = null;
        var snapshot = data;
        if (IsOwnedFile(item)) {
            if (!ManagedPath(item.Value)) throw new IOException("副本路径无效。");
            original = Path.GetDirectoryName(item.Value)!;
            if (IsFlatLibrary && !SamePath(item.Value, FlatTrashPath(DesktopDirectory, item))) throw new IOException("回收站路径无效。");
            if (!Guid.TryParse(Path.GetFileName(original), out _) || snapshot.Items.Concat(snapshot.Trash ?? [])
                .Any(entry => entry.Id != item.Id && entry.Kind is StationKind.File or StationKind.Folder && Below(entry.Value, original)))
                throw new IOException("副本目录不是独立内容目录。");
            if (Directory.Exists(original)) {
                // Check the entire owned container before deleting any of its contents.
                await Task.Run(() => CheckTrashTree(original));
                quarantine = Path.Combine(TrashDirectory, Guid.NewGuid().ToString("N"));
                journal = Path.Combine(Path.GetDirectoryName(path)!, "rename-journals", item.Id.ToString("N") + ".delete.json");
                Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
                WriteStorageJournal(journal, new[] { item.Id.ToString(), original, quarantine });
            }
        }
        bool staged = false, committed = false;
        try {
            if (quarantine != null) {
                Directory.Move(original!, quarantine);
                staged = true;
            }
            await Change(d => {
                if (!(d.Trash ?? []).Contains(item) || d.Items.Any(entry => entry.Id == item.Id))
                    throw new IOException("回收站项目已变化，请重试。");
                return d with { Trash = (d.Trash ?? []).Where(entry => entry.Id != item.Id).ToArray() };
            });
            committed = true;
            if (quarantine != null) await Task.Run(() => CleanupOwned(quarantine));
        } catch {
            if (committed) {
                await Change(d => d with { Trash = [.. d.Trash ?? [], item], FolderColors = snapshot.FolderColors });
            }
            if (staged && Directory.Exists(quarantine)) Directory.Move(quarantine!, original!);
            throw;
        } finally {
            if (journal != null && (!staged || !Directory.Exists(quarantine))) {
                try { File.Delete(journal); }
                catch (IOException error) { App.Log(error); }
                catch (UnauthorizedAccessException error) { App.Log(error); }
            }
        }
    }

    static void CheckTrashTree(string directory)
    {
        NoLink(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (attributes.HasFlag(FileAttributes.Directory)) CheckTrashTree(entry);
            else {
                if (attributes.HasFlag(FileAttributes.ReadOnly)) throw new IOException("回收站副本包含只读文件。");
                using var probe = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        }
    }
}
