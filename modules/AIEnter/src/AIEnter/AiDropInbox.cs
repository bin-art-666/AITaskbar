using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace AIEnter;

internal sealed partial class AiWindow
{
    readonly StackPanel dropInbox = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    Border? dropInboxCard;
    DropPayload? pendingDrop;
    string? pendingProvider, pendingEndpointKey, pendingTargetName;
    bool pendingFilesDelivered;
    string[] unconfirmedAttachments = [];
    bool receiptAlreadyVisible;
    string[] submittedFiles = [], beforeAttachmentImages = [];
    double[] attachmentRatios = [];
    Microsoft.UI.Dispatching.DispatcherQueueTimer? receiptTimer;
    bool checkingReceipt;
    int receiptAttempts;
    bool transferringDrop;
    internal bool HasPendingDrop => pendingDrop != null;
    void InitializeDropInbox(Grid host)
    {
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var inboxScroll = new ScrollViewer { Content = dropInbox, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        layout.SizeChanged += (_, _) => inboxScroll.MaxHeight = Math.Clamp(layout.ActualHeight * .25, 80, 160);
        dropInboxCard = new Border { Child = inboxScroll, Padding = new Thickness(10), CornerRadius = new CornerRadius(12), Visibility = Visibility.Collapsed,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 245, 248, 250)) };
        layout.Children.Add(dropInboxCard); Grid.SetRow(host, 1); layout.Children.Add(host);
        // Called before the host enters the visual tree.
        dropContent = layout;
        receiptTimer = DispatcherQueue.CreateTimer(); receiptTimer.Interval = TimeSpan.FromSeconds(1);
        receiptTimer.Tick += async (_, _) => await ObservePendingReceipt();
        Closed += (_, _) => { receiptTimer.Stop(); ClearDropInbox(); };
    }
    Grid? dropContent;
    internal async Task<bool> ReceiveDrop(DataPackageView view, string? intendedProvider = null)
    {
        if (transferringDrop || ModalActive) return false;
        using var lease = KeepOpenForDrag();
        string provider = intendedProvider ?? options.Data.Provider;
        var payload = await DropPayload.Read(view);
        return await ReceivePayload(payload, provider);
    }
    internal async Task<bool> ReceivePayload(DropPayload payload, string provider)
    {
        if (transferringDrop || ModalActive) { payload.Dispose(); return false; }
        using var lease = KeepOpen();
        bool retained = false;
        try
        {
            if (options.Data.Provider != provider) throw new IOException("接收期间 AI 已切换，请回到原 AI 后重新拖入。");
            if (payload.Bitmap != null)
            {
                string path = await ImageAttachments.Save(payload.Bitmap); payload.OwnFile(path); payload.Files = [path]; payload.Bitmap = null;
            }
            if (payload.Files.Length > 0 && !await Task.Run(() => payload.Files.All(File.Exists)).WaitAsync(TimeSpan.FromSeconds(5)))
                throw new IOException("文件已移动或无法访问，请检查原位置后重新拖入。");
            if (transferringDrop) return false;
            if (!IsOpen) ShowPanel();
            bool append = false;
            if (pendingDrop != null && pendingProvider == provider && pendingEndpointKey == AiPolicy.Resolve(options.Data).Key
                && payload.Text.Length == 0 && payload.Files.Length > 0
                && payload.Files.All(file => pendingDrop.Files.Concat(submittedFiles).Contains(file, StringComparer.OrdinalIgnoreCase)))
            {
                if (!transferringDrop) await TransferDrop();
                return true;
            }
            if (pendingDrop != null)
            {
                bool sameTarget = pendingProvider == options.Data.Provider && pendingEndpointKey == AiPolicy.Resolve(options.Data).Key;
                bool canAppend = sameTarget && unconfirmedAttachments.Length == 0 && pendingDrop.CanAppend(payload);
                var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "已有待发送内容",
                    Content = $"当前有 {pendingDrop.Files.Length} 个文件、{pendingDrop.Text.Length} 字待交给 {pendingTargetName}。\n新拖入 {payload.Files.Length} 个文件、{payload.Text.Length} 字。"
                        + (!sameTarget ? "\n服务已改变，只能替换或取消，原暂存不会交给新服务。" : !canAppend ? "\n合并超过 12 个文件或 20000 字，可替换或取消。" : "\n追加会保留两次内容，替换会清除原暂存。")
                        + (pendingFilesDelivered ? "\n已有附件已交给网站；替换或取消不能撤回它们。" : ""),
                    PrimaryButtonText = "追加", IsPrimaryButtonEnabled = canAppend, SecondaryButtonText = "替换", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
                var choice = await dialog.ShowAsync();
                if (choice == ContentDialogResult.None) return false;
                append = choice == ContentDialogResult.Primary;
            }
            StageDrop(payload, append); retained = true;
            dropInbox.Visibility = Visibility.Collapsed; if (dropInboxCard != null) dropInboxCard.Visibility = Visibility.Collapsed; if (dropContent != null) dropContent.RowSpacing = 0;
            ConfirmIncomingDrop();
            if (current?.Ready != null)
            {
                try { await current.Ready.WaitAsync(TimeSpan.FromSeconds(20)); }
                catch (TimeoutException) { RenderDropInbox(); SetStatus("网页尚未就绪，文件已保留，请稍后重试放入。"); return true; }
            }
            await TransferDrop(); return true;
        }
        finally { if (!retained) payload.Dispose(); }
    }
    void StageDrop(DropPayload payload, bool append)
    {
        if (append)
        {
            if (pendingDrop == null || pendingProvider != options.Data.Provider || pendingEndpointKey != AiPolicy.Resolve(options.Data).Key)
                throw new IOException("待发送目标已变化，请取消后重新拖入。");
            pendingDrop.Append(payload);
        }
        else
        {
            ClearDropInbox(); pendingDrop = payload; pendingProvider = options.Data.Provider;
            var endpoint = AiPolicy.Resolve(options.Data); pendingEndpointKey = endpoint.Key; pendingTargetName = endpoint.Name;
        }
    }
    void RenderDropInbox()
    {
        if (pendingDrop is not { } payload) return;
        dropInbox.Children.Clear();
        dropInbox.Visibility = Visibility.Visible; if (dropInboxCard != null) dropInboxCard.Visibility = Visibility.Visible;
        if (dropContent != null) dropContent.RowSpacing = 8;
        dropInbox.Children.Add(new TextBlock { Text = unconfirmedAttachments.Length > 0 ? "已交给网页，待核对" : "附件暂存", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        dropInbox.Children.Add(new TextBlock { Text = payload.Text.Length > 300 ? payload.Text[..300] + "…" : payload.Text, MaxHeight = 80, TextWrapping = TextWrapping.Wrap });
        foreach (string file in payload.Files.Concat(unconfirmedAttachments))
            dropInbox.Children.Add(new TextBlock { Text = Path.GetFileName(file), TextTrimming = TextTrimming.CharacterEllipsis });
        dropInbox.Children.Add(new TextBlock { Text = "目标：" + pendingTargetName, FontSize = 12, Opacity = .7 });
        dropInbox.Children.Add(new TextBlock { Text = unconfirmedAttachments.Length > 0 ? "已交给网页，但尚未确认收到。请检查输入框附件；重新检查不会重复投递。若网页未收到，可取消后用网页附件按钮上传。" : "文件尚未加入网页附件。请确认已登录并回到对话页；AIEnter 不会发送消息。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var confirm = new Button { Content = unconfirmedAttachments.Length > 0 ? "重新检查附件" : "重试放入 " + pendingTargetName };
        confirm.Click += async (_, _) => { if (transferringDrop) return; confirm.IsEnabled = false; await TransferDrop(); confirm.IsEnabled = true; }; actions.Children.Add(confirm);
        if (payload.Text.Length > 0)
        {
            var copy = new Button { Content = "复制文字／链接" };
            copy.Click += (_, _) => { var data = new DataPackage(); data.SetText(payload.Text); Clipboard.SetContent(data); SetStatus("已复制，可粘贴到当前 AI 输入框。"); };
            actions.Children.Add(copy);
        }
        var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => { if (!transferringDrop) ClearDropInbox(); }; actions.Children.Add(cancel); dropInbox.Children.Add(actions);
    }
    void ClearDropInbox()
    {
        receiptTimer?.Stop(); submittedFiles = []; beforeAttachmentImages = []; attachmentRatios = [];
        pendingDrop?.Dispose(); pendingDrop = null; pendingProvider = null; pendingEndpointKey = pendingTargetName = null;
        unconfirmedAttachments = []; receiptAlreadyVisible = false;
        pendingFilesDelivered = false; if (dropContent != null) dropContent.RowSpacing = 0;
        dropInbox.Children.Clear(); dropInbox.Visibility = Visibility.Collapsed; if (dropInboxCard != null) dropInboxCard.Visibility = Visibility.Collapsed;
    }
    async Task EndWebDrag()
    {
        if (current?.View.CoreWebView2 is not { } core) return;
        try { await core.ExecuteScriptAsync(DropWebScripts.EndDrag).AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    async Task ObservePendingReceipt()
    {
        if (checkingReceipt || transferringDrop || pendingDrop is not { } original || unconfirmedAttachments.Length == 0) return;
        if (++receiptAttempts > 120) { receiptTimer?.Stop(); return; }
        if (pendingProvider != options.Data.Provider || current == null || current.Endpoint.Key != pendingEndpointKey || current.View.CoreWebView2 is not { } core) return;
        checkingReceipt = true;
        try
        {
            bool received = await core.ExecuteScriptAsync(DropWebScripts.AttachmentReceipt(unconfirmedAttachments, beforeAttachmentImages, attachmentRatios, !receiptAlreadyVisible)).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == "true";
            if (received && ReferenceEquals(pendingDrop, original) && pendingProvider == options.Data.Provider)
            {
                unconfirmedAttachments = []; receiptTimer?.Stop();
                if (string.IsNullOrWhiteSpace(original.Text)) { ClearDropInbox(); SetStatus("网页已显示附件；请核对上传状态，尚未发送消息。"); }
                else await TransferDrop();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally { checkingReceipt = false; }
    }
    static async Task<double[]> ImageRatios(string[] files)
    {
        var ratios = new double[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            if (!new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" }.Contains(Path.GetExtension(files[i]).ToLowerInvariant())) continue;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(files[i]);
                using var stream = await file.OpenReadAsync();
                var image = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                ratios[i] = image.PixelHeight == 0 ? 0 : image.PixelWidth / (double)image.PixelHeight;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        return ratios;
    }
    async Task SubmitNativePaste(Microsoft.Web.WebView2.Core.CoreWebView2 core, DropPayload payload)
    {
        var files = payload.Files.ToArray();
        var names = files.Select(Path.GetFileName).ToArray();
        beforeAttachmentImages = JsonSerializer.Deserialize<string[]>(await core.ExecuteScriptAsync(DropWebScripts.AttachmentSnapshot).AsTask().WaitAsync(TimeSpan.FromSeconds(3))) ?? [];
        attachmentRatios = await ImageRatios(files).WaitAsync(TimeSpan.FromSeconds(5));
        receiptAlreadyVisible = await core.ExecuteScriptAsync(DropWebScripts.AttachmentReceipt(names!)).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == "true";
        current?.View.Focus(FocusState.Programmatic);
        await WebAttachmentPaste.Deliver(core, files, () =>
        {
            submittedFiles = files; payload.Commit(); payload.Files = []; pendingFilesDelivered = true;
            unconfirmedAttachments = names!; receiptAttempts = 0; receiptTimer?.Start();
        });
    }
    async Task TransferDrop()
    {
        if (pendingDrop is not { } payload || transferringDrop) return;
        bool includedText = !string.IsNullOrWhiteSpace(payload.Text);
        transferringDrop = true; using var lease = KeepOpen();
        try
        {
            if (pendingProvider != options.Data.Provider || pendingEndpointKey != AiPolicy.Resolve(options.Data).Key || current?.View.CoreWebView2 is not { } core || current.Endpoint.Key != pendingEndpointKey || popup != null || current.Failed)
                throw new IOException("当前服务已变化或网页未就绪，请回到原服务，或取消后重新拖入。");
            if (!verification && (!Uri.TryCreate(core.Source, UriKind.Absolute, out var source) || source.Scheme != "https" || source.GetLeftPart(UriPartial.Authority) != new Uri(current.Endpoint.Url).GetLeftPart(UriPartial.Authority)))
                throw new IOException("请先完成登录并回到当前 AI 的对话页面，再确认。");
            if (payload.Files.Length > 0)
            {
                if (payload.Files.Any(p => !File.Exists(p))) throw new IOException("附件原位置已失效，请重新拖入。");
                await EndWebDrag();
                SetStatus("正在加入网页附件…");
                if (pendingProvider is "chatgpt" or "doubao")
                {
                    SetStatus("正在通过网页粘贴加入附件…");
                    await SubmitNativePaste(core, payload);
                }
                else
                {
                // Some sites mount file inputs only after the explicit attachment control opens.
                string raw = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = DropWebScripts.FileInput(payload.Files), returnByValue = false })).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                using var doc = JsonDocument.Parse(raw); var node = doc.RootElement.GetProperty("result");
                if (!node.TryGetProperty("objectId", out var objectId) || node.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "null")
                    throw new IOException("网页附件入口未就绪或存在多个入口。请打开网页的附件区域；如仍无法确定，请用网页附件按钮手动选择，暂存内容仍保留。");
                // The user explicitly dropped these files into this AI; attach without submitting a message.
                try
                {
                    var names = payload.Files.Select(p => Path.GetFileName(p)).ToArray();
                    bool checkReceipt = pendingProvider is "doubao" or "chatgpt";
                    beforeAttachmentImages = JsonSerializer.Deserialize<string[]>(await core.ExecuteScriptAsync(DropWebScripts.AttachmentSnapshot).AsTask().WaitAsync(TimeSpan.FromSeconds(3))) ?? [];
                    attachmentRatios = await ImageRatios(payload.Files).WaitAsync(TimeSpan.FromSeconds(5));
                    if (checkReceipt) receiptAlreadyVisible = await core.ExecuteScriptAsync(DropWebScripts.AttachmentReceipt(names)).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == "true";
                    var files = payload.Files.ToArray();
                    try { await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", JsonSerializer.Serialize(new { files, objectId = objectId.GetString() })).AsTask().WaitAsync(TimeSpan.FromSeconds(8)); }
                    catch (TimeoutException)
                    {
                        // The site may still complete the side effect. Record uncertain delivery
                        // before returning so a retry cannot attach the same files twice.
                        submittedFiles = files; payload.Commit(); payload.Files = []; pendingFilesDelivered = true; unconfirmedAttachments = names;
                        receiptAttempts = 0; receiptTimer?.Start();
                        throw new IOException("网页未及时返回接收结果；请核对附件，重新检查不会重复上传。");
                    }
                    submittedFiles = files; payload.Commit(); payload.Files = []; pendingFilesDelivered = true;
                    if (checkReceipt) unconfirmedAttachments = names;
                }
                finally { try { await core.CallDevToolsProtocolMethodAsync("Runtime.releaseObject", JsonSerializer.Serialize(new { objectId = objectId.GetString() })).AsTask().WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
                }
            }
            if (unconfirmedAttachments.Length > 0)
            {
                SetStatus("已交给网页，正在核对附件显示…");
                bool confirmed = false;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    if (await core.ExecuteScriptAsync(DropWebScripts.AttachmentReceipt(unconfirmedAttachments, beforeAttachmentImages, attachmentRatios, !receiptAlreadyVisible)).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == "true") { confirmed = true; break; }
                    await Task.Delay(500);
                }
                if (!confirmed)
                {
                    receiptAttempts = 0; receiptTimer?.Start(); RenderDropInbox();
                    SetStatus("附件已交给网页，正在等待显示回执；不代表投递失败，请核对网页附件。"); return;
                }
                unconfirmedAttachments = [];
            }
            if (!string.IsNullOrWhiteSpace(payload.Text))
            {
                const string focus = DropWebScripts.FocusEditor;
                using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = focus, returnByValue = true })).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                if (!result.RootElement.GetProperty("result").TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String) throw new IOException("网页输入框未就绪；请打开对话输入框后重试，文字仍保留。");
                await core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = (value.GetString() == "newline" && !payload.Text.StartsWith("\n") ? "\n" : "") + payload.Text })).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); payload.Text = "";
            }
            bool hadFiles = pendingFilesDelivered;
            bool doubaoReceipt = pendingProvider == "doubao" && hadFiles;
            ClearDropInbox(); SetStatus(doubaoReceipt ? "网页已显示附件，请核对上传状态；尚未发送消息。" : hadFiles
                ? includedText ? "文件已加入网页附件，文字已放入草稿；尚未发送消息。" : "文件已加入网页附件；尚未发送消息。"
                : "文字或链接已放入当前 AI 草稿；尚未发送消息。");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RenderDropInbox();
            string reason = string.IsNullOrWhiteSpace(ex.Message) ? $"系统操作失败（0x{ex.HResult:X8}）。" : ex.Message;
            SetStatus((pendingFilesDelivered ? "附件已交给网站；剩余步骤未完成：" : "未完成：") + reason);
        }
        finally { await EndWebDrag(); transferringDrop = false; }
    }
}
