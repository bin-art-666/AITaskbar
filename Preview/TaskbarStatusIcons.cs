using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using VectorPath = Microsoft.UI.Xaml.Shapes.Path;

namespace AITaskbar;

internal sealed class TaskbarStatusIcon : Grid
{
    private readonly VectorPath[] parts;
    private readonly VectorPath unavailable;
    private readonly bool wifi;

    internal TaskbarStatusIcon(bool wifi, Brush foreground)
    {
        this.wifi = wifi;
        Width = Height = wifi ? 18 : 21;
        var surface = new Canvas { Width = 1024, Height = 1024 };
        parts = (wifi ? WifiParts : VolumeParts).Select(data => CreatePath(data, foreground)).ToArray();
        foreach (var part in parts) surface.Children.Add(part);
        unavailable = CreatePath(wifi ? "M125 220 L180 165 900 885 845 940 Z"
            : "M687 393 L823 529 959 393 1009 443 873 579 1009 715 959 765 823 629 687 765 637 715 773 579 637 443 Z", foreground);
        surface.Children.Add(unavailable);
        Children.Add(new Viewbox { Child = surface });
        IsHitTestVisible = false;
        SetLevel(0);
    }

    internal void SetLevel(int level) => SetLevel(level, level == 0);

    internal void SetVolume(int level, bool muted) =>
        SetLevel(muted || level == 0 ? 0 : level < 45 ? 2 : 3, muted);

    private void SetLevel(int level, bool unavailableVisible)
    {
        if (wifi)
        {
            for (int index = 0; index < parts.Length; index++)
                parts[index].Opacity = index < level ? 1 : .24;
        }
        else
        {
            for (int index = 1; index < parts.Length; index++)
                parts[index].Visibility = index < level ? Visibility.Visible : Visibility.Collapsed;
        }
        unavailable.Visibility = unavailableVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static VectorPath CreatePath(string data, Brush foreground)
    {
        var path = (VectorPath)XamlReader.Load(
            $"""<Path xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Data="{data}" />""");
        path.Fill = foreground;
        return path;
    }

    // The supplied Wi-Fi and volume SVGs split into independently rendered contours.
    private static readonly string[] WifiParts =
    [
        "M372.352 747.84L512 896l139.648-148.16c-76.8-81.92-202.048-81.92-279.296 0z",
        "M186.24 550.4l93.12 98.752c128.448-136.32 336.96-136.32 465.408 0L837.824 550.4c-179.648-190.592-471.488-190.592-651.648 0z",
        "M0 352.832l93.12 98.752c231.296-245.44 606.464-245.44 837.76 0L1024 352.832C741.44 53.056 283.008 53.056 0 352.832z",
    ];

    private static readonly string[] VolumeParts =
    [
        "M260.256 356.576l204.288-163.968a32 32 0 0 1 52.032 24.96v610.432a32 32 0 0 1-51.968 24.992l-209.92-167.552H96a32 32 0 0 1-32-32v-264.864a32 32 0 0 1 32-32h164.256z",
        "M670.784 720.128a32 32 0 0 1-44.832-45.664 214.08 214.08 0 0 0 64.32-153.312 213.92 213.92 0 0 0-55.776-144.448 32 32 0 1 1 47.36-43.04 277.92 277.92 0 0 1 72.416 187.488 278.08 278.08 0 0 1-83.488 198.976z",
        "M822.912 858.88a32 32 0 1 1-45.888-44.608A419.008 419.008 0 0 0 896 521.152c0-108.704-41.376-210.848-114.432-288.384a32 32 0 0 1 46.592-43.872c84.16 89.28 131.84 207.04 131.84 332.256 0 127.84-49.76 247.904-137.088 337.728z",
    ];
}
