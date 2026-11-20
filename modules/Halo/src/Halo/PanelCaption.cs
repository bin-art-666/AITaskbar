using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Halo;

// Register OS caption hit regions, rather than synthesizing mouse messages from XAML events.
internal sealed class PanelCaption : IDisposable
{
    readonly Window owner;
    readonly FrameworkElement header, controls;
    readonly Func<bool> enabled;
    readonly FrameworkElement? leading;
    readonly bool nativeResizeRegions;
    InputNonClientPointerSource? source;
    bool disposed, queued, suspended;
    static readonly NonClientRegionKind[] ResizeRegions = [NonClientRegionKind.TopBorder, NonClientRegionKind.LeftBorder, NonClientRegionKind.BottomBorder, NonClientRegionKind.RightBorder];
    internal PanelCaption(Window owner, FrameworkElement header, FrameworkElement controls, Func<bool> enabled, FrameworkElement? leading=null, bool nativeResizeRegions=true)
    {
        this.owner = owner; this.header = header; this.controls = controls; this.enabled = enabled;this.leading=leading; this.nativeResizeRegions = nativeResizeRegions;
        header.Loaded += Loaded; header.SizeChanged += SizeChanged; controls.SizeChanged += SizeChanged;
        if(leading!=null)leading.SizeChanged+=SizeChanged;
        owner.AppWindow.Changed += WindowChanged;
    }
    void Loaded(object sender, RoutedEventArgs args) => Queue();
    void SizeChanged(object sender, SizeChangedEventArgs args) => Queue();
    void WindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    { if (args.DidSizeChange) Queue(); }
    internal void Queue()
    {
        if (disposed || queued) return; queued = true;
        if (!owner.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => { queued = false; Refresh(); })) queued = false;
    }
    internal void Suspend() { suspended = true; Clear(); }
    internal void Resume() { suspended = false; Queue(); }
    void Clear() {
        source ??= InputNonClientPointerSource.GetForWindowId(owner.AppWindow.Id);
        source.ClearRegionRects(NonClientRegionKind.Caption);
        foreach (var region in ResizeRegions) source.ClearRegionRects(region);
    }
    internal void Refresh()
    {
        if (disposed || suspended) return;
        source ??= InputNonClientPointerSource.GetForWindowId(owner.AppWindow.Id);
        if (!enabled() || owner.Content is not FrameworkElement root || header.XamlRoot == null || header.ActualHeight <= 0 || controls.ActualWidth <= 0)
        { source.ClearRegionRects(NonClientRegionKind.Caption); foreach (var region in ResizeRegions) source.ClearRegionRects(region); return; }
        double scale = header.XamlRoot.RasterizationScale;
        var origin = new Native.Point(); var handle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        ClientToScreen(handle, ref origin); GetWindowRect(handle, out var window);
        int grip = Math.Max(4, (int)Math.Round(5 * scale));
        int width = window.Right - window.Left, height = window.Bottom - window.Top;
        if (nativeResizeRegions) {
        source.SetRegionRects(NonClientRegionKind.TopBorder, [new(0, 0, width, grip)]);
        source.SetRegionRects(NonClientRegionKind.BottomBorder, [new(0, height - grip, width, grip)]);
        source.SetRegionRects(NonClientRegionKind.LeftBorder, [new(0, grip, grip, Math.Max(1, height - grip * 2))]);
        source.SetRegionRects(NonClientRegionKind.RightBorder, [new(width - grip, grip, grip, Math.Max(1, height - grip * 2))]);
        } else foreach (var region in ResizeRegions) source.ClearRegionRects(region);
        var position = header.TransformToVisual(root).TransformPoint(new());
        int captionInset = nativeResizeRegions ? grip : (int)Math.Ceiling(24 * scale);
        int x = origin.X - window.Left + captionInset;
        int y = origin.Y - window.Top + grip;
        int right = origin.X - window.Left + (int)Math.Floor(root.ActualWidth * scale) - captionInset;
        int bottom = origin.Y - window.Top + (int)Math.Ceiling((position.Y + header.ActualHeight) * scale);
        var regions = new List<RectInt32>();
        if (right > x && bottom > y) regions.Add(new(x, y, right - x, bottom - y));
        // Navigation stretches across the empty header. Exclude the actual buttons,
        // rather than excluding their entire container and losing the drag space.
        foreach (var button in HeaderButtons(header))
        {
            var point = button.TransformToVisual(root).TransformPoint(new());
            int padding = (int)Math.Ceiling(2 * scale);
            var excluded = new RectInt32(origin.X - window.Left + (int)Math.Floor(point.X * scale) - padding,
                origin.Y - window.Top + (int)Math.Floor(point.Y * scale) - padding,
                (int)Math.Ceiling(button.ActualWidth * scale) + 2 * padding,
                (int)Math.Ceiling(button.ActualHeight * scale) + 2 * padding);
            regions = regions.SelectMany(region => Subtract(region, excluded)).ToList();
        }
        source.SetRegionRects(NonClientRegionKind.Caption, regions.ToArray());
    }
    static IEnumerable<Microsoft.UI.Xaml.Controls.Primitives.ButtonBase> HeaderButtons(DependencyObject node)
    {
        if (node is FrameworkElement element && (element.Visibility != Visibility.Visible || element.Opacity == 0)) yield break;
        if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button)
        {
            if (button.ActualWidth > 0 && button.ActualHeight > 0) yield return button;
            yield break;
        }
        for (int index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var child in HeaderButtons(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, index))) yield return child;
    }
    static IEnumerable<RectInt32> Subtract(RectInt32 area, RectInt32 excluded)
    {
        int left = Math.Max(area.X, excluded.X), top = Math.Max(area.Y, excluded.Y);
        int right = Math.Min(area.X + area.Width, excluded.X + excluded.Width);
        int bottom = Math.Min(area.Y + area.Height, excluded.Y + excluded.Height);
        if (left >= right || top >= bottom) { yield return area; yield break; }
        if (top > area.Y) yield return new(area.X, area.Y, area.Width, top - area.Y);
        if (bottom < area.Y + area.Height) yield return new(area.X, bottom, area.Width, area.Y + area.Height - bottom);
        if (left > area.X) yield return new(area.X, top, left - area.X, bottom - top);
        if (right < area.X + area.Width) yield return new(right, top, area.X + area.Width - right, bottom - top);
    }
    internal async Task Verify(bool active)
    {
        if (owner.Content is FrameworkElement root) root.UpdateLayout();
        await Task.Delay(120); Refresh();
        var rectangles = source!.GetRegionRects(NonClientRegionKind.Caption) ?? [];
        foreach (var kind in ResizeRegions) if (((source.GetRegionRects(kind)?.Length ?? 0) > 0) != (active && nativeResizeRegions)) throw new Exception("WinUI resize regions do not match the window mode.");
        if (!active) { if (rectangles.Length != 0) throw new Exception("Docked panel retained caption drag region"); return; }
        if (rectangles.Length == 0) throw new Exception("Detached window has no native caption region");
        var rectangle = rectangles[0]; var handle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        GetWindowRect(handle, out var window);
        if (!rectangles.Any(region => HitTest(handle, window.Left + region.X + region.Width / 2, window.Top + region.Y + region.Height / 2) == 2))
            throw new Exception("No registered header area is recognized as a native caption: " + string.Join(",", rectangles.Select(region => HitTest(handle, window.Left + region.X + region.Width / 2, window.Top + region.Y + region.Height / 2))));
        var bodyPoint = new Native.Point { X = (int)(((FrameworkElement)owner.Content).ActualWidth * header.XamlRoot.RasterizationScale / 2), Y = (int)((header.ActualHeight + ((FrameworkElement)owner.Content).ActualHeight) * header.XamlRoot.RasterizationScale / 2) };
        ClientToScreen(handle, ref bodyPoint);
        if (HitTest(handle, bodyPoint.X, bodyPoint.Y) == 2) throw new Exception("内容空白区域不能拖动窗口。");
        var rootElement = (UIElement)owner.Content;
        foreach (var button in HeaderButtons(header))
        {
            var point = button.TransformToVisual(rootElement).TransformPoint(new(button.ActualWidth / 2, button.ActualHeight / 2));
            var client = new Native.Point { X = (int)Math.Round(point.X * header.XamlRoot.RasterizationScale), Y = (int)Math.Round(point.Y * header.XamlRoot.RasterizationScale) };
            ClientToScreen(handle, ref client);
            if (HitTest(handle, client.X, client.Y) == 2) throw new Exception("Caption overlaps an AI service or window control button.");
        }
        // The middle can contain an AI button when the default list grows.
        // Independently find the widest actual horizontal gap instead.
        var spans = HeaderButtons(header).Select(button =>
        {
            double left = button.TransformToVisual(header).TransformPoint(new()).X;
            return (Left: left - 2, Right: left + button.ActualWidth + 2);
        }).OrderBy(span => span.Left);
        double cursor = 0, gapLeft = 0, gapWidth = 0;
        foreach (var span in spans.Append((Left: header.ActualWidth, Right: header.ActualWidth)))
        {
            if (span.Left - cursor > gapWidth) { gapLeft = cursor; gapWidth = span.Left - cursor; }
            cursor = Math.Max(cursor, span.Right);
        }
        if (gapWidth > 10)
        {
            var middle = header.TransformToVisual(rootElement).TransformPoint(new(gapLeft + gapWidth / 2, header.ActualHeight / 2));
            var client = new Native.Point { X = (int)Math.Round(middle.X * header.XamlRoot.RasterizationScale), Y = (int)Math.Round(middle.Y * header.XamlRoot.RasterizationScale) };
            ClientToScreen(handle, ref client);
            if (HitTest(handle, client.X, client.Y) != 2) throw new Exception("The header's empty gap is not draggable.");
        }
    }

    static int HitTest(IntPtr window, int x, int y)
    {
        var coordinate = new IntPtr(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
        if (SendMessageTimeout(window, 0x84, IntPtr.Zero, coordinate, 2, 1000, out var result) == IntPtr.Zero) throw new Exception("Caption hit test timed out");
        return result.ToInt32();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        header.Loaded -= Loaded; header.SizeChanged -= SizeChanged; controls.SizeChanged -= SizeChanged; owner.AppWindow.Changed -= WindowChanged;
        if(leading!=null)leading.SizeChanged-=SizeChanged;
        try { source?.ClearRegionRects(NonClientRegionKind.Caption); foreach (var region in ResizeRegions) source?.ClearRegionRects(region); } catch { /* Window may already be destroyed. */ }
    }
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Native.Point point);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Native.Rect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
