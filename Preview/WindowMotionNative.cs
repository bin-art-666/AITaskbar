using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Native = AIEnter.Native;

namespace AITaskbar;

internal static class WindowMotionNative
{
    internal static Rect? ScreenBounds(nint owner, FrameworkElement icon)
    {
        if (icon.XamlRoot is not { } root || !icon.IsLoaded || icon.ActualWidth <= 0) return null;
        var bounds = icon.TransformToVisual(root.Content).TransformBounds(new Rect(0, 0, icon.ActualWidth, icon.ActualHeight));
        double scale = root.RasterizationScale;
        var origin = new Native.Point { X = (int)Math.Round(bounds.X * scale), Y = (int)Math.Round(bounds.Y * scale) };
        return ClientToScreen(owner, ref origin)
            ? new Rect(origin.X, origin.Y, bounds.Width * scale, bounds.Height * scale) : null;
    }

    internal static Rect? WindowBounds(RunningWindow window)
    {
        if (!RunningWindows.IsCurrent(window) ||
            DwmGetWindowAttribute(window.Handle, 9, out Native.Rect frame, Marshal.SizeOf<Native.Rect>()) < 0 ||
            frame.Right <= frame.Left || frame.Bottom <= frame.Top) return null;
        return new Rect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
    }

    internal static Snapshot? Capture(RunningWindow window)
    {
        if (!RunningWindows.IsCurrent(window) || IsHungAppWindow(window.Handle) ||
            GetWindowDisplayAffinity(window.Handle, out var affinity) && affinity != 0 ||
            !GetWindowRect(window.Handle, out var raw)) return null;
        bool minimized = RunningWindows.IsMinimized(window);
        if (minimized)
        {
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetWindowPlacement(window.Handle, ref placement) ||
                !GetMonitorInfo(MonitorFromWindow(window.Handle, 2), ref monitor)) return null;
            raw = (placement.Flags & 2) != 0 ? monitor.Work : placement.Normal;
            if ((placement.Flags & 2) == 0)
            {
                int x = monitor.Work.Left - monitor.Bounds.Left;
                int y = monitor.Work.Top - monitor.Bounds.Top;
                raw.Left += x;
                raw.Right += x;
                raw.Top += y;
                raw.Bottom += y;
            }
        }
        int width = raw.Right - raw.Left, height = raw.Bottom - raw.Top;
        if (width <= 0 || height <= 0 || (long)width * height > 18_000_000) return null;
        var rawBounds = new Rect(raw.Left, raw.Top, width, height);
        var bounds = minimized ? rawBounds : WindowBounds(window) ?? rawBounds;
        using var surface = new DibSurface(width, height);
        // PrintWindow asks this window alone to render. Some apps decline or return an empty frame.
        if (!PrintWindow(window.Handle, surface.Dc, 2) || !RunningWindows.IsCurrent(window)) return null;
        int physicalLeft = Math.Clamp((int)bounds.Left - raw.Left, 0, width - 1);
        int physicalTop = Math.Clamp((int)bounds.Top - raw.Top, 0, height - 1);
        int physicalRight = Math.Clamp((int)bounds.Right - raw.Left, physicalLeft + 1, width);
        int physicalBottom = Math.Clamp((int)bounds.Bottom - raw.Top, physicalTop + 1, height);
        var destination = new Rect(raw.Left + physicalLeft, raw.Top + physicalTop,
            physicalRight - physicalLeft, physicalBottom - physicalTop);

        int left = physicalLeft, top = physicalTop, right = physicalRight, bottom = physicalBottom;
        bool virtualized = false;
        if (!minimized && TryGetVirtualizedSize(window.Handle, surface, width, height,
            out int printWidth, out int printHeight))
        {
            virtualized = true;
            // PrintWindow can paint at the source app's DPI while DWM bounds are in physical pixels.
            // Convert the frame insets before cropping to keep the client area in the same place.
            left = Math.Clamp((int)Math.Round((double)physicalLeft * printWidth / width), 0, printWidth - 1);
            top = Math.Clamp((int)Math.Round((double)physicalTop * printHeight / height), 0, printHeight - 1);
            right = Math.Clamp((int)Math.Round((double)physicalRight * printWidth / width), left + 1, printWidth);
            bottom = Math.Clamp((int)Math.Round((double)physicalBottom * printHeight / height), top + 1, printHeight);
        }
        int croppedWidth = right - left, croppedHeight = bottom - top;
        int stride = croppedWidth * 4;
        var pixels = new byte[stride * croppedHeight];
        int first = -1;
        bool content = false;
        int renderedRight = 0, renderedBottom = 0;
        for (int y = 0; y < croppedHeight; y++)
        {
            int index = y * stride;
            Marshal.Copy(surface.Bits + ((top + y) * width + left) * 4, pixels, index, stride);
            for (int x = index; x < index + stride; x += 4)
            {
                int color = pixels[x] | pixels[x + 1] << 8 | pixels[x + 2] << 16;
                if (first < 0) first = color;
                content |= first != color;
                if (color != 0)
                {
                    renderedRight = Math.Max(renderedRight, (x - index) / 4 + 1);
                    renderedBottom = y + 1;
                }
                pixels[x + 3] = 255;
            }
        }
        if (!content) return null;
        double filledWidth = (double)renderedRight / croppedWidth;
        double filledHeight = (double)renderedBottom / croppedHeight;
        if (filledWidth < .3 || filledHeight < .3) return null;
        // Keep the partial-frame fallback for apps without a usable logical window rectangle.
        if (!virtualized && filledWidth is >= .35 and < .9 && filledHeight is >= .35 and < .9 &&
            Math.Abs(filledWidth - filledHeight) < .06)
        {
            int rowBytes = renderedRight * 4;
            var cropped = new byte[rowBytes * renderedBottom];
            for (int y = 0; y < renderedBottom; y++)
                System.Buffer.BlockCopy(pixels, y * stride, cropped, y * rowBytes, rowBytes);
            return TrimBlackGutter(cropped, renderedRight, renderedBottom, destination);
        }
        return TrimBlackGutter(pixels, croppedWidth, croppedHeight, destination);
    }

    private static bool TryGetVirtualizedSize(nint window, DibSurface surface, int width, int height,
        out int printWidth, out int printHeight)
    {
        printWidth = printHeight = 0;
        nint context = GetWindowDpiAwarenessContext(window);
        nint original = context == 0 ? 0 : SetThreadDpiAwarenessContext(context);
        if (original == 0) return false;
        Native.Rect logical;
        try
        {
            if (!GetWindowRect(window, out logical)) return false;
        }
        finally { SetThreadDpiAwarenessContext(original); }
        printWidth = logical.Right - logical.Left;
        printHeight = logical.Bottom - logical.Top;
        if (printWidth <= 0 || printWidth >= width || printHeight <= 0 || printHeight >= height) return false;

        // Some apps still paint in physical pixels regardless of their DPI awareness.
        var row = new int[width];
        int paintedRight = 0, paintedBottom = 0;
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(surface.Bits + y * width * 4, row, 0, width);
            int x = width - 1;
            while (x >= 0 && (row[x] & 0x00ffffff) == 0) x--;
            if (x < 0) continue;
            if (x >= printWidth || y >= printHeight) return false;
            paintedRight = Math.Max(paintedRight, x + 1);
            paintedBottom = y + 1;
        }
        return paintedRight >= printWidth * .85 && paintedBottom >= printHeight * .85;
    }

    private static Snapshot TrimBlackGutter(byte[] pixels, int width, int height, Rect destination)
    {
        int left = Gutter(width, height, (x, y) => IsBlack(x, y));
        int right = Gutter(width, height, (x, y) => IsBlack(width - 1 - x, y));
        int top = Gutter(height, width, (y, x) => IsBlack(x, y));
        int bottom = Gutter(height, width, (y, x) => IsBlack(x, height - 1 - y));
        if (left + right + top + bottom == 0)
            return new Snapshot(pixels, width, height, destination, new Rect(0, 0, 1, 1));

        int innerWidth = width - left - right, innerHeight = height - top - bottom;
        int rowBytes = innerWidth * 4;
        var trimmed = new byte[rowBytes * innerHeight];
        for (int row = 0; row < innerHeight; row++)
            System.Buffer.BlockCopy(pixels, ((row + top) * width + left) * 4, trimmed, row * rowBytes, rowBytes);
        // Keep the cropped pixels in their original portion of the window.
        var content = new Rect((double)left / width, (double)top / height,
            (double)innerWidth / width, (double)innerHeight / height);
        return new Snapshot(trimmed, innerWidth, innerHeight, destination, content);

        bool IsBlack(int x, int y)
        {
            int offset = (y * width + x) * 4;
            return (pixels[offset] | pixels[offset + 1] | pixels[offset + 2]) == 0;
        }
    }

    private static int Gutter(int depth, int span, Func<int, int, bool> isBlack)
    {
        int limit = Math.Min(24, depth / 16);
        int start = span / 8, end = span - start;
        for (int line = 0; line <= limit; line++)
        {
            int painted = 0;
            for (int sample = start; sample < end; sample++)
                if (!isBlack(line, sample)) painted++;
            if (painted > (end - start) / 20) return line >= 2 ? line : 0;
        }
        // A black area that continues into the content can belong to the app.
        return 0;
    }

    internal sealed record Snapshot(byte[] Pixels, int Width, int Height, Rect Bounds, Rect Content);

    internal sealed class NativeTransitionScope : IDisposable
    {
        private const uint TransitionsForcedDisabled = 3;
        private readonly RunningWindow source;
        private int original;
        private bool changed;

        internal NativeTransitionScope(RunningWindow source)
        {
            this.source = source;
            // On systems that expose this as write-only, leave it alone.
            if (!RunningWindows.IsCurrent(source) ||
                DwmGetWindowAttribute(source.Handle, TransitionsForcedDisabled, out original, sizeof(int)) < 0) return;
            if (original != 0)
            {
                Suppressed = true;
                return;
            }
            int disabled = 1;
            changed = Suppressed = Native.DwmSetWindowAttribute(source.Handle, TransitionsForcedDisabled, ref disabled, sizeof(int)) >= 0;
        }

        internal bool Suppressed { get; }

        public void Dispose()
        {
            if (!changed) return;
            changed = false;
            if (StillOwnsWindow(source))
                Native.DwmSetWindowAttribute(source.Handle, TransitionsForcedDisabled, ref original, sizeof(int));
        }
    }

    internal sealed class WindowCover : IDisposable
    {
        private readonly RunningWindow source;
        private bool cloaked, clipped;
        private nint originalRegion;

        internal WindowCover(RunningWindow source)
        {
            this.source = source;
            if (!RunningWindows.IsCurrent(source) || IsHungAppWindow(source.Handle) ||
                DwmGetWindowAttribute(source.Handle, 14, out int original, sizeof(int)) >= 0 && original != 0) return;

            int enabled = 1;
            cloaked = Native.DwmSetWindowAttribute(source.Handle, 13, ref enabled, sizeof(int)) >= 0;
            if (cloaked) return;

            // DWM can reject cloaking another process. Keep its exact window region before
            // hiding its pixels and give the backup back only if our empty region is still set.
            nint saved = CreateRectRgn(0, 0, 0, 0);
            nint empty = CreateRectRgn(0, 0, 0, 0);
            try
            {
                if (saved == 0 || empty == 0) return;
                int kind = GetWindowRgn(source.Handle, saved);
                if (kind == 1 || SetWindowRgn(source.Handle, empty, true) == 0) return;
                empty = 0; // SetWindowRgn owns this handle after success.
                clipped = true;
                if (kind != 0)
                {
                    originalRegion = saved;
                    saved = 0;
                }
            }
            finally
            {
                if (saved != 0) DeleteObject(saved);
                if (empty != 0) DeleteObject(empty);
            }
        }

        internal bool Applied => cloaked || clipped;

        public void Dispose()
        {
            if (!Applied) return;
            // Restore only the HWND that was covered, even if the window became hidden.
            bool same = StillOwnsWindow(source);
            if (same && cloaked)
            {
                int enabled = 0;
                Native.DwmSetWindowAttribute(source.Handle, 13, ref enabled, sizeof(int));
            }
            if (same && clipped)
            {
                nint current = CreateRectRgn(0, 0, 0, 0);
                try
                {
                    if ((current == 0 || GetWindowRgn(source.Handle, current) == 1) &&
                        SetWindowRgn(source.Handle, originalRegion, true) != 0)
                        originalRegion = 0;
                }
                finally { if (current != 0) DeleteObject(current); }
            }
            if (originalRegion != 0) DeleteObject(originalRegion);
            originalRegion = 0;
            cloaked = clipped = false;
        }
    }

    private static bool StillOwnsWindow(RunningWindow window) =>
        GetWindowThreadProcessId(window.Handle, out uint process) != 0 && process == window.ProcessId;

    internal sealed class DibSurface : IDisposable
    {
        private nint bitmap, previous;
        internal nint Dc { get; private set; }
        internal nint Bits { get; }

        internal DibSurface(int width, int height)
        {
            Dc = CreateCompatibleDC(0);
            if (Dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new BitmapInfo
            {
                Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32
            };
            bitmap = CreateDIBSection(Dc, in info, 0, out nint bits, 0, 0);
            if (bitmap != 0) previous = SelectObject(Dc, bitmap);
            if (previous == 0 || previous == -1)
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error);
            }
            Bits = bits;
        }

        public void Dispose()
        {
            if (Dc == 0) return;
            if (previous != 0 && previous != -1) SelectObject(Dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(Dc);
            Dc = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, ImageSize;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ImportantColors, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        internal int Length;
        internal uint Flags, Show;
        internal Native.Point Min, Max;
        internal Native.Rect Normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Native.Rect Bounds, Work;
        internal uint Flags;
    }

    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Native.Point point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Native.Rect bounds);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Native.Rect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(nint dc, in BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
}
