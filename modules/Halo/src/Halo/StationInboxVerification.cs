using Microsoft.UI.Xaml;
using System.Text.Json;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifyDropQueue(Action<string, bool> check)
    {
        using var retain = Retain();
        var app = App.Current;
        string incoming = Path.Combine(App.DataDirectory, "incoming-drops"); Directory.CreateDirectory(incoming);
        string fixture = Path.Combine(App.DataDirectory, "queue-fixture"); Directory.CreateDirectory(fixture);
        async Task<string> Enqueue(string[] files, string[]? owned = null) {
            string path = Path.Combine(incoming, Guid.NewGuid() + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new TaskbarPacket(Files: files, OwnedFiles: owned))); return path;
        }
        string missing = Path.Combine(fixture, "旧失败.txt");
        string failed = await Enqueue([missing]); await app.DrainDrops();
        check("QueueFailureRetainedForManualRetry", File.Exists(failed + ".retry") && !File.Exists(failed) && status.Text.Contains("旧失败.txt"));
        string source = Path.Combine(fixture, "新成功.txt"); await File.WriteAllTextAsync(source, "新内容");
        string good = await Enqueue([source]); await app.DrainDrops();
        check("NewDropDoesNotReplayOldFailure", status.Text == "已存入 1 项" && File.Exists(failed + ".retry") && !File.Exists(good));
        check("NewDropHasCommittedReceipt", App.Station.HasImportReceipt(Path.GetFileNameWithoutExtension(good)));
        string anotherMissing = Path.Combine(fixture, "本批失败.txt");
        string mixedFailure = await Enqueue([anotherMissing]);
        string mixedSuccess = await Enqueue([source]); await app.DrainDrops();
        check("MixedDropSummaryKeepsSuccessfulResult", status.Text.Contains("已存入 1 项") && status.Text.Contains("另有 1 批未存入") && status.Text.Contains("本批失败.txt")
            && App.Station.HasImportReceipt(Path.GetFileNameWithoutExtension(mixedSuccess)) && File.Exists(mixedFailure + ".retry"));
        await File.WriteAllTextAsync(missing, "原失败现可读"); await app.DrainDrops(retryFailed: true);
        check("ManualRetryImportsRecoveredSource", !File.Exists(failed + ".retry") && App.Station.HasImportReceipt(Path.GetFileNameWithoutExtension(failed)) && File.Exists(mixedFailure + ".retry"));
        string shortcut = Path.Combine(fixture, "快捷方式.lnk"); byte[] shortcutBytes = [0x4c, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0];
        await File.WriteAllBytesAsync(shortcut, shortcutBytes);
        string shortcutPacket = await Enqueue([shortcut]); await app.DrainDrops();
        var shortcutCopy = App.Station.Items.Last(item => Path.GetFileName(item.Value) == "快捷方式.lnk");
        check("ShortcutImportsAsIndependentFile", shortcutCopy.Owned && shortcutCopy.Value != shortcut && (await File.ReadAllBytesAsync(shortcutCopy.Value)).SequenceEqual(shortcutBytes) && !File.Exists(shortcutPacket));
        string lockedPacket = await Enqueue([source]);
        using (var locked = new FileStream(lockedPacket, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            await app.DrainDrops();
            check("QueueCleanupFailureDoesNotClaimImportFailed", App.Station.HasImportReceipt(Path.GetFileNameWithoutExtension(lockedPacket)) && File.Exists(lockedPacket)
                && status.Text.Contains("已存入 1 项") && status.Text.Contains("队列清理待重试") && !status.Text.Contains("未存入"));
        }
        int count = App.Station.Items.Count; await app.DrainDrops();
        check("CommittedQueueCleanupRetryIsIdempotent", !File.Exists(lockedPacket) && App.Station.Items.Count == count && !status.Text.Contains("未完成"));
        string ownedId = Guid.NewGuid().ToString(); string batch = Path.Combine(incoming, ownedId); Directory.CreateDirectory(batch);
        string ownedFile = Path.Combine(batch, "附件.txt"); await File.WriteAllTextAsync(ownedFile, "附件");
        string ownedMissing = Path.Combine(fixture, "附件批次缺少.txt"); string ownedPacket = Path.Combine(incoming, ownedId + ".json");
        await File.WriteAllTextAsync(ownedPacket, JsonSerializer.Serialize(new TaskbarPacket(Files: [ownedFile, ownedMissing], OwnedFiles: [ownedFile])));
        await app.DrainDrops();
        check("FailedOwnedPacketKeepsAttachments", File.Exists(ownedPacket + ".retry") && File.Exists(ownedFile) && !App.Station.HasImportReceipt(ownedId));
        await File.WriteAllTextAsync(ownedMissing, "后续可读"); await app.DrainDrops(retryFailed: true);
        check("RetrySuffixKeepsOriginalAttachmentBatch", App.Station.HasImportReceipt(ownedId) && !File.Exists(ownedPacket + ".retry") && !Directory.Exists(batch) && File.Exists(ownedMissing));
        File.Delete(mixedFailure + ".retry"); // Remove only this test's intentionally unreadable packet.
        ClearNotification();
    }
}
