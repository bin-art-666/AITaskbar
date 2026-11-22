using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;

namespace AITaskbar;

// Mirrors the notification messages that Shell_NotifyIcon sends to the taskbar. Explorer
// still receives the original messages and owns notifications and its normal tray state.
internal sealed class WindowsNotificationArea : IDisposable
{
    private const string TrayClass = "Shell_TrayWnd", ReceiverTitle = "AITaskbar notification receiver";
    private const uint CopyDataMessage = 0x004a;
    private const uint NotificationSignature = 0x34753423;
    private readonly Dictionary<(nint Window, uint Id), TrayIcon> icons = new();
    private readonly DispatcherQueue dispatcher;
    private readonly WindowProcedure procedure;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly nint module = GetModuleHandle(null);
    private nint receiver, explorer;
    private uint explorerProcess;
    private Mutex? ownership;
    private bool registered, changedQueued, disposed, registrationPending, needsRegistrationOnExit;
    internal event Action? Changed;
    internal bool Available => receiver != 0;
    internal IEnumerable<TrayIcon> Icons => icons.Values.Where(icon => icon.Image is not null &&
        !icon.Hidden && IsCurrentOwner(icon));

    internal WindowsNotificationArea(DispatcherQueue dispatcher)
    {
        this.dispatcher = dispatcher;
        procedure = Receive;
    }

    internal void Refresh()
    {
        if (disposed) return;
        foreach (var entry in icons.ToArray())
        {
            if (IsCurrentOwner(entry.Value)) continue;
            icons.Remove(entry.Key);
            QueueChanged();
        }

        var native = FindExplorerTray();
        if (native == 0 || GetWindowThreadProcessId(native, out uint owner) == 0) return;
        if (receiver == 0)
        {
            if (!Start()) return;
            QueueChanged();
        }
        registrationPending |= explorer != native || explorerProcess != owner;
        explorer = native;
        explorerProcess = owner;
        if (FindWindowEx(0, 0, TrayClass, null) != receiver)
            SetWindowPos(receiver, -1, 0, 0, 0, 0, 0x0013);
        if (registrationPending && FindWindowEx(0, 0, TrayClass, null) == receiver)
        {
            // The applications still own their icons, so live entries stay usable while
            // Explorer and the applications finish re-registering them.
            registrationPending = false;
            RequestRegistration(explorerProcess);
        }
    }

    private bool Start()
    {
        if (taskbarCreated == 0 || ownership is not null) return false;
        var mutex = new Mutex(false, @"Local\AITaskbar.NotificationArea");
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); return false; }
        ownership = mutex;
        var windowClass = new WindowClass { Procedure = procedure, Instance = module, ClassName = TrayClass };
        registered = RegisterClass(ref windowClass) != 0;
        if (registered)
        {
            // A hidden, nonactivating window participates in the Shell32 class lookup.
            const uint NoActivateToolWindowTopmost = 0x08000088, Popup = 0x80000000;
            receiver = CreateWindowEx(NoActivateToolWindowTopmost, TrayClass, ReceiverTitle, Popup,
                0, 0, 0, 0, 0, 0, module, 0);
        }
        if (receiver != 0) return true;
        Stop();
        return false;
    }

    internal static nint FindExplorerTray()
    {
        uint shell = 0;
        var desktop = GetShellWindow();
        if (desktop != 0) GetWindowThreadProcessId(desktop, out shell);
        var relay = shell == 0 ? FindWindowEx(0, 0, TrayClass, ReceiverTitle) : 0;
        nint window = 0;
        while ((window = FindWindowEx(0, window, TrayClass, null)) != 0)
        {
            if (shell != 0 && GetWindowThreadProcessId(window, out uint owner) != 0 && owner == shell) return window;
            if (shell == 0 && relay != window) return window;
        }
        return 0;
    }

    private void RequestRegistration(uint shell)
    {
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != shell && owner != Environment.ProcessId) PostMessage(window, taskbarCreated, 0, 0);
            return true;
        }, 0);
    }

    private nint Receive(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == taskbarCreated)
        {
            registrationPending = true;
            dispatcher.TryEnqueue(Refresh);
            return 0;
        }
        if (message != CopyDataMessage || lParam == 0 || disposed)
            return DefWindowProc(window, message, wParam, lParam);

        bool mirrored = false;
        var packet = Marshal.PtrToStructure<CopyData>(lParam);
        if (packet.Data != 0 && packet.Size >= 4 && ReadUInt(packet.Data, 0) == NotificationSignature)
        {
            if (packet.Kind == 1 && packet.Size >= 32)
                mirrored = UpdateIcon(packet.Data, packet.Size);
            else if (packet.Kind == 3 && packet.Size >= 40 && TryIconLocation(packet.Data, out nint location))
                return location;
        }
        var target = FindExplorerTray();
        if (target != 0 && target != window &&
            SendMessageTimeout(target, message, wParam, lParam, 0x22, 500, out var result) != 0 && result != 0)
            return (nint)result;
        if (mirrored) needsRegistrationOnExit = true;
        return mirrored ? 1 : 0;
    }

    private bool UpdateIcon(nint data, uint size)
    {
        // Shell32 normalizes HWND and HICON to 32-bit fields in this wire format.
        uint operation = ReadUInt(data, 4);
        if (operation is not (0 or 1 or 2 or 4)) return false;
        nint window = (nint)Marshal.ReadInt32(data, 12);
        uint id = ReadUInt(data, 16);
        uint flags = ReadUInt(data, 20);
        Guid guid = (flags & 32) != 0 && size >= 960 ? Marshal.PtrToStructure<Guid>(data + 944) : Guid.Empty;
        var key = (window, id);
        if (guid != Guid.Empty)
        {
            foreach (var entry in icons)
                if (entry.Value.Guid == guid) { key = entry.Key; break; }
        }
        if (operation == 2)
        {
            if (!icons.Remove(key)) return false;
            QueueChanged();
            return true;
        }
        if (window == 0 || GetWindowThreadProcessId(window, out uint owner) == 0 || owner == Environment.ProcessId)
            return false;
        if (owner == explorerProcess || IsSystemIcon(guid)) return false;

        if (!icons.TryGetValue(key, out var icon) || icon.Process != owner)
        {
            if (operation == 4) return false;
            icon = new(window, owner, id, guid);
            icons.Remove(key);
            icons[(window, id)] = icon;
        }
        else if (key != (window, id))
        {
            icons.Remove(key);
            icons[(window, id)] = icon;
            icon.Window = window;
            icon.Id = id;
        }
        if (guid != Guid.Empty) icon.Guid = guid;
        if (operation == 0)
        {
            icon.Version = 0;
            icon.SeenRegistration = true;
        }
        if (operation == 4)
        {
            if (size < 812) return false;
            uint version = ReadUInt(data, 808);
            if (version > 4) return false;
            icon.Version = version;
            return true;
        }
        if ((flags & 1) != 0) icon.Callback = ReadUInt(data, 24);
        if ((flags & 2) != 0)
        {
            nint image = (nint)Marshal.ReadInt32(data, 28);
            icon.Image = image == 0 ? null : ImageBytes(image) ?? icon.Image;
        }
        if ((flags & 4) != 0 && size > 32)
        {
            int chars = (int)Math.Min((size - 32) / 2, 128);
            icon.Title = (Marshal.PtrToStringUni(data + 32, chars) ?? "").Split('\0')[0];
        }
        if ((flags & 8) != 0 && size >= 296 && (ReadUInt(data, 292) & 1) != 0)
            icon.Hidden = (ReadUInt(data, 288) & 1) != 0;
        if (!icon.SeenRegistration && !icon.RegistrationRequested && (!icon.CanActivate || icon.Image is null))
            icon.RegistrationRequested = PostMessage(window, taskbarCreated, 0, 0);
        QueueChanged();
        return true;
    }

    private static bool IsSystemIcon(Guid guid)
    {
        if (guid == Guid.Empty) return false;
        string value = guid.ToString();
        // Explorer's built-in status icons are represented separately by AITaskbar.
        return value.StartsWith("7820ae", StringComparison.Ordinal) && value.EndsWith("-23e3-4229-82c1-e41cb67d5b9c", StringComparison.Ordinal);
    }

    private static byte[]? ImageBytes(nint handle)
    {
        var copy = handle == 0 ? 0 : CopyIcon(handle);
        if (copy == 0) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(copy);
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception error) when (error is ArgumentException or ExternalException) { return null; }
        finally { DestroyIcon(copy); }
    }

    private bool TryIconLocation(nint data, out nint point)
    {
        var identifier = Marshal.PtrToStructure<IconIdentifier>(data);
        point = 0;
        if (identifier.Query is not (1 or 2)) return false;
        var icon = identifier.Guid == Guid.Empty
            ? icons.GetValueOrDefault(((nint)unchecked((int)identifier.Window), identifier.Id))
            : icons.Values.FirstOrDefault(item => item.Guid == identifier.Guid);
        if (icon is null || icon.LastInteraction == 0 || Environment.TickCount64 - icon.LastInteraction > 3000 ||
            !IsCurrentOwner(icon)) return false;
        // The shell adds the packed extent from query 2 to the origin from query 1.
        point = identifier.Query == 1 ? (nint)PackPoint(icon.Left, icon.Top)
            : (nint)PackPoint(icon.Right - icon.Left, icon.Bottom - icon.Top);
        return true;
    }

    internal void Activate(TrayIcon icon, bool context, bool keyboard, int left, int top, int right, int bottom, int screenX, int screenY)
    {
        if (!icons.TryGetValue((icon.Window, icon.Id), out var current) || current != icon ||
            !IsCurrentOwner(icon) || icon.Callback < 0x400) return;
        icon.Left = left; icon.Top = top; icon.Right = right; icon.Bottom = bottom;
        long now = Environment.TickCount64;
        bool doubleClick = !keyboard && !context && icon.LastLeftClick != 0 &&
            now - icon.LastLeftClick <= GetDoubleClickTime();
        icon.LastInteraction = now;
        icon.LastLeftClick = !keyboard && !context && !doubleClick ? now : 0;
        AllowSetForegroundWindow(icon.Process);
        uint position = PackPoint(screenX, screenY);
        if (context)
        {
            Send(icon, 0x0204, position);
            Send(icon, 0x0205, position);
            if (icon.Version >= 3) Send(icon, 0x007b, position);
        }
        else if (keyboard && icon.Version >= 3)
        {
            Send(icon, 0x0401, position);
        }
        else
        {
            Send(icon, doubleClick ? 0x0203u : 0x0201u, position);
            Send(icon, 0x0202, position);
            if (icon.Version >= 3) Send(icon, 0x0400, position);
        }
    }

    private static void Send(TrayIcon icon, uint message, uint position) =>
        PostMessage(icon.Window, icon.Callback, icon.Version >= 4 ? position : icon.Id,
            (nint)(message | (icon.Version >= 4 ? icon.Id << 16 : 0)));

    private static uint PackPoint(int x, int y) => (uint)(ushort)x | (uint)(ushort)y << 16;
    private static uint ReadUInt(nint data, int offset) => unchecked((uint)Marshal.ReadInt32(data, offset));
    private static bool IsCurrentOwner(TrayIcon icon) =>
        GetWindowThreadProcessId(icon.Window, out uint current) != 0 && current == icon.Process;

    private void QueueChanged()
    {
        if (changedQueued) return;
        changedQueued = dispatcher.TryEnqueue(() => { changedQueued = false; if (!disposed) Changed?.Invoke(); });
    }

    private void Stop()
    {
        if (receiver != 0) DestroyWindow(receiver);
        receiver = 0;
        if (registered) UnregisterClass(TrayClass, module);
        registered = false;
        ownership?.ReleaseMutex();
        ownership?.Dispose();
        ownership = null;
    }

    public void Dispose()
    {
        disposed = true;
        Stop();
        var native = FindExplorerTray();
        if (needsRegistrationOnExit && native != 0 && GetWindowThreadProcessId(native, out uint shell) != 0)
            RequestRegistration(shell);
        icons.Clear();
    }

    internal sealed class TrayIcon(nint window, uint process, uint id, Guid guid)
    {
        internal nint Window = window;
        internal readonly uint Process = process;
        internal uint Id = id, Callback, Version;
        internal Guid Guid = guid;
        internal string Title = "";
        internal byte[]? Image;
        internal bool Hidden;
        internal bool SeenRegistration, RegistrationRequested;
        internal bool CanActivate => Callback >= 0x400;
        private string? applicationKey;
        internal string OrderKey => Guid != Guid.Empty ? $"guid:{Guid:D}"
            : $"{applicationKey ??= IdentifyApplication()}:{Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        internal long LastInteraction, LastLeftClick;
        internal int Left, Top, Right, Bottom;

        private string IdentifyApplication()
        {
            var (executable, appId) = RunningWindows.DescribeProcess(Process);
            if (appId is not null) return "app:" + appId;
            if (executable is not null) return "exe:" + executable;

            var windowClass = new StringBuilder(256);
            GetClassName(Window, windowClass, windowClass.Capacity);
            return "class:" + windowClass;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData { public nuint Kind; public uint Size; public nint Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier { public uint Magic, Query, Size, Padding, Window, Id; public Guid Guid; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    private delegate bool EnumWindowProcedure(nint window, nint parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string cls, string name,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint window, uint message,
        nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProcedure callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
