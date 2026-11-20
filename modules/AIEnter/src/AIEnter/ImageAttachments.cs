using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AIEnter;

internal static class ImageAttachments
{
    internal static string DirectoryPath => Path.Combine(App.DataDirectory, "drop-images");
    internal static bool Managed(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static async Task<string> Save(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream(); using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
        return await Save(stream);
    }
    internal static async Task<string> SaveFile(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync(); return await Save(stream);
    }
    static async Task<string> Save(IRandomAccessStream input)
    {
        if (input.Size > 32 * 1024 * 1024) throw new IOException("图片超过 32 MB。");
        input.Seek(0); var decoder = await BitmapDecoder.CreateAsync(input);
        if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 64000000) throw new IOException("图片像素过大，请先缩小图片。");
        double scale = Math.Min(1, 2048d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)) };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        Directory.CreateDirectory(DirectoryPath); string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (File.Create(path + ".tmp")) { }
            var outputFile = await StorageFile.GetFileFromPathAsync(path + ".tmp");
            using (var output = await outputFile.OpenAsync(FileAccessMode.ReadWrite))
            { var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output); encoder.SetSoftwareBitmap(bitmap); await encoder.FlushAsync(); }
            File.Move(path + ".tmp", path); return path;
        }
        finally { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
    }
}
