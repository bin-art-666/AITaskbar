using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
    private readonly TextBlock feedback = new() { MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private bool closed, menuOpen;
    internal PreviewModuleIcons ModuleIcons { get; }
    internal IReadOnlyList<PinnedApp> PinnedItems => pins.Items;

    public PreviewTaskbar(App app, string root, AppearanceHost appearance, int iconSize,
        Action<Exception> reportFailure)
    {
        Title = "AITaskbar 预览栏";
        pins = new(Path.Combine(root, "preview-pins.json"));
        AppWindow.IsShownInSwitchers = false;
        var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        tasks = new(pins, app.IconPreferences,
            iconSize, app.AppFeedback, app.IconLayout, app.CurrentAppCenterAppearance.Icon, app.ShowAppCenterSettings, handle, Report);
        AIEnter.Native.RemoveWindowFrame(handle);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        var halo = Button("Halo", "Halo 中转站", () => app.HaloModule.Handle("Toggle"));
        var ai = Button("AIEnter", "AIEnter 对话", () => _ = app.AiModule.ToggleAiWindow(false));
        SetMenu(halo, "Halo 设置", Halo.App.OpenSettings);
        SetMenu(ai, "AIEnter 设置", app.AiModule.OpenSettings);
        SetMenu(tasks.AppCenterButton, "应用中心设置", app.ShowAppCenterSettings);
        AcceptDrop(halo, Halo.StationWindow.Supports, async view =>
        {
            app.HaloModule.Handle("Open");
            return Halo.App.Panel is { } panel && await panel.Receive(view);
        });
        AcceptDrop(ai, AIEnter.DropPayload.Supported, async view =>
        {
            await AIEnter.App.Ai.OpenForDrop(AIEnter.Native.Monitors().First(monitor => monitor.Primary), true);
            return await AIEnter.App.Ai.ReceiveDrop(view);
        });
        var icons = ModuleIcons = new PreviewModuleIcons(halo, ai, DispatcherQueue);
        left.Children.Add(halo);
        left.Children.Add(Button("\uE718", "固定应用", () => _ = PickPinAsync(), glyph: true));

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(feedback);
        right.Children.Add(Button("\uE713", "打开 AITaskbar 设置", app.ShowSettings, glyph: true));
        right.Children.Add(Button("\uE8BB", "关闭预览栏", Close, glyph: true));
        right.Children.Add(icons.AiEntrance);

        var strip = new Grid { Padding = new Thickness(10, 0, 10, 0) };
        strip.Children.Add(tasks.HoverSurface);
        strip.Children.Add(tasks.Surface);
        strip.Children.Add(tasks.AppDragSurface);
        strip.Children.Add(left);
        strip.Children.Add(right);
        void FitApps(object sender, SizeChangedEventArgs args) =>
            tasks.SetAvailableWidth(strip.ActualWidth - 36 - 2 * Math.Max(left.ActualWidth, right.ActualWidth));
        strip.SizeChanged += FitApps;
        left.SizeChanged += FitApps;
        right.SizeChanged += FitApps;
        var surface = new Border();
        var visual = new PreviewBarAppearance(this, surface, strip, appearance, light =>
        {
            icons.RefreshSystem(light);
            tasks.SetFeedbackLight(light);
        });

        refresh = DispatcherQueue.CreateTimer();
        refresh.Interval = TimeSpan.FromMilliseconds(1100);
        refresh.Tick += (_, _) =>
        {
            visual.Refresh();
            icons.RefreshSystem(visual.IsLight);
            if (!menuOpen) tasks.Refresh();
        };
        Closed += (_, _) =>
        {
            closed = true;
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
            bar = new(handle, DispatcherQueue, iconSize, tasks.RememberForeground, error => { Close(); reportFailure(error); });
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
        bar.SetIconSize(size);
    }
    internal void RefreshIconStyle(string executable) => tasks.RefreshIconStyle(executable);
    internal void SetAppCenterIcon(AppCenterIconStyle style) => tasks.SetAppCenterIcon(style);
    internal void SetAppFeedback(TaskbarAppFeedbackStyle style) => tasks.SetAppFeedback(style);
    internal void SetIconLayout(PreviewIconLayout layout) => tasks.SetIconLayout(layout);
    internal void SetThumbnailWidth(int width) => tasks.SetThumbnailWidth(width);

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

    private static Button Button(string label, string name, Action click, bool glyph = false)
    {
        var button = new Button { Content = label, Height = 34, Padding = new Thickness(10, 0, 10, 0) };
        if (glyph) button.FontFamily = new FontFamily("Segoe Fluent Icons");
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private void SetMenu(Button button, string label, Action click)
    {
        var menu = new MenuFlyout();
        var action = new MenuFlyoutItem { Text = label };
        menu.Opening += (_, _) => menuOpen = true;
        menu.Closed += (_, _) => menuOpen = false;
        action.Click += (_, _) => click();
        menu.Items.Add(action);
        button.ContextFlyout = menu;
    }

    private void AcceptDrop(Button button, Func<DataPackageView, bool> supports, Func<DataPackageView, Task<bool>> deliver)
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
            try { Report(await deliver(args.DataView) ? "" : "本次拖入未完成，请重试。"); }
            catch (Exception error) { Report(error.Message); }
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
