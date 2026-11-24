using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace AITaskbar;

internal sealed class TaskbarAppArtwork
{
    private sealed record CacheEntry(AppIconArtwork? Artwork, long LoadedAt, long UsedAt);
    private readonly Dictionary<string, CacheEntry> icons = new(StringComparer.OrdinalIgnoreCase);

    internal static string? Key(string? executable, string? appId) => appId is null ? executable : appId + "|" + executable;

    internal AppIconArtwork? Get(string? executable, string? appId)
    {
        var key = Key(executable, appId);
        if (string.IsNullOrEmpty(key)) return null;
        return TryGet(key, out var artwork) ? artwork : Remember(key, Prepare(executable, appId));
    }

    internal bool TryGet(string key, out AppIconArtwork? artwork)
    {
        artwork = null;
        long now = Environment.TickCount64;
        if (!icons.TryGetValue(key, out var entry) || entry.Artwork is null && now - entry.LoadedAt >= 30_000)
            return false;
        artwork = entry.Artwork;
        icons[key] = entry with { UsedAt = now };
        return true;
    }

    internal static AppIconArtwork.Prepared? Prepare(string? executable, string? appId)
    {
        try { return AppIconArtwork.Prepare(executable, appId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ExternalException) { }
        return null;
    }

    internal AppIconArtwork? Remember(string key, AppIconArtwork.Prepared? prepared)
    {
        if (TryGet(key, out var existing)) return existing;
        AppIconArtwork? artwork = null;
        try { artwork = prepared?.Create(); }
        catch (ExternalException) { }
        if (!icons.ContainsKey(key) && icons.Count >= 512)
            icons.Remove(icons.MinBy(pair => pair.Value.UsedAt).Key);
        long now = Environment.TickCount64;
        icons[key] = new(artwork, now, now);
        return artwork;
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
