using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Halo;

internal sealed partial class StationWindow
{
    ContentDialog? activeContentDialog;
    internal static bool TextInputTarget(DependencyObject? target) {
        while (target != null) {
            if (target is TextBox or PasswordBox or RichEditBox) return true;
            target = VisualTreeHelper.GetParent(target);
        }
        return false;
    }
    async Task PasteContent()
    {
        if (App.Station.ReadOnly) return;
        // Only read the clipboard in response to this explicit command. Never subscribe to changes.
        var view = Clipboard.GetContent();
        if (await PasteLibraryCut(view, App.Station.LibraryRoot)) return;
        if (!StationTransfer.Supported(view)) { Notify("剪贴板没有可粘贴的文件、图片、文字或链接"); return; }
        await Receive(view);
    }
    internal async Task<StationInput[]?> ChooseLinkBatch(StationInput[] inputs)
    {
        if (inputs is not [{ Kind: StationKind.Text, TextStyle: StationTextStyle.Plain } note] || RichContent.LinkLines(note.Value) is not {} links) return inputs;
        var preview = new TextBlock { Text = string.Join(Environment.NewLine, links), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = $"检测到 {links.Length} 条网址", Content = new ScrollViewer { Content = preview, MaxHeight = 250 },
            PrimaryButtonText = "拆成独立链接", SecondaryButtonText = "保留整段文字", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Secondary };
        activeContentDialog = dialog; ContentDialogResult choice;
        try { choice = await ShowPanelDialog(dialog); } finally { activeContentDialog = null; }
        return choice switch { ContentDialogResult.Primary => links.Select(link => new StationInput(StationKind.Link, link)).ToArray(), ContentDialogResult.Secondary => inputs, _ => null };
    }
    async Task EditNote(StationItem? item, StationTextStyle initialStyle = StationTextStyle.Plain)
    {
        var styles = new ComboBox { Header = "文字类型", ItemsSource = new[] { "普通文字", "Markdown", "代码", "网页摘录" }, SelectedIndex = (int)(item?.TextStyle ?? initialStyle), HorizontalAlignment = HorizontalAlignment.Stretch };
        var title = new TextBox { Header = "名称（可留空）", Text = item?.Title ?? "", MaxLength = 200 };
        var language = new TextBox { Header = "代码语言（例如 python、csharp、json）", Text = item?.Language ?? "", MaxLength = 40 };
        var source = new TextBox { Header = "来源网址（可留空）", Text = item?.SourceUrl ?? "", MaxLength = 4096 };
        var text = new TextBox { Header = "内容", Text = item?.Value ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, MaxHeight = 280, MaxLength = StationStore.MaximumText };
        void StyleInput() { bool code = styles.SelectedIndex == (int)StationTextStyle.Code; language.Visibility = code ? Visibility.Visible : Visibility.Collapsed; text.FontFamily = new FontFamily(code ? "Consolas" : "Segoe UI"); }
        styles.SelectionChanged += (_, _) => StyleInput(); StyleInput();
        var body = new StackPanel { Spacing = 10 }; foreach (var control in new UIElement[] { title, styles, language, source, text }) body.Children.Add(control);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; body.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = item == null ? "新建便笺" : "编辑便笺", Content = new ScrollViewer { Content = body, MaxHeight = 470 }, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.None };
        dialog.PrimaryButtonClick += async (_, args) => {
            var deferral = args.GetDeferral();
            try {
                var style = (StationTextStyle)styles.SelectedIndex; string lang = style == StationTextStyle.Code ? language.Text.Trim() : "";
                if (item == null) await App.Station.ImportCopies([new(StationKind.Text, text.Text, title.Text, TextStyle: style, SourceUrl: source.Text.Trim(), Language: lang)]);
                else await App.Station.UpdateNote(item.Id, text.Text, style, source.Text.Trim(), lang, title.Text);
            } catch (Exception failure) { args.Cancel = true; error.Text = failure.Message; }
            finally { deferral.Complete(); }
        };
        activeContentDialog = dialog;
        try { await ShowPanelDialog(dialog); } finally { activeContentDialog = null; }
    }
}
