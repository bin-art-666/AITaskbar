using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Halo;

internal sealed partial class StationStore
{
    const string RecycleBin = "$RECYCLE.BIN", VolumeInformation = "System Volume Information";
    string RootMigrationJournal => Path.Combine(Path.GetDirectoryName(path)!, "root-storage-migration.json");
    sealed record RootMigration(Guid Id, string Source, string Destination, string Volume, LibraryMigrationEntry[] Entries, ulong[]? FileIds = null);
    sealed class RootMigrationUnavailableException() : IOException("迁移所用的磁盘暂不可用，请连接原磁盘后重试。");

    internal static bool IsVolumeRoot(string value) => SamePath(value, Path.GetPathRoot(value));

    static bool IsSystemRootEntry(string value, string root)
    {
        if (!IsVolumeRoot(root)) return false;
        string top = Path.GetRelativePath(root, value).Split(Path.DirectorySeparatorChar, 2)[0];
        return SamePath(top, RecycleBin) || SamePath(top, VolumeInformation);
    }

    static string ReadVolumeId(string root)
    {
        var name = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(root, name, (uint)name.Capacity)
            || !Guid.TryParse(name.ToString().Split('{', '}').ElementAtOrDefault(1), out var id))
            throw new IOException("无法读取文件库所在的卷。", new Win32Exception(Marshal.GetLastWin32Error()));
        return id.ToString("D");
    }

    LibraryMigrationPlan PlanRootMigration(string destination)
    {
        RequireAvailableLibrary();
        string source = DesktopDirectory;
        if (SamePath(source, destination)) throw new IOException("已在使用此磁盘根目录。");
        if (Below(path, destination)) throw new IOException("请选择不包含 Halo 配置目录的专用磁盘。");
        var drive = new DriveInfo(destination);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS")
            throw new IOException("请选择已连接的本机 NTFS 固定磁盘。");
        var occupied = ReservedFlatNames();
        occupied.Add(RecycleBin);
        occupied.Add(VolumeInformation);
        occupied.UnionWith(Directory.EnumerateFileSystemEntries(destination).Select(entry => Path.GetFileName(entry)));
        occupied.UnionWith(data.Items.Where(item => !item.Owned && (item.Kind is StationKind.File or StationKind.Folder)
            && SamePath(Path.GetDirectoryName(item.Value), destination)).Select(item => Path.GetFileName(item.Value)));
        var plan = PlanStorageMigration(data, destination, occupied);
        long bytes = plan.Entries.Sum(entry => {
            if (!ManagedPath(entry.Source)) throw new IOException("副本路径超出 Halo 文件库。");
            return MigrationBytes(entry.Source);
        });
        return plan with { Volume = ReadVolumeId(destination), RequiredBytes = bytes };
    }

    static long MigrationBytes(string path)
    {
        NoLink(path);
        return Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path).Sum(MigrationBytes) : new FileInfo(path).Length;
    }

    async Task MoveStorageToRoot(LibraryMigrationPlan plan, CancellationToken token)
    {
        await desktopGate.WaitAsync(token);
        string destination = plan.Destination;
        RootMigration? pending = null;
        try {
            if (ReadOnly) throw new IOException(LoadWarning);
            RequireCleanMigrationState();
            var current = await Task.Run(() => PlanRootMigration(destination), token);
            if (current.Volume != plan.Volume || !current.Entries.SequenceEqual(plan.Entries))
                throw new IOException("文件或磁盘已变化，请重新预览迁移。");
            if (new DriveInfo(destination).AvailableFreeSpace < current.RequiredBytes)
                throw new IOException("磁盘空间不足，无法保留原文件并复制到新位置。");
            var transaction = new RootMigration(Guid.NewGuid(), DesktopDirectory, destination, current.Volume!, plan.Entries);
            Directory.CreateDirectory(Path.GetDirectoryName(RootMigrationJournal)!);
            WriteStorageJournal(RootMigrationJournal, transaction);
            pending = transaction;
            pending = await Task.Run(() => StageRootMigration(transaction, token), token);
            var ready = pending;
            await Task.Run(() => PublishRootMigration(ready, token), token);
            await Change(document => {
                token.ThrowIfCancellationRequested();
                RequireRootVolume(ready);
                if (!MatchesRootMigrationItems(document, ready))
                    throw new IOException("文件记录已变化，请重新预览迁移。");
                return RelocateLibrary(document, plan);
            });
        }
        finally {
            try {
                if (pending != null)
                    await Task.Run(RecoverRootMigration);
            }
            catch (RootMigrationUnavailableException) { LibraryOffline = storageRecoveryPending = true; throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                catalogReadOnly = true;
                catalogWarning = "有未完成的存储操作，已停止写入以保护文件。";
                App.Log(error);
                throw;
            }
            finally { desktopGate.Release(); }
        }
    }

    static string RootStaging(RootMigration migration) =>
        Path.Combine(migration.Destination, FlatWork, migration.Id.ToString("N"));

    RootMigration StageRootMigration(RootMigration migration, CancellationToken token)
    {
        RequireRootVolume(migration);
        EnsurePrivateFolder(migration.Destination, FlatWork);
        string staging = RootStaging(migration);
        if (EntryExists(staging)) throw new IOException("迁移暂存目录已存在。");
        Directory.CreateDirectory(staging);
        using (var marker = new FileStream(Path.Combine(staging, MigrationOwner), FileMode.CreateNew, FileAccess.Write)) {
            marker.Write(migration.Id.ToByteArray());
            marker.Flush(true);
        }
        var identities = new List<ulong>();
        foreach (var entry in migration.Entries) {
            if (!ManagedPath(entry.Source)) throw new IOException("副本路径超出 Halo 文件库。");
            string copy = Path.Combine(staging, entry.Relative);
            CheckLibraryParents(copy);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            CopyTree(entry.Source, copy, token);
            VerifyCopiedTree(entry.Source, copy, token);
            identities.Add(ReadFileId(copy));
        }
        var prepared = migration with { FileIds = identities.ToArray() };
        WriteStorageJournal(RootMigrationJournal, prepared, replace: true);
        return prepared;
    }

    static void PublishRootMigration(RootMigration migration, CancellationToken token)
    {
        string staging = RootStaging(migration);
        foreach (var entry in migration.Entries) {
            token.ThrowIfCancellationRequested();
            RequireRootVolume(migration);
            string target = Path.Combine(migration.Destination, entry.Relative);
            CheckLibraryParents(target);
            if (entry.InTrash) {
                EnsurePrivateFolder(migration.Destination, FlatTrash);
                string container = Path.GetDirectoryName(target)!;
                if (EntryExists(container)) throw new IOException("回收站目标目录已存在。");
                Directory.CreateDirectory(container);
            }
            if (EntryExists(target)) throw new IOException("磁盘根目录中的文件已变化，请重新预览迁移。");
            MoveEntry(Path.Combine(staging, entry.Relative), target, entry.Kind);
        }
    }

    static void RequireRootVolume(RootMigration migration)
    {
        try { if (ReadVolumeId(migration.Destination) == migration.Volume) return; }
        catch (IOException) { }
        throw new RootMigrationUnavailableException();
    }

    void RecoverRootMigration()
    {
        if (!File.Exists(RootMigrationJournal)) return;
        var migration = JsonSerializer.Deserialize<RootMigration>(File.ReadAllText(RootMigrationJournal))
            ?? throw InvalidRootMigration();
        bool committed = SamePath(DesktopDirectory, migration.Destination) && data.StorageVolume == migration.Volume;
        ValidateRootMigration(migration, committed);
        RequireRootVolume(migration);
        string staging = RootStaging(migration);
        string marker = Path.Combine(staging, MigrationOwner);
        CheckLibraryParents(marker);
        if (EntryExists(staging)) {
            if (!File.Exists(marker)) {
                if (Directory.EnumerateFileSystemEntries(staging).Any()) throw InvalidRootMigration();
            }
            else if (!File.ReadAllBytes(marker).AsSpan().SequenceEqual(migration.Id.ToByteArray()))
                throw InvalidRootMigration();
            if (!committed && migration.FileIds is { } identities) {
                for (int index = 0; index < migration.Entries.Length; index++) {
                    RequireRootVolume(migration);
                    var entry = migration.Entries[index];
                    string copy = Path.Combine(staging, entry.Relative);
                    string target = Path.Combine(migration.Destination, entry.Relative);
                    CheckLibraryParents(target);
                    if (EntryExists(copy) || !EntryExists(target)) continue;
                    if (ReadFileId(target) != identities[index]) throw InvalidRootMigration();
                    CheckLibraryParents(entry.Source);
                    VerifyCopiedTree(entry.Source, target, CancellationToken.None);
                    MoveEntry(target, copy, entry.Kind);
                    if (entry.InTrash) PruneFlatTrashContainer(target);
                }
            }
            // Keep the marker until the other entries are gone so cleanup can be retried.
            foreach (string entry in Directory.EnumerateFileSystemEntries(staging)) {
                if (SamePath(entry, marker)) continue;
                NoLink(entry);
                if (Directory.Exists(entry)) DeleteOwnedTree(entry);
                else File.Delete(entry);
            }
            File.Delete(marker);
            Directory.Delete(staging);
        }
        File.Delete(RootMigrationJournal);
    }

    static bool MatchesRootMigrationItems(StationDocument document, RootMigration migration)
    {
        var items = document.Items.Where(IsOwnedFile).Select(item => (item.Id, item.Value, item.Kind, false))
            .Concat((document.Trash ?? []).Where(IsOwnedFile).Select(item => (item.Id, item.Value, item.Kind, true)));
        return items.SequenceEqual(migration.Entries.Select(entry => (entry.Id, entry.Source, entry.Kind, entry.InTrash)));
    }

    void ValidateRootMigration(RootMigration migration, bool committed)
    {
        if (migration.Id == Guid.Empty || !Guid.TryParseExact(migration.Volume, "D", out _)
            || !IsVolumeRoot(migration.Destination) || CanonicalLibraryPath(migration.Destination) != migration.Destination
            || CanonicalLibraryPath(migration.Source) != migration.Source || SamePath(migration.Source, migration.Destination)
            || Below(path, migration.Destination) || !committed && !SamePath(DesktopDirectory, migration.Source)
            || migration.Entries == null || migration.FileIds is { } ids && ids.Length != migration.Entries.Length)
            throw InvalidRootMigration();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = data.Items.Concat(Trash).Where(IsOwnedFile).ToDictionary(item => item.Id);
        foreach (var entry in migration.Entries) {
            string name = Path.GetFileName(entry.Relative);
            string expected = entry.InTrash ? Path.Combine(FlatTrash, entry.Id.ToString("N"), name) : name;
            if (entry.Relative != expected || name != ValidDesktopName(name) || !Below(entry.Source, migration.Source)
                || !names.Add(entry.Relative) || !current.TryGetValue(entry.Id, out var item) || item.Kind != entry.Kind
                || !SamePath(item.Value, committed ? Path.Combine(migration.Destination, expected) : entry.Source)
                || !entry.InTrash && (ReservedFlatNames().Contains(name) || IsSystemRootEntry(Path.Combine(migration.Destination, name), migration.Destination)))
                throw InvalidRootMigration();
        }
    }

    static IOException InvalidRootMigration() => new("磁盘根目录的迁移记录无效。");

    static ulong ReadFileId(string path)
    {
        using var handle = OpenMigrationEntry(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            throw new IOException("无法验证迁移文件的标识。", new Win32Exception(Marshal.GetLastWin32Error()));
        return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MigrationFileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, uint length);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern SafeFileHandle OpenMigrationEntry(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out MigrationFileInformation information);
}
