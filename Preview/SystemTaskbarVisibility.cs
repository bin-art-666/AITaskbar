using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AITaskbar;

internal sealed class SystemTaskbarVisibility(string root) : IDisposable
{
    private const uint GetState = 4, SetState = 10, AutoHide = 1;
    private const int HideWindow = 0, ShowWithoutActivating = 8;
    private EventWaitHandle? restored;
    private Snapshot? hidden;
    private bool toggling, disposed;
    private readonly string restoreFile = Path.Combine(root, "system-taskbar-restore-state.txt");

    internal bool? Visible
    {
        get
        {
            var primary = Primary();
            return primary == 0 ? null : hidden is null && IsWindowVisible(primary);
        }
    }

    internal bool CanToggle => !toggling && !disposed && Primary() != 0;

    internal void KeepHidden()
    {
        if (hidden is not { } snapshot) return;
        // Explorer can re-show its auto-hide strip after applying the shell state change.
        foreach (var window in snapshot.Windows)
            if (IsWindowVisible(window)) ShowWindowAsync(window, HideWindow);
    }

    internal async Task<bool> SetVisibleAsync(bool visible)
    {
        if (!CanToggle) return false;
        toggling = true;
        try
        {
            var primary = Primary();
            if (primary == 0) return false;
            if (hidden is not null)
            {
                if (visible) RestoreOwned();
                return true;
            }
            uint state = State(primary);
            uint? pending = ReadPendingState();
            // A journal is only relevant while the shell still has the state we applied.
            if (pending.HasValue && state != (pending.Value | AutoHide))
            {
                File.Delete(restoreFile);
                pending = null;
            }
            if (visible)
            {
                if (pending.HasValue)
                {
                    if (Restore(new Snapshot(pending.Value, [primary]))) File.Delete(restoreFile);
                }
                else if (!IsWindowVisible(primary)) ShowWindowAsync(primary, ShowWithoutActivating);
                return true;
            }
            if (!pending.HasValue && !IsWindowVisible(primary)) return true;
            var snapshot = new Snapshot(pending ?? state, VisibleWindows().Append(primary).Distinct().ToArray());
            string token = Guid.NewGuid().ToString("N");
            using var ready = OpenSignal(token, "Ready", create: true);
            restored = OpenSignal(token, "Restored", create: true);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var value in new[] { "--watch-system-taskbar", Environment.ProcessId.ToString(),
                token, snapshot.State.ToString(), string.Join(',', snapshot.Windows.Select(hwnd => hwnd.ToInt64())) })
                start.ArgumentList.Add(value);
            try
            {
                using var guard = Process.Start(start) ?? throw new InvalidOperationException("无法启动系统任务栏恢复进程。");
                if (!await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(5))) || guard.HasExited)
                    throw new InvalidOperationException("系统任务栏恢复进程未就绪。");
                if (disposed) return false;
                if (Primary() != primary) throw new InvalidOperationException("系统任务栏已重新启动，请重试。");
                // Record the original state before changing a shell setting that can survive reboot.
                if (!pending.HasValue && (snapshot.State & AutoHide) == 0) SavePendingState(snapshot.State);
                hidden = snapshot;
                if ((state & AutoHide) == 0) UpdateState(primary, state | AutoHide);
                foreach (var window in snapshot.Windows) ShowWindowAsync(window, HideWindow);
                return true;
            }
            finally
            {
                if (hidden is null) EndWatch();
            }
        }
        finally { toggling = false; }
    }

    private void RestoreOwned()
    {
        if (hidden is not { } snapshot) return;
        bool restoredState = Restore(snapshot);
        hidden = null;
        EndWatch();
        if (restoredState && (snapshot.State & AutoHide) == 0)
        {
            try { File.Delete(restoreFile); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private uint? ReadPendingState()
    {
        try
        {
            return uint.TryParse(File.ReadAllText(restoreFile), NumberStyles.None, CultureInfo.InvariantCulture, out uint state)
                && state <= 3 && (state & AutoHide) == 0 ? state : null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private void SavePendingState(uint state)
    {
        Directory.CreateDirectory(root);
        string temporary = restoreFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, state.ToString(CultureInfo.InvariantCulture));
            File.Move(temporary, restoreFile, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private void EndWatch()
    {
        restored?.Set();
        restored?.Dispose();
        restored = null;
    }

    public void Dispose()
    {
        disposed = true;
        RestoreOwned();
    }

    internal static async Task WatchAsync(string[] arguments)
    {
        if (arguments.Length != 6 || !int.TryParse(arguments[2], out int ownerId) ||
            !Guid.TryParseExact(arguments[3], "N", out _) || !uint.TryParse(arguments[4], out uint state) ||
            state > 3) return;
        var windows = new List<nint>();
        foreach (var value in arguments[5].Split(','))
        {
            if (!long.TryParse(value, out long window) || window == 0) return;
            windows.Add(new nint(window));
        }
        try
        {
            using var owner = Process.GetProcessById(ownerId);
            using var self = Process.GetCurrentProcess();
            if (owner.SessionId != self.SessionId || !StringComparer.OrdinalIgnoreCase.Equals(owner.MainModule?.FileName, Environment.ProcessPath))
                return;
            using var ready = OpenSignal(arguments[3], "Ready", create: false);
            using var restored = OpenSignal(arguments[3], "Restored", create: false);
            var stopped = owner.WaitForExitAsync();
            var closed = Task.Run(() => restored.WaitOne());
            ready.Set();
            if (await Task.WhenAny(stopped, closed) == stopped && !restored.WaitOne(0))
                Restore(new Snapshot(state, windows));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or WaitHandleCannotBeOpenedException) { }
    }

    private static bool Restore(Snapshot snapshot)
    {
        var primary = Primary();
        if (primary == 0) return false;
        if ((snapshot.State & AutoHide) == 0 && State(primary) == (snapshot.State | AutoHide))
            UpdateState(primary, snapshot.State);
        foreach (var window in VisibleOrHiddenWindows())
            if (snapshot.Windows.Contains(window)) ShowWindowAsync(window, ShowWithoutActivating);
        return (snapshot.State & AutoHide) != 0 || State(primary) != (snapshot.State | AutoHide);
    }

    private static EventWaitHandle OpenSignal(string token, string name, bool create)
    {
        var path = $@"Local\AITaskbar.SystemTaskbar.{token}.{name}";
        return create ? new(false, EventResetMode.ManualReset, path) : EventWaitHandle.OpenExisting(path);
    }

    private static nint Primary() => FindWindowEx(0, 0, "Shell_TrayWnd", null);
    private static IEnumerable<nint> VisibleWindows() => VisibleOrHiddenWindows().Where(IsWindowVisible);

    private static IEnumerable<nint> VisibleOrHiddenWindows()
    {
        var primary = Primary();
        if (primary == 0) yield break;
        yield return primary;
        nint next = 0;
        while ((next = FindWindowEx(0, next, "Shell_SecondaryTrayWnd", null)) != 0) yield return next;
    }

    private static uint State(nint window)
    {
        var data = new AppBarState { Size = (uint)Marshal.SizeOf<AppBarState>(), Window = window };
        return (uint)SHAppBarMessage(GetState, ref data);
    }

    private static void UpdateState(nint window, uint state)
    {
        var data = new AppBarState { Size = (uint)Marshal.SizeOf<AppBarState>(), Window = window, Parameter = (nint)state };
        SHAppBarMessage(SetState, ref data);
    }

    private sealed record Snapshot(uint State, IReadOnlyList<nint> Windows);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarState
    {
        public uint Size;
        public nint Window;
        public uint Callback, Edge;
        public AIEnter.Native.Rect Rectangle;
        public nint Parameter;
    }

    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, ref AppBarState state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string type, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
}
