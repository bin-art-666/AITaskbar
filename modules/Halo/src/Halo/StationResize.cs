using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace Halo;

// Pointer capture and drag lifetime follow AIEnter's dock grips, extended to four edges and corners.
internal sealed partial class StationWindow
{
    sealed class GripHost : Grid
    {
        internal GripHost(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
    }
    bool resizing, trackingMouseResize;
    int resizeEdges;
    Native.Point resizeOrigin;
    RectInt32 resizeBounds;
    IDisposable? resizeLease;
    Thumb? activeGrip;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? layoutTracking;
    readonly List<Thumb> resizeGrips = [];
    void VerifyResizeGrips()
    {
        if (resizeGrips.Count != 8 || resizeGrips.Any(g => !g.IsLoaded || g.ActualWidth < 1 || g.ActualHeight < 1))
            throw new InvalidOperationException("窗口缩放手柄未完成布局。");
    }
    void InitializeResizeGrips()
    {
        if (TaskbarFlyout != null)
        {
            layoutTracking = DispatcherQueue.CreateTimer();
            layoutTracking.Interval = TimeSpan.FromMilliseconds(16);
            layoutTracking.Tick += (_, _) => TrackWindowLayout();
            Closed += (_, _) => layoutTracking.Stop();
        }
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => {
            if (!resizing) return;
            // GetCursorPos can already point at the end of a fast queued drag.
            // Preserve the actual press from its event packet before DragDelta arrives.
            var press = e.GetCurrentPoint(root);
            trackingMouseResize = TaskbarFlyout != null && press.PointerDeviceType == PointerDeviceType.Mouse;
            var p = press.Position; double scale = root.XamlRoot.RasterizationScale;
            var bounds = CurrentBounds();
            resizeOrigin = new() { X = bounds.X + (int)Math.Round(p.X * scale), Y = bounds.Y + (int)Math.Round(p.Y * scale) };
        }), true);
        AddGrip(1, "左边缘", HorizontalAlignment.Left, VerticalAlignment.Stretch, InputSystemCursorShape.SizeWestEast);
        AddGrip(2, "右边缘", HorizontalAlignment.Right, VerticalAlignment.Stretch, InputSystemCursorShape.SizeWestEast);
        AddGrip(4, "上边缘", HorizontalAlignment.Stretch, VerticalAlignment.Top, InputSystemCursorShape.SizeNorthSouth);
        AddGrip(8, "下边缘", HorizontalAlignment.Stretch, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNorthSouth);
        AddGrip(5, "左上角", HorizontalAlignment.Left, VerticalAlignment.Top, InputSystemCursorShape.SizeNorthwestSoutheast);
        AddGrip(6, "右上角", HorizontalAlignment.Right, VerticalAlignment.Top, InputSystemCursorShape.SizeNortheastSouthwest);
        AddGrip(9, "左下角", HorizontalAlignment.Left, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNortheastSouthwest);
        AddGrip(10, "右下角", HorizontalAlignment.Right, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNorthwestSoutheast);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => {
            if (!resizing || e.Key != VirtualKey.Escape) return;
            e.Handled = true; CancelResize();
        }), true);
        Closed += (_, _) => EndResize(true);
    }
    void AddGrip(int edges, string label, HorizontalAlignment horizontal, VerticalAlignment vertical, InputSystemCursorShape cursor)
    {
        bool corner = (edges & 3) != 0 && (edges & 12) != 0;
        var host = new GripHost(cursor) { HorizontalAlignment = horizontal, VerticalAlignment = vertical };
        if (corner) { host.Width = host.Height = 22; }
        else if (horizontal == HorizontalAlignment.Stretch) { host.Height = 6; host.Margin = new Thickness(22, 0, 22, 0); }
        else { host.Width = 6; host.Margin = new Thickness(0, 22, 0, 22); }
        var thumb = new Thumb { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsTabStop = true,
            Template = (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Background='Transparent'/></ControlTemplate>") };
        AutomationProperties.SetName(thumb, "调整窗口" + label);
        AutomationProperties.SetHelpText(thumb, "拖动调整宽高，松开保存；Esc 取消本次拖动。");
        if (TaskbarFlyout != null) Canvas.SetZIndex(host, corner ? 2 : 1);
        resizeGrips.Add(thumb); host.Children.Add(thumb); Grid.SetRowSpan(host, root.RowDefinitions.Count); root.Children.Add(host);
        thumb.DragStarted += (_, _) => {
            if (destroyed || ModalActive || DragActive || resizing || !GetCursorPos(out resizeOrigin)) { thumb.CancelDrag(); return; }
            resizing = true; resizeEdges = edges; activeGrip = thumb; resizeLease = KeepOpenForDrag();
            trackingMouseResize = TaskbarFlyout != null && (Native.GetAsyncKeyState(1) & 0x8000) != 0;
            resizeBounds = CurrentBounds(); saveSize.Stop();
            thumb.Focus(FocusState.Pointer);
            layoutTracking?.Start();
        };
        thumb.DragDelta += (_, _) => TrackWindowLayout();
        thumb.DragCompleted += (_, e) =>
        {
            if (activeGrip != thumb) return;
            // A popup can lose XAML capture while it moves under the mouse. The
            // screen-coordinate tracker still owns this gesture until release.
            if (trackingMouseResize)
            {
                if (!e.Canceled && (Native.GetAsyncKeyState(1) & 0x8000) == 0) TrackWindowLayout();
                return;
            }
            if (!e.Canceled && TaskbarFlyout != null) TrackWindowLayout();
            EndResize(e.Canceled);
        };
    }
    void TrackWindowLayout()
    {
        if (!GetCursorPos(out var point)) return;
        if (!resizing) { MoveFlyout(point); return; }
        if (trackingMouseResize)
        {
            if ((Native.GetAsyncKeyState((int)VirtualKey.Escape) & 0x8000) != 0) { CancelResize(); return; }
            // After release the cursor may already be somewhere else. Store the
            // last bounds rendered during the actual gesture.
            if ((Native.GetAsyncKeyState(1) & 0x8000) == 0) { EndResize(false); return; }
        }
        MoveResize(point.X - resizeOrigin.X, point.Y - resizeOrigin.Y);
    }
    void MoveResize(int dx, int dy)
    {
        var work = LayoutWorkArea(resizeBounds);
        double scale = WindowLayoutScale;
        int minWidth = Math.Min((int)(280 * scale), work.Right - work.Left), minHeight = Math.Min((int)(200 * scale), work.Bottom - work.Top);
        int maxWidth = (int)(1600 * scale), maxHeight = (int)(1400 * scale);
        int left = resizeBounds.X, top = resizeBounds.Y, right = left + resizeBounds.Width, bottom = top + resizeBounds.Height;
        int Clamp(int value, int min, int max) => Math.Clamp(value, min, Math.Max(min, max));
        if ((resizeEdges & 1) != 0) left = Clamp(left + dx, Math.Max(work.Left, right - maxWidth), right - minWidth);
        if ((resizeEdges & 2) != 0) right = Clamp(right + dx, left + minWidth, Math.Min(work.Right, left + maxWidth));
        if ((resizeEdges & 4) != 0) top = Clamp(top + dy, Math.Max(work.Top, bottom - maxHeight), bottom - minHeight);
        if ((resizeEdges & 8) != 0) bottom = Clamp(bottom + dy, top + minHeight, Math.Min(work.Bottom, top + maxHeight));
        var bounds = new RectInt32(left, top, right - left, bottom - top);
        if (Native.Same(bounds, CurrentBounds())) return;
        positioning = true;
        try { ApplyBounds(bounds); }
        finally { positioning = false; }
    }
    void CancelResize()
    {
        var grip = activeGrip;
        EndResize(true);
        grip?.CancelDrag();
    }
    void EndResize(bool cancel)
    {
        if (!resizing) return;
        layoutTracking?.Stop();
        resizing = trackingMouseResize = false; activeGrip = null;
        try {
            if (!destroyed) { if (cancel) ApplyBounds(resizeBounds); SaveSize(); }
        } finally { resizeLease?.Dispose(); resizeLease = null; }
    }
}
