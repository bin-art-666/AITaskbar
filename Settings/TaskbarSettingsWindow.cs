using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal enum SettingsSection { Taskbar, Halo, AIEnter, AppCenter, TaskbarSubpages }

internal sealed partial class TaskbarSettingsWindow : Window
{
    private readonly App app;
    private readonly Button previewToggle = new();
    private readonly Slider heightSlider = new() { Minimum = PreviewHeight.Minimum, Maximum = PreviewHeight.Maximum,
        StepFrequency = 1, Width = 132 };
    private readonly TextBlock heightValue = new() { MinWidth = 46, VerticalAlignment = VerticalAlignment.Center };
    private Button resetHeight = null!;
    private bool syncingHeight;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly UISettings colors = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly NavigationView nav;
    private readonly Dictionary<SettingsSection, NavigationViewItem> navigation = [];
    private readonly ImageIcon haloIcon = new() { Width = 18, Height = 18 };
    private readonly ImageIcon aiIcon = new() { Width = 18, Height = 18, Source = PreviewModuleIcons.CreateStaticAiIcon() };
    private PreviewModuleIcons? boundIcons;
    private Halo.RingAppearanceBridge? navigationRingBridge;
    private (Halo.UiAppearance Appearance, bool Light, Color? Foreground)? navigationRingStyle;
    private Halo.SettingsPage? haloSettings;
    private AIEnter.SettingsPage? aiSettings;
    private AppCenterSettingsPage? appCenterSettings;
    internal Task PendingSettings => Task.WhenAll(haloSettings?.PendingOperation ?? Task.CompletedTask, glassCommit);

    public TaskbarSettingsWindow(App app, string profileDirectory)
    {
        this.app = app;
        Title = "AITaskbar · 设置";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AITaskbar-Ring.ico"));
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = true;
        presenter.IsMaximizable = presenter.IsMinimizable = false;
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        bool glass = colors.AdvancedEffectsEnabled && !accessibility.HighContrast;
        Halo.GlassBackdrop? backdrop = null;
        if (glass)
        {
            backdrop = Halo.PanelTheme.DesktopBackdrop();
            backdrop.UpdateBlur(85);
            SystemBackdrop = backdrop;
        }
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        double scale = Math.Max(96, AIEnter.Native.GetDpiForWindow(handle)) / 96d;
        int width = Math.Min((int)Math.Round(760 * scale), work.Width);
        int height = Math.Min((int)Math.Round(540 * scale), work.Height);
        AppWindow.MoveAndResize(new(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        AppWindow.Closing += (_, args) => { args.Cancel = true; HideSettings(); };

        nav = SettingsNavigation.Sidebar(144);
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var widths = new SettingsPaneWidths(profileDirectory, SetStatus);
        var taskbarNav = SettingsNavigation.Sidebar(144);
        var appearanceBody = new StackPanel();
        BuildPreview(appearanceBody);
        var appearanceItem = AddSubpage("任务栏外观设置", SettingsNavigation.Subpage("任务栏外观设置", appearanceBody));
        AddSubpage("图标设置", BuildIconSettings());
        var compactSubpages = SettingsNavigation.ConnectSubpages(taskbarNav, item => item.Tag);
        var taskbarPages = new SettingsSplitView(taskbarNav, widths, SettingsSection.TaskbarSubpages,
            (length, condensed) =>
            {
                taskbarNav.OpenPaneLength = length;
                taskbarNav.IsPaneVisible = !condensed;
                taskbarNav.IsPaneOpen = !condensed;
            }, compactSubpages);
        NavigationViewItem AddSubpage(string title, FrameworkElement page)
        {
            var item = new NavigationViewItem { Content = title, Tag = page };
            taskbarNav.MenuItems.Add(item);
            return item;
        }
        void UpdateGlassActions() => glassActions.Visibility =
            ReferenceEquals(nav.SelectedItem, navigation.GetValueOrDefault(SettingsSection.Taskbar)) &&
            ReferenceEquals(taskbarNav.SelectedItem, appearanceItem)
                ? Visibility.Visible : Visibility.Collapsed;
        taskbarNav.SelectionChanged += (_, _) => UpdateGlassActions();
        var navigationLayout = new SettingsSplitView(nav, widths, SettingsSection.Taskbar, (length, compact) =>
        {
            nav.OpenPaneLength = length;
            nav.PaneDisplayMode = compact ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
            nav.IsPaneOpen = !compact;
        });
        AddNavigation(SettingsSection.Taskbar, "任务栏设置", new FontIcon { Glyph = "\uE713" });
        AddNavigation(SettingsSection.Halo, "Halo", haloIcon);
        AddNavigation(SettingsSection.AIEnter, "AIEnter", aiIcon);
        AddNavigation(SettingsSection.AppCenter, "应用中心", new IconSourceElement
        {
            IconSource = new FontIconSource { Glyph = "\uE8A9" }
        });
        nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is not NavigationViewItem { Tag: SettingsSection section }) return;
            UpdateGlassActions();
            nav.Content = section switch
            {
                SettingsSection.Halo => haloSettings ??= app.HaloModule.CreateSettingsPage(this, widths),
                SettingsSection.AIEnter => aiSettings ??= app.AiModule.CreateSettingsPage(this, widths),
                SettingsSection.AppCenter => appCenterSettings ??= new AppCenterSettingsPage(app, widths),
                _ => taskbarPages
            };
        };
        Select(SettingsSection.Taskbar);
        nav.Content = taskbarPages;
        UpdateGlassActions();

        var footer = new Grid { Padding = new Thickness(20, 8, 20, 12), ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var exit = ActionButton("退出 AITaskbar", () => { if (PendingSettings.IsCompleted) app.RequestExit(); });
        Grid.SetColumn(glassActions, 1);
        Grid.SetColumn(exit, 2);
        footer.Children.Add(status);
        footer.Children.Add(glassActions);
        footer.Children.Add(exit);
        var caption = new Grid { Margin = new Thickness(8, 6, 8, 0), Background = transparent };
        caption.Children.Add(new TextBlock { Text = "AITaskbar · 设置", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var back = Halo.WindowControls.IconButton("\uE72B", "返回任务栏");
        back.Click += (_, _) =>
        {
            HideSettings();
            app.FocusPreview();
        };
        var pin = new ToggleButton();
        Halo.PanelTheme.FramedIconStyle(pin, "置顶");
        Halo.WindowControls.SetIcon(pin, "\uE718");
        pin.Click += (_, _) =>
        {
            bool pinned = pin.IsChecked == true;
            presenter.IsAlwaysOnTop = pinned;
            string label = pinned ? "取消置顶" : "置顶";
            ToolTipService.SetToolTip(pin, label);
            AutomationProperties.SetName(pin, label);
        };
        var close = Halo.WindowControls.IconButton("\uE8BB", "关闭设置窗口");
        close.Click += (_, _) => HideSettings();
        var windowActions = Halo.PanelTheme.Actions();
        windowActions.HorizontalAlignment = HorizontalAlignment.Right;
        windowActions.Children.Add(back);
        windowActions.Children.Add(pin);
        windowActions.Children.Add(close);
        caption.Children.Add(windowActions);
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(navigationLayout, 1);
        Grid.SetRow(footer, 2);
        layout.Children.Add(caption);
        layout.Children.Add(navigationLayout);
        layout.Children.Add(footer);
        void RefreshMaterial()
        {
            bool dark = layout.ActualTheme == ElementTheme.Dark;
            bool contrast = accessibility.HighContrast;
            var baseColor = contrast ? colors.GetColorValue(UIColorType.Background) : dark
                ? Color.FromArgb(glass ? (byte)158 : (byte)255, 27, 31, 39)
                : Color.FromArgb(glass ? (byte)137 : (byte)255, 244, 248, 250);
            layout.Background = new SolidColorBrush(baseColor);
        }
        layout.ActualThemeChanged += (_, _) => RefreshMaterial();
        nav.ActualThemeChanged += (_, _) => RefreshNavigationIcons();
        layout.Loaded += (_, _) => RefreshMaterial();
        Content = layout;
        var dragRegion = new Halo.PanelCaption(this, caption, windowActions, () => true);
        Halo.PanelResizeFrame? frame = null;
        frame = new Halo.PanelResizeFrame(handle, () => true, () => DispatcherQueue.TryEnqueue(ApplyFrame),
            () => layout.CornerRadius, minimumSize: (500, 350));
        void ApplyFrame()
        {
            var radius = new CornerRadius(Halo.App.Settings?.Data.Radius ?? 24);
            if (layout.CornerRadius != radius) layout.CornerRadius = radius;
            Halo.PanelTheme.ClipDesktopBackdrop(layout);
            frame?.UpdateRegion();
        }
        Halo.Native.RemoveWindowFrame(handle);
        backdrop?.ReapplyTransparentFrame();
        layout.Loaded += (_, _) => ApplyFrame();
        layout.SizeChanged += (_, _) => ApplyFrame();
        Activated += (_, _) => { ApplyFrame(); RefreshNavigationIcons(); };
        app.BarAppearanceChanged += PreviewAppearanceChanged;
        Closed += (_, _) =>
        {
            app.BarAppearanceChanged -= PreviewAppearanceChanged;
            if (navigationRingBridge is not null) navigationRingBridge.Changed -= RefreshNavigationIcons;
            dragRegion.Dispose();
            frame.Dispose();
        };
    }

    public void UpdatePreviewState(bool enabled)
    {
        previewToggle.Content = enabled ? "关闭预览栏" : "开启预览栏";
        RefreshNavigationIcons();
        UpdateEffectiveTransparency();
    }

    private void RefreshNavigationIcons()
    {
        var source = app.ModuleIcons;
        if (!ReferenceEquals(source, boundIcons))
        {
            if (source is null)
            {
                aiIcon.ClearValue(ImageIcon.SourceProperty);
                aiIcon.Source = PreviewModuleIcons.CreateStaticAiIcon();
            }
            else aiIcon.SetBinding(ImageIcon.SourceProperty, new Binding
                { Source = source.AiIcon, Path = new PropertyPath(nameof(Image.Source)), Mode = BindingMode.OneWay });
            boundIcons = source;
        }
        var bridge = Halo.App.RingBridge;
        if (!ReferenceEquals(bridge, navigationRingBridge))
        {
            if (navigationRingBridge is not null) navigationRingBridge.Changed -= RefreshNavigationIcons;
            navigationRingBridge = bridge;
            if (navigationRingBridge is not null) navigationRingBridge.Changed += RefreshNavigationIcons;
        }
        var appearance = Halo.App.Settings is null ? new Halo.UiAppearance() : Halo.App.Appearance;
        var light = nav.ActualTheme != ElementTheme.Dark;
        Color? foreground = accessibility.HighContrast ? colors.GetColorValue(UIColorType.Foreground) : null;
        var style = (appearance, light, foreground);
        if (style == navigationRingStyle) return;
        haloIcon.Source = PreviewModuleIcons.CreateStaticHaloIcon(appearance, light, foreground);
        navigationRingStyle = style;
    }
    internal void Select(SettingsSection section) => nav.SelectedItem = navigation[section];
    internal void ShowHaloStorage() => haloSettings?.ShowStorageSettings();
    internal void ShowAppCenterIconSettings() => appCenterSettings?.SelectIcon();

    public void SetStatus(string text)
    {
        status.Text = text;
        status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildPreview(StackPanel page)
    {
        resetHeight = ActionButton("默认", () => app.SetTaskbarHeight(null), "恢复默认任务栏高度");
        AutomationProperties.SetName(heightSlider, "任务栏高度");
        heightSlider.ValueChanged += (_, args) =>
        {
            if (!syncingHeight) app.SetTaskbarHeight((int)Math.Round(args.NewValue));
        };
        page.Children.Add(SettingRow("任务栏高度", "拖动即时生效。栏较矮时，图标会随之缩小。",
            Actions(heightSlider, heightValue, resetHeight)));
        UpdateHeightControl();
        BuildGlassSettings(page);
        previewToggle.Click += (_, _) => app.TogglePreview();
        page.Children.Add(SettingRow("AITaskbar 任务栏", "在栏上右键可显示 Windows 原生任务栏，开启双栏对照。", previewToggle));

        BuildLumaPreferences(page);
    }

    internal void UpdateHeightControl()
    {
        syncingHeight = true;
        heightSlider.Value = app.TaskbarHeight;
        heightValue.Text = $"{app.TaskbarHeight} DIP";
        resetHeight.IsEnabled = app.TaskbarHeight != PreviewHeight.Default;
        syncingHeight = false;
    }

    private void AddNavigation(SettingsSection section, string title, IconElement icon)
    {
        var item = new NavigationViewItem { Content = title, Icon = icon, Tag = section };
        nav.MenuItems.Add(item);
        navigation.Add(section, item);
    }

    private static Grid SettingRow(string title, string help, FrameworkElement control)
    {
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = help, FontSize = 12, Opacity = .72, TextWrapping = TextWrapping.Wrap });
        var row = new Grid { ColumnSpacing = 16, MinHeight = 64 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    private static Button ActionButton(string label, Action run, string? name = null)
    {
        var button = new Button { Content = label, CornerRadius = new CornerRadius(18) };
        if (name is not null) AutomationProperties.SetName(button, name);
        button.Click += (_, _) => run();
        return button;
    }

    private static StackPanel Actions(params UIElement[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }
}
