using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIEnter;

// All coordinates are physical virtual-desktop pixels, including negative origins.
internal sealed record ScreenshotFrame(int Left, int Top, int Width, int Height, byte[] Pixels)
{
    internal ScreenshotFrame Crop(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - x); height = Math.Clamp(height, 1, Height - y);
        var pixels = new byte[checked(width * height * 4)];
        for (int row = 0; row < height; row++) System.Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, pixels, row * width * 4, width * 4);
        return new(Left + x, Top + y, width, height, pixels);
    }
    internal async Task<BitmapImage> Image()
    {
        using var stream = new InMemoryRandomAccessStream();
        await Encode(stream); stream.Seek(0);
        var image = new BitmapImage(); await image.SetSourceAsync(stream); return image;
    }
    async Task Encode(IRandomAccessStream stream)
    {
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)Width, (uint)Height, 96, 96, Pixels);
        await encoder.FlushAsync();
    }
    internal async Task<string> Save(string configuredFolder)
    {
        string folder = configuredFolder.Length == 0
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AIEnter", "Screenshots") : configuredFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) throw new IOException("截图保存目录无效，请在设置中选择目录。");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
        string temporary = path + ".tmp";
        try
        {
            var file = await StorageFile.GetFileFromPathAsync((await Create(temporary)).FullName);
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite)) await Encode(stream);
            File.Move(temporary, path); return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static async Task<FileInfo> Create(string path)
    { await File.WriteAllBytesAsync(path, []); return new FileInfo(path); }
}

internal static class ScreenshotCapture
{
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint ColorsUsed, ColorsImportant;
    }
    internal static ScreenshotFrame Desktop()
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000) throw new IOException("桌面尺寸不支持截图。");
        IntPtr screen = GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        if (screen == IntPtr.Zero) throw new Win32Exception();
        try
        {
            memory = CreateCompatibleDC(screen);
            var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var address, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero || address == IntPtr.Zero) throw new Win32Exception();
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x40CC0020)) throw new Win32Exception();
            var bytes = new byte[checked(width * height * 4)]; Marshal.Copy(address, bytes, 0, bytes.Length);
            return new(x, y, width, height, bytes);
        }
        finally
        {
            if (previous != IntPtr.Zero && memory != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
}
