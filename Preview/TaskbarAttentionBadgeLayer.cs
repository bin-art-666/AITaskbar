using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AITaskbar;

internal sealed class TaskbarAttentionBadgeLayer
{
    private sealed record Entry(Border Badge, Button Button, FrameworkElement Feedback, FrameworkElement? Viewport)
    {
        internal TaskbarAttentionIndicator? Pulse { get; set; }
    }
    private readonly Dictionary<FrameworkElement, Entry> badges = [];
    private readonly Border leftOverflow = CreateOverflowMarker(), rightOverflow = CreateOverflowMarker();
    private FrameworkElement? overflowViewport;
    internal Canvas Surface { get; } = new() { IsHitTestVisible = false };
    internal Canvas PulseSurface { get; } = new() { IsHitTestVisible = false };

    internal TaskbarAttentionBadgeLayer()
    {
        Surface.Children.Add(leftOverflow);
        Surface.Children.Add(rightOverflow);
        Surface.LayoutUpdated += (_, _) => Reposition();
    }

    private static Border CreateOverflowMarker()
    {
        var marker = TaskbarAttentionIndicator.CreateBadge();
        marker.Width = marker.Height = 6;
        marker.CornerRadius = new CornerRadius(3);
        marker.Visibility = Visibility.Collapsed;
        return marker;
    }

    internal void SetOverflow(FrameworkElement viewport, bool left, bool right, Color? highlight)
    {
        overflowViewport = left || right ? viewport : null;
        leftOverflow.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        rightOverflow.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        if (left) TaskbarAttentionIndicator.ColorBadge(leftOverflow, highlight);
        if (right) TaskbarAttentionIndicator.ColorBadge(rightOverflow, highlight);
    }

    internal void Set(FrameworkElement icon, Button button, FrameworkElement feedback, FrameworkElement? viewport,
        bool pending, bool animate, Color? highlight)
    {
        if (!pending)
        {
            Remove(icon);
            return;
        }
        if (!badges.TryGetValue(icon, out var entry))
        {
            entry = new(TaskbarAttentionIndicator.CreateBadge(), button, feedback, viewport);
            badges.Add(icon, entry);
            icon.Unloaded += OnIconUnloaded;
            Surface.Children.Add(entry.Badge);
        }
        TaskbarAttentionIndicator.ColorBadge(entry.Badge, highlight);
        if (!animate) StopPulse(entry);
    }

    internal void Pulse(TaskbarIconPlate icon)
    {
        if (!badges.TryGetValue(icon, out var entry) || !icon.IsLoaded || icon.ActualWidth <= 0) return;
        StopPulse(entry);
        var pulse = new TaskbarAttentionIndicator(icon.ActualWidth, icon.ActualHeight, icon.Artwork.CornerRadius.TopLeft);
        entry.Pulse = pulse;
        PulseSurface.Children.Add(pulse);
        Reposition();
        pulse.Pulse(() => { if (ReferenceEquals(entry.Pulse, pulse)) StopPulse(entry); });
    }

    internal void Reposition()
    {
        if (overflowViewport is { IsLoaded: true } edgeViewport && Surface.XamlRoot is { } layoutRoot && edgeViewport.XamlRoot == layoutRoot)
        {
            var origin = edgeViewport.TransformToVisual(Surface).TransformPoint(new Point());
            double top = origin.Y + TaskbarAppFeedback.TopSpace;
            if (leftOverflow.Visibility == Visibility.Visible) Place(leftOverflow, origin.X - leftOverflow.Width, top, 1, null, 0, 0);
            if (rightOverflow.Visibility == Visibility.Visible) Place(rightOverflow, origin.X + edgeViewport.ActualWidth, top, 1, null, 0, 0);
        }
        foreach (var (icon, entry) in badges)
        {
            if (Surface.XamlRoot is not { } root || !icon.IsLoaded || icon.XamlRoot != root || icon.ActualWidth <= 0)
            {
                entry.Badge.Opacity = 0;
                if (entry.Pulse is { } unplaced) unplaced.Opacity = 0;
                continue;
            }
            double opacity = entry.Button.Opacity * entry.Feedback.Opacity;
            var origin = icon.TransformToVisual(Surface).TransformPoint(new Point());
            Rect? area = entry.Viewport is { Clip: RectangleGeometry clip } viewport
                ? viewport.TransformToVisual(Surface).TransformBounds(clip.Rect) : null;
            Place(entry.Badge, origin.X + TaskbarAttentionIndicator.BadgeLeft(icon.ActualWidth),
                origin.Y - TaskbarAttentionIndicator.BadgeOverhang, opacity, area, 0, TaskbarAttentionIndicator.BadgeOverhang);
            if (entry.Pulse is { } pulse)
            {
                // The two layer canvases share the same origin in the taskbar grid.
                double padding = TaskbarAttentionIndicator.RingOverhang;
                Place(pulse, origin.X - padding, origin.Y - padding, opacity, area, padding, padding);
            }
        }
    }

    private static void Place(FrameworkElement element, double left, double top, double opacity, Rect? clip,
        double leftPadding, double rightPadding)
    {
        if (element.Opacity != opacity) element.Opacity = opacity;
        if (Canvas.GetLeft(element) != left) Canvas.SetLeft(element, left);
        if (Canvas.GetTop(element) != top) Canvas.SetTop(element, top);
        if (clip is { } area)
        {
            var bounds = new Rect(area.X - left - leftPadding, area.Y - top,
                area.Width + leftPadding + rightPadding, area.Height);
            if (element.Clip is not RectangleGeometry geometry) element.Clip = new RectangleGeometry { Rect = bounds };
            else if (!geometry.Rect.Equals(bounds)) geometry.Rect = bounds;
        }
        else if (element.Clip is not null) element.Clip = null;
    }

    private void StopPulse(Entry entry)
    {
        if (entry.Pulse is not { } pulse) return;
        entry.Pulse = null;
        pulse.StopPulse();
        PulseSurface.Children.Remove(pulse);
    }

    private void OnIconUnloaded(object sender, RoutedEventArgs args) => Remove((FrameworkElement)sender);

    private void Remove(FrameworkElement icon)
    {
        if (!badges.Remove(icon, out var entry)) return;
        icon.Unloaded -= OnIconUnloaded;
        StopPulse(entry);
        Surface.Children.Remove(entry.Badge);
    }
}
