using System.Runtime.InteropServices;

namespace AIEnter;

// Keep native window sizing without a second, painted non-client border.
internal sealed class AiResizeFrame : IDisposable
{
    readonly IntPtr window;
    readonly Func<bool> enabled;
    readonly Action frameChanged;
    readonly Func<Microsoft.UI.Xaml.CornerRadius> corners;
    readonly Procedure procedure;
    static readonly UIntPtr Id = new(0x41495246);
    bool disposed;
    delegate IntPtr Procedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    internal AiResizeFrame(IntPtr window, Func<bool> enabled, Action frameChanged, Func<Microsoft.UI.Xaml.CornerRadius> corners)
    {
        this.window = window; this.corners = corners; this.enabled = enabled; this.frameChanged = frameChanged; procedure = Handle;
        if (!SetWindowSubclass(window, procedure, Id, UIntPtr.Zero)) throw new InvalidOperationException("无法初始化 AI 窗口边框。");
        SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
    }
    IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (!disposed)
        {
            if (message is 0x007D or 0x02E0 or 0x031E) frameChanged(); // style, DPI, DWM composition changes
            if (message == 0x0083 && wParam != IntPtr.Zero) return IntPtr.Zero; // WM_NCCALCSIZE: client fills window.
            // WinUI may consume a non-client pointer after the custom hit test.
            // Let the OS sizing loop handle resize presses directly.
            if (message == 0x00A1 && enabled() && wParam.ToInt64() is >= 10 and <= 17)
                return DefWindowProc(hwnd, 0x0112, new IntPtr(0xF000 + wParam.ToInt64() - 9), lParam); // SC_SIZE + WMSZ edge
            if (message == 0x0084 && enabled() && GetWindowRect(hwnd, out var rect))
            {
                long packed = lParam.ToInt64();
                int x = (short)(packed & 0xFFFF) - rect.Left, y = (short)((packed >> 16) & 0xFFFF) - rect.Top;
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                int grip = Math.Max(4, (int)Math.Round(5 * Native.GetDpiForWindow(hwnd) / 96d));
                if (x >= 0 && y >= 0 && x < width && y < height)
                {
                    // Rectangular 5px corner intersections are outside a rounded HRGN.
                    // Hit the visible arc itself, without turning header buttons into grips.
                    var radii = corners();
                    double scale = Native.GetDpiForWindow(hwnd) / 96d;
                    var cornerHits = new[] { (x, y, radii.TopLeft, 13), (width - 1 - x, y, radii.TopRight, 14),
                        (x, height - 1 - y, radii.BottomLeft, 16), (width - 1 - x, height - 1 - y, radii.BottomRight, 17) };
                    foreach (var (cx, cy, value, cornerHit) in cornerHits)
                    {
                        double radius = Math.Min(value * scale, Math.Min(width, height) / 2d);
                        double reach = Math.Max(grip * 2, radius + grip);
                        if (cx < reach && cy < reach && (radius <= grip ||
                            Math.Pow(cx - radius, 2) + Math.Pow(cy - radius, 2) >= Math.Pow(Math.Max(0, radius - grip * 1.5), 2)))
                            return new(cornerHit);
                    }
                    bool left = x < grip, right = x >= width - grip, top = y < grip, bottom = y >= height - grip;
                    int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
                    if (hit != 0) return new(hit);
                }
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    internal void VerifyEdges()
    {
        if ((GetWindowLongPtr(window, -16).ToInt64() & (0x00C00000 | 0x00040000)) != 0)
            throw new Exception("Expanded window restored an OS-painted caption or sizing frame.");
        GetWindowRect(window, out var rect);
        double scale = Native.GetDpiForWindow(window) / 96d;
        var radius = corners();
        int Arc(double r) => Math.Max(2, (int)Math.Ceiling(r * scale * (1 - Math.Sqrt(.5))) + 1);
        var tl = Arc(radius.TopLeft); var tr = Arc(radius.TopRight);
        var bl = Arc(radius.BottomLeft); var br = Arc(radius.BottomRight);
        foreach (var point in new[] { (rect.Left + 1, (rect.Top + rect.Bottom) / 2, 10),
            (rect.Right - 2, (rect.Top + rect.Bottom) / 2, 11),
            ((rect.Left + rect.Right) / 2, rect.Top + 1, 12),
            ((rect.Left + rect.Right) / 2, rect.Bottom - 2, 15),
            (rect.Left + tl, rect.Top + tl, 13), (rect.Right - 1 - tr, rect.Top + tr, 14),
            (rect.Left + bl, rect.Bottom - 1 - bl, 16), (rect.Right - 1 - br, rect.Bottom - 1 - br, 17) })
        {
            var packed = new IntPtr(unchecked((int)((uint)(ushort)point.Item1 | ((uint)(ushort)point.Item2 << 16))));
            if (SendMessageTimeout(window, 0x0084, IntPtr.Zero, packed, 2, 1000, out var hit) == IntPtr.Zero || hit.ToInt32() != point.Item3)
                throw new Exception("Expanded window lost its native resize edge.");
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; RemoveWindowSubclass(window, procedure, Id);
    }
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, Procedure callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr hwnd, Procedure callback, UIntPtr id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Native.Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
