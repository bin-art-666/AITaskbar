using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class PreviewApplicationStrip
{
    private const double Gap = 4, SeparatorWidth = 1, IndicatorSpace = 7;
    private readonly StackPanel items = new()
    {
        Orientation = Orientation.Horizontal, Spacing = Gap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly PinnedApps pins;
    private readonly PreviewIconPreferences iconPreferences;
    private readonly nint owner;
    private readonly Action<string> report;
    private readonly Dictionary<string, AppIconArtwork?> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly List<AppButton> shown = [];
    private AppGroup[] groups = [];
    private string[]? layout;
    private nint foreground;
    private int iconSize;
    private Color? indicatorColor;
    private double available = double.PositiveInfinity;
    private FlyoutBase? openFlyout;
    private Button? more;
    private Border? separator;
    private bool? dark, contrast;
    public StackPanel Surface => items;

    private sealed record AppButton(AppGroup Group, Button Button, Border Indicator, Border IconPlate, AppIconArtwork? Icon);

    public PreviewApplicationStrip(PinnedApps pins, PreviewIconPreferences iconPreferences, int iconSize, Color? indicatorColor,
        nint owner, Action<string> report)
    {
        this.pins = pins;
        this.iconPreferences = iconPreferences;
        this.iconSize = iconSize;
        this.indicatorColor = indicatorColor;
        this.owner = owner;
        this.report = report;
        Surface.ActualThemeChanged += (_, _) => { RefreshTheme(); UpdateIndicators(); };
    }

    public void Refresh()
    {
        if (openFlyout is not null) return;
        var foregroundWindow = RunningWindows.Foreground();
        groups = AppGroups.Build(pins.Items, RunningWindows.List());
        if (groups.Any(group => group.Windows.Any(window => window.Handle == foregroundWindow)))
            foreground = foregroundWindow;
        else if (!AIEnter.Native.PanelHasFocus(owner))
            foreground = 0;
        Render();
    }

    public void SetAvailableWidth(double width)
    {
        available = Math.Max(0, width);
        if (openFlyout is null) Render();
    }

    public void SetIconSize(int size)
    {
        if (iconSize == size) return;
        openFlyout?.Hide();
        iconSize = size;
        layout = null;
        Render();
    }

    public void SetIndicatorColor(Color? color)
    {
        indicatorColor = color;
        UpdateIndicators();
    }

    private void Render()
    {
        int pinned = pins.Items.Count;
        int count = groups.Length;
        while (count > 0 && RequiredWidth(count, pinned, count < groups.Length) > available) count--;
        bool overflow = count < groups.Length && RequiredWidth(count, pinned, true) <= available;
        var order = groups.Take(count).Select(group => group.Key).ToArray();
        bool divided = pinned > 0 && count > pinned;
        string[] next = [divided ? "divider:" + pinned : "", overflow ? "overflow" : "", .. order];
        if (layout is null || !layout.SequenceEqual(next, StringComparer.OrdinalIgnoreCase))
        {
            layout = next;
            items.Children.Clear();
            shown.Clear();
            separator = null;
            more = null;
            for (int index = 0; index < count; index++)
            {
                if (divided && index == pinned)
                {
                    separator = new Border { Width = SeparatorWidth, Height = 20, VerticalAlignment = VerticalAlignment.Center };
                    items.Children.Add(separator);
                }
                var view = BuildButton(groups[index]);
                shown.Add(view);
                items.Children.Add(view.Button);
            }
            if (overflow)
            {
                more = IconButton("更多应用", "⋯");
                more.Click += (_, _) => ShowOverflow();
                items.Children.Add(more);
            }
            Surface.Visibility = items.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            dark = null;
        }

        for (int index = 0; index < shown.Count; index++)
        {
            var view = shown[index];
            var group = groups[index];
            if (view.Group.Name != group.Name)
            {
                AutomationProperties.SetName(view.Button, group.Name);
                ToolTipService.SetToolTip(view.Button, group.Name);
            }
            if (view.Group.Pin != group.Pin) SetAppMenu(view, group);
            shown[index] = view with { Group = group };
        }
        RefreshTheme();
        UpdateIndicators();
    }

    private double RequiredWidth(int count, int pinned, bool overflow)
    {
        int buttons = count + (overflow ? 1 : 0);
        int divider = pinned > 0 && count > pinned ? 1 : 0;
        return buttons * iconSize + Math.Max(0, buttons + divider - 1) * Gap + divider * SeparatorWidth;
    }

    private AppButton BuildButton(AppGroup group)
    {
        var button = IconButton(group.Name, "\uE7C5", glyph: true);
        var mark = new Border { Height = 3, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom };
        var contents = new Grid { Width = iconSize, Height = iconSize + IndicatorSpace };
        var icon = ProgramIcon(group.Executable);
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
        button.Content = contents;
        button.Click += (_, _) =>
        {
            if (groups.FirstOrDefault(value => StringComparer.OrdinalIgnoreCase.Equals(value.Key, group.Key)) is { } current)
                Open(current, button);
        };
        var view = new AppButton(group, button, mark, plate, icon);
        SetAppMenu(view, group);
        return view;
    }

    private Button IconButton(string name, string content, bool glyph = false)
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
        if (separator is not null)
        {
            var edge = highContrast ? system.GetColorValue(UIColorType.Foreground)
                : useDark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(70, 35, 58, 84);
            separator.Background = new SolidColorBrush(edge);
        }
        foreach (var view in shown) UpdateIcon(view);
    }

    private void UpdateIcon(AppButton view)
    {
        bool highContrast = contrast == true;
        bool original = highContrast || (view.Group.Executable is { } path && iconPreferences.UsesOriginal(path));
        if (view.Icon is { } icon && view.IconPlate.Child is Image image)
        {
            image.Source = original ? icon.Original : icon.Graphic;
            image.Width = image.Height = original || icon.HasPlate ? iconSize : Math.Round(iconSize * (20d / 26));
        }
        if (original || view.Icon?.HasPlate == true)
        {
            view.IconPlate.Background = null;
            return;
        }

        double luminance = view.Icon?.Luminance ?? (dark == true ? .9 : .1);
        // A dark mark needs a lighter plate even on a dark taskbar.
        var color = dark == true
            ? luminance < .34 ? Color.FromArgb(255, 196, 210, 230) : Color.FromArgb(255, 23, 38, 58)
            : luminance > .67 ? Color.FromArgb(255, 71, 89, 113) : Color.FromArgb(255, 224, 234, 249);
        view.IconPlate.Background = new SolidColorBrush(color);
    }

    private void UpdateIndicators()
    {
        var color = accessibility.HighContrast ? system.GetColorValue(UIColorType.Foreground)
            : indicatorColor ?? system.GetColorValue(UIColorType.Accent);
        foreach (var view in shown)
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
        if (group.Windows.Count == 0) Launch(group.Executable);
        else if (group.Windows.Count == 1) Activate(group.Windows[0]);
        else
        {
            var menu = NewMenu();
            foreach (var window in group.Windows)
                menu.Items.Add(WindowItem(window));
            menu.ShowAt(anchor);
        }
    }

    private void ShowOverflow()
    {
        if (more is null) return;
        var menu = NewMenu();
        foreach (var group in groups.Skip(shown.Count))
        {
            var entry = new MenuFlyoutSubItem { Text = group.Name };
            if (group.Windows.Count == 0)
            {
                var launch = new MenuFlyoutItem { Text = "打开应用" };
                launch.Click += (_, _) => Launch(group.Executable);
                entry.Items.Add(launch);
            }
            else foreach (var window in group.Windows) entry.Items.Add(WindowItem(window));
            var pin = PinItem(group);
            if (pin is not null) entry.Items.Add(pin);
            menu.Items.Add(entry);
        }
        menu.ShowAt(more);
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
        menu.Opening += (_, _) => openFlyout = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(openFlyout, menu)) openFlyout = null;
        };
        return menu;
    }

    private void SetAppMenu(AppButton view, AppGroup group)
    {
        var pin = PinItem(group);
        var menu = NewMenu();
        if (pin is not null) menu.Items.Add(pin);
        if (group.Executable is { } path && view.Icon is not null)
        {
            string Label() => iconPreferences.UsesOriginal(path) ? "使用圆角图标" : "使用原始图标";
            var style = new MenuFlyoutItem { Text = Label() };
            menu.Opening += (_, _) => style.IsEnabled = contrast != true;
            style.Click += (_, _) =>
            {
                try
                {
                    iconPreferences.SetOriginal(path, !iconPreferences.UsesOriginal(path));
                    style.Text = Label();
                    UpdateIcon(view);
                    report("");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { report(error.Message); }
            };
            menu.Items.Add(style);
        }
        view.Button.ContextFlyout = menu.Items.Count == 0 ? null : menu;
    }

    private MenuFlyoutItem? PinItem(AppGroup group)
    {
        if (group.Pin is null && !RunningWindows.CanPin(group.Executable)) return null;
        var item = new MenuFlyoutItem { Text = group.Pin is null ? "固定到预览栏" : "取消固定" };
        item.Click += (_, _) =>
        {
            if (group.Pin is null) ChangePins(() => pins.Add(group.Executable!));
            else ChangePins(() => pins.Remove(group.Pin.Executable));
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

    private void Launch(string? executable)
    {
        try
        {
            if (!RunningWindows.CanPin(executable)) throw new FileNotFoundException("应用已移动或无法启动。");
            using var process = Process.Start(new ProcessStartInfo(executable!)
            {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)
            });
            report("");
        }
        catch (Exception error) { report(error.Message); }
    }

    private AppIconArtwork? ProgramIcon(string? executable)
    {
        if (string.IsNullOrEmpty(executable)) return null;
        if (icons.TryGetValue(executable, out var loaded)) return loaded;
        AppIconArtwork? image = null;
        try { image = AppIconArtwork.Load(executable); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException) { }
        if (icons.Count >= 128) icons.Clear();
        return icons[executable] = image;
    }
}
