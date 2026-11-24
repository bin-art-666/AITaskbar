using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace Halo;

internal sealed partial class SettingsPage : Grid
{
    readonly StationWindow owner;
    readonly Window settingsHost;
    readonly AITaskbar.SettingsSubpageNavigation nav;
    readonly Dictionary<string, ScrollViewer> pageByTag = new();
    readonly Button save = new() { Content = "保存" };
    readonly Button cancel = new() { Content = "取消" };
    readonly TextBlock saveError = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly List<(Slider Slider, string Name)> sliders = [];
    readonly List<Button> notificationPreviewButtons = [];
    readonly List<Button> allButtons = [];
    readonly List<CheckBox> allCheckBoxes = [];
    bool loading;
    bool saving;
    readonly TextBox ringColor = new() { Header = "暗色任务栏圆环颜色", PlaceholderText = "#RRGGBB", MaxLength = 7 };
    readonly TextBox lightColor = new() { Header = "亮色任务栏圆环颜色", PlaceholderText = "#RRGGBB", MaxLength = 7 };
    readonly TextBox buttonColor = new() { Header = "按钮与提示颜色", PlaceholderText = "#RRGGBB 或 #AARRGGBB", MaxLength = 9 };
    readonly TextBox folderColor = new() { Header = "文件夹颜色", PlaceholderText = "#RRGGBB", MaxLength = 7 };
    readonly TextBox lightBackplateColor = new() { Header = "亮色背板", PlaceholderText = "跟随任务栏", MaxLength = 7 };
    readonly TextBox darkBackplateColor = new() { Header = "暗色背板", PlaceholderText = "跟随任务栏", MaxLength = 7 };
    readonly TextBlock backplateColorError = new() { Text = "请输入 #RRGGBB，或留空跟随任务栏。", Visibility = Visibility.Collapsed };
    readonly TextBlock folderColorError = new() { Text = "请输入 #RRGGBB 颜色值", Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock buttonColorError = new() { Text = "请输入 #RRGGBB 或 #AARRGGBB 颜色值", Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock colorNoticeLabel = new() { Text = "底部提示预览", Padding = new Thickness(10, 6, 10, 6) };
    readonly Border colorNotice = new() { CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left };
    readonly Slider flowLength = new() { Minimum = 10, Maximum = 80, StepFrequency = 1 };
    readonly CheckBox hoverFlow = new() { Content = "悬停流光（自动适配任务栏主题）" };
    readonly Slider flowSpeed = new() { Minimum = 50, Maximum = 200, StepFrequency = 10 };
    readonly TextBlock colorError = new() { Text = "请输入 #RRGGBB 颜色值", Visibility = Visibility.Collapsed };
    readonly CheckBox pinned = new() { Content = "置顶面板" };
    readonly CheckBox autoHide = new() { Content = "展开后失焦自动收起" };
    readonly ComboBox sortMode = new();
    ScrollViewer storageSettingsPage = null!;
    internal Task PendingSave { get; private set; } = Task.CompletedTask;
    internal SettingsPage(StationWindow owner, Window? settingsHost = null, AITaskbar.SettingsPaneWidths? widths = null)
    {
        this.owner = owner;
        this.settingsHost = settingsHost ?? owner;
        if (settingsHost is not null)
        {
            var saveShortcut = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
                { Key = Windows.System.VirtualKey.S, Modifiers = Windows.System.VirtualKeyModifiers.Control, ScopeOwner = this };
            var cancelShortcut = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
                { Key = Windows.System.VirtualKey.Escape, ScopeOwner = this };
            saveShortcut.Invoked += (_, args) => args.Handled = HandleShortcut(true);
            cancelShortcut.Invoked += (_, args) => args.Handled = HandleShortcut(false);
            KeyboardAccelerators.Add(saveShortcut);
            KeyboardAccelerators.Add(cancelShortcut);
        }
        Visibility = Visibility.Collapsed;

        var pages = new (string Tag, string Title, Action<StackPanel> Build)[]
        {
            ("appearance", "外观", BuildAppearance),
            ("ring", "任务栏圆环", BuildRing),
            ("notification", "提示", BuildNotification),
            ("window", "窗口与行为", BuildWindow),
            ("actions", "操作", body => { BuildItemMenuSettings(body); BuildTrashSettings(body); BuildShortcutSettings(body); }),
        };
        foreach (var (tag, title, build) in pages)
        {
            var body = new StackPanel();
            build(body);
            var page = AITaskbar.SettingsNavigation.Subpage(title, body);
            pageByTag.Add(tag, page);
            if (tag == "window") storageSettingsPage = page;
        }
        nav = new(pages.Select(page => (page.Title, (FrameworkElement)pageByTag[page.Tag])),
            widths, AITaskbar.SettingsSection.Halo, "Halo 二级菜单", 128);

        save.Click += (_, _) => StartSave();
        cancel.Click += (_, _) => Reset();
        RegisterButton(save); RegisterButton(cancel);
        RegisterCheckBox(pinned); RegisterCheckBox(autoHide); RegisterCheckBox(hoverFlow);
        RegisterCheckBox(showInteractionBorders);
        var footer = AITaskbar.SettingsNavigation.Footer(saveError, cancel, save);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetRow(footer, 1);
        Children.Add(nav);
        Children.Add(footer);
    }

    void StartSave()
    {
        if (!saving && save.IsEnabled) PendingSave = Save();
    }

    async Task Save()
    {
        saving = true;
        using var lease = owner.Retain();
        save.IsEnabled = cancel.IsEnabled = sortMode.IsEnabled = false;
        nav.IsEnabled = false;
        try {
            var value = Value();
            value.Validate();
            if (!App.Station.ReadOnly && sortMode.SelectedItem is ComboBoxItem { Tag: StationSort sort }
                && sort != App.Station.Sort(value.NewestFirst))
                await App.Station.SetSort(sort, value.NewestFirst);
            var current = App.Settings.Data;
            App.Settings.SaveSoon(value with {
                Width = current.Width, Height = current.Height, Detached = current.Detached,
                CompactX = current.CompactX, CompactY = current.CompactY,
                ExpandedWidth = current.ExpandedWidth, ExpandedHeight = current.ExpandedHeight,
                ExpandedX = current.ExpandedX, ExpandedY = current.ExpandedY,
            });
            Reset();
        } catch (Exception error) { saveError.Text = error.Message; saveError.Visibility = Visibility.Visible; }
        finally {
            saving = false;
            nav.IsEnabled = true;
            cancel.IsEnabled = true;
            sortMode.IsEnabled = !App.Station.ReadOnly;
            UpdateSaveState();
        }
    }
    void BuildAppearance(StackPanel body)
    {
        body.Children.Add(Section("按钮、提示与文件反馈"));
        AddColor(body, buttonColor, UiDefaults.ButtonColor, allowAlpha: true);
        body.Children.Add(buttonColorError);
        body.Children.Add(showInteractionBorders);
        showInteractionBorders.Checked += (_, _) => Preview();
        showInteractionBorders.Unchecked += (_, _) => Preview();
        colorNotice.Child = colorNoticeLabel;
        colorNotice.ActualThemeChanged += (_, _) => RefreshNoticePreview();
        body.Children.Add(colorNotice);
        body.Children.Add(Section("内容网格"));
        AddColor(body, folderColor, HaloDefaults.FolderColor, ownError: folderColorError);
        body.Children.Add(folderColorError);
        body.Children.Add(BuildGridPreview());
        Add(body, "文件图标大小", 32, 96);
        Add(body, "水平间距", 4, 32, " DIP");
        Add(body, "垂直间距", 4, 32, " DIP");
        BuildDragPreviewSettings(body);
        body.Children.Add(Section("窗口背板"));
        if (owner.UsesTaskbarFlyout)
        {
            AddColor(body, lightBackplateColor, "", allowEmpty: true);
            AddColor(body, darkBackplateColor, "", allowEmpty: true);
            body.Children.Add(backplateColorError);
        }
        Add(body, "背板透明程度", 0, 80, "%");
        Add(body, "背板模糊程度", 0, 100, "%");
        Add(body, "圆角", 12, 40);
    }
    void BuildRing(StackPanel body)
    {
        body.Children.Add(BuildRingPreviews());
        AddColor(body, lightColor, HaloDefaults.RingLightColor);
        AddColor(body, ringColor, HaloDefaults.RingColor);
        body.Children.Add(colorError);
        body.Children.Add(Section("尺寸与流光"));
        Add(body, "圆环大小", 16, 36);
        Add(body, "圆环粗细", 5, 35, "%");
        body.Children.Add(hoverFlow);
        AddFlow(body, "流光长度", flowLength, "%");
        AddFlow(body, "流光速度", flowSpeed, "%");
        ToolTipService.SetToolTip(flowLength, "占圆周的比例：短光点到长光带；长度和速度独立");
        ToolTipService.SetToolTip(flowSpeed, "100% 为标准速度；关闭系统动画时使用静态强调色");
        hoverFlow.Checked += (_, _) => { flowSpeed.IsEnabled = flowLength.IsEnabled = true; Preview(); };
        hoverFlow.Unchecked += (_, _) => { flowSpeed.IsEnabled = flowLength.IsEnabled = false; Preview(); };
    }
    void BuildNotification(StackPanel body)
    {
        foreach (bool important in new[] { false, true })
        {
            var sample = new Button { Content = important ? "预览失败提示" : "预览普通提示" };
            sample.Click += (_, _) => ShowNotificationSample(important);
            notificationPreviewButtons.Add(sample);
            Add(body, important ? "失败提示显示时长" : "普通提示显示时长", 1, 30, " 秒", sample);
        }
        body.Children.Add(new TextBlock { Text = "失败时长也用于警告。复制、备份进行中的提示持续显示。", TextWrapping = TextWrapping.Wrap, Opacity = .65 });
        body.Children.Add(notificationSample);
    }
    void BuildWindow(StackPanel body)
    {
        body.Children.Add(Section("窗口行为"));
        body.Children.Add(pinned);
        body.Children.Add(autoHide);
        body.Children.Add(new TextBlock { Text = "置顶后点击外部不收起；独立窗口失焦自动收起遵循窗口偏好，置顶优先。", TextWrapping = TextWrapping.Wrap, Opacity = .65 });
        body.Children.Add(Section("排序方式"));
        foreach (var (mode, label) in new[] { (StationSort.Manual, "手动排序（保留空位）"), (StationSort.Newest, "最新在前"), (StationSort.Oldest, "最早在前") })
        {
            var item = new ComboBoxItem { Content = label, Tag = mode };
            sortMode.Items.Add(item);
        }
        body.Children.Add(sortMode);
        body.Children.Add(new TextBlock { Text = "排序只影响显示顺序；手动排序保留空位与手动布局。", TextWrapping = TextWrapping.Wrap, Opacity = .65 });
        BuildStorageSettings(body);
        pinned.Checked += (_, _) => UpdateSaveState();
        pinned.Unchecked += (_, _) => UpdateSaveState();
        autoHide.Checked += (_, _) => UpdateSaveState();
        autoHide.Unchecked += (_, _) => UpdateSaveState();
        sortMode.SelectionChanged += (_, _) => UpdateSaveState();
        sortMode.IsEnabled = !App.Station.ReadOnly;
    }
    void Add(StackPanel body, string name, int min, int max, string unit = "", Button? sample = null)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = name });
        var value = new TextBlock { Opacity = .65 }; Grid.SetColumn(value, 1); row.Children.Add(value); body.Children.Add(row);
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = 1, IsThumbToolTipEnabled = true }; sliders.Add((slider, name));
        if (sample == null) body.Children.Add(slider);
        else {
            var controls = new Grid { ColumnSpacing = 10 };
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(sample, 1); controls.Children.Add(slider); controls.Children.Add(sample); body.Children.Add(controls);
        }
        AutomationProperties.SetName(slider, name);
        if (name == "圆环粗细") ToolTipService.SetToolTip(slider, "线条宽度占圆环直径的比例");
        slider.ValueChanged += (_, _) => { value.Text = $"{slider.Value:0}{unit}"; Preview(); };
        value.Text = $"{slider.Value:0}{unit}";
    }
    void AddColor(StackPanel body, TextBox input, string defaultColor, bool allowAlpha = false, TextBlock? ownError = null, bool allowEmpty = false)
    {
        var picker = new ColorPicker { IsAlphaEnabled = allowAlpha, IsAlphaSliderVisible = allowAlpha, IsColorSliderVisible = true, IsColorChannelTextInputVisible = false, IsHexInputVisible = false };
        bool Valid(string color) => allowAlpha ? ValidButtonColor(color) : UiAppearance.ValidRingColor(color);
        bool syncing = false;
        void Sync() { if (!Valid(input.Text)) return; syncing = true; uint c = UiAppearance.ColorValue(input.Text, 0); picker.Color = Windows.UI.Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c); syncing = false; }
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var choose = new Button { Content = "选取颜色", Flyout = new Flyout { Content = picker } };
        var reset = new Button { Content = "恢复默认" }; reset.Click += (_, _) => input.Text = defaultColor;
        AutomationProperties.SetName(choose, $"选取{input.Header}");
        AutomationProperties.SetName(reset, $"恢复默认{input.Header}");
        RegisterButton(choose); RegisterButton(reset);
        colors.Children.Add(choose); colors.Children.Add(reset); body.Children.Add(input); body.Children.Add(colors);
        input.TextChanged += (_, _) => {
            if (!syncing) Sync();
            if (allowEmpty) backplateColorError.Visibility = ValidBackplateColors() ? Visibility.Collapsed : Visibility.Visible;
            else if (ownError != null) ownError.Visibility = Valid(input.Text) ? Visibility.Collapsed : Visibility.Visible;
            else if (allowAlpha) buttonColorError.Visibility = Valid(input.Text) ? Visibility.Collapsed : Visibility.Visible;
            else colorError.Visibility = ValidRingColor() ? Visibility.Collapsed : Visibility.Visible;
            Preview();
        };
        picker.ColorChanged += (_, e) => {
            if (syncing) return;
            var color = e.NewColor;
            input.Text = allowAlpha && color.A != 255 ? $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        };
    }
    void AddFlow(StackPanel body, string name, Slider slider, string unit)
    {
        var label = new TextBlock(); body.Children.Add(label); body.Children.Add(slider);
        slider.ValueChanged += (_, _) => { label.Text = $"{name}  {slider.Value:0}{unit}"; Preview(); };
        label.Text = $"{name}  {slider.Value:0}{unit}";
    }
    bool ValidRingColor() => UiAppearance.ValidRingColor(ringColor.Text) && UiAppearance.ValidRingColor(lightColor.Text);
    bool ValidBackplateColors() => UiAppearance.ValidBackplateColor(lightBackplateColor.Text) && UiAppearance.ValidBackplateColor(darkBackplateColor.Text);
    bool ValidAppearanceColors() => ValidRingColor() && ValidButtonColor(buttonColor.Text) && UiAppearance.ValidRingColor(folderColor.Text) && ValidBackplateColors();
    static bool ValidButtonColor(string value) => value.Length != 0 && UiAppearance.ValidColor(value);
    internal void RefreshNoticePreview() => PanelTheme.ApplyNoticeColors(colorNotice, colorNoticeLabel);
    void Preview()
    {
        if (loading || Visibility != Visibility.Visible) return;
        var value = Value(); owner.PreviewGrid(StationGridAppearance.From(value));
        owner.PreviewNotificationTiming(NotificationTiming.From(value));
        if (ValidAppearanceColors()) App.Preview(value.Appearance);
        UpdateSamples(value);
        RefreshNoticePreview();
        UpdateSaveState();
    }
    void UpdateSaveState()
    {
        if (loading) return;
        bool sortChanged = !App.Station.ReadOnly && sortMode.SelectedItem is ComboBoxItem { Tag: StationSort sort }
            && sort != App.Station.Sort(App.Settings.Data.NewestFirst);
        save.IsEnabled = !saving && !editingStorage && !editingTrash && !App.Settings.ReadOnly && ValidAppearanceColors() && (Value() != App.Settings.Data || sortChanged);
    }
    int SliderValue(string name) => (int)sliders.Single(entry => entry.Name == name).Slider.Value;
    HaloSettings Value()
    {
        var current = App.Settings.Data;
        var menu = DraftFolderMenu;
        var fileMenu = DraftFileMenu;
        return current with { FileMenu = fileMenu == current.EffectiveFileMenu ? current.FileMenu : fileMenu, FolderMenuPrimary = menu.PrimaryActions, FolderMenu = menu == current.EffectiveFolderMenu ? current.FolderMenu : menu, ShowInteractionBorders = showInteractionBorders.IsChecked == true, DragPreviewMode = SelectedDragMode, DragPreviewDistance = (int)dragDistance.Value, ButtonColor = buttonColor.Text.ToUpperInvariant(), FolderColor = folderColor.Text.ToUpperInvariant(), IconSize = SliderValue("文件图标大小"), HorizontalSpacing = SliderValue("水平间距"), VerticalSpacing = SliderValue("垂直间距"), NormalNotificationSeconds = SliderValue("普通提示显示时长"), ErrorNotificationSeconds = SliderValue("失败提示显示时长"), Transparency = SliderValue("背板透明程度"), Blur = SliderValue("背板模糊程度"), Radius = SliderValue("圆角"), RingSize = SliderValue("圆环大小"), RingThickness = SliderValue("圆环粗细"), RingColor = ringColor.Text.ToUpperInvariant(), RingHoverFlow = hoverFlow.IsChecked == true, RingFlowSpeed = (int)flowSpeed.Value, RingLightColor = lightColor.Text.ToUpperInvariant(), RingFlowLength = (int)flowLength.Value, Pinned = pinned.IsChecked == true, DetachedHideOnBlur = autoHide.IsChecked == true,
            LightBackplateColor = lightBackplateColor.Text.ToUpperInvariant(), DarkBackplateColor = darkBackplateColor.Text.ToUpperInvariant() };
    }

    // 验收测试通过名称/标签查找控件；这些辅助方法让测试不依赖具体布局结构。
    internal Slider FindSlider(string name) => sliders.Single(entry => entry.Name == name).Slider;
    internal Button FindButton(string label) => allButtons.Concat(notificationPreviewButtons).FirstOrDefault(button => Equals(button.Content, label))
        ?? throw new InvalidOperationException($"未找到按钮：{label}");
    internal CheckBox FindCheckBox(string label) => allCheckBoxes.FirstOrDefault(box => Equals(box.Content, label))
        ?? throw new InvalidOperationException($"未找到复选框：{label}");
    void RegisterButton(Button button) => allButtons.Add(button);
    void RegisterCheckBox(CheckBox box) => allCheckBoxes.Add(box);
    internal void Reset()
    {
        ClearPreviews();
        var original = App.Settings.Data; loading = true;
        RestoreDragPreviewSettings(original);
        LoadFolderMenu(original.EffectiveFolderMenu);
        LoadFileMenu(original.EffectiveFileMenu);
        RefreshStoragePath();
        RefreshTrashSettings();
        saveError.Visibility = Visibility.Collapsed;
        int[] values = [original.IconSize, original.HorizontalSpacing, original.VerticalSpacing, original.Transparency, original.Blur, original.Radius, original.RingSize, original.RingThickness, original.NormalNotificationSeconds, original.ErrorNotificationSeconds];
        // 注意：Add 顺序为 外观(图标/水平/垂直/透明/模糊/圆角) → 圆环(大小/粗细) → 提示(普通/失败)
        var sliderOrder = new[] { "文件图标大小", "水平间距", "垂直间距", "背板透明程度", "背板模糊程度", "圆角", "圆环大小", "圆环粗细", "普通提示显示时长", "失败提示显示时长" };
        for (int i = 0; i < sliderOrder.Length; i++) FindSlider(sliderOrder[i]).Value = values[i];
        ringColor.Text = original.RingColor; lightColor.Text = original.RingLightColor;
        buttonColor.Text = original.ButtonColor.Length == 0 ? UiDefaults.ButtonColor : original.ButtonColor;
        folderColor.Text = original.FolderColor;
        lightBackplateColor.Text = original.LightBackplateColor;
        darkBackplateColor.Text = original.DarkBackplateColor;
        showInteractionBorders.IsChecked = original.ShowInteractionBorders;
        flowLength.Value = original.RingFlowLength; hoverFlow.IsChecked = original.RingHoverFlow; flowSpeed.Value = original.RingFlowSpeed;
        flowSpeed.IsEnabled = flowLength.IsEnabled = original.RingHoverFlow;
        pinned.IsChecked = original.Pinned; autoHide.IsChecked = original.DetachedHideOnBlur;
        foreach (ComboBoxItem item in sortMode.Items) if (item.Tag is StationSort sort && sort == App.Station.Sort(original.NewestFirst)) sortMode.SelectedItem = item;
        loading = false;
        UpdateSamples(original);
        RefreshNoticePreview();
        UpdateSaveState();
    }
    internal void ClearPreviews()
    {
        notificationTimer?.Stop();
        notificationSample.IsOpen = false;
        owner.ClearSettingsNotification();
        App.Preview(null);
        owner.PreviewGrid(null);
        owner.PreviewNotificationTiming(null);
    }
    internal void SyncPin()
    {
        loading = true;
        pinned.IsChecked = App.Settings.Data.Pinned;
        loading = false;
        UpdateSaveState();
    }

    internal ComboBox SortControlForVerification => sortMode;
    internal TextBox ButtonColorForVerification => buttonColor;
    internal TextBox FolderColorForVerification => folderColor;
}
