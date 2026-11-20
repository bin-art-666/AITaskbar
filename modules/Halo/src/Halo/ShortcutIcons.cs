using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Halo;

internal sealed partial class StationWindow
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellFileInfo { public nint Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int X, Y; public uint Colors, Important; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern nint SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint value);
    static async Task LoadShortcutIcon(Preview preview)
    {
        string path = preview.Item.Value; bool generic = true;
        // Read local shortcut metadata without executing it. Never inspect a network target for an icon.
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) {
            object? shell = null, shortcut = null;
            try {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type != null) {
                    shell = Activator.CreateInstance(type); shortcut = ((dynamic)shell!).CreateShortcut(path); string target = ((dynamic)shortcut).TargetPath;
                    if (target.Length > 3 && char.IsAsciiLetter(target[0]) && target[1] == ':' && (File.Exists(target) || Directory.Exists(target))) { path = target; generic = false; }
                }
            } catch { }
            finally { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        }
        var info = new ShellFileInfo();
        if (SHGetFileInfo(path, 0x80, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100u | (generic ? 0x10u : 0)) == 0 || info.Icon == 0) return;
        nint dc = 0, bitmap = 0, previous = 0;
        try {
            const int size = 64; dc = CreateCompatibleDC(0); var header = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = size, Height = -size, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(dc, ref header, 0, out var bits, 0, 0); if (bitmap == 0) return;
            previous = SelectObject(dc, bitmap); Marshal.Copy(new byte[size * size * 4], 0, bits, size * size * 4);
            if (!DrawIconEx(dc, 0, 0, info.Icon, size, size, 0, 0, 3)) return;
            var pixels = new byte[size * size * 4]; Marshal.Copy(bits, pixels, 0, pixels.Length);
            using var stream = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, size, size, 96, 96, pixels); await encoder.FlushAsync();
            stream.Seek(0); var image = new BitmapImage(); await image.SetSourceAsync(stream); preview.Image.Source = image; preview.Fallback.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        } finally { if (previous != 0) SelectObject(dc, previous); if (bitmap != 0) DeleteObject(bitmap); if (dc != 0) DeleteDC(dc); DestroyIcon(info.Icon); }
    }
}
