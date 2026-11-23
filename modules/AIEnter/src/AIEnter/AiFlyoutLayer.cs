using System.Runtime.InteropServices;

namespace AIEnter;

internal sealed partial class AiWindow
{
    const int OwnerIndex = -8, ExtendedStyleIndex = -20;
    const long TopmostStyle = 0x00000008;
    const uint OwnerWindow = 4;
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

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    static extern nint GetLayerOwner(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetLayerStyle(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern nint SetLayerOwner(nint window, int index, nint owner);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    static extern bool SetLayerPosition(nint window, nint after, int x, int y, int width, int height, uint flags);
}
