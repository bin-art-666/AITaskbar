using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AIEnter;

internal sealed partial class AiWindow
{
    double FlyoutScale => Math.Max(96, display.Dpi) / 96d;
    AiBounds? expandedPreview;
    AiBounds? moveStart;
    Native.Point moveStartPointer;
    Pointer? headerMovePointer;
    IDisposable? moveLease;

    AiBounds ExpandedWorkArea()
    {
        var work = display.Info.Work;
        int gap = Math.Max(0, Math.Min((int)Math.Round(12 * FlyoutScale), (Math.Min(work.Right - work.Left, work.Bottom - work.Top) - 1) / 2));
        return new(work.Left + gap, work.Top + gap, work.Right - work.Left - 2 * gap, work.Bottom - work.Top - 2 * gap);
    }

    AiBounds ExpandedFlyoutBounds()
    {
        if (expandedPreview is { } preview) return preview;
        var work = ExpandedWorkArea();
        double scale = FlyoutScale;
        var saved = options.Data.ExpandedLayout;
        var dock = DockBounds();
        int width = ClampDimension(saved?.Width * scale ?? Math.Max(900 * scale, dock.Width + 160 * scale), 420, work.Width);
        int height = ClampDimension(saved?.Height * scale ?? Math.Max(work.Height * .82, dock.Height + 96 * scale), 360, work.Height);
        var bounds = new AiBounds(work.X + work.Width - width, work.Y + work.Height - height, width, height);
        if (TaskbarFlyout?.CanMove == true && saved is not null && saved.Display == display.Device)
            bounds = FitExpandedPosition(bounds, work, work.X + saved.X * scale, work.Y + saved.Y * scale);
        return bounds;
    }

    static AiBounds FitExpandedPosition(AiBounds bounds, AiBounds work, double x, double y) => bounds with
    {
        X = (int)Math.Round(Math.Clamp(x, work.X, work.X + work.Width - bounds.Width)),
        Y = (int)Math.Round(Math.Clamp(y, work.Y, work.Y + work.Height - bounds.Height))
    };

    int ClampDimension(double value, int minimumDip, int maximum) =>
        (int)Math.Round(Math.Clamp(value, Math.Min(maximum, minimumDip * FlyoutScale), maximum));

    void EndExpandedResize(bool cancel)
    {
        var preview = expandedPreview;
        expandedPreview = null;
        if (!cancel && preview is not null && preview != dockStartBounds) SaveExpandedLayout(preview);
        if (!destroyed) Position();
    }

    void SaveExpandedLayout(AiBounds bounds)
    {
        try
        {
            double scale = FlyoutScale;
            var saved = options.Data.ExpandedLayout;
            var work = ExpandedWorkArea();
            var layout = new AiExpandedLayout(bounds.Width / scale, bounds.Height / scale, display.Device,
                (bounds.X - work.X) / scale, (bounds.Y - work.Y) / scale);
            if (saved != layout) options.Save(options.Data with { ExpandedLayout = layout });
        }
        catch { SetStatus("展开面板布局保存失败，已恢复原布局。"); }
    }

    void InitializeExpandedHeaderMove()
    {
        aiHeader.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetHelpText(aiHeader, "展开时可拖动顶栏空白处移动面板，按 Esc 取消拖动。");
        aiHeader.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (!Detached || !IsOpen || destroyed || dockResizing || options.ReadOnly || ModalActive || DragActive
                || !args.GetCurrentPoint(aiHeader).Properties.IsLeftButtonPressed || !IsHeaderDragSurface(args.OriginalSource)
                || !GetCursorPos(out moveStartPointer) || !aiHeader.CapturePointer(args.Pointer)) return;
            headerMovePointer = args.Pointer;
            moveStart = ExpandedFlyoutBounds();
            moveLease = KeepOpen();
            aiHeader.Focus(FocusState.Pointer);
            layoutDragTimer?.Start();
            args.Handled = true;
        }), true);
        aiHeader.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) =>
        {
            if (headerMovePointer?.PointerId != args.Pointer.PointerId) return;
            TrackLayoutDrag();
            EndExpandedMove(false);
            args.Handled = true;
        }), true);
        aiHeader.PointerCaptureLost += (_, args) =>
        {
            if (headerMovePointer?.PointerId == args.Pointer.PointerId) EndExpandedMove(true);
        };
        aiHeader.PointerCanceled += (_, args) =>
        {
            if (headerMovePointer?.PointerId == args.Pointer.PointerId) EndExpandedMove(true);
        };
        aiHeader.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (moveStart is null || args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            EndExpandedMove(true);
        }), true);
    }

    bool IsHeaderDragSurface(object source)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == aiHeader) return true;
            if (element is ButtonBase or Thumb or ScrollBar) return false;
        }
        return false;
    }

    void MoveExpandedFlyout(Native.Point cursor)
    {
        if (moveStart is not { } start) return;
        double dx = (double)cursor.X - moveStartPointer.X;
        double dy = (double)cursor.Y - moveStartPointer.Y;
        if (expandedPreview is null && Math.Max(Math.Abs(dx), Math.Abs(dy)) < 3 * FlyoutScale) return;
        UpdateExpandedPreview(FitExpandedPosition(start, ExpandedWorkArea(),
            start.X + dx, start.Y + dy));
    }

    void UpdateExpandedPreview(AiBounds preview)
    {
        if (expandedPreview == preview) return;
        expandedPreview = preview;
        Position();
    }

    void EndExpandedMove(bool cancel)
    {
        if (moveStart is not { } start) return;
        layoutDragTimer?.Stop();
        var preview = expandedPreview;
        expandedPreview = null;
        try
        {
            if (!cancel && preview is not null && preview != start) SaveExpandedLayout(preview);
        }
        finally
        {
            moveStart = null;
            var pointer = headerMovePointer;
            headerMovePointer = null;
            if (pointer is not null) aiHeader.ReleasePointerCapture(pointer);
            moveLease?.Dispose();
            moveLease = null;
            if (!destroyed) Position();
        }
    }
}
