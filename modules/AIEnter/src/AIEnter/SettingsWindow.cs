using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIEnter;

internal sealed partial class SettingsWindow : PanelWindow
{
    readonly AiOptionsStore options;
    readonly ComboBox provider = new() { Header = "默认 AI 服务", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ToggleSwitch floatingMode = new() { Header = "窗口模式", OnContent = "悬浮面板", OffContent = "独立窗口" };
    readonly ToggleSwitch pinned = new() { Header = "固定 / 置顶", OnContent = "保持展开并置顶", OffContent = "悬浮自动收起 / 独立正常层级" };
    readonly ToggleSwitch followCorners = new() { Header = "网页圆角跟随界面", OnContent = "跟随界面圆角", OffContent = "使用自定义圆角" };
    readonly Slider radius = Slider("网页圆角", 0, 40);
    readonly Slider backplateRadius = Slider("背板圆角", 12, 40);
    readonly Slider backplateTransparency = Slider("顶栏背板透明度（%）", 0, 80);
    readonly Slider backplateBlur = Slider("顶栏背板模糊度（%）", 0, 100);
    readonly Slider buttonRadius = Slider("按钮圆角", 0, 20);
    readonly Slider buttonSize = Slider("按钮高度", 28, 44);
    readonly Slider padding = Slider("窗口内边距", 0, 16);
    readonly Slider taskbarSize = Slider("任务栏图标大小", 16, 36);
    readonly Slider flowSpeed = Slider("色彩流动速度（%）", 0, 1800);
    readonly ToggleSwitch screenshotDoubleClick = new() { Header = "双击任务栏图标截图", OnContent = "开启（单击遵循系统双击等待时间）", OffContent = "关闭（单击直接打开／收起）" };
    readonly Slider screenshotSeconds = Slider("截图驻留时间（秒，0 不自动关闭）", 0, 120);
    readonly TextBox screenshotFolder = new() { Header = "截图保存目录", PlaceholderText = "留空：图片\\AIEnter\\Screenshots" };
    readonly ToggleSwitch glow = new() { Header = "对话打开时显示光晕", OnContent = "开启", OffContent = "关闭" };
    readonly Slider glowRange = Slider("光晕范围", 0, 12);
    readonly Slider glowIntensity = Slider("光晕强度（%）", 0, 100);
    readonly TextBox hotkey = new() { Header = "全局快捷键", PlaceholderText = "点击后按下快捷键，例如 Ctrl+Alt+Space", IsReadOnly = true };
    readonly TextBlock status = PanelTheme.Hint("外观调整实时预览，保存后记住；关闭撤销未保存的预览。");
    bool syncing = true, resetPanelSize;
    readonly HashSet<int> pressedModifiers = [];
    readonly Action<AiOptions> saveOptions;
    readonly string? initialHotkeyWarning;
    protected override bool DismissOnOutside => false;
    protected override bool Frameless => false;
    protected override bool AllowHide() { if (allowHideOnce) { allowHideOnce = false; return true; } if (IsDirty) { _ = ConfirmDiscardOrSave(); return false; } return true; }
    static Slider Slider(string header, int min, int max) => new()
    { Header = header, Minimum = min, Maximum = max, StepFrequency = 1, TickFrequency = 0 };
    static TextBlock Section(string title) => new() { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };

    internal SettingsWindow(AiOptionsStore options, Action quit, Action<AiOptions> saveOptions, string? hotkeyWarning)
    {
        this.options = options; this.saveOptions = saveOptions; initialHotkeyWarning = hotkeyWarning;
        Title = "AIEnter · 设置";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AIEnter.ico"));
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, true); presenter.IsMaximizable = false; presenter.IsMinimizable = false;
        SystemBackdrop = PanelTheme.Backdrop();
        var layout = new Grid { Padding = new Thickness(18), RowSpacing = 12, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(SystemColor(Windows.UI.ViewManagement.UIColorType.Background, Windows.UI.Color.FromArgb(255, 243, 247, 248))) };
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var fields = new StackPanel { Spacing = 16 };
        fields.Children.Add(Section("任务栏图标"));
        fields.Children.Add(taskbarSize); fields.Children.Add(flowSpeed); fields.Children.Add(PanelTheme.Hint("0 暂停，100 为初始流速，200 为当前流速，1800 最快；调整立即预览。")); fields.Children.Add(glow); fields.Children.Add(glowRange); fields.Children.Add(glowIntensity);
        fields.Children.Add(PanelTheme.Hint("图标紧贴圆形按钮，光晕独立绘制，不额外撑宽占位，并受任务栏可用高度限制。只有对话展开时显示光晕，设置窗口不会点亮。"));
        fields.Children.Add(Section("截图")); fields.Children.Add(screenshotDoubleClick); fields.Children.Add(screenshotSeconds); fields.Children.Add(screenshotFolder);
        var chooseScreenshotFolder = new Button { Content = "选择保存目录" };
        chooseScreenshotFolder.Click += async (_, _) =>
        {
            using var lease = KeepOpen();
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var folder = await picker.PickSingleFolderAsync(); if (folder != null) screenshotFolder.Text = folder.Path;
            }
            catch (Exception error) { status.Text = "无法选择目录：" + error.Message; }
        };
        fields.Children.Add(chooseScreenshotFolder);
        fields.Children.Add(PanelTheme.Hint("框选后立即保存 PNG；关闭或超时不删除图片。悬停时暂停倒计时。点击 AI 后关闭预览，在 AI 窗口处理附件，不发送消息。"));
        fields.Children.Add(Section("AI 与窗口"));
        fields.Children.Add(Section("全局快捷键"));
        hotkey.KeyDown += CaptureHotkey;
        hotkey.KeyUp += (_, e) => { int mod = ModifierKey(e.Key); if (mod != 0) pressedModifiers.Remove(mod); };
        hotkey.GotFocus += (_, _) => { if (!syncing) hotkey.PlaceholderText = "请按下快捷键（Esc 取消录制）"; };
        hotkey.LostFocus += (_, _) => hotkey.PlaceholderText = "点击后按下快捷键，例如 Ctrl+Alt+Space";
        var clearHotkey = new Button { Content = "清除快捷键" }; clearHotkey.Click += (_, _) => hotkey.Text = "";
        fields.Children.Add(hotkey); fields.Children.Add(clearHotkey);
        fields.Children.Add(PanelTheme.Hint("需要包含 Ctrl、Alt 或 Win。保存时会检测系统占用；冲突不会替换当前快捷键。"));
        InitializeProviderManagement(fields);
        fields.Children.Add(floatingMode); fields.Children.Add(pinned);
        fields.Children.Add(Section("窗口外观"));
        fields.Children.Add(backplateTransparency); fields.Children.Add(backplateBlur); fields.Children.Add(backplateRadius);
        fields.Children.Add(PanelTheme.Hint("透明度越高越通透；模糊度控制背景虚化。透明度为 0 时背板完全不透明，会遮住模糊效果；模糊度为 0 时关闭虚化。"));
        fields.Children.Add(buttonRadius); fields.Children.Add(buttonSize); fields.Children.Add(padding);
        fields.Children.Add(PanelTheme.Hint("按钮高度和按钮圆角同时控制顶部 AI 按钮与底部状态提示；长提示显示省略号，鼠标悬停查看全文。"));
        InitializeButtonColorSettings(fields);
        fields.Children.Add(followCorners); fields.Children.Add(radius);
        followCorners.Toggled += (_, _) => { RefreshFields(); Preview(); };
        radius.ValueChanged += (_, _) => { Preview(); RefreshDirty(); };

        foreach (var slider in new[] { backplateRadius, backplateTransparency, backplateBlur, buttonRadius, buttonSize, padding, taskbarSize, flowSpeed, screenshotSeconds, glowRange, glowIntensity })
            slider.ValueChanged += (_, _) => { Preview(); RefreshDirty(); };
        taskbarSize.ValueChanged += (_, _) => UpdateTaskbarSizeLabel();
        screenshotDoubleClick.Toggled += (_, _) => { Preview(); RefreshDirty(); };
        screenshotFolder.TextChanged += (_, _) => RefreshDirty();
        glow.Toggled += (_, _) => { RefreshFields(); Preview(); RefreshDirty(); };
        floatingMode.Toggled += (_, _) => RefreshDirty(); pinned.Toggled += (_, _) => RefreshDirty();
        provider.SelectionChanged += (_, _) => RefreshDirty(); hotkey.TextChanged += (_, _) => RefreshDirty();
        var defaults = new Button { Content = "恢复外观默认值" };
        defaults.Click += (_, _) =>
        {
            syncing = true; LoadVisualDefaults();
            followCorners.IsOn = UiDefaults.FollowGlobalCorners; radius.Value = UiDefaults.WebCornerRadius;
            resetPanelSize = true; syncing = false; RefreshFields(); Preview(); RefreshDirty();
            status.Text = "已预览默认外观；保存后恢复面板默认尺寸。";
        };
        fields.Children.Add(defaults);
        InitializeSettingsTransfer(fields);
        var pages = new Dictionary<string, StackPanel>();
        var pageNames = new[] { "AI 服务", "窗口与操作", "任务栏", "外观", "备份与恢复" };
        var pageHost = new Grid();
        foreach (var name in pageNames) { var page = new StackPanel { Spacing = 12 }; pages[name] = page; pageHost.Children.Add(new ScrollViewer { Content = page, Tag = name, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = name == "AI 服务" ? Visibility.Visible : Visibility.Collapsed }); }
        var sidebar = new StackPanel { Spacing = 5, Width = 132 };
        foreach (var name in pageNames) { var nav = new Button { Content = name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Tag = name, Padding = new Thickness(12, 9, 10, 9) }; nav.Click += (_, _) => SelectPage((string)nav.Tag, pageHost, pages, sidebar); sidebar.Children.Add(nav); }
        var contentGrid = new Grid { ColumnSpacing = 16 };
        contentGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); contentGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.Children.Add(sidebar); Grid.SetColumn(pageHost, 1); contentGrid.Children.Add(pageHost); Grid.SetRow(contentGrid, 0); layout.Children.Add(contentGrid);
        var settingsChildren = fields.Children.ToArray();
        foreach (var child in settingsChildren) fields.Children.Remove(child);
        // Existing controls retain their state while navigation only changes visibility.
        ArrangeSettingsPages(settingsChildren, pages);
        SelectPage("AI 服务", pageHost, pages, sidebar);
        var bottom = new StackPanel { Spacing = 8, Padding = new Thickness(10), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(SurfaceColor()) };
        bottom.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dirtyLabel = new TextBlock { Text = "所有更改已保存", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) };
        var save = new Button { Content = "保存设置", IsEnabled = false }; save.Click += (_, _) => Save();
        var discard = new Button { Content = "放弃更改", IsEnabled = false }; discard.Click += (_, _) => DiscardDraft();
        var close = new Button { Content = "关闭" }; close.Click += (_, _) => Hide();
        var exit = new Button { Content = "退出 AITaskbar" }; exit.Click += (_, _) => { if (IsDirty) _ = ConfirmQuit(quit); else quit(); };
        bottom.Children.Add(dirtyLabel);
        actions.Children.Add(discard); actions.Children.Add(save); actions.Children.Add(close); exit.Margin = new Thickness(10, 0, 0, 0); actions.Children.Add(exit); bottom.Children.Add(actions);
        dirtyStatus = dirtyLabel; saveButton = save; discardButton = discard;
        Grid.SetRow(bottom, 1); layout.Children.Add(bottom);
        InitializePanel(layout);
        var display = Native.Monitors().First(m => m.Primary); var work = display.Info.Work;
        int width = Math.Min(work.Right - work.Left, (int)(500 * display.Dpi / 96d));
        int height = Math.Min(work.Bottom - work.Top, (int)(760 * display.Dpi / 96d));
        AppWindow.MoveAndResize(new(work.Left + (work.Right - work.Left - width) / 2,
            work.Top + (work.Bottom - work.Top - height) / 2, width, height));
    }
    internal void Open()
    {
        if (!IsOpen)
        {
            syncing = true; resetPanelSize = false;
            var value = options.Data;
            LoadProviderOrder(value);
            floatingMode.IsOn = value.FloatingPanel; pinned.IsOn = value.Pinned;
            hotkey.Text = value.Hotkey;
            followCorners.IsOn = value.FollowGlobalCorners; radius.Value = value.WebCornerRadius;
            LoadAppearance(value.Appearance ?? new());
            status.Text = initialHotkeyWarning is not null ? "快捷键未能注册：" + initialHotkeyWarning : options.Warning ?? "调整会先预览；保存后生效。";
            syncing = false; RefreshFields(); savedSnapshot = Snapshot(); RefreshDirty();
        }
        ShowPanel();
    }
    void LoadVisualDefaults()
    {
        var d = new UiAppearance();
        LoadAppearance(ReadAppearance() with { ButtonRadius = d.ButtonRadius, ButtonSize = d.ButtonSize,
            WindowPadding = d.WindowPadding, BackplateRadius = d.BackplateRadius, ButtonColor = d.ButtonColor,
            SelectedButtonColor = d.SelectedButtonColor, BackplateTransparency = d.BackplateTransparency, BackplateBlur = d.BackplateBlur });
    }
    void LoadAppearance(UiAppearance value)
    {
        LoadButtonColors(value);
        backplateRadius.Value = value.BackplateRadius; backplateTransparency.Value = value.BackplateTransparency; backplateBlur.Value = value.BackplateBlur;
        buttonRadius.Value = value.ButtonRadius; buttonSize.Value = value.ButtonSize;
        padding.Value = value.WindowPadding; taskbarSize.Value = value.TaskbarIconSize; UpdateTaskbarSizeLabel();
        flowSpeed.Value = value.FlowSpeed; screenshotDoubleClick.IsOn = value.ScreenshotDoubleClick; screenshotSeconds.Value = value.ScreenshotSeconds; screenshotFolder.Text = value.ScreenshotFolder;
        glow.IsOn = value.GlowEnabled; glowRange.Value = value.GlowRange; glowIntensity.Value = value.GlowIntensity;
        UpdateAppearancePreview();
    }
    void UpdateTaskbarSizeLabel() { }
    UiAppearance ReadAppearance() => new((int)buttonRadius.Value, (int)buttonSize.Value, (int)padding.Value,
        (int)taskbarSize.Value, glow.IsOn, (int)glowRange.Value, (int)glowIntensity.Value, (int)backplateRadius.Value, normalButtonColor, selectedButtonColor, (int)backplateTransparency.Value, (int)backplateBlur.Value, (int)flowSpeed.Value, screenshotDoubleClick.IsOn, (int)screenshotSeconds.Value, screenshotFolder.Text.Trim());
    void Preview()
    {
        if (syncing) return;
        var appearance = ReadAppearance();
        try { appearance.Validate(); }
        catch (InvalidDataException error) { status.Text = error.Message; RefreshDirty(); return; }
        App.PreviewAppearance(appearance);
        App.Ai.PreviewCorners(followCorners.IsOn, (int)Math.Round(radius.Value));
        UpdateAppearancePreview();
        status.Text = "正在预览；保存后记住，关闭撤销未保存的外观。";
        RefreshDirty();
    }
    protected override void OnHiding()
    {
        App.PreviewAppearance(null);
        App.Ai.PreviewCorners(options.Data.FollowGlobalCorners, options.Data.WebCornerRadius);
    }
    void RefreshFields()
    {
        radius.IsEnabled = !followCorners.IsOn; radius.Visibility = followCorners.IsOn ? Visibility.Collapsed : Visibility.Visible;
        glowRange.IsEnabled = glowIntensity.IsEnabled = glow.IsOn;
        glowRange.Visibility = glowIntensity.Visibility = glow.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }
    void Save()
    {
        try
        {
            if (provider.SelectedItem is not ComboBoxItem item) throw new InvalidDataException("请选择 AI 服务。");
            var candidate = AiPolicy.WithProviderSettings(options.Data, providerDraft.AddedProviders ?? [], providerOrder.ToArray(), (string)item.Tag) with { Pinned = pinned.IsOn, FloatingPanel = floatingMode.IsOn,
                FollowGlobalCorners = followCorners.IsOn, WebCornerRadius = (int)Math.Round(radius.Value), Appearance = ReadAppearance(),
                Hotkey = hotkey.Text.Trim(),
                DockWidth = resetPanelSize ? UiDefaults.DockWidth : options.Data.DockWidth,
                DockHeight = resetPanelSize ? UiDefaults.DockHeight : options.Data.DockHeight,
                DockTopOffset = resetPanelSize ? 0 : options.Data.DockTopOffset,
                Layouts = resetLayouts ? [] : options.Data.Layouts };
            saveOptions(candidate);
            resetPanelSize = resetLayouts = false; App.PreviewAppearance(null); savedSnapshot = Snapshot(); RefreshDirty(); status.Text = "设置已保存。";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        { status.Text = "保存失败：" + error.Message; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    void CaptureHotkey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Windows.System.VirtualKey.Escape) { pressedModifiers.Clear(); hotkey.Focus(FocusState.Programmatic); return; }
        int modifier = ModifierKey(e.Key);
        if (modifier != 0) { pressedModifiers.Add(modifier); return; }
        var parts = new List<string>();
        if (pressedModifiers.Contains(1) || (GetAsyncKeyState(0x11) & 0x8000) != 0) parts.Add("Ctrl");
        if (pressedModifiers.Contains(2) || (GetAsyncKeyState(0x12) & 0x8000) != 0) parts.Add("Alt");
        if (pressedModifiers.Contains(3) || (GetAsyncKeyState(0x10) & 0x8000) != 0) parts.Add("Shift");
        if (pressedModifiers.Contains(4) || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0) parts.Add("Win");
        parts.Add(e.Key.ToString());
        if (HotkeyPolicy.TryNormalize(string.Join('+', parts), out string normalized)) hotkey.Text = normalized;
        else status.Text = "快捷键需要包含 Ctrl、Alt 或 Win，且只允许一个按键。";
    }
    static int ModifierKey(Windows.System.VirtualKey key) => key switch
    {
        Windows.System.VirtualKey.Control or Windows.System.VirtualKey.LeftControl or Windows.System.VirtualKey.RightControl => 1,
        Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftMenu or Windows.System.VirtualKey.RightMenu => 2,
        Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftShift or Windows.System.VirtualKey.RightShift => 3,
        Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows => 4,
        _ => 0
    };
}
