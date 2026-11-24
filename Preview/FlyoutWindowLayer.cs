using System.Runtime.InteropServices;

namespace AITaskbar;

internal sealed class FlyoutWindowLayer(nint hiddenOwner)
{
    const int OwnerIndex = -8;
    internal const int ExtendedStyleIndex = -20;
    internal const long TopmostStyle = 0x00000008;
    const long TransparentStyle = 0x00000020;
    const uint PreviousWindow = 3;
    internal const uint OwnerWindow = 4;
    const uint ExtendedFrameBounds = 9, Cloaked = 14;
    const uint PreserveGeometryAndActivation = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    internal nint Handle { get; private set; }
    nint taskbarFlyoutOwner;

    internal void Attach(nint window, bool topmost)
    {
        Handle = window;
        taskbarFlyoutOwner = GetLayerWindow(window, OwnerWindow);
        Apply(topmost);
    }

    internal void Apply(bool topmost)
    {
        if (Handle == 0) return;
        // A window owned by the topmost taskbar cannot enter normal Z order.
        // The hidden module window keeps ownership without exposing
        // an additional taskbar or Alt+Tab entry.
        var owner = topmost ? taskbarFlyoutOwner : hiddenOwner;
        if (GetLayerWindow(Handle, OwnerWindow) != owner)
        {
            SetLayerOwner(Handle, OwnerIndex, owner);
            if (GetLayerWindow(Handle, OwnerWindow) != owner) return;
        }

        if (((GetLayerStyle(Handle, ExtendedStyleIndex).ToInt64() & TopmostStyle) != 0) != topmost)
            SetLayerPosition(Handle, topmost ? -1 : -2, 0, 0, 0, 0, PreserveGeometryAndActivation);
    }

    internal void Detach()
    {
        if (Handle != 0 && GetLayerWindow(Handle, OwnerWindow) != taskbarFlyoutOwner)
            SetLayerOwner(Handle, OwnerIndex, taskbarFlyoutOwner);
        Handle = taskbarFlyoutOwner = 0;
    }

    internal void Raise()
    {
        if (Handle == 0) return;
        // WinUI's popup can receive clicks without taking foreground activation.
        // Raise it within ordinary windows when the user clicks its exposed area.
        if (!SetForegroundWindow(Handle))
            SetLayerPosition(Handle, 0, 0, 0, 0, 0, PreserveGeometryAndActivation);
    }

    internal bool RecallIfCovered(int x, int y, int width, int height)
    {
        if (Handle == 0) return false;

        // The taskbar click can change the active window before this runs.
        // Inspect the stacking order and overlap of the ordinary windows instead.
        for (var window = GetLayerWindow(Handle, PreviousWindow); window != 0; window = GetLayerWindow(window, PreviousWindow))
        {
            if (!IsLayerWindowVisible(window) || IsLayerWindowMinimized(window) || GetLayerWindow(window, OwnerWindow) == Handle
                || (GetLayerStyle(window, ExtendedStyleIndex).ToInt64() & (TopmostStyle | TransparentStyle)) != 0
                || (GetLayerCloaking(window, Cloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
                continue;

            if (GetLayerFrame(window, ExtendedFrameBounds, out var bounds, Marshal.SizeOf<AIEnter.Native.Rect>()) != 0
                && !GetWindowRect(window, out bounds)) continue;
            if (bounds.Left >= x + width || bounds.Right <= x
                || bounds.Top >= y + height || bounds.Bottom <= y) continue;

            // The taskbar receives foreground activation during the click.
            // Move the popup itself ahead of ordinary windows before focusing it.
            SetLayerPosition(Handle, 0, 0, 0, 0, 0, PreserveGeometryAndActivation);
            SetForegroundWindow(Handle);
            return true;
        }
        return false;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    internal static extern nint GetLayerWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetLayerStyle(nint window, int index);
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
    static extern int GetLayerFrame(nint window, uint attribute, out AIEnter.Native.Rect bounds, int size);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(nint window, out AIEnter.Native.Rect bounds);
}
