using System.Runtime.InteropServices;
using System.Text;

namespace AITaskbar;

internal sealed record RunningWindow(nint Handle, uint ProcessId, string Title, string? Executable,
    string? IconExecutable = null, string? IconAppId = null, string? PinAppId = null);

internal static class RunningWindows
{
    private const long AppWindow = 0x00040000, ToolWindow = 0x00000080, NoActivate = 0x08000000;
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
            // A hosted Settings frame carries the app's identity even when the host owns its child windows.
            var appId = process.AppId ?? (IsFrameHost(process.Executable) || IsPackagedExecutable(process.Executable)
                ? WindowAppId(handle) : null);
            bool settings = StringComparer.OrdinalIgnoreCase.Equals(appId, PackagedAppPin.SettingsId);
            var pinAppId = PackagedAppPin.Name(appId) is not null ? appId : null;
            var iconAppId = StringComparer.OrdinalIgnoreCase.Equals(appId, PhotosAppId) ? PhotosAppId : null;
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
        GetWindowThreadProcessId(window.Handle, out var processId) != 0
        && processId == window.ProcessId && IsWindowVisible(window.Handle);

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
        nint process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0) return default;
        try
        {
            var path = new StringBuilder(32768);
            int size = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref size)) return default;
            string executable = path.ToString();
            if (!IsPackagedExecutable(executable) &&
                !string.Equals(Path.GetFileName(executable), "OneDrive.App.exe", StringComparison.OrdinalIgnoreCase))
                return (executable, null);

            var appId = new StringBuilder(130);
            uint length = (uint)appId.Capacity;
            return (executable, GetApplicationUserModelId(process, ref length, appId) == 0 ? appId.ToString() : null);
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder appId);

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
