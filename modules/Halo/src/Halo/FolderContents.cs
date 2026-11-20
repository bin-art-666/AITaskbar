namespace Halo;

internal sealed partial class StationStore
{
    internal string ValidateFolderPath(StationItem folder, string path, bool write = false)
    {
        string origin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Value));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (folder.Kind != StationKind.Folder || !SamePath(full, origin) && !Below(full, origin))
            throw new IOException("该路径不在打开的文件夹内。");
        if (write && (ReadOnly || !folder.Owned)) throw new IOException("此文件夹只读。");
        if (folder.Owned && (!Items.Any(item => item.Id == folder.Id && item.Owned && item.Kind == StationKind.Folder
                && SamePath(item.Value, origin)) || !ManagedPath(full)))
            throw new IOException("Halo 文件夹已移动或移除。");
        CheckLibraryParents(full);
        return full;
    }

    internal StationItem[] ReadFolderContents(StationItem folder, string directory, CancellationToken token)
    {
        return EnumerateFolderContents(folder, directory, token)
            .Select(entry => FolderEntry(folder, entry))
            .OrderByDescending(item => item.Kind == StationKind.Folder)
            .ThenBy(item => Path.GetFileName(item.Value), StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static StationItem FolderEntry(StationItem folder, FileSystemInfo entry) =>
        new(FolderEntryId(entry.FullName), entry is DirectoryInfo ? StationKind.Folder : StationKind.File,
            entry.FullName, "", entry.LastWriteTimeUtc, folder.Owned);

    internal IEnumerable<FileSystemInfo> EnumerateFolderContents(StationItem folder, string directory, CancellationToken token)
    {
        string path = ValidateFolderPath(folder, directory);
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos()) {
            token.ThrowIfCancellationRequested();
            if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) yield return entry;
        }
    }

    internal async Task<string> CreateFolderIn(StationItem folder, string directory, string name, CancellationToken token)
    {
        name = ValidDesktopName(name);
        await desktopGate.WaitAsync(token);
        try {
            string target = ValidateFolderPath(folder, directory, write: true);
            token.ThrowIfCancellationRequested();
            string work = CreateFlatWork();
            try {
                string staged = Directory.CreateDirectory(Path.Combine(work, name)).FullName;
                return PublishFolderEntry(staged, target, StationKind.Folder, FolderNames(target));
            } finally { DiscardFlatWork(work); }
        } finally { desktopGate.Release(); }
    }

    static HashSet<string> FolderNames(string directory) => Directory.EnumerateFileSystemEntries(directory)
        .Select(entry => Path.GetFileName(entry)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    static string PublishFolderEntry(string source, string directory, StationKind kind, HashSet<string> names)
    {
        while (true) {
            string target = Path.Combine(directory, AllocateFlatName(names, Path.GetFileName(source), kind));
            try { MoveEntry(source, target, kind); return target; }
            catch (IOException) when (EntryExists(source) && EntryExists(target)) { }
        }
    }

    internal async Task<int> CopyIntoFolder(StationItem folder, string directory, StationInput[] inputs, CancellationToken token)
    {
        if (inputs.Length is 0 or > MaximumCapacity || inputs.Any(item => item.Kind is not (StationKind.File or StationKind.Folder)))
            throw new IOException("请选择要复制的文件或文件夹。");
        await desktopGate.WaitAsync(token);
        try {
            string target = ValidateFolderPath(folder, directory, write: true);
            var prepared = inputs.Select(Normalize).ToArray();
            foreach (var item in prepared) {
                if (string.IsNullOrEmpty(Path.GetFileName(item.Value))) throw new IOException("请选择具体文件或文件夹。");
                if (item.Kind == StationKind.Folder && (SamePath(target, item.Value) || Below(target, item.Value)
                    || SamePath(DesktopDirectory, item.Value) || Below(DesktopDirectory, item.Value)))
                    throw new IOException("不能把文件夹复制到自身或其子目录。");
            }

            return await Task.Run(() => {
                string work = CreateFlatWork();
                var published = new List<(string Staged, string Destination, StationKind Kind)>();
                try {
                    var staged = prepared.Select((item, index) => {
                        string slot = Directory.CreateDirectory(Path.Combine(work, index.ToString())).FullName;
                        string copy = Path.Combine(slot, Path.GetFileName(item.Value));
                        CopyTree(item.Value, copy, token);
                        VerifyCopiedTree(item.Value, copy, token);
                        return (Path: copy, item.Kind);
                    }).ToArray();
                    token.ThrowIfCancellationRequested();
                    ValidateFolderPath(folder, target, write: true);
                    var names = FolderNames(target);
                    foreach (var item in staged) {
                        token.ThrowIfCancellationRequested();
                        string destination = PublishFolderEntry(item.Path, target, item.Kind, names);
                        published.Add((item.Path, destination, item.Kind));
                    }
                    return published.Count;
                } catch {
                    foreach (var (staged, destination, kind) in published.AsEnumerable().Reverse())
                        MoveEntry(destination, staged, kind);
                    throw;
                } finally { DiscardFlatWork(work); }
            }, token);
        } finally { desktopGate.Release(); }
    }
}
