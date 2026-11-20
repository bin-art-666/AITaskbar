using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using VirtualKey = Windows.System.VirtualKey;

namespace Halo;

internal sealed partial class FolderBrowser
{
    readonly Canvas dragLayer = new() { IsHitTestVisible = false };
    readonly Border dropFrame = new() { IsHitTestVisible = false, CornerRadius = new(8),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    readonly TranslateTransform dragPosition = new();
    readonly Dictionary<GridViewItem, double> dragOpacity = [];
    DispatcherQueueTimer autoScroll = null!;
    FolderDropIntent folderDrop = null!;
    LibraryDropCue libraryDrop = null!;
    DragCopyFeedback copyFeedback = null!;
    bool copyDrag;
    IDisposable? localDragLease;
    Action? cancelPress;
    Point visualAnchor, gridPointer, previewOffset;
    LibraryDrag? localLibraryDrag;
    bool localDrag;
    Task<StationWindow.DragPreviewPixels?>? previewCapture;
    SoftwareBitmap? systemPreview;

    void InitializeOrdering()
    {
        SetRow(dropFrame, GetRow(files));
        root.Children.Insert(root.Children.IndexOf(notice), dropFrame);
        folderDrop = new(DispatcherQueue, dropFrame, () => FolderTargetAt(gridPointer),
            card => DrawDropFrame(FolderDropIntent.CardFrame(files, card)));
        libraryDrop = new(root, () => { if (copyDrag && localDrag) UpdateTarget(gridPointer, true); });
        copyFeedback = new(root, copy => { if (localDrag) TrackCarriedDrop(copy); });
        Children.Add(dragLayer);
        ScrollViewer.SetHorizontalScrollMode(files, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(files, ScrollBarVisibility.Auto);
        files.SizeChanged += (_, _) => ApplyPositions();
        PreviewKeyDown += (_, e) => {
            if (e.Key == VirtualKey.Escape && CancelNavigationDrag()) e.Handled = true;
        };
        autoScroll = DispatcherQueue.CreateTimer();
        autoScroll.Interval = TimeSpan.FromMilliseconds(50);
        autoScroll.Tick += (_, _) => {
            if ((!localDrag && owner.ActiveNativeLibraryDrag == null) || StationWindow.Child<ScrollViewer>(files) is not { } scroll) return;
            double x = Math.Clamp(scroll.HorizontalOffset + ScrollStep(gridPointer.X, files.ActualWidth), 0, scroll.ScrollableWidth);
            double y = Math.Clamp(scroll.VerticalOffset + ScrollStep(gridPointer.Y, files.ActualHeight), 0, scroll.ScrollableHeight);
            if (scroll.ChangeView(x, y, null, true))
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => {
                    if (localDrag || owner.ActiveNativeLibraryDrag != null) UpdateTarget(gridPointer, copyDrag);
                });
        };
        static int ScrollStep(double point, double end) => point < 30 ? -16 : point > end - 30 ? 16 : 0;
    }

    StationItem[] Entries() => files.Items.Cast<GridViewItem>().Select(card => (StationItem)card.Tag).ToArray();
    int VisibleColumns => Math.Clamp((int)((files.ActualWidth - files.Padding.Left - files.Padding.Right) / metrics.CellWidth), 1, 64);
    StationLayout? GridLayout => directory == null ? null : store.FolderDisplayGrid(folder, directory, Entries(), VisibleColumns);

    void ApplyPositions()
    {
        if (closed) return;
        if (FilteringFolder) {
            for (int index = 0; index < files.Items.Count; index++)
                StationGridPanel.Position((GridViewItem)files.Items[index], index, VisibleColumns);
            (files.ItemsPanelRoot as StationGridPanel)?.InvalidateMeasure();
            return;
        }
        if (GridLayout is not { } layout) return;
        var positions = layout.Cells.ToDictionary(cell => cell.Id);
        foreach (GridViewItem card in files.Items) {
            var cell = positions[((StationItem)card.Tag).Id];
            StationGridPanel.Position(card, cell.Row * layout.Columns + cell.Column, layout.Columns);
        }
        (files.ItemsPanelRoot as StationGridPanel)?.InvalidateMeasure();
    }

    bool ContainsGridPoint(Point p) => !FilteringFolder && !store.ReadOnly && directory != null && store.SortFolder(folder, directory) == FolderSort.Manual && p.X >= 0 && p.Y >= 0
        && p.X < files.ActualWidth && p.Y < files.ActualHeight;

    (int Column, int Row, Rect Frame) TargetAt(Point p)
    {
        var panel = files.ItemsPanelRoot as StationGridPanel;
        Point origin = panel?.TransformToVisual(files).TransformPoint(new()) ?? new Point(files.Padding.Left, files.Padding.Top);
        double width = panel?.CellWidth ?? metrics.CellWidth, height = panel?.CellHeight ?? metrics.CellHeight;
        int columns = panel?.Columns ?? GridLayout?.Columns ?? VisibleColumns;
        int column = Math.Clamp((int)Math.Floor((p.X - origin.X) / width), 0, columns - 1);
        int row = Math.Clamp((int)Math.Floor((p.Y - origin.Y) / height), 0, 4095);
        return (column, row, new Rect(origin.X + column * width + metrics.Margin.Left,
            origin.Y + row * height + metrics.Margin.Top, metrics.CardWidth, metrics.CardHeight));
    }

    void UpdateTarget(Point p, bool copy = false)
    {
        copyDrag = copy;
        gridPointer = p;
        var drag = localLibraryDrag ?? owner.ActiveNativeLibraryDrag;
        var current = directory == null ? null : new LibraryLocation(folder.Id, directory);
        bool sameDirectory = drag != null && current != null && StationWindow.SameLibraryLocation(drag.Source, current);
        desktopDropTarget.Visibility = drag != null && CanMoveInFolder(drag) ? Visibility.Visible : Visibility.Collapsed;
        var spot = drag != null ? FolderMoveSpot(p, drag) : null;
        libraryDrop.Track(spot, copy);
        bool regular = spot == null && sameDirectory && (copy ? FolderCopyPlacement(p, drag!) != null || CanCopyIntoSortedFolder(p, drag!)
            : ContainsGridPoint(p));
        bool carriedToCurrent = spot == null && !sameDirectory && drag != null && current != null && CanDropIntoCurrent(p, drag, current, copy);
        if (regular || carriedToCurrent) DrawDropFrame(TargetAt(p).Frame);
        else dropFrame.Visibility = Visibility.Collapsed;
        if (copy || spot != null || !sameDirectory) folderDrop.Reset();
        bool grouping = !copy && spot == null && sameDirectory && folderDrop.Track(FolderTargetAt(p));
        if (localDrag) copyFeedback.Show(files, p, copy
            ? regular || carriedToCurrent || spot != null ? FolderCopyCaption() : DragCopyFeedback.InvalidTarget : null);
        if (grouping || regular || carriedToCurrent || spot is { IsBreadcrumb: false }) {
            if (!autoScroll.IsRunning) autoScroll.Start();
        } else autoScroll.Stop();
    }

    bool CanMoveInFolder(LibraryDrag drag) => !closed && !busy && !store.ReadOnly && folder.Owned
        && directory != null && StationStore.CanMoveLibraryItems(drag.Items);

    LibraryDropSpot? FolderMoveSpot(Point point, LibraryDrag drag)
    {
        if (!CanMoveInFolder(drag)) return null;
        if (desktopDropTarget.Visibility == Visibility.Visible && LibraryDropCue.Bounds(desktopDropTarget, files).Contains(point)
            && StationStore.CanMoveTo(drag.Source, store.LibraryRoot, drag.Items))
            return new(store.LibraryRoot, desktopDropTarget, true);
        if (point.Y < 0) {
            var parent = string.Equals(directory, folder.Value, StringComparison.OrdinalIgnoreCase)
                ? store.LibraryRoot : new LibraryLocation(folder.Id, Path.GetDirectoryName(directory!)!);
            if (LibraryDropCue.Bounds(back, files).Contains(point) && StationStore.CanMoveTo(drag.Source, parent, drag.Items))
                return new(parent, back, true);
            foreach (var element in StationWindow.Descendants<BreadcrumbBarItem>(breadcrumbs)) {
                if (!element.IsLoaded || element.Content is not Crumb crumb || string.Equals(crumb.Path, directory, StringComparison.OrdinalIgnoreCase)
                    || !LibraryDropCue.Bounds(element, files).Contains(point)) continue;
                var destination = new LibraryLocation(folder.Id, crumb.Path);
                if (StationStore.CanMoveTo(drag.Source, destination, drag.Items)) return new(destination, element, true);
            }
        }
        return LibraryDropCue.FolderAt(files, point, drag.Source, drag.Items, item => new(folder.Id, item.Value), store);
    }

    string FolderCopyCaption() => libraryDrop.Armed?.CopyCaption
        ?? (libraryDrop.HasCandidate ? "停留以复制到文件夹" : "松开以复制");

    LibraryCopyPlacement? FolderCopyPlacement(Point point, LibraryDrag drag)
    {
        if (!ContainsGridPoint(point) || GridLayout is not { } grid || !CanMoveInFolder(drag)
            || !StationStore.CanCopyTo(new(folder.Id, directory!), drag.Items)) return null;
        var (column, row, _) = TargetAt(point);
        var placement = new LibraryCopyPlacement(drag.Anchor, column, row);
        return StationStore.CopyCells(grid, drag.Items.Select(item => item.Id).ToArray(), placement) == null ? null : placement;
    }

    bool CanCopyIntoSortedFolder(Point point, LibraryDrag drag) => CanMoveInFolder(drag) && !FilteringFolder
        && store.SortFolder(folder, directory!) != FolderSort.Manual
        && LibraryDropCue.IsGridBlank(files, point) && StationStore.CanCopyTo(new(folder.Id, directory!), drag.Items);

    sealed record FolderDropPlan(LibraryLocation? Destination = null, Guid? Group = null, (int Column, int Row)? Cell = null,
        bool Copy = false, LibraryCopyPlacement? Placement = null);

    bool CanDropIntoCurrent(Point point, LibraryDrag drag, LibraryLocation current, bool copy) =>
        CanMoveInFolder(drag) && LibraryDropCue.IsGridBlank(files, point)
        && (copy ? StationStore.CanCopyTo(current, drag.Items) : StationStore.CanMoveTo(drag.Source, current, drag.Items));

    FolderDropPlan? ResolveFolderDrop(Point point, LibraryDrag drag, bool copy = false)
    {
        if (closed || busy || store.ReadOnly || directory == null) return null;
        var spot = FolderMoveSpot(point, drag);
        if (spot != null) return libraryDrop.Release(spot) is { } destination ? new(Destination: destination, Copy: copy) : null;
        var current = new LibraryLocation(folder.Id, directory);
        if (!StationWindow.SameLibraryLocation(drag.Source, current))
            return CanDropIntoCurrent(point, drag, current, copy) ? new(Destination: current, Copy: copy) : null;
        if (copy) {
            var placement = FolderCopyPlacement(point, drag);
            return placement != null || CanCopyIntoSortedFolder(point, drag) ? new(Destination: current, Copy: true, Placement: placement) : null;
        }
        if (folderDrop.OnRelease(FolderTargetAt(point)) is { } group) return new(Group: group);
        if (!ContainsGridPoint(point)) return null;
        var (column, row, _) = TargetAt(point);
        return new(Cell: (column, row));
    }

    void ClearLibraryDropTarget(bool keepModifierWatch = false)
    {
        libraryDrop.Clear();
        if (keepModifierWatch) copyFeedback.Show(files, default, null);
        else {
            copyFeedback.Clear();
            copyDrag = false;
        }
        folderDrop.Reset();
        autoScroll.Stop();
        desktopDropTarget.Visibility = Visibility.Collapsed;
        dropFrame.Visibility = Visibility.Collapsed;
    }

    void DrawDropFrame(Rect frame)
    {
        var visible = FolderDropIntent.Intersect(frame, new Rect(0, 0, files.ActualWidth, files.ActualHeight));
        if (visible.Width == 0 || visible.Height == 0) { dropFrame.Visibility = Visibility.Collapsed; return; }
        dropFrame.Width = visible.Width;
        dropFrame.Height = visible.Height;
        dropFrame.Margin = new Thickness(visible.X, visible.Y, 0, 0);
        dropFrame.Visibility = Visibility.Visible;
    }

    GridViewItem? FolderTargetAt(Point point)
    {
        var drag = localLibraryDrag ?? owner.ActiveNativeLibraryDrag;
        if (FilteringFolder || !folder.Owned || store.ReadOnly || directory == null || drag?.Items is not [var item]
            || !StationStore.CanGroupFile(item) || !StationWindow.SameLibraryLocation(drag.Source, new(folder.Id, directory))) return null;
        double size = metrics.IconSize + 2 * StationWindow.DragPreviewFramePadding;
        return FolderDropIntent.FindTarget(files, new Rect(point.X + previewOffset.X, point.Y + previewOffset.Y, size, size),
            item.Id, position => {
                var (column, row, _) = TargetAt(position);
                return (column, row);
            });
    }

    void BeginLocalDrag(GridViewItem card, Point mouse, Point grab)
    {
        if (!card.IsSelected) files.SelectedItem = card;
        var selected = files.SelectedItems.Cast<GridViewItem>().Distinct().ToArray();
        var visual = (StationWindow.Preview)((ContentControl)card.Content).Tag;
        var thumbnail = StationWindow.CreateDragThumbnail(visual, metrics.IconSize, selected.Length, colors);
        double size = metrics.IconSize + 2 * StationWindow.DragPreviewFramePadding;
        var placement = DragPreviewPlacement.From(App.Settings.Data);
        var (anchor, padding) = placement.Layout(size, grab);
        previewOffset = placement.Offset(size, size, grab);
        if (directory == null) return;
        RememberDragOrigin();
        localLibraryDrag = new(new(folder.Id, directory), selected.Select(item => (StationItem)item.Tag).ToArray(), visual.Item.Id);
        visualAnchor = anchor;
        localDrag = true;
        localDragLease = owner.RetainFolderDrag();
        foreach (var item in selected) {
            dragOpacity.Add(item, item.Opacity);
            item.Opacity *= .5;
        }
        var preview = new Border { Child = thumbnail, Padding = padding, RenderTransform = dragPosition };
        dragLayer.Children.Add(preview);
        MoveVisual(mouse);
        previewCapture = StationWindow.CaptureDragPreview(preview, anchor);
        selectionDetails.UpdateSelectionDetails();
    }

    void MoveVisual(Point mouse)
    {
        dragPosition.X = mouse.X - visualAnchor.X;
        dragPosition.Y = mouse.Y - visualAnchor.Y;
    }

    bool CancelLocalDrag()
    {
        if (!localDrag) return false;
        cancelPress?.Invoke();
        FinishLocalDrag();
        return true;
    }

    void FinishLocalDrag(bool handingOff = false)
    {
        localDrag = false;
        localLibraryDrag = null;
        StopBackHover();
        ReleaseCarriedPointer();
        cancelPress = null;
        localDragLease?.Dispose();
        localDragLease = null;
        ClearLibraryDropTarget();
        if (!handingOff) ClearDragVisual();
        if (!closed) selectionDetails.UpdateSelectionDetails();
    }

    void ClearDragVisual()
    {
        previewCapture = null;
        dragLayer.Children.Clear();
        foreach (var (item, opacity) in dragOpacity) item.Opacity = opacity;
        dragOpacity.Clear();
    }

    async Task ApplySystemPreview(DragUI ui)
    {
        var capture = previewCapture;
        if (capture is { IsCompleted: false }) await Task.WhenAny(capture, Task.Delay(100));
        systemPreview = StationWindow.CreateDragBitmap(capture);
        if (systemPreview != null) ui.SetContentFromSoftwareBitmap(systemPreview, capture!.Result!.Anchor);
        ClearDragVisual();
    }

    async Task SavePosition(Guid[] moving, Guid anchor, int column, int row)
    {
        if (directory == null || closed) return;
        using var lease = owner.RetainFolderDrag();
        busy = true;
        try {
            await store.SaveFolderMove(folder, directory, moving, anchor, column, row, VisibleColumns);
            if (!closed) {
                ApplyPositions();
                Notify("已调整位置");
            }
        } catch (Exception error) { if (!closed) Notify(error.Message); }
        finally { busy = false; }
    }

    async Task CommitLocalDrop(Point point, bool copy = false)
    {
        var drag = localLibraryDrag;
        var target = drag == null ? null : ResolveFolderDrop(point, drag, copy);
        FinishLocalDrag();
        await FinishDragNavigation(restore: target == null);
        if (drag != null && target != null) await Run(() => CommitFolderDrop(drag, target));
    }

    async Task CommitFolderDrop(LibraryDrag drag, FolderDropPlan target)
    {
        if (target.Copy) await owner.CopyLibrarySelection(drag.Source, target.Destination!, drag.Items, target.Placement);
        else if (target.Destination != null) await owner.MoveLibrarySelection(drag.Source, target.Destination, drag.Items);
        else if (target.Group is { } group) {
            if (directory == null || closed) return;
            FolderUndo? undo;
            busy = true;
            try { undo = await store.GroupFilesInFolder(folder, directory, drag.Anchor, group, VisibleColumns); }
            finally { busy = false; }
            await RefreshAfterLibraryMove();
            if (!closed) Notify("已创建文件夹", undo);
        } else if (target.Cell is { } cell) {
            await SavePosition(drag.Items.Select(item => item.Id).ToArray(), drag.Anchor, cell.Column, cell.Row);
        }
    }
}
