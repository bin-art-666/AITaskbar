using System.Text.Json;

namespace Halo;

internal enum StationKind { File, Folder, Text, Link }
internal enum StationTextStyle { Plain, Markdown, Code, Excerpt }
internal sealed record StationInput(StationKind Kind, string Value, string Title = "", bool Owned = false, StationTextStyle TextStyle = StationTextStyle.Plain, string SourceUrl = "", string Language = "");
internal sealed record StationItem(Guid Id, StationKind Kind, string Value, string Title, DateTimeOffset Created, bool Owned = false, StationTextStyle TextStyle = StationTextStyle.Plain, string SourceUrl = "", string Language = "", FolderTrashOrigin? Origin = null);
internal sealed record StationDocument(int Version, StationItem[] Items, int Capacity = StationStore.DefaultCapacity, string ArchiveDirectory = "", bool RemoveAfterArchive = true, StationActions? Actions = null, StationSort? Sort = null, Guid[]? ManualOrder = null, StationLayout? CompactLayout = null, StationLayout? ExpandedLayout = null, StationItem[]? Trash = null, string[]? ImportReceipts = null, string? StorageDirectory = null, Guid? LastStorageOperation = null, FolderGridLayout[]? FolderLayouts = null, Dictionary<string, string>? FolderColors = null);
internal sealed record StationAddResult(int Added, int Duplicates);

// Legacy references stay explicit; new desktop imports own independent copies.
internal sealed partial class StationStore
{
    internal const int DefaultCapacity = 200, MaximumCapacity = 2000, MaximumText = 20000;
    const long MaximumDocumentBytes = 256L * 1024 * 1024;
    readonly string path;
    readonly SemaphoreSlim gate = new(1, 1);
    StationDocument data = new(6, [], Sort: HaloDefaults.Sort);
    bool preserveBackup;
    internal IReadOnlyList<StationItem> Items => Array.AsReadOnly(data.Items);
    internal int Capacity => data.Capacity;
    internal string ArchiveDirectory => data.ArchiveDirectory;
    internal bool RemoveAfterArchive => data.RemoveAfterArchive;
    internal StationActions Actions => data.Actions ?? new();
    internal Task UpdateActions(StationActions value) { value.Validate(); return Change(d => d with { Actions = value }); }
    internal Task Edit(Guid id, string value, string title) => Change(d => d with { Items = d.Items.Select(i => i.Id == id ? i with { Value = i.Kind == StationKind.Text ? Normalize(new(i.Kind, value)).Value : i.Value, Title = Normalize(new(i.Kind, i.Value, title)).Title } : i).ToArray() });
    internal Task Relocate(Guid id,string path) => Change(d=>d with { Items=d.Items.Select(i=>i.Id==id&&i.Kind is StationKind.File or StationKind.Folder ? i with {Value=Normalize(new(i.Kind,path)).Value} : i).ToArray() });
    internal string AttachmentDirectory => Path.Combine(Path.GetDirectoryName(path)!, "station-files");
    internal string WindowLayoutPath => Path.Combine(Path.GetDirectoryName(path)!, "station-window.json");
    internal event Action? Changed;

    internal StationStore(string path)
    {
        this.path = Path.GetFullPath(path);
        if (!File.Exists(this.path) && !File.Exists(this.path + ".bak")) {
            if (File.Exists(FlatJournal) || File.Exists(MigrationJournal)) {
                try { RecoverAvailableStorage(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { catalogReadOnly = true; catalogWarning = "有未完成的存储操作，已停止写入以保护文件。"; }
            }
            return;
        }
        try { data = Read(this.path); }
        catch (NotSupportedException) { catalogReadOnly = true; catalogWarning = "中转站数据来自更新版本，已停止写入。"; return; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            try { data = Read(this.path + ".bak"); preserveBackup = true; catalogWarning = "已从备份恢复中转站。"; }
            catch (Exception backupError) when (backupError is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            { catalogReadOnly = true; catalogWarning = "中转站数据无法读取，已停止写入以保护原文件。"; return; }
        }
        try { RecoverAvailableStorage(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { App.Log(error); catalogReadOnly = true; catalogWarning = "有未完成的存储操作，已停止写入以保护文件。"; }
    }
    static StationDocument Read(string path)
    {
        if (new FileInfo(path).Length > MaximumDocumentBytes) throw new InvalidDataException("中转站文件过大。");
        var value = JsonSerializer.Deserialize<StationDocument>(File.ReadAllText(path)) ?? throw new InvalidDataException("内容为空。");
        if (value.Version is not (1 or 2 or 3 or 4 or 5 or 6)) throw new NotSupportedException();
        if (value.Version < 4) value = value with { Version = 4 };
        Validate(value); return value;
    }
    internal static Uri? WebUri(string value) => !value.Any(char.IsControl) && value == value.Trim()
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? uri : null;
    internal static StationInput Text(string text, string title = "") => WebUri(text) is { } uri
        ? new(StationKind.Link, uri.AbsoluteUri, title) : new(StationKind.Text, text, title);
    static StationInput Normalize(StationInput value)
    {
        if (value == null || value.Value == null || value.Title == null || !Enum.IsDefined(value.Kind)) throw new InvalidDataException("内容格式不支持。");
        if (!Enum.IsDefined(value.TextStyle) || value.SourceUrl == null || value.Language == null
            || value.Kind != StationKind.Text && (value.TextStyle != StationTextStyle.Plain || value.SourceUrl.Length > 0 || value.Language.Length > 0)
            || value.SourceUrl.Length > 4096 || value.SourceUrl.Length > 0 && WebUri(value.SourceUrl) == null
            || value.Language.Length > 40 || value.Language.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('#' or '+' or '-' or '.' or '_')))
            throw new InvalidDataException("文字样式、来源网址或代码语言无效。");
        string payload = value.Value;
        if (value.Kind is StationKind.File or StationKind.Folder)
        {
            if (payload.Length > 32767 || !Path.IsPathFullyQualified(payload) || payload.Any(char.IsControl)) throw new InvalidDataException("文件没有可保存的本地路径。");
            payload = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payload));
        }
        else if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaximumText) throw new InvalidDataException("文字或链接不能为空，最多 20,000 字符。");
        if (value.Kind == StationKind.Link) payload = WebUri(payload)?.AbsoluteUri ?? throw new InvalidDataException("仅支持 HTTP/HTTPS 链接。");
        return value with { Value = payload, Title = value.Title.Length > 200 ? value.Title[..200] : value.Title };
    }
    static string Key(StationKind kind, string value) => kind is StationKind.File or StationKind.Folder
        ? "path:" + value.ToUpperInvariant() : kind + ":" + value;
    static void Validate(StationDocument document)
    {
        if (document.StorageDirectory is { } storage && (document.Version < 5 || CanonicalLibraryPath(storage) != storage || Path.GetPathRoot(storage) == storage))
            throw new InvalidDataException("文件库位置无效。");
        document.Actions?.Validate();
        ValidateLayout(document.CompactLayout); ValidateLayout(document.ExpandedLayout);
        ValidateFolderLayouts(document.FolderLayouts);
        ValidateFolderColors(document.FolderColors);
        if (document.Sort is {} sort && !Enum.IsDefined(sort) || document.ManualOrder is {} order && (order.Length > MaximumCapacity || order.Contains(Guid.Empty) || order.Distinct().Count() != order.Length))
            throw new InvalidDataException("排序记录无效。");
        if (document.Items == null || document.Items.Length > MaximumCapacity || document.Capacity is < 1 or > MaximumCapacity
            || document.ArchiveDirectory is null || document.ArchiveDirectory.Length > 32767
            || document.ArchiveDirectory.Length > 0 && (!Path.IsPathFullyQualified(document.ArchiveDirectory) || document.ArchiveDirectory.Any(char.IsControl))) throw new InvalidDataException("中转站条目或设置无效。");
        if (document.Trash is { Length: > 10000 } || document.ImportReceipts is { Length: > 10000 }) throw new InvalidDataException("回收站或导入记录过多。");
        var ids = new HashSet<Guid>(); var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Items.Concat(document.Trash ?? []))
        {
            if (item == null || item.Id == Guid.Empty || !ids.Add(item.Id)) throw new InvalidDataException("中转站条目无效。");
            if (item.Origin is { } origin && (!IsOwnedFile(item) || document.Items.Contains(item) || !ValidTrashOrigin(origin)))
                throw new InvalidDataException("回收站原位置无效。");
            var normalized = Normalize(new(item.Kind, item.Value, item.Title, item.Owned, item.TextStyle, item.SourceUrl, item.Language));
            if (normalized.Value != item.Value || normalized.Title != item.Title || document.Items.Contains(item) && !keys.Add(Key(item.Kind, item.Value))) throw new InvalidDataException("中转站条目重复或格式无效。");
        }
    }
    async Task<StationDocument> Change(Func<StationDocument, StationDocument> change)
    {
        await gate.WaitAsync();
        StationDocument committed;
        try
        {
            if (ReadOnly) throw new IOException(LoadWarning);
            var next = change(data); if (ReferenceEquals(next, data)) return data;
            if (next.ManualOrder is {} order) next = next with { ManualOrder = order.Where(id => next.Items.Any(i => i.Id == id)).ToArray() };
            next = next with { CompactLayout = SyncLayout(next.CompactLayout, next.Items), ExpandedLayout = SyncLayout(next.ExpandedLayout, next.Items) };
            if (next.FolderLayouts is { Length: > 0 } layouts) {
                var folders = next.Items.Concat(next.Trash ?? []).Where(item => item.Kind == StationKind.Folder).Select(item => item.Id).ToHashSet();
                next = next with { FolderLayouts = layouts.Where(layout => folders.Contains(layout.FolderId)).ToArray() };
            }
            next = next with { FolderColors = PruneFolderColors(next) };
            Validate(next);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                try
                {
                    using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(stream, next);
                        if (stream.Length > MaximumDocumentBytes) throw new InvalidDataException("中转站内容过大。");
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(path + ".tmp", path, preserveBackup ? null : path + ".bak");
                    else File.Move(path + ".tmp", path);
                }
                finally { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
            });
            committed = data = next; preserveBackup = false;
        }
        finally { gate.Release(); }
        Changed?.Invoke();
        return committed;
    }
    internal async Task<StationAddResult> Add(IEnumerable<StationInput> values)
    {
        var inputs = values.Take(MaximumCapacity + 1).Select(Normalize).ToArray();
        if (inputs.Length > MaximumCapacity) throw new InvalidDataException($"一次最多存入 {MaximumCapacity} 项，请分批拖入。");
        StationAddResult result = new(0, 0);
        await Change(document =>
        {
            var keys = document.Items.Select(i => Key(i.Kind, i.Value)).ToHashSet(StringComparer.Ordinal);
            var additions = new List<StationItem>(); int duplicates = 0;
            foreach (var input in inputs)
            {
                if (!keys.Add(Key(input.Kind, input.Value))) { duplicates++; continue; }
                additions.Add(new(Guid.NewGuid(), input.Kind, input.Value, input.Title, DateTimeOffset.Now, input.Owned, input.TextStyle, input.SourceUrl, input.Language));
            }
            if (additions.Count > 0 && document.Items.Length + additions.Count > document.Capacity) throw new InvalidDataException($"中转站上限 {document.Capacity} 项，目前还可存 {Math.Max(0, document.Capacity - document.Items.Length)} 项；本批未存入，请先整理或归档。");
            result = new(additions.Count, duplicates);
            return additions.Count == 0 ? document : document with { Items = [.. document.Items, .. additions] };
        });
        return result;
    }
    internal Task Remove(Guid id) => Change(d => d.Items.Any(i => i.Id == id) ? d with { Items = d.Items.Where(i => i.Id != id).ToArray() } : d);
    internal Task Clear() => Change(d => d.Items.Length == 0 ? d : d with { Items = [] });
    internal Task UpdateOptions(int capacity, string archiveDirectory, bool removeAfterArchive)
    {
        if (capacity is < 1 or > MaximumCapacity) throw new ArgumentException($"保存数量应为 1～{MaximumCapacity} 项。");
        if (!string.IsNullOrWhiteSpace(archiveDirectory) && (!Path.IsPathFullyQualified(archiveDirectory) || archiveDirectory.Any(char.IsControl))) throw new ArgumentException("请选择有效的完整归档路径。");
        string directory = string.IsNullOrWhiteSpace(archiveDirectory) ? "" : Path.GetFullPath(archiveDirectory);
        return Change(d => d.Capacity == capacity && d.ArchiveDirectory == directory && d.RemoveAfterArchive == removeAfterArchive
            ? d : d with { Capacity = capacity, ArchiveDirectory = directory, RemoveAfterArchive = removeAfterArchive });
    }
    internal async Task<int> RemoveArchived(IEnumerable<StationItem> archived)
    {
        await desktopGate.WaitAsync();
        try {
            RequireAvailableLibrary();
            var verified = archived.ToHashSet();
            var selected = data.Items.Where(verified.Contains).ToArray();
            if (selected.Length == 0) return 0;
            var removed = selected.Select(item => IsFlatLibrary && IsOwnedFile(item)
                ? item with { Value = FlatTrashPath(DesktopDirectory, item) } : item).ToArray();
            await MoveFlatRecords(selected, removed, d => {
                if (!selected.All(d.Items.Contains)) throw new IOException("项目已变化，请重试。");
                return d with { Items = d.Items.Where(i => !verified.Contains(i)).ToArray(), Trash = [.. d.Trash ?? [], .. removed] };
            });
            return selected.Length;
        } finally { desktopGate.Release(); }
    }
    internal async Task Flush() { await desktopGate.WaitAsync(); try { await gate.WaitAsync(); gate.Release(); } finally { desktopGate.Release(); } }
    internal async Task Restore(StationItem[] items)
    {
        var ids = items.Select(item => item.Id).ToHashSet();
        var nested = Trash.Where(item => ids.Contains(item.Id) && item.Origin != null).ToArray();
        var destinations = nested.Select(item => OriginalTrashLocation(item) ?? throw new IOException("原父目录已不存在，请选择恢复位置。")).ToArray();
        for (int i = 0; i < nested.Length; i++)
            await RestoreInFolder(nested[i], destinations[i].Root, destinations[i].Directory);
        await RestoreToDesktop(items.Where(item => nested.All(entry => entry.Id != item.Id)).ToArray());
    }
    internal async Task RestoreToDesktop(StationItem[] items) {
        await desktopGate.WaitAsync();
        try {
            RequireAvailableLibrary();
            var ids = items.Select(i => i.Id).ToHashSet();
            var keys = data.Items.Select(i => Key(i.Kind, i.Value)).ToHashSet(StringComparer.Ordinal);
            var selected = Trash.Where(i => ids.Contains(i.Id)
                && (IsFlatLibrary && IsOwnedFile(i) || keys.Add(Key(i.Kind, i.Value)))).ToArray();
            if (selected.Length == 0) return;
            var names = IsFlatLibrary ? FlatNames() : null;
            var restored = selected.Select(item => (names != null && IsOwnedFile(item)
                ? item with { Value = Path.Combine(DesktopDirectory, AllocateFlatName(names, Path.GetFileName(item.Value), item.Kind)), Title = "" }
                : item) with { Origin = null }).ToArray();
            var restoredIds = restored.Select(i => i.Id).ToHashSet();
            await MoveFlatRecords(selected, restored, d => {
                if (d.Items.Length + selected.Length > d.Capacity) throw new IOException("容量不足，无法撤销移除。");
                return d with { Items = [.. d.Items, .. restored], Trash = (d.Trash ?? []).Where(i => !restoredIds.Contains(i.Id)).ToArray() };
            });
            if (IsFlatLibrary) foreach (var item in selected.Where(IsOwnedFile)) PruneFlatTrashContainer(item.Value);
        } finally { desktopGate.Release(); }
    }
}
