using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace AIEnter;

// Own explicit, DPI-sized HWND icons for the running-app taskbar button.
internal sealed class RunningWindowIcon : IDisposable
{
    readonly nint hwnd;
    nint large, small;
    uint dpi;
    internal RunningWindowIcon(Window window) => hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
    internal void Apply(bool force = false)
    {
        uint nextDpi = Math.Max(96, Native.GetDpiForWindow(hwnd));
        if (!force && dpi == nextDpi && large != 0) return;
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "AIEnter.Window.ico");
        nint nextLarge = LoadImage(0, path, 1, GetSystemMetricsForDpi(11, nextDpi), GetSystemMetricsForDpi(12, nextDpi), 0x10);
        nint nextSmall = LoadImage(0, path, 1, GetSystemMetricsForDpi(49, nextDpi), GetSystemMetricsForDpi(50, nextDpi), 0x10);
        if (nextLarge == 0 || nextSmall == 0)
        {
            if (nextLarge != 0) DestroyIcon(nextLarge);
            if (nextSmall != 0) DestroyIcon(nextSmall);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to load running window icons.");
        }
        SendMessage(hwnd, 0x80, 1, nextLarge);
        SendMessage(hwnd, 0x80, 0, nextSmall);
        if (large != 0) DestroyIcon(large);
        if (small != 0) DestroyIcon(small);
        large = nextLarge; small = nextSmall; dpi = nextDpi;
    }
    public void Dispose()
    {
        if (large != 0) DestroyIcon(large);
        if (small != 0) DestroyIcon(small);
        large = small = 0;
    }
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint LoadImage(nint instance, string file, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
}
