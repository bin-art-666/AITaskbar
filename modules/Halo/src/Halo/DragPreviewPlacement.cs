using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Halo;

internal enum DragPreviewMode { GrabPoint, Below, LowerRight }

internal readonly record struct DragPreviewPlacement(DragPreviewMode Mode, int Distance)
{
    internal const int DefaultDistance = 6, MaximumDistance = 24;
    internal static DragPreviewPlacement From(HaloSettings settings) => new(settings.DragPreviewMode, settings.DragPreviewDistance);

    internal Point Offset(double width, double height, Point grab, double scale = 1) => Mode switch {
        DragPreviewMode.Below => new(-width / 2, Distance * scale),
        DragPreviewMode.LowerRight => new(Distance * scale, Distance * scale),
        _ => new(-width * grab.X, -height * grab.Y),
    };

    // Positive offsets become transparent pixels so the DragUI anchor stays inside its bitmap.
    internal (Point Anchor, Thickness Padding) Layout(double size, Point grab)
    {
        var offset = Offset(size, size, grab);
        return (new(Math.Max(0, -offset.X), Math.Max(0, -offset.Y)),
            new(Math.Max(0, offset.X), Math.Max(0, offset.Y), 0, 0));
    }
}
