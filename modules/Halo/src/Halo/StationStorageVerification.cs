namespace Halo;

public partial class App
{
    async Task VerifyStorageMigration(Action<string, bool> check)
    {
        string fixture = Path.Combine(DataDirectory, "migration-fixture");
        string payload = Path.Combine(fixture, "演示资料");
        Directory.CreateDirectory(Path.Combine(payload, "空目录"));
        string original = Path.Combine(payload, "这是一个完整的示例文件名.txt");
        await File.WriteAllTextAsync(original, "完整副本");
        string stationPath = Path.Combine(fixture, "profile", "station.json");
        string legacyLibrary = Path.Combine(fixture, "legacy-library");
        Directory.CreateDirectory(Path.GetDirectoryName(stationPath)!);
        Directory.CreateDirectory(legacyLibrary);
        await File.WriteAllTextAsync(stationPath, System.Text.Json.JsonSerializer.Serialize(new StationDocument(5, [], StorageDirectory: legacyLibrary)));
        var station = new StationStore(stationPath);
        await station.ImportCopies([new(StationKind.Folder, payload), new(StationKind.File, original)]);
        await station.Add([new(StationKind.File, original)]);
        var active = station.Items.Single(i => i.Owned && i.Kind == StationKind.Folder);
        var trash = station.Items.Single(i => i.Owned && i.Kind == StationKind.File);
        await station.RemoveArchived([trash]);
        await station.ImportCopies([new(StationKind.File, original), new(StationKind.File, original)]);
        await station.MoveGrid([active.Id], active.Id, 1, 1, false, 4, true);
        await station.MoveGrid([active.Id], active.Id, 2, 1, true, 4, true);
        var layoutBefore = station.Layout(false, 4, true).Cells;
        var expandedBefore = station.Layout(true, 4, true).Cells;
        var idsBefore = station.Items.Select(i => i.Id).ToArray();
        string oldRoot = station.DesktopDirectory;

        string destination = StationStore.ProposeStorageDirectory(fixture);
        var plan = station.PreviewStorageMigration(destination);
        string numbered = Path.GetFileNameWithoutExtension(original) + " (2).txt";
        check("MigrationPreviewListsNumberedDuplicate", plan.Entries.Count(i => !i.InTrash && i.Relative == numbered) == 1);
        await station.MoveStorage(plan, CancellationToken.None);
        var reopened = new StationStore(stationPath);
        var relocated = reopened.Items.Single(i => i.Id == active.Id);
        check("MigratedLibraryReloadsFilesTrashAndLayout", !reopened.ReadOnly && reopened.DesktopDirectory == destination
            && Path.GetDirectoryName(relocated.Value) == destination
            && File.ReadAllText(Path.Combine(relocated.Value, Path.GetFileName(original))) == "完整副本"
            && Directory.Exists(Path.Combine(relocated.Value, "空目录"))
            && File.ReadAllText(reopened.Trash.Single(i => i.Id == trash.Id).Value) == "完整副本"
            && reopened.Layout(false, 4, true).Cells.SequenceEqual(layoutBefore)
            && reopened.Layout(true, 4, true).Cells.SequenceEqual(expandedBefore)
            && reopened.Items.Select(i => i.Id).SequenceEqual(idsBefore)
            && File.Exists(Path.Combine(destination, Path.GetFileName(original))) && File.Exists(Path.Combine(destination, numbered)));
        check("MigrationKeepsOldCopiesAndLegacyReference", Directory.Exists(oldRoot) && Directory.Exists(active.Value)
            && File.Exists(trash.Value) && reopened.Items.Single(i => !i.Owned).Value == original);
        await reopened.ImportCopies([new(StationKind.File, original)]);
        check("NewImportsUseMigratedLibrary", reopened.Items.Where(i => i.Owned).All(i => Path.GetDirectoryName(i.Value) == destination));
        string backup = Path.Combine(fixture, "portable.zip");
        await reopened.ExportDesktop(backup);
        await reopened.RestoreDesktop(backup);
        check("BackupRestoreKeepsConfiguredLibrary", new StationStore(stationPath) is { ReadOnly: false } backupStore && backupStore.DesktopDirectory == destination
            && reopened.Items.Where(i => i.Owned).All(i => Path.GetDirectoryName(i.Value) == destination && (File.Exists(i.Value) || Directory.Exists(i.Value))));

        string canceled = StationStore.ProposeStorageDirectory(fixture);
        try { await reopened.MoveStorage(canceled, new CancellationToken(canceled: true)); }
        catch (OperationCanceledException) { }
        check("CanceledMigrationDoesNotSwitch", !Directory.Exists(canceled) && reopened.DesktopDirectory == destination);

        string missingCopy = reopened.Trash.Single().Value;
        File.Move(missingCopy, missingCopy + ".missing");
        string failed = StationStore.ProposeStorageDirectory(fixture);
        bool rejected = false;
        try { await reopened.MoveStorage(failed, CancellationToken.None); }
        catch (IOException) { rejected = true; }
        check("FailedMigrationKeepsActiveLibraryAndCleansStaging", rejected && reopened.DesktopDirectory == destination
            && new StationStore(stationPath).DesktopDirectory == destination && !Directory.Exists(failed)
            && !Directory.GetDirectories(fixture, ".Halo-migration-*").Any());
        File.Move(missingCopy + ".missing", missingCopy);

        var layoutAtInterruption = reopened.Layout(true, 4, true).Cells;
        foreach (bool renamed in new[] { false, true }) {
            string interrupted = StationStore.ProposeStorageDirectory(fixture);
            Guid operation = Guid.NewGuid();
            string staging = renamed ? interrupted : Path.Combine(fixture, ".Halo-migration-" + operation.ToString("N"));
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, ".halo-migration-owner"), operation.ToString("N"));
            await File.WriteAllTextAsync(Path.Combine(staging, "部分迁移.txt"), "未提交");
            string journal = Path.Combine(Path.GetDirectoryName(stationPath)!, "storage-migration.json");
            await File.WriteAllTextAsync(journal, System.Text.Json.JsonSerializer.Serialize(new { Id = operation, Destination = interrupted }));
            var afterCrash = new StationStore(stationPath);
            check($"MigrationCrashBeforeCommit_{renamed}", !afterCrash.ReadOnly && afterCrash.DesktopDirectory == destination
                && !Directory.Exists(staging) && !File.Exists(journal) && File.Exists(missingCopy)
                && afterCrash.Layout(true, 4, true).Cells.SequenceEqual(layoutAtInterruption));
        }
        var committedOperation = Guid.NewGuid();
        string committedMarker = Path.Combine(destination, ".halo-migration-owner");
        string committedJournal = Path.Combine(Path.GetDirectoryName(stationPath)!, "storage-migration.json");
        await File.WriteAllTextAsync(committedMarker, committedOperation.ToString("N"));
        await File.WriteAllTextAsync(committedJournal, System.Text.Json.JsonSerializer.Serialize(new { Id = committedOperation, Destination = destination }));
        var afterCommit = new StationStore(stationPath);
        check("MigrationCrashAfterCommitKeepsFlatLibrary", !afterCommit.ReadOnly && afterCommit.DesktopDirectory == destination
            && File.Exists(missingCopy) && Directory.Exists(relocated.Value) && !File.Exists(committedMarker) && !File.Exists(committedJournal));

        await File.WriteAllTextAsync(committedMarker, committedOperation.ToString("N"));
        await File.WriteAllTextAsync(committedJournal, System.Text.Json.JsonSerializer.Serialize(new { Id = committedOperation, Destination = destination }));
        string disconnected = destination + "-disconnected";
        Directory.Move(destination, disconnected);
        StationStore offline;
        try {
            offline = new StationStore(stationPath);
            check("OfflineConfiguredLibraryDefersRecoveryAndKeepsLatestCatalog", offline.LibraryOffline
                && offline.DesktopDirectory == destination && offline.Items.SequenceEqual(afterCommit.Items)
                && File.Exists(committedJournal) && !Directory.Exists(destination));
        } finally { Directory.Move(disconnected, destination); }
        check("ReconnectCompletesDeferredStorageRecovery", await offline.RefreshLibraryAvailability() && !offline.ReadOnly
            && !File.Exists(committedJournal) && !File.Exists(committedMarker) && File.Exists(missingCopy));
        await VerifyFlatLibrary(check, fixture);
    }

    static async Task VerifyFlatLibrary(Action<string, bool> check, string fixture)
    {
        string catalog = Path.Combine(fixture, "flat-profile", "station.json");
        var store = new StationStore(catalog);
        string first = Path.Combine(fixture, "file-source", "报告.txt");
        string folder = Path.Combine(fixture, "folder-source", "报告.TXT");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.Combine(folder, "空目录"));
        await File.WriteAllTextAsync(first, "同名文件");
        await store.ImportCopies([new(StationKind.File, first), new(StationKind.File, first), new(StationKind.Folder, folder)]);
        var initial = store.Items.ToArray();
        check("FlatImportsShareCaseInsensitiveFileAndFolderNames", initial.Select(i => Path.GetFileName(i.Value))
            .SequenceEqual(["报告.txt", "报告 (2).txt", "报告.TXT (2)"])
            && initial.All(i => Path.GetDirectoryName(i.Value) == store.DesktopDirectory)
            && Directory.Exists(Path.Combine(initial[2].Value, "空目录")));

        await store.RemoveArchived([initial[0]]);
        var trashed = store.Trash.Single();
        check("FlatTrashMovesCopyOutOfLibraryRoot", !File.Exists(initial[0].Value)
            && File.ReadAllText(trashed.Value) == "同名文件"
            && File.GetAttributes(Path.Combine(store.DesktopDirectory, ".halo-trash")).HasFlag(FileAttributes.Hidden));
        await File.WriteAllTextAsync(initial[0].Value, "资源管理器新文件");
        await store.Restore([initial[0]]);
        var restored = store.Items.Single(i => i.Id == initial[0].Id);
        check("FlatTrashRestoreNumbersConflictWithoutOverwriting", Path.GetFileName(restored.Value) == "报告 (3).txt"
            && File.ReadAllText(restored.Value) == "同名文件" && File.ReadAllText(initial[0].Value) == "资源管理器新文件"
            && !File.Exists(trashed.Value) && store.Trash.Count == 0);
        await store.RenameOwned(restored.Id, "报告.txt");
        var renamed = store.Items.Single(i => i.Id == restored.Id);
        check("FlatRenameKeepsIdAndResolvesOccupiedNames", renamed.Value == restored.Value && File.Exists(renamed.Value)
            && File.ReadAllText(initial[0].Value) == "资源管理器新文件");
        await store.RemoveArchived([renamed]);
        await store.DeleteTrash(store.Trash.ToArray());
        check("FlatPermanentDeleteLeavesOtherFiles", !File.Exists(renamed.Value) && store.Trash.Count == 0
            && File.Exists(initial[1].Value) && Directory.Exists(initial[2].Value) && File.Exists(initial[0].Value)
            && new StationStore(catalog) is { ReadOnly: false });
        await store.RemoveArchived([initial[2]]);
        await store.NewFolder(Path.GetFileName(initial[2].Value));
        await store.Restore([initial[2]]);
        var restoredFolder = store.Items.Single(i => i.Id == initial[2].Id);
        check("FlatRestoreFolderKeepsContentsAndNumbersConflict", Path.GetDirectoryName(restoredFolder.Value) == store.DesktopDirectory
            && restoredFolder.Value != initial[2].Value && Directory.Exists(Path.Combine(restoredFolder.Value, "空目录"))
            && Directory.Exists(initial[2].Value));
    }
}
