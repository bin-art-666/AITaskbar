using System.Text.Json;

namespace Halo;

internal sealed partial class StationStore
{
    internal const string LibraryOfflineMessage = "文件库暂不可用，请连接磁盘或检查目录后重试。";
    bool catalogReadOnly, storageRecoveryPending;
    internal bool LibraryOffline { get; private set; }
    internal bool ReadOnly => catalogReadOnly || LibraryOffline;
    internal string? LoadWarning => LibraryOffline ? LibraryOfflineMessage : catalogWarning;
    string? catalogWarning;

    bool LibraryAccessible()
    {
        string directory = DesktopDirectory;
        try {
            if (data.StorageVolume is { } volume && ReadVolumeId(directory) != volume) return false;
            CheckLibraryParents(directory);
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            _ = entries.MoveNext();
            return true;
        }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException) {
            // The default directory is created on the first file import.
            return data.StorageDirectory == null && !data.Items.Any(IsOwnedFile) && !Trash.Any(IsOwnedFile)
                && !File.Exists(FlatJournal) && Directory.Exists(Path.GetPathRoot(directory));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    void RecoverAvailableStorage()
    {
        if (!LibraryAccessible()) LibraryOffline = storageRecoveryPending = true;
        else {
            try { RecoverStorage(); }
            catch (RootMigrationUnavailableException) { LibraryOffline = storageRecoveryPending = true; }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && !LibraryAccessible()) {
                LibraryOffline = storageRecoveryPending = true;
            }
        }
    }

    internal async Task<bool> RefreshLibraryAvailability()
    {
        // Do not probe a library in the middle of moving its contents.
        if (!await desktopGate.WaitAsync(0)) return false;
        try {
            bool wasOffline = LibraryOffline, wasReadOnly = ReadOnly;
            bool offline = !await Task.Run(LibraryAccessible);
            if (!offline && storageRecoveryPending && !catalogReadOnly) {
                try { await Task.Run(RecoverStorage); }
                catch (RootMigrationUnavailableException) { offline = true; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) {
                    App.Log(error);
                    if (!await Task.Run(LibraryAccessible)) offline = true;
                    else {
                        catalogReadOnly = true;
                        catalogWarning = "有未完成的存储操作，已停止写入以保护文件。";
                    }
                }
            }
            LibraryOffline = storageRecoveryPending = offline;
            return wasOffline != LibraryOffline || wasReadOnly != ReadOnly;
        } finally { desktopGate.Release(); }
    }

    void RequireAvailableLibrary()
    {
        if (LibraryOffline || !LibraryAccessible()) throw new IOException(LibraryOfflineMessage);
    }
}
