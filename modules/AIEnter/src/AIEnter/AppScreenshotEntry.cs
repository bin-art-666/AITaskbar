using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIEnter;

public partial class App
{
    EventWaitHandle? screenshotEvent;
    RegisteredWaitHandle? screenshotWait;
    ScreenshotSelection? screenshotSelection;
    ScreenshotPreview? screenshotPreview;
    Window? screenshotNotice;
    bool screenshotBusy;
    void InitializeScreenshotEntry(string prefix)
        => screenshotEvent = new(false, EventResetMode.AutoReset, prefix + ".Screenshot");
    void RegisterScreenshotEntry(DispatcherQueue dispatcher)
        => screenshotWait = ThreadPool.RegisterWaitForSingleObject(screenshotEvent!, (_, _) => dispatcher.TryEnqueue(() => _ = TakeScreenshot()), null, Timeout.Infinite, false);
    async Task TakeScreenshot()
    {
        if (screenshotBusy || aiQuitting || SessionState.Locked == true || !Appearance.ScreenshotDoubleClick) return;
        screenshotBusy = true;
        try
        {
            screenshotPreview?.Destroy(); screenshotPreview = null;
            Ai.Hide();
            await Task.Delay(200); // Let the existing panel exit animation finish before capture.
            var desktop = await Task.Run(ScreenshotCapture.Desktop);
            var selected = await ScreenshotSelection.Select(desktop, window => screenshotSelection = window);
            if (selected == null || aiQuitting) return;
            string path = await selected.Save(Appearance.ScreenshotFolder);
            screenshotPreview = await ScreenshotPreview.Open(selected,path);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory,"screenshot-error.log"),$"{DateTimeOffset.Now:O} {error}\n");
            if (!aiQuitting)
            {
                screenshotNotice?.Close();
                var notice = new Window { Title = "AIEnter · 截图未完成" };
                var text = new TextBlock { Text = "截图未完成："+error.Message, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(20) };
                notice.Content=text; notice.AppWindow.Resize(new(460,180)); screenshotNotice=notice;
                notice.Closed += (_, _) => { if (ReferenceEquals(screenshotNotice,notice)) screenshotNotice=null; }; notice.Activate();
            }
        }
        finally { screenshotBusy = false; }
    }
    bool screenshotSending;
    internal async Task SendScreenshot(string path, string provider, DesktopMonitor display)
    {
        if (screenshotSending) { Ai.DropError("正在处理上一张截图；新截图仍保存在本地，请稍后再放入。"); return; }
        screenshotSending = true;
        try
        {
            if (!await Ai.ReceiveFiles([path], provider, display))
                Ai.DropError("当前已有附件正在处理；截图已保存，请稍后从本地拖入。");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Ai.DropError("截图已保存在本地，放入未完成：" + error.Message); }
        finally { screenshotSending = false; }
    }
    void DisposeScreenshotEntry()
    {
        screenshotWait?.Unregister(null); screenshotSelection?.Cancel(); screenshotPreview?.Destroy(); screenshotNotice?.Close(); screenshotEvent?.Dispose();
    }
}
