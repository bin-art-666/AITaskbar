using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal enum AppWindowState { Closed, Background, Foreground }

internal sealed class TaskbarAppFeedback
{
    internal const int TopSpace = TaskbarAppFeedbackStyle.MaxExpansion;
    internal const int ExtraHeight = 2 * TopSpace + 2 + TaskbarAppFeedbackStyle.MaxDiameter;
    private readonly Border dot = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly int size;
    internal Canvas Surface { get; }

    internal static (Color Active, Color Background) DotColors(TaskbarAppFeedbackStyle style, UISettings system, bool highContrast) =>
        highContrast
            ? (system.UIElementColor(UIElementType.Highlight), system.GetColorValue(UIColorType.Foreground))
            : (TaskbarAppFeedbackStyle.Parse(style.Active) ?? Microsoft.UI.Colors.DodgerBlue,
                TaskbarAppFeedbackStyle.Parse(style.Background) ?? Microsoft.UI.Colors.Black);

    internal TaskbarAppFeedback(FrameworkElement icon, int size)
    {
        this.size = size;
        Surface = new() { Width = size, Height = size + ExtraHeight };
        Canvas.SetTop(icon, TopSpace);
        Surface.Children.Add(icon);
        Surface.Children.Add(dot);
    }

    internal void ApplyStyle(TaskbarAppFeedbackStyle style)
    {
        dot.Width = dot.Height = style.Diameter;
        dot.CornerRadius = new CornerRadius(style.Diameter / 2d);
        Canvas.SetLeft(dot, (size - style.Diameter) / 2d);
        Canvas.SetTop(dot, size + TopSpace + style.HoverExpansion + 2);
    }

    internal void ShowState(AppWindowState state, Color active, Color background, bool light)
    {
        dot.Visibility = state == AppWindowState.Closed ? Visibility.Collapsed : Visibility.Visible;
        if (state == AppWindowState.Closed) return;
        var color = state == AppWindowState.Foreground ? active : background;
        if (dot.Background is not SolidColorBrush fill || fill.Color != color)
            dot.Background = new SolidColorBrush(color);
        bool outline = !light && state == AppWindowState.Background && color != Microsoft.UI.Colors.Black &&
            color.R < 96 && color.G < 96 && color.B < 96;
        dot.BorderThickness = outline ? new Thickness(.5) : new Thickness(0);
        if (outline && dot.BorderBrush is null)
            dot.BorderBrush = new SolidColorBrush(Color.FromArgb(185, 255, 255, 255));
    }
}
