using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Native = AIEnter.Native;

namespace AITaskbar;

internal sealed class DesktopAppBar : IDisposable
{
    private const uint Add = 0, Remove = 1, QueryPosition = 2, SetPosition = 3, Activate = 6, WindowPositionChanged = 9;
    private const uint BottomEdge = 3, NoActivate = 0x0010, NoMoveOrSize = 0x0003;
    private readonly nint handle;
    private readonly DispatcherQueue dispatcher;
    private readonly Action<Exception> reportFailure;
    private readonly Action beforeMouseActivation;
    private readonly SubclassProcedure callback;
    private readonly uint notification = RegisterWindowMessage("AITaskbar.Preview.AppBar");
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private Native.Rect? proposed, reserved;
    private int heightDip;
    internal bool CoversWindows { get; private set; }
    private bool registered, disposed, queued, shellRestarted, fullscreen;

    public DesktopAppBar(nint handle, DispatcherQueue dispatcher, int height, bool coversWindows,
        Action beforeMouseActivation, Action<Exception> reportFailure)
    {
        this.handle = handle;
        this.dispatcher = dispatcher;
        this.reportFailure = reportFailure;
        this.beforeMouseActivation = beforeMouseActivation;
        heightDip = height;
        CoversWindows = coversWindows;
        callback = WndProc;
        if (notification == 0 || taskbarCreated == 0 || !SetWindowSubclass(handle, callback, 1, 0))
            throw new InvalidOperationException("无法初始化预览栏窗口。");
        try
        {
            Register();
            Position();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private AppBarData Data() => new() { Size = (uint)Marshal.SizeOf<AppBarData>(), Window = handle, Callback = notification, Edge = BottomEdge };

    private void Register()
    {
        var data = Data();
        if (SHAppBarMessage(Add, ref data) == 0)
            throw new InvalidOperationException("Windows 未能注册预览栏。");
        registered = true;
    }

    private void Position()
    {
        var monitor = Native.Monitors().First(monitor => monitor.Primary);
        var height = (int)Math.Round(heightDip * monitor.Dpi / 96d);
        var data = Data();
        data.Rectangle = monitor.Bounds;
        // A zero-height reservation keeps the shell's position and fullscreen notifications.
        int reservedHeight = CoversWindows ? 0 : height;
        data.Rectangle.Top = data.Rectangle.Bottom - reservedHeight;
        SHAppBarMessage(QueryPosition, ref data);
        data.Rectangle.Top = data.Rectangle.Bottom - reservedHeight;
        if (proposed is not { } previous || !previous.Equals(data.Rectangle))
        {
            proposed = data.Rectangle;
            if (SHAppBarMessage(SetPosition, ref data) == 0)
                throw new InvalidOperationException("Windows 未能为预览栏留出空间。");
            reserved = data.Rectangle;
        }
        var bounds = reserved!.Value;
        if (CoversWindows) bounds.Top = bounds.Bottom - height;
        if (GetWindowRect(handle, out var current) && current.Equals(bounds)) return;
        if (!SetWindowPos(handle, fullscreen ? new nint(1) : new nint(-1), bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, NoActivate))
            throw new InvalidOperationException("无法将预览栏停靠到主屏底部。");
    }

    internal void Reposition() => QueuePosition();

    internal void SetCoverWindows(bool coversWindows)
    {
        CoversWindows = coversWindows;
        Reposition();
    }

    internal void SetHeight(int height)
    {
        if (heightDip == height) return;
        heightDip = height;
        Reposition();
    }

    private void QueuePosition(bool newShell = false)
    {
        shellRestarted |= newShell;
        if (queued) return;
        queued = dispatcher.TryEnqueue(() =>
        {
            queued = false;
            if (disposed) return;
            try
            {
                if (shellRestarted)
                {
                    shellRestarted = false;
                    registered = false;
                    proposed = null;
                    reserved = null;
                    Register();
                }
                Position();
            }
            catch (Exception error) { reportFailure(error); }
        });
    }

    private nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint unused)
    {
        if (!disposed)
        {
            if (message == 0x0021) beforeMouseActivation(); // WM_MOUSEACTIVATE
            else if (message == taskbarCreated) QueuePosition(newShell: true);
            else if (message == 0x007e || message == 0x02e0) QueuePosition(); // Display or DPI change
            else if (registered && message == notification)
            {
                if (wParam is 0 or 1) QueuePosition(); // State or position change
                else if (wParam == 2)
                {
                    fullscreen = lParam != 0;
                    SetWindowPos(handle, fullscreen ? new nint(1) : new nint(-1), 0, 0, 0, 0, NoMoveOrSize | NoActivate);
                }
            }
            else if (registered && message is 0x0006 or 0x0047)
            {
                var data = Data();
                SHAppBarMessage(message == 0x0006 ? Activate : WindowPositionChanged, ref data);
                if (message == 0x0047) QueuePosition();
            }
            else if (message == 0x0082) Dispose(); // Unregister before the HWND is destroyed.
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (registered)
        {
            var data = Data();
            SHAppBarMessage(Remove, ref data);
            registered = false;
        }
        RemoveWindowSubclass(handle, callback, 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint Callback;
        public uint Edge;
        public Native.Rect Rectangle;
        public nint Parameter;
    }

    private delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Native.Rect bounds);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
