using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace AIEnter;

internal sealed partial class AiWindow : PanelWindow
{
    readonly AiOptionsStore options;
    readonly string directory;
    readonly Action changed;
    readonly bool verification;
    readonly Grid root = PanelTheme.Layout();
    readonly StackPanel headerActions = PanelTheme.Actions();
    readonly Border backplate = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
    readonly DesktopGlassBackdrop desktopGlass = new(transparentWindow: true);
    readonly Grid browsers = new();
    Border pageFrame = null!;
    readonly TextBlock status = new() { FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
    readonly List<Choice> providerChoices = [];
    readonly Button expand = Icon("\uE740", "展开为独立窗口");
    readonly ToggleButton pin = new();
    readonly Button back = Icon("\uE72B", "返回 AI 对话");
    readonly Button retry = new() { Content = "重新加载", Visibility = Visibility.Collapsed };
    readonly Dictionary<string, Page> pages = new();
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer saveTimer;
    DesktopMonitor display;
    int dockTop;
    bool destroyed, changingMode;
    string fixtureNavigation = "";
    Page? current;
    Popup? popup;
    AiBounds? floating;
    bool switcherVisibilityApplied;
    internal bool Detached { get; private set; }
    readonly RunningWindowIcon? runningWindowIcon;
    internal bool Initializing => current?.Ready is { IsCompleted: false };
    internal string Status => status.Text;
    internal string DisplayDevice => display.Device;
    protected override bool DismissOnOutside => !Detached && pin.IsChecked != true;
    protected override bool FloatFadeMotion => true;
    protected override Action<float, float> BackdropMotion => desktopGlass.SetMotion;
    protected override bool Frameless => !Detached;
    sealed class Page(AiEndpoint endpoint)
    {
        internal AiEndpoint Endpoint = endpoint;
        internal WebView2 View = new() { DefaultBackgroundColor = Colors.Transparent };
        internal CoreWebView2Environment? Environment;
        internal Task? Ready;
        internal bool Failed;
        internal bool Retry;
        internal string Status = endpoint.Name + " · 正在载入网页…";
    }
    sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
    sealed record Popup(Page Owner, WebView2 View);
    internal AiWindow(AiOptionsStore options, string directory, DesktopMonitor display, Action changed, bool verification = false,
        FrameworkElement? taskbarAnchor = null)
    {
        this.options = options; this.directory = directory; this.display = display; this.changed = changed;
        this.verification = verification;
        Title = "AIEnter · AI 助手";
        if (taskbarAnchor is null)
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AIEnter.ico"));
            runningWindowIcon = new RunningWindowIcon(this);
            runningWindowIcon.Apply();
            AppWindow.Changed += (_, _) => { if (!destroyed) runningWindowIcon.Apply(); };
            Closed += (_, _) => runningWindowIcon.Dispose();
            var presenter = (OverlappedPresenter)AppWindow.Presenter;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false; presenter.IsAlwaysOnTop = true;
            UpdateSwitcherVisibility();
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int corner = 1, border = unchecked((int)0xFFFFFFFE);
            Native.DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int)); Native.DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            SystemBackdrop = desktopGlass;
        }
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var controls = headerActions;
        Style(pin, "固定 AI 面板"); AiControls.SetIcon(pin, "\uE718");
        var close = Icon("\uE8BB", "收起 AI 面板");
        expand.Click += (_, _) => ToggleDetached(); close.Click += (_, _) => Hide();
        pin.Click += (_, _) => { try { options.Save(options.Data with { Pinned = pin.IsChecked == true }); } catch { ApplyPin(); SetStatus("固定状态保存失败，请重试。"); } };
        controls.Children.Add(expand); controls.Children.Add(pin); controls.Children.Add(close);
        aiNavigation.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        aiNavigation.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        back.Visibility = Visibility.Collapsed; back.Click += (_, _) => ClosePopup(); aiNavigation.Children.Add(back);
        InitializeProviderStrip(); Grid.SetColumn(providerHost, 1); aiNavigation.Children.Add(providerHost);
        aiHeader = PanelTheme.Header(aiNavigation, controls); root.Children.Add(aiHeader);
        if (taskbarAnchor is null) InitializeFloatingCaption(aiHeader, controls, () => Detached, aiNavigation);
        InitializeAiHeaderLayout();
        var browserHost = new Grid(); browserHost.Children.Add(browsers); InitializeDropInbox(browserHost);
        var content = new Border { CornerRadius = PanelTheme.Value<CornerRadius>("CardRadius"), Child = dropContent, Background = new SolidColorBrush(Colors.Transparent) };
        pageFrame = content;
        PanelTheme.Track(content, static border => border.CornerRadius = PanelTheme.Value<CornerRadius>("CardRadius"));
        InitializeViewportMask();
        Grid.SetRow(content, 1); root.Children.Add(content);
        var statusRow = new Grid { ColumnSpacing = 8 };
        statusRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        statusRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var statusProgress = new ProgressRing { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };
        statusRow.Children.Add(statusProgress); Grid.SetColumn(status, 1); statusRow.Children.Add(status);
        Grid.SetColumn(retry, 2); statusRow.Children.Add(retry);
        var footer = PanelTheme.Track(new Border { Child = statusRow }, static border =>
        {
            border.Height = PanelTheme.Value<double>("ActionSize");
            border.CornerRadius = PanelTheme.Value<CornerRadius>("ControlRadius");
        });
        PanelTheme.Track(retry, static button =>
        {
            button.Height = PanelTheme.Value<double>("ActionSize"); button.MinHeight = 0;
            button.Padding = new Thickness(8, 0, 8, 0); button.CornerRadius = PanelTheme.Value<CornerRadius>("ControlRadius");
        });
        this.statusProgress = statusProgress;
        status.Visibility = Visibility.Collapsed;
        transientStatusTimer = DispatcherQueue.CreateTimer(); transientStatusTimer.Interval = TimeSpan.FromSeconds(6);
        transientStatusTimer.IsRepeating = false;
        transientStatusTimer.Tick += (_, _) => { if (retry.Visibility != Visibility.Visible) footer.Visibility = Visibility.Collapsed; };
        footer.Visibility = Visibility.Collapsed;
        footer.HorizontalAlignment = HorizontalAlignment.Stretch; footer.VerticalAlignment = VerticalAlignment.Bottom;
        footer.Margin = new Thickness(8); footer.Padding = new Thickness(10, 0, 0, 0);
        footer.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 245, 248, 250));
        footer.RequestedTheme = ElementTheme.Light;
        statusHost = footer;
        retry.Click += async (_, _) => { if (current is null) return; if (current.View.CoreWebView2 != null && !current.Failed) current.View.Reload(); else { Remove(current); current = null; await ShowSelected(); } };
        Grid.SetRow(footer, 1); Canvas.SetZIndex(footer, 5); root.Children.Add(footer);
        saveTimer = DispatcherQueue.CreateTimer(); saveTimer.Interval = TimeSpan.FromMilliseconds(400); saveTimer.IsRepeating = false;
        saveTimer.Tick += (_, _) => SaveFloating();
        if (taskbarAnchor is null)
            AppWindow.Changed += (_, e) => { if (Detached && !destroyed && (e.DidPositionChange || e.DidSizeChange)) { saveTimer.Stop(); saveTimer.Start(); } };
        Closed += (_, _) => { destroyed = true; EndDockResize(true); EndExpandedMove(true); saveTimer.Stop(); transientStatusTimer.Stop(); ClosePopup(); foreach (var page in pages.Values) page.View.Close(); pages.Clear(); };
        var shell = new Grid(); shell.Children.Add(backplate); shell.Children.Add(root);
        var panel = new Border { Background = new SolidColorBrush(Colors.Transparent), Child = shell,
            CornerRadius = new CornerRadius(App.Appearance.BackplateRadius) };
        if (taskbarAnchor is not null)
        {
            TaskbarFlyout = new(taskbarAnchor, panel, backplate, movable: true);
            InitializeExpandedHeaderMove();
            AutomationProperties.SetName(expand, "放大 AI 面板");
            ToolTipService.SetToolTip(expand, "放大 AI 面板");
        }
        InitializeDockResize(shell);
        InitializePanel(panel);
        if (taskbarAnchor is not null) InitializeFlyoutFileDrop(taskbarAnchor);
        if (verification && Environment.GetCommandLineArgs().Contains("--compare-no-glass")) backplate.Visibility = Visibility.Collapsed;
        InitializeBackplateCorners(); InitializeDropTarget(); ApplyOptions();
    }
    static FontIcon Glyph(string value) => PanelTheme.Glyph(value);
    static void Style(ButtonBase button, string name) => PanelTheme.FramedIconStyle(button, name);
    static Button Icon(string value, string name) => AiControls.IconButton(value,name);
    Microsoft.UI.Dispatching.DispatcherQueueTimer transientStatusTimer = null!;
    Border statusHost = null!;
    ProgressRing statusProgress = null!;
    internal void ReportDropError(string text) => SetStatus(text);
    void SetStatus(string text)
    {
        if (destroyed) return;
        status.Text = text;
        ToolTipService.SetToolTip(status, text);
        if (statusHost != null)
        {
            statusProgress.IsActive = false; statusProgress.Visibility = Visibility.Collapsed;
            status.Visibility = Visibility.Visible; statusHost.Visibility = Visibility.Visible;
            transientStatusTimer.Stop(); transientStatusTimer.Start();
        }
        changed();
    }
    void PageStatus(Page page, string text, bool retry = false)
    {
        page.Status = text; page.Retry = retry;
        if (!destroyed && current == page)
        {
            this.retry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            status.Text = text;
            ToolTipService.SetToolTip(status, text);
            if (retry) { SetStatus(text); statusProgress.IsActive = false; statusProgress.Visibility = Visibility.Collapsed; }
            else if (text.Contains("载入", StringComparison.Ordinal))
            {
                transientStatusTimer.Stop(); statusProgress.Visibility = Visibility.Visible; statusProgress.IsActive = true;
                status.Visibility = Visibility.Visible; statusHost.Visibility = Visibility.Visible;
            }
            else if (statusHost != null)
            {
                transientStatusTimer.Stop(); statusProgress.IsActive = false; statusProgress.Visibility = Visibility.Collapsed;
                statusHost.Visibility = Visibility.Collapsed;
            }
        }
    }
    static void MemoryTarget(WebView2 view, bool low)
    {
        try { if (view.CoreWebView2 is { } core) core.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal; }
        catch { /* A failed browser is recovered through the explicit reload action. */ }
    }
    internal void ApplyOptions()
    {
        if (destroyed) return;
        if (!changingMode && Detached != !options.Data.FloatingPanel) ApplyWindowMode(!options.Data.FloatingPanel);
        expand.IsEnabled = !options.ReadOnly;
        cornerPreview=null;RefreshViewportMasks();
        if (!dockResizing)
        {
            if (dockWidth != options.Data.DockWidth || dockHeight != options.Data.DockHeight) dockPlacement = null;
            dockWidth = options.Data.DockWidth; dockHeight = options.Data.DockHeight;
        }
        UpdateResizeGrips();
        if (IsOpen && (TaskbarFlyout is not null || !Detached) && !dockResizing) Position();
        {
            providerChoices.Clear();
            foreach (string id in AiPolicy.OrderedProviders(options.Data))
            {
                try { providerChoices.Add(new Choice(id, AiPolicy.Resolve(options.Data with { Provider = id }).Name)); } catch (InvalidDataException) { }
            }
            RenderProviderStrip(); RefreshProviderEnabled(); pin.IsEnabled = !options.ReadOnly; ApplyPin();
        }
        if (IsOpen) _ = ShowSelected();
    }
    void ApplyPin()
    {
        pin.IsChecked = options.Data.Pinned; AiControls.SetIcon(pin,options.Data.Pinned ? "\uE77A" : "\uE718");
        ToolTipService.SetToolTip(pin, options.Data.Pinned ? "取消固定" : "固定 AI 面板");
        if (TaskbarFlyout is null) ((OverlappedPresenter)AppWindow.Presenter).IsAlwaysOnTop = !Detached || options.Data.Pinned;
        else ApplyFlyoutLayer();
    }
    internal async Task ToggleFromTaskbar(DesktopMonitor monitor, uint pressTime = 0, Native.Point pressPoint = default, FirstOpenTiming? timing = null)
    {
        if (ModalActive || DragActive) return;
        bool minimized = TaskbarFlyout is null && ((OverlappedPresenter)AppWindow.Presenter).State == OverlappedPresenterState.Minimized;
        if (IsOpen && !minimized) { Hide(); return; }
        if (!minimized && pressTime != 0 && DismissedByPress(pressTime, pressPoint)) return;
        if (minimized) ((OverlappedPresenter)AppWindow.Presenter).Restore();
        await Open(monitor, monitor.Info.Work.Top, timing: timing);
    }
    internal async Task Open(DesktopMonitor monitor, int top, bool activate = true, FirstOpenTiming? timing = null)
    {
        display = monitor; dockTop = top;
        dockPlacement = null; // Each entry opens at the work area bottom-right; old top offsets are ignored.
        if (TaskbarFlyout is not null || !Detached) Position(); else ClampFloating();
        ShowPanel(activate);
        timing?.Mark("windowShownMs");
        RefreshBackplateCorners(); await ShowSelected(timing);
    }
    async Task ShowSelected(FirstOpenTiming? timing = null)
    {
        if (destroyed) return;
        var endpoint = AiPolicy.Resolve(options.Data);
        if (current?.Endpoint.Key == endpoint.Key && current.Ready != null)
        { current.Endpoint = endpoint; MemoryTarget(current.View, false); await current.Ready; return; }
        ClosePopup();
        if (current != null) { current.View.Visibility = Visibility.Collapsed; MemoryTarget(current.View, true); }
        if (!pages.TryGetValue(endpoint.Key, out var page))
        {
            page = new(endpoint); pages.Add(endpoint.Key, page); browsers.Children.Add(page.View);
        }
        current = page; page.View.Visibility = Visibility.Visible;
        MemoryTarget(page.View, false);
        page.Ready ??= Initialize(page, timing);
        if (!page.Failed) PageStatus(page, page.Status, page.Retry);
        changed(); await page.Ready;
    }
    async Task Initialize(Page page, FirstOpenTiming? timing)
    {
        try
        {
            PageStatus(page, "正在载入 " + page.Endpoint.Name + "…");
            string profile = Path.Combine(directory, "AiWebProfiles", page.Endpoint.Key);
            timing?.Mark("webViewStartMs");
            page.Environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
            timing?.Mark("environmentReadyMs");
            if (destroyed) return;
            await page.View.EnsureCoreWebView2Async(page.Environment);
            timing?.Mark("webViewReadyMs");
            if (destroyed) return;
            Configure(page.View, page);
            await MaskBrowser(page.View);
            await page.View.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.addEventListener('dragover',e=>e.preventDefault(),true);window.addEventListener('drop',e=>{e.preventDefault();e.stopImmediatePropagation();},true);");
            timing?.Mark("navigationStartMs");
            if (verification) page.View.NavigateToString("<!doctype html><html><meta charset='utf-8'><style>body{font:16px system-ui;padding:20px;background:#eef5fa}textarea{width:90%;height:180px;padding:10px;font:16px system-ui}</style><h2>AI 面板测试页</h2><p>仅本机测试，不连接 AI 网站。</p><textarea id='draft'>保留我的草稿</textarea><p><input id='attachments' type='file' multiple></p><p><a id='drag-source' href='https://example.invalid/drag-fixture'>拖动测试链接</a></p></html>");
            else page.View.CoreWebView2.Navigate(page.Endpoint.Url);
            if (timing != null) _ = timing.RecordComposer(page.View.CoreWebView2, directory, () => destroyed || current != page);
            if (!IsOpen) MemoryTarget(page.View, true);
        }
        catch { timing?.Save(directory, "initialization-failed"); page.Failed = true; PageStatus(page, "网页初始化失败，请检查 Edge WebView2 运行时或点击重新加载。", true); }
        finally { if (!destroyed) changed(); }
    }
    void Configure(WebView2 view, Page page)
    {
        AttachDropTarget(view);
        var core = view.CoreWebView2; core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += async (_, args) =>
        {
            args.Handled = true;
            if (destroyed || popup != null) { SetStatus("请先返回 AI 对话，再打开其他页面。"); return; }
            using var lease = KeepOpen(); var deferral = args.GetDeferral(); WebView2? target = null;
            try
            {
                target = new WebView2 { DefaultBackgroundColor = Colors.Transparent }; browsers.Children.Add(target);
                popup = new(page, target); back.Visibility = Visibility.Visible; RefreshProviderEnabled(); page.View.Visibility = Visibility.Collapsed;
                await target.EnsureCoreWebView2Async(page.Environment);
                if (destroyed || popup?.View != target) return;
                Configure(target, page); await MaskBrowser(target); args.NewWindow = target.CoreWebView2;
            }
            catch { if (popup?.View == target) ClosePopup(); SetStatus("附加页面无法打开，请返回后重试。"); }
            finally { deferral.Complete(); }
        };
        core.WindowCloseRequested += (_, _) => { if (popup?.View == view) ClosePopup(); else Hide(); };
        core.NavigationStarting += (_, args) =>
        {
            if (verification) fixtureNavigation = args.Uri;
            // NavigateToString has a synthetic navigation URI. Only the local fixture uses it;
            // all real website navigation is checked by the production policy.
            if (!verification && !AiPolicy.AllowedNavigation(args.Uri)) { args.Cancel = true; SetStatus("该链接无法在 AI 面板内打开，仅支持安全网页。"); return; }
            PageStatus(page, page.Endpoint.Name + " · 正在载入网页…");
        };
        core.NavigationCompleted += (sender, args) =>
        {
            if (destroyed) return;
            string host = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) ? uri.Host : "";
            if(args.IsSuccess)_ = MaskBrowser(view);
            PageStatus(page, args.IsSuccess ? page.Endpoint.Name + (host.Length > 0 ? " · " + host : "") : "网页加载失败（" + args.WebErrorStatus + "），可点击重新加载。", !args.IsSuccess);
        };
        core.ProcessFailed += (_, _) => { if (!destroyed) { page.Failed = true; PageStatus(page, "网页进程已停止，点击重新加载可恢复页面。", true); } };
    }
    void ClosePopup()
    {
        if (popup is not { } value) return;
        popup = null; maskRegistrations.Remove(value.View); maskSurfaces.Remove(value.View); browsers.Children.Remove(value.View); value.View.Close(); back.Visibility = Visibility.Collapsed; RefreshProviderEnabled();
        if (current == value.Owner) current.View.Visibility = Visibility.Visible;
    }
    void Remove(Page page) { maskRegistrations.Remove(page.View); maskSurfaces.Remove(page.View); pages.Remove(page.Endpoint.Key); browsers.Children.Remove(page.View); page.View.Close(); }
    internal void HideDocked() { if (!Detached) Hide(); }
    protected override void OnHiding()
    {
        SaveFloating();
        changed();
    }
    protected override void OnHidden()
    {
        foreach (var page in pages.Values) MemoryTarget(page.View, true);
    }
    internal void ToggleDetached()
    {
        if (ModalActive || changingMode || options.ReadOnly) return;
        try { options.Save(options.Data with { FloatingPanel = Detached }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetStatus("窗口模式保存失败：" + error.Message); }
    }
    void ApplyWindowMode(bool detached)
    {
        if (changingMode || Detached == detached) return;
        changingMode = true;
        try
        {
            using var lease = KeepOpen();
            EndDockResize(true); EndExpandedMove(true); SaveFloating(); Detached = detached;
            UpdateResizeGrips();
            if (TaskbarFlyout is not null)
            {
                aiHeader.IsTabStop = Detached;
                dockPlacement = null;
                Position();
                AiControls.SetIcon(expand, Detached ? "\uE73F" : "\uE740");
                string label = Detached ? "缩小 AI 面板" : "放大 AI 面板";
                AutomationProperties.SetName(expand, label);
                ToolTipService.SetToolTip(expand, label);
                UpdateAiHeaderLayout();
                return;
            }
            var presenter = (OverlappedPresenter)AppWindow.Presenter;
            // Both modes share one borderless silhouette. Resize hit testing is
            // handled by AiResizeFrame rather than an OS-painted thick frame.
            presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false;
            UpdateSwitcherVisibility();
            runningWindowIcon?.Apply(force: true);
            if (Detached)
            {
                Native.RestoreResizeFrame(WinRT.Interop.WindowNative.GetWindowHandle(this));
                ClampFloating();
            }
            else
            {
                Native.RemoveWindowFrame(WinRT.Interop.WindowNative.GetWindowHandle(this));
                dockPlacement = null; Position();
            }
            AiControls.SetIcon(expand, Detached ? "\uE73F" : "\uE740");
            UpdateAiHeaderLayout();
            string name = Detached ? "切换为悬浮面板" : "展开为独立窗口";
            AutomationProperties.SetName(expand, name); ToolTipService.SetToolTip(expand, name);
            ApplyPin();
            if (IsOpen) ShowPanel();
            backplateShape = null; RefreshBackplateCorners();
            QueueFrameRefresh();
            UpdateFloatingCaption();
        }
        finally { changingMode = false; }
    }
    void UpdateSwitcherVisibility()
    {
        try
        {
            AppWindow.IsShownInSwitchers = Detached;
            switcherVisibilityApplied = true;
        }
        catch (NotImplementedException)
        {
            // Shell switcher registration is optional for both window modes.
            switcherVisibilityApplied = false;
        }
    }
    void SaveFloating()
    {
        saveTimer.Stop(); if (TaskbarFlyout is not null || !Detached || current == null || destroyed) return;
        floating = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        try { options.SaveBounds(current.Endpoint.Key, floating); } catch { SetStatus("窗口位置保存失败，请检查设置目录权限。"); }
    }
    void ClampFloating()
    {
        var work = display.Info.Work; var endpoint = AiPolicy.Resolve(options.Data);
        var value = AiPolicy.Fit(floating ?? options.Data.Layouts?.GetValueOrDefault(endpoint.Key), new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), display.Dpi);
        AppWindow.MoveAndResize(new(value.X, value.Y, value.Width, value.Height));
    }
    void Position()
    {
        var bounds = DockBounds();
        if (TaskbarFlyout is not null && PanelContent is { } panel)
        {
            if (Detached) bounds = ExpandedFlyoutBounds();
            panel.Width = bounds.Width / FlyoutScale;
            panel.Height = bounds.Height / FlyoutScale;
            TaskbarFlyout.SetScreenPosition(bounds.X, bounds.Y);
            ApplyFlyoutLayer();
            return;
        }
        AppWindow.MoveAndResize(new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }
    AiBounds DockBounds()
    {
        var work = display.Info.Work;
        return dockPlacement ?? AiPolicy.DockBounds(dockWidth, dockHeight,
            new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), display.Dpi);
    }
    internal void UpdateDisplay(DesktopMonitor monitor)
    {
        if (monitor != display) { EndDockResize(true); EndExpandedMove(true); }
        display = monitor;
        if (IsOpen)
        {
            if (Detached && TaskbarFlyout is null) ClampFloating();
            else { dockPlacement = null; Position(); }
        }
    }
}
