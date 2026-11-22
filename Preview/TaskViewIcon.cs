using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AITaskbar;

internal sealed class TaskViewIcon : Grid
{
    // The purple plate occupies about 80% of the PNG's transparent canvas.
    private const double ArtworkScale = 1.25;

    internal TaskViewIcon(int size)
    {
        SetSize(size);
        Children.Add(new Image
        {
            Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "TaskView.png")))
            {
                DecodePixelWidth = 160
            },
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Windows.Foundation.Point(.5, .5),
            RenderTransform = new ScaleTransform { ScaleX = ArtworkScale, ScaleY = ArtworkScale }
        });
    }

    internal void SetSize(int size) => Width = Height = size;
}
