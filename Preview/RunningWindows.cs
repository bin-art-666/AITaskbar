using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AITaskbar;

internal sealed record RunningWindow(nint Handle, uint ProcessId, string Title, string? Executable,
    string? IconExecutable = null, string? IconAppId = null, string? PinAppId = null);

internal static class RunningWindows
{
    private const long AppWindow = 0x00040000, ToolWindow = 0x00000080, NoActivate = 0x08000000;
    private const uint QueryProcess = 0x1000, TerminateProcessAccess = 0x0001;
    private static readonly HashSet<string> sharedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ApplicationFrameHost.exe", "RuntimeBroker.exe", "dllhost.exe", "svchost.exe",
        "rundll32.exe", "conhost.exe", "OpenConsole.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "TextInputHost.exe"
    };
    private const ushort VariantWideString = 31;
    private const string PhotosAppId = "Microsoft.OneDrive.Photos_8wekyb3d8bbwe!OneDrive.App";
    private static readonly PropertyKey appUserModelId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    internal static readonly string SystemSettingsExecutable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel", "SystemSettings.exe");

    public static RunningWindow[] List()
    {
        var windows = new List<RunningWindow>();
        var processes = new Dictionary<uint, (string? Executable, string? AppId)>();
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
            if (!processes.TryGetValue(processId, out var process))
            {
                process = DescribeProcess(processId);
                processes.Add(processId, process);
            }
            // A hosted frame can carry an app identity that is separate from its owning process.
            var appId = process.AppId ?? (IsFrameHost(process.Executable) || IsPackagedExecutable(process.Executable)
                ? WindowAppId(handle) : null);
            bool settings = StringComparer.OrdinalIgnoreCase.Equals(appId, PackagedAppPin.SettingsId);
            var pinAppId = PackagedAppPin.Name(appId) is not null ? appId : null;
            var iconAppId = StringComparer.OrdinalIgnoreCase.Equals(appId, PhotosAppId) ||
                StringComparer.OrdinalIgnoreCase.Equals(appId, PackagedAppIcon.StoreAppId) ? appId : null;
            windows.Add(new(handle, processId, title, process.Executable,
                settings ? SystemSettingsExecutable : null, iconAppId, pinAppId));
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
        OwnsHandle(window) && IsWindowVisible(window.Handle);

    internal static bool OwnsHandle(RunningWindow window) =>
        GetWindowThreadProcessId(window.Handle, out var processId) != 0 && processId == window.ProcessId;

    internal static bool IsMinimized(RunningWindow window) => IsIconic(window.Handle);

    internal static bool Minimize(RunningWindow window) =>
        IsCurrent(window) && ShowWindowAsync(window.Handle, 6); // SW_MINIMIZE

    internal static bool RequestRestore(RunningWindow window) =>
        IsCurrent(window) && (!IsMinimized(window) || ShowWindowAsync(window.Handle, 9)); // SW_RESTORE

    internal static bool RequestClose(RunningWindow window) =>
        !OwnsHandle(window) || PostMessage(window.Handle, 0x0010, 0, 0); // WM_CLOSE

    internal static string? EndTaskUnavailable(RunningWindow window)
    {
        if (window.ProcessId == Environment.ProcessId || IsShellProcess(window.ProcessId) ||
            Path.GetFileName(window.Executable) is { } name && sharedExecutables.Contains(name))
            return "此窗口由系统或共享进程承载。";
        return window.Executable is null ? "无法确认应用进程。" : null;
    }

    internal static bool ConfirmEndTask(nint owner, string name) =>
        MessageBox(owner, $"将强制结束「{name}」的应用进程。未保存的内容可能丢失。", "结束任务",
            0x00000004 | 0x00000030 | 0x00000100) == 6; // Yes/No, warning, default No

    internal static bool EndTask(RunningWindow window)
    {
        if (EndTaskUnavailable(window) is not null) return false;
        using var process = OpenProcess(QueryProcess | TerminateProcessAccess, false, window.ProcessId);
        if (process.IsInvalid || !IsCurrent(window) ||
            !StringComparer.OrdinalIgnoreCase.Equals(ProcessImage(process), window.Executable) ||
            !IsProcessCritical(process, out bool critical) || critical) return false;
        return TerminateProcess(process, 1);
    }

    private static bool IsShellProcess(uint processId) =>
        GetWindowThreadProcessId(GetShellWindow(), out uint shellProcess) != 0 && shellProcess == processId;

    internal static bool HideForTransition(RunningWindow window, bool wasMinimized) =>
        IsCurrent(window) && IsMinimized(window) == wasMinimized && ShowWindowAsync(window.Handle, 0); // SW_HIDE

    internal static bool ShowAfterHide(RunningWindow window, bool minimized) =>
        OwnsHandle(window) && ShowWindowAsync(window.Handle, minimized ? 7 : 9); // SW_SHOWMINNOACTIVE / SW_RESTORE

    internal static void ReshowAfterInterruptedTransition(RunningWindow window)
    {
        if (OwnsHandle(window))
            ShowWindowAsync(window.Handle, IsMinimized(window) ? 7 : 8); // SW_SHOWMINNOACTIVE / SW_SHOWNA
    }

    public static nint Foreground()
    {
        var handle = AIEnter.Native.GetForegroundWindow();
        return AIEnter.Native.GetAncestor(handle, 3) is var owner && owner != 0 ? owner : handle;
    }

    public static bool CanPin(string? executable) => Path.IsPathFullyQualified(executable ?? "") &&
        string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) &&
        !IsFrameHost(executable) &&
        !IsPackagedExecutable(executable) && File.Exists(executable);

    internal static bool IsFrameHost(string? executable) =>
        string.Equals(Path.GetFileName(executable), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsPackagedExecutable(string? executable) =>
        executable?.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) == true;

    private static string? WindowAppId(nint handle)
    {
        if (SHGetPropertyStoreForWindow(handle, typeof(IPropertyStore).GUID, out var store) < 0 || store is null)
            return null;
        try
        {
            int result = store.GetValue(in appUserModelId, out var value);
            try { return result >= 0 && value.Type == VariantWideString ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static (string? Executable, string? AppId) DescribeProcess(uint processId)
    {
        using var process = OpenProcess(QueryProcess, false, processId);
        if (process.IsInvalid || ProcessImage(process) is not { } executable) return default;
        if (!IsPackagedExecutable(executable) &&
            !string.Equals(Path.GetFileName(executable), "OneDrive.App.exe", StringComparison.OrdinalIgnoreCase))
            return (executable, null);

        var appId = new StringBuilder(130);
        uint length = (uint)appId.Capacity;
        return (executable, GetApplicationUserModelId(process, ref length, appId) == 0 ? appId.ToString() : null);
    }

    private static string? ProcessImage(SafeProcessHandle process)
    {
        var path = new StringBuilder(32768);
        int size = path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(nint hwnd, string text, string caption, uint type);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder appId);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        private readonly Guid format = format;
        private readonly uint id = id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal nint Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropertyVariant value);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyVariant value);
}
