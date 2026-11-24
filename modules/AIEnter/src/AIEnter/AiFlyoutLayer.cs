using AITaskbar;

namespace AIEnter;

internal sealed partial class AiWindow
{
    FlyoutWindowLayer? flyoutLayer;
    bool FlyoutTopmost => !Detached || options.Data.Pinned;

    void AttachFlyoutLayer(nint window)
    {
        flyoutLayer ??= new(WinRT.Interop.WindowNative.GetWindowHandle(this));
        flyoutLayer.Attach(window, FlyoutTopmost);
    }

    void ApplyFlyoutLayer() => flyoutLayer?.Apply(FlyoutTopmost);
    void DetachFlyoutLayer() => flyoutLayer?.Detach();

    void RaiseExpandedFlyout(nint window)
    {
        if (flyoutLayer?.Handle == window && IsOpen && !FlyoutTopmost && !ModalActive && !DragActive)
            flyoutLayer.Raise();
    }

    bool RecallCoveredExpandedFlyout()
    {
        if (flyoutLayer is null || FlyoutTopmost) return false;
        var bounds = ExpandedFlyoutBounds();
        return flyoutLayer.RecallIfCovered(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
