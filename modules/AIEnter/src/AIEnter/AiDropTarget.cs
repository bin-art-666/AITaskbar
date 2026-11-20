using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AIEnter;

internal sealed partial class AiWindow
{
    readonly ConditionalWeakTable<DragEventArgs, object> consumedDrops = new();
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
        var shell = (Grid)((Border)Content).Child;
        Canvas.SetZIndex(dropHint, 100); shell.Children.Add(dropHint);
        AttachDropTarget((FrameworkElement)Content);
    }
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
        dropHint.Child = new TextBlock { Text = "松开放入 " + name + " · 不自动发送", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        dropHint.Visibility = Visibility.Visible;
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