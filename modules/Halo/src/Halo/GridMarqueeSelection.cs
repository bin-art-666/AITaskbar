using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Halo;

internal sealed class GridMarqueeSelection
{
    const double DragThreshold = 5;
    const double EdgeBand = 28;
    readonly Grid host;
    readonly GridView grid;
    readonly Canvas layer = new() { IsHitTestVisible = false };
    readonly Border frame = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly DispatcherQueueTimer scrollTimer;
    readonly Func<bool> canStart;
    readonly Func<IDisposable> keepOpen;
    readonly Action clearSelection;
    readonly Action<Guid?> commitAnchor;
    readonly Action refreshLabels;
    Pointer? pointer;
    HashSet<Guid> prior = [];
    ScrollViewer? scroll;
    IDisposable? lease;
    Point origin, startOffset, position;
    bool additive;

    internal bool IsActive => lease != null;

    internal GridMarqueeSelection(Grid host, GridView grid, FrameworkElement notice, Func<bool> canStart,
        Func<IDisposable> keepOpen, Action clearSelection, Action<Guid?> commitAnchor, Action refreshLabels)
    {
        this.host = host;
        this.grid = grid;
        this.canStart = canStart;
        this.keepOpen = keepOpen;
        this.clearSelection = clearSelection;
        this.commitAnchor = commitAnchor;
        this.refreshLabels = refreshLabels;
        layer.Children.Add(frame);
        Grid.SetRow(layer, Grid.GetRow(grid));
        host.Children.Insert(host.Children.IndexOf(notice), layer);
        scrollTimer = host.DispatcherQueue.CreateTimer();
        scrollTimer.Interval = TimeSpan.FromMilliseconds(30);
        scrollTimer.IsRepeating = true;
        scrollTimer.Tick += (_, _) => ScrollAtEdge();
        host.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
        host.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Moved), true);
        host.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released), true);
        host.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Lost), true);
        host.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((sender, e) => {
            if (IsActive && ReferenceEquals(e.OriginalSource, host)) Lost(sender, e);
        }), true);
        host.PointerExited += (_, e) => {
            var point = e.GetCurrentPoint(host).Position;
            if (!IsActive && pointer?.PointerId == e.Pointer.PointerId &&
                (point.X < 0 || point.Y < 0 || point.X >= host.ActualWidth || point.Y >= host.ActualHeight)) Reset();
        };
        host.PreviewKeyDown += (_, e) => {
            if (!IsActive || e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            Cancel();
        };
        host.Unloaded += (_, _) => Cancel();
    }

    internal void ApplyColors(StationInteractionColors colors)
    {
        var fill = colors.Selection;
        frame.Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Min((int)fill.A, 84), fill.R, fill.G, fill.B));
        frame.BorderBrush = new SolidColorBrush(colors.Outline);
        frame.BorderThickness = new Thickness(1);
    }

    void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (pointer != null || !canStart() || !StationWindow.ContentBlankTarget(e.OriginalSource as DependencyObject, host)) return;
        var point = e.GetCurrentPoint(grid);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (point.PointerDeviceType != PointerDeviceType.Mouse) {
            if (Viewport().Contains(point.Position)) clearSelection();
            return;
        }
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || !Viewport().Contains(point.Position)) return;
        pointer = e.Pointer;
        RememberSelection(point.Position, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control));
        e.Handled = true;
    }

    HashSet<Guid> SelectedIds() => grid.SelectedItems.Cast<GridViewItem>().Select(Identity).ToHashSet();

    void RememberSelection(Point point, bool extend)
    {
        origin = position = point;
        startOffset = Offset();
        additive = extend;
        prior = SelectedIds();
    }

    void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (pointer?.PointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(grid);
        if (!point.Properties.IsLeftButtonPressed) { Cancel(); return; }
        position = point.Position;
        if (!IsActive) {
            if (!canStart()) { Reset(); return; }
            if (Math.Max(Math.Abs(position.X - origin.X), Math.Abs(position.Y - origin.Y)) < DragThreshold) return;
            if (!host.CapturePointer(e.Pointer)) { Reset(); return; }
            Start();
        }
        e.Handled = true;
        additive = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        Render();
    }

    void Released(object sender, PointerRoutedEventArgs e)
    {
        if (pointer?.PointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(grid);
        if (point.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (!IsActive) {
            if (!additive && canStart()) clearSelection();
            Reset();
            return;
        }
        position = point.Position;
        additive = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        Render();
        commitAnchor(grid.SelectedItems.Cast<GridViewItem>().OrderBy(StationGridPanel.Cell)
            .Select(card => (Guid?)Identity(card)).FirstOrDefault());
        Reset();
        grid.Focus(FocusState.Programmatic);
    }

    void Lost(object sender, PointerRoutedEventArgs e)
    {
        if (pointer?.PointerId == e.Pointer.PointerId) Cancel();
    }

    void Start()
    {
        lease = keepOpen();
        scroll = StationWindow.Child<ScrollViewer>(grid);
        if (scroll != null) scroll.ViewChanged += ViewChanged;
        if (!StationWindow.InsideElement(host, FocusManager.GetFocusedElement(host.XamlRoot) as DependencyObject))
            grid.Focus(FocusState.Programmatic);
        refreshLabels();
        scrollTimer.Start();
    }

    void ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => Render();

    void Render()
    {
        var viewport = Viewport();
        var offset = Offset();
        var start = new Point(origin.X + startOffset.X - offset.X, origin.Y + startOffset.Y - offset.Y);
        var end = new Point(Math.Clamp(position.X, viewport.Left, viewport.Right), Math.Clamp(position.Y, viewport.Top, viewport.Bottom));
        var bounds = new Rect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        var shown = FolderDropIntent.Intersect(bounds, viewport);
        var translated = grid.TransformToVisual(layer).TransformPoint(new(shown.X, shown.Y));
        Canvas.SetLeft(frame, translated.X);
        Canvas.SetTop(frame, translated.Y);
        frame.Width = shown.Width;
        frame.Height = shown.Height;
        frame.Visibility = shown.Width > 0 && shown.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
        var next = additive ? prior.ToHashSet() : new HashSet<Guid>();
        foreach (GridViewItem card in grid.Items)
            if (card.IsLoaded && Intersects(bounds, FolderDropIntent.CardFrame(grid, card))) next.Add(Identity(card));
        Select(next);
    }

    static bool Intersects(Rect box, Rect card)
    {
        var intersection = FolderDropIntent.Intersect(box, card);
        return intersection.Width > 0 && intersection.Height > 0;
    }

    Rect Viewport()
    {
        var viewer = scroll ?? StationWindow.Child<ScrollViewer>(grid);
        if (viewer != null && StationWindow.Child<ScrollContentPresenter>(viewer) is { ActualWidth: > 0, ActualHeight: > 0 } viewport) {
            var top = viewport.TransformToVisual(grid).TransformPoint(new());
            return FolderDropIntent.Intersect(new(top.X, top.Y, viewport.ActualWidth, viewport.ActualHeight),
                new(0, 0, grid.ActualWidth, grid.ActualHeight));
        }
        return new(0, 0, grid.ActualWidth, grid.ActualHeight);
    }

    Point Offset()
    {
        var viewer = scroll ?? StationWindow.Child<ScrollViewer>(grid);
        return viewer == null ? new() : new(viewer.HorizontalOffset, viewer.VerticalOffset);
    }

    void ScrollAtEdge()
    {
        if (scroll == null) return;
        var viewport = Viewport();
        double x = Math.Clamp(scroll.HorizontalOffset + ScrollStep(position.X, viewport.Left, viewport.Right), 0, scroll.ScrollableWidth);
        double y = Math.Clamp(scroll.VerticalOffset + ScrollStep(position.Y, viewport.Top, viewport.Bottom), 0, scroll.ScrollableHeight);
        if (x != scroll.HorizontalOffset || y != scroll.VerticalOffset) scroll.ChangeView(x, y, null, true);
    }

    static double ScrollStep(double point, double start, double end)
    {
        double band = Math.Min(EdgeBand, (end - start) / 3);
        if (point < start + band) return -Math.Clamp(start + band - point, 2, 24);
        if (point > end - band) return Math.Clamp(point - end + band, 2, 24);
        return 0;
    }

    void Select(HashSet<Guid> ids)
    {
        foreach (var card in grid.SelectedItems.Cast<GridViewItem>().ToArray())
            if (!ids.Contains(Identity(card))) grid.SelectedItems.Remove(card);
        foreach (GridViewItem card in grid.Items)
            if (!card.IsSelected && ids.Contains(Identity(card))) grid.SelectedItems.Add(card);
    }

    static Guid Identity(GridViewItem card) => ((StationItem)card.Tag).Id;

    internal void Cancel()
    {
        if (IsActive) Select(prior);
        Reset();
    }

    internal void Verify(Action<string, bool> check, string name)
    {
        var viewport = Viewport();
        var cards = grid.Items.Cast<GridViewItem>().ToArray();
        var first = cards.FirstOrDefault(card => card.IsLoaded && card.ActualWidth > 8
            && Intersects(viewport, FolderDropIntent.CardFrame(grid, card)));
        if (first == null || cards.Length < 2) throw new InvalidOperationException("框选验收需要可见项目和另一项初始选择。");
        var baseline = Identity(cards.First(card => card != first));
        var before = SelectedIds();
        var rect = FolderDropIntent.Intersect(viewport, FolderDropIntent.CardFrame(grid, first));
        try {
            Select([baseline]);
            RememberSelection(new(rect.Left, rect.Top), extend: true);
            Start();
            position = new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            Render();
            check(name + "MarqueeAddsToExistingSelection", SelectedIds().SetEquals([Identity(first), baseline])
                && frame.Visibility == Visibility.Visible);
            position = origin;
            Render();
            check(name + "MarqueeShrinksWithoutDroppingOriginalSelection", SelectedIds().SetEquals([baseline]));
            additive = false;
            position = new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            Render();
            check(name + "MarqueeReplacesSelection", SelectedIds().SetEquals([Identity(first)]));
            Cancel();
            check(name + "MarqueeCancelRestoresSelectionAndEndsCapture", SelectedIds().SetEquals([baseline])
                && !IsActive && !scrollTimer.IsRunning && frame.Visibility == Visibility.Collapsed);
        } finally {
            Cancel();
            Select(before);
        }
    }

    internal async Task VerifyEdgeScroll(Action<string, bool> check)
    {
        var viewer = StationWindow.Child<ScrollViewer>(grid);
        if (viewer == null || viewer.ScrollableHeight < 40)
            throw new InvalidOperationException("框选边缘滚动验收需要可纵向滚动的网格。");
        var before = viewer.VerticalOffset;
        bool down = before < viewer.ScrollableHeight / 2;
        var viewport = Viewport();
        double x = viewport.Left + viewport.Width / 2;
        RememberSelection(new(x - 25, viewport.Top + viewport.Height / 2), extend: false);
        try {
            Start();
            position = new(x, down ? viewport.Bottom - 1 : viewport.Top + 1);
            Render();
            for (int attempt = 0; attempt < 10 && viewer.VerticalOffset == before; attempt++) await Task.Delay(30);
            check("MarqueeScrollsWhilePointerRemainsAtGridEdge", down ? viewer.VerticalOffset > before : viewer.VerticalOffset < before);
        } finally {
            Cancel();
            viewer.ChangeView(null, before, null, true);
        }
    }

    void Reset()
    {
        var captured = pointer;
        pointer = null;
        prior.Clear();
        frame.Visibility = Visibility.Collapsed;
        scrollTimer.Stop();
        if (scroll != null) scroll.ViewChanged -= ViewChanged;
        scroll = null;
        if (lease == null) return;
        lease.Dispose();
        lease = null;
        if (captured != null) host.ReleasePointerCapture(captured);
        refreshLabels();
    }
}
