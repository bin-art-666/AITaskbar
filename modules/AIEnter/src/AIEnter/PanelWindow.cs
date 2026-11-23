using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VirtualKey = Windows.System.VirtualKey;

namespace AIEnter;

/// <summary>Shared lifecycle for transient feature panels: outside focus, Escape and close dismiss them.</summary>
internal abstract class PanelWindow : Window
{
    internal static event Action? PresentationChanged;
    protected void NotifyPresentationChanged() => PresentationChanged?.Invoke();
    long activationVersion;
    bool closed;
    long hideVersion;
    CancellationTokenSource? hideCancellation;
    internal bool IsClosing { get; private set; }
    protected virtual bool FloatFadeMotion => false;
    protected virtual Action<float, float>? BackdropMotion => null;
    protected AITaskbar.TaskbarPanelFlyout? TaskbarFlyout { get; set; }
    internal FrameworkElement? PanelContent { get; private set; }
    int modalDepth;
    int dragDepth;
    uint outsidePressTime;
    Native.Point outsidePressPoint;
    bool hasOutsidePress;
    internal void ObserveOutsidePress(uint time, Native.Point point) { outsidePressTime = time; outsidePressPoint = point; hasOutsidePress = true; }
    internal bool DismissedByPress(uint time, Native.Point point) => !IsOpen && hasOutsidePress
        && unchecked(time - outsidePressTime) <= 50 && point.X == outsidePressPoint.X && point.Y == outsidePressPoint.Y;
    OutsideClickWatcher? outsideClicks;
    PanelCaption? caption;
    protected void InitializeFloatingCaption(FrameworkElement header, FrameworkElement controls, Func<bool> enabled, FrameworkElement? leading = null) => caption = new(this, header, controls, enabled, leading);
    protected void UpdateFloatingCaption() => caption?.Queue();
    internal Task VerifyFloatingCaption(bool enabled) => caption?.Verify(enabled) ?? throw new InvalidOperationException("Caption not initialized");
    internal bool IsOpen { get; private set; }
    internal bool ModalActive => modalDepth > 0;
    internal bool DragActive => dragDepth > 0;
    bool VisibilityProtected => modalDepth > 0 || dragDepth > 0;
    protected virtual bool DismissOnOutside => true;
    protected virtual bool ConsumeOutsideClick => false;
    protected virtual bool Frameless => true;
    protected virtual bool AllowHide() => true;
    protected virtual bool HandlePanelWheel(Native.Point point,int delta,bool horizontal)=>false;
    protected virtual bool UsesPanelWheel=>false;

    protected PanelWindow()
    {
        Activated += (_, args) =>
        {
            long version = ++activationVersion;
            if (TaskbarFlyout is not null || args.WindowActivationState != WindowActivationState.Deactivated || !IsOpen || VisibilityProtected || !DismissOnOutside) return;
            // Let XAML finish activating a dropdown/owned popup before deciding focus left the panel.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (closed || !IsOpen || version != activationVersion || VisibilityProtected || !DismissOnOutside) return;
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (!Native.PanelHasFocus(handle) && !PanelToggleTargets.OwnsForegroundPointer()) Hide();
            });
        };
        AppWindow.Closing += (_, args) => { if (!closed) { args.Cancel = true; Hide(); } };
        Closed += (_, _) => { closed = true; CancelPendingHide(); IsOpen = false; activationVersion++; outsideClicks?.Dispose(); caption?.Dispose(); PanelTheme.Unregister(this); NotifyPresentationChanged(); };
    }

    protected void InitializePanel(FrameworkElement content)
    {
        PanelContent = content;
        if (TaskbarFlyout is { } flyout)
        {
            flyout.DismissOnOutside = () => !VisibilityProtected && DismissOnOutside && AllowHide();
            flyout.Closed += () =>
            {
                if (IsOpen) { IsOpen = false; OnHiding(); NotifyPresentationChanged(); }
                OnHidden();
            };
            Closed += (_, _) => flyout.Dispose();
        }
        else Content = content;
        PanelTheme.Register(this);
        content.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            Hide();
        };
        AppWindow.Hide();
    }

    protected void ShowPanel(bool activate = true)
    {
        if (TaskbarFlyout is { } flyout)
        {
            PanelTheme.Refresh(this);
            flyout.Show(activate);
            IsOpen = flyout.IsOpen;
            NotifyPresentationChanged();
            return;
        }
        bool entering = !IsOpen, resume = IsClosing;
        CancelPendingHide();
        PanelTheme.Refresh(this);
        activationVersion++;
        IsOpen = true; hasOutsidePress = false;
        // Activate alone is not a reliable way to re-show an AppWindow that was
        // explicitly hidden, especially after a non-activating drag-hover open.
        if (entering && FloatFadeMotion && Content is FrameworkElement content) PanelMotion.Enter(content, true, resume, BackdropMotion);
        AppWindow.Show(false);
        if (entering && !FloatFadeMotion && Content is FrameworkElement legacyContent) PanelMotion.Enter(legacyContent);
        if (activate) Activate();
        if (Frameless) Native.RemoveWindowFrame(WinRT.Interop.WindowNative.GetWindowHandle(this));
        caption?.Queue();
        outsideClicks?.Dispose();
        outsideClicks = new OutsideClickWatcher(WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue,
            () => { if (!VisibilityProtected && DismissOnOutside) Hide(); },
            UsesPanelWheel?(point,delta,horizontal)=>!VisibilityProtected&&HandlePanelWheel(point,delta,horizontal):null,
            ObserveOutsidePress, ConsumeOutsideClick);
        NotifyPresentationChanged();
    }

    internal void Hide()
    {
        if (!IsOpen || closed || VisibilityProtected) return;
        if (!AllowHide()) return;
        if (TaskbarFlyout is { } flyout)
        {
            IsOpen = false;
            OnHiding();
            flyout.Hide();
            NotifyPresentationChanged();
            return;
        }
        activationVersion++;
        IsOpen = false;
        outsideClicks?.Dispose();
        outsideClicks = null;
        try { OnHiding(); }
        finally
        {
            NotifyPresentationChanged();
            int duration = FloatFadeMotion && AppWindow.IsVisible && Content is FrameworkElement element
                && ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).State != Microsoft.UI.Windowing.OverlappedPresenterState.Minimized
                ? PanelMotion.Exit(element, BackdropMotion) : 0;
            if (duration > 0)
            {
                IsClosing = true;
                ((UIElement)Content).IsHitTestVisible = false;
                Native.EnableWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), false);
                hideCancellation = new();
                _ = FinishHide(++hideVersion, duration, hideCancellation.Token);
            }
            else HideImmediately();
        }
    }
    async Task FinishHide(long version, int duration, CancellationToken cancellation)
    {
        try { await Task.Delay(duration, cancellation); }
        catch (OperationCanceledException) { return; }
        if (!closed && version == hideVersion && !IsOpen) HideImmediately();
    }
    void CancelPendingHide()
    {
        hideVersion++; hideCancellation?.Cancel(); hideCancellation?.Dispose(); hideCancellation = null;
        if (IsClosing && !closed)
        {
            ((UIElement)Content).IsHitTestVisible = true;
            Native.EnableWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), true);
        }
        IsClosing = false;
    }
    void HideImmediately()
    {
        AppWindow.Hide();
        CancelPendingHide();
        if (Content is FrameworkElement content) PanelMotion.Reset(content, BackdropMotion);
        OnHidden();
    }

    protected virtual void OnHidden() { }
    protected virtual void OnHiding() { }
    internal void ConfirmIncomingDrop()
    {
        if (closed || !IsOpen) return;
        var lease = KeepOpenForDrag(); ShowPanel();
        if (!DispatcherQueue.TryEnqueue(async () => { try { await Task.Delay(350); } finally { lease.Dispose(); } })) lease.Dispose();
    }
    protected IDisposable KeepOpen()
    {
        modalDepth++; activationVersion++;
        return new ModalLease(() => { modalDepth--; activationVersion++; });
    }
    protected IDisposable KeepOpenForDrag()
    {
        dragDepth++; activationVersion++;
        return new ModalLease(() => { dragDepth--; activationVersion++; });
    }
    sealed class ModalLease(Action release) : IDisposable
    {
        Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
    internal void Destroy()
    {
        if (closed) return;
        try { Hide(); }
        finally { CancelPendingHide(); if (Content is FrameworkElement content) PanelMotion.Stop(content); closed = true; outsideClicks?.Dispose(); Close(); }
    }
}
