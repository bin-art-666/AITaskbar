using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class AppCenterSettingsPage : Grid
{
    private readonly App app;
    private readonly SettingsSubpageNavigation nav;
    private readonly FrameworkElement interfacePage;
    private readonly AppCenterIcon sampleIcon = new(48);
    private readonly AppCenterIcon sampleTaskbarIcon = new(23);
    private readonly ToggleSwitch showInTaskbar = new()
    {
        Header = "在任务栏显示应用中心", OnContent = "显示", OffContent = "隐藏"
    };
    private readonly ToggleButton animate = new() { Content = "预览展开状态" };
    private readonly ToggleSwitch animateOutline = new() { Header = "展开时显示动态描边" };
    private readonly Border samplePanel = new() { CornerRadius = new CornerRadius(10), MaxWidth = 430,
        HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel sampleRows = new() { Padding = new Thickness(14) };
    private readonly TextBox sampleSearch = AppCenterSearch.Create(compact: true);
    private readonly Border sampleTaskbar = new();
    private readonly SolidColorBrush sampleText = new();
    private readonly List<Action> syncSliders = [];
    private readonly TextBlock status = new();
    private readonly Button save = new() { Content = "保存应用中心设置" };
    private readonly Button cancel = new() { Content = "取消更改" };
    private readonly ComboBox density = new();
    private readonly ComboBox background = new();
    private readonly ComboBox orderMode = new();
    private readonly UISettings colors = new();
    private readonly AccessibilitySettings accessibility = new();
    private AppCenterAppearance draft;
    private bool syncing;

    internal AppCenterSettingsPage(App app, SettingsPaneWidths widths)
    {
        this.app = app;
        draft = app.SavedAppCenterAppearance;
        var iconPage = BuildIconPage();
        interfacePage = BuildPanelPage();
        nav = new(new[] { ("图标设计", (FrameworkElement)iconPage), ("界面设计", interfacePage) },
            widths, SettingsSection.AppCenter, "应用中心 二级菜单");
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        var footer = SettingsNavigation.Footer(status, cancel, save);
        SetRow(footer, 1);
        Children.Add(nav);
        Children.Add(footer);
        save.Click += (_, _) =>
        {
            if (!app.TrySaveAppCenterAppearance(draft))
            {
                status.Text = "无法保存应用中心设置。";
                return;
            }
            Sync();
            status.Text = "应用中心设置已保存。";
        };
        cancel.Click += (_, _) => Reset();
        Loaded += (_, _) =>
        {
            app.BarAppearanceChanged += OnBarAppearanceChanged;
            colors.ColorValuesChanged += OnSystemColorsChanged;
            RefreshPanel();
        };
        Unloaded += (_, _) =>
        {
            app.BarAppearanceChanged -= OnBarAppearanceChanged;
            colors.ColorValuesChanged -= OnSystemColorsChanged;
        };
        ActualThemeChanged += (_, _) => RefreshPanel();
        Sync();
    }

    internal void SelectInterface() => nav.Select(interfacePage);

    internal void Reset()
    {
        animate.IsChecked = false;
        sampleIcon.SetExpanded(false);
        Apply(app.SavedAppCenterAppearance);
    }

    private ScrollViewer BuildIconPage()
    {
        var body = new StackPanel();
        AutomationProperties.SetName(showInTaskbar, "在任务栏显示应用中心");
        showInTaskbar.Toggled += (_, _) =>
        {
            if (!syncing) Apply(draft with { Icon = draft.Icon with { ShowInTaskbar = showInTaskbar.IsOn } });
        };
        body.Children.Add(showInTaskbar);
        var sample = new Button { Content = sampleIcon, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(sample, "应用中心图标预览");
        sample.Click += (_, _) => { if (animate.IsEnabled) animate.IsChecked = animate.IsChecked != true; };
        animate.Checked += (_, _) => UpdatePreview(true);
        animate.Unchecked += (_, _) => UpdatePreview(false);
        void UpdatePreview(bool expanded)
        {
            sampleIcon.SetExpanded(expanded);
            animate.Content = expanded ? "预览收起状态" : "预览展开状态";
        }
        var preview = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        preview.Children.Add(sample);
        preview.Children.Add(animate);
        body.Children.Add(preview);
        body.Children.Add(Heading("四宫格"));
        AddSlider(body, "大小（含间距）", AppCenterIconStyle.SizeMin, AppCenterIconStyle.SizeMax,
            icon => icon.Size, (icon, value) => icon with { Size = value });
        AddSlider(body, "白色方块圆角", 0, AppCenterIconStyle.RadiusMax,
            icon => icon.Radius, (icon, value) => icon with { Radius = value });
        AddSlider(body, "白色方块间距", 0, AppCenterIconStyle.GapMax,
            icon => icon.Gap, (icon, value) => icon with { Gap = value });
        body.Children.Add(Heading("展开动画"));
        AutomationProperties.SetName(animateOutline, "展开时显示动态描边");
        animateOutline.Toggled += (_, _) =>
        {
            if (!syncing) Apply(draft with { Icon = draft.Icon with { AnimateOutline = animateOutline.IsOn } });
        };
        body.Children.Add(animateOutline);
        AddSlider(body, "白线长度", AppCenterIconStyle.LengthMin, AppCenterIconStyle.LengthMax,
            icon => icon.LineLength, (icon, value) => icon with { LineLength = value }, enabled: icon => icon.AnimateOutline);
        AddSlider(body, "绕圈用时", AppCenterIconStyle.LapMin, AppCenterIconStyle.LapMax,
            icon => icon.LapTenths, (icon, value) => icon with { LapTenths = value },
            value => (value / 10d).ToString("0.#", CultureInfo.CurrentCulture) + " 秒/圈", icon => icon.AnimateOutline);
        body.Children.Add(DefaultButton(() => Apply(draft with { Icon = new() })));
        return SettingsNavigation.Subpage("图标设计", body);
    }

    private ScrollViewer BuildPanelPage()
    {
        var body = new StackPanel();
        density.Items.Add("紧凑");
        density.Items.Add("宽松");
        background.Items.Add("与任务栏一致");
        background.Items.Add("亮色");
        background.Items.Add("暗色");
        orderMode.Items.Add("A–Z");
        orderMode.Items.Add("手动排序");
        AutomationProperties.SetName(density, "图标网格密度");
        AutomationProperties.SetName(background, "主页背板");
        AutomationProperties.SetName(orderMode, "应用排序");
        density.SelectionChanged += (_, _) =>
        {
            if (!syncing) Apply(draft with { Panel = draft.Panel with { Spacious = density.SelectedIndex == 1 } });
        };
        background.SelectionChanged += (_, _) =>
        {
            if (!syncing && background.SelectedIndex >= 0)
                Apply(draft with { Panel = draft.Panel with { Backdrop = (QuickBackdrop)background.SelectedIndex } });
        };
        orderMode.SelectionChanged += (_, _) =>
        {
            if (!syncing) Apply(draft with { Panel = draft.Panel with { ManualOrder = orderMode.SelectedIndex == 1 } });
        };
        body.Children.Add(Field("图标网格密度", density));
        body.Children.Add(Field("主页背板", background));
        body.Children.Add(Field("应用排序", orderMode));
        body.Children.Add(Heading("展开面板预览"));
        BuildPanelSample();
        samplePanel.IsHitTestVisible = false;
        body.Children.Add(samplePanel);
        body.Children.Add(DefaultButton(() => Apply(draft with { Panel = new() })));
        return SettingsNavigation.Subpage("界面设计", body);
    }

    private void AddSlider(StackPanel page, string name, int minimum, int maximum,
        Func<AppCenterIconStyle, int> get, Func<AppCenterIconStyle, int, AppCenterIconStyle> set,
        Func<int, string>? format = null, Func<AppCenterIconStyle, bool>? enabled = null)
    {
        format ??= value => value + "%";
        var slider = new Slider { Minimum = minimum, Maximum = maximum, StepFrequency = 1 };
        var valueText = new TextBlock();
        AutomationProperties.SetName(slider, name);
        slider.ValueChanged += (_, args) =>
        {
            if (!syncing) Apply(draft with { Icon = set(draft.Icon, (int)args.NewValue) });
        };
        syncSliders.Add(() =>
        {
            int value = get(draft.Icon);
            slider.IsEnabled = enabled?.Invoke(draft.Icon) ?? true;
            slider.Value = value;
            valueText.Text = format(value);
        });
        page.Children.Add(Field(name, slider, valueText));
    }

    private void Apply(AppCenterAppearance next)
    {
        draft = next;
        app.PreviewAppCenterAppearance(next);
        status.Text = "";
        Sync();
    }

    private void Sync()
    {
        syncing = true;
        showInTaskbar.IsOn = draft.Icon.ShowInTaskbar;
        animateOutline.IsOn = animate.IsEnabled = draft.Icon.AnimateOutline;
        foreach (var sync in syncSliders) sync();
        density.SelectedIndex = draft.Panel.Spacious ? 1 : 0;
        background.SelectedIndex = (int)draft.Panel.Backdrop;
        orderMode.SelectedIndex = draft.Panel.ManualOrder ? 1 : 0;
        sampleIcon.Apply(draft.Icon);
        sampleTaskbarIcon.Apply(draft.Icon);
        RefreshPanel();
        syncing = false;
        save.IsEnabled = cancel.IsEnabled = draft != app.SavedAppCenterAppearance;
    }

    private void OnBarAppearanceChanged() => DispatcherQueue.TryEnqueue(RefreshPanel);
    private void OnSystemColorsChanged(UISettings sender, object args) => OnBarAppearanceChanged();

    private void RefreshPanel()
    {
        var palette = AppCenterBackplate.Resolve(draft.Panel.Backdrop, app.BarAppearance, colors, accessibility.HighContrast);
        samplePanel.RequestedTheme = palette.Theme;
        AppCenterSearch.ApplyTheme(sampleSearch, palette.Theme == ElementTheme.Dark, accessibility.HighContrast);
        // The same light and dark desktop sample sits behind each tint.
        var scene = new LinearGradientBrush { StartPoint = new(.12, .32), EndPoint = new(.96, .72) };
        double softness = Math.Clamp(palette.Blur / 90d, .01, .33);
        scene.GradientStops.Add(new() { Color = Color.FromArgb(255, 231, 239, 247), Offset = .43 - softness });
        scene.GradientStops.Add(new() { Color = Color.FromArgb(255, 31, 69, 118), Offset = .43 + softness });
        scene.GradientStops.Add(new() { Color = Color.FromArgb(255, 14, 34, 67), Offset = 1 });
        samplePanel.Background = scene;
        sampleRows.Background = new SolidColorBrush(palette.Tint);
        sampleTaskbar.Background = new SolidColorBrush(palette.Taskbar);
        sampleText.Color = palette.Text;
        sampleRows.Spacing = draft.Panel.Spacious ? 15 : 9;
    }

    private void BuildPanelSample()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        const string terminal = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";
        sampleSearch.IsReadOnly = true;
        sampleSearch.IsTabStop = false;
        sampleSearch.HorizontalAlignment = HorizontalAlignment.Center;
        sampleSearch.Width = 148;
        sampleRows.Children.Add(sampleSearch);
        var pinned = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        pinned.Children.Add(new SymbolIcon(Symbol.Pin) { Foreground = sampleText });
        pinned.Children.Add(SampleTile("资源管理器", explorer, null, "\uE8B7"));
        pinned.Children.Add(SampleTile("终端", null, terminal, "\uE756"));
        sampleRows.Children.Add(pinned);
        sampleRows.Children.Add(new Border { Background = sampleText, Opacity = .2, Height = 1 });
        var apps = new Grid { ColumnSpacing = 3 };
        foreach (var (name, id, glyph) in new[] { ("计算器", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "\uE8EF"),
            ("记事本", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "\uE70B"), ("画图", "Microsoft.Paint_8wekyb3d8bbwe!App", "\uE790") })
        {
            var tile = SampleTile(name, null, id, glyph);
            Grid.SetColumn(tile, apps.ColumnDefinitions.Count);
            apps.ColumnDefinitions.Add(new());
            apps.Children.Add(tile);
        }
        sampleRows.Children.Add(apps);
        sampleRows.Children.Add(new TextBlock { Text = "━  ·  ·", Foreground = sampleText,
            HorizontalAlignment = HorizontalAlignment.Center });

        var barIcons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Center };
        barIcons.Children.Add(sampleTaskbarIcon);
        barIcons.Children.Add(SampleArtwork(explorer, null, "\uE8B7", 23));
        barIcons.Children.Add(SampleArtwork(null, terminal, "\uE756", 23));
        sampleTaskbar.Child = barIcons;
        var screen = new StackPanel();
        screen.Children.Add(sampleRows);
        screen.Children.Add(sampleTaskbar);
        samplePanel.Child = screen;
    }

    private StackPanel SampleTile(string name, string? executable, string? appId, string fallback)
    {
        var tile = new StackPanel { Spacing = 3 };
        tile.Children.Add(SampleArtwork(executable, appId, fallback, 26));
        tile.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = sampleText,
            HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return tile;
    }

    private TaskbarIconPlate SampleArtwork(string? executable, string? appId, string fallback, int size)
    {
        var icon = app.AppArtwork.Get(executable, appId);
        FrameworkElement glyph = icon is null ? new FontIcon { Glyph = fallback, FontSize = size * .65 }
            : new Image { Stretch = Stretch.Uniform };
        var plate = new TaskbarIconPlate(size, glyph) { HorizontalAlignment = HorizontalAlignment.Center };
        TaskbarAppArtwork.Apply(plate, icon, size, app.IconLayout,
            executable is not null && app.IconPreferences.UsesOriginal(executable), accessibility.HighContrast);
        return plate;
    }

    private static TextBlock Heading(string title) => new() { Text = title, FontWeight = FontWeights.SemiBold };

    private static Grid Field(string label, FrameworkElement control, TextBlock? value = null)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (value is not null)
        {
            SetColumn(value, 1);
            row.Children.Add(value);
        }
        SetRow(control, 1);
        SetColumnSpan(control, 2);
        row.Children.Add(control);
        return row;
    }

    private static Button DefaultButton(Action reset)
    {
        var button = new Button { Content = "恢复默认" };
        button.Click += (_, _) => reset();
        return button;
    }
}
