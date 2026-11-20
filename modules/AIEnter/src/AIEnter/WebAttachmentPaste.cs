using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.Web.WebView2.Core;

namespace AIEnter;

// Clipboard is held only for the browser paste. Preserve all original formats
// in memory without logging contents; restore only while we still own it.
internal sealed class ClipboardRestore : IAsyncDisposable
{
    const int ClipboardBusy = unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN
    const int ClipboardCannotEmpty = unchecked((int)0x800401D1);
    const int ClipboardCannotSet = unchecked((int)0x800401D2);
    readonly DataPackage original = new();
    readonly List<IRandomAccessStream> retainedStreams = [];
    readonly uint capturedSequence = GetClipboardSequenceNumber();
    uint ownedSequence;
    bool owns;
    ClipboardRestore() { }
    internal static async Task<ClipboardRestore> Capture(CancellationToken cancellation = default)
    {
        var snapshot = new ClipboardRestore();
        try
        {
            var view = Clipboard.GetContent();
            foreach (string format in view.AvailableFormats)
            {
                if (format == StandardDataFormats.Bitmap)
                {
                    var reference = await view.GetBitmapAsync().AsTask(cancellation);
                    using var source = await reference.OpenReadAsync().AsTask(cancellation);
                    if (source.Size > 64 * 1024 * 1024) throw new IOException("剪贴板图片过大，无法安全暂存。请改用网页附件按钮。");
                    var memory = new InMemoryRandomAccessStream();
                    snapshot.retainedStreams.Add(memory);
                    await RandomAccessStream.CopyAsync(source, memory).AsTask(cancellation);
                    memory.Seek(0);
                    snapshot.original.SetBitmap(RandomAccessStreamReference.CreateFromStream(memory));
                }
                else if (format == StandardDataFormats.StorageItems) snapshot.original.SetStorageItems(await view.GetStorageItemsAsync().AsTask(cancellation));
                else snapshot.original.SetData(format, await view.GetDataAsync(format).AsTask(cancellation));
            }
            snapshot.CheckUnchanged();
            return snapshot;
        }
        catch { await snapshot.DisposeAsync(); throw; }
    }
    void CheckUnchanged()
    {
        if (GetClipboardSequenceNumber() != capturedSequence)
            throw new COMException("剪贴板正在变化。", ClipboardBusy);
    }
    internal static async Task<ClipboardRestore> PreparePaste(DataPackage package)
    {
        // Retry before dispatching the browser command, so the page sees only one paste.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (int attempt = 0; ; attempt++)
        {
            ClipboardRestore? snapshot = null;
            try
            {
                snapshot = await Capture(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                snapshot.CheckUnchanged();
                snapshot.Set(package);
                return snapshot;
            }
            catch (Exception error)
            {
                if (snapshot != null) await snapshot.DisposeAsync();
                bool busy = error.HResult is ClipboardBusy or ClipboardCannotEmpty or ClipboardCannotSet;
                if ((busy && (attempt == 9 || timeout.IsCancellationRequested))
                    || (error is OperationCanceledException && timeout.IsCancellationRequested))
                    throw new IOException("剪贴板暂时不可用，附件已保留，请稍后重试。", error);
                if (!busy) throw;
                await Task.Delay(80);
            }
        }
    }
    internal void Set(DataPackage package)
    { Clipboard.SetContent(package); ownedSequence=GetClipboardSequenceNumber(); owns=true; }
    public async ValueTask DisposeAsync()
    {
        try
        {
            for (int attempt=0;owns && attempt<10;attempt++)
            {
                if (GetClipboardSequenceNumber()!=ownedSequence) return;
                try { Clipboard.SetContent(original); Clipboard.Flush(); return; }
                catch (COMException) when(attempt<9) { await Task.Delay(80); }
            }
        }
        finally { foreach(var stream in retainedStreams)stream.Dispose(); }
    }
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
}

internal static class WebAttachmentPaste
{
    internal static async Task Deliver(CoreWebView2 core,string[] files,Action dropping)
    {
        var package=new DataPackage { RequestedOperation=DataPackageOperation.Copy };
        var storage=new List<IStorageItem>();
        foreach(string path in files)storage.Add(await StorageFile.GetFileFromPathAsync(path));
        package.SetStorageItems(storage,true);
        if(files.Length==1&&new[]{".png",".jpg",".jpeg",".bmp",".webp"}.Contains(Path.GetExtension(files[0]).ToLowerInvariant()))
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile((IStorageFile)storage[0]));
        string focused=await core.ExecuteScriptAsync(DropWebScripts.FocusEditor).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        if(focused=="null")throw new IOException("网页输入区尚未就绪，请完成登录并打开对话页。");
        await using var restore = await ClipboardRestore.PreparePaste(package);
        try
        {
            await core.ExecuteScriptAsync("window.__aiEnterPasteAck={received:false,count:0};window.__aiEnterPasteAckHandler=e=>{if(e.isTrusted){window.__aiEnterPasteAck.received=true;window.__aiEnterPasteAck.count=e.clipboardData?.files?.length??0;}};window.addEventListener('paste',window.__aiEnterPasteAckHandler,true);");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled","{\"enabled\":true}").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            dropping();
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new { type="rawKeyDown",modifiers=2,key="v",code="KeyV",windowsVirtualKeyCode=86,commands=new[]{"Paste"} })).AsTask().WaitAsync(TimeSpan.FromSeconds(6));
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new { type="keyUp",modifiers=2,key="v",code="KeyV",windowsVirtualKeyCode=86 })).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            long deadline=Environment.TickCount64+4000;
            bool received=false;
            while(Environment.TickCount64<deadline)
            {
                string ack=await core.ExecuteScriptAsync("!!window.__aiEnterPasteAck?.received&&window.__aiEnterPasteAck.count>0").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                if(ack=="true"){received=true;break;}
                await Task.Delay(50);
            }
            if(!received)throw new IOException("浏览器尚未确认收到粘贴文件，请检查网页附件；不会自动重投。");
        }
        finally
        {
            try { await core.ExecuteScriptAsync("if(window.__aiEnterPasteAckHandler)window.removeEventListener('paste',window.__aiEnterPasteAckHandler,true);delete window.__aiEnterPasteAckHandler;delete window.__aiEnterPasteAck;").AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }catch { }
            try { await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled","{\"enabled\":false}").AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }catch { }
        }
    }
}
