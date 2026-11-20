using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AIEnter;

internal sealed partial class AiWindow
{
    async Task VerifyDropInputs()
    {
        var saved = options.Data;
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        async Task<string?> Text() => JsonSerializer.Deserialize<string>(await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('draft').value"));
        async Task Ready()
        {
            await ShowSelected();
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (current!.View.CoreWebView2 is { } core && await core.ExecuteScriptAsync("!!document.getElementById('draft')") == "true") return;
                await Task.Delay(50);
            }
            throw new Exception("Drop fixture did not load.");
        }
        string area = Path.Combine(directory, "drop-fixtures"); Directory.CreateDirectory(area);
        string first = Path.Combine(area, "first.txt"), second = Path.Combine(area, "second.txt");
        await File.WriteAllTextAsync(first, "local attachment one"); await File.WriteAllTextAsync(second, "local attachment two");
        try
        {
            options.Save(options.Data with { Provider = "chatgpt", DefaultProvider = "deepseek" }); await Ready();
            Check(((FrameworkElement)Content).AllowDrop && current!.View.AllowDrop, "Native content or WebView drop routing is missing.");
            await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('draft').value='existing draft';window.sent=0;let f=document.getElementById('attachments');if(!f){f=document.createElement('input');f.type='file';f.multiple=true;f.id='attachments';document.body.append(f);}const b=document.createElement('button');b.textContent='Send';b.onclick=()=>window.sent++;document.body.append(b);");
            await current!.View.CoreWebView2.ExecuteScriptAsync("const input=document.getElementById('attachments');input.setAttribute('aria-hidden','true');input.addEventListener('change',()=>{let row=document.getElementById('fixture-receipt');if(!row){row=document.createElement('div');row.id='fixture-receipt';document.body.append(row);}row.replaceChildren(...[...input.files].map(f=>{const span=document.createElement('span');span.textContent=f.name;return span;}));});");
            await current!.View.CoreWebView2.ExecuteScriptAsync("window.nativeDropEvents=[];for(const t of ['paste'])document.addEventListener(t,e=>window.nativeDropEvents.push({type:t,trusted:e.isTrusted,files:e.clipboardData?.files?.length}));window.nativeDropCount=0;document.addEventListener('dragover',e=>e.preventDefault());document.addEventListener('paste',e=>{e.preventDefault();window.nativeDropTrusted=e.isTrusted;if(window.fixtureDropReject)return;window.nativeDropCount++;const input=document.getElementById('attachments');if(input){input.files=e.clipboardData.files;input.dispatchEvent(new Event('change',{bubbles:true}));}else{let row=document.getElementById('fixture-receipt');if(!row){row=document.createElement('div');row.id='fixture-receipt';document.body.append(row);}row.replaceChildren(...[...e.clipboardData.files].map(f=>{const span=document.createElement('span');span.textContent=f.name;return span;}));}});");
            var text = new DataPackage(); text.SetText("dropped text");
            Check(await ReceiveDrop(text.GetView()), "Text drop was rejected.");
            Check(await Text() == "existing draft\ndropped text", "Dropped text overwrote or failed to append the draft.");
            Check(Status.Contains("文字或链接已放入") && Status.Contains("尚未发送"), "Text receive did not report draft-only status.");
            ToggleDetached(); Check(Detached, "Drop protection prevented mode switching."); await Task.Delay(150);
            var link = new DataPackage(); link.SetWebLink(new Uri("https://example.invalid/link"));
            Check(await ReceiveDrop(link.GetView()) && (await Text())!.EndsWith("https://example.invalid/link"), "Expanded mode failed to receive links: " + await Text() + " | " + Status);
            Check(Status.Contains("文字或链接已放入"), "Link receive did not report successful placement.");
            var size = AppWindow.Size; AppWindow.Resize(new(size.Width - 40, size.Height - 20)); await Task.Delay(150);
            var files = new DataPackage(); files.SetStorageItems([await StorageFile.GetFileFromPathAsync(first), await StorageFile.GetFileFromPathAsync(second)]);
            await current!.View.CoreWebView2.ExecuteScriptAsync("window.dragOverlay=true;document.addEventListener('dragleave',()=>window.dragOverlay=false);document.addEventListener('dragend',()=>window.dragOverlay=false);");
            Check(await ReceiveDrop(files.GetView()), "Files were rejected after resize.");
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('attachments').files.length") == "2", "Files did not reach the page input. Status=" + Status + "; native=" + await current!.View.CoreWebView2.ExecuteScriptAsync("JSON.stringify({count:window.nativeDropCount,trusted:window.nativeDropTrusted,events:window.nativeDropEvents,viewport:[innerWidth,innerHeight,devicePixelRatio]})"));
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync("window.dragOverlay") == "false", "Page drag overlay lifecycle did not end after native delivery.");
            Check(Status.Contains("文件已加入网页附件") && Status.Contains("尚未发送"), "File receive did not report attachment-only status: " + Status + "; receipt=" + await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('fixture-receipt')?.textContent"));
            AppWindow.Resize(size); ToggleDetached(); Check(!Detached, "Drop protection prevented returning to floating mode."); await Task.Delay(150);
            const int imageWidth = 64, imageHeight = 32;
            var imagePixels = new byte[imageWidth * imageHeight * 4];
            for (int i = 0; i < imagePixels.Length; i += 4)
            {
                imagePixels[i] = 180;
                imagePixels[i + 3] = 255;
            }
            string image = await new ScreenshotFrame(0, 0, imageWidth, imageHeight, imagePixels).Save(area);
            async Task CheckBitmapAttachment(string source)
            {
                string attachment = await current!.View.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify([...document.getElementById('attachments').files].map(f=>[f.name,f.type,f.size]))");
                string received = await current.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                    """{"expression":"(async()=>{const files=document.getElementById('attachments').files;if(files.length!==1||files[0].type!=='image/png')return [];const image=await createImageBitmap(files[0]);try{return [image.width,image.height];}finally{image.close();}})()","awaitPromise":true,"returnByValue":true}""");
                using var result = JsonDocument.Parse(received);
                bool decoded = result.RootElement.GetProperty("result").TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2
                    && value[0].GetInt32() == imageWidth && value[1].GetInt32() == imageHeight;
                Check(decoded && !HasPendingDrop && Status.Contains("文件已加入网页附件"),
                    source + " did not become a confirmed PNG attachment. Status=" + Status + "; files=" + attachment);
            }
            var bitmap = new DataPackage(); bitmap.SetBitmap(RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(image)));
            Check(await ReceiveDrop(bitmap.GetView()), "Bitmap drop was rejected.");
            await CheckBitmapAttachment("Window bitmap");
            // Compare a normal file drop and screenshot's StorageItems bridge
            // using the identical PNG. No website or real screenshot is uploaded.
            await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('fixture-receipt').replaceChildren();document.getElementById('attachments').value=''");
            var directImage = new DataPackage(); directImage.SetStorageItems([await StorageFile.GetFileFromPathAsync(image)]);
            Check(await ReceiveDrop(directImage.GetView()) && !HasPendingDrop,"Direct PNG drop did not complete.");
            string directReceipt = await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('attachments').files[0].name");
            await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('fixture-receipt').replaceChildren();document.getElementById('attachments').value=''");
            var screenshotView = await DropPayload.FilesView([image]);
            Check(screenshotView.Contains(StandardDataFormats.StorageItems),"Screenshot was not represented as a regular file drop.");
            Check(await ReceiveDrop(screenshotView) && !HasPendingDrop,"Screenshot PNG bridge did not complete through ReceiveDrop.");
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('attachments').files[0].name") == directReceipt,"Screenshot and direct drop attached different files.");
            Check(File.Exists(image),"Shared file drop deleted the saved PNG.");
            string queue = Path.Combine(directory, "incoming-drops"); Directory.CreateDirectory(queue);
            string packetFile = Path.Combine(queue, "local-test.json");
            await File.WriteAllTextAsync(packetFile, JsonSerializer.Serialize(new TaskbarDropPacket(Provider: "chatgpt", Text: "taskbar text", Files: [first])));
            var packet = await DropPayload.ReadPacket(packetFile, queue);
            Check(await ReceivePayload(packet.Payload, packet.Provider) && options.Data.Provider == "chatgpt", "Taskbar route changed current AI to the default.");
            Check((await Text())!.EndsWith("taskbar text"), "Taskbar text did not reach the current AI.");
            File.Delete(packetFile);
            string pictureBatch = Path.Combine(queue, "picture-test"); Directory.CreateDirectory(pictureBatch);
            string pictureSource = Path.Combine(pictureBatch, "image.source"); File.Copy(image, pictureSource, true);
            string picturePacket = Path.Combine(queue, "picture-test.json");
            await File.WriteAllTextAsync(picturePacket, JsonSerializer.Serialize(new TaskbarDropPacket(Provider: "chatgpt", Bitmap: pictureSource, OwnedFiles: [pictureSource])));
            var picture = await DropPayload.ReadPacket(picturePacket, queue);
            Check(await ReceivePayload(picture.Payload, picture.Provider), "Taskbar bitmap packet was rejected.");
            await CheckBitmapAttachment("Taskbar bitmap packet");
            File.Delete(picturePacket);
            await current!.View.CoreWebView2.ExecuteScriptAsync("const doc=document.createElement('input');doc.type='file';doc.multiple=true;doc.accept='application/vnd.openxmlformats-officedocument.wordprocessingml.document';doc.id='word-files';document.body.append(doc)");
            Check(JsonSerializer.Deserialize<string>(await current!.View.CoreWebView2.ExecuteScriptAsync("(" + DropWebScripts.FileInput(["test.docx"]) + ")?.id")) == "word-files", "Typed attachment input selection failed.");
            await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('word-files').remove()");
            await current!.View.CoreWebView2.ExecuteScriptAsync("const broad=document.createElement('input');broad.type='file';broad.multiple=true;broad.accept='application/*';broad.id='broad-files';document.body.append(broad)");
            Check(JsonSerializer.Deserialize<string>(await current!.View.CoreWebView2.ExecuteScriptAsync("(" + DropWebScripts.FileInput(["test.pdf"]) + ")?.id")) == "broad-files", "MIME wildcard attachment input was not recognized.");
            await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('broad-files').remove()");
            await current!.View.CoreWebView2.ExecuteScriptAsync("const near=document.createElement('section');const ed=document.getElementById('draft');ed.before(near);near.append(ed);const local=document.createElement('input');local.type='file';local.multiple=true;local.id='composer-files';near.append(local);");
            Check(JsonSerializer.Deserialize<string>(await current!.View.CoreWebView2.ExecuteScriptAsync("(" + DropWebScripts.FileInput(["fixture.doc"]) + ")?.id")) == "composer-files", "Composer-local file input was not preferred over unrelated upload inputs.");
            await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('composer-files').remove()");
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync(DropWebScripts.AttachmentReceipt(["receipt-test.doc"])) == "false", "Missing UI attachment was reported received.");
            await current!.View.CoreWebView2.ExecuteScriptAsync("const receipt=document.createElement('div');receipt.id='receipt-test';receipt.textContent='receipt-test.doc';document.body.append(receipt)");
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync(DropWebScripts.AttachmentReceipt(["receipt-test.doc"])) == "true", "Visible attachment receipt was not recognized.");
            await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('receipt-test').textContent='receipt-test.doc 上传失败'");
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync(DropWebScripts.AttachmentReceipt(["receipt-test.doc"])) == "false", "Failed upload was reported received.");
            await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('receipt-test').remove()");
            await current!.View.CoreWebView2.ExecuteScriptAsync("const thumb=document.createElement('img');thumb.id='thumbnail-only';thumb.src='data:image/svg+xml,'+encodeURIComponent('<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"32\"><rect width=\"64\" height=\"32\" fill=\"cyan\"/></svg>');document.body.append(thumb)");
            await Task.Delay(150);
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync(DropWebScripts.AttachmentReceipt(["thumbnail-only.png"], [], [2d])) == "true", "Image-only attachment receipt was not recognized.");
            var snapshot=JsonSerializer.Deserialize<string[]>(await current!.View.CoreWebView2.ExecuteScriptAsync(DropWebScripts.AttachmentSnapshot))!;
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync(DropWebScripts.AttachmentReceipt(["thumbnail-only.png"], snapshot, [2d])) == "false", "Existing page image was misreported as new attachment.");
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync(DropWebScripts.AttachmentReceipt(["thumbnail-only.png"], [], [3d])) == "false", "Wrong-aspect page image was misreported as attachment.");
            await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('thumbnail-only').remove()");
            string delayedFile=Path.Combine(area,"delayed-receipt.png"); File.Copy(image,delayedFile,true);
            await current!.View.CoreWebView2.ExecuteScriptAsync("window.delayedAttachCount=0;document.getElementById('attachments').addEventListener('change',()=>{window.delayedAttachCount++;const row=document.getElementById('fixture-receipt');row.replaceChildren();setTimeout(()=>{const span=document.createElement('span');span.textContent='delayed-receipt.png';row.replaceChildren(span);},5500)},{once:true});");
            var delayed=new DataPackage(); delayed.SetStorageItems([await StorageFile.GetFileFromPathAsync(delayedFile)]);
            Check(await ReceiveDrop(delayed.GetView()) && pendingDrop!=null && unconfirmedAttachments.Length==1,"Unconfirmed receipt was discarded or falsely marked complete.");
            for(int attempt=0;attempt<20&&pendingDrop!=null;attempt++) await Task.Delay(300);
            Check(pendingDrop==null && dropInbox.Visibility==Visibility.Collapsed,"Late attachment receipt did not clear stale pending UI.");
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync("window.delayedAttachCount")=="1","Receipt polling attached files again.");
            string before = (await Text())!;
            bool rejected = false;
            try { await ReceivePayload(new DropPayload { Text = "wrong target" }, "deepseek"); } catch (IOException) { rejected = true; }
            Check(rejected && await Text() == before, "Drop crossed AI targets after selection changed.");
            await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('attachments').remove();window.fixtureDropReject=true");
            var unsupported = new DataPackage(); unsupported.SetStorageItems([await StorageFile.GetFileFromPathAsync(first)]);
            Check(await ReceiveDrop(unsupported.GetView()) && pendingDrop != null && dropInbox.Visibility == Visibility.Visible, "Unsupported attachment lost retry content.");
            ClearDropInbox();
            await current!.View.CoreWebView2.ExecuteScriptAsync("window.fixtureDropReject=false;document.getElementById('fixture-receipt').replaceChildren()");
            string nativeFile=Path.Combine(area,"native-no-input.txt"); await File.WriteAllTextAsync(nativeFile,"Native drag fixture");
            Check(await ReceiveDrop(await DropPayload.FilesView([nativeFile])) && !HasPendingDrop,"Native paste failed without a file input: " + Status + "; receipt=" + await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('fixture-receipt')?.textContent"));
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('input[type=file]').length")=="0","Native drag unexpectedly required a file input.");
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync("window.nativeDropTrusted")=="true","Paste was not dispatched by the browser.");
            Check(await current!.View.CoreWebView2.ExecuteScriptAsync("window.sent") == "0", "Drop submitted an AI message.");
        }
        finally { ClearDropInbox(); options.Save(saved); await ShowSelected(); }
    }
}
