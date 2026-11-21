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
    private readonly Border hover = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly Border dot = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly int size;
    private bool hovering, showHover;
    internal Canvas Surface { get; }

    internal static (Color Active, Color Background) DotColors(TaskbarAppFeedbackStyle style, UISettings system, bool highContrast) =>
        highContrast
            ? (system.UIElementColor(UIElementType.Highlight), system.GetColorValue(UIColorType.Foreground))
            : (TaskbarAppFeedbackStyle.Parse(style.Active) ?? Microsoft.UI.Colors.DodgerBlue,
                TaskbarAppFeedbackStyle.Parse(style.Background) ?? Microsoft.UI.Colors.Black);

    internal TaskbarAppFeedback(FrameworkElement icon, int size, bool animate)
    {
        this.size = size;
        if (animate) hover.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) };
        Surface = new() { Width = size, Height = size + ExtraHeight };
        Canvas.SetTop(icon, TopSpace);
        Surface.Children.Add(hover);
        Surface.Children.Add(icon);
        Surface.Children.Add(dot);
        icon.PointerEntered += (_, args) =>
        {
            hovering = args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse;
            UpdateHover();
        };
        icon.PointerExited += (_, _) => { hovering = false; UpdateHover(); };
    }

    internal void ApplyStyle(TaskbarAppFeedbackStyle style, bool light, UISettings system, bool highContrast)
    {
        showHover = style.ShowHover;
        int expansion = style.HoverExpansion;
        hover.Width = hover.Height = size + 2 * expansion;
        hover.CornerRadius = new CornerRadius(size * (6d / 26) + expansion);
        Canvas.SetLeft(hover, -expansion);
        Canvas.SetTop(hover, TopSpace - expansion);
        hover.Background = new SolidColorBrush(highContrast ? system.UIElementColor(UIElementType.Highlight)
            : light ? Color.FromArgb(42, 0, 0, 0) : Color.FromArgb(95, 255, 255, 255));
        UpdateHover();

        dot.Width = dot.Height = style.Diameter;
        dot.CornerRadius = new CornerRadius(style.Diameter / 2d);
        Canvas.SetLeft(dot, (size - style.Diameter) / 2d);
        Canvas.SetTop(dot, size + TopSpace + expansion + 2);
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

    private void UpdateHover() => hover.Opacity = hovering && showHover ? 1 : 0;
}
