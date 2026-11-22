using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed partial class PreviewApplicationStrip
{
    private const double SectionGap = 4, SeparatorWidth = 1, IndicatorSpace = 7;
    private const int WheelNotch = 120;
    private readonly StackPanel items = new()
    {
        Orientation = Orientation.Horizontal, Spacing = SectionGap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly PinnedApps pins;
    private readonly RunningAppOrder runningOrder = new();
    private readonly AppCenterIcon appCenterIcon;
    private readonly Button taskViewButton;
    private readonly Image taskViewImage;
    private readonly PreviewIconPreferences iconPreferences;
    private readonly nint owner;
    private readonly Action<string> report;
    private readonly Dictionary<string, AppIconArtwork?> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly AppMenuIcons appMenuIcons;
    private readonly TaskbarIconHoverLayer iconHover;
    private readonly Dictionary<ElementTheme, Style> menuStyles = [];
    private readonly WindowMotion windowMotion;
    private TitleBarMinimizeHook? titleBarMinimize;
    private CancellationTokenSource? windowAction;
    private readonly StackPanel pinnedPanel = new() { Orientation = Orientation.Horizontal };
    private readonly AppRange pinnedApps;
    private readonly AppRange runningApps = new(new Canvas());
    private readonly Dictionary<int, RunningTile> runningTiles = [];
    private double scrollPosition, scrollFrom, scrollTo;
    private long? scrollStarted;
    private long? lastTaskViewOpened;
    private int mountedStart;
    private readonly Border separator = new()
    {
        Width = SeparatorWidth, Height = 20, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed
    };
    private AppGroup[] groups = [];
    private nint foreground;
    private int iconSize;
    private int wheelRemainder;
    private TaskbarAppFeedbackStyle feedbackStyle;
    private PreviewIconLayout iconLayout;
    private int thumbnailWidth;
    private int thumbnailAnimationMs;
    private bool feedbackLight;
    private double available = double.PositiveInfinity;
    private FlyoutBase? openFlyout;
    private readonly DispatcherQueueTimer hoverTimer;
    private WindowThumbnailPopup? thumbnails;
    private Button? hoverAnchor;
    private string? thumbnailGroup;
    private IReadOnlyList<RunningWindow>? thumbnailWindows;
    private bool? dark, contrast;
    public StackPanel Surface => items;
    internal Canvas HoverSurface => iconHover.Surface;
    internal Button AppCenterButton { get; }
    private IEnumerable<AppButton> Shown => pinnedApps.Buttons.Concat(runningApps.Buttons);
    private IEnumerable<AppButton> Rendered => pinnedApps.Buttons.Concat(runningTiles.Values.Select(tile => tile.View));

    private sealed record AppButton(AppGroup Group, Button Button, TaskbarAppFeedback Feedback, Border IconPlate, AppIconArtwork? Icon);
    private sealed record RunningTile(string Key, AppButton View, TranslateTransform Transform);

    private sealed class AppRange(Panel panel)
    {
        internal Panel Panel { get; } = panel;
        internal List<AppButton> Buttons { get; } = [];
        internal string[]? Layout { get; set; }
        internal int Start { get; set; }
        internal int LastStart { get; set; }
    }

    public PreviewApplicationStrip(PinnedApps pins, PreviewIconPreferences iconPreferences, int iconSize, TaskbarAppFeedbackStyle feedbackStyle,
        PreviewIconLayout iconLayout, AppCenterIconStyle appCenterStyle, Action openAppCenter, nint owner, Action<string> report)
    {
        this.pins = pins;
        this.iconPreferences = iconPreferences;
        this.iconSize = iconSize;
        this.feedbackStyle = feedbackStyle;
        this.iconLayout = iconLayout;
        thumbnailWidth = iconLayout.ThumbnailWidth;
        thumbnailAnimationMs = iconLayout.ThumbnailAnimationMs;
        pinnedPanel.Spacing = iconLayout.Gap;
        pinnedApps = new(pinnedPanel);
        this.owner = owner;
        this.report = report;
        windowMotion = new(items, owner);
        appMenuIcons = new(system, accessibility);
        iconHover = new(system.AnimationsEnabled && !accessibility.HighContrast);
        appCenterIcon = new AppCenterIcon(iconSize);
        appCenterIcon.Apply(appCenterStyle);
        AppCenterButton = IconButton("应用中心", appCenterIcon);
        AppCenterButton.Visibility = appCenterStyle.ShowInTaskbar ? Visibility.Visible : Visibility.Collapsed;
        AppCenterButton.VerticalContentAlignment = VerticalAlignment.Top;
        AppCenterButton.Click += (_, _) =>
        {
            DismissThumbnails();
            openAppCenter();
        };
        items.Children.Add(AppCenterButton);
        taskViewImage = new Image
        {
            Width = iconSize,
            Height = iconSize,
            Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "TaskView.png")))
            {
                DecodePixelWidth = 128
            },
            Stretch = Stretch.Uniform
        };
        taskViewButton = IconButton("任务视图", taskViewImage);
        taskViewButton.VerticalContentAlignment = VerticalAlignment.Top;
        ToolTipService.SetToolTip(taskViewButton, "任务视图（Win + Tab）");
        taskViewButton.Click += (_, _) => OpenTaskView();
        items.Children.Add(taskViewButton);
        items.Children.Add(pinnedApps.Panel);
        items.Children.Add(separator);
        items.Children.Add(runningApps.Panel);
        foreach (var range in new[] { pinnedApps, runningApps })
        {
            range.Panel.Visibility = Visibility.Collapsed;
            range.Panel.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        runningApps.Panel.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(ScrollRunningApps), handledEventsToo: true);
        pinnedApps.Panel.KeyDown += (_, args) => NavigateApps(pinnedApps, args);
        runningApps.Panel.KeyDown += (_, args) => NavigateApps(runningApps, args);
        hoverTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        hoverTimer.IsRepeating = false;
        hoverTimer.Tick += (_, _) => UpdateThumbnails();
        Surface.ActualThemeChanged += (_, _) => { RefreshTheme(); UpdateIndicators(); };
    }

    public void Refresh() => Refresh(preserveRangeStart: false);

    private void Refresh(bool preserveRangeStart)
    {
        if (openFlyout is not null || AppDragInteraction || windowMotion.HasRectangularRequest) return;
        var foregroundWindow = RunningWindows.Foreground();
        groups = runningOrder.Apply(AppGroups.Build(pins.Items, RunningWindows.List()));
        if (groups.Any(group => group.Windows.Any(window => window.Handle == foregroundWindow)))
            foreground = foregroundWindow;
        else if (!AIEnter.Native.PanelHasFocus(owner))
            foreground = 0;
        Render(preserveRangeStart: preserveRangeStart);
        if (thumbnailGroup is { } key && thumbnailWindows is { } opened)
        {
            var current = groups.FirstOrDefault(group => StringComparer.OrdinalIgnoreCase.Equals(group.Key, key));
            if (current is null || current.Windows.Count == 0)
            {
                hoverAnchor = null;
                thumbnailGroup = null;
                thumbnailWindows = null;
                hoverTimer.Stop();
                thumbnails?.UpdateSources([]);
            }
            else if (!Shown.Any(view => StringComparer.OrdinalIgnoreCase.Equals(view.Group.Key, key)))
                DismissThumbnails();
            else if (!opened.SequenceEqual(current.Windows))
            {
                thumbnailWindows = current.Windows;
                thumbnails?.UpdateSources(current.Windows);
            }
        }
    }

    internal void RememberForeground()
    {
        var current = RunningWindows.Foreground();
        if (current != owner) foreground = current;
    }

    public void SetAvailableWidth(double width)
    {
        available = Math.Max(0, width);
        if (openFlyout is null && !AppDragInteraction) Render();
    }

    public void SetIconSize(int size)
    {
        if (iconSize == size) return;
        ResetAppDragInteraction();
        openFlyout?.Hide();
        iconSize = size;
        appCenterIcon.SetSize(size);
        AppCenterButton.Width = size;
        AppCenterButton.Height = size + IndicatorSpace;
        taskViewButton.Width = size;
        taskViewButton.Height = size + IndicatorSpace;
        taskViewImage.Width = taskViewImage.Height = size;
        pinnedApps.Layout = runningApps.Layout = null;
        if (!AppDragInteraction) Render();
    }

    internal void SetAppFeedback(TaskbarAppFeedbackStyle style)
    {
        feedbackStyle = style;
        ApplyHoverStyle();
        foreach (var view in Rendered) view.Feedback.ApplyStyle(style);
        UpdateIndicators();
    }

    internal void SetIconLayout(PreviewIconLayout layout)
    {
        bool gapChanged = iconLayout.Gap != layout.Gap;
        if (gapChanged) ResetAppDragInteraction();
        bool scrollChanged = iconLayout.Animation != layout.Animation;
        bool colorChanged = iconLayout.ColorMode != layout.ColorMode;
        bool menuColorChanged = iconLayout.MenuColorMode != layout.MenuColorMode;
        iconLayout = layout;
        SetThumbnailWidth(layout.ThumbnailWidth);
        SetThumbnailAnimation(layout.ThumbnailAnimationMs);
        if (colorChanged || menuColorChanged) openFlyout?.Hide();
        if (colorChanged)
            foreach (var view in Rendered) UpdateIcon(view);
        if (menuColorChanged)
        {
            UpdateThumbnailTheme(Surface.ActualTheme);
            foreach (var view in Rendered) SetAppMenu(view);
        }
        if (!gapChanged && (!scrollChanged || scrollStarted is null)) return;
        pinnedPanel.Spacing = layout.Gap;
        runningApps.Layout = null;
        Render();
    }

    internal void SetThumbnailWidth(int width)
    {
        thumbnailWidth = width;
        thumbnails?.Resize(width);
    }

    internal void SetThumbnailAnimation(int durationMs)
    {
        thumbnailAnimationMs = durationMs;
        if (thumbnails is not null) thumbnails.HoverDurationMs = durationMs;
    }

    internal void SetFeedbackLight(bool light)
    {
        if (feedbackLight == light) return;
        feedbackLight = light;
        SetAppFeedback(feedbackStyle);
    }

    private void ApplyHoverStyle() => iconHover.ApplyStyle(feedbackStyle, feedbackLight, system, accessibility.HighContrast);

    internal void RefreshIconStyle(string executable)
    {
        foreach (var view in Rendered)
            if (StringComparer.OrdinalIgnoreCase.Equals(view.Group.IconExecutable, executable)) UpdateIcon(view);
    }

    internal void SetAppCenterIcon(AppCenterIconStyle style)
    {
        appCenterIcon.Apply(style);
        var visibility = style.ShowInTaskbar ? Visibility.Visible : Visibility.Collapsed;
        if (AppCenterButton.Visibility == visibility) return;
        AppCenterButton.Visibility = visibility;
        if (openFlyout is null && !AppDragInteraction) Render();
    }

    private void Render(AppRange? scrolled = null, bool animateRunning = false, bool preserveRangeStart = false)
    {
        if (AppDragInteraction) return;
        int pinned = groups.TakeWhile(group => group.Pin is not null).Count();
        int running = groups.Length - pinned;
        int visiblePins = pinned;
        int requiredRunning = running > 0 && RequiredWidth(0, 1) <= available ? 1 : 0;
        while (visiblePins > 0 && RequiredWidth(visiblePins, requiredRunning) > available) visiblePins--;
        int visibleRunning = running;
        while (visibleRunning > 0 && RequiredWidth(visiblePins, visibleRunning) > available) visibleRunning--;

        RenderRange(pinnedApps, 0, pinned, visiblePins, keepAnchor: scrolled != pinnedApps && !preserveRangeStart);
        RenderRange(runningApps, pinned, running, visibleRunning, keepAnchor: scrolled != runningApps && !preserveRangeStart, animateRunning);
        separator.Visibility = visiblePins > 0 && visibleRunning > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (runningApps.LastStart == 0 || visibleRunning == 0) wheelRemainder = 0;
        RefreshTheme();
        UpdateIndicators();
    }

    private void RenderRange(AppRange range, int first, int count, int visible, bool keepAnchor, bool animate = false)
    {
        if (keepAnchor && range.Buttons.Count > 0)
        {
            var anchor = range.Buttons[0].Group.Key;
            int index = Array.FindIndex(groups, first, count, group => StringComparer.OrdinalIgnoreCase.Equals(group.Key, anchor));
            if (index >= 0) range.Start = index - first;
        }
        range.LastStart = visible > 0 ? count - visible : 0;
        range.Start = Math.Clamp(range.Start, 0, range.LastStart);
        int start = first + range.Start;
        var order = groups.Skip(start).Take(visible).Select(LayoutKey).ToArray();
        bool running = range == runningApps;
        if (range.Layout is null || !range.Layout.SequenceEqual(order, StringComparer.OrdinalIgnoreCase) ||
            (running && mountedStart != range.Start))
        {
            bool slide = animate && iconLayout.DurationMs > 0 && range.Layout is { } previous && previous.Length == visible &&
                Surface.IsLoaded && system.AnimationsEnabled && !accessibility.HighContrast;
            range.Layout = order;
            range.Buttons.Clear();
            if (running)
                MountRunningTiles(first, count, visible, slide);
            else
            {
                range.Panel.Children.Clear();
                for (int index = 0; index < visible; index++)
                {
                    var view = BuildButton(groups[start + index]);
                    range.Buttons.Add(view);
                    range.Panel.Children.Add(view.Button);
                }
                dark = null;
            }
            range.Panel.Visibility = visible > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        for (int index = 0; index < range.Buttons.Count; index++)
        {
            var view = range.Buttons[index];
            var group = groups[start + index];
            if (view.Group.Name != group.Name)
            {
                AutomationProperties.SetName(view.Button, group.Name);
            }
            if (view.Group.Name != group.Name || (view.Group.Windows.Count == 0) != (group.Windows.Count == 0))
                ToolTipService.SetToolTip(view.Button, group.Windows.Count == 0 ? group.Name : null);
            var updated = view with { Group = group };
            range.Buttons[index] = updated;
            if (running)
            {
                int tileIndex = range.Start + index;
                runningTiles[tileIndex] = runningTiles[tileIndex] with { View = updated };
            }
        }
    }

    private static string LayoutKey(AppGroup group) =>
        group.Key + "|" + IconCacheKey(group.IconExecutable, group.IconAppId);

    private void MountRunningTiles(int first, int count, int visible, bool slide)
    {
        var viewport = runningApps.Panel;
        double step = iconSize + iconLayout.Gap;
        mountedStart = runningApps.Start;
        viewport.Width = Math.Max(0, visible * step - iconLayout.Gap);
        viewport.Height = iconSize + TaskbarAppFeedback.ExtraHeight;
        scrollTo = mountedStart * step;
        if (!slide)
        {
            StopScrollAnimation();
            scrollPosition = scrollTo;
        }

        int firstTile = Math.Max(0, (int)Math.Floor(Math.Min(scrollPosition, scrollTo) / step));
        int endTile = Math.Min(count, (int)Math.Ceiling(Math.Max(scrollPosition, scrollTo) / step) + visible);
        foreach (int index in runningTiles.Keys.ToArray())
        {
            var tile = runningTiles[index];
            if (index >= firstTile && index < endTile && tile.View.Button.Width == iconSize &&
                StringComparer.OrdinalIgnoreCase.Equals(tile.Key, LayoutKey(groups[first + index]))) continue;
            RemoveRunningTile(index);
        }
        for (int index = firstTile; index < endTile; index++)
        {
            if (!runningTiles.TryGetValue(index, out var tile))
            {
                var group = groups[first + index];
                var view = BuildButton(group);
                UpdateIcon(view);
                var transform = new TranslateTransform();
                view.Button.RenderTransform = transform;
                tile = new(LayoutKey(group), view, transform);
                runningTiles.Add(index, tile);
                viewport.Children.Add(view.Button);
            }
            bool settledSlot = index >= mountedStart && index < mountedStart + visible;
            tile.View.Button.IsTabStop = settledSlot;
            tile.View.Button.IsHitTestVisible = settledSlot;
            if (settledSlot) runningApps.Buttons.Add(tile.View);
        }
        if (slide)
        {
            viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, viewport.Width, viewport.Height) };
            scrollFrom = scrollPosition;
            if (scrollStarted is null) CompositionTarget.Rendering += AnimateScroll;
            scrollStarted = Stopwatch.GetTimestamp();
        }
        PositionRunningTiles();
    }

    private void AnimateScroll(object? sender, object args)
    {
        if (scrollStarted is not { } started) return;
        double progress = Math.Min(1, Stopwatch.GetElapsedTime(started).TotalMilliseconds / iconLayout.DurationMs);
        scrollPosition = PreviewIconLayout.ScrollOffset(scrollFrom, scrollTo, progress);
        if (progress == 1)
        {
            StopScrollAnimation();
            int end = mountedStart + runningApps.Buttons.Count;
            foreach (int index in runningTiles.Keys.ToArray())
                if (index < mountedStart || index >= end) RemoveRunningTile(index);
        }
        PositionRunningTiles();
    }

    private void PositionRunningTiles()
    {
        double width = runningApps.Panel.Width;
        foreach (var (index, tile) in runningTiles)
        {
            double left = index * (iconSize + iconLayout.Gap) - scrollPosition;
            tile.Transform.X = left;
            tile.View.Button.Opacity = PreviewIconLayout.EdgeOpacity(left, iconSize, width);
        }
        iconHover.Reposition();
    }

    private void StopScrollAnimation()
    {
        if (scrollStarted is not null) CompositionTarget.Rendering -= AnimateScroll;
        scrollStarted = null;
        runningApps.Panel.Clip = null;
    }

    private void RemoveRunningTile(int index)
    {
        runningApps.Panel.Children.Remove(runningTiles[index].View.Button);
        runningTiles.Remove(index);
    }

    private double RequiredWidth(int pinned, int running)
    {
        int fixedButtons = AppCenterButton.Visibility == Visibility.Visible ? 2 : 1;
        int divider = pinned > 0 && running > 0 ? 1 : 0;
        int sections = fixedButtons + divider + (pinned > 0 ? 1 : 0) + (running > 0 ? 1 : 0);
        int gaps = Math.Max(0, pinned - 1) + Math.Max(0, running - 1);
        return (pinned + running + fixedButtons) * iconSize + gaps * iconLayout.Gap +
            (sections - 1) * SectionGap + divider * SeparatorWidth;
    }

    private void OpenTaskView()
    {
        if (lastTaskViewOpened is { } last && Stopwatch.GetElapsedTime(last).TotalMilliseconds < 300) return;
        DismissThumbnails();
        if (WindowsTaskView.TryOpen()) lastTaskViewOpened = Stopwatch.GetTimestamp();
        else report("暂时无法打开任务视图，请按 Win + Tab。");
    }

    private void ScrollRunningApps(object sender, PointerRoutedEventArgs args)
    {
        if (runningApps.LastStart == 0 || openFlyout is not null || AppDragInteraction) return;
        int delta = args.GetCurrentPoint(runningApps.Panel).Properties.MouseWheelDelta;
        if (delta == 0) return;
        args.Handled = true;
        if (Math.Sign(delta) != Math.Sign(wheelRemainder)) wheelRemainder = 0;
        wheelRemainder += delta;
        int steps = wheelRemainder / WheelNotch;
        wheelRemainder %= WheelNotch;

        int next = Math.Clamp(runningApps.Start - steps, 0, runningApps.LastStart);
        if (next == runningApps.Start) return;
        runningApps.Start = next;
        DismissThumbnails();
        Render(scrolled: runningApps, animateRunning: true);
    }

    private void NavigateApps(AppRange range, KeyRoutedEventArgs args)
    {
        if (openFlyout is not null || AppDragInteraction ||
            args.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
        int focused = range.Buttons.FindIndex(view => view.Button.FocusState != FocusState.Unfocused);
        if (focused < 0) return;
        int step = args.Key == Windows.System.VirtualKey.Left ? -1 : 1;
        int next = focused + step;
        if (next >= 0 && next < range.Buttons.Count)
        {
            args.Handled = range.Buttons[next].Button.Focus(FocusState.Keyboard);
            return;
        }

        int start = Math.Clamp(range.Start + step, 0, range.LastStart);
        if (start == range.Start) return;
        range.Start = start;
        DismissThumbnails();
        Render(scrolled: range);
        range.Buttons[focused].Button.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private AppButton BuildButton(AppGroup group)
    {
        var button = IconButton(group.Name, "\uE7C5", glyph: true);
        button.Height = iconSize + TaskbarAppFeedback.ExtraHeight;
        button.Resources["ButtonBackgroundPointerOver"] = button.Background;
        button.Resources["ButtonBackgroundPressed"] = button.Background;
        button.Resources["ButtonBorderBrushPointerOver"] = button.Background;
        button.Resources["ButtonBorderBrushPressed"] = button.Background;
        if (group.Windows.Count > 0) ToolTipService.SetToolTip(button, null);
        button.PointerEntered += (_, args) =>
        {
            if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse || openFlyout is not null || AppDragInteraction) return;
            var current = Shown.FirstOrDefault(item => ReferenceEquals(item.Button, button));
            if (current is null || current.Group.Windows.Count == 0)
            {
                hoverAnchor = null;
                hoverTimer.Stop();
                thumbnails?.FadeOutOnLeave();
                return;
            }
            if (StringComparer.OrdinalIgnoreCase.Equals(thumbnailGroup, current.Group.Key) &&
                thumbnails?.ResumeHover() == true)
            {
                hoverAnchor = button;
                hoverTimer.Stop();
            }
            else
            {
                DismissThumbnails();
                hoverAnchor = button;
                ScheduleHover(400);
            }
        };
        button.PointerExited += (_, _) =>
        {
            if (!ReferenceEquals(hoverAnchor, button)) return;
            hoverAnchor = null;
            ScheduleHover(260);
        };
        var icon = ProgramIcon(group.IconExecutable, group.IconAppId);
        var plate = new Border
        {
            Width = iconSize, Height = iconSize, CornerRadius = new CornerRadius(iconSize * (6d / 26)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top
        };
        if (icon is not null)
            plate.Child = new Image { Stretch = Stretch.Uniform };
        else
            plate.Child = new FontIcon { Glyph = "\uE7C5", FontSize = Math.Round(iconSize * .7) };
        var feedback = new TaskbarAppFeedback(plate, iconSize);
        feedback.ApplyStyle(feedbackStyle);
        iconHover.Track(plate, group.Pin is null ? runningApps.Panel : null);
        button.Content = feedback.Surface;
        button.Click += (_, _) =>
        {
            if (AppClickSuppressed(button)) return;
            DismissThumbnails();
            if (groups.FirstOrDefault(value => StringComparer.OrdinalIgnoreCase.Equals(value.Key, group.Key)) is { } current)
                Open(current, button, plate);
        };
        var view = new AppButton(group, button, feedback, plate, icon);
        EnableAppReordering(button, group.Pin is null ? runningApps : pinnedApps);
        SetAppMenu(view);
        return view;
    }

    private Button IconButton(string name, object content, bool glyph = false)
    {
        var button = new Button { Content = content, Width = iconSize, Height = iconSize + IndicatorSpace, Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8), HorizontalContentAlignment = HorizontalAlignment.Center };
        if (glyph) button.FontFamily = new FontFamily("Segoe Fluent Icons");
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private void RefreshTheme()
    {
        bool useDark = Surface.ActualTheme == ElementTheme.Dark;
        bool highContrast = accessibility.HighContrast;
        if (dark == useDark && contrast == highContrast) return;
        bool refreshMenus = contrast is not null && contrast != highContrast &&
            iconLayout.MenuColorMode != AppMenuColorMode.FollowTaskbar;
        dark = useDark;
        contrast = highContrast;
        UpdateThumbnailTheme(Surface.ActualTheme);
        var edge = highContrast ? system.GetColorValue(UIColorType.Foreground)
            : useDark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(70, 35, 58, 84);
        separator.Background = new SolidColorBrush(edge);
        ApplyHoverStyle();
        foreach (var view in Rendered)
        {
            UpdateIcon(view);
            if (refreshMenus) SetAppMenu(view);
        }
    }

    private void UpdateIcon(AppButton view)
    {
        bool highContrast = contrast == true;
        bool original = highContrast || (view.Group.IconExecutable is { } path && iconPreferences.UsesOriginal(path));
        if (view.Icon is { } icon && view.IconPlate.Child is Image image)
        {
            image.Source = original ? icon.Original : icon.Graphic(iconLayout.ColorMode);
            image.Width = image.Height = original || icon.HasPlate ? iconSize : Math.Round(iconSize * (20d / 26));
        }
        if (view.IconPlate.Child is FontIcon fallback)
        {
            if (original) fallback.ClearValue(IconElement.ForegroundProperty);
            else fallback.Foreground = new SolidColorBrush(AppIconArtwork.FallbackColor(iconLayout.ColorMode));
        }
        if (original || view.Icon?.HasPlate == true)
        {
            view.IconPlate.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            return;
        }

        view.IconPlate.Background = new SolidColorBrush(AppIconArtwork.BackplateColor(iconLayout.ColorMode));
    }

    private void UpdateIndicators()
    {
        var (activeColor, backgroundColor) = TaskbarAppFeedback.DotColors(feedbackStyle, system, accessibility.HighContrast);
        foreach (var view in Rendered)
        {
            bool running = view.Group.Windows.Count > 0;
            bool active = view.Group.Windows.Any(window => window.Handle == foreground);
            view.Feedback.ShowState(IndicatorState(running, active), activeColor, backgroundColor, feedbackLight);
            AutomationProperties.SetItemStatus(view.Button, active ? "当前前台应用" : running
                ? $"正在运行，{view.Group.Windows.Count} 个窗口" : "已固定，未运行");
        }
    }

    private static AppWindowState IndicatorState(bool running, bool active) =>
        active ? AppWindowState.Foreground : running ? AppWindowState.Background : AppWindowState.Closed;

    private bool UsePreviewWindowAnimation => iconLayout.UsesPreviewWindowAnimation &&
        system.AnimationsEnabled && !accessibility.HighContrast;

    private bool UseRectangularWindowAnimation => UsePreviewWindowAnimation &&
        iconLayout.WindowAnimation == WindowAnimationStyle.WindowsStyle;

    internal void EnableTitleBarMinimize() =>
        titleBarMinimize ??= new TitleBarMinimizeHook(items.DispatcherQueue, FindTitleBarWindow, MinimizeFromTitleBar);

    private RunningWindow? FindTitleBarWindow(nint handle)
    {
        if (handle == 0 || !UsePreviewWindowAnimation || !items.IsLoaded) return null;
        foreach (var view in Shown)
        {
            var window = view.Group.Windows.FirstOrDefault(window => window.Handle == handle);
            if (window is not null && RunningWindows.IsCurrent(window) && !RunningWindows.IsMinimized(window) &&
                WindowMotionNative.ScreenBounds(owner, view.IconPlate) is not null) return window;
        }
        return null;
    }

    private void MinimizeFromTitleBar(RunningWindow window)
    {
        if (UseRectangularWindowAnimation && windowMotion.RetargetRectangular(window, opening: false)) return;
        if (!RunningWindows.IsCurrent(window) || RunningWindows.IsMinimized(window)) return;
        var view = Shown.FirstOrDefault(view => view.Group.Windows.Contains(window));
        var origin = UsePreviewWindowAnimation && view is not null ? WindowMotionNative.ScreenBounds(owner, view.IconPlate) : null;
        if (origin is null)
        {
            TitleBarMinimizeHook.RequestNativeMinimize(window);
            return;
        }
        windowAction?.Cancel();
        MinimizeWindow(window, origin);
    }

    private void Open(AppGroup group, FrameworkElement anchor, FrameworkElement plate)
    {
        // A window can appear between the last strip refresh and a click on its pinned icon.
        if (UseRectangularWindowAnimation && group.Windows.Count == 0)
        {
            group = AppGroups.Build(pins.Items, RunningWindows.List())
                .FirstOrDefault(current => StringComparer.OrdinalIgnoreCase.Equals(current.Key, group.Key)) ?? group;
        }
        if (UseRectangularWindowAnimation && group.Windows.Any(window => windowMotion.RetargetRectangular(window, opening: null)))
            return;
        windowAction?.Cancel();
        if (group.Windows.Count == 0)
        {
            if (!Launch(group) || !UsePreviewWindowAnimation) return;
            WindowMotion.Pulse(plate);
            if (WindowMotionNative.ScreenBounds(owner, plate) is { } origin)
            {
                int duration = iconLayout.EffectiveWindowDurationMs;
                var style = iconLayout.WindowAnimation;
                _ = RunWindowAction(token => AnimateLaunchAsync(group.Key, origin, style, duration, token));
            }
        }
        else if (group.Windows.Count == 1) OpenWindow(group.Windows[0], plate, toggle: true);
        else ShowThumbnails(anchor, plate, group);
    }

    private void OpenWindow(RunningWindow window, FrameworkElement plate, bool toggle)
    {
        if (UseRectangularWindowAnimation && windowMotion.RetargetRectangular(window, opening: toggle ? null : true)) return;
        windowAction?.Cancel();
        if (!RunningWindows.IsCurrent(window)) { report("暂时无法切换到该窗口。"); return; }
        bool minimized = RunningWindows.IsMinimized(window);
        nint current = RunningWindows.Foreground();
        bool minimize = toggle && !minimized &&
            (current == window.Handle || foreground == window.Handle && AIEnter.Native.PanelHasFocus(owner));
        if (!minimize && !minimized) { Activate(window); return; }
        var origin = UsePreviewWindowAnimation ? WindowMotionNative.ScreenBounds(owner, plate) : null;
        if (minimize)
        {
            MinimizeWindow(window, origin);
            return;
        }
        if (origin is not { } icon) { Activate(window); return; }

        if (UseRectangularWindowAnimation)
        {
            RunRectangularWindowAction(window, icon, opening: true);
            return;
        }
        int duration = iconLayout.EffectiveWindowDurationMs;
        var style = iconLayout.WindowAnimation;
        _ = RunWindowAction(async token =>
        {
            if (await windowMotion.RestoreAsync(window, icon, style, duration, token))
                ActivateIndicators(window);
            else report("暂时无法切换到该窗口。");
        });
    }

    private void MinimizeWindow(RunningWindow window, Rect? origin)
    {
        if (origin is not { } icon)
        {
            CompleteMinimize(window, RunningWindows.Minimize(window));
            return;
        }
        if (UseRectangularWindowAnimation)
        {
            RunRectangularWindowAction(window, icon, opening: false);
            return;
        }
        int duration = iconLayout.EffectiveWindowDurationMs;
        var style = iconLayout.WindowAnimation;
        _ = RunWindowAction(async token =>
            CompleteMinimize(window, await windowMotion.MinimizeAsync(window, icon, style, duration, token)));
    }

    private void RunRectangularWindowAction(RunningWindow window, Rect icon, bool opening)
    {
        int duration = iconLayout.EffectiveWindowDurationMs;
        _ = RunWindowAction(async token =>
        {
            if (!await windowMotion.RunRectangularAsync(window, icon, opening, duration, token))
                report("暂时无法切换到该窗口。");
            else if (RunningWindows.IsMinimized(window))
                CompleteMinimize(window, success: true);
            else
                ActivateIndicators(window);
        });
    }

    private async Task RunWindowAction(Func<CancellationToken, Task> action)
    {
        using var request = new CancellationTokenSource();
        windowAction = request;
        try { await action(request.Token); }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { if (ReferenceEquals(windowAction, request)) windowAction = null; }
    }

    private async Task AnimateLaunchAsync(string key, Rect origin, WindowAnimationStyle style, int duration, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < 2.5)
        {
            await Task.Delay(45, token);
            var current = RunningWindows.Foreground();
            if (current == 0 || current == owner) continue;
            var launched = AppGroups.Build(pins.Items, RunningWindows.List())
                .FirstOrDefault(group => StringComparer.OrdinalIgnoreCase.Equals(group.Key, key))?
                .Windows.FirstOrDefault(window => window.Handle == current);
            if (launched is null) continue;
            await windowMotion.ExpandLaunchedAsync(launched, origin, style, duration, token);
            return;
        }
    }

    private MenuFlyout NewMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        var theme = MenuTheme;
        if (theme != ElementTheme.Default)
        {
            if (!menuStyles.TryGetValue(theme, out var style))
            {
                style = new Style(typeof(MenuFlyoutPresenter));
                style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, theme));
                style.Setters.Add(new Setter(Control.BackgroundProperty, MenuBackground(theme)));
                menuStyles.Add(theme, style);
            }
            menu.MenuFlyoutPresenterStyle = style;
        }
        menu.Opening += (_, _) => { DismissThumbnails(); openFlyout = menu; };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(openFlyout, menu)) openFlyout = null;
        };
        return menu;
    }

    private ElementTheme MenuTheme => accessibility.HighContrast ? ElementTheme.Default : iconLayout.MenuColorMode switch
    {
        AppMenuColorMode.Light => ElementTheme.Light,
        AppMenuColorMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private static SolidColorBrush MenuBackground(ElementTheme theme) => new(theme == ElementTheme.Dark
        ? Color.FromArgb(255, 37, 44, 57) : Color.FromArgb(255, 245, 248, 252));

    private void UpdateThumbnailTheme(ElementTheme taskbarTheme)
    {
        if (thumbnails is null) return;
        if (accessibility.HighContrast)
        {
            thumbnails.SetTheme(ElementTheme.Default, system.GetColorValue(UIColorType.Background));
            return;
        }
        var theme = MenuTheme;
        if (theme != ElementTheme.Default)
        {
            thumbnails.SetTheme(theme, MenuBackground(theme).Color);
            return;
        }
        thumbnails.SetTheme(taskbarTheme, taskbarTheme == ElementTheme.Dark
            ? Color.FromArgb(255, 34, 38, 45) : Color.FromArgb(255, 243, 247, 251));
    }

    private void ScheduleHover(int milliseconds)
    {
        hoverTimer.Stop();
        hoverTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        hoverTimer.Start();
    }

    private void UpdateThumbnails()
    {
        if (thumbnails?.ContainsPointer() == true) return;
        if (hoverAnchor is null && openFlyout is null)
        {
            thumbnails?.FadeOutOnLeave();
            return;
        }
        if (hoverAnchor is not { } anchor || openFlyout is not null ||
            Shown.FirstOrDefault(view => ReferenceEquals(view.Button, anchor)) is not { } hovered || hovered.Group.Windows.Count == 0)
        {
            DismissThumbnails();
            return;
        }
        ShowThumbnails(anchor, hovered.IconPlate, hovered.Group);
    }

    private void ShowThumbnails(FrameworkElement anchor, FrameworkElement plate, AppGroup group)
    {
        try
        {
            thumbnails ??= new WindowThumbnailPopup(() => hoverTimer.Stop(), () => ScheduleHover(260), ClearThumbnailTracking)
            {
                HoverDurationMs = thumbnailAnimationMs
            };
            UpdateThumbnailTheme(anchor.ActualTheme);
            hoverAnchor = anchor as Button;
            thumbnails.Open(owner, anchor, group.Windows, thumbnailWidth,
                window =>
                {
                    var currentPlate = Shown.FirstOrDefault(view => StringComparer.OrdinalIgnoreCase.Equals(view.Group.Key, group.Key))
                        ?.IconPlate ?? plate;
                    DismissThumbnails();
                    OpenWindow(window, currentPlate, toggle: false);
                },
                CloseThumbnail);
            if (thumbnails.ResumeHover())
            {
                thumbnailGroup = group.Key;
                thumbnailWindows = group.Windows;
            }
            else ClearThumbnailTracking();
        }
        catch (Exception error)
        {
            DismissThumbnails();
            report("暂时无法显示窗口缩略图。");
            Debug.WriteLine(error);
        }
    }

    private async void CloseThumbnail(RunningWindow window)
    {
        var group = thumbnailGroup;
        CloseWindows([window]);
        // WM_CLOSE can be delayed by the application or open a save prompt.
        for (int attempt = 0; attempt < 12 && group is not null && thumbnailGroup == group; attempt++)
        {
            await Task.Delay(60);
            if (thumbnailGroup != group) return;
            if (RunningWindows.IsCurrent(window)) continue;
            Refresh();
            return;
        }
    }

    public void DismissThumbnails()
    {
        ClearThumbnailTracking();
        thumbnails?.Dismiss();
    }

    private void ClearThumbnailTracking()
    {
        hoverAnchor = null;
        thumbnailGroup = null;
        thumbnailWindows = null;
        hoverTimer.Stop();
    }

    public void Close()
    {
        ResetAppDragInteraction();
        titleBarMinimize?.Dispose();
        windowAction?.Cancel();
        windowMotion.Clear();
        StopScrollAnimation();
        DismissThumbnails();
        thumbnails?.Close();
        thumbnails = null;
    }

    private void SetAppMenu(AppButton view)
    {
        var menu = NewMenu();
        int opening = 0;
        int Populate(AppGroup group, IReadOnlyList<AppGroup> current, bool readRecent = false)
        {
            menu.Items.Clear();
            var app = RunningWindows.CanPin(group.Executable) ? Path.GetFileName(group.Executable)?.ToLowerInvariant() : null;
            (string Text, string Arguments)? privateWindow = app switch
            {
                "chrome.exe" => ("新建无痕窗口", "--incognito"),
                "msedge.exe" => ("新建 InPrivate 窗口", "--inprivate"),
                _ => null
            };
            if (group.Windows.Count == 0 && privateWindow is null)
                menu.Items.Add(AppMenuItem("打开", AppMenuIcon.Open, () => Open(group, view.Button, view.IconPlate)));
            else if (NewWindowArguments(app) is { } arguments)
                menu.Items.Add(AppMenuItem("新建窗口", AppMenuIcon.NewWindow, () => Launch(group, arguments)));
            if (privateWindow is { } task)
                menu.Items.Add(AppMenuItem(task.Text, AppMenuIcon.PrivateWindow, () => Launch(group, task.Arguments)));
            if (readRecent && iconLayout.ShowRecentFiles && app == "chrome.exe")
                AddBrowserRecentFiles(menu, group, RecentBrowser.Chrome);
            if (readRecent && iconLayout.ShowEdgeRecentFiles && app == "msedge.exe")
                AddBrowserRecentFiles(menu, group, RecentBrowser.Edge);
            int recentPosition = menu.Items.Count;
            if (PinItem(group) is { } pin) menu.Items.Add(pin);
            if (group.Windows.Count == 0) return recentPosition;

            if (menu.Items.Count != 0) menu.Items.Add(new MenuFlyoutSeparator());
            bool singleWindow = group.Windows.Count == 1;
            menu.Items.Add(AppMenuItem(singleWindow ? "关闭窗口" : "关闭所有窗口",
                singleWindow ? AppMenuIcon.CloseWindow : AppMenuIcon.CloseAllWindows, () => CloseWindows(group.Windows)));
            var reason = EndTaskUnavailable(group, current);
            var end = AppMenuItem("结束任务", AppMenuIcon.EndTask, () => EndAppTask(group), enabled: reason is null);
            menu.Items.Add(end);
            if (reason is not null)
            {
                menu.Items.Add(new AppMenuFlyoutItem { Text = reason,
                    Icon = appMenuIcons.Create(AppMenuIcon.Empty, iconLayout.ColorMode), IsEnabled = false });
                AutomationProperties.SetHelpText(end, reason);
            }
            return recentPosition;
        }
        Populate(view.Group, groups);
        menu.Opening += async (_, _) =>
        {
            int request = ++opening;
            var current = windowMotion.HasRectangularRequest ? groups : AppGroups.Build(pins.Items, RunningWindows.List());
            var group = FindApp(current, view.Group.Key) ?? view.Group;
            int recentPosition = Populate(group, current, readRecent: true);
            if (group.Windows.Count == 0 || RecentBrowserFor(group.Executable) is not { } browser || !ShowsRecentSessions(browser)) return;
            var tabs = await BrowserRecentSessions.ReadAsync(browser);
            if (tabs.Length == 0 || request != opening || !ReferenceEquals(openFlyout, menu) ||
                !ShowsRecentSessions(browser) || !group.Windows.Any(RunningWindows.IsCurrent)) return;
            menu.Items.Insert(recentPosition, RecentSessionMenu(browser, tabs));
        };
        view.Button.ContextFlyout = menu;
    }

    private static AppGroup? FindApp(IEnumerable<AppGroup> apps, string key) =>
        apps.FirstOrDefault(app => StringComparer.OrdinalIgnoreCase.Equals(app.Key, key));

    private static RecentBrowser? RecentBrowserFor(string? executable) => Path.GetFileName(executable)?.ToLowerInvariant() switch
    {
        "msedge.exe" => RecentBrowser.Edge,
        "chrome.exe" => RecentBrowser.Chrome,
        _ => null
    };

    private bool ShowsRecentSessions(RecentBrowser browser) =>
        browser == RecentBrowser.Chrome ? iconLayout.ShowChromeRecentlyClosed : iconLayout.ShowEdgeRecentlyClosed;

    private MenuFlyoutSubItem RecentSessionMenu(RecentBrowser browser, IReadOnlyList<BrowserRecentEntry> tabs)
    {
        var recent = RecentMenu("最近关闭");
        foreach (var tab in tabs)
            recent.Items.Add(AppMenuItem(tab.Title, AppMenuIcon.RecentFile, () => _ = RestoreBrowserTabAsync(browser, tab.SessionId)));
        ApplySubmenuTheme(recent);
        return recent;
    }

    private async Task RestoreBrowserTabAsync(RecentBrowser browser, string sessionId)
    {
        if (ShowsRecentSessions(browser) && !await BrowserRecentSessions.RestoreAsync(browser, sessionId))
            report($"{browser} 未连接，或该页面已无法恢复。");
    }

    private void AddBrowserRecentFiles(MenuFlyout menu, AppGroup group, RecentBrowser browser)
    {
        var files = BrowserRecentDocuments.Read(group, browser);
        if (files.Length == 0) return;
        var recent = RecentMenu("最近文件");
        foreach (string path in files)
        {
            recent.Items.Add(AppMenuItem(Path.GetFileName(path), AppMenuIcon.RecentFile, () =>
            {
                if (!BrowserRecentDocuments.IsAvailable(path)) { report("文件已移动或无法访问。"); return; }
                Launch(group, new Uri(path).AbsoluteUri);
            }));
        }
        ApplySubmenuTheme(recent);
        menu.Items.Add(recent);
    }

    private MenuFlyoutSubItem RecentMenu(string text) => new()
    {
        Text = text,
        RequestedTheme = MenuTheme,
        Icon = appMenuIcons.Create(AppMenuIcon.RecentFile, iconLayout.ColorMode)
    };

    private void ApplySubmenuTheme(MenuFlyoutSubItem submenu)
    {
        var theme = MenuTheme;
        if (theme == ElementTheme.Default || submenu.Items.Count == 0) return;
        // WinUI creates a separate presenter for nested menus without exposing a style property.
        submenu.Items[0].Loaded += (sender, _) =>
        {
            for (var parent = VisualTreeHelper.GetParent((DependencyObject)sender); parent is not null;
                parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not MenuFlyoutPresenter presenter) continue;
                presenter.RequestedTheme = theme;
                presenter.Background = MenuBackground(theme);
                break;
            }
        };
    }

    private MenuFlyoutItem AppMenuItem(string text, AppMenuIcon icon, Action action, bool enabled = true)
    {
        var item = new AppMenuFlyoutItem { Text = text, Icon = appMenuIcons.Create(icon, iconLayout.ColorMode, enabled), IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static string? NewWindowArguments(string? app) => app switch
        {
            "explorer.exe" => "/n",
            "chrome.exe" or "msedge.exe" or "code.exe" or "code - insiders.exe" => "--new-window",
            _ => null
        };

    private void CloseWindows(IEnumerable<RunningWindow> windows)
    {
        bool failed = false;
        foreach (var window in windows)
            failed |= !RunningWindows.RequestClose(window);
        report(failed ? "部分窗口无法发送关闭请求。" : "");
        Surface.DispatcherQueue.TryEnqueue(Refresh);
    }

    private static string? EndTaskUnavailable(AppGroup group, IReadOnlyList<AppGroup> apps)
    {
        foreach (var window in group.Windows)
        {
            if (RunningWindows.EndTaskUnavailable(window) is { } reason) return reason;
            if (apps.Any(app => !StringComparer.OrdinalIgnoreCase.Equals(app.Key, group.Key) &&
                app.Windows.Any(other => other.ProcessId == window.ProcessId)))
                return "此进程由多个应用共用。";
        }
        return null;
    }

    private void EndAppTask(AppGroup selected)
    {
        if (!RunningWindows.ConfirmEndTask(owner, selected.Name)) return;
        var current = AppGroups.Build(pins.Items, RunningWindows.List());
        if (FindApp(current, selected.Key) is not { } group) return;
        if (EndTaskUnavailable(group, current) is { } reason) { report(reason); return; }

        var targets = group.Windows.Where(window => selected.Windows.Any(previous =>
            previous.Handle == window.Handle && previous.ProcessId == window.ProcessId)).DistinctBy(window => window.ProcessId);
        bool failed = false;
        foreach (var window in targets)
            failed |= !RunningWindows.EndTask(window);
        report(failed ? "部分应用进程无法结束。" : "");
        Surface.DispatcherQueue.TryEnqueue(Refresh);
    }

    private MenuFlyoutItem? PinItem(AppGroup group)
    {
        if (group.Pin is null && group.PinAppId is null && !RunningWindows.CanPin(group.Executable)) return null;
        bool pinned = group.Pin is not null;
        return AppMenuItem(pinned ? "取消固定" : "固定到预览栏", pinned ? AppMenuIcon.Unpin : AppMenuIcon.Pin, () =>
        {
            if (group.Pin is { } pin) ChangePins(() => pins.Remove(pin));
            else if (group.PinAppId is { } appId) ChangePins(() => pins.Add(appId, group.IconExecutable));
            else ChangePins(() => pins.Add(group.Executable!));
        });
    }

    public void ChangePins(Action change)
    {
        try { change(); report(""); Surface.DispatcherQueue.TryEnqueue(Refresh); }
        catch (Exception error) { report(error.Message); }
    }

    private void Activate(RunningWindow window)
    {
        if (!RunningWindows.Activate(window)) { report("暂时无法切换到该窗口。"); return; }
        ActivateIndicators(window);
    }

    private void ActivateIndicators(RunningWindow window)
    {
        foreground = window.Handle;
        UpdateIndicators();
        report("");
    }

    private void CompleteMinimize(RunningWindow window, bool success)
    {
        if (!success) { report("暂时无法最小化该窗口。"); return; }
        if (foreground == window.Handle) foreground = 0;
        UpdateIndicators();
        report("");
    }

    private bool Launch(AppGroup group, string? argument = null)
    {
        try
        {
            if (argument is null && group.PinAppId is { } appId) PackagedAppPin.Launch(appId);
            else
            {
                var executable = group.Executable;
                if (!RunningWindows.CanPin(executable)) throw new FileNotFoundException("应用已移动或无法启动。");
                var start = new ProcessStartInfo(executable!)
                {
                    UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)
                };
                if (argument is not null) start.ArgumentList.Add(argument);
                using var process = Process.Start(start);
            }
            report("");
            return true;
        }
        catch (Exception error) { report(error.Message); return false; }
    }

    private AppIconArtwork? ProgramIcon(string? executable, string? appId)
    {
        var key = IconCacheKey(executable, appId);
        if (string.IsNullOrEmpty(key)) return null;
        if (icons.TryGetValue(key, out var loaded)) return loaded;
        AppIconArtwork? image = null;
        try { image = AppIconArtwork.Load(executable, appId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException) { }
        if (icons.Count >= 128) icons.Clear();
        return icons[key] = image;
    }

    private static string? IconCacheKey(string? executable, string? appId) => appId is null ? executable : appId + "|" + executable;
}
