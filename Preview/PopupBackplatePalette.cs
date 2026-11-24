using Microsoft.UI.Xaml;
using Windows.UI;

namespace AITaskbar;

internal readonly record struct PopupBackplatePalette(ElementTheme Theme, Color Tint, Color Solid, float Blur)
{
    internal const float Radius = 14;

    internal static PopupBackplatePalette Fixed(ElementTheme theme, Color background) =>
        new(theme, TaskbarPanelFlyout.Alpha(background, 208), background, 8.5f);
}
