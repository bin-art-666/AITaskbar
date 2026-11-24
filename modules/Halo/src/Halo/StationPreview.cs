using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Halo;

internal sealed partial class StationWindow
{
    static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico" };
    static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".markdown", ".cs", ".py", ".js", ".ts", ".json", ".xml", ".html", ".css", ".sql", ".log", ".ini", ".yaml", ".yml", ".csv", ".url", ".ps1", ".sh" };
    static readonly HashSet<string> MediaTypes = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mp3", ".wav", ".m4a", ".wma", ".flac", ".ogg" };
    static bool HasFileContentPreview(string path)
    {
        string extension = Path.GetExtension(path);
        return ImageTypes.Contains(extension) || TextTypes.Contains(extension) || MediaTypes.Contains(extension)
            || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    internal async Task PreviewItem(StationItem item, Func<Task>? open = null, GridView? grid = null, StationItem? folderRoot = null)
    {
        if (!item.Owned && item.Kind is StationKind.File or StationKind.Folder) {
            if (await ShowPanelDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = "预览原文件引用？", Content = "这项内容尚未复制到 Halo，本次将读取原文件。", PrimaryButtonText = "预览原文件", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }) != ContentDialogResult.Primary) return;
        }
        if (item.Kind == StationKind.Folder) {
            await PreviewFolder(item, folderRoot ?? item, grid ?? files, open ?? (() => OpenItem(item)));
            return;
        }
        var content = new StackPanel { Spacing = 10 };
        MediaPlayerElement? media = null;
        FileStream? pdfSource = null; IRandomAccessStream? pdfStream = null;
        string title = item.Kind switch {
            StationKind.File => Path.GetFileName(item.Value),
            StationKind.Text when item.Title.Length == 0 => "便笺",
            _ => DesktopName(item),
        };
        var heading = PanelTheme.Title(title);
        heading.TextWrapping = TextWrapping.Wrap;
        heading.TextTrimming = TextTrimming.None;
        var dialog = new ContentDialog {
            Title = heading, Content = new ScrollViewer {
                Content = content, MaxHeight = 430, HorizontalScrollMode = ScrollMode.Disabled, IsTabStop = false,
            },
            CloseButtonText = "关闭",
            PrimaryButtonText = item.Kind is StationKind.File or StationKind.Link ? "用默认应用打开" : "",
        };
        AutomationProperties.SetName(dialog, $"快速预览 · {title}");
        try {
            if (item.Kind == StationKind.Text) {
                content.Children.Add(new TextBlock { Text = RichContent.StyleName(item.TextStyle) + (item.Language.Length > 0 ? " · " + item.Language : ""), Opacity = .65 });
                content.Children.Add(item.TextStyle == StationTextStyle.Plain ? new TextBlock { Text = item.Value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap } : MarkdownView.Render(item.Value, item.TextStyle == StationTextStyle.Code));
                if (item.SourceUrl.Length > 0) content.Children.Add(new HyperlinkButton { Content = "来源：" + item.SourceUrl, NavigateUri = new Uri(item.SourceUrl) });
            } else if (item.Kind == StationKind.Link) content.Children.Add(new TextBlock { Text = item.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            else {
                string extension = Path.GetExtension(item.Value);
                if (RichContent.IsShortcut(item.Value)) {
                    content.Children.Add(new TextBlock { Text = $"快捷方式（{extension}）\n{item.Value}",
                        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                } else if (ImageTypes.Contains(extension)) {
                    var image = new BitmapImage { DecodePixelWidth = 1400 }; using var stream = File.OpenRead(item.Value); await image.SetSourceAsync(stream.AsRandomAccessStream());
                    content.Children.Add(new Image { Source = image, MaxHeight = 350, Stretch = Stretch.Uniform });
                } else if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) {
                    pdfSource = new FileStream(item.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    pdfStream = pdfSource.AsRandomAccessStream();
                    var document = await PdfDocument.LoadFromStreamAsync(pdfStream);
                    var image = new Image { MaxHeight = 340, Stretch = Stretch.Uniform }; uint index = 0;
                    var previous = new Button { Content = "上一页" }; var next = new Button { Content = "下一页" }; var page = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                    var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; navigation.Children.Add(previous); navigation.Children.Add(page); navigation.Children.Add(next);
                    async Task Render() {
                        previous.IsEnabled = next.IsEnabled = false;
                        try { using var pdfPage = document.GetPage(index); using var stream = new InMemoryRandomAccessStream(); await pdfPage.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1200 });
                            stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); image.Source = bitmap; page.Text = $"{index + 1} / {document.PageCount}";
                        } catch (Exception error) { page.Text = "页面无法预览：" + error.Message; }
                        finally { previous.IsEnabled = index > 0; next.IsEnabled = index + 1 < document.PageCount; }
                    }
                    previous.Click += async (_, _) => { if (index > 0) { index--; await Render(); } }; next.Click += async (_, _) => { if (index + 1 < document.PageCount) { index++; await Render(); } };
                    if (document.PageCount == 0) throw new IOException("PDF 没有可预览页面。"); await Render(); content.Children.Add(navigation); content.Children.Add(image);
                } else if (MediaTypes.Contains(extension)) {
                    media = new MediaPlayerElement { AutoPlay = false, AreTransportControlsEnabled = true, Height = 280 };
                    var failure = new TextBlock { TextWrapping = TextWrapping.Wrap };
                    var player = new Windows.Media.Playback.MediaPlayer { AutoPlay = false }; media.SetMediaPlayer(player);
                    player.Source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(item.Value));
                    media.MediaPlayer.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() => failure.Text = "系统无法解码此媒体，请使用默认应用打开。");
                    content.Children.Add(media); content.Children.Add(failure);
                } else if (TextTypes.Contains(extension)) {
                    if (new FileInfo(item.Value).Length > 1024 * 1024) throw new IOException("文本超过 1 MB，请使用默认应用打开。");
                    string text = await File.ReadAllTextAsync(item.Value);
                    content.Children.Add(extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase) ? MarkdownView.Render(text) : MarkdownView.Render(text, true));
                } else {
                    string type = extension.Length > 0 ? extension : "无扩展名";
                    content.Children.Add(new TextBlock { Text = $"文件类型：{type}\n{item.Value}\n该格式请用默认应用打开。",
                        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                }
            }
        } catch (Exception error) { content.Children.Add(new TextBlock { Text = "无法快速预览：" + error.Message, TextWrapping = TextWrapping.Wrap }); }
        ContentDialogResult result;
        try { result = await ShowStationDialog(dialog, grid ?? files); }
        finally { media?.MediaPlayer?.Dispose(); pdfStream?.Dispose(); pdfSource?.Dispose(); }
        if (result == ContentDialogResult.Primary) {
            if (open == null) await OpenItem(item);
            else await open();
        }
    }
}
