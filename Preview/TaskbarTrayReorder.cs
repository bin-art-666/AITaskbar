using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using TrayIcon = AITaskbar.WindowsNotificationArea.TrayIcon;

namespace AITaskbar;

internal sealed partial class TaskbarSystemTray
{
    private const double AppDragThreshold = 5, AppScrollEdge = 16;
    private TrayIcon[] displayedApps = [];
    private AppPress? appPress;
    private AppDrag? appDrag;
    private Button? suppressedAppClick;

    private sealed record AppPress(TrayIcon Icon, Button Button, uint PointerId, Point Start);

    private sealed class AppDrag(int source, Point pointer)
    {
        internal readonly int Source = source;
        internal int Preview = source;
        internal Point Pointer = pointer;
    }

    private void EnableAppReorder(TrayIcon icon, Button button)
    {
        button.AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var pointer = args.GetCurrentPoint(appScroll);
            if (appPress is not null || pointer.PointerDeviceType != PointerDeviceType.Mouse ||
                !pointer.Properties.IsLeftButtonPressed || !button.CapturePointer(args.Pointer)) return;
            suppressedAppClick = null;
            appPress = new(icon, button, pointer.PointerId, pointer.Position);
        }), handledEventsToo: true);

        button.AddHandler(PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (appPress is not { } press || press.PointerId != args.Pointer.PointerId) return;
            var point = args.GetCurrentPoint(appScroll).Position;
            if (appDrag is null)
            {
                if (Math.Max(Math.Abs(point.X - press.Start.X), Math.Abs(point.Y - press.Start.Y)) < AppDragThreshold) return;
                int source = Array.IndexOf(displayedApps, icon);
                if (source < 0) return;
                appDrag = new(source, point);
                suppressedAppClick = button;
                button.Opacity = .45;
            }
            appDrag.Pointer = point;
            PreviewAppDrop();
            args.Handled = true;
        }), handledEventsToo: true);

        button.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, args) =>
        {
            if (appPress is not { } press || press.PointerId != args.Pointer.PointerId) return;
            int destination = -1;
            if (appDrag is not null)
            {
                destination = AppDropSlot(args.GetCurrentPoint(appScroll).Position);
                args.Handled = true;
            }
            FinishAppReorder(press, destination);
        }), handledEventsToo: true);

        button.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, args) =>
        {
            if (appPress is not { } press || press.PointerId != args.Pointer.PointerId) return;
            // Button releases capture as part of processing PointerReleased.
            DispatcherQueue.TryEnqueue(() => { if (appPress == press) CancelAppReorder(); });
        }), handledEventsToo: true);
        button.PointerCanceled += (_, args) => { if (appPress?.PointerId == args.Pointer.PointerId) CancelAppReorder(); };
        button.AddHandler(KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (appDrag is null || args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            CancelAppReorder();
        }), handledEventsToo: true);
    }

    private static void SetAppSlot(Button button, int slot)
    {
        Grid.SetColumn(button, slot % AppColumns);
        Grid.SetRow(button, slot / AppColumns);
    }

    private int AppDropSlot(Point pointer)
    {
        if (!IsInAppViewport(pointer)) return -1;
        var point = appScroll.TransformToVisual(appIcons).TransformPoint(pointer);
        if (point.X < 0 || point.X >= appIcons.ActualWidth || point.Y < 0 || point.Y >= appIcons.ActualHeight) return -1;
        const int pitch = AppTileSize + AppTileSpacing;
        int column = Math.Clamp((int)((point.X + AppTileSpacing / 2d) / pitch), 0, AppColumns - 1);
        int row = (int)((point.Y + AppTileSpacing / 2d) / pitch);
        return Math.Min(row * AppColumns + column, displayedApps.Length - 1);
    }

    private bool IsInAppViewport(Point pointer) =>
        pointer.X >= 0 && pointer.X < appScroll.ViewportWidth && pointer.Y >= 0 && pointer.Y < appScroll.ViewportHeight;

    private void PreviewAppDrop()
    {
        if (appDrag is not { } drag) return;
        int slot = AppDropSlot(drag.Pointer);
        if (slot < 0) slot = drag.Source;
        if (slot == drag.Preview) return;
        drag.Preview = slot;
        for (int i = 0; i < displayedApps.Length; i++)
        {
            int preview = i;
            if (i == drag.Source) preview = slot;
            else if (i > drag.Source && i <= slot) preview--;
            else if (i < drag.Source && i >= slot) preview++;
            SetAppSlot(tiles[displayedApps[i]].Button, preview);
        }
    }

    private void ScrollAppReorder()
    {
        if (appDrag is not { } drag || !IsInAppViewport(drag.Pointer) || appScroll.ScrollableHeight == 0) return;
        if (drag.Pointer.Y < AppScrollEdge)
            appScroll.ChangeView(null, Math.Max(0, appScroll.VerticalOffset - AppTileSize - AppTileSpacing), null, true);
        else if (drag.Pointer.Y > appScroll.ViewportHeight - AppScrollEdge)
            appScroll.ChangeView(null, Math.Min(appScroll.ScrollableHeight, appScroll.VerticalOffset + AppTileSize + AppTileSpacing), null, true);
    }

    private void CancelAppReorder()
    {
        if (appPress is { } press) FinishAppReorder(press, -1);
    }

    private void FinishAppReorder(AppPress press, int destination)
    {
        var drag = appDrag;
        appPress = null;
        appDrag = null;
        if (drag is null) return;

        press.Button.Opacity = 1;
        press.Button.ReleasePointerCaptures();
        DispatcherQueue.TryEnqueue(() => { if (appDrag is null) suppressedAppClick = null; });
        if (destination >= 0 && destination != drag.Source)
        {
            var target = displayedApps[destination];
            var current = notificationArea.Icons.ToHashSet();
            if (current.Contains(press.Icon) && current.Contains(target))
            {
                try
                {
                    appOrder.Move(displayedApps.Select(icon => icon.OrderKey), press.Icon.OrderKey, target.OrderKey,
                        after: destination > drag.Source);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    report("无法保存后台应用图标顺序。");
                }
            }
        }
        RefreshAppIcons();
    }
}
