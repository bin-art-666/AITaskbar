using System.Runtime.InteropServices;

namespace AIEnter;

internal sealed class GlobalHotkey : IDisposable
{
    const int SubclassId = 0x4149, WmHotkey = 0x0312, WmRemove = 0x0082;
    const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;
    static readonly SubclassProc Proc = WindowProc;
    static readonly object Gate = new();
    static GlobalHotkey? active;
    static int nextId = 0x4200;
    readonly nint hwnd;
    readonly Action pressed;
    readonly int id = Interlocked.Increment(ref nextId);
    readonly string key;
    uint modifiers, virtualKey;
    bool registered, disposed;

    GlobalHotkey(nint hwnd, string key, Action pressed) { this.hwnd = hwnd; this.key = key; this.pressed = pressed; }

    internal static GlobalHotkey? Prepare(nint hwnd, string value, Action pressed)
    {
        if (!HotkeyPolicy.TryNormalize(value, out string key)) throw new InvalidDataException("快捷键格式无效。");
        if (key.Length == 0) return null;
        var candidate = new GlobalHotkey(hwnd, key, pressed);
        candidate.Parse(key);
        lock (Gate)
        {
            if (active is null && !SetWindowSubclass(hwnd, Proc, (nuint)SubclassId, 0))
                throw new InvalidOperationException("无法安装快捷键消息处理器。");
            if (!RegisterHotKey(hwnd, candidate.id, candidate.modifiers | ModNoRepeat, candidate.virtualKey))
            {
                if (active is null) RemoveWindowSubclass(hwnd, Proc, (nuint)SubclassId);
                throw new InvalidOperationException("快捷键已被其他程序占用，或此组合不受 Windows 允许。");
            }
            candidate.registered = true;
        }
        return candidate;
    }

    internal void Commit()
    {
        lock (Gate)
        {
            if (disposed || !registered) return;
            var previous = active;
            active = this;
            if (previous is not null && previous != this) previous.ReleaseRegistration();
        }
    }

    void Parse(string key)
    {
        foreach (string part in key.Split('+')[..^1]) modifiers |= part switch
        { "Ctrl" => ModControl, "Alt" => ModAlt, "Shift" => ModShift, "Win" => ModWin, _ => 0 };
        string name = key.Split('+')[^1];
        virtualKey = name switch
        {
            "SPACE" => 0x20, "TAB" => 0x09, "ESC" => 0x1B, "ENTER" => 0x0D, "INSERT" => 0x2D, "DELETE" => 0x2E,
            "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            _ when name.Length > 1 && name[0] == 'F' => (uint)(0x70 + int.Parse(name[1..]) - 1),
            _ => name[0]
        };
    }

    internal static GlobalHotkey? Replace(nint hwnd, string key, Action pressed)
    {
        var next = Prepare(hwnd, key, pressed);
        next?.Commit();
        if (next is null) lock (Gate) { active?.ReleaseRegistration(); active = null; }
        return next;
    }

    static nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint subclassId, nuint data)
    {
        if (msg == WmHotkey)
        {
            Action? action; lock (Gate) action = active?.id == (int)wParam ? active.pressed : null;
            action?.Invoke(); return 0;
        }
        if (msg == WmRemove)
        {
            lock (Gate) { active?.ReleaseRegistration(); active = null; }
            RemoveWindowSubclass(hwnd, Proc, (nuint)SubclassId);
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    void ReleaseRegistration()
    {
        if (!registered) return;
        UnregisterHotKey(hwnd, id); registered = false;
    }
    public void Dispose()
    {
        lock (Gate)
        {
            if (disposed) return; disposed = true; ReleaseRegistration();
            if (active == this) active = null;
            if (active is null) RemoveWindowSubclass(hwnd, Proc, (nuint)SubclassId);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint subclassId, nuint data);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("comctl32.dll", SetLastError = true)] static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);
}
