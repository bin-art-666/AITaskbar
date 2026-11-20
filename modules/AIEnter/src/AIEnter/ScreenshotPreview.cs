using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AIEnter;

internal sealed class ScreenshotPreview : PanelWindow
{
    readonly ScreenshotFrame frame;
    readonly string path;
    readonly Func<string, string, DesktopMonitor, Task> handoff;
    readonly Grid layout = new() { RowSpacing = 6 };
    readonly TextBlock status = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, MaxLines = 3, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly List<ButtonBase> targets = [];
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    long lastTick;
    double remaining;
    bool menuOpen, copying, finished;
    long copyFeedbackUntil;
    protected override bool ConsumeOutsideClick => true;
    internal string SavedPath => path;
    internal bool HasOutsideClickGuard => ConsumeOutsideClick;
    bool PointerInside()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return GetCursorPos(out var point) && GetWindowRect(handle,out var bounds)
            && point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
    }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.Point point);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Native.Rect bounds);
    ScreenshotPreview(ScreenshotFrame frame, string path, double width, Func<string, string, DesktopMonitor, Task>? handoff)
    {
        this.frame = frame; this.path = path; this.handoff = handoff ?? App.Current.SendScreenshot; Title = "AIEnter · 截图已保存";
        var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false,false); presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        AppWindow.IsShownInSwitchers = false;
        SystemBackdrop = PanelTheme.Backdrop();
        var providers = AiPolicy.OrderedProviders(App.Ai.Options.Data);
        double available = width - App.Appearance.ButtonSize * 3 - 46;
        var overflow = new MenuFlyout();
        foreach (string id in providers)
        {
            string name = AiPolicy.Resolve(App.Ai.Options.Data with { Provider = id }).Name;
            var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 40));
            double buttonWidth = Math.Min(140, label.DesiredSize.Width + 24);
            if (buttonWidth <= available)
            {
                var button = new ToggleButton { Content = label, IsChecked = id == App.Ai.Options.Data.Provider };
                PanelTheme.FramedIconStyle(button, "将截图放入 " + name);
                button.Width = buttonWidth; button.MinWidth = 0; button.Height = App.Appearance.ButtonSize;
                button.Click += async (_, _) => await Send(id);
                targets.Add(button); toolbar.Children.Add(button); available -= buttonWidth + 6;
            }
            else
            {
                var item = new MenuFlyoutItem { Text = name }; item.Click += async (_, _) => await Send(id); overflow.Items.Add(item);
            }
        }
        if (overflow.Items.Count > 0)
        {
            var more = AiControls.IconButton("\uE712", "更多 AI"); more.Flyout = overflow;
            targets.Add(more); toolbar.Children.Add(more);
            IDisposable? popupLease = null;
            overflow.Opened += (_, _) => { menuOpen = true; popupLease = KeepOpen(); };
            overflow.Closed += (_, _) => { menuOpen = false; popupLease?.Dispose(); popupLease = null; };
        }
        var copy = AiControls.IconButton("\uE8C8", "复制截图（Ctrl+C）");
        var copyShortcut = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        { Key = Windows.System.VirtualKey.C, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        copyShortcut.Invoked += async (_, args) => { args.Handled=true; await Copy(); };
        copy.KeyboardAccelerators.Add(copyShortcut);
        copy.Click += async (_, _) => { copy.IsEnabled=false; try { await Copy(); } finally { copy.IsEnabled=true; } };
        toolbar.Children.Add(copy);
        var close = AiControls.IconButton("\uE8BB", "关闭截图预览（保留本地图片）"); close.Click += (_, _) => Hide();
        targets.Add(close); toolbar.Children.Add(close);
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(toolbar); Grid.SetRow(status,2); layout.Children.Add(status);
        ToolTipService.SetToolTip(status, path);
        var border = new Border { Child = layout, Padding = new Thickness(8), CornerRadius = new CornerRadius(App.Appearance.BackplateRadius), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255,242,248,250)) };
        InitializePanel(border);
        remaining = App.Appearance.ScreenshotSeconds;
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) =>
        {
            long now = Environment.TickCount64; double elapsed = (now-lastTick)/1000d; lastTick=now;
            if (remaining <= 0 || menuOpen || PointerInside() || copying || finished) return;
            remaining -= elapsed;
            if (remaining <= 0) Hide(); else if (now >= copyFeedbackUntil) status.Text = $"已保存 · {Math.Ceiling(remaining)} 秒后关闭 · 悬停暂停";
        };
        Closed += (_, _) => { timer.Stop(); finished = true; };
    }
    internal async Task Copy()
    {
        if (copying || finished) return;
        copying=true;
        using var lease=KeepOpen();
        try
        {
            var file=await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var package=new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            status.Text="已复制截图，可直接粘贴";
        }
        catch(Exception error) when(error is not OutOfMemoryException) { status.Text="复制失败："+error.Message; }
        finally { copying=false; copyFeedbackUntil=Environment.TickCount64+3000; }
    }
    internal Task Send(string provider)
    {
        if (finished) return Task.CompletedTask;
        // Transfer ownership before awaiting any site initialization or confirmation
        // dialog: a disabled, topmost screenshot must never block the AI window.
        string savedPath = path;
        var display = Native.Monitors().OrderByDescending(m => Intersection(frame, m.Bounds)).First();
        Hide();
        return handoff(savedPath, provider, display);
    }
    static long Intersection(ScreenshotFrame frame, Native.Rect bounds) =>
        (long)Math.Max(0,Math.Min(frame.Left+frame.Width,bounds.Right)-Math.Max(frame.Left,bounds.Left)) * Math.Max(0,Math.Min(frame.Top+frame.Height,bounds.Bottom)-Math.Max(frame.Top,bounds.Top));
    protected override void OnHidden()
    {
        if (finished) return; finished = true; timer.Stop();
        DispatcherQueue.TryEnqueue(Destroy);
    }
    internal static async Task<ScreenshotPreview> Open(ScreenshotFrame frame, string path, Func<string, string, DesktopMonitor, Task>? handoff = null)
    {
        var display = Native.Monitors().OrderByDescending(m => Intersection(frame,m.Bounds)).First();
        var work = display.Info.Work; double scale = display.Dpi/96d;
        int width = Math.Min(work.Right-work.Left-16, Math.Max((int)(360*scale), Math.Min(frame.Width,(int)(900*scale))));
        int chrome = (int)((App.Appearance.ButtonSize+54)*scale);
        int height = Math.Min(work.Bottom-work.Top-16, (int)(frame.Height * Math.Min(1,width/(double)frame.Width))+chrome);
        var window = new ScreenshotPreview(frame,path,width/scale,handoff);
        var image = new Image { Source = await frame.Image(), Stretch = Stretch.Uniform };
        Grid.SetRow(image,1); window.layout.Children.Add(image);
        int x = Math.Clamp(frame.Left,work.Left+8,Math.Max(work.Left+8,work.Right-width-8));
        bool below = frame.Top-chrome < work.Top;
        if (below) { Grid.SetRow(window.toolbar,2); Grid.SetRow(window.status,0); }
        int y = Math.Clamp(below?frame.Top:frame.Top-(int)((App.Appearance.ButtonSize+16)*scale),work.Top+8,Math.Max(work.Top+8,work.Bottom-height-8));
        window.AppWindow.MoveAndResize(new RectInt32(x,y,width,height));
        window.ShowPanel();
        window.status.Text = window.remaining > 0 ? $"已保存 · {window.remaining:0} 秒后关闭 · 悬停暂停" : "截图已保存 · 点击 AI 加入附件，不发送消息";
        window.lastTick = Environment.TickCount64; window.timer.Start();
        return window;
    }
}
