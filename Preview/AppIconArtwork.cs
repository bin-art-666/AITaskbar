using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AITaskbar;

internal sealed record AppIconArtwork(BitmapImage Original, BitmapImage Graphic, bool HasPlate, double Luminance)
{
    public static AppIconArtwork? Load(string? executable, string? appId = null)
    {
        using var bitmap = LoadArtwork(executable, appId);
        if (bitmap is null) return null;
        using var storeIcon = StringComparer.OrdinalIgnoreCase.Equals(appId, PackagedAppIcon.StoreAppId)
            ? PackagedAppIcon.LoadStoreIconForLightBackground() : null;
        var artwork = storeIcon ?? bitmap;
        var (bounds, hasPlate, luminance) = Measure(artwork);
        var original = ToImage(bitmap);
        if (bounds.IsEmpty) return null;
        if (hasPlate && executable is not null) bounds = RemoveTtCloudFrame(executable, artwork, bounds);
        using var foreground = artwork.Clone(bounds, PixelFormat.Format32bppArgb);
        if (!hasPlate) return new(original, ToImage(foreground), false, luminance);
        using var plate = RoundedPlate(foreground);
        return new(original, ToImage(plate), true, luminance);
    }

    private static Bitmap? LoadArtwork(string? executable, string? appId)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(appId, PackagedAppPin.ChatGptId) && File.Exists(executable))
            return LoadBitmap(executable!) ?? PackagedAppIcon.Load(appId!);

        return (appId is not null ? PackagedAppIcon.Load(appId) : null) ??
            (string.IsNullOrEmpty(executable) ? null : LoadBitmap(executable));
    }

    private static Bitmap? LoadBitmap(string executable)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(executable, RunningWindows.SystemSettingsExecutable))
        {
            string images = Path.Combine(Path.GetDirectoryName(executable)!, "images");
            foreach (string file in new[] { "logo.targetsize-96_altform-unplated.png", "logo.targetsize-48_altform-unplated.png", "logo.png" })
            {
                string path = Path.Combine(images, file);
                if (File.Exists(path)) return new Bitmap(path);
            }
        }

        using var icon = Icon.ExtractAssociatedIcon(executable);
        return icon?.ToBitmap();
    }

    private static Rectangle RemoveTtCloudFrame(string executable, Bitmap bitmap, Rectangle bounds)
    {
        if (!Path.GetFileName(executable).Equals("ttcloudv2.exe", StringComparison.OrdinalIgnoreCase)
            || bitmap.Width != 32 || bitmap.Height != 32 || bounds != new Rectangle(0, 0, 32, 32))
            return bounds;

        // The current TTCLOUD artwork has a three-pixel white frame around the colored tile.
        // Check both sides of the frame before applying the crop to a replacement icon.
        int white = Color.White.ToArgb();
        if (bitmap.GetPixel(2, 16).ToArgb() != white || bitmap.GetPixel(29, 16).ToArgb() != white
            || bitmap.GetPixel(16, 2).ToArgb() != white || bitmap.GetPixel(16, 29).ToArgb() != white
            || bitmap.GetPixel(3, 16).ToArgb() == white || bitmap.GetPixel(28, 16).ToArgb() == white
            || bitmap.GetPixel(16, 3).ToArgb() == white || bitmap.GetPixel(16, 28).ToArgb() == white)
            return bounds;

        bounds.Inflate(-3, -3);
        return bounds;
    }

    internal static (Rectangle Bounds, bool HasPlate, double Luminance) Measure(Bitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        int solid = 0;
        double luminance = 0;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A < 24) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
            if (pixel.A < 224) continue;
            solid++;
            luminance += (.2126 * pixel.R + .7152 * pixel.G + .0722 * pixel.B) / 255;
        }
        if (right < 0) return (Rectangle.Empty, false, .5);

        var bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        int inset = (int)Math.Floor(Math.Min(bounds.Width, bounds.Height) * .1);
        // Sample close enough to the corners to distinguish a rounded square from a filled circle.
        bool hasPlate = Math.Min(bounds.Width, bounds.Height) >= .88 * Math.Max(bounds.Width, bounds.Height)
            && solid >= .75 * bounds.Width * bounds.Height
            && bitmap.GetPixel(left + inset, top + inset).A >= 224
            && bitmap.GetPixel(right - inset, top + inset).A >= 224
            && bitmap.GetPixel(left + inset, bottom - inset).A >= 224
            && bitmap.GetPixel(right - inset, bottom - inset).A >= 224;
        return (bounds, hasPlate, solid == 0 ? .5 : luminance / solid);
    }

    internal static Bitmap RoundedPlate(Bitmap foreground)
    {
        const int size = 104, diameter = 48;
        using var square = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (var scale = Graphics.FromImage(square))
        {
            var area = new Rectangle(0, 0, size, size);
            var top = PlateEdgeColor(foreground, top: true);
            var bottom = PlateEdgeColor(foreground, top: false);
            using var backing = new LinearGradientBrush(area, top, bottom, LinearGradientMode.Vertical);
            scale.FillRectangle(backing, area);
            scale.InterpolationMode = InterpolationMode.HighQualityBicubic;
            scale.PixelOffsetMode = PixelOffsetMode.Half;
            double factor = (double)size / Math.Max(foreground.Width, foreground.Height);
            int width = (int)Math.Round(foreground.Width * factor);
            int height = (int)Math.Round(foreground.Height * factor);
            var imageArea = new Rectangle((size - width) / 2, (size - height) / 2, width, height);
            scale.DrawImage(foreground, imageArea, 0, 0, foreground.Width, foreground.Height, GraphicsUnit.Pixel);
        }

        var plate = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(size - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(size - diameter, size - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, size - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        using var draw = Graphics.FromImage(plate);
        draw.SmoothingMode = SmoothingMode.AntiAlias;
        using var texture = new TextureBrush(square);
        draw.FillPath(texture, path);
        return plate;
    }

    private static Color PlateEdgeColor(Bitmap bitmap, bool top)
    {
        // The median of an edge band favors the background over details in the logo.
        var samples = new List<Color>();
        int rows = Math.Max(1, (bitmap.Height + 7) / 8);
        for (int row = 0; row < rows; row++)
        {
            int y = top ? row : bitmap.Height - 1 - row;
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A >= 248) samples.Add(pixel);
            }
        }
        if (samples.Count == 0)
        {
            int y = top ? rows : bitmap.Height - 1 - rows;
            var pixel = bitmap.GetPixel(bitmap.Width / 2, Math.Clamp(y, 0, bitmap.Height - 1));
            return Color.FromArgb(255, pixel.R, pixel.G, pixel.B);
        }
        int middle = samples.Count / 2;
        return Color.FromArgb(samples.Select(color => color.R).Order().ElementAt(middle),
            samples.Select(color => color.G).Order().ElementAt(middle),
            samples.Select(color => color.B).Order().ElementAt(middle));
    }

    private static BitmapImage ToImage(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        using var imageStream = stream.AsRandomAccessStream();
        var image = new BitmapImage();
        image.SetSource(imageStream);
        return image;
    }
}
