using System.Runtime.InteropServices;

namespace AITaskbar;

internal static class WindowsTaskView
{
    private const ushort Tab = 0x09, Shift = 0x10, Control = 0x11, Alt = 0x12, LeftWin = 0x5B, RightWin = 0x5C;
    private const uint Keyboard = 1, ExtendedKey = 0x0001, KeyUp = 0x0002;
    private static readonly int InputSize = Marshal.SizeOf<Input>();

    internal static bool TryOpen()
    {
        if (IsDown(Tab) || IsDown(Shift) || IsDown(Control) || IsDown(Alt) || IsDown(LeftWin) || IsDown(RightWin))
            return false;

        Input[] shortcut = [Key(LeftWin, ExtendedKey), Key(Tab), Key(Tab, KeyUp), Key(LeftWin, ExtendedKey | KeyUp)];
        uint sent = SendInput((uint)shortcut.Length, shortcut, InputSize);
        if (sent == shortcut.Length) return true;
        if (sent > 0)
        {
            // Release any keys pressed by the partial sequence without sending another key-down.
            Input[] releases = sent == 2 ? shortcut[2..] : shortcut[3..];
            SendInput((uint)releases.Length, releases, InputSize);
        }
        return false;
    }

    private static bool IsDown(ushort key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static Input Key(ushort key, uint flags = 0) => new()
    {
        Type = Keyboard,
        Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse; // Determines the size of the native INPUT union.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey, ScanCode;
        internal uint Flags, Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal nuint ExtraInfo;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int key);
}
