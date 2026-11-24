using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AITaskbar;

internal sealed class ShowDesktopWindows
{
    private const uint KeepSizeAndPosition = 0x0003, NoActivate = 0x0010, NoOwnerZOrder = 0x0200, AsyncPosition = 0x4000;
    private const int ExtendedStyle = -20, Topmost = 0x00000008;
    private const uint NextWindow = 2;
    private RunningWindow[] minimized = [];
    private RunningWindow? foreground;

    internal async Task ToggleAsync(nint foregroundAtPress)
    {
        var restore = minimized.Where(window => RunningWindows.IsCurrent(window) && RunningWindows.IsMinimized(window)).ToArray();
        if (restore.Length == 0)
        {
            var windows = RunningWindows.List(preserveZOrder: true).Where(window => !RunningWindows.IsMinimized(window)).ToArray();
            foreground = Array.Find(windows, window => RunningWindows.MatchesForeground(window, foregroundAtPress));
            if (windows.Length == 0) return;
            var background = windows.Skip(1).Reverse().Where(MinimizeWindow).ToArray();
            // Keep the front visible until the background has had a chance to minimize.
            await WaitForWindowStateAsync(background, minimizedState: true, timeoutMs: 160);
            var front = windows[0];
            minimized = MinimizeWindow(front)
                ? [front, .. background.Reverse()] : background.Reverse().ToArray();
            await WaitForWindowStateAsync(minimized, minimizedState: true);
            return;
        }

        // Foreground permission belongs to the taskbar click. Use it before background apps restore.
        var activate = restore.Contains(foreground) ? foreground : foreground is null ? restore[0] : null;
        if (activate is not null && RestoreWindow(activate, after: 0) &&
            await WaitForWindowStateAsync([activate], minimizedState: false))
        {
            RunningWindows.Activate(activate);
        }

        foreach (var (window, after) in InStackOrder(restore))
        {
            if (window != activate && RunningWindows.IsMinimized(window)) RestoreWindow(window, after);
            else QueuePosition(window, after);
        }
        await WaitForWindowStateAsync(restore, minimizedState: false);
        await ConfirmStackOrderAsync(restore);
        minimized = restore.Where(window => RunningWindows.OwnsHandle(window) && RunningWindows.IsMinimized(window)).ToArray();
        if (minimized.Length == 0) foreground = null;
    }

    private static async Task<bool> WaitForWindowStateAsync(RunningWindow[] windows, bool minimizedState, int timeoutMs = 400)
    {
        var started = Stopwatch.GetTimestamp();
        while (windows.Any(window => RunningWindows.OwnsHandle(window) &&
            (!RunningWindows.IsCurrent(window) || RunningWindows.IsMinimized(window) != minimizedState)))
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= timeoutMs) return false;
            await Task.Delay(16);
        }
        return true;
    }

    private static bool RestoreWindow(RunningWindow window, nint after)
    {
        // Queue hide, position and show together so the target can restore without a visible zoom from its icon.
        bool hidden = RunningWindows.HideForTransition(window, wasMinimized: true);
        QueuePosition(window, after);
        bool restored = hidden ? RunningWindows.ShowAfterHide(window, minimized: false, activate: false)
            : RunningWindows.RequestRestore(window, activate: false);
        if (restored) QueuePosition(window, after);
        else if (hidden) RunningWindows.ShowAfterHide(window, minimized: true);
        return restored;
    }

    private static bool MinimizeWindow(RunningWindow window)
    {
        if (!RunningWindows.HideForTransition(window, wasMinimized: false)) return RunningWindows.Minimize(window, activateNext: false);
        if (RunningWindows.ShowAfterHide(window, minimized: true)) return true;
        RunningWindows.ReshowAfterInterruptedTransition(window);
        return false;
    }

    private static void QueuePosition(RunningWindow window, nint after) =>
        SetWindowPos(window.Handle, after, 0, 0, 0, 0, KeepSizeAndPosition | NoActivate | NoOwnerZOrder | AsyncPosition);

    private static IEnumerable<(RunningWindow Window, nint After)> InStackOrder(IEnumerable<RunningWindow> windows)
    {
        nint previousTopmost = 0, previousNormal = 0;
        foreach (var window in windows)
        {
            if (!RunningWindows.OwnsHandle(window)) continue;
            bool topmost = (GetWindowLongW(window.Handle, ExtendedStyle) & Topmost) != 0;
            yield return (window, topmost ? previousTopmost : previousNormal);
            if (topmost) previousTopmost = window.Handle;
            else previousNormal = window.Handle;
        }
    }

    private static async Task ConfirmStackOrderAsync(RunningWindow[] restore)
    {
        // Restore messages run in independent app queues. Check the actual stack after those messages.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var visible = restore.Where(window => RunningWindows.IsCurrent(window) && !RunningWindows.IsMinimized(window)).ToArray();
            if (MatchesStackOrder(visible)) return;
            foreach (var (window, after) in InStackOrder(visible)) QueuePosition(window, after);
            await Task.Delay(16);
        }
    }

    private static bool MatchesStackOrder(RunningWindow[] windows)
    {
        if (windows.Length < 2) return true;
        var targets = windows.Select(window => window.Handle).ToHashSet();
        var visited = new HashSet<nint>();
        int current = 0;
        for (nint handle = GetTopWindow(0); handle != 0 && current < windows.Length && visited.Add(handle);
            handle = GetWindow(handle, NextWindow))
        {
            if (!targets.Contains(handle)) continue;
            if (handle != windows[current++].Handle) return false;
        }
        return current == windows.Length;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
}
