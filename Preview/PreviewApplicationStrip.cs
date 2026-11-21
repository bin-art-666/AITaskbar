using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class PreviewApplicationStrip
{
    private const double Gap = 4, SeparatorWidth = 1, IndicatorSpace = 7;
    private const double PinDragThreshold = 5;
    private const int WheelNotch = 120;
    private const double ScrollDurationMs = 170, EdgeFade = .4;
    private readonly StackPanel items = new()
    {
        Orientation = Orientation.Horizontal, Spacing = Gap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly PinnedApps pins;
    private readonly AppCenterIcon appCenterIcon;
    private readonly PreviewIconPreferences iconPreferences;
    private readonly nint owner;
    private readonly Action<string> report;
    private readonly Dictionary<string, AppIconArtwork?> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly AppRange pinnedApps = new(new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gap });
    private readonly AppRange runningApps = new(new Canvas());
    private readonly Dictionary<int, RunningTile> runningTiles = [];
    private double scrollPosition, scrollFrom, scrollTo;
    private long? scrollStarted;
    private int mountedStart;
    private readonly Border separator = new()
    {
        Width = SeparatorWidth, Height = 20, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed
    };
    private AppGroup[] groups = [];
    private nint foreground;
    private int iconSize;
    private int wheelRemainder;
    private Color? indicatorColor;
    private double available = double.PositiveInfinity;
    private FlyoutBase? openFlyout;
    private readonly DispatcherQueueTimer hoverTimer;
    private WindowThumbnailPopup? thumbnails;
    private Button? hoverAnchor;
    private string? thumbnailGroup;
    private IReadOnlyList<RunningWindow>? thumbnailWindows;
    private PinPress? pinPress;
    private Button? suppressedClick;
    private bool draggingPin;
    private Border? dropMarker;
    private bool? dark, contrast;
    public StackPanel Surface => items;
    internal Button AppCenterButton { get; }
    private IEnumerable<AppButton> Shown => pinnedApps.Buttons.Concat(runningApps.Buttons);
    private IEnumerable<AppButton> Rendered => pinnedApps.Buttons.Concat(runningTiles.Values.Select(tile => tile.View));

    private sealed record AppButton(AppGroup Group, Button Button, Border Indicator, Border IconPlate, AppIconArtwork? Icon,
        Border? PinDropMarker);
    private sealed record PinPress(PinnedApp Pin, uint PointerId, Point Start);
    private sealed record RunningTile(string Key, AppButton View, TranslateTransform Transform);

    private sealed class AppRange(Panel panel)
    {
        internal Panel Panel { get; } = panel;
        internal List<AppButton> Buttons { get; } = [];
        internal string[]? Layout { get; set; }
        internal int Start { get; set; }
        internal int LastStart { get; set; }
    }

    public PreviewApplicationStrip(PinnedApps pins, PreviewIconPreferences iconPreferences, int iconSize, Color? indicatorColor,
        AppCenterIconStyle appCenterStyle, Action openAppCenter, nint owner, Action<string> report)
    {
        this.pins = pins;
        this.iconPreferences = iconPreferences;
        this.iconSize = iconSize;
        this.indicatorColor = indicatorColor;
        this.owner = owner;
        this.report = report;
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

    public void Refresh()
    {
        if (openFlyout is not null || pinPress is not null) return;
        var foregroundWindow = RunningWindows.Foreground();
        groups = AppGroups.Build(pins.Items, RunningWindows.List());
        if (groups.Any(group => group.Windows.Any(window => window.Handle == foregroundWindow)))
            foreground = foregroundWindow;
        else if (!AIEnter.Native.PanelHasFocus(owner))
            foreground = 0;
        Render();
        if (thumbnailGroup is { } key && thumbnailWindows is { } opened &&
            (groups.FirstOrDefault(group => StringComparer.OrdinalIgnoreCase.Equals(group.Key, key)) is not { } current
                || !opened.SequenceEqual(current.Windows)))
            DismissThumbnails();
    }

    public void SetAvailableWidth(double width)
    {
        available = Math.Max(0, width);
        if (openFlyout is null && pinPress is null) Render();
    }

    public void SetIconSize(int size)
    {
        if (iconSize == size) return;
        openFlyout?.Hide();
        iconSize = size;
        appCenterIcon.SetSize(size);
        AppCenterButton.Width = size;
        AppCenterButton.Height = size + IndicatorSpace;
        pinnedApps.Layout = runningApps.Layout = null;
        if (pinPress is null) Render();
    }

    public void SetIndicatorColor(Color? color)
    {
        indicatorColor = color;
        UpdateIndicators();
    }

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
        if (openFlyout is null && pinPress is null) Render();
    }

    private void Render(AppRange? scrolled = null, bool animateRunning = false)
    {
        int pinned = groups.TakeWhile(group => group.Pin is not null).Count();
        int running = groups.Length - pinned;
        int visiblePins = pinned;
        int requiredRunning = running > 0 && RequiredWidth(0, 1) <= available ? 1 : 0;
        while (visiblePins > 0 && RequiredWidth(visiblePins, requiredRunning) > available) visiblePins--;
        int visibleRunning = running;
        while (visibleRunning > 0 && RequiredWidth(visiblePins, visibleRunning) > available) visibleRunning--;

        RenderRange(pinnedApps, 0, pinned, visiblePins, keepAnchor: scrolled != pinnedApps);
        RenderRange(runningApps, pinned, running, visibleRunning, keepAnchor: scrolled != runningApps, animateRunning);
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
            DismissThumbnails();
            bool slide = animate && range.Layout is { } previous && previous.Length == visible &&
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
            if (view.Group.Pin != group.Pin) SetAppMenu(view, group);
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
        double step = iconSize + Gap;
        mountedStart = runningApps.Start;
        viewport.Width = Math.Max(0, visible * step - Gap);
        viewport.Height = iconSize + IndicatorSpace;
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
        double progress = Math.Min(1, Stopwatch.GetElapsedTime(started).TotalMilliseconds / ScrollDurationMs);
        double remaining = 1 - progress;
        scrollPosition = scrollTo + (scrollFrom - scrollTo) * remaining * remaining * remaining;
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
            double left = index * (iconSize + Gap) - scrollPosition;
            tile.Transform.X = left;
            double visible = Math.Clamp(Math.Min(left + iconSize, width) - Math.Max(0, left), 0, iconSize);
            tile.View.Button.Opacity = 1 - EdgeFade * (1 - visible / iconSize);
        }
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
        int buttons = pinned + running + (AppCenterButton.Visibility == Visibility.Visible ? 1 : 0);
        int divider = pinned > 0 && running > 0 ? 1 : 0;
        return buttons * iconSize + Math.Max(0, buttons + divider - 1) * Gap + divider * SeparatorWidth;
    }

    private void ScrollRunningApps(object sender, PointerRoutedEventArgs args)
    {
        if (runningApps.LastStart == 0 || openFlyout is not null || pinPress is not null) return;
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
        if (openFlyout is not null || pinPress is not null ||
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
        if (group.Windows.Count > 0) ToolTipService.SetToolTip(button, null);
        button.PointerEntered += (_, args) =>
        {
            if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse || openFlyout is not null || pinPress is not null) return;
            var current = Shown.FirstOrDefault(item => ReferenceEquals(item.Button, button));
            if (current is null || current.Group.Windows.Count == 0) { DismissThumbnails(); return; }
            hoverAnchor = button;
            if (StringComparer.OrdinalIgnoreCase.Equals(thumbnailGroup, current.Group.Key)) hoverTimer.Stop();
            else
            {
                thumbnails?.Dismiss();
                thumbnailGroup = null;
                thumbnailWindows = null;
                ScheduleHover(400);
            }
        };
        button.PointerExited += (_, _) =>
        {
            if (!ReferenceEquals(hoverAnchor, button)) return;
            hoverAnchor = null;
            ScheduleHover(260);
        };
        var mark = new Border { Height = 3, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom };
        var contents = new Grid { Width = iconSize, Height = iconSize + IndicatorSpace };
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
        contents.Children.Add(plate);
        contents.Children.Add(mark);
        var pinMarker = group.Pin is { } pin ? EnablePinReordering(button, contents, pin) : null;
        button.Content = contents;
        button.Click += (_, _) =>
        {
            if (ReferenceEquals(suppressedClick, button)) return;
            DismissThumbnails();
            if (groups.FirstOrDefault(value => StringComparer.OrdinalIgnoreCase.Equals(value.Key, group.Key)) is { } current)
                Open(current, button);
        };
        var view = new AppButton(group, button, mark, plate, icon, pinMarker);
        SetAppMenu(view, group);
        return view;
    }

    private Border EnablePinReordering(Button button, Grid contents, PinnedApp pin)
    {
        var marker = new Border
        {
            Width = 2, Height = iconSize, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        contents.Children.Add(marker);
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(items);
            if (point.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed) return;
            suppressedClick = null;
            if (!button.CapturePointer(args.Pointer)) return;
            DismissThumbnails();
            pinPress = new(pin, args.Pointer.PointerId, point.Position);
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (pinPress is not { } press || press.PointerId != args.Pointer.PointerId) return;
            var point = args.GetCurrentPoint(items).Position;
            if (!draggingPin && Math.Abs(point.X - press.Start.X) < PinDragThreshold) return;
            draggingPin = true;
            suppressedClick = button;
            var target = PinDropTarget(point, press.Pin);
            if (target is { } drop) ShowPinDropMarker(drop.View.PinDropMarker!, drop.After);
            else ClearPinDropMarker();
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) =>
        {
            if (pinPress is not { } press || press.PointerId != args.Pointer.PointerId) return;
            try
            {
                if (draggingPin && PinDropTarget(args.GetCurrentPoint(items).Position, press.Pin) is { } drop)
                {
                    pins.Move(press.Pin, drop.View.Group.Pin!, drop.After);
                    report("");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                report("无法保存固定应用顺序。");
            }
            finally { EndPinPress(press); }
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, args) =>
        {
            if (pinPress is { } press && press.PointerId == args.Pointer.PointerId)
                Surface.DispatcherQueue.TryEnqueue(() => EndPinPress(press));
        }), handledEventsToo: true);
        return marker;
    }

    private (AppButton View, bool After)? PinDropTarget(Point point, PinnedApp source)
    {
        if (point.Y < 0 || point.Y >= items.ActualHeight) return null;
        foreach (var view in pinnedApps.Buttons)
        {
            if (view.Group.Pin is not { } pin) continue;
            double left = view.Button.TransformToVisual(items).TransformPoint(new()).X;
            if (point.X < left - Gap / 2 || point.X > left + view.Button.ActualWidth + Gap / 2) continue;
            return PinnedApps.SameProgram(pin.Key, source.Key)
                ? null : (view, point.X >= left + view.Button.ActualWidth / 2);
        }
        return null;
    }

    private void ShowPinDropMarker(Border marker, bool after)
    {
        if (!ReferenceEquals(dropMarker, marker))
        {
            ClearPinDropMarker();
            dropMarker = marker;
            marker.Background = new SolidColorBrush(accessibility.HighContrast
                ? system.GetColorValue(UIColorType.Foreground) : system.GetColorValue(UIColorType.Accent));
            marker.Visibility = Visibility.Visible;
        }
        marker.HorizontalAlignment = after ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    private void EndPinPress(PinPress press)
    {
        if (!ReferenceEquals(pinPress, press)) return;
        pinPress = null;
        draggingPin = false;
        ClearPinDropMarker();
        Surface.DispatcherQueue.TryEnqueue(() =>
        {
            suppressedClick = null;
            Refresh();
        });
    }

    private void ClearPinDropMarker()
    {
        if (dropMarker is not null) dropMarker.Visibility = Visibility.Collapsed;
        dropMarker = null;
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
        dark = useDark;
        contrast = highContrast;
        var edge = highContrast ? system.GetColorValue(UIColorType.Foreground)
            : useDark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(70, 35, 58, 84);
        separator.Background = new SolidColorBrush(edge);
        foreach (var view in Rendered) UpdateIcon(view);
    }

    private void UpdateIcon(AppButton view)
    {
        bool highContrast = contrast == true;
        bool original = highContrast || (view.Group.IconExecutable is { } path && iconPreferences.UsesOriginal(path));
        if (view.Icon is { } icon && view.IconPlate.Child is Image image)
        {
            image.Source = original ? icon.Original : icon.Graphic;
            image.Width = image.Height = original || icon.HasPlate ? iconSize : Math.Round(iconSize * (20d / 26));
        }
        if (view.IconPlate.Child is FontIcon fallback)
        {
            if (original) fallback.ClearValue(IconElement.ForegroundProperty);
            else fallback.Foreground = new SolidColorBrush(Color.FromArgb(255, 39, 46, 56));
        }
        if (original || view.Icon?.HasPlate == true)
        {
            view.IconPlate.Background = null;
            return;
        }

        // Very light artwork needs a dark plate to stay legible against its background.
        var color = view.Icon?.Luminance >= .8 ? Color.FromArgb(255, 52, 58, 68) : Microsoft.UI.Colors.White;
        view.IconPlate.Background = new SolidColorBrush(color);
    }

    private void UpdateIndicators()
    {
        var color = accessibility.HighContrast ? system.GetColorValue(UIColorType.Foreground)
            : indicatorColor ?? system.GetColorValue(UIColorType.Accent);
        foreach (var view in Rendered)
        {
            bool running = view.Group.Windows.Count > 0;
            bool active = view.Group.Windows.Any(window => window.Handle == foreground);
            view.Indicator.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            view.Indicator.Width = active ? 16 : 4;
            if (view.Indicator.Background is not SolidColorBrush brush || brush.Color != color)
                view.Indicator.Background = new SolidColorBrush(color);
            AutomationProperties.SetItemStatus(view.Button, active ? "当前前台应用" : running
                ? $"正在运行，{view.Group.Windows.Count} 个窗口" : "已固定，未运行");
        }
    }

    private void Open(AppGroup group, FrameworkElement anchor)
    {
        if (group.Windows.Count == 0) Launch(group);
        else if (group.Windows.Count == 1) Activate(group.Windows[0]);
        else
        {
            var menu = NewMenu();
            foreach (var window in group.Windows)
                menu.Items.Add(WindowItem(window));
            menu.ShowAt(anchor);
        }
    }

    private MenuFlyoutItem WindowItem(RunningWindow window)
    {
        var item = new MenuFlyoutItem { Text = window.Title };
        item.Click += (_, _) => Activate(window);
        return item;
    }

    private MenuFlyout NewMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        menu.Opening += (_, _) => { DismissThumbnails(); openFlyout = menu; };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(openFlyout, menu)) openFlyout = null;
        };
        return menu;
    }

    private void ScheduleHover(int milliseconds)
    {
        hoverTimer.Stop();
        hoverTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        hoverTimer.Start();
    }

    private void UpdateThumbnails()
    {
        if (hoverAnchor is not { } anchor || openFlyout is not null ||
            Shown.FirstOrDefault(view => ReferenceEquals(view.Button, anchor)) is not { } hovered || hovered.Group.Windows.Count == 0)
        {
            DismissThumbnails();
            return;
        }
        try
        {
            thumbnails ??= new WindowThumbnailPopup(window => { Activate(window); DismissThumbnails(); },
                () => hoverTimer.Stop(), () => ScheduleHover(260));
            thumbnails.Open(owner, anchor, hovered.Group.Windows);
            thumbnailGroup = hovered.Group.Key;
            thumbnailWindows = hovered.Group.Windows;
        }
        catch (Exception error)
        {
            DismissThumbnails();
            report("暂时无法显示窗口缩略图。");
            Debug.WriteLine(error);
        }
    }

    public void DismissThumbnails()
    {
        hoverAnchor = null;
        thumbnailGroup = null;
        thumbnailWindows = null;
        hoverTimer.Stop();
        thumbnails?.Dismiss();
    }

    public void CloseThumbnails()
    {
        StopScrollAnimation();
        DismissThumbnails();
        thumbnails?.Close();
        thumbnails = null;
    }

    private void SetAppMenu(AppButton view, AppGroup group)
    {
        var pin = PinItem(group);
        if (pin is null)
        {
            view.Button.ContextFlyout = null;
            return;
        }
        var menu = NewMenu();
        menu.Items.Add(pin);
        view.Button.ContextFlyout = menu;
    }

    private MenuFlyoutItem? PinItem(AppGroup group)
    {
        if (group.Pin is null && group.PinAppId is null && !RunningWindows.CanPin(group.Executable)) return null;
        var item = new MenuFlyoutItem { Text = group.Pin is null ? "固定到预览栏" : "取消固定" };
        item.Click += (_, _) =>
        {
            if (group.Pin is { } pin) ChangePins(() => pins.Remove(pin));
            else if (group.PinAppId is { } appId) ChangePins(() => pins.Add(appId, group.IconExecutable));
            else ChangePins(() => pins.Add(group.Executable!));
        };
        return item;
    }

    public void ChangePins(Action change)
    {
        try { change(); report(""); Surface.DispatcherQueue.TryEnqueue(Refresh); }
        catch (Exception error) { report(error.Message); }
    }

    private void Activate(RunningWindow window)
    {
        if (!RunningWindows.Activate(window)) { report("暂时无法切换到该窗口。"); return; }
        foreground = window.Handle;
        UpdateIndicators();
        report("");
    }

    private void Launch(AppGroup group)
    {
        try
        {
            if (group.PinAppId is { } appId) PackagedAppPin.Launch(appId);
            else
            {
                var executable = group.Executable;
                if (!RunningWindows.CanPin(executable)) throw new FileNotFoundException("应用已移动或无法启动。");
                using var process = Process.Start(new ProcessStartInfo(executable!)
                {
                    UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)
                });
            }
            report("");
        }
        catch (Exception error) { report(error.Message); }
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
