using Microsoft.UI.Dispatching;

namespace Halo;

public partial class App
{
    async Task VerifyStorageMaintenance(List<string> checks)
    {
        void Check(string name, bool condition)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        async Task WaitFor(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 70 && !condition(); attempt++) await Task.Delay(100);
        }
        string backup = Directory.CreateDirectory(Path.Combine(DataDirectory, "backup-verification")).FullName;
        string source = Path.Combine(DataDirectory, "backup-source.txt");
        await File.WriteAllTextAsync(source, "Halo maintenance");
        await Station.ImportCopies([new(StationKind.File, source)]);
        var original = Station.Items.Single();
        await Station.MoveGrid([original.Id], original.Id, 2, 1, false, 5, true);
        var position = Station.Layout(false, 5, true).Cells.Single();
        StartStorageMaintenance(DispatcherQueue.GetForCurrentThread());
        try {
            await WaitFor(() => storageWatcher != null);
            Check("WatcherRunsWhilePanelIsClosed", storageWatcher != null && Panel == null);
            string renamed = Path.Combine(Station.DesktopDirectory, "renamed.txt");
            File.Move(original.Value, renamed);
            await WaitFor(() => Station.Items.SingleOrDefault()?.Value == renamed);
            Check("ExternalRenameKeepsIdAndPosition", Station.Items.Single().Id == original.Id
                && Station.Items[0].Value == renamed && Station.Layout(false, 5, true).Cells.Single() == position);
            File.Delete(renamed);
            await WaitFor(() => Station.Items.Count == 0);
            Check("ExternalDeletionRemovesRecordAndLeavesRecycleBinUntouched", Station.Items.Count == 0 && Station.Trash.Count == 0);
        }
        finally { await StopStorageMaintenance(); }

        await Station.ImportCopies([new(StationKind.File, source)]);
        await Station.Add([StationStore.Text("备用便笺")]);
        string library = StationStore.ProposeStorageDirectory(DataDirectory);
        await Station.MoveStorage(library, CancellationToken.None);
        string disconnected = library + "-offline";
        Directory.Move(library, disconnected);
        try {
            await Station.SynchronizeExternalFiles(library, []);
            Check("UnavailableLibraryPreservesRecords", Station.Items.Count == 2);
        }
        finally { Directory.Move(disconnected, library); }
        await BackupNow(backup);
        string first = Directory.GetFiles(backup, "*.zip").Single();
        var now = DateTimeOffset.Now;
        var plan = new HaloBackupPlan(backup, Enabled: true, Weekly: false, Minute: 22 * 60, ActivatedAt: now.AddDays(-4));
        Settings.Save(Settings.Data with { Backup = plan });
        await RunScheduledBackup(now);
        await RunScheduledBackup(now);
        var archives = Directory.GetFiles(backup, "*.zip");
        Check("MissedPlanProducesExactlyOneExtraArchive", archives.Length == 2 && File.Exists(first));
        var restored = new StationStore(Path.Combine(DataDirectory, "restored-maintenance", "station.json"));
        await restored.RestoreDesktop(archives.Single(file => file != first));
        Check("ScheduledArchiveRestoresFileAndNote", restored.Items.Count == 2
            && File.ReadAllText(restored.Items.Single(item => item.Owned && item.Kind == StationKind.File).Value) == "Halo maintenance"
            && restored.Items.Any(item => item.Value == "备用便笺"));

        Settings.Save(Settings.Data with { Backup = plan with { Directory = Path.Combine(backup, "missing"), ActivatedAt = now.AddDays(-5) } });
        await RunScheduledBackup(now);
        Check("BackupFailurePreservesBothArchives", Directory.GetFiles(backup, "*.zip").Length == 2
            && BackupStatus.Contains("备份失败", StringComparison.Ordinal));
        Check("WeeklyBackupUsesChosenDay", new HaloBackupPlan(Weekly: true, Day: DayOfWeek.Wednesday, Minute: 600)
            .LatestSlot(new DateTime(2026, 11, 26, 9, 0, 0)) == new DateTime(2026, 11, 25, 10, 0, 0));
    }
}
