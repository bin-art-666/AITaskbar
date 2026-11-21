using System.Runtime.InteropServices;
using System.Text;

namespace AITaskbar;

internal sealed record RunningWindow(nint Handle, uint ProcessId, string Title, string? Executable);

internal static class RunningWindows
{
    private const long AppWindow = 0x00040000, ToolWindow = 0x00000080, NoActivate = 0x08000000;

    public static RunningWindow[] List()
    {
        var windows = new List<RunningWindow>();
        var paths = new Dictionary<uint, string?>();
        var desktop = GetShellWindow();
        EnumWindows((handle, _) =>
        {
            if (handle == desktop || !IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || processId == Environment.ProcessId) return true;
            long style = GetWindowLongPtr(handle, -20).ToInt64();
            if ((style & AppWindow) == 0 &&
                ((style & (ToolWindow | NoActivate)) != 0 || GetWindow(handle, 4) != 0)) return true;
            if (DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            var text = new StringBuilder(1024);
            if (GetWindowText(handle, text, text.Capacity) == 0) return true;
            string title = text.ToString().Trim();
            if (title.Length == 0) return true;
            if (!paths.TryGetValue(processId, out var path))
            {
                path = ExecutablePath(processId);
                paths.Add(processId, path);
            }
            windows.Add(new(handle, processId, title, path));
            return true;
        }, 0);
        // Keep button positions stable when foreground windows change z-order.
        return windows.OrderBy(window => window.ProcessId).ThenBy(window => window.Handle.ToInt64()).ToArray();
    }

    public static bool Activate(RunningWindow window)
    {
        if (!IsCurrent(window)) return false;
        if (IsIconic(window.Handle)) ShowWindowAsync(window.Handle, 9); // SW_RESTORE
        var popup = GetLastActivePopup(window.Handle);
        return SetForegroundWindow(popup != 0 && IsWindowVisible(popup) ? popup : window.Handle);
    }

    public static bool IsCurrent(RunningWindow window) =>
        GetWindowThreadProcessId(window.Handle, out var processId) != 0
        && processId == window.ProcessId && IsWindowVisible(window.Handle);

    public static nint Foreground()
    {
        var handle = AIEnter.Native.GetForegroundWindow();
        return AIEnter.Native.GetAncestor(handle, 3) is var owner && owner != 0 ? owner : handle;
    }

    public static bool CanPin(string? executable) => Path.IsPathFullyQualified(executable ?? "") &&
        string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Path.GetFileName(executable), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) &&
        !executable!.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) && File.Exists(executable);

    private static string? ExecutablePath(uint processId)
    {
        nint process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0) return null;
        try
        {
            var path = new StringBuilder(32768);
            int size = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    private delegate bool WindowEnumerator(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnumerator callback, nint data);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint id);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int result, int size);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);
}
