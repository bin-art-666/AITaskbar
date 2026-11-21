using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Halo;
internal sealed partial class StationWindow
{
    readonly TextBox desktopSearch = new() { PlaceholderText = "搜索文件名、便笺或链接 · Esc 收起", Margin = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    bool Searching => !string.IsNullOrWhiteSpace(desktopSearch.Text);
    void InitializeDesktopActions() {
        InitializeSearchResults();
        desktopSearch.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => _ = RefreshSearch());
        root.PreviewKeyDown += (_, e) => HandleStationShortcut(e);
    }
    void ShowSearch() { if (App.Station.LibraryOffline) return; desktopSearch.Visibility = Visibility.Visible; UpdateStationContent(); files.Margin = new Thickness(0, 44, 0, 0); desktopSearch.Focus(FocusState.Programmatic); }
    void HideSearch() { desktopSearch.Visibility = includeImageText.Visibility = Visibility.Collapsed; files.Margin = new Thickness(0); if (desktopSearch.Text.Length > 0) desktopSearch.Text = ""; }
    internal static string DesktopName(StationItem item) => item.Title.Length > 0 ? item.Title : item.Kind is StationKind.File or StationKind.Folder ? Path.GetFileName(item.Value) : item.Value;
    internal static bool SearchMatches(StationItem item, string query)
    {
        query = query.Trim();
        string value = item.Kind is StationKind.File or StationKind.Folder ? Path.GetFileName(item.Value) : item.Value;
        return DesktopName(item).Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }
    internal void VerifyDesktopSearch() {
        ShowSearch(); desktopSearch.Text = DesktopName(App.Station.Items[0]);
        if (searchResults.Items.Count == 0 || searchView.Visibility != Visibility.Visible) throw new Exception("搜索未显示匹配内容。");
        HideSearch(); if (desktopSearch.Visibility != Visibility.Collapsed || desktopSearch.Text.Length != 0 || files.Items.Count != App.Station.Items.Count) throw new Exception("收起搜索未清除内容或恢复网格。");
    }
    internal async Task<string?> AskText(string title, string initial = "", bool multiline = false, int maxLength = 120) {
        var input = new TextBox { Text = initial, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinWidth = 260, MaxHeight = 320, MaxLength = multiline ? StationStore.MaximumText : maxLength };
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = input, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, e) => { if (string.IsNullOrWhiteSpace(input.Text)) { e.Cancel = true; input.PlaceholderText = "内容不能为空"; } };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }
    static bool CanRenameStationItem(StationItem item) => item.Owned || item.Kind is StationKind.Text or StationKind.Link;
    Task RenameItem(StationItem item) => ShowRenameDialog(item, files, name => App.Station.RenameOwned(item.Id, name));
    async Task EditDesktopText(StationItem item) {
        if (item.Kind == StationKind.Text) { await EditNote(item); return; }
        var text = await AskText(item.Kind == StationKind.Link ? "编辑链接" : "编辑便笺", item.Value, item.Kind == StationKind.Text, StationStore.MaximumText);
        if (text != null) await App.Station.UpdateContent(item.Id, text);
    }
    async Task NewDesktopFolder() { var name = await AskText("新建文件夹", "新建文件夹"); if (name != null) await App.Station.NewFolder(name); }
    Task NewDesktopNote() => EditNote(null);
    internal Task ShowTrash() => ShowTrash(root.XamlRoot, this);

    internal async Task ShowTrash(XamlRoot xamlRoot, Window host) {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 360, MinWidth = 300 };
        var records = App.Station.Trash.ToArray();
        foreach (var item in records) list.Items.Add(new ListViewItem { Content = DesktopName(item) + (!item.Owned && item.Kind is StationKind.File or StationKind.Folder ? " · 原文件引用" : ""), Tag = item });
        var dialog = new ContentDialog { XamlRoot = xamlRoot, Title = $"Halo 回收站 · {records.Length} 项", Content = list, PrimaryButtonText = "恢复选中", SecondaryButtonText = "永久删除选中", CloseButtonText = "关闭", IsPrimaryButtonEnabled = false, IsSecondaryButtonEnabled = false };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = list.SelectedItems.Count > 0 && !App.Station.ReadOnly;
        var result = await ShowTrashDialog(dialog); var selected = list.SelectedItems.Cast<ListViewItem>().Select(i => (StationItem)i.Tag).ToArray();
        if (result == ContentDialogResult.Primary) {
            var desktopItems = selected.Where(item => item.Origin == null).ToArray();
            await App.Station.RestoreToDesktop(desktopItems);
            int restored = desktopItems.Length;
            foreach (var item in selected.Where(item => item.Origin != null)) {
                if (App.Station.OriginalTrashLocation(item) is { } location)
                    await App.Station.RestoreInFolder(item, location.Root, location.Directory);
                else if (!await RestoreToChosenLocation(item, xamlRoot, host)) continue;
                restored++;
            }
            if (restored > 0) Notify($"已恢复 {restored} 项");
        }
        else if (result == ContentDialogResult.Secondary && selected.Length > 0 && await ConfirmTrashDeletion(selected, xamlRoot) is { } deleted)
            Notify(TrashDeletionMessage(deleted), deleted.Failed > 0);
    }

    internal async Task<TrashDeleteResult?> ConfirmTrashDeletion(StationItem[] selected, XamlRoot? xamlRoot = null)
    {
        if (selected.Length == 0 || App.Station.ReadOnly) return null;
        var dialog = new ContentDialog {
            XamlRoot = xamlRoot ?? root.XamlRoot, Title = $"永久删除回收站中的 {selected.Length} 项？",
            Content = "删除后无法从 Halo 回收站恢复。原文件引用只会移除记录。",
            PrimaryButtonText = "永久删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };
        return await ShowTrashDialog(dialog) == ContentDialogResult.Primary ? await App.Station.DeleteTrash(selected) : null;
    }

    async Task<ContentDialogResult> ShowTrashDialog(ContentDialog dialog)
    {
        activeContentDialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally { activeContentDialog = null; }
    }

    internal static string TrashDeletionMessage(TrashDeleteResult result) =>
        $"已永久删除 {result.Deleted} 项。" + (result.Failed > 0 ? $" {result.Failed} 项删除失败，已保留。" : "");
    async Task<bool> RestoreToChosenLocation(StationItem item, XamlRoot xamlRoot, Window host)
    {
        var dialog = new ContentDialog { XamlRoot = xamlRoot, Title = $"恢复「{DesktopName(item)}」",
            Content = "原父目录已不存在。请选择恢复到中转站，或选择另一个 Halo 文件夹。",
            PrimaryButtonText = "选择文件夹…", SecondaryButtonText = "恢复到中转站", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary) { await App.Station.RestoreToDesktop([item]); return true; }
        if (choice != ContentDialogResult.Primary) return false;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(host));
        var target = await picker.PickSingleFolderAsync();
        if (target == null) return false;
        var folder = App.Station.ManagedFolderContaining(target.Path) ?? throw new IOException("请选择 Halo 管理的文件夹。");
        await App.Station.RestoreInFolder(item, folder, target.Path);
        return true;
    }
    async Task BackupDesktop() {
        var picker = new FileSavePicker { SuggestedFileName = "Halo桌面-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") }; picker.FileTypeChoices.Add("Halo 桌面备份", new List<string> { ".zip" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync(); if (file == null) return;
        NotifyProgress("正在备份文件和布局…"); await App.Station.ExportDesktop(file.Path); Notify("桌面备份已完成（旧引用仅保存路径）");
    }
    async Task RestoreDesktop() {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".zip"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync(); if (file == null) return;
        if (await new ContentDialog { XamlRoot = root.XamlRoot, Title = "恢复 Halo 桌面？", Content = "将替换当前内容和布局。会先在 Halo 数据目录的 backups 中创建完整备份；原文件不会改动。", PrimaryButtonText = "备份当前并恢复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowAsync() != ContentDialogResult.Primary) return;
        string backups = Path.Combine(App.DataDirectory, "backups"); Directory.CreateDirectory(backups); NotifyProgress("正在备份当前桌面并恢复…");
        await App.Station.ExportDesktop(Path.Combine(backups, "before-restore-" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".zip"));
        await App.Station.RestoreDesktop(file.Path); Notify("桌面已恢复");
    }
}
