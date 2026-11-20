using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifyImageTextSearch(Action<string, bool> check)
    {
        using var retain = Retain();
        var engines = ImageTextSearch.Engines();
        if (engines.Length == 0) return;
        var folder = App.Station.Items.Single(item => item.Kind == StationKind.Folder && DesktopName(item) == "定位验收");
        string directory = Path.Combine(folder.Value, "乙");
        var storage = await StorageFolder.GetFolderFromPathAsync(directory);
        var screenshot = await storage.CreateFileAsync("图像验收截图.png", CreationCollisionOption.FailIfExists);
        bool chinese = engines.Any(engine => engine.RecognizerLanguage.LanguageTag.StartsWith("zh-", StringComparison.OrdinalIgnoreCase));
        string phrase = chinese ? "连接失败" : "HALO SCREENSHOT";
        using var bitmap = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1000, 240, 96);
        using (var drawing = bitmap.CreateDrawingSession()) {
            drawing.Clear(Colors.White);
            using var format = new CanvasTextFormat { FontFamily = chinese ? "Microsoft YaHei UI" : "Segoe UI", FontSize = 80 };
            drawing.DrawText(phrase, 35, 55, Colors.Black, format);
        }
        using (var stream = await screenshot.OpenAsync(FileAccessMode.ReadWrite))
            await bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png);

        Open();
        ShowSearch();
        includeImageText.IsChecked = false;
        desktopSearch.Text = phrase;
        await RefreshSearch(debounce: false);
        check("ImageTextSearchRunsOnDemand", !searchResults.Items.Cast<ListViewItem>()
            .Any(row => ((SearchHit)row.Tag).Item.Value == screenshot.Path));
        includeImageText.IsChecked = true;
        await RefreshSearch(debounce: false);
        var hit = searchResults.Items.Cast<ListViewItem>().Select(row => (SearchHit)row.Tag)
            .SingleOrDefault(entry => entry.Item.Value == screenshot.Path);
        check("RealScreenshotTextFoundWithoutFilenameMatch", hit?.ImageMatch != null && hit.Location == "定位验收 › 乙");
        await LocateSearchResult(hit!);
        check("ImageTextResultLocatesScreenshot", folderBrowser?.CurrentDirectory == directory
            && folderBrowser.Files.SelectedItem is GridViewItem { Tag: StationItem item } && item.Value == screenshot.Path);
        CloseFolderBrowser();
        includeImageText.IsChecked = false;
    }

    internal void VerifyImportFeedback(Action<string, bool> check)
    {
        var bridge = App.RingBridge!;
        ShowPendingDropFailures();
        ReportImportFeedback();
        check("RingImportSuccessPublished", bridge.ReadImportFeedbackForVerification() is > 0 and < 0x80000000u);
        ReportImportFeedback([new("环形验收失败.png：文件不可访问", "verify")]);
        check("RingImportFailurePublished", (bridge.ReadImportFeedbackForVerification() & 0x80000000u) != 0);
        check("RingClickShowsAndAcknowledgesFailure", ShowPendingDropFailures() && status.Text.Contains("环形验收失败.png")
            && bridge.ReadImportFeedbackForVerification() == 0 && !ShowPendingDropFailures());
        ReportImportFeedback([new("重试验收", "retry")]);
        ResolveDropFailure("retry");
        ReportImportFeedback();
        check("ResolvedFailureAllowsNextSuccess", bridge.ReadImportFeedbackForVerification() is > 0 and < 0x80000000u && !ShowPendingDropFailures());
        bridge.PublishImportFeedback(null);
        ClearNotification();
    }
}
