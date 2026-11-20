using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Halo;

public partial class App
{
    async Task VerifyContentTypes(Action<string, bool> check)
    {
        string directory = Path.Combine(DataDirectory, "content-check"); Directory.CreateDirectory(directory);
        var store = new StationStore(Path.Combine(directory, "station.json"));
        var html = new DataPackage(); html.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<h2>标题</h2><p>第一段 <strong>粗体</strong></p><ul><li>项目一</li><li><a href='/page'>项目二</a></li></ul><script>alert(1)</script><img src='https://example.com/pixel'>"));
        html.Properties.ContentSourceWebLink = new Uri("https://example.com/source");
        check("HtmlOnlyPackageAccepted", StationTransfer.Supported(html.GetView()));
        StationInput excerpt;
        using (var import = await StationTransfer.Read(html.GetView(), store.AttachmentDirectory)) { excerpt = import.Inputs.Single(); }
        check("HtmlExcerptKeepsStructureAndSource", excerpt.TextStyle == StationTextStyle.Excerpt && excerpt.SourceUrl == "https://example.com/source" && excerpt.Value.Contains("## 标题") && excerpt.Value.Contains("**粗体**") && excerpt.Value.Contains("- 项目一") && excerpt.Value.Contains("https://example.com/page"));
        check("HtmlExcerptDropsActiveAndRemoteContent", !excerpt.Value.Contains("alert(1)") && !excerpt.Value.Contains("pixel") && !MarkdownView.ToHtml(excerpt.Value).Contains("<script"));
        string htmlPacket = Path.Combine(DataDirectory, "incoming-drops", Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(htmlPacket, JsonSerializer.Serialize(new TaskbarPacket(Version: 2, Html: HtmlFormatHelper.CreateHtmlFormat("<p>任务栏网页摘录</p>"), SourceUrl: "https://example.com/taskbar")));
        var packetResult = await ImportPacket(htmlPacket); File.Delete(htmlPacket);
        check("TaskbarHtmlPacketPreservesExcerptAndSource", packetResult.Result.Added == 1 && Station.Items.Last() is { TextStyle: StationTextStyle.Excerpt, SourceUrl: "https://example.com/taskbar" });
        var link = new DataPackage(); link.SetWebLink(new Uri("https://example.com/target")); link.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<a href='https://example.com/target'>标题</a>"));
        using (var import = await StationTransfer.Read(link.GetView(), store.AttachmentDirectory)) check("BrowserLinkRemainsLink", import.Inputs.Single().Kind == StationKind.Link);
        var fallback = new DataPackage(); fallback.SetHtmlFormat("broken header"); fallback.SetText("纯文字回退");
        using (var import = await StationTransfer.Read(fallback.GetView(), store.AttachmentDirectory)) check("BadOptionalHtmlFallsBackToText", import.Inputs.Single().Value == "纯文字回退");
        check("MultiUrlDetectionExcludesMixedText", RichContent.LinkLines("https://example.com/a\nhttps://example.com/b")?.Length == 2 && RichContent.LinkLines("说明\nhttps://example.com/b") == null && RichContent.LinkLines("javascript:alert(1)\nhttps://example.com/b") == null);
        var code = new StationInput(StationKind.Text, "print('Halo')", "示例", TextStyle: StationTextStyle.Code, Language: "python");
        var markdown = new StationInput(StationKind.Text, "# 文档\n\n- 项目\n\n[链接](https://example.com)", TextStyle: StationTextStyle.Markdown);
        await store.ImportCopies([code, markdown, excerpt]);
        var note = store.Items.First(); var ids = store.Items.Select(item => item.Id).ToArray();
        await store.MoveGrid([note.Id], note.Id, 2, 2, false, 4, true);
        await store.UpdateNote(note.Id, "print('更新')", StationTextStyle.Code, "https://example.com/code", "python", "更新示例");
        check("NoteEditPreservesIdentityAndLayout", store.Items.First().Id == note.Id && store.Layout(false, 4, true).Cells.Single(cell => cell.Id == note.Id) is { Column: 2, Row: 2 });
        var reloaded = new StationStore(Path.Combine(directory, "station.json"));
        check("NoteMetadataPersists", reloaded.Items.First() is { TextStyle: StationTextStyle.Code, Language: "python", SourceUrl: "https://example.com/code", Title: "更新示例" });
        var copied = new DataPackage(); await StationTransfer.Fill(copied, reloaded.Items.First());
        using (var import = await StationTransfer.Read(copied.GetView(), store.AttachmentDirectory)) check("NoteCopyPasteRetainsModeAndSource", import.Inputs.Single() is { TextStyle: StationTextStyle.Code, Language: "python", SourceUrl: "https://example.com/code" });
        var copiedMarkdown = new DataPackage(); await StationTransfer.Fill(copiedMarkdown, reloaded.Items[1]);
        check("MarkdownOffersPlainAndHtml", copiedMarkdown.GetView().Contains(StandardDataFormats.Text) && copiedMarkdown.GetView().Contains(StandardDataFormats.Html));
        var export = new DataPackage(); await StationTransfer.FillMany(export, reloaded.Items.Take(2).ToArray(), store.AttachmentDirectory);
        var exported = await export.GetView().GetStorageItemsAsync();
        check("CodeAndMarkdownExportCorrectExtensions", Path.GetExtension(exported[0].Name) == ".py" && Path.GetExtension(exported[1].Name) == ".md");
        var singleExport = new DataPackage(); await StationTransfer.FillMany(singleExport, [reloaded.Items.First()], store.AttachmentDirectory, exportNoteFiles: true);
        check("SingleCodeDragProducesSourceFile", (await singleExport.GetView().GetStorageItemsAsync()).Single().Name.EndsWith(".py", StringComparison.Ordinal));
        var removed = store.Items.Last(); await store.RemoveArchived([removed]);
        string archive = Path.Combine(directory, "content.zip"); await store.ExportDesktop(archive);
        var restored = new StationStore(Path.Combine(directory, "restored", "station.json")); await restored.RestoreDesktop(archive);
        check("ContentBackupRetainsModesAndTrashSources", restored.Items.First().Language == "python" && restored.Trash.Single().SourceUrl == excerpt.SourceUrl && restored.Trash.Single().TextStyle == StationTextStyle.Excerpt);
        await restored.Restore(restored.Trash.ToArray());
        check("RichContentRestoreKeepsIds", restored.Items.Select(item => item.Id).Order().SequenceEqual(ids.Order()));
        foreach (var invalid in new[] { code with { SourceUrl = "javascript:alert(1)" }, code with { Language = "py\nrun" }, code with { TextStyle = (StationTextStyle)100 } }) {
            bool rejected = false; try { await store.ImportCopies([invalid]); } catch (InvalidDataException) { rejected = true; }
            check("ContentMetadataRejects" + (int)invalid.TextStyle + "_" + (invalid.SourceUrl.Length > 0 ? "source" : "language"), rejected);
        }
        string legacy = Path.Combine(directory, "legacy.json"); await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(new StationDocument(3, [new(Guid.NewGuid(), StationKind.Text, "旧便笺", "", DateTimeOffset.Now)])));
        check("LegacyNotesRemainPlain", new StationStore(legacy).Items.Single().TextStyle == StationTextStyle.Plain);
        string shortcut = Path.Combine(directory, "copy.lnk"); await File.WriteAllBytesAsync(shortcut, [1, 2, 3, 4]);
        await store.ImportCopies([new(StationKind.File, shortcut)]);
        var shortcutPackage = new DataPackage(); await StationTransfer.Fill(shortcutPackage, store.Items.Last());
        var shortcutFile = (StorageFile)(await shortcutPackage.GetView().GetStorageItemsAsync()).Single(); using var stream = await shortcutFile.OpenReadAsync();
        check("ShortcutCanBeDeliveredWithoutResolvingTarget", stream.Size == 4);
        await Panel!.VerifyContentUI(check, directory);
    }
}

internal sealed partial class StationWindow
{
    internal async Task VerifyContentUI(Action<string, bool> check, string directory)
    {
        using var retain = Retain();
        check("PasteShortcutLeavesTextEditorsAlone", TextInputTarget(new TextBox()) && TextInputTarget(new RichEditBox()) && !TextInputTarget(files));
        var note = new StationItem(Guid.NewGuid(), StationKind.Text, "# 预览标题\n\n- 列表\n\n**强调**", "Markdown", DateTimeOffset.Now, true, StationTextStyle.Markdown, "https://example.com/source");
        var preview = PreviewItem(note); await Task.Delay(120);
        check("NativeMarkdownPreviewUsesLocalControls", activeContentDialog != null && Descendants<RichTextBlock>((DependencyObject)activeContentDialog.Content).Any() && Descendants<HyperlinkButton>((DependencyObject)activeContentDialog.Content).Single().NavigateUri.AbsoluteUri == note.SourceUrl);
        activeContentDialog!.Hide(); await preview; await Task.Delay(100);
        StationInput[] lines = [new(StationKind.Text, "https://example.com/a\nhttps://example.com/b")];
        var keep = ChooseLinkBatch(lines); await Task.Delay(100); InvokeDialog("保留整段文字");
        check("BatchLinksCanRemainOneNote", (await keep) is [{ Kind: StationKind.Text }]); await Task.Delay(100);
        var split = ChooseLinkBatch(lines); await Task.Delay(100); InvokeDialog("拆成独立链接");
        check("BatchLinksSplitOnlyAfterUserChoice", (await split) is { Length: 2 } links && links.All(input => input.Kind == StationKind.Link)); await Task.Delay(100);
        var cancel = ChooseLinkBatch(lines); await Task.Delay(100); activeContentDialog!.Hide(); check("BatchLinksCancelDoesNotImport", await cancel == null); await Task.Delay(100);
        int count = App.Station.Items.Count; var edit = EditNote(null, StationTextStyle.Code); await Task.Delay(100);
        var body = (StackPanel)((ScrollViewer)activeContentDialog!.Content).Content;
        body.Children.OfType<TextBox>().Single(input => Equals(input.Header, "内容")).Text = "print('native')";
        body.Children.OfType<TextBox>().Single(input => Equals(input.Header, "代码语言（例如 python、csharp、json）")).Text = "python";
        InvokeDialog("保存"); await edit;
        check("NativeCodeEditorSavesSelectedMode", App.Station.Items.Count == count + 1 && App.Station.Items.Last() is { TextStyle: StationTextStyle.Code, Language: "python", Value: "print('native')" });
        await Task.Delay(100); var codePreview = PreviewItem(App.Station.Items.Last()); await Task.Delay(100);
        check("CodePreviewUsesMonospace", Descendants<TextBlock>((DependencyObject)activeContentDialog!.Content).Any(text => text.Text == "print('native')" && text.FontFamily.Source == "Consolas"));
        activeContentDialog.Hide(); await codePreview;
        await VerifyFilePreviews(check, directory);
        void InvokeDialog(string label) {
            var button = Descendants<Button>(activeContentDialog!).Single(button => Equals(button.Content, label));
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
    }
    async Task VerifyFilePreviews(Action<string, bool> check, string directory)
    {
        async Task Show(string path, Func<ContentDialog, Task> verify) {
            var task = PreviewItem(new(Guid.NewGuid(), StationKind.File, path, Path.GetFileName(path), DateTimeOffset.Now, true));
            for (int i = 0; activeContentDialog == null && !task.IsCompleted && i < 100; i++) await Task.Delay(50);
            if (activeContentDialog == null) throw new Exception("文件预览未打开：" + Path.GetExtension(path));
            await Task.Delay(100);
            try { await verify(activeContentDialog); } finally { activeContentDialog?.Hide(); await task; }
            await Task.Delay(100);
        }
        string imagePath = Path.Combine(directory, "preview.png");
        File.Copy(Path.Combine(App.DataDirectory, "测试图片.png"), imagePath, true);
        await Show(imagePath, dialog => { check("NativeImageQuickPreview", Descendants<Image>((DependencyObject)dialog.Content).Any(image => image.Source != null)); return Task.CompletedTask; });
        string textPath = Path.Combine(directory, "preview.txt"); await File.WriteAllTextAsync(textPath, "本地文本预览");
        await Show(textPath, dialog => { check("NativeTextFileQuickPreview", Descendants<TextBlock>((DependencyObject)dialog.Content).Any(text => text.Text == "本地文本预览")); return Task.CompletedTask; });
        string pdfPath = Path.Combine(directory, "preview.pdf");
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        string PdfStream(string commands) => $"<< /Length {Encoding.ASCII.GetByteCount(commands)} >>\nstream\n{commands}endstream";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources <<>> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources <<>> /Contents 6 0 R >>",
            PdfStream("0 0 1 rg 20 20 160 160 re f\n"), PdfStream("1 0 0 rg 20 20 160 160 re f\n")];
        for (int i = 0; i < objects.Length; i++) { offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString())); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString()); pdf.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10") + " 00000 n \n");
        pdf.Append($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); await File.WriteAllTextAsync(pdfPath, pdf.ToString(), Encoding.ASCII);
        await Show(pdfPath, async dialog => {
            check("NativePdfFirstPagePreview", Descendants<Image>((DependencyObject)dialog.Content).Any(image => image.Source != null) && Descendants<TextBlock>((DependencyObject)dialog.Content).Any(text => text.Text == "1 / 2"));
            var next = Descendants<Button>((DependencyObject)dialog.Content).Single(button => Equals(button.Content, "下一页"));
            ((IInvokeProvider)new ButtonAutomationPeer(next).GetPattern(PatternInterface.Invoke)).Invoke();
            for (int i = 0; i < 40 && !Descendants<TextBlock>((DependencyObject)dialog.Content).Any(text => text.Text == "2 / 2"); i++) await Task.Delay(50);
            check("NativePdfPageNavigation", Descendants<TextBlock>((DependencyObject)dialog.Content).Any(text => text.Text == "2 / 2"));
        });
        string wavePath = Path.Combine(directory, "preview.wav");
        using (var file = File.Create(wavePath)) using (var wave = new BinaryWriter(file)) {
            wave.Write(Encoding.ASCII.GetBytes("RIFF")); wave.Write(16036); wave.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16); wave.Write((ushort)1); wave.Write((ushort)1);
            wave.Write(8000); wave.Write(16000); wave.Write((ushort)2); wave.Write((ushort)16); wave.Write(Encoding.ASCII.GetBytes("data")); wave.Write(16000); wave.Write(new byte[16000]);
        }
        await Show(wavePath, dialog => { check("NativeMediaPreviewDoesNotAutoplay", Descendants<MediaPlayerElement>((DependencyObject)dialog.Content).Any(media => media.MediaPlayer?.Source != null && !media.AutoPlay && media.AreTransportControlsEnabled && media.MediaPlayer.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing)); return Task.CompletedTask; });
    }
}
