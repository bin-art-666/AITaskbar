using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace AITaskbar;

internal sealed class TaskbarAppArtwork
{
    private readonly Dictionary<string, AppIconArtwork?> icons = new(StringComparer.OrdinalIgnoreCase);

    internal static string? Key(string? executable, string? appId) => appId is null ? executable : appId + "|" + executable;

    internal AppIconArtwork? Get(string? executable, string? appId)
    {
        var key = Key(executable, appId);
        if (string.IsNullOrEmpty(key)) return null;
        if (icons.TryGetValue(key, out var artwork)) return artwork;
        try { artwork = AppIconArtwork.Load(executable, appId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ExternalException) { }
        if (icons.Count >= 256) icons.Clear();
        return icons[key] = artwork;
    }

    internal static void Apply(TaskbarIconPlate plate, AppIconArtwork? icon, int size, PreviewIconLayout layout,
        bool useOriginal, bool highContrast)
    {
        bool original = useOriginal || highContrast;
        TaskbarIconOutline.Apply(plate.Outline, layout, highContrast);
        if (icon is not null && plate.Artwork.Child is Image image)
        {
            image.Source = original ? icon.Original : icon.Graphic(layout.ColorMode);
            image.Width = image.Height = original || icon.HasPlate ? size : Math.Round(size * (20d / 26));
        }
        if (plate.Artwork.Child is FontIcon fallback)
        {
            if (original) fallback.ClearValue(IconElement.ForegroundProperty);
            else fallback.Foreground = new SolidColorBrush(AppIconArtwork.FallbackColor(layout.ColorMode));
        }
        plate.Artwork.Background = new SolidColorBrush(original || icon?.HasPlate == true
            ? Microsoft.UI.Colors.Transparent : AppIconArtwork.BackplateColor(layout.ColorMode));
    }
}
