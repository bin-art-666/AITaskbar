namespace AIEnter;

internal sealed class AiService
{
    readonly string directory;
    readonly bool verification;
    AiWindow? window;
    Microsoft.UI.Xaml.FrameworkElement? taskbarAnchor;
    (Microsoft.UI.Xaml.ElementTheme Theme, Windows.UI.Color Tint)? taskbarPalette;
    internal AiOptionsStore Options { get; }
    internal bool HasPendingTransfer => window?.HasPendingDrop == true;
    internal bool Busy => window?.Initializing == true;
    internal bool IsOpen => window?.IsOpen == true;
    internal bool DockedOpen=>window is {IsOpen:true,Detached:false};
    internal string? DisplayDevice => window?.DisplayDevice;
    internal bool ModalActive => window?.ModalActive == true;
    internal string Status => window?.Status ?? "点击打开 AI 面板，右上角可放大。";
    internal event Action? Changed;
    internal AiService(string directory, AiOptionsStore? options = null, bool verification = false)
    {
        this.directory = directory; this.verification = verification; Options = options ?? new(Path.Combine(directory, "ai.json"));
        Options.Changed += OptionsChanged;
    }
    void OptionsChanged() { window?.ApplyOptions(); Changed?.Invoke(); }
    internal void AttachTaskbar(Microsoft.UI.Xaml.FrameworkElement anchor)
    {
        taskbarAnchor = anchor;
        window?.Destroy();
        window = null;
    }
    internal void DetachTaskbar(Microsoft.UI.Xaml.FrameworkElement anchor)
    {
        if (!ReferenceEquals(taskbarAnchor, anchor)) return;
        window?.Destroy();
        window = null;
        taskbarAnchor = null;
        taskbarPalette = null;
    }
    internal void SetTaskbarPalette(Microsoft.UI.Xaml.ElementTheme theme, Windows.UI.Color tint)
    {
        taskbarPalette = (theme, tint);
        window?.SetTaskbarPalette(theme, tint);
    }
    AiWindow GetPanel(DesktopMonitor display)
    {
        if (window is not null) return window;
        window = new AiWindow(Options, directory, display, () => Changed?.Invoke(), verification, taskbarAnchor);
        if (taskbarPalette is { } palette) window.SetTaskbarPalette(palette.Theme, palette.Tint);
        return window;
    }
    internal void Select(string provider) => Options.Save(Options.Data with { Provider = provider });
    internal async Task Open(DesktopMonitor display, int? top = null)
    {
        if (SessionState.Locked == true) return;
        await GetPanel(display).Open(display, top ?? display.Info.Work.Top);
    }
    internal async Task ToggleFromTaskbar(DesktopMonitor display, uint pressTime = 0, Native.Point pressPoint = default, FirstOpenTiming? timing = null)
    {
        if (SessionState.Locked == true) return;
        await GetPanel(display).ToggleFromTaskbar(display, pressTime, pressPoint, timing);
    }
    internal async Task OpenForDrop(DesktopMonitor display, bool activate)
    {
        if (SessionState.Locked == true) return;
        var panel = GetPanel(display);
        if (activate) await panel.Open(display, display.Info.Work.Top, true); else await panel.OpenForDragHover(display);
    }
    internal async Task<bool> ReceiveFiles(string[] paths, string provider, DesktopMonitor display)
    {
        if (!AiPolicy.AvailableProviders(Options.Data).Contains(provider)) throw new IOException("目标 AI 已移除。");
        var view = await DropPayload.FilesView(paths);
        Select(provider);
        await OpenForDrop(display, true).WaitAsync(TimeSpan.FromSeconds(20));
        if (Options.Data.Provider != provider) throw new IOException("接收期间 AI 已切换，请回到原 AI 后重新拖入。");
        // Do not bypass ReceiveDrop: parsing, target locking, pending content,
        // upload and retry are shared with normal window drops.
        return await ReceiveDrop(view);
    }
    internal Task<bool> ReceivePayload(DropPayload payload, string provider) => window?.ReceivePayload(payload, provider) ?? Task.FromResult(false);
    internal void DropError(string text) => window?.ReportDropError(text);
    internal void Hide(bool includeDetached = true) { if (includeDetached) window?.Hide(); else window?.HideDocked(); }
    internal async Task OpenPreferred(DesktopMonitor display)
    {
        if (SessionState.Locked == true) return;
        var panel = GetPanel(display);
        panel.ApplyOptions();
        await panel.Open(display, display.Info.Work.Top);
    }
    internal void PreviewCorners(bool follow,int radius)=>window?.PreviewCorners(follow,radius);
    internal async Task PreviewViewport(DesktopMonitor display,int top)
    {if(SessionState.Locked==true)return;await GetPanel(display).PreviewViewport(display,top);}
    internal Task<bool> ReceiveDrop(Windows.ApplicationModel.DataTransfer.DataPackageView view) => window?.ReceiveDrop(view) ?? throw new IOException("请先打开 AI 面板。");
    internal void UpdateDisplay(DesktopMonitor display) { if (window?.DisplayDevice == display.Device) window.UpdateDisplay(display); }
    internal void Stop() { Options.Changed -= OptionsChanged; window?.Destroy(); window = null; }
}
