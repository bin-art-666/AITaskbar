using Microsoft.UI.Dispatching;
using System.Text.Json;

namespace Halo;

public partial class App
{
    sealed record BackupRun(DateTimeOffset? CompletedAt = null, string? Archive = null,
        DateTime? ScheduledSlot = null, DateTimeOffset? Activation = null, string? Error = null);

    readonly List<(string From, string To)> storageRenames = [];
    DispatcherQueueTimer? storageTimer;
    FileSystemWatcher? storageWatcher;
    string? observedRoot;
    DateTimeOffset? pendingFileCheck;
    DateTimeOffset nextFullCheck, nextBackupRetry;
    Task storageTick = Task.CompletedTask;
    Task? activeBackup;
    BackupRun backupRun = new();
    DateTimeOffset? retryActivation;
    const string BackupStatusName = "backup-status.json";

    internal event Action? BackupStatusChanged;
    internal bool BackupRunning => activeBackup is { IsCompleted: false };
    internal string BackupStatus => BackupRunning ? "正在备份…" : string.Join(Environment.NewLine, new[] {
        backupRun.CompletedAt is { } completed ? $"最近成功：{completed.LocalDateTime:g}" : "最近成功：尚未备份",
        backupRun.Error,
        Settings.Data.Backup is { Enabled: true } plan ? $"下次计划：{plan.NextSlot(DateTime.Now):g}" : null
    }.Where(message => message != null));

    void StartStorageMaintenance(DispatcherQueue dispatcher)
    {
        try {
            string status = Path.Combine(DataDirectory, BackupStatusName);
            if (File.Exists(status)) backupRun = JsonSerializer.Deserialize<BackupRun>(File.ReadAllText(status)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { Log(error); }
        storageTimer = dispatcher.CreateTimer();
        storageTimer.Interval = TimeSpan.FromSeconds(1);
        storageTimer.Tick += (_, _) => {
            if (storageTick.IsCompleted && !quitting) storageTick = UpdateStorage(dispatcher);
        };
        storageTimer.Start();
    }

    async Task UpdateStorage(DispatcherQueue dispatcher)
    {
        try {
            var now = DateTimeOffset.Now;
            string root = Station.DesktopDirectory;
            if (!string.Equals(observedRoot, root, StringComparison.OrdinalIgnoreCase) || storageWatcher == null && now >= nextFullCheck) {
                StartWatchingStorage(dispatcher, root);
                pendingFileCheck = now;
            }
            bool due = Settings.Data.Backup is { } plan && TryScheduledSlot(plan, now, out _);
            if (due || now >= nextFullCheck || pendingFileCheck is { } pending && now >= pending) {
                if (await CheckExternalFiles(root)) return;
            }
            if (!quitting && due) await RunScheduledBackup(now);
        }
        catch (Exception error) { Log(error); }
    }

    void StartWatchingStorage(DispatcherQueue dispatcher, string root)
    {
        storageWatcher?.Dispose();
        storageWatcher = null;
        observedRoot = root;
        storageRenames.Clear();
        if (!Directory.Exists(root)) return;
        FileSystemWatcher? watcher = null;
        try {
            watcher = new FileSystemWatcher(root) {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            };
            void OnChange(string path, string? oldPath = null) => dispatcher.TryEnqueue(() => {
                if (quitting || !ReferenceEquals(watcher, storageWatcher)
                    || IgnoredStoragePath(root, path) && (oldPath == null || IgnoredStoragePath(root, oldPath))) return;
                if (oldPath != null) storageRenames.Add((oldPath, path));
                pendingFileCheck ??= DateTimeOffset.Now.AddSeconds(2);
            });
            watcher.Deleted += (_, e) => OnChange(e.FullPath);
            watcher.Renamed += (_, e) => OnChange(e.FullPath, e.OldFullPath);
            watcher.Error += (_, _) => dispatcher.TryEnqueue(() => {
                if (!ReferenceEquals(watcher, storageWatcher)) return;
                storageWatcher?.Dispose();
                storageWatcher = null;
                nextFullCheck = DateTimeOffset.MinValue;
            });
            storageWatcher = watcher;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) {
            watcher?.Dispose();
            storageWatcher = null;
            Log(error);
        }
    }

    static bool IgnoredStoragePath(string root, string path)
    {
        string top = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, 2)[0];
        return top.Equals(".halo-work", StringComparison.OrdinalIgnoreCase)
            || top.Equals(".halo-trash", StringComparison.OrdinalIgnoreCase)
            || top.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || top.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    async Task<bool> CheckExternalFiles(string root)
    {
        var renames = storageRenames.ToArray();
        storageRenames.Clear();
        bool notifyPanel = pendingFileCheck != null;
        pendingFileCheck = null;
        nextFullCheck = DateTimeOffset.Now.AddMinutes(1);
        bool availabilityChanged = await Station.RefreshLibraryAvailability();
        bool pending = !quitting && await Station.SynchronizeExternalFiles(root, renames);
        if (pending) pendingFileCheck ??= DateTimeOffset.Now.AddSeconds(1);
        if (!quitting && Panel is { } panel && (notifyPanel || availabilityChanged))
            await panel.RefreshAfterExternalFiles(availabilityChanged);
        return pending;
    }

    bool TryScheduledSlot(HaloBackupPlan plan, DateTimeOffset now, out DateTime slot)
    {
        slot = plan.LatestSlot(now.LocalDateTime);
        return plan.Enabled && plan.ActivatedAt is { } activated && slot >= activated.LocalDateTime
            && (backupRun.Activation != activated || backupRun.ScheduledSlot == null || backupRun.ScheduledSlot < slot)
            && (retryActivation != activated || now >= nextBackupRetry);
    }

    internal async Task RunScheduledBackup(DateTimeOffset now)
    {
        if (BackupRunning || Settings.Data.Backup is not { } plan || !TryScheduledSlot(plan, now, out var slot)) return;
        try { await StartBackup(plan.Directory, slot, plan.ActivatedAt); }
        catch (Exception error) {
            retryActivation = plan.ActivatedAt;
            nextBackupRetry = now.AddMinutes(5);
            Log(error);
        }
    }

    internal Task BackupNow(string directory) => StartBackup(directory);

    Task StartBackup(string directory, DateTime? slot = null, DateTimeOffset? activation = null)
    {
        if (quitting) throw new IOException("Halo 正在退出。");
        if (BackupRunning) throw new IOException("已有备份正在运行。");
        activeBackup = BackupToDirectory(directory, slot, activation);
        BackupStatusChanged?.Invoke();
        return activeBackup;
    }

    async Task BackupToDirectory(string directory, DateTime? slot, DateTimeOffset? activation)
    {
        // Yield before publishing status so the running task is registered first.
        await Task.Yield();
        try {
            Station.ValidateBackupDirectory(directory);
            if (slot == null && await CheckExternalFiles(Station.DesktopDirectory)) {
                await Task.Delay(TimeSpan.FromSeconds(1));
                await CheckExternalFiles(Station.DesktopDirectory);
            }
            await Task.Run(() => Station.ValidateBackupDirectory(directory));
            string filename = slot is { } time && activation is { } started
                ? $"Halo-定时-{time:yyyyMMdd-HHmm}-{started.UtcTicks:x}.zip"
                : $"Halo-备份-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.zip";
            string archive = Path.Combine(directory, filename);
            if (slot != null && File.Exists(archive))
                await Task.Run(() => StationStore.VerifyBackupArchive(archive));
            else
                await Station.ExportDesktop(archive, verifyNewArchive: true);
            SaveBackupRun(backupRun with {
                CompletedAt = DateTimeOffset.Now, Archive = archive, Error = null,
                ScheduledSlot = slot ?? backupRun.ScheduledSlot, Activation = activation ?? backupRun.Activation
            });
        }
        catch (Exception error) {
            try { SaveBackupRun(backupRun with { Error = "备份失败：" + error.Message }); }
            catch (Exception statusError) { Log(statusError); }
            throw;
        }
        finally {
            activeBackup = null;
            BackupStatusChanged?.Invoke();
        }
    }

    void SaveBackupRun(BackupRun status)
    {
        backupRun = status;
        string destination = Path.Combine(DataDirectory, BackupStatusName);
        Directory.CreateDirectory(DataDirectory);
        string temporary = destination + ".tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, status);
                stream.Flush(true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    async Task StopStorageMaintenance()
    {
        storageTimer?.Stop();
        storageWatcher?.Dispose();
        storageWatcher = null;
        try { await Task.WhenAll(storageTick, activeBackup ?? Task.CompletedTask); }
        catch (Exception error) { Log(error); }
    }
}
