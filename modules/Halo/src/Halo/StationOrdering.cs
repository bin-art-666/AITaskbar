using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Foundation;

namespace Halo;

internal sealed partial class StationWindow
{
    readonly Border insertion = new() { CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    HashSet<Guid> movingIds = [];
    bool orderRebuildPending, layoutInitializing;
    Guid dragAnchor;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? orderScroll;
    LibraryDropCue libraryDrop = null!;
    DragCopyFeedback copyFeedback = null!;
    bool copyDrag;
    LibraryDrag? carriedFolderDrag;
    Point orderPointer;
    internal static T? Child<T>(DependencyObject parent) where T : DependencyObject {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) return match;
            if (Child<T>(child) is {} found) return found;
        }
        return null;
    }
    int GridColumns() {
        int visible = Math.Clamp((int)(Math.Max(1, files.ActualWidth - files.Padding.Left - files.Padding.Right) / GridAppearance.CellWidth), 1, 64);
        return !Searching && App.Station.Sort(App.Settings.Data.NewestFirst) == StationSort.Manual ? App.Station.Layout(detached, visible, App.Settings.Data.NewestFirst).Columns : visible;
    }
    void ApplyGridPositions()
    {
        if (ShowingSettings || Searching || App.Station.LibraryOffline) return;
        bool manual = App.Station.Sort(App.Settings.Data.NewestFirst) == StationSort.Manual;
        int columns = GridColumns(); var layout = manual ? App.Station.Layout(detached, columns, App.Settings.Data.NewestFirst) : null;
        var positions = layout?.Cells.ToDictionary(c => c.Id, c => c.Row * layout.Columns + c.Column);
        int index = 0;
        foreach (GridViewItem item in files.Items) { StationGridPanel.Position(item, positions?.GetValueOrDefault(((StationItem)item.Tag).Id, index) ?? index, layout?.Columns ?? 0); index++; }
        ScrollViewer.SetHorizontalScrollBarVisibility(files, manual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(files, manual ? ScrollMode.Enabled : ScrollMode.Disabled);
        (files.ItemsPanelRoot as StationGridPanel)?.InvalidateMeasure();
        if (manual && gridPreview == null && files.IsLoaded && files.ActualWidth > 20 && !App.Station.HasLayout(detached) && !layoutInitializing && !App.Station.ReadOnly) {
            layoutInitializing = true; bool expanded = detached;
            _ = Run(async () => { try { await App.Station.EnsureLayout(expanded, columns, App.Settings.Data.NewestFirst); } finally { layoutInitializing = false; } });
        }
    }
    internal (int Column, int Row) GridTarget(Point point, bool show = true)
    {
        var panel = files.ItemsPanelRoot as StationGridPanel;
        int columns = panel?.Columns ?? GridColumns(); double width = panel?.CellWidth ?? GridAppearance.CellWidth, height = panel?.CellHeight ?? GridAppearance.CellHeight;
        var origin = panel?.TransformToVisual(files).TransformPoint(new(0, 0)) ?? new Point(10, 10);
        int column = Math.Clamp((int)Math.Floor((point.X - origin.X) / width), 0, columns - 1);
        int row = Math.Clamp((int)Math.Floor((point.Y - origin.Y) / height), 0, 4095);
        if (show) {
            var metrics = GridAppearance;
            double x = origin.X + column * width + metrics.Margin.Left, y = origin.Y + row * height + metrics.Margin.Top;
            DrawDropFrame(new Rect(x, y, metrics.CardWidth, metrics.CardHeight));
        }
        return (column, row);
    }
    Rect VisibleDropFrame(Rect frame) => FolderDropIntent.Intersect(frame, new Rect(0, 0, files.ActualWidth, files.ActualHeight));
    void DrawDropFrame(Rect frame)
    {
        var visible = VisibleDropFrame(frame);
        if (visible.Width == 0 || visible.Height == 0) { insertion.Visibility = Visibility.Collapsed; return; }
        insertion.Width = visible.Width;
        insertion.Height = visible.Height;
        insertion.Margin = new(visible.X + files.Margin.Left, visible.Y + files.Margin.Top, 0, 0);
        insertion.Visibility = Visibility.Visible;
    }
    internal Guid? InsertionTarget(Point point, bool show = true) {
        var target = GridTarget(point, show); int index = target.Row * GridColumns() + target.Column;
        return index < files.Items.Count ? ((StationItem)((GridViewItem)files.Items[index]).Tag).Id : null;
    }
    void InitializeOrdering()
    {
        Grid.SetRow(insertion, 1); root.Children.Add(insertion);
        InitializeFolderDrop();
        libraryDrop = new(root, () => { if (copyDrag && pointerDragLease != null) UpdateDropTarget(orderPointer, true); });
        copyFeedback = new(root, copy => { if (pointerDragLease != null) UpdateDropTarget(orderPointer, copy); });
        orderScroll = DispatcherQueue.CreateTimer(); orderScroll.Interval = TimeSpan.FromMilliseconds(50);
        orderScroll.Tick += (_, _) => {
            if (Child<ScrollViewer>(files) is not {} scroll) return;
            double delta = orderPointer.Y < 36 ? -18 : orderPointer.Y > files.ActualHeight - 36 ? 18 : 0;
            double dx = scroll.ScrollableWidth <= 0 ? 0 : orderPointer.X < 36 ? -18 : orderPointer.X > files.ActualWidth - 36 ? 18 : 0;
            double x = Math.Clamp(scroll.HorizontalOffset + dx, 0, scroll.ScrollableWidth);
            double y = Math.Clamp(scroll.VerticalOffset + delta, 0, scroll.ScrollableHeight);
            if (scroll.ChangeView(x, y, null, true))
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
                    if (insertion.Visibility == Visibility.Visible || libraryDrop.HasCandidate) UpdateDropTarget(orderPointer, copyDrag);
                });
        };
        root.DragLeave += (_, _) => { if (!outgoingHandoff || flyoutDragBoundary == null) ClearDropTarget(); };
        Closed += (_, _) => ClearDropTarget();
    }
    bool CanReorderGrid => !ShowingSettings && !Searching && !App.Station.ReadOnly;
    bool CanDropInGrid(Point point) => CanReorderGrid
        && point.X >= 0 && point.X < files.ActualWidth && point.Y >= 0 && point.Y < files.ActualHeight;

    void TrackInternalDrop(DragEventArgs e)
    {
        if (outgoingHandoff && nativeReturnCancelled) { e.AcceptedOperation = DataPackageOperation.None; return; }
        var point = InternalDragPoint(e);
        bool copy = e.Modifiers.HasFlag(DragDropModifiers.Control);
        if (!HasLibraryDragIdentity(e.DataView) || ActiveNativeLibraryDrag is not { } drag || !UpdateDropTarget(point, copy)) {
            e.AcceptedOperation = DataPackageOperation.None;
            if (copy) { e.DragUIOverride.Caption = DragCopyFeedback.InvalidTarget; e.DragUIOverride.IsCaptionVisible = true; }
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsGlyphVisible = copy;
        e.DragUIOverride.Caption = copy ? CopyDropCaption() : libraryDrop.Armed?.Caption ?? (libraryDrop.HasCandidate ? "停留以移入文件夹"
            : armedFolderDrop.HasValue ? FolderDropCaption : SameLibraryLocation(drag.Source, App.Station.LibraryRoot) ? "调整位置" : "松开以移动到中转站");
        e.DragUIOverride.IsCaptionVisible = true;
    }
    LibraryDropSpot? MainFolderSpot(Point point)
    {
        if (ShowingSettings) return null;
        var session = ActiveNativeLibraryDrag ?? carriedFolderDrag;
        var source = session?.Source ?? App.Station.LibraryRoot;
        var items = session?.Items ?? App.Station.Items.Where(item => movingIds.Contains(item.Id)).ToArray();
        return LibraryDropCue.FolderAt(files, point, source, items, item => new(item.Id, item.Value), App.Station);
    }

    string CopyDropCaption() => libraryDrop.Armed?.CopyCaption
        ?? (libraryDrop.HasCandidate ? "停留以复制到文件夹" : "松开以复制");

    LibraryCopyPlacement? MainCopyPlacement(Point point, StationItem[] items, Guid anchor)
    {
        if (!CanDropInGrid(point) || App.Station.Sort(App.Settings.Data.NewestFirst) != StationSort.Manual
            || !StationStore.CanCopyTo(App.Station.LibraryRoot, items)) return null;
        var (column, row) = GridTarget(point, false);
        var placement = new LibraryCopyPlacement(anchor, column, row, detached, App.Settings.Data.NewestFirst);
        return StationStore.CopyCells(App.Station.Layout(detached, GridColumns(), placement.NewestFirst),
            items.Select(item => item.Id).ToArray(), placement) == null ? null : placement;
    }

    bool CanCopyIntoSortedMain(Point point, StationItem[] items) => CanReorderGrid
        && App.Station.Sort(App.Settings.Data.NewestFirst) != StationSort.Manual
        && LibraryDropCue.IsGridBlank(files, point) && StationStore.CanCopyTo(App.Station.LibraryRoot, items);

    bool UpdateDropTarget(Point point, bool copy = false)
    {
        copyDrag = copy;
        orderPointer = point;
        var native = ActiveNativeLibraryDrag;
        var session = native ?? carriedFolderDrag;
        bool sameDirectory = session == null || SameLibraryLocation(session.Source, App.Station.LibraryRoot);
        bool movingToFolder = libraryDrop.Track(MainFolderSpot(point), copy);
        var items = copy ? session?.Items ?? App.Station.Items.Where(item => movingIds.Contains(item.Id)).ToArray() : [];
        bool regularDrop = !movingToFolder && sameDirectory
            && (copy ? MainCopyPlacement(point, items, session?.Anchor ?? dragAnchor) != null || CanCopyIntoSortedMain(point, items)
                : CanDropInGrid(point));
        bool movingToRoot = !movingToFolder && !sameDirectory && !ShowingSettings && !App.Station.ReadOnly
            && CanDropFolderSelectionAtDesktop(session!, point, copy);
        if (regularDrop || movingToRoot) GridTarget(point);
        else insertion.Visibility = Visibility.Collapsed;
        if (!copy && sameDirectory && !movingToFolder) TrackFolderDrop(point);
        else ResetFolderDrop();
        if (native == null) copyFeedback.Show(files, point, copy
            ? regularDrop || movingToFolder || movingToRoot ? CopyDropCaption() : DragCopyFeedback.InvalidTarget : null);
        if (regularDrop || movingToFolder || movingToRoot) {
            if (orderScroll is { IsRunning: false }) orderScroll.Start();
        } else orderScroll?.Stop();
        return regularDrop || movingToFolder || movingToRoot || folderDropCandidate.HasValue;
    }

    bool CanDropFolderSelectionAtDesktop(LibraryDrag drag, Point point, bool copy) =>
        StationStore.CanMoveLibraryItems(drag.Items) && (copy || folderBrowser?.ShowingDesktopDuringDrag == true
            ? LibraryDropCue.IsGridBlank(files, point) && (!copy || StationStore.CanCopyTo(App.Station.LibraryRoot, drag.Items))
            : new Rect(0, 0, files.ActualWidth, files.ActualHeight).Contains(point));

    internal void TrackCarriedFolderDrag(LibraryDrag drag, Point point, bool copy)
    {
        carriedFolderDrag = drag;
        UpdateDropTarget(point, copy);
    }

    internal LibraryLocation? DesktopDestinationForFolderDrag(LibraryDrag drag, Point point, bool copy)
    {
        var spot = MainFolderSpot(point);
        return spot != null ? libraryDrop.Release(spot)
            : !App.Station.ReadOnly && CanDropFolderSelectionAtDesktop(drag, point, copy) ? App.Station.LibraryRoot : null;
    }

    internal void ClearCarriedFolderDrop()
    {
        if (carriedFolderDrag == null) return;
        carriedFolderDrag = null;
        ClearDropTarget();
    }

    internal Task CommitCarriedFolderDrag(LibraryDrag drag, LibraryLocation destination, bool copy) => Run(() =>
        copy ? CopyLibrarySelection(drag.Source, destination, drag.Items) : MoveLibrarySelection(drag.Source, destination, drag.Items));
    async Task CompleteInternalDrop(DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.None;
        if (outgoingHandoff && nativeReturnCancelled || !HasLibraryDragIdentity(e.DataView)
            || ActiveNativeLibraryDrag is not { HandledInside: false } drag) return;
        var point = InternalDragPoint(e);
        if (App.Verification) TraceInternalDrop("XAML", point);
        var commit = PrepareInternalDrop(drag, point, e.Modifiers.HasFlag(DragDropModifiers.Control));
        if (commit is null) return;
        var deferral = e.GetDeferral();
        try {
            if (!await MatchesNativeLibraryDrag(e.DataView, drag) || drag.HandledInside) return;
            drag.HandledInside = true;
            await commit();
            e.AcceptedOperation = DataPackageOperation.Copy;
        } catch (Exception error) { e.AcceptedOperation = DataPackageOperation.None; Notify(error.Message, true); }
        finally { deferral.Complete(); }
    }

    Point InternalDragPoint(DragEventArgs e) => TaskbarFlyout is not null && outgoingHandoff && GetCursorPos(out var cursor)
        ? FlyoutGridPoint(cursor.X, cursor.Y) : e.GetPosition(files);

    void TraceInternalDrop(string route, Point point)
    {
        GetCursorPos(out var cursor);
        var grid = GridTarget(point, false);
        File.AppendAllText(Path.Combine(App.DataDirectory, "halo-flyout-progress.log"),
            $"{route} internal drop: {point.X:F1},{point.Y:F1}, actual screen {cursor.X},{cursor.Y}, cell {grid}\n");
    }

    Func<Task>? PrepareInternalDrop(LibraryDrag drag, Point point, bool copy)
    {
        var target = GridTarget(point, false);
        var folderTarget = FolderDropOnRelease(point);
        var spot = MainFolderSpot(point);
        var destination = libraryDrop.Release(spot);
        ClearDropTarget();
        if (spot != null && destination == null) return null;
        var sameDirectory = SameLibraryLocation(drag.Source, App.Station.LibraryRoot);
        var placement = copy && sameDirectory && destination == null ? MainCopyPlacement(point, drag.Items, drag.Anchor) : null;
        if (placement != null || copy && sameDirectory && destination == null && CanCopyIntoSortedMain(point, drag.Items))
            destination = App.Station.LibraryRoot;
        if (destination == null && !sameDirectory && !ShowingSettings && !App.Station.ReadOnly
            && CanDropFolderSelectionAtDesktop(drag, point, copy)) destination = App.Station.LibraryRoot;
        if (copy) return destination is null ? null : () => CopyLibrarySelection(drag.Source, destination, drag.Items, placement);
        if (destination != null) return () => MoveLibrarySelection(drag.Source, destination, drag.Items);
        return sameDirectory && (CanDropInGrid(point) || folderTarget != null)
            ? () => CommitGridDrop(drag.Items.Select(i => i.Id).ToArray(), target, folderTarget) : null;
    }
    async Task MoveDraggedItems(Guid[] ids, (int Column, int Row) target)
    {
        await App.Station.MoveGrid(ids, dragAnchor, target.Column, target.Row, detached, GridColumns(), App.Settings.Data.NewestFirst);
        Notify("已调整位置");
    }
    async Task CommitGridDrop(Guid[] ids, (int Column, int Row) target, Guid? folderTarget)
    {
        if (folderTarget is { } folder) {
            if (ids.Length != 1 || ids[0] != dragAnchor) throw new InvalidDataException("拖动项目无效。");
            await App.Station.GroupFiles(ids[0], folder, detached, GridColumns());
            Notify("已创建文件夹");
        } else await MoveDraggedItems(ids, target);
    }
    void ClearDropTarget()
    {
        insertion.Visibility = Visibility.Collapsed;
        libraryDrop.Clear();
        copyFeedback.Clear();
        copyDrag = false;
        ResetFolderDrop();
        orderScroll?.Stop();
    }
    void EndOrdering()
    {
        ClearDropTarget();
        movingIds.Clear();
        QueuePendingCardRebuild();
    }
    void QueuePendingCardRebuild()
    {
        if (!orderRebuildPending || cardPress != null || movingIds.Count != 0 || outgoingHandoff) return;
        Changed();
    }
}
