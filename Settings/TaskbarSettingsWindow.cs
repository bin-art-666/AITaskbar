using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class TaskbarSettingsWindow : Window
{
    private readonly App app;
    private readonly Button previewToggle = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly UISettings colors = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly List<Border> panes = [];

    public TaskbarSettingsWindow(App app)
    {
        this.app = app;
        Title = "AITaskbar · 设置";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AITaskbar-Ring.ico"));
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
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
        AppWindow.Closing += (_, args) => { args.Cancel = true; AppWindow.Hide(); };

        var nav = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false, IsPaneOpen = true, OpenPaneLength = 144,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left
        };
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        nav.Resources["NavigationViewDefaultPaneBackground"] = transparent;
        nav.Resources["NavigationViewExpandedPaneBackground"] = transparent;
        nav.Resources["NavigationViewContentBackground"] = transparent;

        var preview = BuildPage("预览栏", BuildPreview);
        var modules = BuildPage("集成模块", BuildModules);
        var previewItem = NavigationItem("预览栏", "\uE713", preview);
        nav.MenuItems.Add(previewItem);
        nav.MenuItems.Add(NavigationItem("集成模块", "\uE8A5", modules));
        nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: Border page }) nav.Content = page;
        };
        nav.SelectedItem = previewItem;
        nav.Content = preview;

        var footer = new Grid { Padding = new Thickness(20, 8, 20, 12), ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var exit = ActionButton("退出 AITaskbar", app.RequestExit);
        Grid.SetColumn(exit, 1);
        footer.Children.Add(status);
        footer.Children.Add(exit);
        var caption = new Grid { Margin = new Thickness(8, 6, 8, 0), Background = transparent };
        caption.Children.Add(new TextBlock { Text = "AITaskbar · 设置", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var back = Halo.WindowControls.IconButton("\uE72B", "返回任务栏");
        back.Click += (_, _) =>
        {
            AppWindow.Hide();
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
        close.Click += (_, _) => AppWindow.Hide();
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
        Grid.SetRow(nav, 1);
        Grid.SetRow(footer, 2);
        layout.Children.Add(caption);
        layout.Children.Add(nav);
        layout.Children.Add(footer);
        void RefreshMaterial()
        {
            bool dark = layout.ActualTheme == ElementTheme.Dark;
            bool contrast = accessibility.HighContrast;
            var baseColor = contrast ? colors.GetColorValue(UIColorType.Background) : dark
                ? Color.FromArgb(glass ? (byte)158 : (byte)255, 27, 31, 39)
                : Color.FromArgb(glass ? (byte)137 : (byte)255, 244, 248, 250);
            layout.Background = new SolidColorBrush(baseColor);
            var edge = contrast ? colors.GetColorValue(UIColorType.Foreground)
                : dark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(32, 19, 40, 57);
            var fill = dark ? Color.FromArgb(30, 17, 20, 26) : Color.FromArgb(22, 255, 255, 255);
            foreach (var pane in panes)
            {
                pane.Background = new SolidColorBrush(contrast ? baseColor : fill);
                pane.BorderBrush = new SolidColorBrush(edge);
            }
        }
        layout.ActualThemeChanged += (_, _) => RefreshMaterial();
        layout.Loaded += (_, _) => RefreshMaterial();
        Content = layout;
        var dragRegion = new Halo.PanelCaption(this, caption, windowActions, () => true, nativeResizeRegions: false);
        Halo.PanelResizeFrame? frame = null;
        frame = new Halo.PanelResizeFrame(handle, () => true, () => DispatcherQueue.TryEnqueue(ApplyFrame),
            () => layout.CornerRadius);
        void ApplyFrame()
        {
            var radius = new CornerRadius(Halo.App.Settings.Data.Radius);
            if (layout.CornerRadius != radius) layout.CornerRadius = radius;
            Halo.PanelTheme.ClipDesktopBackdrop(layout);
            frame?.UpdateRegion();
        }
        Halo.Native.RemoveWindowFrame(handle);
        backdrop?.ReapplyTransparentFrame();
        layout.Loaded += (_, _) => ApplyFrame();
        layout.SizeChanged += (_, _) => ApplyFrame();
        Activated += (_, _) => ApplyFrame();
        Closed += (_, _) => { dragRegion.Dispose(); frame.Dispose(); };
    }

    public void UpdatePreviewState(bool enabled) => previewToggle.Content = enabled ? "关闭预览栏" : "开启预览栏";

    public void SetStatus(string text)
    {
        status.Text = text;
        status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildPreview(StackPanel page)
    {
        previewToggle.Click += (_, _) => app.TogglePreview();
        page.Children.Add(SettingRow("双栏对比", "让 AITaskbar 预览栏与 Windows 原生任务栏同时显示。", previewToggle));

        var slider = new Slider
        {
            Minimum = PreviewIconSize.Minimum, Maximum = PreviewIconSize.Maximum, StepFrequency = 2,
            Value = app.IconSize, MinWidth = 120
        };
        var size = new TextBlock { Text = $"{app.IconSize} DIP", Opacity = .72, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, "应用图标大小");
        slider.ValueChanged += (_, args) =>
        {
            int next = (int)Math.Round(args.NewValue);
            if (!app.TrySetIconSize(next)) slider.Value = app.IconSize;
            size.Text = $"{app.IconSize} DIP";
        };
        var controls = Actions(slider, size);
        page.Children.Add(SettingRow("应用图标大小", "统一调整中间应用区的图标。拖动时即时生效。", controls));

        var colorChoice = new Button();
        var sampleDot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center };
        var sampleBar = new Border { Width = 16, Height = 4, CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center };
        var sample = Actions(sampleDot, sampleBar);
        var label = new TextBlock();
        colorChoice.Content = Actions(sample, label);
        void UpdateColor()
        {
            label.Text = app.IndicatorColor is { } value ? PreviewIndicatorColor.Format(value) : "跟随系统";
            AutomationProperties.SetName(colorChoice, "运行标记颜色 · " + label.Text);
            sampleDot.Background = sampleBar.Background = new SolidColorBrush(
                app.IndicatorColor ?? colors.GetColorValue(UIColorType.Accent));
        }
        UpdateColor();
        colorChoice.Click += (_, _) => PickIndicatorColor(colorChoice, UpdateColor);
        colorChoice.CornerRadius = new CornerRadius(18);
        page.Children.Add(SettingRow("运行标记颜色", "运行中的圆点与前台窗口的短条共用此颜色。", colorChoice));
    }

    private void PickIndicatorColor(Button anchor, Action update)
    {
        var picker = new ColorPicker
        {
            Color = app.IndicatorColor ?? colors.GetColorValue(UIColorType.Accent),
            IsAlphaEnabled = false, IsHexInputVisible = true, IsColorChannelTextInputVisible = false, MaxWidth = 258
        };
        var content = new StackPanel { Spacing = 8, MaxWidth = 258 };
        var flyout = new Flyout { Content = content };
        picker.ColorChanged += (_, args) => app.PreviewIndicator(args.NewColor);
        flyout.Closed += (_, _) => app.PreviewIndicator(app.IndicatorColor);
        void Save(Color? color)
        {
            if (!app.TrySetIndicatorColor(color)) return;
            update();
            flyout.Hide();
        }
        content.Children.Add(picker);
        content.Children.Add(Actions(ActionButton("应用颜色", () => Save(picker.Color)),
            ActionButton("跟随系统", () => Save(null))));
        flyout.ShowAt(anchor);
    }

    private void BuildModules(StackPanel page)
    {
        page.Children.Add(SettingRow("Halo", "文件、图片与文字中转站。", Actions(
            ActionButton("打开／收起", () => app.HaloModule.Handle("Toggle"), "Halo 打开或收起"),
            ActionButton("设置", Halo.App.OpenSettings, "Halo 设置"))));
        page.Children.Add(SettingRow("AIEnter", "AI 对话面板与附件入口。", Actions(
            ActionButton("打开／收起", () => _ = app.AiModule.ToggleAiWindow(false), "AIEnter 打开或收起"),
            ActionButton("设置", app.AiModule.OpenSettings, "AIEnter 设置"))));
        page.Children.Add(SettingRow("LumaBar", "调整任务栏材质、明暗和鼠标光效。",
            ActionButton("启用／打开", app.OpenAppearance, "LumaBar 启用或打开")));
    }

    private static NavigationViewItem NavigationItem(string title, string glyph, Border page) =>
        new() { Content = title, Icon = new FontIcon { Glyph = glyph }, Tag = page };

    private Border BuildPage(string title, Action<StackPanel> build)
    {
        var page = new StackPanel { Padding = new Thickness(24, 16, 24, 16), Spacing = 10, MaxWidth = 880 };
        page.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8) });
        build(page);
        var pane = new Border
        {
            Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled }
        };
        panes.Add(pane);
        return pane;
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
