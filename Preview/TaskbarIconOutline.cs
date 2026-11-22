using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AITaskbar;

internal static class TaskbarIconOutline
{
    internal static Border Create() => new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };

    internal static void Apply(Border outline, PreviewIconLayout layout, bool highContrast)
    {
        outline.Visibility = layout.ShowIconOutline && !highContrast ? Visibility.Visible : Visibility.Collapsed;
        if (outline.Visibility == Visibility.Collapsed) return;
        var color = layout.ColorMode == IconColorMode.Dark ? Color.FromArgb(142, 116, 195, 255)
            : Color.FromArgb(110, 111, 165, 211);
        if (outline.BorderBrush is not SolidColorBrush brush || brush.Color != color)
            outline.BorderBrush = new SolidColorBrush(color);
    }
}

internal sealed class TaskbarIconPlate : Grid
{
    internal Border Artwork { get; }
    internal Border Outline { get; }

    internal TaskbarIconPlate(int size, FrameworkElement graphic)
    {
        Width = Height = size;
        var radius = new CornerRadius(size * (6d / 26));
        Artwork = new Border { CornerRadius = radius, Child = graphic };
        Outline = TaskbarIconOutline.Create();
        Outline.CornerRadius = radius;
        Children.Add(Artwork);
        Children.Add(Outline);
    }
}
