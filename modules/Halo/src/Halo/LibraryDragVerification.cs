using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifyMainLibraryDrop(Action<string, bool> check)
    {
        string seeds = Directory.CreateDirectory(Path.Combine(App.DataDirectory, "main-library-drop")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(seeds, "拖放归档测试")).FullName;
        string source = Path.Combine(seeds, "拖放移动样例.txt");
        await File.WriteAllTextAsync(source, "内部移动");
        await App.Station.ImportCopies([new(StationKind.Folder, target), new(StationKind.File, source)]);
        await Task.Delay(170);
        var cards = files.Items.Cast<GridViewItem>().ToArray();
        var onto = cards.Single(card => Path.GetFileName(((StationItem)card.Tag).Value) == "拖放归档测试");
        var from = cards.Single(card => Path.GetFileName(((StationItem)card.Tag).Value) == "拖放移动样例.txt");
        var moving = (StationItem)from.Tag;
        var folder = (StationItem)onto.Tag;
        files.ScrollIntoView(onto);
        await Task.Delay(130);
        var box = LibraryDropCue.Bounds(onto, files);
        var point = new Point(box.Left + box.Width / 2, box.Top + box.Height / 2);
        cardPress = new(from, 0, ReleaseSelection.None);
        movingIds = [moving.Id];
        dragAnchor = moving.Id;
        pointerDragLease = KeepOpenForDrag();
        try {
            UpdateDropTarget(point);
            check("LibraryDragMainGridRecognizesExistingFolder", MainFolderSpot(point)?.Destination.RootId == folder.Id
                && MainFolderSpot(new(box.Left + 1, point.Y)) == null);
            await Task.Delay(420);
            await CommitPointerDrag(point);
            string published = Path.Combine(folder.Value, Path.GetFileName(source));
            check("LibraryDragMainGridMovesOnlyOwnedCopy", !File.Exists(moving.Value) && File.ReadAllText(published) == "内部移动"
                && File.Exists(source) && LatestLibraryMove != null);
            await UndoLibraryMove();
            check("LibraryDragMainGridUndoRestoresOwnedCopy", File.Exists(moving.Value) && !File.Exists(published));
        } finally {
            if (pointerDragLease != null) CancelPointerDrag();
        }
    }
}

internal sealed partial class FolderBrowser
{
    internal async Task VerifyLibraryDragTargets(Action<string, bool> check, string parent)
    {
        string working = await store.CreateFolderIn(folder, parent, "内部拖放", default);
        string destination = Directory.CreateDirectory(Path.Combine(working, "归档")).FullName;
        string first = Path.Combine(working, "A.txt"), second = Path.Combine(working, "B.txt");
        string duplicate = Path.Combine(destination, "A.txt");
        await File.WriteAllTextAsync(first, "来源 A");
        await File.WriteAllTextAsync(second, "来源 B");
        await File.WriteAllTextAsync(duplicate, "已有 A");
        await Navigate(working);
        await Task.Delay(150);
        files.UpdateLayout();

        GridViewItem Card(string value) => files.Items.Cast<GridViewItem>().Single(card => ((StationItem)card.Tag).Value == value);
        Point Center(FrameworkElement element)
        {
            var bounds = LibraryDropCue.Bounds(element, files);
            return new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }
        Point BeginBatch()
        {
            files.SelectedItems.Clear();
            files.SelectedItems.Add(Card(first));
            files.SelectedItems.Add(Card(second));
            BeginLocalDrag(Card(first), new(45, 80), new(.5, .5));
            var point = Center(Card(destination));
            UpdateTarget(point);
            return point;
        }

        var point = BeginBatch();
        check("LibraryDragFolderCenterWaitsForCue", libraryDrop.Armed == null && ResolveFolderDrop(point, localLibraryDrag!) == null
            && File.Exists(first) && File.Exists(second));
        await Task.Delay(420);
        var folderPlan = ResolveFolderDrop(point, localLibraryDrag!);
        check("LibraryDragFolderCueArmsWithoutTouchingFiles", folderPlan?.Destination?.Directory == destination
            && File.Exists(first) && !File.Exists(Path.Combine(destination, "B.txt")));
        UpdateTarget(point, copy: true);
        check("CtrlDragSwitchesArmedFolderToCopyWithoutAnotherDwell",
            ResolveFolderDrop(point, localLibraryDrag!, copy: true) is { Copy: true, Destination: not null }
            && FolderCopyCaption() == "松开以复制到此文件夹");
        UpdateTarget(point, copy: false);
        check("CtrlDragReleaseSwitchesArmedFolderBackToMove",
            ResolveFolderDrop(point, localLibraryDrag!) is { Copy: false, Destination: not null });
        UpdateTarget(point, copy: true);
        CancelLocalDrag();
        check("LibraryDragCancelClearsFolderCue", libraryDrop.Armed == null && File.Exists(first) && File.Exists(second));

        point = BeginBatch();
        await Task.Delay(420);
        await CommitLocalDrop(point);
        string numbered = Path.Combine(destination, "A (2).txt"), movedSecond = Path.Combine(destination, "B.txt");
        check("LibraryDragMovesWholeSelectionIntoExistingFolder", !File.Exists(first) && !File.Exists(second)
            && File.ReadAllText(numbered) == "来源 A" && File.ReadAllText(movedSecond) == "来源 B" && File.ReadAllText(duplicate) == "已有 A");
        await UndoLastOrganization();
        check("LibraryDragMoveCanBeUndone", File.Exists(first) && File.Exists(second) && !File.Exists(numbered) && !File.Exists(movedSecond));

        await Navigate(destination);
        await Task.Delay(160);
        files.UpdateLayout();
        files.SelectedItem = Card(duplicate);
        BeginLocalDrag(Card(duplicate), new(45, 80), new(.5, .5));
        UpdateTarget(new(65, 55));
        root.UpdateLayout();
        var parentCrumb = StationWindow.Descendants<BreadcrumbBarItem>(breadcrumbs).FirstOrDefault(element =>
            element.IsLoaded && element.Content is Crumb crumb && crumb.Path == working);
        var parentPoint = Center((FrameworkElement?)parentCrumb ?? back);
        UpdateTarget(parentPoint);
        check("LibraryDragParentBreadcrumbResolvesToParent", ResolveFolderDrop(parentPoint, localLibraryDrag!)?.Destination?.Directory == working);
        await CommitLocalDrop(parentPoint);
        check("LibraryDragParentDropMovesWithoutCopying", !File.Exists(duplicate)
            && File.ReadAllText(Path.Combine(working, "A (2).txt")) == "已有 A");
        await UndoLastOrganization();

        files.SelectedItem = Card(duplicate);
        BeginLocalDrag(Card(duplicate), new(45, 80), new(.5, .5));
        UpdateTarget(new(65, 55));
        root.UpdateLayout();
        var desktopPoint = Center(desktopDropTarget);
        UpdateTarget(desktopPoint);
        check("LibraryDragDesktopBreadcrumbResolvesToLibraryRoot", desktopDropTarget.Visibility == Visibility.Visible
            && ResolveFolderDrop(desktopPoint, localLibraryDrag!)?.Destination == store.LibraryRoot);
        await CommitLocalDrop(desktopPoint);
        check("LibraryDragDesktopBreadcrumbMovesIntoMainGrid", !File.Exists(duplicate)
            && store.Items.Any(item => item.Kind == StationKind.File && Path.GetFileName(item.Value) == "A.txt"));
        await UndoLastOrganization();

        var item = (StationItem)Card(duplicate).Tag;
        var package = new DataPackage();
        await FillCopy(package, [item]);
        var native = owner.BeginNativeLibraryDrag(package, new(folder.Id, destination), [item], item.Id);
        try {
            var returned = package.GetView();
            var localPoint = new Point(files.ActualWidth - 30, files.ActualHeight - 50);
            UpdateTarget(localPoint);
            check("LibraryDragNativeReturnUsesSourceFolderAndKeepsExternalCopyPayload",
                StationWindow.HasLibraryDragIdentity(returned) && await owner.MatchesNativeLibraryDrag(returned, native)
                && returned.RequestedOperation == DataPackageOperation.Copy
                && returned.Contains(StandardDataFormats.StorageItems) && ResolveFolderDrop(localPoint, native) is { Cell: not null, Destination: null });
            UpdateTarget(localPoint, copy: true);
            check("CtrlDragReturningFromNativeHandoffCanCopyIntoEmptyCell",
                ResolveFolderDrop(localPoint, native, copy: true) is { Copy: true, Placement: not null });
            owner.EndNativeLibraryDrag(native);
            check("LibraryDragExpiredIdentityCannotBeImportedAsAnInternalMove",
                StationWindow.HasLibraryDragIdentity(returned) && !await owner.MatchesNativeLibraryDrag(returned, native));
        } finally {
            owner.EndNativeLibraryDrag(native);
            ClearLibraryDropTarget();
        }
        var sourceCard = Card(duplicate);
        files.SelectedItems.Clear();
        files.SelectedItems.Add(sourceCard);
        BeginLocalDrag(sourceCard, new(45, 80), new(.5, .5));
        var emptyPoint = new Point(files.ActualWidth - 30, files.ActualHeight - 50);
        UpdateTarget(emptyPoint, copy: true);
        await CommitLocalDrop(emptyPoint, copy: true);
        check("CtrlDragFolderEmptyDropCommitsCopyAtRelease", File.Exists(duplicate)
            && File.ReadAllText(Path.Combine(destination, "A (2).txt")) == File.ReadAllText(duplicate));
        await Navigate(parent);
    }
}
