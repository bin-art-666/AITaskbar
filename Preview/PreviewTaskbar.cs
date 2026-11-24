using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace AITaskbar;

internal sealed class PreviewTaskbar : Window
{
    private readonly nint handle;
    private readonly DesktopAppBar bar;
    private readonly PinnedApps pins;
    private readonly DispatcherQueueTimer refresh;
    private readonly PreviewApplicationStrip tasks;
    private readonly TaskbarSystemTray tray;
    private readonly Border surface = new() { CornerRadius = new CornerRadius(0) };
    private readonly Grid strip = new() { Padding = new Thickness(10, 0, 10, 0) };
    private readonly ScaleTransform stripScale = new();
    private int naturalStripHeight;
    private readonly SystemTaskbarVisibility systemTaskbar;
    private AppCenterWindow? appCenter;
    private readonly TextBlock feedback = new() { MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private bool closed, menuOpen;
    internal PreviewModuleIcons ModuleIcons { get; }
    internal IReadOnlyList<PinnedApp> PinnedItems => pins.Items;

    public PreviewTaskbar(App app, string root, AppearanceHost appearance, int iconSize,
        Action<Exception> reportFailure)
    {
        Title = "AITaskbar 预览栏";
        systemTaskbar = new(root);
        naturalStripHeight = HeightForIcons(iconSize);
        pins = new(Path.Combine(root, "preview-pins.json"));
        // Some shell configurations do not implement switcher registration for this window.
        try { AppWindow.IsShownInSwitchers = false; }
        catch (NotImplementedException) { }
        var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        tasks = new(pins, app.IconPreferences, app.AppArtwork,
            iconSize, app.AppFeedback, app.IconLayout, app.AttentionDiagnostics, app.CurrentAppCenterAppearance.Icon, ToggleAppCenter, handle, Report);
        AIEnter.Native.RemoveWindowFrame(handle);
        const int cornerPreferenceAttribute = 33; // DWMWA_WINDOW_CORNER_PREFERENCE
        int doNotRound = 1; // DWMWCP_DONOTROUND
        AIEnter.Native.DwmSetWindowAttribute(handle, cornerPreferenceAttribute, ref doNotRound, sizeof(int));

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        var halo = Button("Halo", "Halo 中转站", () => app.HaloModule.Handle("Toggle"));
        var ai = Button("AIEnter", "AIEnter 对话", () => _ = app.AiModule.ToggleAiWindow(false));
        AIEnter.PanelToggleTargets.Register(this, ai);
        Halo.PanelToggleTargets.Register(this, halo);
        var aiService = AIEnter.App.Ai;
        SetMenu(halo, "Halo 设置", Halo.App.OpenSettings);
        SetMenu(ai, "AIEnter 设置", app.AiModule.OpenSettings);
        tasks.AppCenterButton.ContextRequested += (_, args) =>
        {
            args.Handled = true;
            ToggleAppCenter();
        };
        void ToggleAppCenter()
        {
            if (appCenter is not null) { appCenter.Close(); return; }
            try
            {
                tasks.DismissThumbnails();
                var window = new AppCenterWindow(app, AppWindow, pins, root, tasks.Refresh);
                appCenter = window;
                tasks.SetAppCenterExpanded(true);
                window.Closed += (_, _) => { appCenter = null; tasks.SetAppCenterExpanded(false); };
            }
            catch (Exception error) { reportFailure(error); Report("无法打开应用中心。"); }
        }
        ConfigureHaloDrop(app.HaloModule, halo, Report);
        AcceptDrop(ai, AIEnter.DropPayload.Supported, async view =>
        {
            await AIEnter.App.Ai.OpenForDrop(AIEnter.Native.Monitors().First(monitor => monitor.Primary), true);
            return await AIEnter.App.Ai.ReceiveDrop(view);
        }, Report);
        var icons = ModuleIcons = new PreviewModuleIcons(halo, ai, DispatcherQueue);
        icons.SetIconLayout(app.IconLayout);
        left.Children.Add(halo);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(feedback);
        tray = new(DispatcherQueue, handle, root, app.ClockStyle, app.QuickAppearance,
            app.ShowSettings, app.ShowAppearanceSettings, app.ShowQuickControlSettings, app.TrySetQuickAppearance,
            tasks.DismissThumbnails, Report);
        right.Children.Add(tray);
        right.Children.Add(icons.AiEntrance);

        strip.Children.Add(tasks.HoverSurface);
        strip.Children.Add(tasks.AttentionPulseSurface);
        strip.Children.Add(tasks.Surface);
        strip.Children.Add(tasks.AttentionBadgeSurface);
        strip.Children.Add(tasks.AppDragSurface);
        strip.Children.Add(left);
        strip.Children.Add(right);
        void FitApps(object sender, SizeChangedEventArgs args) =>
            tasks.SetAvailableWidth(strip.ActualWidth - 36 - 2 * Math.Max(left.ActualWidth, right.ActualWidth));
        strip.SizeChanged += FitApps;
        left.SizeChanged += FitApps;
        right.SizeChanged += FitApps;
        strip.RenderTransform = stripScale;
        var stripHost = new Canvas();
        stripHost.Children.Add(strip);
        var showDesktop = new ShowDesktopButton(handle, tasks.ForegroundAtPress, tasks.DismissThumbnails, Report, reportFailure);
        var barContent = new Grid();
        barContent.Children.Add(stripHost);
        barContent.Children.Add(showDesktop);
        surface.SizeChanged += (_, _) => UpdateStripLayout();
        var taskbarMenu = NewBarMenu();
        AddMenuAction(taskbarMenu, "固定应用", () => _ = PickPinAsync(), Symbol.Pin);
        AddMenuAction(taskbarMenu, "AITaskbar 设置", app.ShowSettings, Symbol.Setting);
        var visibilityIcon = new SymbolIcon();
        var visibility = new MenuFlyoutItem { Icon = visibilityIcon };
        visibility.Click += async (_, _) =>
        {
            if (systemTaskbar.Visible is bool visible)
                await SetSystemTaskbarVisibilityAsync(!visible, app.SaveSystemTaskbarVisibility);
        };
        var coverIcon = new SymbolIcon();
        var coverWindows = new MenuFlyoutItem { Text = "覆盖窗口", Icon = coverIcon };
        coverWindows.Click += (_, _) =>
        {
            try
            {
                app.SaveTaskbarCoverWindows(!app.TaskbarCoversWindows);
                bar.SetCoverWindows(app.TaskbarCoversWindows);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                reportFailure(error);
                Report("无法保存覆盖窗口设置。");
            }
        };
        taskbarMenu.Opening += (_, _) =>
        {
            bool systemTaskbarHidden = systemTaskbar.Visible == false;
            visibility.Text = systemTaskbarHidden ? "显示系统任务栏" : "隐藏系统任务栏";
            visibilityIcon.Symbol = systemTaskbarHidden ? Symbol.DockBottom : Symbol.Remove;
            visibility.IsEnabled = systemTaskbar.CanToggle;
            coverIcon.Symbol = app.TaskbarCoversWindows ? Symbol.Accept : Symbol.SwitchApps;
            AutomationProperties.SetItemStatus(coverWindows, app.TaskbarCoversWindows ? "已开启" : "已关闭");
        };
        taskbarMenu.Items.Add(visibility);
        taskbarMenu.Items.Add(coverWindows);
        taskbarMenu.Items.Add(new MenuFlyoutSeparator());
        AddMenuAction(taskbarMenu, "退出 AITaskbar", app.RequestExit, Symbol.Cancel);
        surface.ContextRequested += (_, args) =>
        {
            if (args.Handled || !args.TryGetPosition(surface, out var point)) return;
            for (var element = args.OriginalSource as DependencyObject; element is not null && element != surface;
                element = VisualTreeHelper.GetParent(element))
            {
                if (element is ButtonBase && element != showDesktop) return;
            }
            args.Handled = true;
            taskbarMenu.ShowAt(surface, point);
        };
        var panelColors = new Windows.UI.ViewManagement.UISettings();
        var panelAccessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
        var visual = new PreviewBarAppearance(this, surface, barContent, appearance, light =>
        {
            icons.RefreshSystem(light);
            showDesktop.ApplyTone(light, panelColors, panelAccessibility.HighContrast);
            tasks.SetFeedbackLight(light);
            tray.ApplyTone(light, app.BarAppearance);
            appCenter?.ApplyPalette();
            var palette = TaskbarPanelFlyout.ResolvePalette(new QuickControlAppearance(Backdrop: QuickBackdrop.FollowTaskbar),
                light, app.BarAppearance, panelColors, panelAccessibility.HighContrast);
            tasks.SetThumbnailTaskbarPalette(palette.Theme, palette.Solid);
            aiService.SetTaskbarPalette(palette.Theme, palette.Solid);
            app.HaloModule.SetTaskbarPalette(palette.Theme, palette.Solid);
        });

        refresh = DispatcherQueue.CreateTimer();
        refresh.Interval = TimeSpan.FromMilliseconds(1100);
        refresh.Tick += (_, _) =>
        {
            systemTaskbar.KeepHidden();
            visual.Refresh();
            icons.RefreshSystem(visual.IsLight);
            if (!menuOpen) tasks.Refresh();
        };
        Closed += (_, _) =>
        {
            closed = true;
            aiService.DetachTaskbar(ai);
            app.HaloModule.DetachTaskbar(halo);
            appCenter?.Close();
            tray.Dispose();
            systemTaskbar.Dispose();
            refresh.Stop();
            tasks.Close();
            icons.Dispose();
            visual.Dispose();
            bar?.Dispose();
        };
        try
        {
            Content = surface;
            visual.Refresh();
            bar = new(handle, DispatcherQueue, app.TaskbarHeight, app.TaskbarCoversWindows,
                tasks.RememberForeground, tasks.UpdateAppAttention, error => { Close(); reportFailure(error); });
            aiService.AttachTaskbar(ai);
            app.HaloModule.AttachTaskbar(halo);
            tasks.Refresh();
            refresh.Start();
        }
        catch
        {
            Close();
            throw;
        }

    }

    internal void ShowDocked(bool activate)
    {
        if (activate) Activate();
        else AppWindow.Show(activateWindow: false);
        // WinUI can initially move the window back into the now smaller work area.
        bar.Reposition();
        tasks.EnableTitleBarMinimize();
    }

    internal void SetIconSize(int size)
    {
        tasks.SetIconSize(size);
        appCenter?.RefreshStyle();
        naturalStripHeight = HeightForIcons(size);
        UpdateStripLayout();
    }

    internal void SetHeight(int height) => bar.SetHeight(height);
    internal void SetClockStyle(TaskbarClockStyle style) => tray.SetClockStyle(style);
    internal void SetQuickAppearance(QuickControlAppearance appearance) => tray.SetQuickAppearance(appearance);

    internal async Task SetSystemTaskbarVisibilityAsync(bool visible, Action<bool>? save = null)
    {
        try
        {
            if (!await systemTaskbar.SetVisibleAsync(visible) || closed) return;
            bar.Reposition();
            save?.Invoke(visible);
            // Explorer applies the taskbar state and updates the work area asynchronously.
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (!closed) bar.RefreshReservation();
        }
        catch (Exception error)
        {
            if (!closed) Report(error.Message);
        }
        finally
        {
            if (!closed) tray.EnableNotificationReceiver();
        }
    }

    private static int HeightForIcons(int iconSize) => Math.Max(48, iconSize + TaskbarAppFeedback.ExtraHeight + 1);

    private void UpdateStripLayout()
    {
        double width = surface.ActualWidth, height = surface.ActualHeight;
        if (width <= 0 || height <= 0) return;
        double scale = Math.Min(1, height / naturalStripHeight);
        strip.Padding = new Thickness(10, 0, (ShowDesktopButton.EdgeWidth + 2) / scale, 0);
        strip.Width = width / scale;
        strip.Height = Math.Max(height, naturalStripHeight);
        stripScale.ScaleX = stripScale.ScaleY = scale;
    }
    internal void RefreshIconStyle(string executable)
    {
        tasks.RefreshIconStyle(executable);
        appCenter?.RefreshStyle();
    }
    internal void SetAppCenterIcon(AppCenterIconStyle style)
    {
        tasks.SetAppCenterIcon(style);
        if (!style.ShowInTaskbar) appCenter?.Close();
        else appCenter?.RefreshStyle();
    }
    internal void SetAppFeedback(TaskbarAppFeedbackStyle style) => tasks.SetAppFeedback(style);
    internal void SetIconLayout(PreviewIconLayout layout)
    {
        tasks.SetIconLayout(layout);
        appCenter?.RefreshStyle();
        ModuleIcons.SetIconLayout(layout);
    }
    internal void SetThumbnailWidth(int width) => tasks.SetThumbnailWidth(width);
    internal void SetThumbnailAnimation(int durationMs) => tasks.SetThumbnailAnimation(durationMs);

    private async Task PickPinAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !closed) tasks.ChangePins(() => pins.Add(file.Path));
        }
        catch (Exception error) { if (!closed) Report(error.Message); }
    }

    private static Button Button(string label, string name, Action click)
    {
        var button = new Button { Content = label, Height = 34, Padding = new Thickness(10, 0, 10, 0) };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private void SetMenu(Button button, string label, Action click)
    {
        var menu = NewBarMenu();
        AddMenuAction(menu, label, click);
        button.ContextFlyout = menu;
    }

    private MenuFlyout NewBarMenu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menuOpen = true;
            tasks.DismissThumbnails();
        };
        menu.Closed += (_, _) => menuOpen = false;
        return menu;
    }

    private static void AddMenuAction(MenuFlyout menu, string label, Action click, Symbol? icon = null)
    {
        var action = new MenuFlyoutItem { Text = label };
        if (icon is { } symbol) action.Icon = new SymbolIcon(symbol);
        action.Click += (_, _) => click();
        menu.Items.Add(action);
    }

    internal static void ConfigureHaloDrop(Halo.App halo, Button button, Action<string> report) =>
        AcceptDrop(button, Halo.StationWindow.Supports, async view =>
        {
            halo.Handle("Open");
            return Halo.App.Panel is { } panel && await panel.Receive(view);
        }, report);

    private static void AcceptDrop(Button button, Func<DataPackageView, bool> supports,
        Func<DataPackageView, Task<bool>> deliver, Action<string> report)
    {
        button.AllowDrop = true;
        button.DragOver += (_, args) =>
        {
            if (!supports(args.DataView)) return;
            args.AcceptedOperation = DataPackageOperation.Copy;
            args.Handled = true;
        };
        button.Drop += async (_, args) =>
        {
            if (!supports(args.DataView)) return;
            var deferral = args.GetDeferral();
            args.Handled = true;
            args.AcceptedOperation = DataPackageOperation.Copy;
            try { report(await deliver(args.DataView) ? "" : "本次拖入未完成，请重试。"); }
            catch (Exception error) { report(error.Message); }
            finally { deferral.Complete(); }
        };
    }

    private void Report(string message)
    {
        if (closed) return;
        feedback.Text = message;
        ToolTipService.SetToolTip(feedback, message);
    }

}
