using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Halo;

internal sealed partial class FolderBrowser
{
    enum BackHover { None, Button, OutsidePanel }
    sealed record DragOrigin(string Directory, Point Scroll, string[] Selection, Guid? Anchor, string Search, bool SearchVisible);

    readonly ContentControl nativeCarrier = new() { IsHitTestVisible = false, IsTabStop = false };
    readonly Border backHint = new() {
        Child = new TextBlock { Text = "继续按住，返回上一级", Padding = new(8, 4, 8, 4), FontSize = 12,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(8, 6, 8, 0),
        CornerRadius = new(8), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    Microsoft.UI.Dispatching.DispatcherQueueTimer backHoverTimer = null!;
    Pointer? carriedPointer;
    ContentControl? originGrip;
    LibraryDrag? awaitingNativeDrag;
    DragOrigin? dragOrigin;
    Task pendingDragBack = Task.CompletedTask;
    Point dragMouse;
    BackHover backHover;
    long backHoverStarted;
    bool backNeedsReentry;
    bool nativeFinished;
    internal bool ShowingDesktopDuringDrag { get; private set; }

    void InitializeDragNavigation()
    {
        Children.Insert(0, nativeCarrier);
        SetRow(backHint, GetRow(files));
        root.Children.Add(backHint);
        nativeCarrier.DragStarting += async (_, e) => {
            if (awaitingNativeDrag is { } drag) await StartNativePayload(e, drag);
            else e.Cancel = true;
        };
        nativeCarrier.DropCompleted += async (_, e) => await FinishNativeNavigation(e.DropResult);
        AddHandler(PointerMovedEvent, new PointerEventHandler(ContinueCarriedDrag), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(ReleaseCarriedDrag), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(CarriedPointerLost), true);
        PointerCaptureLost += (sender, e) => {
            if (ReferenceEquals(e.OriginalSource, this)) CarriedPointerLost(sender, e);
        };
        backHoverTimer = DispatcherQueue.CreateTimer();
        backHoverTimer.IsRepeating = false;
        backHoverTimer.Tick += async (_, _) => await TryFinishBackHover();
    }

    bool CarryPointer(Pointer pointer)
    {
        if (!CapturePointer(pointer)) return false;
        carriedPointer = pointer;
        return true;
    }

    void ReleaseCarriedPointer()
    {
        var pointer = carriedPointer;
        carriedPointer = null;
        if (pointer != null) ReleasePointerCapture(pointer);
    }

    async void ContinueCarriedDrag(object sender, PointerRoutedEventArgs e)
    {
        if (carriedPointer?.PointerId != e.Pointer.PointerId || localLibraryDrag is not { } drag) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) { CancelNavigationDrag(); return; }
        e.Handled = true;
        dragMouse = point.Position;
        MoveVisual(dragMouse);
        if (InsideStation(dragMouse)) {
            TrackBackHover(dragMouse, drag);
            TrackCarriedDrop(e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control));
            return;
        }
        StopBackHover();
        awaitingNativeDrag = drag;
        nativeFinished = false;
        using var lease = owner.RetainFolderDrag();
        var carrier = !ShowingDesktopDuringDrag && originGrip is { IsLoaded: true } grip ? grip : nativeCarrier;
        var nativePoint = e.GetCurrentPoint(carrier);
        owner.ClearCarriedFolderDrop();
        FinishLocalDrag(handingOff: true);
        carrier.CanDrag = true;
        try { await FinishNativeNavigation(await carrier.StartDragAsync(nativePoint)); }
        catch (Exception error) {
            if (!closed) Notify(error.Message);
            await FinishNativeNavigation(DataPackageOperation.None);
        } finally {
            carrier.CanDrag = false;
            awaitingNativeDrag = null;
        }
    }

    async void ReleaseCarriedDrag(object sender, PointerRoutedEventArgs e)
    {
        if (carriedPointer?.PointerId != e.Pointer.PointerId || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        bool copy = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        if (ShowingDesktopDuringDrag && localLibraryDrag is { } drag) {
            var destination = owner.DesktopDestinationForFolderDrag(drag, e.GetCurrentPoint(owner.Files).Position, copy);
            FinishLocalDrag();
            if (destination == null) { await FinishDragNavigation(restore: true); return; }
            await FinishDragNavigation(restore: false);
            await owner.CommitCarriedFolderDrag(drag, destination, copy);
            return;
        }
        await CommitLocalDrop(e.GetCurrentPoint(files).Position, copy);
    }

    void CarriedPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (carriedPointer?.PointerId == e.Pointer.PointerId) CancelNavigationDrag();
    }

    internal bool CancelNavigationDrag()
    {
        if (!CancelLocalDrag()) return false;
        _ = FinishDragNavigation(restore: true);
        return true;
    }

    void TrackCarriedDrop(bool copy)
    {
        copyDrag = copy;
        if (localLibraryDrag is not { } drag) return;
        if (ShowingDesktopDuringDrag) {
            owner.TrackCarriedFolderDrag(drag, TransformToVisual(owner.Files).TransformPoint(dragMouse), copy);
            return;
        }
        UpdateTarget(TransformToVisual(files).TransformPoint(dragMouse), copy);
    }

    bool InsideStation(Point point) => point.X >= 0 && point.Y >= 0 && point.X < ActualWidth && point.Y < ActualHeight;

    BackHover BackRegion(Point point)
    {
        if (ShowingDesktopDuringDrag || !InsideStation(point) || !root.IsLoaded) return BackHover.None;
        if (LibraryDropCue.Bounds(back, this).Contains(point)) return BackHover.Button;
        return LibraryDropCue.Bounds(root, this).Contains(point) ? BackHover.None : BackHover.OutsidePanel;
    }

    void TrackBackHover(Point point, LibraryDrag drag)
    {
        dragMouse = point;
        var region = BackRegion(point);
        if (region == BackHover.None) backNeedsReentry = false;
        if (region == BackHover.None || backNeedsReentry || !pendingDragBack.IsCompleted || !CanMoveInFolder(drag)) {
            StopBackHover();
            return;
        }
        if (region == backHover) return;
        backHover = region;
        backHoverStarted = Stopwatch.GetTimestamp();
        backHoverTimer.Interval = BackHoverDelay(region);
        backHoverTimer.Start();
        backHint.Visibility = Visibility.Visible;
    }

    static TimeSpan BackHoverDelay(BackHover region) =>
        TimeSpan.FromMilliseconds(region == BackHover.Button ? 350 : 400);

    async Task TryFinishBackHover()
    {
        if (backHover == BackHover.None || (localLibraryDrag ?? nativeLibraryDrag) is not { } drag
            || backHover != BackRegion(dragMouse) || !CanMoveInFolder(drag)) { StopBackHover(); return; }
        var remaining = BackHoverDelay(backHover) - Stopwatch.GetElapsedTime(backHoverStarted);
        if (remaining > TimeSpan.Zero) {
            backHoverTimer.Interval = remaining;
            backHoverTimer.Start();
            return;
        }
        StopBackHover();
        backNeedsReentry = true;
        pendingDragBack = BackWhileDragging(drag);
        await pendingDragBack;
        if (ReferenceEquals(localLibraryDrag, drag)) TrackCarriedDrop(copyDrag);
    }

    async Task BackWhileDragging(LibraryDrag drag)
    {
        if (directory == null) return;
        ClearLibraryDropTarget(keepModifierWatch: true);
        if (string.Equals(directory, folder.Value, StringComparison.OrdinalIgnoreCase)) {
            ShowingDesktopDuringDrag = true;
            shade.Visibility = root.Visibility = Visibility.Collapsed;
            owner.Files.Focus(FocusState.Programmatic);
        } else await Navigate(Path.GetDirectoryName(directory)!, duringDrag: drag);
    }

    void StopBackHover()
    {
        backHoverTimer.Stop();
        backHover = BackHover.None;
        backHint.Visibility = Visibility.Collapsed;
    }

    void RememberDragOrigin()
    {
        if (directory == null) return;
        var scroll = StationWindow.Child<ScrollViewer>(files);
        dragOrigin = new(directory, new(scroll?.HorizontalOffset ?? 0, scroll?.VerticalOffset ?? 0),
            SelectedEntries().Select(item => item.Value).ToArray(), selectionAnchor, folderSearch.Text,
            folderSearch.Visibility == Visibility.Visible);
        backNeedsReentry = false;
    }

    async Task FinishDragNavigation(bool restore)
    {
        var origin = dragOrigin;
        dragOrigin = null;
        originGrip = null;
        using var lease = restore && origin != null ? owner.RetainFolderDrag() : null;
        StopBackHover();
        owner.ClearCarriedFolderDrop();
        await pendingDragBack;
        if (closed) return;
        if (ShowingDesktopDuringDrag) {
            ShowingDesktopDuringDrag = false;
            if (!restore) { owner.CloseFolderBrowser(this); return; }
            shade.Visibility = root.Visibility = Visibility.Visible;
        }
        if (!restore || origin == null) return;
        if (!string.Equals(directory, origin.Directory, StringComparison.OrdinalIgnoreCase))
            await Navigate(origin.Directory);
        if (closed || !string.Equals(directory, origin.Directory, StringComparison.OrdinalIgnoreCase)) return;
        folderSearch.Text = origin.Search;
        folderSearch.Visibility = origin.SearchVisible ? Visibility.Visible : Visibility.Collapsed;
        RenderFolderEntries(origin.Selection);
        selectionAnchor = origin.Anchor;
        files.UpdateLayout();
        StationWindow.FocusGridSelection(files);
        StationWindow.Child<ScrollViewer>(files)?.ChangeView(origin.Scroll.X, origin.Scroll.Y, null, true);
    }

    async Task FinishNativeNavigation(DataPackageOperation result)
    {
        if (nativeFinished) return;
        nativeFinished = true;
        EndDrag();
        await FinishDragNavigation(restore: result == DataPackageOperation.None);
    }
}
