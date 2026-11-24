using System.Runtime.InteropServices;

namespace AIEnter;

internal sealed partial class AiWindow
{
    const int OwnerIndex = -8, ExtendedStyleIndex = -20;
    const long TopmostStyle = 0x00000008;
    const long TransparentStyle = 0x00000020;
    const uint PreviousWindow = 3, OwnerWindow = 4;
    const uint ExtendedFrameBounds = 9, Cloaked = 14;
    const uint PreserveGeometryAndActivation = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    nint layeredFlyout, taskbarFlyoutOwner;

    void AttachFlyoutLayer(nint window)
    {
        layeredFlyout = window;
        taskbarFlyoutOwner = GetLayerOwner(window, OwnerWindow);
        ApplyFlyoutLayer();
    }

    void ApplyFlyoutLayer()
    {
        if (layeredFlyout == 0) return;
        bool topmost = !Detached || options.Data.Pinned;
        // A window owned by the topmost taskbar cannot enter normal Z order.
        // The existing hidden AI window keeps ownership without exposing
        // an additional taskbar or Alt+Tab entry.
        var owner = topmost ? taskbarFlyoutOwner : WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (GetLayerOwner(layeredFlyout, OwnerWindow) != owner)
        {
            SetLayerOwner(layeredFlyout, OwnerIndex, owner);
            if (GetLayerOwner(layeredFlyout, OwnerWindow) != owner) return;
        }

        if (((GetLayerStyle(layeredFlyout, ExtendedStyleIndex).ToInt64() & TopmostStyle) != 0) != topmost)
            SetLayerPosition(layeredFlyout, topmost ? -1 : -2, 0, 0, 0, 0, PreserveGeometryAndActivation);
    }

    void DetachFlyoutLayer()
    {
        if (layeredFlyout != 0 && GetLayerOwner(layeredFlyout, OwnerWindow) != taskbarFlyoutOwner)
            SetLayerOwner(layeredFlyout, OwnerIndex, taskbarFlyoutOwner);
        layeredFlyout = taskbarFlyoutOwner = 0;
    }

    void RaiseExpandedFlyout(nint window)
    {
        if (window != layeredFlyout || !IsOpen || !Detached || options.Data.Pinned || ModalActive || DragActive) return;
        // WinUI's popup can receive clicks without taking foreground activation.
        // Raise it within ordinary windows when the user clicks its exposed area.
        if (!SetForegroundWindow(window))
            SetLayerPosition(window, 0, 0, 0, 0, 0, PreserveGeometryAndActivation);
    }

    bool RecallCoveredExpandedFlyout()
    {
        if (layeredFlyout == 0 || !Detached || options.Data.Pinned) return false;

        // The taskbar click can change the active window before this runs.
        // Inspect the stacking order and overlap of the ordinary windows instead.
        var panel = ExpandedFlyoutBounds();
        for (var window = GetLayerOwner(layeredFlyout, PreviousWindow); window != 0; window = GetLayerOwner(window, PreviousWindow))
        {
            if (!IsLayerWindowVisible(window) || IsLayerWindowMinimized(window) || GetLayerOwner(window, OwnerWindow) == layeredFlyout
                || (GetLayerStyle(window, ExtendedStyleIndex).ToInt64() & (TopmostStyle | TransparentStyle)) != 0
                || (GetLayerCloaking(window, Cloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
                continue;

            if (GetLayerFrame(window, ExtendedFrameBounds, out var bounds, Marshal.SizeOf<Native.Rect>()) != 0
                && !GetBackplateWindowRect(window, out bounds)) continue;
            if (bounds.Left >= panel.X + panel.Width || bounds.Right <= panel.X
                || bounds.Top >= panel.Y + panel.Height || bounds.Bottom <= panel.Y) continue;

            // The taskbar receives foreground activation during the click.
            // Move the popup itself ahead of ordinary windows before focusing it.
            SetLayerPosition(layeredFlyout, 0, 0, 0, 0, 0, PreserveGeometryAndActivation);
            SetForegroundWindow(layeredFlyout);
            return true;
        }
        return false;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    static extern nint GetLayerOwner(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetLayerStyle(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern nint SetLayerOwner(nint window, int index, nint owner);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    static extern bool SetLayerPosition(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    static extern bool IsLayerWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    static extern bool IsLayerWindowMinimized(nint window);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    static extern int GetLayerCloaking(nint window, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    static extern int GetLayerFrame(nint window, uint attribute, out Native.Rect bounds, int size);
}
