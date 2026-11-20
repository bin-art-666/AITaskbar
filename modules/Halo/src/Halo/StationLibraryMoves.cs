using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Halo;

internal sealed partial class StationWindow
{
    const string CutFormat = "Halo.LibraryMove.1";
    sealed record CutSelection(string Token, LibraryLocation Source, StationItem[] Items);
    CutSelection? pendingCut;
    FolderUndo? latestLibraryMove;

    internal FolderUndo? LatestLibraryMove => latestLibraryMove is { IsCurrent: { } current } move && current() ? move : null;

    internal async Task PickLibraryDestination(LibraryLocation source, StationItem[] items, GridView grid)
    {
        if (App.Station.ReadOnly || !StationStore.CanMoveLibraryItems(items)) return;
        var location = App.Station.LibraryRoot;
        var parents = new Stack<LibraryLocation>();
        var path = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var back = new Button { Content = "上一级" };
        var choices = new ListView { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, MaxHeight = 225, MinHeight = 48 };
        var empty = new TextBlock { Text = "没有子文件夹", Opacity = .7 };
        var content = new StackPanel { Spacing = 8 };
        AutomationProperties.SetName(choices, "可浏览的目标文件夹");
        content.Children.Add(path); content.Children.Add(back); content.Children.Add(choices); content.Children.Add(empty);
        var dialog = new ContentDialog {
            Title = $"移动 {items.Length} 项到…", Content = content, PrimaryButtonText = "移动到此处",
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };

        void ShowLocation()
        {
            var children = App.Station.LibrarySubfolders(location);
            path.Text = location.RootId == null ? "中转站" : "中转站 / " + string.Join(" / ",
                parents.Reverse().Where(parent => parent.RootId != null)
                    .Select(parent => Path.GetFileName(parent.Directory)).Append(Path.GetFileName(location.Directory)));
            back.IsEnabled = parents.Count > 0;
            dialog.IsPrimaryButtonEnabled = StationStore.CanMoveTo(source, location, items);
            choices.Items.Clear();
            foreach (var child in children) choices.Items.Add(new ListViewItem { Content = Path.GetFileName(child.Directory), Tag = child });
            empty.Visibility = children.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Navigate(LibraryLocation target, bool goingBack)
        {
            var previous = location;
            location = target;
            if (goingBack) parents.Pop();
            else parents.Push(previous);
            try { ShowLocation(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                location = previous;
                if (goingBack) parents.Push(target);
                else parents.Pop();
                path.Text = error.Message;
                dialog.IsPrimaryButtonEnabled = false;
            }
        }

        choices.ItemClick += (_, e) => {
            if (e.ClickedItem is ListViewItem { Tag: LibraryLocation next }) Navigate(next, goingBack: false);
        };
        back.Click += (_, _) => { if (parents.TryPeek(out var parent)) Navigate(parent, goingBack: true); };
        ShowLocation();
        if (await ShowStationDialog(dialog, grid) == ContentDialogResult.Primary)
            await MoveLibrarySelection(source, location, items);
    }

    internal Task CutLibrarySelection(LibraryLocation source, StationItem[] items)
    {
        if (App.Station.ReadOnly || !StationStore.CanMoveLibraryItems(items)) return Task.CompletedTask;
        var cut = new CutSelection(Guid.NewGuid().ToString("N"), source, items);
        var package = new DataPackage();
        package.SetData(CutFormat, cut.Token);
        Clipboard.SetContent(package);
        pendingCut = cut;
        string message = $"已剪切 {items.Length} 项，前往 Halo 目标目录按 Ctrl+V";
        if (folderBrowser is { } browser) browser.NotifyLibraryCut(message);
        else Notify(message);
        return Task.CompletedTask;
    }

    internal async Task<bool> PasteLibraryCut(DataPackageView clipboard, LibraryLocation destination)
    {
        if (!clipboard.Contains(CutFormat) || clipboard.Contains(StandardDataFormats.StorageItems)
            || clipboard.Contains(StandardDataFormats.Text)) return false;
        if (pendingCut is not { } cut || await clipboard.GetDataAsync(CutFormat) is not string token || token != cut.Token)
            throw new IOException("这次 Halo 剪切已失效，请重新选择项目。");
        await MoveLibrarySelection(cut.Source, destination, cut.Items);
        if (ReferenceEquals(pendingCut, cut)) pendingCut = null;
        return true;
    }

    internal async Task MoveLibrarySelection(LibraryLocation source, LibraryLocation destination, StationItem[] items)
    {
        var result = await App.Station.MoveLibraryItems(source, destination, items, folderBrowser?.LibraryColumns ?? GridColumns());
        latestLibraryMove = result;
        undo = [];
        if (folderBrowser is { ShowingDesktopDuringDrag: false } browser) await browser.RefreshAfterLibraryMove();
        string target = destination.RootId == null ? "中转站" : Path.GetFileName(destination.Directory);
        string message = $"已移动 {items.Length} 项到「{target}」";
        if (folderBrowser is { ShowingDesktopDuringDrag: false } active) active.NotifyLibraryMove(message, result);
        else Notify(message, undoLabel: result.Label, undoAction: UndoLibraryMove);
    }

    internal async Task CopyLibrarySelection(LibraryLocation source, LibraryLocation destination, StationItem[] items,
        LibraryCopyPlacement? placement = null)
    {
        int count = await App.Station.CopyLibraryItems(source, destination, items, folderBrowser?.LibraryColumns ?? GridColumns(), placement);
        if (folderBrowser is { ShowingDesktopDuringDrag: false } browser) await browser.RefreshAfterLibraryMove();
        string target = destination.RootId == null ? "中转站" : Path.GetFileName(destination.Directory);
        if (folderBrowser is { ShowingDesktopDuringDrag: false } active) active.NotifyLibraryMove($"已复制 {count} 项到「{target}」", null);
        else Notify($"已复制 {count} 项到「{target}」");
    }

    internal async Task UndoLibraryMove()
    {
        if (LatestLibraryMove is not { } move) return;
        await move.Apply();
        latestLibraryMove = null;
        if (folderBrowser is { } browser) await browser.RefreshAfterLibraryMove();
        if (folderBrowser is { } active) active.NotifyLibraryMove("已撤销移动", null);
        else Notify("已撤销移动");
    }
}
