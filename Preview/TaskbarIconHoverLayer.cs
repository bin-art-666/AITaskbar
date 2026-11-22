using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class TaskbarIconHoverLayer
{
    private readonly Border frame = new() { Opacity = 0 };
    private readonly RectangleGeometry scrollClip = new();
    private FrameworkElement? icon, viewport;
    private int expansion;
    private bool show, suspended;
    internal Canvas Surface { get; } = new() { IsHitTestVisible = false };

    internal TaskbarIconHoverLayer(bool animate)
    {
        if (animate) frame.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) };
        Surface.Children.Add(frame);
        Surface.LayoutUpdated += (_, _) => Reposition();
    }

    internal void Track(FrameworkElement target, FrameworkElement? scrollViewport = null)
    {
        target.PointerEntered += (_, args) =>
        {
            if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
            icon = target;
            viewport = scrollViewport;
            Reposition();
        };
        target.PointerExited += (_, _) => Hide(target);
        target.Unloaded += (_, _) => Hide(target);
    }

    internal void ApplyStyle(TaskbarAppFeedbackStyle style, bool light, UISettings system, bool highContrast)
    {
        show = style.ShowHover;
        expansion = style.HoverExpansion;
        frame.Background = new SolidColorBrush(highContrast ? system.UIElementColor(UIElementType.Highlight)
            : light ? Color.FromArgb(42, 0, 0, 0) : Color.FromArgb(95, 255, 255, 255));
        Reposition();
    }

    internal void Suspend(bool value)
    {
        suspended = value;
        Reposition();
    }

    private void Hide(FrameworkElement target)
    {
        if (!ReferenceEquals(icon, target)) return;
        icon = viewport = null;
        frame.Opacity = 0;
    }

    internal void Reposition()
    {
        if (suspended || !show || icon is not { IsLoaded: true } target || Surface.XamlRoot is not { } root || target.XamlRoot != root)
        {
            if (frame.Opacity != 0) frame.Opacity = 0;
            return;
        }
        var origin = target.TransformToVisual(Surface).TransformPoint(new Point());
        double left = origin.X - expansion, top = origin.Y - expansion;
        double width = target.ActualWidth + 2 * expansion, height = target.ActualHeight + 2 * expansion;
        if (Canvas.GetLeft(frame) != left) Canvas.SetLeft(frame, left);
        if (Canvas.GetTop(frame) != top) Canvas.SetTop(frame, top);
        if (frame.Width != width || frame.Height != height)
        {
            frame.Width = width;
            frame.Height = height;
            frame.CornerRadius = new CornerRadius(target.ActualWidth * (6d / 26) + expansion);
        }
        if (viewport is { Clip: RectangleGeometry clip })
        {
            var area = viewport.TransformToVisual(Surface).TransformBounds(clip.Rect);
            var rect = new Rect(area.X - left, area.Y - top, area.Width, area.Height);
            if (!scrollClip.Rect.Equals(rect)) scrollClip.Rect = rect;
            if (frame.Clip is null) frame.Clip = scrollClip;
        }
        else if (frame.Clip is not null) frame.Clip = null;
        if (frame.Opacity != 1) frame.Opacity = 1;
    }
}
