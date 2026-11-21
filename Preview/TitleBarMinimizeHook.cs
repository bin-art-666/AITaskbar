using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Native = AIEnter.Native;

namespace AITaskbar;

internal sealed class TitleBarMinimizeHook : IDisposable
{
    private delegate nint MouseHook(int code, nint message, nint data);
    private const int LeftDown = 0x0201, LeftUp = 0x0202;
    private readonly MouseHook callback;
    private readonly DispatcherQueue dispatcher;
    private readonly Func<nint, RunningWindow?> findWindow;
    private readonly Action<RunningWindow> minimize;
    private RunningWindow? pressed;
    private nint hook;

    internal TitleBarMinimizeHook(DispatcherQueue dispatcher, Func<nint, RunningWindow?> findWindow,
        Action<RunningWindow> minimize)
    {
        this.dispatcher = dispatcher;
        this.findWindow = findWindow;
        this.minimize = minimize;
        callback = OnMouse;
        hook = SetWindowsHookExW(14, callback, GetModuleHandleW(null), 0); // WH_MOUSE_LL
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code == 0 && (message == LeftDown || message == LeftUp && pressed is not null))
        {
            var point = Marshal.PtrToStructure<Native.Point>(data);
            nint target = GetAncestor(WindowFromPoint(point), 2); // GA_ROOT
            if (message == LeftDown)
            {
                pressed = null;
                if (findWindow(target) is { } window && IsMinimizeButton(target, point))
                {
                    pressed = window;
                    return 1;
                }
            }
            else if (pressed is { } window)
            {
                pressed = null;
                // A release outside the same button cancels the press, as with the native caption.
                if (target == window.Handle && RunningWindows.IsCurrent(window) && IsMinimizeButton(target, point))
                {
                    if (!dispatcher.TryEnqueue(() => minimize(window))) RequestNativeMinimize(window);
                }
                return 1;
            }
        }
        return CallNextHookEx(0, code, message, data);
    }

    private static bool IsMinimizeButton(nint window, Native.Point point)
    {
        // Limit the cross-process query to the top of a tracked window.
        if (!GetWindowRect(window, out var bounds) || point.Y - bounds.Top is < 0 or > 192) return false;
        const int MinimizeBox = 0x00020000, Disabled = 0x08000000;
        if ((GetWindowLongW(window, -16) & (MinimizeBox | Disabled)) != MinimizeBox) return false;
        var info = new TitleBarInfoEx { Size = (uint)Marshal.SizeOf<TitleBarInfoEx>(), States = new uint[6], Rectangles = new Native.Rect[6] };
        const uint BlockAbortIfHungOrExited = 0x0001 | 0x0002 | 0x0020;
        if (SendMessageTimeoutW(window, 0x033F, 0, ref info, BlockAbortIfHungOrExited, 14, out _) == 0) return false;
        const uint Unavailable = 0x0001, InvisibleOrOffscreen = 0x8000 | 0x10000;
        uint state = info.States[2];
        if ((state & Unavailable) != 0) return false;
        var button = info.Rectangles[2];
        if ((state & InvisibleOrOffscreen) == 0 && button.Left < button.Right && button.Top < button.Bottom)
            return point.X >= button.Left && point.X < button.Right && point.Y >= button.Top && point.Y < button.Bottom;

        // Custom title bars can omit the native rectangle while still returning HTMINBUTTON.
        if (point.X is < short.MinValue or > short.MaxValue || point.Y is < short.MinValue or > short.MaxValue) return false;
        nint coordinates = unchecked((int)((uint)(ushort)point.X | (uint)(ushort)point.Y << 16));
        return SendMessageTimeoutW(window, 0x0084, 0, coordinates, BlockAbortIfHungOrExited, 14, out nint hit) != 0 &&
            hit == 8; // WM_NCHITTEST / HTMINBUTTON
    }

    internal static void RequestNativeMinimize(RunningWindow window)
    {
        if (RunningWindows.IsCurrent(window))
            PostMessageW(window.Handle, 0x0112, 0xF020, 0); // WM_SYSCOMMAND, SC_MINIMIZE
    }

    public void Dispose()
    {
        if (hook != 0) UnhookWindowsHookEx(hook);
        hook = 0;
        pressed = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TitleBarInfoEx
    {
        internal uint Size;
        internal Native.Rect Bounds;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] internal uint[] States;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] internal Native.Rect[] Rectangles;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint SetWindowsHookExW(int id, MouseHook procedure, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Native.Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Native.Rect bounds);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetWindowLongW(nint window, int index);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint SendMessageTimeoutW(nint window, uint message,
        nint wParam, ref TitleBarInfoEx info, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint SendMessageTimeoutW(nint window, uint message,
        nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
