using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Halo;

internal sealed record TaskbarPacket(int Version = 1, string Text = "", string[]? Files = null, string[]? OwnedFiles = null, string? Bitmap = null, string? Error = null, string? Html = null, string SourceUrl = "");
internal sealed record TaskbarImportResult(TaskbarPacket Packet, StationAddResult Result);
internal sealed class TaskbarSourceException(string message) : IOException(message);
public partial class App
{
    bool draining, storageMigration;
    internal async Task ChangeStationStorage(LibraryMigrationPlan plan, CancellationToken token)
    {
        if (draining || storageMigration || quitting) throw new IOException("Halo 正在处理文件，请稍后更改位置。");
        storageMigration = true;
        try { await Station.MoveStorage(plan, token); }
        finally { storageMigration = false; }
    }
    internal void ResumeQueuedDrops()
    {
        if (Directory.Exists(Path.Combine(DataDirectory, "incoming-drops"))) _ = DrainDrops();
    }
    internal async Task DrainDrops(bool retryFailed = false)
    {
        if (draining || quitting || storageMigration) return; draining = true;
        string incoming = Path.Combine(DataDirectory, "incoming-drops");
        try {
            if (!Directory.Exists(incoming)) { if (retryFailed) Panel?.Notify("没有待重试的拖入"); return; }
            Panel ??= CreatePanel();
            using var lease = Panel.Retain();
            if (Station.LibraryOffline && Pending().Any()) {
                Panel.Open();
                Panel.ReportImportFailure(StationStore.LibraryOfflineMessage);
                return;
            }
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int completed = 0, added = 0, duplicates = 0, cleanupPending = 0;
            var failures = new List<ImportIssue>();
            while (Pending().Where(path => !attempted.Contains(path)).OrderBy(path => path).ToArray() is { Length: > 0 } pending)
                foreach (string path in pending) {
                    attempted.Add(path);
                    Panel ??= CreatePanel(); Panel.Open();
                    TaskbarImportResult imported;
                    try { imported = await ImportPacket(path); }
                    catch (Exception error) {
                        string failure = DropFailure(path, error);
                        string suffix = error is TaskbarSourceException ? ".failed" : ".retry";
                        string retained = path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? path : path + suffix;
                        try { if (retained != path) File.Move(path, retained); }
                        catch (Exception moveError) { Log(moveError); }
                        attempted.Add(retained);
                        failures.Add(new(failure, DropBatchId(path)));
                        Log(new IOException($"拖入批次 {DropBatchId(path)} 未完成。", error));
                        continue;
                    }
                    // ImportCopies has committed its receipt. Queue cleanup failure cannot undo that success.
                    completed++; added += imported.Result.Added; duplicates += imported.Result.Duplicates;
                    Panel.ResolveDropFailure(DropBatchId(path));
                    try { File.Delete(path); Cleanup(path, imported.Packet); }
                    catch (Exception error) { cleanupPending++; Log(new IOException("内容已存入，拖入队列清理待重试。", error)); }
                    Panel.ConfirmIncomingDrop();
                }
            if (completed > 0 || failures.Count > 0) Panel?.ReportImportFeedback(failures);
            if (completed > 0) {
                string result = added > 0 || duplicates > 0 ? $"已存入 {added} 项" : "拖入内容已存入，无需重复导入";
                if (duplicates > 0) result += $"，跳过 {duplicates} 项重复内容";
                if (failures.Count > 0) result += $"；另有 {failures.Count} 批未存入（{failures[0].Message}），可在桌面管理中重试";
                if (cleanupPending > 0) result += "；内容已保存，队列清理待重试";
                Panel?.Notify(result, failures.Count > 0 || cleanupPending > 0);
            } else if (failures.Count > 0) Panel?.Notify($"拖入未完成：{failures[0].Message}。失败批次已单独保留，请在桌面管理中重试或重新拖入", true);
            else if (retryFailed) Panel?.Notify("没有待重试的拖入");

            IEnumerable<string> Pending() => Directory.EnumerateFiles(incoming, "*.json")
                .Concat(retryFailed ? Directory.EnumerateFiles(incoming, "*.json.retry") : []);
        } finally { draining = false; }
    }
    static string DropBatchId(string path) => Path.GetFileNameWithoutExtension(path.EndsWith(".json.retry", StringComparison.OrdinalIgnoreCase) ? path[..^6] : path);
    static string DropFailure(string path, Exception error)
    {
        string name = "当前批次";
        try {
            if (new FileInfo(path).Length <= 1024 * 1024) {
                var packet = JsonSerializer.Deserialize<TaskbarPacket>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (packet?.Files is { Length: > 0 }) name = Path.GetFileName(packet.Files[0]);
            }
        } catch { }
        if (error is FileNotFoundException { FileName: { Length: > 0 } missing }) name = Path.GetFileName(missing);
        return StationWindow.ImportFailure(error, name);
    }
    internal static async Task<TaskbarImportResult> ImportPacket(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("拖放数据过大。");
        string id = DropBatchId(path);
        if (!Guid.TryParse(id, out _)) throw new IOException("拖放批次无效。");
        var packet = JsonSerializer.Deserialize<TaskbarPacket>(await File.ReadAllTextAsync(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new IOException("拖放数据无效。");
        if (!string.IsNullOrEmpty(packet.Error)) throw new TaskbarSourceException(packet.Error);
        if (packet.Version is not (1 or 2) || packet.Text == null || packet.Text.Length > StationStore.MaximumText || (packet.Files?.Length ?? 0) > StationStore.MaximumCapacity) throw new IOException("拖放版本、数量或文字无效。");
        if (Station.HasImportReceipt(id)) return new(packet, new(0, 0));
        string batch = Path.Combine(Path.GetDirectoryName(path)!, id);
        string destination = Path.Combine(Station.AttachmentDirectory, "taskbar-" + id);
        bool Owned(string value) => Path.IsPathFullyQualified(value) && Path.GetDirectoryName(Path.GetFullPath(value))!.Equals(Path.GetFullPath(batch), StringComparison.OrdinalIgnoreCase)
            && !File.GetAttributes(batch).HasFlag(System.IO.FileAttributes.ReparsePoint) && !File.GetAttributes(value).HasFlag(System.IO.FileAttributes.ReparsePoint);
        var owned = new HashSet<string>(packet.OwnedFiles ?? [], StringComparer.OrdinalIgnoreCase);
        var inputs = new List<StationInput>();
        foreach (string file in packet.Files ?? [])
        {
            string target = file;
            if (owned.Contains(file)) {
                if (!Owned(file)) throw new IOException("附件路径超出本批次。");
                Directory.CreateDirectory(destination); target = Path.Combine(destination, Path.GetFileName(file));
                await CopyAttachment(file, target);
            }
            if (!Path.IsPathFullyQualified(target)) throw new IOException("文件路径无效。");
            // Packet paths are copied as files, including .lnk. WinRT StorageFile can reject shortcuts.
            var attributes = File.GetAttributes(target);
            inputs.Add(new(attributes.HasFlag(System.IO.FileAttributes.Directory) ? StationKind.Folder : StationKind.File, target));
        }
        if (!string.IsNullOrEmpty(packet.Bitmap)) {
            if (!owned.Contains(packet.Bitmap) || !Owned(packet.Bitmap)) throw new IOException("图片附件路径无效。");
            string target = Path.Combine(destination, "图片.png");
            if (!File.Exists(target)) {
                var data = new DataPackage(); data.SetBitmap(RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(packet.Bitmap)));
                using var import = await StationTransfer.Read(data.GetView(), Station.AttachmentDirectory);
                Directory.CreateDirectory(destination); await CopyAttachment(import.Inputs.Single().Value, target);
            }
            inputs.Add(new(StationKind.File, target));
        }
        if (!string.IsNullOrEmpty(packet.Html) && packet.Files is not { Length: > 0 } && string.IsNullOrEmpty(packet.Bitmap))
            inputs.Add(StationStore.WebUri(packet.Text) is {} link && RichContent.IsSingleAnchor(packet.Html) ? new(StationKind.Link, link.AbsoluteUri) : RichContent.FromHtml(packet.Html, fallback: packet.Text, sourceUrl: packet.SourceUrl));
        else if (packet.Text.Length > 0) inputs.Add(StationStore.Text(packet.Text));
        if (Panel != null) { var chosen = await Panel.ChooseLinkBatch(inputs.ToArray()); if (chosen == null) throw new OperationCanceledException("用户取消存入，批次已保留。"); inputs = chosen.ToList(); }
        var result = await Station.ImportCopies(inputs, receipt: id);
        return new(packet, result);
    }
    static async Task CopyAttachment(string source, string target)
    {
        // A crash during copy leaves only .tmp; retries never mistake a partial file for a committed attachment.
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (File.Exists(target)) {
            await using var saved = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            byte[] originalHash = await System.Security.Cryptography.SHA256.HashDataAsync(input);
            byte[] savedHash = await System.Security.Cryptography.SHA256.HashDataAsync(saved);
            if (input.Length != saved.Length || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(originalHash, savedHash))
                throw new IOException("已保存的附件与来源不同，已保留两份文件。");
            return;
        }
        string temporary = target + ".tmp";
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true)) {
            await input.CopyToAsync(output); await output.FlushAsync(); output.Flush(true);
        }
        File.Move(temporary, target);
    }
    static void Cleanup(string path, TaskbarPacket packet)
    {
        string batch = Path.Combine(Path.GetDirectoryName(path)!, DropBatchId(path));
        foreach (string file in packet.OwnedFiles ?? []) try {
            if (Path.GetDirectoryName(Path.GetFullPath(file)) == batch && !File.GetAttributes(batch).HasFlag(System.IO.FileAttributes.ReparsePoint)
                && !File.GetAttributes(file).HasFlag(System.IO.FileAttributes.ReparsePoint)) File.Delete(file);
        } catch { }
        try { if (Directory.Exists(batch) && !Directory.EnumerateFileSystemEntries(batch).Any()) Directory.Delete(batch); } catch { }
    }
}
