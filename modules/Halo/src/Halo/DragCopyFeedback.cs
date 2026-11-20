using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Halo;

internal sealed class DragCopyFeedback
{
    internal const string InvalidTarget = "请拖到空格或文件夹";
    readonly Canvas layer = new() { IsHitTestVisible = false };
    readonly TextBlock label = new() { FontSize = 11 };
    readonly Border badge = new() {
        CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 3, 6, 3), Visibility = Visibility.Collapsed,
    };
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    bool current;

    internal DragCopyFeedback(Grid root, Action<bool> modifierChanged)
    {
        badge.Child = label;
        layer.Children.Add(badge);
        Grid.SetRowSpan(layer, root.RowDefinitions.Count);
        root.Children.Add(layer);
        timer = root.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(35);
        timer.Tick += (_, _) => {
            bool pressed = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
            if (pressed == current) return;
            current = pressed;
            modifierChanged(pressed);
        };
    }

    internal void Watch(bool copy)
    {
        current = copy;
        timer.Start();
    }

    internal void Show(GridView grid, Point point, string? caption)
    {
        badge.Visibility = caption == null ? Visibility.Collapsed : Visibility.Visible;
        if (caption == null) return;
        label.Text = caption == InvalidTarget ? caption : "+  " + caption;
        var location = grid.TransformToVisual(layer).TransformPoint(point);
        Canvas.SetLeft(badge, Math.Clamp(location.X + 12, 0, Math.Max(0, layer.ActualWidth - 135)));
        Canvas.SetTop(badge, Math.Clamp(location.Y + 4, 0, Math.Max(0, layer.ActualHeight - 25)));
    }

    internal void ApplyColors(StationInteractionColors colors)
    {
        badge.Background = new SolidColorBrush(colors.Drag);
        label.Foreground = new SolidColorBrush(PanelTheme.ReadableText(colors.Drag, layer.ActualTheme == ElementTheme.Dark));
    }

    internal void Clear()
    {
        timer.Stop();
        badge.Visibility = Visibility.Collapsed;
    }
}
