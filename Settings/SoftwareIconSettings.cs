using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AITaskbar;

internal sealed partial class TaskbarSettingsWindow
{
    private void BuildIconSettings(StackPanel page, IconSettingsPreview preview)
    {
        preview.ApplyLayout(app.IconSize, app.IconLayout);
        preview.ApplyFeedback(app.AppFeedback);

        var size = FeedbackSlider("应用图标大小", PreviewIconSize.Minimum, PreviewIconSize.Maximum);
        size.StepFrequency = 2;
        var sizeValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var gap = FeedbackSlider("图标间距", PreviewIconLayout.MinGap, PreviewIconLayout.MaxGap);
        var gapValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var colors = new ComboBox { MinWidth = 118 };
        colors.Items.Add("亮色");
        colors.Items.Add("暗色");
        AutomationProperties.SetName(colors, "图标配色");
        var menuColors = new ComboBox { MinWidth = 118 };
        AutomationProperties.SetName(menuColors, "右键菜单配色");
        (AppMenuColorMode Value, string Label)[] menuModes =
        [
            (AppMenuColorMode.FollowTaskbar, "跟随任务栏"),
            (AppMenuColorMode.Light, "亮色"),
            (AppMenuColorMode.Dark, "暗色")
        ];
        foreach (var mode in menuModes) menuColors.Items.Add(mode.Label);
        var animation = new ComboBox { MinWidth = 118 };
        AutomationProperties.SetName(animation, "滚动动画");
        (IconScrollAnimation Value, string Label)[] modes =
        [
            (IconScrollAnimation.Off, "关闭"),
            (IconScrollAnimation.Fast, "快速"),
            (IconScrollAnimation.Standard, "标准"),
            (IconScrollAnimation.Relaxed, "舒缓")
        ];
        foreach (var mode in modes) animation.Items.Add(mode.Label);
        var windowAnimation = new ComboBox { MinWidth = 118 };
        AutomationProperties.SetName(windowAnimation, "窗口开合动画");
        (WindowAnimationStyle Value, string Label)[] windowModes =
        [
            (WindowAnimationStyle.Curved, "曲面动画"),
            (WindowAnimationStyle.WindowsDefault, "Windows 默认动画"),
            (WindowAnimationStyle.WindowsStyle, "Windows 风格（跟随预览栏）")
        ];
        foreach (var mode in windowModes) windowAnimation.Items.Add(mode.Label);
        var windowSpeed = FeedbackSlider("窗口开合速度", PreviewIconLayout.MinWindowDurationMs, PreviewIconLayout.MaxWindowDurationMs);
        windowSpeed.LargeChange = 50;
        var windowSpeedValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 72 };
        var recentFiles = new ToggleSwitch();
        AutomationProperties.SetName(recentFiles, "显示最近文件");
        var edgeRecentFiles = new ToggleSwitch();
        AutomationProperties.SetName(edgeRecentFiles, "显示 Edge 最近文件");
        var edgeRecentlyClosed = new ToggleSwitch();
        AutomationProperties.SetName(edgeRecentlyClosed, "显示 Edge 最近关闭");
        var chromeRecentlyClosed = new ToggleSwitch();
        AutomationProperties.SetName(chromeRecentlyClosed, "显示 Chrome 最近关闭");

        bool syncing = false;
        void Sync()
        {
            syncing = true;
            size.Value = app.IconSize;
            sizeValue.Text = $"{app.IconSize} DIP";
            gap.Value = app.IconLayout.Gap;
            gapValue.Text = $"{app.IconLayout.Gap} DIP";
            colors.SelectedIndex = app.IconLayout.ColorMode == IconColorMode.Dark ? 1 : 0;
            menuColors.SelectedIndex = Array.FindIndex(menuModes, mode => mode.Value == app.IconLayout.MenuColorMode);
            animation.SelectedIndex = Array.FindIndex(modes, mode => mode.Value == app.IconLayout.Animation);
            windowAnimation.SelectedIndex = Array.FindIndex(windowModes, mode => mode.Value == app.IconLayout.WindowAnimation);
            windowSpeed.IsEnabled = app.IconLayout.WindowAnimation == WindowAnimationStyle.Curved;
            windowSpeed.Value = app.IconLayout.EffectiveWindowDurationMs;
            windowSpeedValue.Text = $"{app.IconLayout.EffectiveWindowDurationMs} 毫秒";
            recentFiles.IsOn = app.IconLayout.ShowRecentFiles;
            edgeRecentFiles.IsOn = app.IconLayout.ShowEdgeRecentFiles;
            edgeRecentlyClosed.IsOn = app.IconLayout.ShowEdgeRecentlyClosed;
            chromeRecentlyClosed.IsOn = app.IconLayout.ShowChromeRecentlyClosed;
            syncing = false;
            preview.ApplyLayout(app.IconSize, app.IconLayout);
        }

        size.ValueChanged += (_, args) =>
        {
            if (syncing) return;
            app.TrySetIconSize((int)Math.Round(args.NewValue));
            Sync();
        };
        gap.ValueChanged += (_, args) =>
        {
            if (syncing) return;
            app.TrySetIconLayout(app.IconLayout with { Gap = (int)Math.Round(args.NewValue) });
            Sync();
        };
        colors.SelectionChanged += (_, _) =>
        {
            if (syncing || colors.SelectedIndex < 0) return;
            var mode = colors.SelectedIndex == 1 ? IconColorMode.Dark : IconColorMode.Light;
            if (mode != app.IconLayout.ColorMode)
            {
                app.TrySetIconLayout(app.IconLayout with { ColorMode = mode });
                Sync();
            }
        };
        menuColors.SelectionChanged += (_, _) =>
        {
            if (syncing || menuColors.SelectedIndex < 0) return;
            var mode = menuModes[menuColors.SelectedIndex].Value;
            if (mode == app.IconLayout.MenuColorMode) return;
            app.TrySetIconLayout(app.IconLayout with { MenuColorMode = mode });
            Sync();
        };
        animation.SelectionChanged += (_, _) =>
        {
            if (syncing || animation.SelectedIndex < 0) return;
            app.TrySetIconLayout(app.IconLayout with { Animation = modes[animation.SelectedIndex].Value });
            Sync();
        };
        windowAnimation.SelectionChanged += (_, _) =>
        {
            if (syncing || windowAnimation.SelectedIndex < 0) return;
            app.TrySetIconLayout(app.IconLayout with { WindowAnimation = windowModes[windowAnimation.SelectedIndex].Value });
            Sync();
        };
        windowSpeed.ValueChanged += (_, args) =>
        {
            if (syncing || !windowSpeed.IsEnabled) return;
            app.TrySetIconLayout(app.IconLayout with { WindowDurationMs = (int)Math.Round(args.NewValue) });
            Sync();
        };
        recentFiles.Toggled += (_, _) =>
        {
            if (syncing || recentFiles.IsOn == app.IconLayout.ShowRecentFiles) return;
            app.TrySetIconLayout(app.IconLayout with { ShowRecentFiles = recentFiles.IsOn });
            Sync();
        };
        edgeRecentFiles.Toggled += (_, _) =>
        {
            if (syncing || edgeRecentFiles.IsOn == app.IconLayout.ShowEdgeRecentFiles) return;
            app.TrySetIconLayout(app.IconLayout with { ShowEdgeRecentFiles = edgeRecentFiles.IsOn });
            Sync();
        };
        edgeRecentlyClosed.Toggled += (_, _) =>
        {
            if (syncing || edgeRecentlyClosed.IsOn == app.IconLayout.ShowEdgeRecentlyClosed) return;
            app.TrySetIconLayout(app.IconLayout with { ShowEdgeRecentlyClosed = edgeRecentlyClosed.IsOn });
            Sync();
        };
        chromeRecentlyClosed.Toggled += (_, _) =>
        {
            if (syncing || chromeRecentlyClosed.IsOn == app.IconLayout.ShowChromeRecentlyClosed) return;
            app.TrySetIconLayout(app.IconLayout with { ShowChromeRecentlyClosed = chromeRecentlyClosed.IsOn });
            Sync();
        };

        page.Children.Add(SettingRow("图标配色", "同时应用于任务栏背板和右键菜单图标。", colors));
        page.Children.Add(SettingRow("右键菜单配色", "调整应用右键菜单和子菜单的背景与文字。", menuColors));
        page.Children.Add(IconSettingsHeading("图标布局"));
        page.Children.Add(SettingRow("应用图标大小", "统一调整中间应用区的图标。", Actions(size, sizeValue)));
        page.Children.Add(SettingRow("图标间距", "分别调整固定区和运行区内相邻图标的间隔。", Actions(gap, gapValue)));
        page.Children.Add(SettingRow("滚动动画", "运行区图标溢出时生效。关闭后仍可用滚轮切换。", animation));
        page.Children.Add(SettingRow("窗口开合动画", "适用于预览栏和窗口标题栏的开合操作。", windowAnimation));
        page.Children.Add(SettingRow("窗口开合速度", "曲面动画可调节为 200–1000 毫秒。Windows 风格使用 200 毫秒。", Actions(windowSpeed, windowSpeedValue)));
        page.Children.Add(SettingRow("显示最近文件", "在 Chrome 右键菜单中显示 Windows 提供的最近文件。", recentFiles));
        page.Children.Add(SettingRow("显示 Edge 最近文件", "在 Edge 右键菜单中显示 Windows 提供的最近文件。", edgeRecentFiles));
        BuildBrowserRecentConnection(page, RecentBrowser.Edge, edgeRecentlyClosed);
        BuildBrowserRecentConnection(page, RecentBrowser.Chrome, chromeRecentlyClosed);
        BuildAppFeedbackSettings(page, preview.ApplyFeedback);
        page.Children.Add(IconSettingsHeading("单个应用的图标"));
        BuildSoftwareIconSettings(page);
        Sync();
    }

    private static TextBlock IconSettingsHeading(string text) =>
        new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };

    private void BuildSoftwareIconSettings(StackPanel page)
    {
        var entries = new StackPanel { Spacing = 4 };
        page.Children.Add(new TextBlock
        {
            Text = "为正在运行、已固定或曾设为原始图标的应用选择样式。更改会即时保存。",
            TextWrapping = TextWrapping.Wrap, Opacity = .72
        });
        page.Children.Add(ActionButton("刷新应用列表", RefreshApps));
        page.Children.Add(entries);
        page.Loaded += (_, _) => RefreshApps();

        void RefreshApps()
        {
            IReadOnlyList<PinnedApp> pinned;
            try { pinned = app.PreviewPins; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                pinned = [];
                SetStatus("无法读取已固定的应用。");
            }

            var apps = AppGroups.Build(pinned, RunningWindows.List())
                .Where(group => group.IconExecutable is not null)
                .Select(group => new PinnedApp(group.Name, group.IconExecutable!))
                .Concat(app.IconPreferences.OriginalExecutables.Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => new PinnedApp(Path.GetFileNameWithoutExtension(path), path)))
                .Where(item => Path.IsPathFullyQualified(item.Executable) && !RunningWindows.IsFrameHost(item.Executable))
                .DistinctBy(item => item.Executable, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);

            entries.Children.Clear();
            foreach (var item in apps) entries.Children.Add(SoftwareIconRow(item));
            if (entries.Children.Count == 0)
                entries.Children.Add(new TextBlock { Text = "打开一个应用后刷新列表。", TextWrapping = TextWrapping.Wrap });
        }
    }

    private FrameworkElement SoftwareIconRow(PinnedApp appEntry)
    {
        var choice = new ComboBox { MinWidth = 124 };
        choice.Items.Add("使用圆角图标");
        choice.Items.Add("使用原始图标");
        choice.SelectedIndex = app.IconPreferences.UsesOriginal(appEntry.Executable) ? 1 : 0;
        AutomationProperties.SetName(choice, $"{appEntry.Name} 图标样式");
        var row = SettingRow(appEntry.Name, "", choice);
        ToolTipService.SetToolTip(row, appEntry.Executable);

        choice.SelectionChanged += (_, _) =>
        {
            bool original = app.IconPreferences.UsesOriginal(appEntry.Executable);
            if (choice.SelectedIndex < 0 || original == (choice.SelectedIndex == 1)) return;
            if (!app.TrySetOriginalIcon(appEntry.Executable, !original)) choice.SelectedIndex = original ? 1 : 0;
        };
        return row;
    }
}
