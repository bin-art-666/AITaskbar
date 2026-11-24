using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AITaskbar;

internal sealed class ShowDesktopWindows
{
    private const uint KeepSizeAndPosition = 0x0003, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    private const int ExtendedStyle = -20, Topmost = 0x00000008;
    private RunningWindow[] minimized = [];
    private RunningWindow? foreground;

    internal async Task ToggleAsync(nint foregroundAtPress)
    {
        var restore = minimized.Where(window => RunningWindows.IsCurrent(window) && RunningWindows.IsMinimized(window)).ToArray();
        if (restore.Length == 0)
        {
            var windows = RunningWindows.List(preserveZOrder: true).Where(window => !RunningWindows.IsMinimized(window)).ToArray();
            foreground = Array.Find(windows, window => RunningWindows.MatchesForeground(window, foregroundAtPress));
            minimized = windows.Where(RunningWindows.Minimize).ToArray();
            await WaitForWindowStateAsync(minimized, minimizedState: true);
            return;
        }

        // Position the windows while they are still minimized, then uncover them from front to back.
        await Task.Run(() => ArrangeMinimizedWindows(restore));
        var activate = restore.Contains(foreground) ? foreground : foreground is null ? restore[0] : null;
        var failed = new List<RunningWindow>();
        foreach (var window in restore)
        {
            if (!RunningWindows.IsCurrent(window) || !RunningWindows.IsMinimized(window)) continue;
            if (!RunningWindows.RequestRestore(window, activate: false) ||
                !await WaitForWindowStateAsync([window], minimizedState: false))
            {
                if (RunningWindows.IsCurrent(window) && RunningWindows.IsMinimized(window)) failed.Add(window);
            }
        }
        minimized = failed.ToArray();
        if (activate is not null && RunningWindows.IsCurrent(activate) && !RunningWindows.IsMinimized(activate))
            RunningWindows.Activate(activate);
        if (minimized.Length == 0) foreground = null;
    }

    private static async Task<bool> WaitForWindowStateAsync(RunningWindow[] windows, bool minimizedState)
    {
        var started = Stopwatch.GetTimestamp();
        while (windows.Any(window => RunningWindows.IsCurrent(window) && RunningWindows.IsMinimized(window) != minimizedState))
        {
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(400)) return false;
            await Task.Delay(16);
        }
        return true;
    }

    private static void ArrangeMinimizedWindows(RunningWindow[] windows)
    {
        nint batch = BeginDeferWindowPos(windows.Length);
        if (batch == 0) return;
        nint previousTopmost = 0, previousNormal = 0; // HWND_TOP in each window's existing band.
        foreach (var window in windows)
        {
            if (!RunningWindows.IsCurrent(window) || !RunningWindows.IsMinimized(window)) continue;
            bool topmost = (GetWindowLongW(window.Handle, ExtendedStyle) & Topmost) != 0;
            ref nint previous = ref (topmost ? ref previousTopmost : ref previousNormal);
            batch = DeferWindowPos(batch, window.Handle, previous, 0, 0, 0, 0,
                KeepSizeAndPosition | NoActivate | NoOwnerZOrder);
            if (batch == 0) return;
            previous = window.Handle;
        }
        EndDeferWindowPos(batch);
    }

    [DllImport("user32.dll")] private static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] private static extern nint DeferWindowPos(nint batch, nint window, nint after,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(nint batch);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint window, int index);
}
