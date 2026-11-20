using System.Text.Json;

namespace AIEnter;

internal sealed record TaskbarDropPacket(int Version = 1, string Provider = "", string Text = "", string[]? Files = null, string[]? OwnedFiles = null, string? Bitmap = null, string? Error = null);

public partial class App
{
    EventWaitHandle? dropEvent, hoverEvent;
    RegisteredWaitHandle? dropWait, hoverWait;
    bool drainingDrops;
    static void SignalDropEntry(string prefix, bool drop)
    {
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + (drop ? ".Drop" : ".Hover")); signal.Set();
    }
    void InitializeDropEntry(string prefix, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        dropEvent = new(false, EventResetMode.AutoReset, prefix + ".Drop"); hoverEvent = new(false, EventResetMode.AutoReset, prefix + ".Hover");
        dropWait = ThreadPool.RegisterWaitForSingleObject(dropEvent, (_, _) => dispatcher.TryEnqueue(() => _ = DrainTaskbarDrops()), null, Timeout.Infinite, false);
        hoverWait = ThreadPool.RegisterWaitForSingleObject(hoverEvent, (_, _) => dispatcher.TryEnqueue(() => _ = OpenDropEntry(false)), null, Timeout.Infinite, false);
    }
    async Task OpenDropEntry(bool activate)
    {
        try { await Ai.OpenForDrop(Native.Monitors().First(m => m.Primary), activate); }
        catch (Exception failure) { Ai.DropError("无法打开拖放目标：" + failure.Message); }
    }
    async Task DrainTaskbarDrops()
    {
        if (drainingDrops || aiQuitting) return;
        drainingDrops = true;
        try
        {
            string directory = Path.Combine(DataDirectory, "incoming-drops");
            if (!Directory.Exists(directory)) return;
            await OpenDropEntry(true);
            while (!aiQuitting)
            {
                var path = Directory.EnumerateFiles(directory, "*.json").OrderBy(File.GetCreationTimeUtc).FirstOrDefault();
                if (path == null) break;
                DropPayload? payload = null;
                try
                {
                    var incoming = await DropPayload.ReadPacket(path, directory);
                    payload = incoming.Payload;
                    if (!await Ai.ReceivePayload(payload, incoming.Provider.Length == 0 ? Ai.Options.Data.Provider : incoming.Provider))
                        throw new IOException("当前 AI 正在处理其他内容，请稍后重新拖入。");
                    payload = null; File.Delete(path);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    payload?.Dispose();
                    Ai.DropError("任务栏拖入未完成：" + failure.Message);
                    try { File.Move(path, path + ".failed", true); } catch (IOException) { break; }
                }
            }
        }
        finally { drainingDrops = false; }
    }
    void DisposeDropEntry()
    {
        dropWait?.Unregister(null); hoverWait?.Unregister(null); dropEvent?.Dispose(); hoverEvent?.Dispose();
    }
}