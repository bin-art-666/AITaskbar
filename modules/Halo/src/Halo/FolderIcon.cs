using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace Halo;

internal static class FolderIcon
{
    const int PixelSize = 192;
    static Task<byte[]>? originalPixels;
    static WriteableBitmap? defaultIcon, tintedIcon;
    static string? tint;

    internal static async Task Apply(Image image, string color, FontIcon? fallback = null)
    {
        if (Equals(image.Tag, color)) return;
        image.Tag = color;
        try {
            var original = await (originalPixels ??= ReadPixels());
            if (!Equals(image.Tag, color)) return;
            if (color == HaloDefaults.FolderColor)
                image.Source = defaultIcon ??= CreateBitmap(original, color);
            else {
                if (color != tint) {
                    tintedIcon = CreateBitmap(original, color);
                    tint = color;
                }
                image.Source = tintedIcon;
            }
            if (fallback != null) fallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) {
            if (Equals(image.Tag, color)) image.Tag = null;
            App.Log(error);
        }
    }

    static async Task<byte[]> ReadPixels()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "Folder.png"));
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // Crop the transparent padding baked into Folder.png so the silhouette fills the icon slot.
        var transform = new BitmapTransform {
            ScaledWidth = 250, ScaledHeight = 250,
            Bounds = new BitmapBounds { X = 29, Y = 28, Width = PixelSize, Height = PixelSize },
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return pixels.DetachPixelData();
    }

    static WriteableBitmap CreateBitmap(byte[] original, string color)
    {
        var pixels = new byte[original.Length];
        bool recolor = color != HaloDefaults.FolderColor;
        uint rgb = UiAppearance.ColorValue(color, 0);
        byte[] channels = [(byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16)];
        for (int i = 0; i < pixels.Length; i += 4) {
            // Keep the neutral highlight and divider while replacing the blue pigment.
            int neutral = Math.Min(original[i], Math.Min(original[i + 1], original[i + 2]));
            int pigment = Math.Max(original[i], Math.Max(original[i + 1], original[i + 2])) - neutral;
            int alpha = pixels[i + 3] = original[i + 3];
            for (int channel = 0; channel < 3; channel++) {
                int value = recolor ? neutral + (pigment * channels[channel] + 127) / 255 : original[i + channel];
                pixels[i + channel] = (byte)((value * alpha + 127) / 255);
            }
        }
        var bitmap = new WriteableBitmap(PixelSize, PixelSize);
        using var buffer = bitmap.PixelBuffer.AsStream();
        buffer.Write(pixels);
        bitmap.Invalidate();
        return bitmap;
    }
}
