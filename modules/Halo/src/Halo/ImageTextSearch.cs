using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace Halo;

internal sealed class ImageTextSearch
{
    const int CacheLimit = 512;
    const long ImageSizeLimit = 32 * 1024 * 1024;
    readonly Dictionary<string, (DateTime Modified, long Size, string Languages, string Text)> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim gate = new(1, 1);

    internal static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp";

    internal static OcrEngine[] Engines()
    {
        var profile = OcrEngine.TryCreateFromUserProfileLanguages();
        var available = OcrEngine.AvailableRecognizerLanguages;
        var preferred = available.Where(language => language.LanguageTag.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
            || language.LanguageTag.Equals("en-US", StringComparison.OrdinalIgnoreCase));
        return new[] { profile }.Concat(preferred.Select(OcrEngine.TryCreateFromLanguage))
            .OfType<OcrEngine>().DistinctBy(engine => engine.RecognizerLanguage.LanguageTag).ToArray();
    }

    internal async Task<string> Read(string path, OcrEngine[] engines, string languages, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try {
            var info = new FileInfo(path);
            if ((info.Attributes & (System.IO.FileAttributes.ReparsePoint | System.IO.FileAttributes.Directory)) != 0 || info.Length > ImageSizeLimit) return "";
            var modified = info.LastWriteTimeUtc;
            long size = info.Length;
            if (cache.TryGetValue(path, out var cached) && cached.Modified == modified && cached.Size == size && cached.Languages == languages) return cached.Text;
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
            using var stream = await file.OpenAsync(FileAccessMode.Read).AsTask(token);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
            uint edge = Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            double scale = Math.Min(1, Math.Min(3000, OcrEngine.MaxImageDimension) / (double)edge);
            var transform = new BitmapTransform {
                ScaledWidth = Math.Max(1u, (uint)(decoder.PixelWidth * scale)),
                ScaledHeight = Math.Max(1u, (uint)(decoder.PixelHeight * scale)),
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token);
            var lines = new List<string>();
            foreach (var engine in engines) {
                var result = await engine.RecognizeAsync(bitmap).AsTask(token);
                lines.AddRange(result.Lines.Select(line => line.Text));
            }
            string recognized = string.Join('\n', lines.Distinct());
            if (recognized.Length > 16000) recognized = recognized[..16000];
            if (cache.Count >= CacheLimit) cache.Clear();
            cache[path] = (modified, size, languages, recognized);
            return recognized;
        } finally { gate.Release(); }
    }

    internal static string? Match(string recognized, string query)
    {
        // Some recognizers insert word spaces between Chinese characters.
        static string WithoutSpaces(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        string compact = WithoutSpaces(query);
        return recognized.Split('\n').FirstOrDefault(line => line.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || WithoutSpaces(line).Contains(compact, StringComparison.CurrentCultureIgnoreCase));
    }
}
