using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using System.Runtime.InteropServices;

namespace AIEnter;

internal sealed partial class AiWindow
{
    readonly ConditionalWeakTable<DragEventArgs, object> consumedDrops = new();
    FlyoutFileDrop? flyoutFileDrop;
    int? flyoutFileDropResult;
    string? flyoutDropLocation;
    readonly Border dropHint = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(235, 220, 248, 255)),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 150, 200)), BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(10),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    internal async Task OpenForDragHover(DesktopMonitor monitor)
    {
        using var lease = KeepOpenForDrag(); await Open(monitor, monitor.Info.Work.Top, false); await Task.Delay(350);
    }
    void InitializeDropTarget()
    {
        var shell = (Grid)((Border)PanelContent!).Child;
        Canvas.SetZIndex(dropHint, 100); shell.Children.Add(dropHint);
        AttachDropTarget(PanelContent!);
    }

    void InitializeFlyoutFileDrop(FrameworkElement anchor)
    {
        var flyout = TaskbarFlyout!;
        int generation = 0;
        flyout.Opened += async () =>
        {
            int currentGeneration = ++generation;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(25);
                if (currentGeneration != generation || !flyout.IsOpen || destroyed || flyoutFileDrop is not null) return;
                var popup = FindFlyoutDropWindow(anchor);
                if (popup == IntPtr.Zero) continue;
                // WinUI does not always light-dismiss a windowed flyout on a click in another window.
                WatchOutsideClicks(popup);
                var drop = new FlyoutFileDrop(() => IsOpen && !flyout.IsClosing && !destroyed && !transferringDrop && !ModalActive,
                    SetDropHint, paths =>
                {
                    string provider = options.Data.Provider;
                    return DispatcherQueue.TryEnqueue(async () => await ReceiveFlyoutFiles(paths, provider));
                });
                flyoutFileDropResult = drop.Register(popup);
                if (flyoutFileDropResult < 0)
                {
                    GetBackplateWindowRect(popup, out var bounds);
                    var point = new Native.Point { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
                    var site = DropWindowAtPoint(point);
                    if (site != popup && Native.GetAncestor(site, 2) == popup) flyoutFileDropResult = drop.Register(site);
                }
                if (flyoutFileDropResult >= 0) flyoutFileDrop = drop;
                return;
            }
        };
        void Detach()
        {
            generation++;
            flyoutFileDrop?.Dispose();
            flyoutFileDrop = null;
            SetDropHint(false);
        }
        flyout.Dismissing += Detach;
        flyout.Closed += Detach;
    }

    IntPtr FindFlyoutDropWindow(FrameworkElement anchor)
    {
        var host = PanelContent!;
        var anchorRoot = anchor.XamlRoot;
        if (anchorRoot?.Content is not UIElement rootContent || host.ActualWidth <= 0 || host.ActualHeight <= 0) return IntPtr.Zero;
        var owner = Microsoft.UI.Win32Interop.GetWindowFromWindowId(anchorRoot.ContentIslandEnvironment.AppWindowId);
        var origin = new Native.Point();
        if (!ClientToScreen(owner, ref origin)) return IntPtr.Zero;
        double scale = anchorRoot.RasterizationScale;
        var anchorBounds = anchor.TransformToVisual(rootContent).TransformBounds(new(0, 0, anchor.ActualWidth, anchor.ActualHeight));
        var point = new Native.Point
        {
            X = origin.X + (int)Math.Round((anchorBounds.Right - Math.Min(48, host.ActualWidth / 2)) * scale),
            Y = origin.Y + (int)Math.Round((anchorBounds.Top - Math.Min(100, host.ActualHeight / 2)) * scale)
        };
        var popup = Native.GetAncestor(DropWindowAtPoint(point), 2);
        flyoutDropLocation = $"point={point.X},{point.Y}; owner={owner:X}; popup={popup:X}";
        if (popup == IntPtr.Zero || popup == owner || DropWindowThread(popup, out uint process) == 0 || process != Environment.ProcessId
            || !GetBackplateWindowRect(popup, out var bounds)
            || Math.Abs((bounds.Right - bounds.Left) / scale - host.ActualWidth) > 40
            || Math.Abs((bounds.Bottom - bounds.Top) / scale - host.ActualHeight) > 48) return IntPtr.Zero;
        return popup;
    }

    async Task ReceiveFlyoutFiles(string[] paths, string provider)
    {
        if (destroyed) return;
        using var lease = KeepOpenForDrag();
        try
        {
            var view = await DropPayload.FilesView(paths);
            if (!await ReceiveDrop(view, provider)) SetStatus("当前正在处理其他内容，请稍后重新拖入。");
        }
        catch (Exception failure) when (failure is not OutOfMemoryException) { SetStatus("拖入未完成：" + failure.Message); }
        finally { await EndWebDrag(); }
    }

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] static extern IntPtr DropWindowAtPoint(Native.Point point);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] static extern uint DropWindowThread(IntPtr window, out uint process);
    void AttachDropTarget(FrameworkElement target)
    {
        if (target.Resources.ContainsKey("AIEnterDropBound")) return;
        target.Resources["AIEnterDropBound"] = true; target.AllowDrop = true;
        target.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(DragOverTarget), true);
        target.AddHandler(UIElement.DragOverEvent, new DragEventHandler(DragOverTarget), true);
        target.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler((_, _) => dropHint.Visibility = Visibility.Collapsed), true);
        target.AddHandler(UIElement.DropEvent, new DragEventHandler(DropOnTarget), true);
    }
    void DragOverTarget(object sender, DragEventArgs args)
    {
        if (!DropPayload.Supported(args.DataView) || transferringDrop || ModalActive) return;
        args.AcceptedOperation = DataPackageOperation.Copy; args.Handled = true;
        string name = AiPolicy.Resolve(options.Data).Name;
        args.DragUIOverride.Caption = "放入 " + name + "，不自动发送";
        args.DragUIOverride.IsCaptionVisible = true;
        SetDropHint(true);
    }

    void SetDropHint(bool visible)
    {
        var next = visible ? Visibility.Visible : Visibility.Collapsed;
        if (dropHint.Visibility == next) return;
        if (visible)
            dropHint.Child = new TextBlock { Text = "松开放入 " + AiPolicy.Resolve(options.Data).Name + " · 不自动发送",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        dropHint.Visibility = next;
    }
    async void DropOnTarget(object sender, DragEventArgs args)
    {
        if (consumedDrops.TryGetValue(args, out _) || !DropPayload.Supported(args.DataView)) return;
        consumedDrops.Add(args, new object());
        args.Handled = true; args.AcceptedOperation = DataPackageOperation.Copy;
        var deferral = args.GetDeferral(); dropHint.Visibility = Visibility.Collapsed;
        try { if (!await ReceiveDrop(args.DataView)) SetStatus("当前正在处理其他内容，请稍后重新拖入。"); }
        catch (Exception failure) when (failure is not OutOfMemoryException) { SetStatus("拖入未完成：" + failure.Message); }
        finally { deferral.Complete(); await EndWebDrag(); }
    }
}
