using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace AIEnter;

public partial class App
{
    async Task VerifyScreenshots()
    {
        ScreenshotPreview? preview = null;
        void Check(bool condition, string error) { if (!condition) throw new Exception(error); }
        try
        {
            if (!Environment.GetCommandLineArgs().Contains("--data-dir")) throw new InvalidOperationException("Screenshot verification requires isolated data.");
            Directory.CreateDirectory(DataDirectory);
            var pixels = new byte[80 * 60 * 4];
            for (int y=0;y<60;y++) for(int x=0;x<80;x++) { int i=(y*80+x)*4; pixels[i]=(byte)x; pixels[i+1]=(byte)y; pixels[i+2]=190; pixels[i+3]=255; }
            var desktop = new ScreenshotFrame(-1920,-200,80,60,pixels);
            var crop=desktop.Crop(10,15,30,20);
            Check(crop.Left==-1910 && crop.Top==-185 && crop.Width==30 && crop.Height==20,"Negative desktop origin or pixel crop was lost.");
            Check(crop.Pixels[0]==10 && crop.Pixels[1]==15 && crop.Pixels[^4]==39 && crop.Pixels[^3]==34,"Crop copied wrong pixels.");
            var clamped=desktop.Crop(79,59,20,20); Check(clamped.Width==1 && clamped.Height==1,"Crop exceeded desktop bounds.");
            string folder=Path.Combine(DataDirectory,"screenshots");
            string path=await crop.Save(folder); string other=await crop.Save(folder);
            Check(path!=other && File.Exists(path) && !Directory.EnumerateFiles(folder,"*.tmp").Any(),"Screenshot save was not unique and atomic.");
            var file=await StorageFile.GetFileFromPathAsync(path);
            using (var stream=await file.OpenReadAsync())
            {
                var decoder=await BitmapDecoder.CreateAsync(stream);
                Check(decoder.PixelWidth==30 && decoder.PixelHeight==20,"Saved PNG has wrong dimensions.");
            }
            const string clipboardMarker="AIEnter.VerificationClipboard";
            string marker=Guid.NewGuid().ToString("N");
            var sentinel=new Windows.ApplicationModel.DataTransfer.DataPackage(); sentinel.SetText("AIEnter clipboard restore fixture"); sentinel.SetData(clipboardMarker,marker);
            var originalClipboard=await ClipboardRestore.Capture();
            try
            {
                originalClipboard.Set(sentinel);
                var temporary=new Windows.ApplicationModel.DataTransfer.DataPackage(); temporary.SetText("temporary paste fixture"); temporary.SetData(clipboardMarker,marker);
                await using(var lease=await ClipboardRestore.Capture())lease.Set(temporary);
                Check(await Windows.ApplicationModel.DataTransfer.Clipboard.GetContent().GetTextAsync()=="AIEnter clipboard restore fixture","Paste did not restore clipboard text.");
                var clipboardLocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseClipboard = Task.Run(() =>
                {
                    bool opened = OpenClipboard(IntPtr.Zero);
                    clipboardLocked.SetResult(opened);
                    if (!opened) return;
                    try { Thread.Sleep(300); }
                    finally { CloseClipboard(); }
                });
                try
                {
                    Check(await clipboardLocked.Task, "Could not simulate clipboard contention.");
                    var preparation = ClipboardRestore.PreparePaste(temporary);
                    await using var lease = await preparation;
                    Check(await Windows.ApplicationModel.DataTransfer.Clipboard.GetContent().GetTextAsync()=="temporary paste fixture",
                        "Paste did not recover after brief clipboard contention.");
                }
                finally { await releaseClipboard; }
                Check(await Windows.ApplicationModel.DataTransfer.Clipboard.GetContent().GetTextAsync()=="AIEnter clipboard restore fixture",
                    "Paste did not restore the clipboard after contention.");
                var replacement=new Windows.ApplicationModel.DataTransfer.DataPackage(); replacement.SetText("newly copied fixture"); replacement.SetData(clipboardMarker,marker);
                await using(var lease=await ClipboardRestore.Capture())
                {
                    lease.Set(temporary); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(replacement);
                }
                Check(await Windows.ApplicationModel.DataTransfer.Clipboard.GetContent().GetTextAsync()=="newly copied fixture","Paste overwrote newer clipboard data.");
            }
            finally
            {
                var clipboard=Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if(clipboard.Contains(clipboardMarker)&&(await clipboard.GetDataAsync(clipboardMarker))?.ToString()==marker)originalClipboard.Set(sentinel);
                await originalClipboard.DisposeAsync();
            }
            var appearance=new UiAppearance(ScreenshotSeconds:1,ScreenshotFolder:folder);
            var copy=JsonSerializer.Deserialize<UiAppearance>(JsonSerializer.Serialize(appearance));
            Check(copy==appearance,"Screenshot settings did not round-trip.");
            bool rejected=false;
            try { (appearance with { ScreenshotFolder="relative-folder" }).Validate(); } catch(InvalidDataException) { rejected=true; }
            Check(rejected,"Relative screenshot directory was accepted.");
            aiLifetimeWindow = new Microsoft.UI.Xaml.Window { Title = "AIEnter 截图验证后台" };
            PanelTheme.Apply(UiStyle.Data); PreviewAppearance(appearance);
            preview=await ScreenshotPreview.Open(crop,path);
            Check(preview.IsOpen && preview.HasOutsideClickGuard && preview.SavedPath==path,"Screenshot preview/outside-click guard did not initialize.");
            await Task.Delay(1800);
            Check(!preview.IsOpen,"Idle screenshot preview did not expire.");
            Check(File.Exists(path),"Closing screenshot preview deleted local image.");
            var blockedHandoff = new TaskCompletionSource();
            int deliveries=0;
            preview=await ScreenshotPreview.Open(crop,path, (saved,target,display)=>{ deliveries++; Check(saved==path && target=="chatgpt","Screenshot handoff target changed."); return blockedHandoff.Task; });
            var transfer = preview.Send("chatgpt");
            Check(!preview.IsOpen && !transfer.IsCompleted,"Screenshot preview remained open while handoff was waiting.");
            await preview.Send("chatgpt"); Check(deliveries==1,"Screenshot preview submitted twice.");
            blockedHandoff.SetResult(); await transfer;
            Check(File.Exists(path),"Handoff deleted saved screenshot.");

            File.WriteAllText(Path.Combine(DataDirectory,"screenshot-result.json"),JsonSerializer.Serialize(new {passed=true, syntheticPixelsOnly=true, negativeOriginCrop=true, pixelAccurate=true, pngSaved=true, uniqueFiles=true, clipboardRestore=true, clipboardContention=true, newerClipboardPreserved=true, settingsRoundTrip=true, invalidPathRejected=true, outsideClickGuard=true, idleTimeout=true, closePreservesFile=true, immediateHandoffDismissal=true, blockedHandoffDoesNotTrapUi=true, noDuplicateHandoff=true, noFilesUploaded=true}));
        }
        catch(Exception error)
        {
            Environment.ExitCode=1; Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(Path.Combine(DataDirectory,"screenshot-result.json"),JsonSerializer.Serialize(new {passed=false,error=error.ToString()}));
        }
        finally { preview?.Destroy(); PreviewAppearance(null); StopAi(); aiLifetimeWindow?.Close(); Exit(); }
    }

    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
}
