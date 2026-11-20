using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifyStationDialogs(Action<string, bool> check)
    {
        string fixture = Directory.CreateDirectory(Path.Combine(App.DataDirectory, "station-dialogs")).FullName;
        string name = "用于校验重命名只选择文件名主体和快速预览完整文件名" + new string('节', 82);
        string source = Path.Combine(fixture, name + ".txt");
        await File.WriteAllTextAsync(source, "原始文本");
        string folderName = "测试完整名称及路径" + new string('长', 85);
        string folderSource = Directory.CreateDirectory(Path.Combine(fixture, folderName)).FullName;
        string subfolderName = "子文件夹" + new string('层', 75);
        Directory.CreateDirectory(Path.Combine(folderSource, subfolderName));
        await App.Station.ImportCopies([new(StationKind.File, source), new(StationKind.Folder, folderSource)]);
        var folder = App.Station.Items.Single(item => item.Kind == StationKind.Folder && Path.GetFileName(item.Value) == folderName);
        var file = App.Station.Items.Single(item => item.Kind == StationKind.File && Path.GetFileName(item.Value) == name + ".txt");
        var theme = root.RequestedTheme;
        static void Invoke(Button button) =>
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        static Button Button(ContentDialog dialog, string label) =>
            Descendants<Button>(dialog).Single(button => Equals(button.Content, label));
        static bool LeftOf(ContentDialog dialog, Button left, Button right) =>
            left.TransformToVisual(dialog).TransformPoint(default).X < right.TransformToVisual(dialog).TransformPoint(default).X;
        static bool Surface(ContentDialog dialog, bool dark) =>
            Descendants<Border>(dialog).FirstOrDefault(border => border.Name == "BackgroundElement")?.Background is SolidColorBrush fill
            && fill.Color == PanelTheme.SolidContentSurface(dark);
        async Task<ContentDialog> Opened(Task task)
        {
            for (int attempt = 0; activeContentDialog is null && !task.IsCompleted && attempt < 50; attempt++) await Task.Delay(25);
            if (activeContentDialog is not { } dialog) throw new InvalidOperationException("预期的 Halo 弹窗没有打开。");
            await Task.Delay(160);
            return dialog;
        }
        try {
            root.RequestedTheme = ElementTheme.Light;
            Open();
            await Task.Delay(160);
            var fileCard = files.Items.Cast<GridViewItem>().Single(card => ((StationItem)card.Tag).Id == file.Id);
            files.SelectedItem = fileCard;
            var rename = RenameItem(file);
            try {
                var dialog = await Opened(rename);
                var input = Descendants<TextBox>(dialog).Single();
                var save = Button(dialog, "保存");
                check("RenameDialogHasCompactLayoutAndShortTitle", Equals(dialog.Title, "重命名")
                    && Descendants<Border>(dialog).Single(border => border.Name == "BackgroundElement").ActualHeight is > 0 and <= 165);
                check("RenameDialogUsesLightHaloSurfaceAndFileStemSelection", Surface(dialog, false)
                    && ReferenceEquals(FocusManager.GetFocusedElement(dialog.XamlRoot), input)
                    && input.Text == name + ".txt" && input.SelectedText == name
                    && LeftOf(dialog, Button(dialog, "取消"), save));
                input.Text = "非法:名称";
                await Task.Delay(120);
                check("RenameDialogShowsInlineValidation", !save.IsEnabled
                    && Descendants<TextBlock>(dialog).Any(text => text.Visibility == Visibility.Visible
                        && text.Text.Contains("不允许的字符", StringComparison.Ordinal)));
                input.Text = "尚未保存.txt";
                Invoke(Button(dialog, "取消"));
                await rename;
                check("RenameDialogCancelPreservesNameAndSelection", App.Station.Items.Single(item => item.Id == file.Id) == file
                    && ReferenceEquals(files.SelectedItem, fileCard) && File.Exists(file.Value));
            } finally {
                if (!rename.IsCompleted) { activeContentDialog?.Hide(); await rename; }
            }
            await Task.Delay(120);
            var filePreview = PreviewItem(file);
            try {
                var dialog = await Opened(filePreview);
                check("PreviewDialogShowsFullWrappedFileName", dialog.Title is TextBlock { TextWrapping: TextWrapping.Wrap } heading
                    && heading.Text == name + ".txt" && heading.TextTrimming == TextTrimming.None
                    && ReferenceEquals(FocusManager.GetFocusedElement(dialog.XamlRoot), Button(dialog, "关闭"))
                    && LeftOf(dialog, Button(dialog, "关闭"), Button(dialog, "用默认应用打开")));
                Invoke(Button(dialog, "关闭"));
                await filePreview;
            } finally {
                if (!filePreview.IsCompleted) { activeContentDialog?.Hide(); await filePreview; }
            }
            await Task.Delay(120);
            root.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(120);
            await OpenItem(folder);
            var browser = folderBrowser!;
            var card = (GridViewItem)browser.Files.Items[0];
            var child = (StationItem)card.Tag;
            browser.Files.SelectedItem = card;
            var folderPreview = PreviewItem(child, () => browser.OpenEntry(child), browser.Files, folder);
            try {
                var dialog = await Opened(folderPreview);
                check("FolderPreviewUsesDarkHaloSurfaceAndCompactPath", Surface(dialog, true)
                    && dialog.Title is Grid titleHeader
                    && Descendants<TextBlock>(titleHeader).Any(heading => heading.Text == subfolderName && heading.TextWrapping == TextWrapping.Wrap)
                    && Descendants<Image>(titleHeader).Any(image => image.Width == 24 && image.Source != null)
                    && Descendants<TextBlock>(dialog).Any(text => text.Text == child.Value
                        && text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.CharacterEllipsis)
                    && Equals(dialog.Resources["ContentDialogPadding"], new Thickness(16, 12, 16, 12))
                    && LeftOf(dialog, Button(dialog, "关闭"), Button(dialog, "打开文件夹")));
                check("FolderPreviewShowsEmptyState", Descendants<TextBlock>(dialog).Any(text => text.Text == "此文件夹为空"));
                var copy = Button(dialog, "复制路径");
                Invoke(copy);
                check("FolderPreviewCopiesFullPath", Equals(copy.Content, "已复制")
                    && await Clipboard.GetContent().GetTextAsync() == child.Value);
                Invoke(Button(dialog, "关闭"));
                await folderPreview;
                check("FolderPreviewCloseKeepsCurrentSelection", ReferenceEquals(browser.Files.SelectedItem, card)
                    && browser.CurrentDirectory == folder.Value);
            } finally {
                if (!folderPreview.IsCompleted) { activeContentDialog?.Hide(); await folderPreview; }
            }
            await Task.Delay(120);
            string nested = Directory.CreateDirectory(Path.Combine(child.Value, "预览项-子文件夹")).FullName;
            await File.WriteAllTextAsync(Path.Combine(nested, "不应出现在当前层.txt"), "二级内容");
            for (int index = 1; index <= 7; index++)
                await File.WriteAllTextAsync(Path.Combine(child.Value, $"预览项-{index}.txt"), $"项目 {index}");
            var contentsPreview = PreviewItem(child, () => browser.OpenEntry(child), browser.Files, folder);
            try {
                var dialog = await Opened(contentsPreview);
                var labels = Descendants<TextBlock>(dialog).Select(text => text.Text).ToArray();
                check("FolderPreviewCapsCurrentLevelAndShowsOverflow", labels.Count(label => label.StartsWith("预览项-", StringComparison.Ordinal)) == 6
                    && labels.Contains("还有更多项目") && !labels.Contains("此文件夹为空") && !labels.Contains("不应出现在当前层.txt"));
                Invoke(Button(dialog, "打开文件夹"));
                await contentsPreview;
                check("FolderPreviewOpensCurrentFolderInPanel", browser.CurrentDirectory == child.Value && browser.Files.Items.Count == 8);
                await browser.Back();
            } finally {
                if (!contentsPreview.IsCompleted) { activeContentDialog?.Hide(); await contentsPreview; }
            }
            await Task.Delay(120);
            string renamed = "";
            var childRename = ShowRenameDialog(child, browser.Files,
                async value => renamed = await App.Station.RenameFolderEntry(folder, folder.Value, child, value, 4));
            try {
                var dialog = await Opened(childRename);
                var input = Descendants<TextBox>(dialog).Single();
                check("FolderRenameDialogSelectsCompleteNameInDarkTheme", Surface(dialog, true) && input.SelectedText == subfolderName);
                input.Text = "新名称";
                await Task.Delay(80);
                Invoke(Button(dialog, "保存"));
                await childRename;
                check("FolderRenameDialogSavesInCurrentDirectory", renamed == Path.Combine(folder.Value, "新名称") && Directory.Exists(renamed));
            } finally {
                if (!childRename.IsCompleted) { activeContentDialog?.Hide(); await childRename; }
            }
        } finally {
            CloseFolderBrowser();
            root.RequestedTheme = theme;
        }
    }
}
