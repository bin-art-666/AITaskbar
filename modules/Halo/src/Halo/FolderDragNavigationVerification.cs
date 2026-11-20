using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Halo;

internal sealed partial class FolderBrowser
{
    internal async Task VerifyDragNavigation(Action<string, bool> check, string parent)
    {
        string work = await store.CreateFolderIn(folder, parent, "拖拽返回验证", default);
        string deeper = Directory.CreateDirectory(Path.Combine(work, "更深一层")).FullName;
        string a = Path.Combine(deeper, "返回样例 A.txt"), b = Path.Combine(deeper, "返回样例 B.txt");
        await File.WriteAllTextAsync(a, "A");
        await File.WriteAllTextAsync(b, "B");
        await Navigate(deeper);
        await Task.Delay(160);
        files.UpdateLayout();
        var cards = files.Items.Cast<GridViewItem>().ToArray();
        files.SelectedItems.Clear();
        foreach (var card in cards) files.SelectedItems.Add(card);
        BeginLocalDrag(cards[0], new(45, 95), new(.5, .5));
        var session = localLibraryDrag!;
        var thumbnail = dragLayer.Children.Single();
        var backBounds = LibraryDropCue.Bounds(back, this);
        Point backPoint = new(backBounds.Left + backBounds.Width / 2, backBounds.Top + backBounds.Height / 2);
        TrackBackHover(backPoint, session);
        check("FolderDragBackWaitsForHoverWithoutChangingFiles", CurrentDirectory == deeper
            && backHover == BackHover.Button && backHint.Visibility == Visibility.Visible && File.Exists(a) && File.Exists(b));
        check("FolderDragBackHintStaysBelowHeader", HintClearsHeader());
        await Task.Delay(480);
        await pendingDragBack;
        check("FolderDragBackRetainsSourceAndOriginalPreview", CurrentDirectory == work
            && ReferenceEquals(localLibraryDrag, session) && ReferenceEquals(dragLayer.Children.Single(), thumbnail)
            && session.Items.Length == 2 && File.Exists(a) && File.Exists(b));
        TrackBackHover(backPoint, session);
        await Task.Delay(420);
        check("FolderDragBackRequiresLeavingTriggerBeforeNextHop", CurrentDirectory == work && backNeedsReentry);

        var body = LibraryDropCue.Bounds(root, this);
        TrackBackHover(new(body.Left + body.Width / 2, body.Top + body.Height / 2), session);
        TrackBackHover(new(3, ActualHeight / 2), session);
        check("FolderDragShadeStartsItsOwnDwell", backHover == BackHover.OutsidePanel
            && backHoverTimer.Interval == TimeSpan.FromMilliseconds(400));
        double originalWidth = root.MaxWidth;
        try {
            root.MaxWidth = 280;
            check("FolderDragEdgeHintClearsHeaderInNarrowPanel", HintClearsHeader());
        } finally {
            root.MaxWidth = originalWidth;
            UpdateLayout();
        }
        await Task.Delay(520);
        await pendingDragBack;
        check("FolderDragShadeGoesBackExactlyOneLevel", CurrentDirectory == parent
            && ReferenceEquals(localLibraryDrag, session) && File.Exists(a) && File.Exists(b));
        CancelLocalDrag();
        await FinishDragNavigation(restore: true);
        check("FolderDragEscapeRestoresOriginalDirectoryAndSelection",
            CurrentDirectory == deeper && SelectedEntries().Select(item => item.Value).ToHashSet().SetEquals([a, b])
            && !localDrag && backHint.Visibility == Visibility.Collapsed);

        await Navigate(folder.Value);
        string rootFile = Path.Combine(folder.Value, "返回到中转站.txt");
        await File.WriteAllTextAsync(rootFile, "顶部拖出验证");
        await Navigate(folder.Value, refresh: true);
        await Task.Delay(140);
        var rootCard = files.Items.Cast<GridViewItem>().Single(card => ((StationItem)card.Tag).Value == rootFile);
        files.SelectedItem = rootCard;
        BeginLocalDrag(rootCard, new(45, 95), new(.5, .5));
        var toDesktop = localLibraryDrag!;
        await BackWhileDragging(toDesktop);
        check("FolderDragFirstLevelExposesDesktopWithoutDetachingPreview",
            ShowingDesktopDuringDrag && root.Visibility == Visibility.Collapsed && shade.Visibility == Visibility.Collapsed
            && dragLayer.Children.Count == 1 && ReferenceEquals(localLibraryDrag, toDesktop) && File.Exists(rootFile));
        CancelLocalDrag();
        await FinishDragNavigation(restore: true);
        check("FolderDragCancelFromDesktopReopensOriginalFolder",
            !ShowingDesktopDuringDrag && root.Visibility == Visibility.Visible && CurrentDirectory == folder.Value
            && SelectedEntries().Any(item => item.Value == rootFile));

        await Navigate(deeper);
        await Task.Delay(120);
        var file = files.Items.Cast<GridViewItem>().Single(card => ((StationItem)card.Tag).Value == a);
        ClearSelection();
        files.SelectedItem = file;
        BeginLocalDrag(file, new(45, 95), new(.5, .5));
        await BackWhileDragging(localLibraryDrag!);
        var blank = new Point(files.ActualWidth - 25, files.ActualHeight - 48);
        UpdateTarget(blank, copy: true);
        await CommitLocalDrop(blank, copy: true);
        check("FolderDragReleaseInParentCopiesOnCtrlOnlyAfterNavigation",
            CurrentDirectory == work && File.Exists(a) && File.ReadAllText(Path.Combine(work, Path.GetFileName(a))) == "A");
        await Navigate(parent);

        bool HintClearsHeader()
        {
            UpdateLayout();
            var hint = LibraryDropCue.Bounds(backHint, this);
            var panel = LibraryDropCue.Bounds(root, this);
            return hint.Width > 0 && hint.Height > 0
                && hint.Top >= Math.Max(LibraryDropCue.Bounds(back, this).Bottom, LibraryDropCue.Bounds(breadcrumbs, this).Bottom)
                && hint.Left >= panel.Left && hint.Right <= panel.Right;
        }
    }
}
