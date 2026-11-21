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
    private readonly NavigationView nav = SettingsNavigation.Sidebar(132);
    private readonly AppCenterIcon sampleIcon = new(48);
    private readonly ToggleSwitch showInTaskbar = new()
    {
        Header = "在任务栏显示应用中心", OnContent = "显示", OffContent = "隐藏"
    };
    private readonly ToggleButton animate = new() { Content = "预览展开状态" };
    private readonly Border samplePanel = new() { CornerRadius = new CornerRadius(12), MaxWidth = 360,
        HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12) };
    private readonly StackPanel sampleRows = new() { Spacing = 4 };
    private readonly List<TextBlock> appRows = [];
    private readonly List<Action> syncSliders = [];
    private readonly TextBlock status = new();
    private readonly Button save = new() { Content = "保存应用中心设置" };
    private readonly Button cancel = new() { Content = "取消更改" };
    private readonly ComboBox density = new();
    private readonly ToggleSwitch follow = new() { OnContent = "跟随任务栏", OffContent = "自定义透明度" };
    private readonly Slider transparency = new() { Minimum = 0, Maximum = 100, StepFrequency = 1 };
    private readonly TextBlock transparencyValue = new();
    private readonly UISettings colors = new();
    private readonly AccessibilitySettings accessibility = new();
    private AppCenterAppearance draft;
    private bool syncing;

    internal AppCenterSettingsPage(App app, SettingsPaneWidths widths)
    {
        this.app = app;
        draft = app.SavedAppCenterAppearance;
        nav.MenuItems.Add(new NavigationViewItem { Content = "图标设计", Tag = BuildIconPage() });
        nav.MenuItems.Add(new NavigationViewItem { Content = "界面设计", Tag = BuildPanelPage() });
        var compact = SettingsNavigation.ConnectSubpages(nav, item => item.Tag);
        var layout = new SettingsSplitView(nav, widths, SettingsSection.AppCenter,
            (size, condensed) => { nav.OpenPaneLength = size; nav.IsPaneVisible = !condensed; }, compact);
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        var footer = SettingsNavigation.Footer(status, cancel, save);
        SetRow(footer, 1);
        Children.Add(layout);
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
        Loaded += (_, _) => { app.BarAppearanceChanged += OnBarAppearanceChanged; RefreshPanel(); };
        Unloaded += (_, _) => app.BarAppearanceChanged -= OnBarAppearanceChanged;
        ActualThemeChanged += (_, _) => RefreshPanel();
        Sync();
    }

    internal void SelectIcon() => nav.SelectedItem = nav.MenuItems[0];

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
        sample.Click += (_, _) => animate.IsChecked = animate.IsChecked != true;
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
        AddSlider(body, "白线长度", AppCenterIconStyle.LengthMin, AppCenterIconStyle.LengthMax,
            icon => icon.LineLength, (icon, value) => icon with { LineLength = value });
        AddSlider(body, "绕圈用时", AppCenterIconStyle.LapMin, AppCenterIconStyle.LapMax,
            icon => icon.LapTenths, (icon, value) => icon with { LapTenths = value },
            value => (value / 10d).ToString("0.#", CultureInfo.CurrentCulture) + " 秒/圈");
        body.Children.Add(DefaultButton(() => Apply(draft with { Icon = new() })));
        return SettingsNavigation.Subpage("图标设计", body);
    }

    private ScrollViewer BuildPanelPage()
    {
        var body = new StackPanel();
        density.Items.Add("紧凑");
        density.Items.Add("宽松");
        AutomationProperties.SetName(density, "列表密度");
        AutomationProperties.SetName(follow, "面板背景");
        AutomationProperties.SetName(transparency, "面板透明度");
        density.SelectionChanged += (_, _) =>
        {
            if (!syncing) Apply(draft with { Panel = draft.Panel with { Spacious = density.SelectedIndex == 1 } });
        };
        follow.Toggled += (_, _) =>
        {
            if (!syncing) Apply(draft with { Panel = draft.Panel with { FollowTaskbar = follow.IsOn } });
        };
        transparency.ValueChanged += (_, args) =>
        {
            if (!syncing) Apply(draft with { Panel = draft.Panel with { Opacity = 100 - (int)args.NewValue } });
        };
        body.Children.Add(Field("列表密度", density));
        body.Children.Add(Field("面板背景", follow));
        body.Children.Add(Field("面板透明度", transparency, transparencyValue));
        body.Children.Add(Heading("展开面板预览"));
        sampleRows.Children.Add(Heading("应用中心"));
        sampleRows.Children.Add(new TextBox { PlaceholderText = "搜索应用", IsReadOnly = true, IsTabStop = false });
        sampleRows.Children.Add(Heading("已固定"));
        AddSampleRow("ChatGPT");
        AddSampleRow("微信");
        sampleRows.Children.Add(Heading("所有应用"));
        AddSampleRow("文件资源管理器");
        AddSampleRow("Microsoft Edge");
        sampleRows.Children.Add(new TextBlock { Text = "浏览本地程序", Margin = new Thickness(0, 8, 0, 0) });
        samplePanel.Child = sampleRows;
        samplePanel.IsHitTestVisible = false;
        body.Children.Add(samplePanel);
        body.Children.Add(DefaultButton(() => Apply(draft with { Panel = new() })));
        return SettingsNavigation.Subpage("界面设计", body);
    }

    private void AddSlider(StackPanel page, string name, int minimum, int maximum,
        Func<AppCenterIconStyle, int> get, Func<AppCenterIconStyle, int, AppCenterIconStyle> set,
        Func<int, string>? format = null)
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
        foreach (var sync in syncSliders) sync();
        density.SelectedIndex = draft.Panel.Spacious ? 1 : 0;
        follow.IsOn = draft.Panel.FollowTaskbar;
        transparency.IsEnabled = !follow.IsOn;
        transparency.Value = 100 - draft.Panel.Opacity;
        transparencyValue.Text = (100 - draft.Panel.Opacity) + "%";
        sampleIcon.Apply(draft.Icon);
        RefreshPanel();
        syncing = false;
        save.IsEnabled = cancel.IsEnabled = draft != app.SavedAppCenterAppearance;
    }

    private void OnBarAppearanceChanged() => DispatcherQueue.TryEnqueue(RefreshPanel);

    private void RefreshPanel()
    {
        var material = app.BarAppearance;
        bool contrast = accessibility.HighContrast;
        bool light = draft.Panel.FollowTaskbar && material is not null && !contrast
            ? material.Tone == LumaBar.Tone.Light : ActualTheme != ElementTheme.Dark;
        var standard = light ? Color.FromArgb(255, 243, 246, 250) : Color.FromArgb(255, 30, 34, 42);
        var background = draft.Panel.FollowTaskbar && material is { Material: not "normal" }
            ? PreviewBarAppearance.FromAbgr(material.Abgr) : standard;
        if (!draft.Panel.FollowTaskbar) background.A = (byte)Math.Round(draft.Panel.Opacity * 255d / 100d);
        if (contrast) background = colors.GetColorValue(UIColorType.Background);
        samplePanel.RequestedTheme = contrast ? ElementTheme.Default : light ? ElementTheme.Light : ElementTheme.Dark;
        samplePanel.Background = new SolidColorBrush(background);
        foreach (var row in appRows)
            row.Padding = new Thickness(8, draft.Panel.Spacious ? 10 : 4, 8, draft.Panel.Spacious ? 10 : 4);
    }

    private void AddSampleRow(string title)
    {
        var row = new TextBlock { Text = title };
        appRows.Add(row);
        sampleRows.Children.Add(row);
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
