using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class IconSettingsPreview
{
    private readonly UISettings system;
    private readonly AccessibilitySettings accessibility;
    private readonly TaskbarIconHoverLayer iconHover;
    private readonly StackPanel icons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel fixedIcons = new() { Orientation = Orientation.Horizontal };
    private readonly AppCenterIcon? appCenterIcon;
    private readonly TaskViewIcon? taskViewIcon;
    private readonly Canvas running = new();
    private readonly List<(AppWindowState State, TaskbarAppFeedback Visual)> examples = [];
    private readonly List<Border> iconPlates = [];
    private TaskbarAppFeedbackStyle feedback = TaskbarAppFeedbackStyle.Default;
    private PreviewIconLayout layout = PreviewIconLayout.Default;
    private int iconSize;
    private bool later;
    private long? started;
    private double offset, from, target;
    private int Step => iconSize + layout.Gap;
    internal Border Surface { get; } = CreateCard();

    internal IconSettingsPreview(UISettings system, AccessibilitySettings accessibility, bool scrollDemo = false)
    {
        this.system = system;
        this.accessibility = accessibility;
        iconHover = new(system.AnimationsEnabled && !accessibility.HighContrast);
        if (!scrollDemo)
        {
            appCenterIcon = new AppCenterIcon(PreviewIconSize.Default)
            {
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, TaskbarAppFeedback.TopSpace, 0, 0)
            };
            ToolTipService.SetToolTip(appCenterIcon, "应用中心");
            AutomationProperties.SetName(appCenterIcon, "应用中心");
            iconHover.Track(appCenterIcon);
            taskViewIcon = new TaskViewIcon(PreviewIconSize.Default)
            {
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, TaskbarAppFeedback.TopSpace, 0, 0)
            };
            ToolTipService.SetToolTip(taskViewIcon, "任务视图");
            AutomationProperties.SetName(taskViewIcon, "任务视图");
            iconHover.Track(taskViewIcon);
        }
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumnSpan(iconHover.Surface, 2);
        row.Children.Add(iconHover.Surface);
        row.Children.Add(icons);
        if (scrollDemo)
        {
            var play = new Button { Content = "试滚动", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(play, "试滚动运行区");
            play.Click += (_, _) => Scroll();
            Grid.SetColumn(play, 1);
            row.Children.Add(play);
        }
        var groups = new StackPanel { Spacing = 5 };
        groups.Children.Add(PreviewLabel(scrollDemo ? "运行区滚动预览" : "应用图标预览"));
        groups.Children.Add(row);
        Surface.Child = groups;
        Surface.ActualThemeChanged += (_, _) => ApplyFeedback(feedback);
        Surface.Unloaded += (_, _) => FinishScroll();
    }

    internal void ApplyLayout(int size, PreviewIconLayout next)
    {
        bool newSize = iconSize != size;
        bool geometryChanged = newSize || layout.Gap != next.Gap || layout.Animation != next.Animation;
        bool recolor = newSize || layout.ColorMode != next.ColorMode;
        iconSize = size;
        layout = next;
        if (newSize)
        {
            icons.Children.Clear();
            fixedIcons.Children.Clear();
            running.Children.Clear();
            examples.Clear();
            iconPlates.Clear();
            if (appCenterIcon is not null)
            {
                appCenterIcon.SetSize(iconSize);
                fixedIcons.Children.Add(appCenterIcon);
            }
            if (taskViewIcon is not null)
            {
                taskViewIcon.SetSize(iconSize);
                fixedIcons.Children.Add(taskViewIcon);
            }
            fixedIcons.Children.Add(AddExample("未打开", "\uE8A5", AppWindowState.Closed));
            icons.Children.Add(fixedIcons);
            icons.Children.Add(new Border { Width = 1, Height = 18, Opacity = .5,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center });
            icons.Children.Add(running);
            running.Children.Add(AddExample("后台", "\uE715", AppWindowState.Background));
            running.Children.Add(AddExample("前台", "\uE8B7", AppWindowState.Foreground));
            running.Children.Add(AddExample("后台", "\uE7C5", AppWindowState.Background));
        }
        if (geometryChanged)
        {
            fixedIcons.Spacing = layout.Gap;
            running.Width = 2 * iconSize + layout.Gap;
            running.Height = iconSize + TaskbarAppFeedback.ExtraHeight;
            FinishScroll();
        }
        if (newSize) ApplyFeedback(feedback);
        else if (recolor) ApplyIconColors();
    }

    internal void ApplyAppCenterStyle(AppCenterIconStyle style)
    {
        if (appCenterIcon is null) return;
        appCenterIcon.Apply(style);
        appCenterIcon.Visibility = style.ShowInTaskbar ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ApplyFeedback(TaskbarAppFeedbackStyle style)
    {
        feedback = style;
        bool light = Surface.ActualTheme != ElementTheme.Dark;
        bool highContrast = accessibility.HighContrast;
        Surface.Background = new SolidColorBrush(highContrast ? system.GetColorValue(UIColorType.Background)
            : light ? Color.FromArgb(255, 229, 235, 243) : Color.FromArgb(255, 38, 53, 75));
        var (active, background) = TaskbarAppFeedback.DotColors(style, system, highContrast);
        iconHover.ApplyStyle(style, light, system, highContrast);
        foreach (var (state, visual) in examples)
        {
            visual.ApplyStyle(style);
            visual.ShowState(state, active, background, light);
        }
        ApplyIconColors();
    }

    internal static Border CreateCard() => new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8) };

    internal static TextBlock PreviewLabel(string text) => new() { Text = text, FontSize = 12, Opacity = .76 };

    private void ApplyIconColors()
    {
        bool highContrast = accessibility.HighContrast;
        var plate = new SolidColorBrush(highContrast ? system.GetColorValue(UIColorType.Background)
            : AppIconArtwork.BackplateColor(layout.ColorMode));
        var ink = new SolidColorBrush(highContrast ? system.GetColorValue(UIColorType.Foreground)
            : AppIconArtwork.FallbackColor(layout.ColorMode));
        foreach (var icon in iconPlates)
        {
            icon.Background = plate;
            ((FontIcon)icon.Child).Foreground = ink;
        }
    }

    private Canvas AddExample(string label, string glyph, AppWindowState state)
    {
        var icon = new Border
        {
            Width = iconSize, Height = iconSize, CornerRadius = new CornerRadius(iconSize * (6d / 26)),
            Child = new FontIcon { Glyph = glyph, FontSize = iconSize * .6 }
        };
        iconPlates.Add(icon);
        ToolTipService.SetToolTip(icon, label);
        AutomationProperties.SetName(icon, label);
        var visual = new TaskbarAppFeedback(icon, iconSize);
        iconHover.Track(icon, state == AppWindowState.Closed ? null : running);
        examples.Add((state, visual));
        return visual.Surface;
    }

    private void Scroll()
    {
        later = !later;
        if (layout.DurationMs == 0 || !system.AnimationsEnabled || accessibility.HighContrast)
        {
            FinishScroll();
            return;
        }
        from = offset;
        target = later ? Step : 0;
        if (started is null)
        {
            running.Clip = new RectangleGeometry { Rect = new Rect(0, 0, running.Width, running.Height) };
            foreach (var icon in running.Children) icon.Visibility = Visibility.Visible;
            CompositionTarget.Rendering += Animate;
        }
        started = Stopwatch.GetTimestamp();
    }

    private void Animate(object? sender, object args)
    {
        if (started is not { } start) return;
        double progress = Math.Min(1, Stopwatch.GetElapsedTime(start).TotalMilliseconds / layout.DurationMs);
        if (progress == 1)
        {
            FinishScroll();
            return;
        }
        offset = PreviewIconLayout.ScrollOffset(from, target, progress);
        PositionIcons();
    }

    private void FinishScroll()
    {
        if (started is not null) CompositionTarget.Rendering -= Animate;
        started = null;
        running.Clip = null;
        int first = later ? 1 : 0;
        for (int index = 0; index < running.Children.Count; index++)
            running.Children[index].Visibility = index == first || index == first + 1 ? Visibility.Visible : Visibility.Collapsed;
        offset = later ? Step : 0;
        PositionIcons();
    }

    private void PositionIcons()
    {
        for (int index = 0; index < running.Children.Count; index++)
        {
            var icon = running.Children[index];
            double left = index * Step - offset;
            if (icon.RenderTransform is not TranslateTransform transform)
                icon.RenderTransform = transform = new TranslateTransform();
            transform.X = left;
            icon.Opacity = PreviewIconLayout.EdgeOpacity(left, iconSize, running.Width);
        }
        iconHover.Reposition();
    }
}
