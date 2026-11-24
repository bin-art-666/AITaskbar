using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Microsoft.UI.Input.DragDrop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.Foundation;

namespace Halo;
internal sealed partial class StationWindow
{
    Guid? selectionAnchor;
    enum ReleaseSelection { None, Single, Toggle }
    sealed record CardPress(GridViewItem Card, uint Pointer, ReleaseSelection Release, Point Origin = default, Point Grab = default);
    sealed record PreparedDrag(StationItem[] Items, Task<IStorageItem[]> Files);
    CardPress? cardPress;
    PreparedDrag? preparedDrag;
    IDisposable? pointerDragLease;
    bool outgoingHandoff;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? flyoutDragBoundary;
    Native.Point flyoutCardOrigin;
    PointerPoint? flyoutPointer;
    string? verifiedShellExport;
    GridViewItem[] SelectionOrder() => files.Items.Cast<GridViewItem>().OrderBy(StationGridPanel.Cell).ToArray();
    internal void SelectCard(GridViewItem card, bool control, bool shift) => SelectGridCard(files, card, control, shift, ref selectionAnchor);
    internal static void SelectGridCard(GridView files, GridViewItem card, bool control, bool shift, ref Guid? anchorId) {
        if (shift) {
            var ordered = files.Items.Cast<GridViewItem>().OrderBy(StationGridPanel.Cell).ToArray();
            int clicked = Array.IndexOf(ordered, card);
            if (clicked < 0) return;
            var id = anchorId;
            int anchor = Array.FindIndex(ordered, i => ((StationItem)i.Tag).Id == id);
            if (anchor < 0) anchor = clicked;
            if (!control) files.SelectedItems.Clear();
            for (int index = Math.Min(anchor, clicked); index <= Math.Max(anchor, clicked); index++) if (!files.SelectedItems.Contains(ordered[index])) files.SelectedItems.Add(ordered[index]);
        } else {
            if (!files.Items.Contains(card)) return;
            if (control) { if (files.SelectedItems.Contains(card)) files.SelectedItems.Remove(card); else files.SelectedItems.Add(card); }
            else if (files.SelectedItems.Count != 1 || !files.SelectedItems.Contains(card)) {
                files.SelectedItems.Clear(); files.SelectedItems.Add(card);
            }
            anchorId = ((StationItem)card.Tag).Id;
        }
    }
    void InitializeCardSelection()
    {
        InitializeDragPreview();
        if (TaskbarFlyout != null)
        {
            flyoutDragBoundary = DispatcherQueue.CreateTimer();
            flyoutDragBoundary.Interval = TimeSpan.FromMilliseconds(20);
            flyoutDragBoundary.Tick += (_, _) => TrackFlyoutCard();
            Closed += (_, _) => flyoutDragBoundary.Stop();
        }
        root.PreviewKeyDown += (_, e) => {
            if (pointerDragLease == null || e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            CancelPointerDrag();
        };
        files.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => ReleaseCardPress(e)), true);
        files.PointerExited += ClearReleasedPress;
        files.PointerEntered += ClearReleasedPress;
        void ClearReleasedPress(object sender, PointerRoutedEventArgs e) {
            if (flyoutDragBoundary?.IsRunning == true) return;
            if (cardPress?.Pointer != e.Pointer.PointerId || e.GetCurrentPoint(files).Properties.IsLeftButtonPressed) return;
            preparedDrag = null;
            EndCardPress();
        }
    }
    void WireCardSelection(ContentControl grip, GridViewItem card) {
        // Handle at the inner drag surface, before GridViewItem's default selection handler.
        var pressHandler = new PointerEventHandler((sender, e) => {
            var point = e.GetCurrentPoint(card);
            if (!IsWithinCard(point.Position, card) || ReferenceEquals(sender, card) && cardPress is { } active
                && active.Card == card && active.Pointer == e.Pointer.PointerId) return;
            if (!point.Properties.IsLeftButtonPressed ||
                point.PointerDeviceType == PointerDeviceType.Mouse && point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
            e.Handled = true;
            if (ModalActive || DragActive || changingMode || resizing) return;
            bool control = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
            bool mouse = point.PointerDeviceType == PointerDeviceType.Mouse;
            grip.CanDrag = !mouse;
            card.Focus(FocusState.Pointer);
            bool selected = files.SelectedItems.Contains(card);
            var release = selected && !shift
                ? control ? ReleaseSelection.Toggle : files.SelectedItems.Count > 1 ? ReleaseSelection.Single : ReleaseSelection.None
                : ReleaseSelection.None;
            var thumbnail = (FrameworkElement)((StackPanel)grip.Content).Children[0];
            var grab = e.GetCurrentPoint(thumbnail).Position;
            cardPress = new(card, e.Pointer.PointerId, release, e.GetCurrentPoint(root).Position,
                new(Math.Clamp((grab.X + DragPreviewFramePadding) / (thumbnail.ActualWidth + 2 * DragPreviewFramePadding), 0, 1),
                    Math.Clamp((grab.Y + DragPreviewFramePadding) / (thumbnail.ActualHeight + 2 * DragPreviewFramePadding), 0, 1)));
            if (release == ReleaseSelection.None) SelectCard(card, control, shift);
            preparedDrag = null;
            if (mouse) {
                if (flyoutDragBoundary != null) flyoutPointer = e.GetCurrentPoint(grip);
                if (!grip.CapturePointer(e.Pointer) && flyoutDragBoundary == null) grip.CanDrag = true;
                if (flyoutDragBoundary != null && GetCursorPos(out flyoutCardOrigin)) flyoutDragBoundary.Start();
                var selection = SelectedCards().Select(i => (Preview)((ContentControl)i.Content).Tag).ToArray();
                if (selection.Length > 0 && selection.All(p => p.Item.Owned && p.Item.Kind is StationKind.File or StationKind.Folder)) {
                    var preparation = Task.WhenAll(selection.Select(p => p.DragFile is { } file
                        ? Task.FromResult(file) : StationTransfer.Storage(p.Item)));
                    // A click can finish without consuming the drag payload.
                    _ = preparation.ContinueWith(static task => { _ = task.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    preparedDrag = new(selection.Select(p => p.Item).ToArray(), preparation);
                }
            }
        });
        grip.AddHandler(UIElement.PointerPressedEvent, pressHandler, true);
        card.AddHandler(UIElement.PointerPressedEvent, pressHandler, true);
        grip.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => {
            if (pointerDragLease != null && cardPress?.Pointer == e.Pointer.PointerId &&
                !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) {
                e.Handled = true;
                _ = CommitPointerDrag(e.GetCurrentPoint(files).Position, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control));
                return;
            }
            var point = e.GetCurrentPoint(card).Position;
            if (IsWithinCard(point, card))
                ReleaseCardPress(e, card);
            else ReleaseCardPress(e);
        }), true);
        grip.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(async (_, e) => {
            if (grip.CanDrag || cardPress is not { } press || press.Card != card || press.Pointer != e.Pointer.PointerId) return;
            var point = e.GetCurrentPoint(root);
            if (!point.Properties.IsLeftButtonPressed) return;
            if (flyoutDragBoundary != null) flyoutPointer = e.GetCurrentPoint(grip);
            var position = point.Position;
            if (pointerDragLease == null) {
                double dx = position.X - press.Origin.X, dy = position.Y - press.Origin.Y;
                if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
                StartPointerOrdering(press, position, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control));
            }
            if (position.X >= 0 && position.X < root.ActualWidth && position.Y >= 0 && position.Y < root.ActualHeight) {
                MoveDragPreview(position);
                UpdateDropTarget(e.GetCurrentPoint(files).Position, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control));
                return;
            }
            await HandOffCard(card, grip, e.GetCurrentPoint(grip));
        }), true);
        grip.PointerCanceled += Cancel;
        grip.PointerCaptureLost += Cancel;
        void Cancel(object sender, PointerRoutedEventArgs e) {
            if (flyoutDragBoundary?.IsRunning == true) return;
            if (cardPress is not { } press || press.Card != card || press.Pointer != e.Pointer.PointerId) return;
            if (pointerDragLease != null) CancelPointerDrag();
            else EndCardPress();
        }
    }

    void StartPointerOrdering(CardPress press, Point position, bool copy)
    {
        if (!files.SelectedItems.Contains(press.Card)) SelectCard(press.Card, false, false);
        movingIds = Selected().Select(i => i.Id).ToHashSet();
        dragAnchor = ((StationItem)press.Card.Tag).Id;
        pointerDragLease = KeepOpenForDrag();
        ShowDragPreview((Preview)((ContentControl)press.Card.Content).Tag, position, movingIds.Count, press.Grab);
        copyFeedback.Watch(copy);
    }

    void TrackFlyoutCard()
    {
        if (cardPress is not { } press || outgoingHandoff || !GetCursorPos(out var cursor)) return;
        var bounds = CurrentBounds();
        double scale = WindowLayoutScale;
        var position = new Point((cursor.X - bounds.X) / scale, (cursor.Y - bounds.Y) / scale);
        var gridOrigin = files.TransformToVisual(root).TransformPoint(default);
        Point gridPoint = new(position.X - gridOrigin.X, position.Y - gridOrigin.Y);
        bool copy = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
        if ((Native.GetAsyncKeyState(1) & 0x8000) == 0)
        {
            if (pointerDragLease != null) _ = CommitPointerDrag(gridPoint, copy);
            else
            {
                var cardOrigin = press.Card.TransformToVisual(root).TransformPoint(default);
                var inCard = IsWithinCard(new(position.X - cardOrigin.X, position.Y - cardOrigin.Y), press.Card);
                CompleteCardPress(press, inCard ? press.Card : null);
            }
            return;
        }
        if (pointerDragLease is null)
        {
            if (Math.Max(Math.Abs(cursor.X - flyoutCardOrigin.X), Math.Abs(cursor.Y - flyoutCardOrigin.Y)) < 4 * scale) return;
            StartPointerOrdering(press, position, copy);
        }
        if (position.X >= 0 && position.Y >= 0 && position.X < root.ActualWidth && position.Y < root.ActualHeight)
        {
            MoveDragPreview(position);
            UpdateDropTarget(gridPoint, copy);
        }
        else if (flyoutPointer is { } pointer)
            _ = HandOffCard(press.Card, (ContentControl)press.Card.Content, pointer);
    }

    async Task HandOffCard(GridViewItem card, ContentControl grip, PointerPoint? nativePoint)
    {
        if (outgoingHandoff) return;
        var items = Selected();
        var preparation = preparedDrag is { } pending && pending.Items.SequenceEqual(items) ? pending.Files : null;
        outgoingHandoff = true;
        flyoutDragBoundary?.Stop();
        using var handoffLease = KeepOpenForDrag();
        FinishPointerDrag(clearPreparation: false);
        try
        {
            if (TaskbarFlyout != null && nativePoint is not null)
            {
                using var operation = new DragOperation { AllowedOperations = DataPackageOperation.Copy };
                await StationTransfer.FillMany(operation.Data, items, App.Station.AttachmentDirectory,
                    exportNoteFiles: true, preparedFiles: preparation);
                if ((Native.GetAsyncKeyState(1) & 0x8000) == 0) return;
                var island = grip.XamlRoot?.ContentIsland;
                if (island is null) throw new InvalidOperationException("无法找到 Halo 拖拽源的输入宿主。");
                using var manager = DragDropManager.GetForIsland(island);
                outgoingPreview = ApplyDragBitmap(dragPreviewCapture, (bitmap, anchor) => operation.SetDragUIContentFromSoftwareBitmap(bitmap, anchor));
                HideDragPreview();
                if (App.Verification) RecordExport("starting WinUI drag");
                var result = await operation.StartAsync(manager, nativePoint);
                if (App.Verification) RecordExport(result.ToString());
                Notify(result == DataPackageOperation.Copy ? "已交给目标窗口，记录保留" : "已取消拖动，记录保留");
            }
            else if (nativePoint is not null)
            {
                grip.CanDrag = true;
                await grip.StartDragAsync(nativePoint);
            }
        }
        catch (Exception error) { if (App.Verification) RecordExport(error.ToString()); Notify(error.Message, true); }
        finally
        {
            if (ActiveNativeLibraryDrag is { Source.RootId: null } nativeDrag && nativeDrag.Anchor == ((StationItem)card.Tag).Id)
                EndNativeLibraryDrag(nativeDrag);
            outgoingPreview?.Dispose();
            outgoingPreview = null;
            HideDragPreview();
            outgoingHandoff = false;
            grip.CanDrag = false;
            preparedDrag = null;
            QueuePendingCardRebuild();
            UpdateSelectionDetails();
        }
    }

    void RecordExport(string value)
    {
        verifiedShellExport = value;
        File.AppendAllText(Path.Combine(App.DataDirectory, "halo-flyout-progress.log"), $"shell export: {value}\n");
    }
    static bool IsWithinCard(Point point, GridViewItem card) =>
        point.X >= 0 && point.X < card.ActualWidth && point.Y >= 0 && point.Y < card.ActualHeight;
    async Task CommitPointerDrag(Point point, bool copy = false)
    {
        var grip = (ContentControl)cardPress!.Card.Content;
        var ids = movingIds.ToArray();
        var items = App.Station.Items.Where(item => movingIds.Contains(item.Id)).ToArray();
        var target = GridTarget(point, false);
        var folderTarget = FolderDropOnRelease(point);
        var spot = MainFolderSpot(point);
        var destination = libraryDrop.Release(spot);
        var placement = copy && spot == null ? MainCopyPlacement(point, items, dragAnchor) : null;
        bool copyIntoCurrent = placement != null || copy && spot == null && CanCopyIntoSortedMain(point, items);
        bool canDrop = spot == null && (CanDropInGrid(point) || folderTarget.HasValue);
        var lease = pointerDragLease!;
        pointerDragLease = null;
        EndCardPress();
        grip.ReleasePointerCaptures();
        HideDragPreview();
        ClearDropTarget();
        try {
            if (copy) {
                if (destination != null || copyIntoCurrent)
                    await CopyLibrarySelection(App.Station.LibraryRoot, destination ?? App.Station.LibraryRoot, items, placement);
            } else if (destination != null) await MoveLibrarySelection(App.Station.LibraryRoot, destination, items);
            else if (canDrop) await CommitGridDrop(ids, target, folderTarget);
        }
        catch (Exception error) { Notify(error.Message, true); }
        finally { lease.Dispose(); preparedDrag = null; EndOrdering(); UpdateSelectionDetails(); }
    }
    void CancelPointerDrag() => FinishPointerDrag(clearPreparation: true);
    void FinishPointerDrag(bool clearPreparation)
    {
        var grip = cardPress?.Card.Content as ContentControl;
        EndCardPress();
        grip?.ReleasePointerCaptures();
        if (clearPreparation) {
            HideDragPreview();
            preparedDrag = null;
        }
        pointerDragLease?.Dispose();
        pointerDragLease = null;
        EndOrdering();
        UpdateSelectionDetails();
    }
    void ReleaseCardPress(PointerRoutedEventArgs e, GridViewItem? card = null)
    {
        if (cardPress is not { } press || press.Pointer != e.Pointer.PointerId || e.GetCurrentPoint(files).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        CompleteCardPress(press, card);
    }
    void CompleteCardPress(CardPress press, GridViewItem? card)
    {
        if (card != null && press.Card == card && !DragActive && press.Release != ReleaseSelection.None)
            SelectCard(card, press.Release == ReleaseSelection.Toggle, false);
        preparedDrag = null;
        EndCardPress();
    }
    void EndCardPress()
    {
        flyoutDragBoundary?.Stop();
        flyoutPointer = null;
        cardPress = null;
        QueuePendingCardRebuild();
    }
    void ClearDesktopSelection() {
        files.SelectedItems.Clear(); selectionAnchor = null; preparedDrag = null; EndCardPress();
    }
    internal void VerifyCardSelection() {
        var cards = SelectionOrder(); if (cards.Length < 3) throw new Exception("多选检查需要至少三项。");
        var first = cards[0];
        var toWindow = first.TransformToVisual(null);
        bool HitsCard(Point point) => VisualTreeHelper.FindElementsInHostCoordinates(toWindow.TransformPoint(point), root)
            .OfType<GridViewItem>().Any();
        if (!new[] { new Point(1, first.ActualHeight / 2), new Point(first.ActualWidth - 1, first.ActualHeight / 2),
            new Point(first.ActualWidth / 2, first.ActualHeight - 2), new Point(first.ActualWidth / 2, 12) }.All(HitsCard)
            || HitsCard(new(first.ActualWidth + first.Margin.Right, first.ActualHeight / 2)))
            throw new Exception("卡片图标、文字、留白与格间空隙的命中边界不正确。");
        var previous = Selected().Select(i => i.Id).ToHashSet();
        for (int i = 0; i < 30; i++) {
            SelectCard(cards[0], false, false); SelectCard(cards[1], true, false);
            if (files.SelectedItems.Count != 2) throw new Exception("Ctrl 添加选择失败。");
            SelectCard(cards[0], true, false); if (files.SelectedItems.Count != 1 || !files.SelectedItems.Contains(cards[1])) throw new Exception("Ctrl 取消选择失败。");
            SelectCard(cards[2], false, false); if (files.SelectedItems.Count != 1 || !files.SelectedItems.Contains(cards[2])) throw new Exception("普通点击切换选择失败。");
            SelectCard(cards[0], false, false); SelectCard(cards[2], false, true); if (files.SelectedItems.Count != 3) throw new Exception("Shift 连选失败。");
        }
        SelectCard(cards[0], false, false); SelectCard(cards[1], true, false);
        CompleteCardPress(new(cards[0], 1, ReleaseSelection.Single), cards[1]);
        if (files.SelectedItems.Count != 2) throw new Exception("在另一张卡片松手改变了多选。");
        CompleteCardPress(new(cards[0], 1, ReleaseSelection.Toggle), cards[0]);
        if (files.SelectedItems.Count != 1 || !files.SelectedItems.Contains(cards[1])) throw new Exception("在原卡片松手没有完成 Ctrl 选择。");
        files.SelectAll(); ClearDesktopSelection();
        if (files.SelectedItems.Count != 0 || selectionAnchor != null) throw new Exception("点击空白未取消选择。");
        files.SelectedItems.Clear(); foreach (var card in cards) if (previous.Contains(((StationItem)card.Tag).Id)) files.SelectedItems.Add(card);
    }
}
