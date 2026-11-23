using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AIEnter;

internal sealed partial class AiWindow
{
    sealed class ResizeGrip : Grid
    {
        internal ResizeGrip(InputSystemCursorShape cursor) => ProtectedCursor = InputSystemCursor.Create(cursor);
    }
    const int ResizeBand = 10, CornerSpan = 32;
    readonly List<(ResizeGrip Host, Thumb Thumb, AiPanelEdge Edge)> resizeGrips = [];
    Thumb? activeResizeGrip;
    bool dockResizing;
    AiPanelEdge dockEdge;
    double dockWidth = AiPolicy.DefaultDockWidth, dockHeight = AiPolicy.DefaultDockHeight;
    double dockWidthBefore, dockHeightBefore;
    AiBounds? dockPlacement;
    AiBounds dockStartBounds = new(0, 0, 0, 0);
    Native.Point dockStartCursor;
    IDisposable? resizeLease;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? layoutDragTimer;
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.Point position);
    void InitializeDockResize(Grid shell)
    {
        layoutDragTimer = DispatcherQueue.CreateTimer();
        layoutDragTimer.Interval = TimeSpan.FromMilliseconds(16);
        layoutDragTimer.Tick += (_, _) => TrackLayoutDrag();
        Closed += (_, _) => layoutDragTimer.Stop();
        ConfigureGrip(shell, AiPanelEdge.Left, "调整 AI 面板宽度");
        ConfigureGrip(shell, AiPanelEdge.Top, "从上边缘调整 AI 面板高度");
        ConfigureGrip(shell, AiPanelEdge.Bottom, "从下边缘调整 AI 面板高度");
        if (TaskbarFlyout is not null)
        {
            ConfigureGrip(shell, AiPanelEdge.Right, "从右边缘调整 AI 面板宽度");
            ConfigureGrip(shell, AiPanelEdge.TopLeft, "从左上角调整 AI 面板大小");
            ConfigureGrip(shell, AiPanelEdge.TopRight, "从右上角调整 AI 面板大小");
            ConfigureGrip(shell, AiPanelEdge.BottomLeft, "从左下角调整 AI 面板大小");
            ConfigureGrip(shell, AiPanelEdge.BottomRight, "从右下角调整 AI 面板大小");
        }
        shell.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (!dockResizing || e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            activeResizeGrip?.CancelDrag();
            EndDockResize(true);
        }), true);
    }

    bool CanResizeFrom(AiPanelEdge edge) => Detached
        ? TaskbarFlyout is not null
        : edge is AiPanelEdge.Left or AiPanelEdge.Top || (edge == AiPanelEdge.Bottom && TaskbarFlyout is null);

    void UpdateResizeGrips()
    {
        foreach (var (host, grip, edge) in resizeGrips)
        {
            host.Visibility = CanResizeFrom(edge) ? Visibility.Visible : Visibility.Collapsed;
            grip.IsEnabled = !options.ReadOnly;
        }
    }

    void ConfigureGrip(Grid shell, AiPanelEdge edge, string name)
    {
        bool horizontal = (edge & (AiPanelEdge.Left | AiPanelEdge.Right)) != 0;
        bool vertical = (edge & (AiPanelEdge.Top | AiPanelEdge.Bottom)) != 0;
        bool corner = horizontal && vertical;
        var cursor = edge switch
        {
            AiPanelEdge.Left or AiPanelEdge.Right => InputSystemCursorShape.SizeWestEast,
            AiPanelEdge.Top or AiPanelEdge.Bottom => InputSystemCursorShape.SizeNorthSouth,
            AiPanelEdge.TopLeft or AiPanelEdge.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
            _ => InputSystemCursorShape.SizeNortheastSouthwest
        };
        var host = new ResizeGrip(cursor)
        {
            HorizontalAlignment = horizontal ? edge.HasFlag(AiPanelEdge.Left) ? HorizontalAlignment.Left : HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            VerticalAlignment = vertical ? edge.HasFlag(AiPanelEdge.Top) ? VerticalAlignment.Top : VerticalAlignment.Bottom : VerticalAlignment.Stretch
        };
        if (horizontal) host.Width = corner ? CornerSpan : ResizeBand;
        if (vertical) host.Height = corner ? CornerSpan : ResizeBand;
        if (vertical && !corner) host.Margin = new Thickness(ResizeBand, 0, ResizeBand, 0);
        if (corner) Canvas.SetZIndex(host, 1);

        var grip = new Thumb();
        grip.HorizontalAlignment = HorizontalAlignment.Stretch; grip.VerticalAlignment = VerticalAlignment.Stretch;
        grip.BorderThickness = new Thickness(0); grip.IsTabStop = true;
        grip.Template = ResizeGripTemplate(host, corner, vertical);
        AutomationProperties.SetName(grip, name); ToolTipService.SetToolTip(grip, name + "（Esc 取消）");
        AutomationProperties.SetHelpText(grip, "拖动调整，松开自动保存；方向键微调，Esc 取消本次拖动。");
        host.Children.Add(grip); shell.Children.Add(host);
        resizeGrips.Add((host, grip, edge));
        grip.DragStarted += (_, _) =>
        {
            if (!GetCursorPos(out var point)) return;
            if (BeginDockResize(point, edge))
            {
                activeResizeGrip = grip;
                grip.Focus(FocusState.Pointer);
                layoutDragTimer?.Start();
            }
        };
        grip.DragCompleted += (_, e) =>
        {
            if (activeResizeGrip != grip) return;
            if (!e.Canceled) TrackLayoutDrag();
            EndDockResize(e.Canceled);
        };
        grip.KeyDown += (_, e) =>
        {
            bool useX = horizontal && e.Key is VirtualKey.Left or VirtualKey.Right;
            bool useY = vertical && e.Key is VirtualKey.Up or VirtualKey.Down;
            if (!(useX || useY) || !BeginDockResize(default, edge)) return;
            e.Handled = true;
            int step = (int)Math.Round(20 * display.Dpi / 96d);
            int delta = e.Key is VirtualKey.Left or VirtualKey.Up ? -step : step;
            MoveDockResize(new() { X = useX ? delta : 0, Y = useY ? delta : 0 });
            EndDockResize(false);
        };
    }

    static ControlTemplate ResizeGripTemplate(ResizeGrip host, bool corner, bool vertical)
    {
        // Keep the diagonal hit areas on the perimeter so the top corner grips
        // do not cover the service labels or the window action buttons.
        string body = corner
            ? $"<Grid><Border Width='{ResizeBand}' Background='Transparent' HorizontalAlignment='{host.HorizontalAlignment}'/><Border Height='{ResizeBand}' Background='Transparent' VerticalAlignment='{host.VerticalAlignment}'/></Grid>"
            : $"<Grid Background='Transparent'><Border Width='{(vertical ? 32 : 2)}' Height='{(vertical ? 2 : 32)}' CornerRadius='1' Background='Gray' Opacity='0.35' HorizontalAlignment='Center' VerticalAlignment='Center'/></Grid>";
        return (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            $"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>{body}</ControlTemplate>");
    }
    void TrackLayoutDrag()
    {
        if (!GetCursorPos(out var cursor)) return;
        // Windowed XAML popups can keep capture without delivering Thumb
        // DragDelta outside their input site. Screen coordinates stay stable.
        if (dockResizing) MoveDockResize(cursor);
        else if (moveStart is not null) MoveExpandedFlyout(cursor);
    }
    bool BeginDockResize(Native.Point cursor, AiPanelEdge edge)
    {
        if (!CanResizeFrom(edge) || destroyed || dockResizing || options.ReadOnly || ModalActive || DragActive) return false;
        dockResizing = true; resizeLease = KeepOpen(); dockEdge = edge;
        dockWidthBefore = dockWidth; dockHeightBefore = dockHeight;
        dockStartBounds = Detached ? ExpandedFlyoutBounds() : TaskbarFlyout is not null ? DockBounds()
            : new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        dockStartCursor = cursor;
        return true;
    }
    void MoveDockResize(Native.Point cursor)
    {
        if (!dockResizing) return;
        long dx = (long)cursor.X - dockStartCursor.X, dy = (long)cursor.Y - dockStartCursor.Y;
        if (Detached)
        {
            UpdateExpandedPreview(AiPolicy.ResizeExpandedBounds(dockStartBounds, ExpandedWorkArea(), dockEdge, dx, dy, FlyoutScale));
            return;
        }
        int delta = (int)Math.Clamp(dockEdge == AiPanelEdge.Left ? dx : dy, int.MinValue, int.MaxValue);
        var area = display.Info.Work;
        var placement = AiPolicy.ResizeDockBounds(dockStartBounds,
            new(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top), dockEdge, delta, display.Dpi);
        if (dockPlacement == placement) return;
        dockPlacement = placement;
        double scale = display.Dpi / 96d;
        dockWidth = Math.Clamp(dockPlacement.Width / scale, AiPolicy.MinimumDockWidth, AiPolicy.MaximumDockWidth);
        dockHeight = Math.Clamp(dockPlacement.Height / scale, AiPolicy.MinimumDockHeight, AiPolicy.MaximumDockHeight);
        Position();
    }
    void EndDockResize(bool cancel)
    {
        if (!dockResizing) return;
        layoutDragTimer?.Stop();
        dockResizing = false;
        activeResizeGrip = null;
        try
        {
            if (Detached)
            {
                EndExpandedResize(cancel);
                return;
            }
            if (!cancel && (Math.Abs(dockWidth - dockWidthBefore) > .01 || Math.Abs(dockHeight - dockHeightBefore) > .01))
            {
                try { options.Save(options.Data with { DockWidth = dockWidth, DockHeight = dockHeight, DockTopOffset = 0 }); }
                catch { cancel = true; SetStatus("面板尺寸保存失败，已恢复原尺寸，请重试。"); }
            }
            if (cancel)
            {
                dockWidth = dockWidthBefore; dockHeight = dockHeightBefore; dockPlacement = dockStartBounds;
                if (!destroyed && !Detached) Position();
            }
        }
        finally { resizeLease?.Dispose(); resizeLease = null; }
    }
}
